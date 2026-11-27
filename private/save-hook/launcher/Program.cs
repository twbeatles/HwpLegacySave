using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HwpSaveHook.Launcher;

// HWP 저장 형식 유지 런처 CLI.
// 사용법:
//   hwp-save-hook plan   [options]            대상 탐지·해시·계획만 JSON으로 출력(프로세스 미실행)
//   hwp-save-hook launch [options] [-- args]  계획대로 자식 Hwp.exe 실행(후킹 가능 시 후킹)
//   hwp-save-hook inspect [--hwp PATH]         새 빌드 지원에 필요한 해시·버전·MVID·대상 토큰 출력
//   hwp-save-hook update-keygen --private-key-out FILE   업데이트 서명 키 생성(공개키 출력)
//   hwp-save-hook update-sign --version V --artifact ZIP --url URL --out FILE
//                 [--private-key-env NAME] [--expires-in-days N]   서명된 updates/latest.json 생성
// 옵션:
//   --hwp PATH              Hwp.exe 경로(미지정 시 레지스트리/기본 경로 자동 탐지)
//   --profiler DLL          x86 프로파일러 DLL(기본: 런처 옆 profiler/HwpSaveHookProfiler.dll)
//   --expected-sha256 HEX   대상 빌드 해시(기본: 설계 확정값)
//   --clr auto|netfx|core   프로파일러 환경변수 패밀리(기본: auto)
//   --force-no-hook         게이트 무시하고 후킹 없이 그냥 실행
//   --allow-compatible      검증되지 않았지만 대상 메서드 구조가 같은 빌드에도 후킹(실험)
//   -- <args...>            이후 인자는 Hwp.exe에 그대로 전달

return LauncherCli.Run(args);

