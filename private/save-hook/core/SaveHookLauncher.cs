using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HwpSaveHook.Launcher;

/// <summary>
/// HWP 저장 형식 유지를 위한 전용 런처.
/// 대상 Hwp.exe를 해시로 게이트한 뒤, 일치할 때만 CLR 프로파일러 환경변수를 설정해
/// 자식 프로세스로 띄운다. 불일치·프로파일러 없음이면 후킹 없이 그냥 실행(fail-safe).
/// 디스크·서명은 건드리지 않는다.
/// 설계: docs/superpowers/specs/2026-11-26-hwp-save-hook-design.md
/// </summary>

/// <summary>후킹 여부 결정.</summary>
public enum HookDecision
{
    /// <summary>대상 확인됨 + 프로파일러 존재 → 후킹하여 실행.</summary>
    Hook,
    /// <summary>해시 불일치·프로파일러 없음·사용자 강제 → 후킹 없이 그냥 실행(fail-safe).</summary>
    RunWithoutHook,
}

/// <summary>프로파일러를 로드할 CLR 종류. §7 미확정이면 Auto(양쪽 다 설정, 무해).</summary>
public enum ClrKind
{
    /// <summary>CLR 종류 미확정 → COR_*(.NET Framework)와 CORECLR_*(.NET Core) 모두 설정.</summary>
    Auto,
    /// <summary>.NET Framework 4.x → COR_* 패밀리만.</summary>
    NetFramework,
    /// <summary>.NET Core/5+ → CORECLR_* 패밀리만.</summary>
    NetCore,
}

/// <summary>해시 게이트 판정 결과.</summary>
public sealed record GateResult(HookDecision Decision, string Reason, string? ActualSha256);

/// <summary>실행 계획(프로세스를 띄우기 전의 전체 결정). dry-run으로 그대로 출력 가능.</summary>
public sealed record LaunchPlan(
    string HwpPath,
    bool Hooked,
    string Reason,
    string? ProfilerPath,
    string Clsid,
    string? ActualSha256,
    string ExpectedSha256,
    IReadOnlyDictionary<string, string> ProfilerEnvironment);

/// <summary>투명 실행(--open) 결과.</summary>
public sealed record TransparentLaunchResult(Process Process, bool AttachedProfiler);

public static class SaveHookLauncher
{
    /// <summary>지원 빌드. 한컴오피스 2022 한글 교육기관용 12.0.0.4645 x86.</summary>
    public const string DefaultExpectedSha256 =
        "8211f34ed67e169c7a957a91bbacb6aa0ded64f642c3e5b112b1ea56cd6bcdd4";

    /// <summary>프로파일러 COM 클래스 식별자. C++ 프로파일러의 CLSID와 반드시 일치해야 한다.</summary>
    public const string ProfilerClsid = "{E72ECB74-8EC7-43AE-B9F1-C03CAC1CACD3}";

    public static string DefaultProfilerPath =>
        Path.Combine(AppContext.BaseDirectory, "profiler", "HwpSaveHookProfiler.dll");

    /// <summary>파일의 SHA-256(대문자 16진). 존재하지 않으면 null.</summary>
    public static string? TryHashFile(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using SHA256 hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// fail-safe 게이트. 프로파일러가 없거나, 대상 해시가 기대값과 다르거나, 해시를 못 읽으면
    /// 후킹하지 않는다. 오프셋·메서드가 다른 빌드에 손대면 크래시·문서 손상 위험(설계 §6).
    /// </summary>
    public static GateResult EvaluateGate(string? actualSha256, bool profilerExists, string expectedSha256)
    {
        if (!profilerExists)
        {
            return new(HookDecision.RunWithoutHook, "profiler-missing", actualSha256);
        }

        if (actualSha256 is null)
        {
            return new(HookDecision.RunWithoutHook, "hwp-unreadable", null);
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(HookDecision.RunWithoutHook, "build-hash-mismatch", actualSha256);
        }

        return new(HookDecision.Hook, "build-hash-match", actualSha256);
    }

    /// <summary>
    /// 프로파일러 attach용 환경변수. 레지스트리 등록을 피하려 *_PROFILER_PATH를 쓴다(설계 §4).
    /// 인라이닝 억제(COR_PRF_DISABLE_INLINING)와 ReJIT은 프로파일러가 SetEventMask로 처리하므로
    /// 여기서 설정하지 않는다. CLR 종류 미확정이면 Auto로 양쪽 패밀리를 모두 설정(무해).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildProfilerEnvironment(
        string profilerDllPath, string clsid, ClrKind clrKind)
    {
        string fullPath = Path.GetFullPath(profilerDllPath);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        if (clrKind is ClrKind.NetFramework or ClrKind.Auto)
        {
            env["COR_ENABLE_PROFILING"] = "1";
            env["COR_PROFILER"] = clsid;
            env["COR_PROFILER_PATH"] = fullPath;
            // 대상은 x86이므로 비트폭별 경로도 명시한다.
            env["COR_PROFILER_PATH_32"] = fullPath;
        }

        if (clrKind is ClrKind.NetCore or ClrKind.Auto)
        {
            env["CORECLR_ENABLE_PROFILING"] = "1";
            env["CORECLR_PROFILER"] = clsid;
            env["CORECLR_PROFILER_PATH"] = fullPath;
            env["CORECLR_PROFILER_PATH_32"] = fullPath;
        }

        return env;
    }

