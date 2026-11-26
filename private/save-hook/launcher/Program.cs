using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using HwpSaveHook.Launcher;

// HWP 저장 형식 유지 런처 CLI.
// 사용법:
//   hwp-save-hook plan   [options]            대상 탐지·해시·계획만 JSON으로 출력(프로세스 미실행)
//   hwp-save-hook launch [options] [-- args]  계획대로 자식 Hwp.exe 실행(후킹 가능 시 후킹)
// 옵션:
//   --hwp PATH              Hwp.exe 경로(미지정 시 레지스트리/기본 경로 자동 탐지)
//   --profiler DLL          x86 프로파일러 DLL(기본: 런처 옆 profiler/HwpSaveHookProfiler.dll)
//   --expected-sha256 HEX   대상 빌드 해시(기본: 설계 확정값)
//   --clr auto|netfx|core   프로파일러 환경변수 패밀리(기본: auto)
//   --force-no-hook         게이트 무시하고 후킹 없이 그냥 실행
//   -- <args...>            이후 인자는 Hwp.exe에 그대로 전달

return LauncherCli.Run(args);

[SupportedOSPlatform("windows")]
internal static class LauncherCli
{
    private const string Usage =
        "Usage: hwp-save-hook plan|launch [--hwp PATH] [--profiler DLL] [--expected-sha256 HEX] [--clr auto|netfx|core] [--force-no-hook] [-- HWP_ARGS...]";

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0];
        if (command is not ("plan" or "launch"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // "--" 이후는 Hwp.exe 전달 인자.
        string[] rest = args[1..];
        List<string> hwpArgs = new();
        int separator = Array.IndexOf(rest, "--");
        if (separator >= 0)
        {
            hwpArgs = rest[(separator + 1)..].ToList();
            rest = rest[..separator];
        }

        string? hwp = null, profiler = null, expected = SaveHookLauncher.DefaultExpectedSha256, clrText = "auto";
        bool forceNoHook = false;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] is "--hwp" or "--profiler" or "--expected-sha256" or "--clr")
            {
                if (i + 1 == rest.Length || string.IsNullOrWhiteSpace(rest[i + 1]) ||
                    rest[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"Missing value for {rest[i]}.");
                    return 2;
                }
            }

            switch (rest[i])
            {
                case "--hwp" when i + 1 < rest.Length: hwp = rest[++i]; break;
                case "--profiler" when i + 1 < rest.Length: profiler = rest[++i]; break;
                case "--expected-sha256" when i + 1 < rest.Length: expected = rest[++i]; break;
                case "--clr" when i + 1 < rest.Length: clrText = rest[++i]; break;
                case "--force-no-hook": forceNoHook = true; break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete option: {rest[i]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        ClrKind clr = clrText switch
        {
            "auto" => ClrKind.Auto,
            "netfx" => ClrKind.NetFramework,
            "core" => ClrKind.NetCore,
            _ => (ClrKind)(-1),
        };
        if ((int)clr < 0)
        {
            Console.Error.WriteLine("--clr must be auto, netfx, or core.");
            return 2;
        }

        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
        {
            Console.Error.WriteLine("--expected-sha256 requires 64 hexadecimal digits.");
            return 2;
        }

        // 프로파일러 기본 경로: 런처 실행 파일 옆 profiler/HwpSaveHookProfiler.dll
        profiler ??= SaveHookLauncher.DefaultProfilerPath;

        LaunchPlan? plan;
        try
        {
            plan = HwpInstallations.InspectWithTimeoutAsync<LaunchPlan?>(token =>
            {
                string? path = HwpInstallations.FindPreferred(hwp, token);
                token.ThrowIfCancellationRequested();
                return path is null ? null : SaveHookLauncher.CreatePlan(path, profiler,
                    SaveHookLauncher.ProfilerClsid, clr, expected, forceNoHook);
            }).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or TimeoutException)
        {
            WriteJson(new JsonObject { ["status"] = "inspection-failed", ["error"] = error.Message });
            return 5;
        }
        if (plan is null)
        {
            WriteJson(new JsonObject { ["status"] = "hwp-not-found", ["note"] = "No readable Hwp.exe found. Pass --hwp PATH." });
            return 3;
        }

        var output = new JsonObject
        {
            ["command"] = command,
            ["hwp"] = plan.HwpPath,
            ["hooked"] = plan.Hooked,
            ["reason"] = plan.Reason,
            ["profiler"] = plan.ProfilerPath,
            ["profilerExists"] = plan.ProfilerPath != null && File.Exists(plan.ProfilerPath),
            ["clsid"] = plan.Clsid,
            ["expectedSha256"] = plan.ExpectedSha256,
            ["actualSha256"] = plan.ActualSha256,
            ["environment"] = ToJson(plan.ProfilerEnvironment),
        };

        if (command == "plan")
        {
            WriteJson(output);
            return 0;
        }

        try
        {
            string? log = plan.Hooked ? RejitLog.NewPath() : null;
            using var process = SaveHookLauncher.Launch(plan, hwpArgs, log);
            output["pid"] = process.Id;
            output["status"] = "launched";
            if (log is not null)
            {
                output["log"] = log;
                RejitStatus state = RejitLog.WaitAsync(process, log, waitForCompilation: false,
                    timeout: TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
                output["profilingStatus"] = state switch
                {
                    RejitStatus.RequestAccepted => "request-accepted",
                    RejitStatus.Applied => "applied",
                    RejitStatus.Failed => "failed",
                    RejitStatus.ProcessExited => "process-exited",
                    _ => "not-observed"
                };
                if (state is RejitStatus.Failed or RejitStatus.ProcessExited or RejitStatus.NotObserved)
                {
                    output["status"] = "profiling-unconfirmed";
                    WriteJson(output);
                    return 5;
                }
            }
            else
            {
                output["profilingStatus"] = "disabled";
                if (process.WaitForExit(300))
                {
                    output["status"] = "process-exited";
                    WriteJson(output);
                    return 5;
                }
            }
            WriteJson(output);
            return 0;
        }
        catch (HangulAlreadyRunningException error)
        {
            output["status"] = "already-running";
            output["error"] = error.Message;
            WriteJson(output);
            return 5;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException
                                      or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            output["status"] = "launch-failed";
            output["error"] = error.Message;
            WriteJson(output);
            return 5;
        }
    }

    private static JsonObject ToJson(IReadOnlyDictionary<string, string> map)
    {
        var node = new JsonObject();
        foreach ((string key, string value) in map)
        {
            node[key] = value;
        }

        return node;
    }

    private static void WriteJson(JsonNode node) =>
        Console.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