[SupportedOSPlatform("windows")]
internal static class LauncherCli
{
    private const string Usage =
        "Usage: hwp-save-hook plan|launch [--hwp PATH] [--profiler DLL] [--expected-sha256 HEX] [--clr auto|netfx|core] [--force-no-hook] [--allow-compatible] [-- HWP_ARGS...]\n" +
        "       hwp-save-hook inspect [--hwp PATH]\n" +
        "       hwp-save-hook list\n" +
        "       hwp-save-hook update-keygen --private-key-out FILE\n" +
        "       hwp-save-hook update-sign --version V --artifact ZIP --url URL --out FILE [--private-key-env NAME] [--expires-in-days N]";

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0];
        if (command is "update-keygen" or "update-sign") return UpdateCommands.Run(command, args[1..]);
        if (command == "list") return ListInstallations();
        if (command is not ("plan" or "launch" or "inspect"))
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
        bool forceNoHook = false, allowCompatible = false;
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
                case "--allow-compatible": allowCompatible = true; break;
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

        if (command == "inspect") return Inspect(hwp);

        LaunchPlan? plan;
        try
        {
            plan = HwpInstallations.InspectWithTimeoutAsync<LaunchPlan?>(token =>
            {
                string? path = HwpInstallations.FindPreferred(hwp, token);
                token.ThrowIfCancellationRequested();
                return path is null ? null : SaveHookLauncher.CreatePlan(path, profiler,
                    SaveHookLauncher.ProfilerClsid, clr, expected, forceNoHook, allowCompatible);
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
            ["build"] = plan.BuildLabel,
            ["targetMode"] = plan.TargetMode == TargetMode.Compatible ? "compatible" : "verified",
            ["targetToken"] = $"0x{plan.TargetToken:X8}",
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
                    timeout: TimeSpan.FromSeconds(6), targetToken: plan.TargetToken).GetAwaiter().GetResult();
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

    /// <summary>이 PC에서 찾은 한글 설치본을 모두 나열하고, 자동 선택될 설치본을 표시한다.</summary>
    private static int ListInstallations()
    {
        try
        {
            var (all, preferred) = HwpInstallations.InspectWithTimeoutAsync(token =>
                (HwpInstallations.FindAll(token), HwpInstallations.FindPreferred(cancellationToken: token)),
                TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            var items = new JsonArray();
            foreach (string path in all)
            {
                string? hash = SaveHookLauncher.TryHashFile(path);
                KnownHwpBuild? known = HwpBuildCatalog.Find(hash);
                HwpBuildInfo build = HwpBuildInspector.Inspect(path);
                items.Add(new JsonObject
                {
                    ["hwp"] = path,
                    ["selected"] = string.Equals(path, preferred, StringComparison.OrdinalIgnoreCase),
                    ["product"] = known?.Product ?? build.ProductName,
                    ["fileVersion"] = build.FileVersion,
                    ["verified"] = known is not null,
                    ["kind"] = build.Kind.ToString().ToLowerInvariant(),
                    ["compatible"] = build.HasCompatibleTarget,
                });
            }
            WriteJson(new JsonObject { ["count"] = all.Count, ["installations"] = items });
            return all.Count == 0 ? 3 : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or TimeoutException)
        {
            WriteJson(new JsonObject { ["status"] = "inspection-failed", ["error"] = error.Message });
            return 5;
        }
    }

    /// <summary>새 한글 빌드를 카탈로그에 추가할 때 필요한 값을 출력한다. 실행 파일은 읽기만 한다.</summary>
    private static int Inspect(string? hwp)
    {
        string? path;
        try
        {
            path = HwpInstallations.InspectWithTimeoutAsync(token => HwpInstallations.FindPreferred(hwp, token))
                .GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
                                      or SecurityException or TimeoutException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        if (path is null)
        {
            WriteJson(new JsonObject { ["status"] = "hwp-not-found", ["note"] = "No readable Hwp.exe found. Pass --hwp PATH." });
            return 3;
        }

        string? hash = SaveHookLauncher.TryHashFile(path);
        HwpBuildInfo build = HwpBuildInspector.Inspect(path);
        KnownHwpBuild? known = HwpBuildCatalog.Find(hash);
        WriteJson(new JsonObject
        {
            ["hwp"] = path,
            ["sha256"] = hash,
            ["fileVersion"] = build.FileVersion,
            ["product"] = known?.Product ?? build.ProductName,
            ["kind"] = build.Kind.ToString().ToLowerInvariant(),
            ["x86"] = build.IsX86,
            ["runtime"] = build.RuntimeVersion,
            ["mvid"] = build.Mvid?.ToString(),
            ["targetMethod"] = $"{HwpBuildInspector.TargetTypeNamespace}.{HwpBuildInspector.TargetTypeName}::{HwpBuildInspector.TargetMethodName}",
            ["targetToken"] = build.TargetToken is int token ? $"0x{token:X8}" : null,
            ["verified"] = known is not null,
            ["targetCalls"] = new JsonArray(build.TargetCalls.Select(call => (JsonNode?)JsonValue.Create(call)).ToArray()),
            ["targetSemanticsMatch"] = build.TargetSemanticsMatch,
            ["compatible"] = build.HasCompatibleTarget,
            ["error"] = build.Error,
        });
        return 0;
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
        Console.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}

/// <summary>릴리스용 업데이트 서명 도구. 개인키는 파일이나 환경변수로만 받고 저장소에 두지 않는다.</summary>
[SupportedOSPlatform("windows")]
internal static class UpdateCommands
{
    public static int Run(string command, string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length)
            {
                Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}");
                return 2;
            }
            options[args[i]] = args[++i];
        }

        try
        {
            return command == "update-keygen" ? KeyGen(options) : Sign(options);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or IOException
                                      or UnauthorizedAccessException or CryptographicException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }

    private static int KeyGen(Dictionary<string, string> options)
    {
        string output = Required(options, "--private-key-out");
        if (File.Exists(output)) throw new IOException($"{output} already exists.");
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        ECParameters parameters = key.ExportParameters(false);
        Console.WriteLine(Convert.ToBase64String([.. parameters.Q.X!, .. parameters.Q.Y!]));
        return 0;
    }

    private static int Sign(Dictionary<string, string> options)
    {
        string version = Required(options, "--version");
        string artifact = Required(options, "--artifact");
        string url = Required(options, "--url");
        string output = Required(options, "--out");
        string keyVariable = options.TryGetValue("--private-key-env", out string? name) ? name : "HWPSAVEHOOK_UPDATE_PRIVATE_KEY";
        int days = options.TryGetValue("--expires-in-days", out string? daysText) ? int.Parse(daysText) : 365;
        if (!Version.TryParse(version, out _)) throw new FormatException($"Invalid version: {version}");
        if (!url.StartsWith("https://", StringComparison.Ordinal)) throw new FormatException("--url must use HTTPS.");
        if (days <= 0) throw new FormatException("--expires-in-days must be positive.");

        string? keyText = Environment.GetEnvironmentVariable(keyVariable);
        if (string.IsNullOrWhiteSpace(keyText)) throw new ArgumentException($"Environment variable {keyVariable} is not set.");
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(keyText.Trim()), out _);

        byte[] bytes = File.ReadAllBytes(artifact);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string expires = DateTimeOffset.UtcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        byte[] payload = UpdateManifestVerifier.CanonicalPayload(version, url, sha256, bytes.LongLength, expires);
        string signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));

        var manifest = new JsonObject
        {
            ["version"] = version,
            ["artifact_url"] = url,
            ["sha256"] = sha256,
            ["size"] = bytes.LongLength,
            ["expires_at"] = expires,
            ["signature"] = signature,
        };
        File.WriteAllText(output, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Signed {output} ({bytes.LongLength} bytes, sha256 {sha256}).");
        return 0;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing {name}.");
}