    /// <summary>해시·프로파일러 존재·환경변수를 묶어 실행 계획을 만든다. 프로세스는 띄우지 않는다.</summary>
    public static LaunchPlan CreatePlan(
        string hwpPath, string profilerDllPath, string clsid, ClrKind clrKind, string expectedSha256,
        bool forceRunWithoutHook = false)
    {
        string resolvedHwp = Path.GetFullPath(hwpPath);
        string resolvedProfiler = Path.GetFullPath(profilerDllPath);
        bool profilerExists = File.Exists(resolvedProfiler);
        string? actual = TryHashFile(resolvedHwp);

        GateResult gate = forceRunWithoutHook
            ? new GateResult(HookDecision.RunWithoutHook, "forced-no-hook", actual)
            : EvaluateGate(actual, profilerExists, expectedSha256);

        bool hooked = gate.Decision == HookDecision.Hook;
        IReadOnlyDictionary<string, string> env = hooked
            ? BuildProfilerEnvironment(resolvedProfiler, clsid, clrKind)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new LaunchPlan(
            resolvedHwp, hooked, gate.Reason,
            profilerExists ? resolvedProfiler : null,
            clsid, actual, expectedSha256, env);
    }

    /// <summary>
    /// 계획대로 자식 Hwp.exe를 띄운다. 후킹 환경변수는 자식에게만 설정되고 상속된다.
    /// 이미 열려 있던 한글에는 영향 없다(설계 §6-4). 디스크·서명 무변경.
    /// </summary>
    public static Process Launch(LaunchPlan plan, IEnumerable<string>? hwpArguments = null, string? profilerLog = null)
    {
        ProcessStartInfo startInfo = CreateStartInfo(plan, applyProfiler: plan.Hooked, hwpArguments, profilerLog);
        return StartSingleInstance(IsHangulRunningInSession, () => Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {plan.HwpPath}."));
    }

    /// <summary>
    /// 투명 래퍼(--open)용 실행. 한글이 실행 중이 아니면 계획대로 프로파일러를 연결해 시작하고,
    /// 이미 실행 중이면 기존 창에서 문서를 열 수 있도록 프로파일러 환경변수 없이 인자만 전달한다.
    /// </summary>
    public static TransparentLaunchResult LaunchTransparent(
        LaunchPlan plan, IEnumerable<string>? hwpArguments = null, string? profilerLog = null) =>
        LaunchTransparentCore(plan, hwpArguments, profilerLog, IsHangulRunningInSession,
            startInfo => Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {plan.HwpPath}."));

    internal static TransparentLaunchResult LaunchTransparentCore(
        LaunchPlan plan,
        IEnumerable<string>? hwpArguments,
        string? profilerLog,
        Func<bool> isRunning,
        Func<ProcessStartInfo, Process> startProcess,
        string mutexName = @"Local\HwpSaveHook.Launch")
    {
        using var mutex = new Mutex(false, mutexName);
        bool ownsMutex;
        try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { ownsMutex = true; }

        try
        {
            bool running = !ownsMutex || isRunning();
            bool attachProfiler = !running && plan.Hooked;
            ProcessStartInfo startInfo = CreateStartInfo(plan, attachProfiler, hwpArguments, profilerLog);
            return new TransparentLaunchResult(startProcess(startInfo), attachProfiler);
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        LaunchPlan plan, bool applyProfiler, IEnumerable<string>? hwpArguments, string? profilerLog)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.HwpPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(plan.HwpPath) ?? Environment.CurrentDirectory,
        };

#if NETFRAMEWORK
        startInfo.Arguments = string.Join(" ", (hwpArguments ?? Enumerable.Empty<string>()).Select(QuoteArgument));
#else
        foreach (string argument in hwpArguments ?? Enumerable.Empty<string>())
        {
            startInfo.ArgumentList.Add(argument);
        }
#endif

        foreach (string family in new[] { "COR", "CORECLR" })
        {
            foreach (string suffix in new[] { "ENABLE_PROFILING", "PROFILER", "PROFILER_PATH", "PROFILER_PATH_32", "PROFILER_PATH_64" })
            {
                startInfo.Environment.Remove($"{family}_{suffix}");
            }
        }

        if (applyProfiler)
        {
            foreach (KeyValuePair<string, string> entry in plan.ProfilerEnvironment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }

            if (profilerLog is not null)
            {
                startInfo.Environment["HWP_SAVEHOOK_LOG"] = Path.GetFullPath(profilerLog);
            }
        }

        return startInfo;
    }

    internal static T StartSingleInstance<T>(Func<bool> isRunning, Func<T> start, string mutexName = @"Local\HwpSaveHook.Launch")
    {
        using var mutex = new Mutex(false, mutexName);
        bool ownsMutex;
        try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) throw new HangulAlreadyRunningException("A Hangul launch is already in progress.");

        try
        {
            if (isRunning()) throw new HangulAlreadyRunningException("Close the running Hangul instance before starting another.");
            return start();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static bool IsHangulRunningInSession()
    {
        using Process current = Process.GetCurrentProcess();
        int session = current.SessionId;
        Process[] instances = Process.GetProcessesByName("Hwp");
        try
        {
            foreach (Process instance in instances)
            {
                try
                {
                    if (instance.SessionId != session) continue;
                    if (!instance.HasExited) return true;
                }
                catch (InvalidOperationException) when (instance.HasExited) { }
                catch (Win32Exception)
                {
                    // 같은 세션의 관리자 권한 Hwp.exe 등 핸들 조회가 거부되면 실행 중으로 간주한다.
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (Process instance in instances) instance.Dispose();
        }
    }

#if NETFRAMEWORK
    // Windows treats runs of backslashes specially before quotes and the closing delimiter.
    private static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder().Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', character == '"' ? (backslashes * 2) + 1 : backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
#endif
}

public sealed class HangulAlreadyRunningException(string message) : InvalidOperationException(message);
