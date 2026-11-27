using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;

namespace HwpSaveHook.Launcher;

/// <summary>같은 Windows 세션에서 실행 중인 한글 프로세스의 HWP 저장 모드 적용 상태.</summary>
public enum RunningHangulState
{
    /// <summary>실행 중인 한글이 없다.</summary>
    None,
    /// <summary>실행 중인 한글 모두에 프로파일러가 로드돼 있다.</summary>
    AllHooked,
    /// <summary>프로파일러 없이 시작된 한글이 하나 이상 있다. 이 창으로 넘긴 문서는 HWPX로 저장될 수 있다.</summary>
    SomeUnhooked,
    /// <summary>권한 등으로 모듈 목록을 읽지 못했다.</summary>
    Unknown,
}

/// <summary>
/// 실행 중인 Hwp.exe에 프로파일러 DLL이 로드됐는지 모듈 목록으로 확인한다.
/// 시작 메뉴·작업 표시줄·다른 형식 문서로 먼저 연 한글은 프로파일러 없이 실행되고,
/// 이후 .hwp 문서도 그 창으로 전달되므로 저장 형식이 HWPX로 바뀐다.
/// </summary>
public static class HangulProcesses
{
    public const string ProfilerModuleName = "HwpSaveHookProfiler.dll";

    /// <summary>
    /// 한글 실행 파일로 보이는지 버전 정보로 확인한다. 같은 이름의 다른 프로그램이
    /// 실행 중 검사를 오작동시키는 것을 막는다. 알 수 없으면 한글로 간주한다(fail-safe).
    /// </summary>
    internal static bool LooksLikeHangulExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            string identity = $"{version.CompanyName} {version.ProductName} {version.FileDescription}";
            if (string.IsNullOrWhiteSpace(identity)) return true;
            return HangulIdentityKeywords.Any(keyword =>
                identity.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                                      or ArgumentException or FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static readonly string[] HangulIdentityKeywords =
        ["Hancom", "HNC", "Haansoft", "한컴", "한글", "Hwp"];

    private static string? TryGetMainModulePath(Process instance)
    {
        try { return instance.MainModule?.FileName; }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>실행 중인 프로세스가 한글로 보이면 true. 알 수 없으면 true(fail-safe).</summary>
    internal static bool LooksLikeHangulInstance(Process instance) =>
        LooksLikeHangulExecutable(TryGetMainModulePath(instance));

    public static RunningHangulState GetState() => GetState(ProfilerModuleName);

    internal static RunningHangulState GetState(string profilerModuleName)
    {
        int session;
        using (Process current = Process.GetCurrentProcess()) session = current.SessionId;

        Process[] instances = Process.GetProcessesByName("Hwp");
        try
        {
            bool any = false, unknown = false;
            foreach (Process instance in instances)
            {
                try
                {
                    if (instance.SessionId != session || instance.HasExited) continue;
                    // 같은 이름의 다른 프로그램은 한글 상태 판정에서 뺀다.
                    if (!LooksLikeHangulExecutable(TryGetMainModulePath(instance))) continue;
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                {
                    // 종료됐거나 핸들 조회가 거부된 프로세스는 아래 모듈 조회에서 다시 판정한다.
                }

                any = true;
                bool? hooked = HasModule(instance.Id, profilerModuleName);
                if (hooked == false) return RunningHangulState.SomeUnhooked;
                if (hooked is null) unknown = true;
            }

            if (!any) return RunningHangulState.None;
            return unknown ? RunningHangulState.Unknown : RunningHangulState.AllHooked;
        }
        finally
        {
            foreach (Process instance in instances) instance.Dispose();
        }
    }

    /// <summary>프로세스에 이름이 같은 모듈이 있으면 true, 없으면 false, 읽지 못하면 null.</summary>
    internal static bool? HasModule(int processId, string moduleName)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapModule | SnapModule32, (uint)processId);
            if (snapshot == InvalidHandle)
            {
                int error = Marshal.GetLastWin32Error();
                // 시작 직후나 모듈 로드 중이면 ERROR_BAD_LENGTH·ERROR_PARTIAL_COPY가 날 수 있어 잠시 뒤 다시 읽는다.
                if (error is ErrorBadLength or ErrorPartialCopy)
                {
                    System.Threading.Thread.Sleep(50);
                    continue;
                }
                return null;
            }

            try
            {
                var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
                if (!Module32First(snapshot, ref entry)) return null;
                do
                {
                    if (string.Equals(entry.Module, moduleName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(entry.ExePath), moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    entry.Size = (uint)Marshal.SizeOf<ModuleEntry32>();
                } while (Module32Next(snapshot, ref entry));
                return false;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        return null;
    }

    /// <summary>테스트용: 현재 프로세스에서 모듈 판별이 동작하는지 확인한다.</summary>
    internal static IReadOnlyList<string> ListModules(int processId)
    {
        var names = new List<string>();
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapModule | SnapModule32, (uint)processId);
        if (snapshot == InvalidHandle) return names;
        try
        {
            var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
            if (!Module32First(snapshot, ref entry)) return names;
            do
            {
                names.Add(entry.Module);
                entry.Size = (uint)Marshal.SizeOf<ModuleEntry32>();
            } while (Module32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return names;
    }

    private const uint SnapModule = 0x00000008;
    private const uint SnapModule32 = 0x00000010;
    private const int ErrorBadLength = 24;
    private const int ErrorPartialCopy = 299;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint Size;
        public uint ModuleId;
        public uint ProcessId;
        public uint GlobalUsage;
        public uint ProcessUsage;
        public IntPtr BaseAddress;
        public uint BaseSize;
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Module32FirstW")]
    private static extern bool Module32First(IntPtr snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Module32NextW")]
    private static extern bool Module32Next(IntPtr snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
