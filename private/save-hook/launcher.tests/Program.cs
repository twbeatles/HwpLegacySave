using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HwpSaveHook.Launcher;

// 외부 테스트 프레임워크 없이(오프라인) 런처 순수 로직을 검증하는 자체 테스트.
// 통과 시 exit 0, 하나라도 실패하면 exit 1.

if (args.Length > 0 && args is not ["--cli", _, _])
{
    if (args is ["--help"] or ["-h"])
    {
        Console.WriteLine("Usage: hwp-save-hook-tests [--cli DOTNET CLI_DLL]");
        return 0;
    }

    Console.Error.WriteLine($"Unsupported arguments: {string.Join(' ', args)}");
    return 2;
}

int failures = 0;
int total = 0;

void Check(string name, bool condition)
{
    total++;
    if (condition)
    {
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  FAIL  {name}");
    }
}

string NewTempDir()
{
    string dir = Path.Combine(Path.GetTempPath(), "hwp-save-hook-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

Console.WriteLine("SaveHookLauncher self-test");

// --- EvaluateGate 전 분기 ---
const string expected = SaveHookLauncher.DefaultExpectedSha256;
string expectedUpper = expected.ToUpperInvariant();

Check("gate: profiler missing -> RunWithoutHook(profiler-missing)",
    SaveHookLauncher.EvaluateGate(expectedUpper, profilerExists: false, expected) is
        { Decision: HookDecision.RunWithoutHook, Reason: "profiler-missing" });

Check("gate: hwp unreadable -> RunWithoutHook(hwp-unreadable)",
    SaveHookLauncher.EvaluateGate(null, profilerExists: true, expected) is
        { Decision: HookDecision.RunWithoutHook, Reason: "hwp-unreadable" });

Check("gate: hash mismatch -> RunWithoutHook(build-hash-mismatch)",
    SaveHookLauncher.EvaluateGate("DEADBEEF", profilerExists: true, expected) is
        { Decision: HookDecision.RunWithoutHook, Reason: "build-hash-mismatch" });

Check("gate: hash match -> Hook(build-hash-match)",
    SaveHookLauncher.EvaluateGate(expectedUpper, profilerExists: true, expected) is
        { Decision: HookDecision.Hook, Reason: "build-hash-match" });

Check("gate: hash match is case-insensitive",
    SaveHookLauncher.EvaluateGate(expected.ToLowerInvariant(), profilerExists: true, expected) is
        { Decision: HookDecision.Hook });

// --- BuildProfilerEnvironment ---
var netfx = SaveHookLauncher.BuildProfilerEnvironment("x.dll", SaveHookLauncher.ProfilerClsid, ClrKind.NetFramework);
Check("env netfx: COR_ENABLE_PROFILING=1", netfx.TryGetValue("COR_ENABLE_PROFILING", out var e1) && e1 == "1");
Check("env netfx: COR_PROFILER=CLSID", netfx.TryGetValue("COR_PROFILER", out var e2) && e2 == SaveHookLauncher.ProfilerClsid);
Check("env netfx: COR_PROFILER_PATH is absolute", netfx.TryGetValue("COR_PROFILER_PATH", out var e3) && Path.IsPathFullyQualified(e3));
Check("env netfx: sets _PATH_32 for x86 child", netfx.ContainsKey("COR_PROFILER_PATH_32"));
Check("env netfx: does NOT set CORECLR_*", !netfx.ContainsKey("CORECLR_ENABLE_PROFILING"));

var core = SaveHookLauncher.BuildProfilerEnvironment("x.dll", SaveHookLauncher.ProfilerClsid, ClrKind.NetCore);
Check("env core: CORECLR_ENABLE_PROFILING=1", core.TryGetValue("CORECLR_ENABLE_PROFILING", out var c1) && c1 == "1");
Check("env core: does NOT set COR_*", !core.ContainsKey("COR_ENABLE_PROFILING"));

var auto = SaveHookLauncher.BuildProfilerEnvironment("x.dll", SaveHookLauncher.ProfilerClsid, ClrKind.Auto);
Check("env auto: sets BOTH families", auto.ContainsKey("COR_ENABLE_PROFILING") && auto.ContainsKey("CORECLR_ENABLE_PROFILING"));

// env must never carry an inlining/ReJIT flag (profiler-side concern, 설계 §4/§6-3)
Check("env: no inlining env var leaks in", !auto.ContainsKey("COR_PRF_DISABLE_INLINING"));

// --- CreatePlan: fail-safe when profiler missing (현재 상태: 프로파일러 미빌드) ---
{
    string dir = NewTempDir();
    try
    {
        // 기대 해시와 "일치하는" 가짜 Hwp.exe를 만들 수는 없으니(내용→해시 역산 불가),
        // 임의 내용 파일로 해시를 실제 계산해 그 값을 기대값으로 넘겨 match 경로를 검증한다.
        string fakeHwp = Path.Combine(dir, "Hwp.exe");
        File.WriteAllText(fakeHwp, "not really hwp");
        string realHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fakeHwp)));

        string missingProfiler = Path.Combine(dir, "nope", "HwpSaveHookProfiler.dll");
        var planNoProfiler = SaveHookLauncher.CreatePlan(fakeHwp, missingProfiler, SaveHookLauncher.ProfilerClsid, ClrKind.Auto, realHash);
        Check("plan: profiler missing -> not hooked, empty env",
            !planNoProfiler.Hooked && planNoProfiler.Reason == "profiler-missing" && planNoProfiler.ProfilerEnvironment.Count == 0);
        Check("plan: records actual hash even when not hooked", planNoProfiler.ActualSha256 == realHash);

        // 프로파일러가 존재하고 해시가 맞으면 후킹 계획이 서는지(환경변수 채워짐).
        string presentProfiler = Path.Combine(dir, "HwpSaveHookProfiler.dll");
        File.WriteAllText(presentProfiler, "stub");
        var planHook = SaveHookLauncher.CreatePlan(fakeHwp, presentProfiler, SaveHookLauncher.ProfilerClsid, ClrKind.Auto, realHash);
        Check("plan: profiler present + hash match -> hooked, env populated",
            planHook.Hooked && planHook.Reason == "build-hash-match" && planHook.ProfilerEnvironment.Count > 0);

        // 해시 불일치면 프로파일러가 있어도 후킹 안 함.
        var planMismatch = SaveHookLauncher.CreatePlan(fakeHwp, presentProfiler, SaveHookLauncher.ProfilerClsid, ClrKind.Auto, SaveHookLauncher.DefaultExpectedSha256);
        Check("plan: hash mismatch -> not hooked even with profiler present",
            !planMismatch.Hooked && planMismatch.Reason == "build-hash-mismatch");

        // 강제 no-hook.
        var planForced = SaveHookLauncher.CreatePlan(fakeHwp, presentProfiler, SaveHookLauncher.ProfilerClsid, ClrKind.Auto, realHash, forceRunWithoutHook: true);
        Check("plan: --force-no-hook overrides a valid match",
            !planForced.Hooked && planForced.Reason == "forced-no-hook");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

// --- TryHashFile ---
Check("hash: missing file -> null", SaveHookLauncher.TryHashFile(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))) is null);

// Use harmless copies of the test host as PE fixtures. They are inspected without execution.
{
    string dir = NewTempDir();
    try
    {
        string generic = Path.Combine(dir, "Hwp.exe");
        string custom = Path.Combine(dir, "Custom install", "Office 2022", "HOffice120", "Bin", "Hwp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.Copy(Environment.ProcessPath!, generic);
        File.Copy(Environment.ProcessPath!, custom);
        File.AppendAllText(generic, "different build");
        string customHash = SaveHookLauncher.TryHashFile(custom)!;
        var candidates = new List<string> { generic };
        HwpInstallations.AddInstallLocation(candidates, Path.Combine(dir, "Custom install"));
        Check("discovery: selects matching build in nested custom install", HwpInstallations.ChooseCandidate(candidates, customHash) == custom);
        Check("discovery: offers the first usable executable for normal launch", HwpInstallations.ChooseCandidate(candidates, "unknown") == generic);
        Check("discovery: explicitly selected custom path is accepted", HwpInstallations.FindPreferred(custom) == custom);
        File.WriteAllText(custom, "not an executable");
        Check("discovery: rejects a non-executable Hwp.exe", HwpInstallations.FindPreferred(custom) is null);
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

// Shared launch protection serializes both callers and leaves the mutex usable after rejection.
{
    string mutex = @"Local\HwpSaveHook.Test." + Guid.NewGuid().ToString("N");
    int starts = 0;
    int rejected = 0;
    Parallel.For(0, 2, _ =>
    {
        try
        {
            SaveHookLauncher.StartSingleInstance(() => starts > 0, () => ++starts, mutex);
        }
        catch (HangulAlreadyRunningException)
        {
            Interlocked.Increment(ref rejected);
        }
    });
    Check("launch: simultaneous requests start one child", starts == 1 && rejected == 1);
    Check("launch: guard can be reused after rejection",
        SaveHookLauncher.StartSingleInstance(() => false, () => 1, mutex) == 1);
}

{
    const int pid = 52;
    static string Event(string state, int processId = 52, string token = "0x06000244") =>
        $"2026-11-26 12:00:00.000 [pid {processId}] ReJITState: token={token} state={state}";

    Check("ReJIT: request acceptance is not application",
        RejitLog.ReadStatus([Event("request-accepted")], pid) == RejitStatus.RequestAccepted);
    Check("ReJIT: known PID and token require successful IL and compilation",
        RejitLog.ReadStatus([Event("il-installed"), Event("compiled"), Event("request-accepted")], pid)
        == RejitStatus.Applied);
    Check("ReJIT: compilation without recorded IL does not confirm application",
        RejitLog.ReadStatus([Event("compiled")], pid) == RejitStatus.NotObserved);
    Check("ReJIT: failures from the target override acceptance",
        RejitLog.ReadStatus([Event("request-accepted"), Event("failed"), Event("compiled")], pid)
        == RejitStatus.Failed);
    Check("ReJIT: unrelated PID and token do not change state",
        RejitLog.ReadStatus([Event("failed", 53), Event("request-accepted", token: "0x06000245")], pid)
        == RejitStatus.NotObserved);
}

{
    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<int> delayed = HwpInstallations.InspectWithTimeoutAsync(token =>
    {
        started.SetResult(true);
        released.Task.GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        return 0;
    }, TimeSpan.FromSeconds(1));
    try
    {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delayed;
        Check("discovery: an unresponsive inspection returns control", false);
    }
    catch (TimeoutException)
    {
        Check("discovery: an unresponsive inspection returns control", true);
    }
    finally
    {
        released.SetResult(true);
    }
    Check("discovery: subsequent inspection can complete",
        await HwpInstallations.InspectWithTimeoutAsync(_ => 42) == 42);
}

if (args is ["--cli", string dotnet, string cli])
{
    async Task<(int Code, string Output, string Error)> RunCliEnv(IDictionary<string, string?>? env, params string[] arguments)
    {
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (env is not null)
        {
            foreach ((string key, string? value) in env)
            {
                if (value is null) start.Environment.Remove(key);
                else start.Environment[key] = value;
            }
        }
        foreach (string arg in new[] { cli }.Concat(arguments)) start.ArgumentList.Add(arg);
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            throw;
        }
        return (child.ExitCode, await output, await error);
    }

    Task<(int Code, string Output, string Error)> RunCli(params string[] arguments) =>
        RunCliEnv(null, arguments);

    Task<(int Code, string Output, string Error)> RunPlan(params string[] arguments) =>
        RunCli(new[] { "plan" }.Concat(arguments).ToArray());

    foreach (string[] input in new[] { new[] { "--profiler", "" }, new[] { "--profiler", " " },
                 new[] { "--profiler" }, new[] { "--profiler", "--force-no-hook" } })
    {
        var invalid = await RunPlan(input);
        Check($"CLI: rejects missing value ({string.Join(",", input)})",
            invalid.Code == 2 && invalid.Error.Contains("Missing value") && !invalid.Error.Contains("Unhandled exception"));
    }
    var invalidHash = await RunPlan("--expected-sha256", "abc");
    Check("CLI: rejects invalid SHA-256", invalidHash.Code == 2);
    string dir = NewTempDir();
    try
    {
        string executable = Path.Combine(dir, "Hwp.exe");
        File.Copy(Environment.ProcessPath!, executable);
        var missing = await RunPlan("--hwp", executable, "--profiler", Path.Combine(dir, "missing.dll"));
        Check("CLI: well-formed missing DLL has a structured plan",
            missing.Code == 0 && missing.Output.Contains("profiler-missing"));
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    // --- 업데이터: 키 생성과 서명 (릴리스 파이프라인 경로, 기존 무커버리지) ---
    string keyEnv = "HWP_SAVEHOOK_TEST_" + Guid.NewGuid().ToString("N");
    string keyDir = NewTempDir();
    try
    {
        string keyFile = Path.Combine(keyDir, "test-private-key.txt");
        var keygen = await RunCli("update-keygen", "--private-key-out", keyFile);
        Check("update-keygen: creates a new key file",
            keygen.Code == 0 && File.Exists(keyFile) && keygen.Output.Trim().Length == 88);
        var keygenAgain = await RunCli("update-keygen", "--private-key-out", keyFile);
        Check("update-keygen: refuses to overwrite an existing key", keygenAgain.Code == 2);
        bool usableKey = false;
        try
        {
            using ECDsa probe = ECDsa.Create();
            probe.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim()), out _);
            usableKey = true;
        }
        catch { }
        Check("update-keygen: the file is a usable PKCS#8 key", usableKey);

        string artifact = Path.Combine(keyDir, "package.zip");
        File.WriteAllBytes(artifact, [0x50, 0x4B, 0x03, 0x04, 0x00]);
        string manifest = Path.Combine(keyDir, "latest.json");
        var key = new Dictionary<string, string?> { [keyEnv] = File.ReadAllText(keyFile).Trim() };
        var signed = await RunCliEnv(key, "update-sign", "--version", "9.0.0", "--artifact", artifact,
            "--url", "https://example.invalid/a.zip", "--out", manifest,
            "--private-key-env", keyEnv, "--expires-in-days", "30");
        Check("update-sign: signs an artifact manifest",
            signed.Code == 0 && File.Exists(manifest));
        Check("update-sign: the manifest verifies against the keygen key",
            UpdateManifestVerifier.Verify(File.ReadAllText(manifest), keygen.Output.Trim(), new Version(1, 0, 0)) is not null);

        var missingKey = await RunCli("update-sign", "--version", "9.0.0", "--artifact", artifact,
            "--url", "https://example.invalid/a.zip", "--out", manifest + ".2",
            "--private-key-env", keyEnv);
        Check("update-sign: missing key is rejected", missingKey.Code == 2);
        var httpUrl = await RunCliEnv(key, "update-sign", "--version", "9.0.0", "--artifact", artifact,
            "--url", "http://example.invalid/a.zip", "--out", manifest + ".3",
            "--private-key-env", keyEnv);
        Check("update-sign: non-HTTPS artifact URL is rejected", httpUrl.Code == 2);
        var badVersion = await RunCliEnv(key, "update-sign", "--version", "nine", "--artifact", artifact,
            "--url", "https://example.invalid/a.zip", "--out", manifest + ".4",
            "--private-key-env", keyEnv);
        Check("update-sign: bad version is rejected", badVersion.Code == 2);
    }
    finally
    {
        try { Directory.Delete(keyDir, recursive: true); } catch { /* best effort */ }
    }
}

// --- SaveHookSettings & ParseOpenArgs ---
{
    string dir = NewTempDir();
    try
    {
        Check("settings: defaults to hook enabled", SaveHookSettings.ReadHookEnabled(dir));
        SaveHookSettings.WriteHookEnabled(false, dir);
        Check("settings: persists disabled mode", !SaveHookSettings.ReadHookEnabled(dir));
        SaveHookSettings.WriteHookEnabled(true, dir);
        Check("settings: persists enabled mode", SaveHookSettings.ReadHookEnabled(dir));

        SaveHookSettings.WriteSelectedPath(@"C:\Hnc\Hwp.exe", dir);
        Check("settings: persists selected path", SaveHookSettings.ReadSelectedPathRaw(dir) == @"C:\Hnc\Hwp.exe");
        SaveHookSettings.WriteSelectedPath(null, dir);
        Check("settings: clears selected path", SaveHookSettings.ReadSelectedPathRaw(dir) is null);
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    var emptyArgs = SaveHookSettings.ParseOpenArgs([]);
    Check("args: empty -> GUI mode", !emptyArgs.IsOpenMode && emptyArgs.ForwardedArgs.Length == 0);
    var openOnly = SaveHookSettings.ParseOpenArgs(["--open"]);
    Check("args: --open -> wrapper mode without args", openOnly.IsOpenMode && openOnly.ForwardedArgs.Length == 0);
    var openDoc = SaveHookSettings.ParseOpenArgs(["--OPEN", @"C:\docs\sample.hwp", "/n"]);
    Check("args: --open forwards arguments",
        openDoc.IsOpenMode && openDoc.ForwardedArgs.SequenceEqual([@"C:\docs\sample.hwp", "/n"]));
}

// --- LaunchTransparentCore ---
{
    string dir = NewTempDir();
    try
    {
        string fakeHwp = Path.Combine(dir, "Hwp.exe");
        string fakeProfiler = Path.Combine(dir, "HwpSaveHookProfiler.dll");
        File.WriteAllText(fakeHwp, "hwp");
        File.WriteAllText(fakeProfiler, "dll");
        string realHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fakeHwp)));
        LaunchPlan plan = SaveHookLauncher.CreatePlan(fakeHwp, fakeProfiler, SaveHookLauncher.ProfilerClsid, ClrKind.NetFramework, realHash);

        string mutex = @"Local\HwpSaveHook.TransparentTest." + Guid.NewGuid().ToString("N");
        static Process StartNoopProcess() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            ArgumentList = { "/d", "/c", "exit", "0" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        ProcessStartInfo? capturedFresh = null;
        TransparentLaunchResult freshResult = SaveHookLauncher.LaunchTransparentCore(
            plan, [@"C:\test\doc.hwp"], Path.Combine(dir, "test.log"),
            info => { capturedFresh = info; return StartNoopProcess(); },
            mutex);
        try
        {
            if (!freshResult.Process.WaitForExit(5000)) freshResult.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            freshResult.Process.Dispose();
        }
        Check("transparent: fresh session attaches profiler and forwards args",
            freshResult.AttachedProfiler &&
            capturedFresh is not null &&
            capturedFresh.Environment.ContainsKey("COR_ENABLE_PROFILING") &&
            capturedFresh.Environment.ContainsKey("HWP_SAVEHOOK_LOG") &&
            capturedFresh.ArgumentList.SequenceEqual([@"C:\test\doc.hwp"]));

        ProcessStartInfo? capturedExisting = null;
        TransparentLaunchResult existingResult = SaveHookLauncher.LaunchTransparentCore(
            plan, [@"C:\test\doc2.hwp"], Path.Combine(dir, "test2.log"),
            info => { capturedExisting = info; return StartNoopProcess(); },
            mutex);
        try
        {
            if (!existingResult.Process.WaitForExit(5000)) existingResult.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            existingResult.Process.Dispose();
        }
        // 한글이 이미 실행 중이어도 프로파일러를 붙인다. 새 프로세스가 기존 창으로 문서를 넘기지 못하고
        // 독립 인스턴스로 남더라도(동시 더블클릭 등) 훅 없는 한글이 생기지 않아야 한다.
        Check("transparent: second launch still attaches profiler and forwards args",
            existingResult.AttachedProfiler &&
            capturedExisting is not null &&
            capturedExisting.Environment.ContainsKey("COR_ENABLE_PROFILING") &&
            capturedExisting.Environment.ContainsKey("HWP_SAVEHOOK_LOG") &&
            capturedExisting.ArgumentList.SequenceEqual([@"C:\test\doc2.hwp"]));

        LaunchPlan normalPlan = SaveHookLauncher.CreatePlan(fakeHwp, fakeProfiler, SaveHookLauncher.ProfilerClsid,
            ClrKind.NetFramework, realHash, forceRunWithoutHook: true);
        ProcessStartInfo? capturedNormal = null;
        TransparentLaunchResult normalResult = SaveHookLauncher.LaunchTransparentCore(
            normalPlan, [@"C:\test\doc3.hwp"], Path.Combine(dir, "test3.log"),
            info => { capturedNormal = info; return StartNoopProcess(); },
            mutex);
        try
        {
            if (!normalResult.Process.WaitForExit(5000)) normalResult.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            normalResult.Process.Dispose();
        }
        Check("transparent: OFF mode never attaches profiler",
            !normalResult.AttachedProfiler &&
            capturedNormal is not null &&
            !capturedNormal.Environment.ContainsKey("COR_ENABLE_PROFILING") &&
            !capturedNormal.Environment.ContainsKey("HWP_SAVEHOOK_LOG"));

        // Regression check: invoking this test host with unknown args (e.g. --version) must exit immediately, never recurse.
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),
            "hwp-save-hook-tests", StringComparison.OrdinalIgnoreCase))
        {
            using Process guardProbe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            Task<string> guardErr = guardProbe.StandardError.ReadToEndAsync();
            bool exited = guardProbe.WaitForExit(5000);
            if (!exited) guardProbe.Kill(entireProcessTree: true);
            Check("self-test: rejects unknown args without recursing",
                exited && guardProbe.ExitCode == 2 && guardErr.GetAwaiter().GetResult().Contains("Unsupported arguments"));
        }
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

// --- WindowsIntegration: Shortcut & File Association ---
{
    string dir = NewTempDir();
    string testRegistryRoot = $@"Software\HwpSaveHookTest-{Guid.NewGuid():N}";
    try
    {
        string shortcutPath = Path.Combine(dir, "한글 2022 (HWP 저장).lnk");
        string launcherExe = Path.Combine(dir, "HwpSaveHook.exe");
        string otherLauncherExe = Path.Combine(dir, "other", "HwpSaveHook.exe");
        string hwpExe = Path.Combine(dir, "Hwp.exe");
        File.WriteAllText(launcherExe, "launcher");
        File.WriteAllText(hwpExe, "hwp");

        WindowsIntegration.CreateShortcut(shortcutPath, launcherExe, hwpExe);
        ShortcutInfo? shortcut = WindowsIntegration.ReadShortcut(shortcutPath);
        Check("shortcut: creates .lnk with --open and Hwp.exe icon",
            shortcut is not null &&
            string.Equals(shortcut.TargetPath, launcherExe, StringComparison.OrdinalIgnoreCase) &&
            shortcut.Arguments == "--open" &&
            string.Equals(shortcut.IconLocation, hwpExe, StringComparison.OrdinalIgnoreCase));
        Check("shortcut: status is Enabled for matching launcher",
            WindowsIntegration.GetShortcutStatus(shortcutPath, launcherExe) == IntegrationStatus.Enabled);
        Check("shortcut: status is PointsElsewhere for moved launcher",
            WindowsIntegration.GetShortcutStatus(shortcutPath, otherLauncherExe) == IntegrationStatus.PointsElsewhere);
        WindowsIntegration.RemoveShortcut(shortcutPath);
        Check("shortcut: removed cleanly",
            WindowsIntegration.GetShortcutStatus(shortcutPath, launcherExe) == IntegrationStatus.Disabled);

        string settingsDir = Path.Combine(dir, "settings");
        const string testProgId = "Hwp.Document.120";
        Check("assoc: initially disabled",
            WindowsIntegration.GetFileAssociationStatus(launcherExe, testRegistryRoot, testProgId) == IntegrationStatus.Disabled);

        WindowsIntegration.EnableFileAssociation(launcherExe, settingsDir, testRegistryRoot, testProgId, hasExistingSystemMapping: true);
        Check("assoc: enabled after registration",
            WindowsIntegration.GetFileAssociationStatus(launcherExe, testRegistryRoot, testProgId) == IntegrationStatus.Enabled);
        Check("assoc: detects moved launcher as PointsElsewhere",
            WindowsIntegration.GetFileAssociationStatus(otherLauncherExe, testRegistryRoot, testProgId) == IntegrationStatus.PointsElsewhere);

        WindowsIntegration.EnableFileAssociation(otherLauncherExe, settingsDir, testRegistryRoot, testProgId, hasExistingSystemMapping: true);
        Check("assoc: updates path cleanly",
            WindowsIntegration.GetFileAssociationStatus(otherLauncherExe, testRegistryRoot, testProgId) == IntegrationStatus.Enabled);

        WindowsIntegration.DisableFileAssociation(settingsDir, testRegistryRoot, testProgId);
        Check("assoc: disabled and cleaned up",
            WindowsIntegration.GetFileAssociationStatus(otherLauncherExe, testRegistryRoot, testProgId) == IntegrationStatus.Disabled &&
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{testRegistryRoot}\{testProgId}") is null);

        // Existing custom command backup & restore
        using (var customKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{testRegistryRoot}\{testProgId}\shell\open\command")!)
        {
            customKey.SetValue(null, "\"C:\\Custom\\Hwp.exe\" \"%1\"");
        }
        WindowsIntegration.EnableFileAssociation(launcherExe, settingsDir, testRegistryRoot, testProgId, hasExistingSystemMapping: true);
        Check("assoc: overrides existing user command",
            WindowsIntegration.GetFileAssociationStatus(launcherExe, testRegistryRoot, testProgId) == IntegrationStatus.Enabled);
        WindowsIntegration.DisableFileAssociation(settingsDir, testRegistryRoot, testProgId);
        using (var restoredKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{testRegistryRoot}\{testProgId}\shell\open\command"))
        {
            Check("assoc: restores previous user command on disable",
                restoredKey?.GetValue(null) as string == "\"C:\\Custom\\Hwp.exe\" \"%1\"");
        }
        Check("assoc: does not treat --open-like substring as wrapper flag",
            WindowsIntegration.ClassifyWrapperCommand($"\"{launcherExe}\" --open-other \"%1\"", launcherExe) == IntegrationStatus.Disabled);

        // 여러 ProgId 되돌리기 정보: 형식 2 왕복과 이전 형식(단일 ProgId) 읽기
        string backupFile = SaveHookSettings.GetAssociationBackupFilePath(settingsDir);
        const string hncCommand = @"""C:\Hnc\Hwp.exe"" ""%1""";
        WindowsIntegration.WriteAssociationBackup(backupFile,
        [
            new("Hwp.Document.120", hncCommand, false),
            new("Hwp.Document.hwpx.120", null, false),
            new("Hwp.Document.hwt.120", "", true),
        ]);
        var roundTrip = WindowsIntegration.ReadAssociationBackup(backupFile);
        Check("assoc backup: format 2 round-trips every ProgId",
            roundTrip.Count == 3 &&
            roundTrip[0] is { ProgId: "Hwp.Document.120", PreviousCommand: hncCommand, CreatedExtensionMapping: false } &&
            roundTrip[1] is { ProgId: "Hwp.Document.hwpx.120", PreviousCommand: null } &&
            roundTrip[2] is { PreviousCommand: "", CreatedExtensionMapping: true });
        const string oldCommand = @"""C:\Old\Hwp.exe"" ""%1""";
        File.WriteAllText(backupFile, "progId=Hwp.Document.120\r\ncreatedExt=0\r\npreviousCommand=" + oldCommand);
        var legacy = WindowsIntegration.ReadAssociationBackup(backupFile);
        Check("assoc backup: reads the single-ProgId format of earlier versions",
            legacy.Count == 1 && legacy[0] is { ProgId: "Hwp.Document.120", PreviousCommand: oldCommand, CreatedExtensionMapping: false });
        File.Delete(backupFile);

        // 여러 ProgId를 감싼 뒤 끄면 모두 되돌린다(테스트 레지스트리 루트, ProgId마다 호출).
        string[] extraProgIds = ["Hwp.Document.hwpx.120", "Hwp.Document.hwt.120"];
        foreach (string progId in extraProgIds)
        {
            WindowsIntegration.EnableFileAssociation(launcherExe, settingsDir, testRegistryRoot, progId, hasExistingSystemMapping: true);
        }
        Check("assoc: records one backup entry per wrapped ProgId",
            WindowsIntegration.ReadAssociationBackup(backupFile).Select(entry => entry.ProgId).SequenceEqual(extraProgIds));
        Check("assoc: opted-in marker exists while enabled", WindowsIntegration.IsFileAssociationOptedIn(settingsDir));
        foreach (string progId in extraProgIds)
        {
            WindowsIntegration.DisableFileAssociation(settingsDir, testRegistryRoot, progId);
        }
        Check("assoc: every wrapped ProgId is cleaned up",
            extraProgIds.All(progId => Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{testRegistryRoot}\{progId}") is null));

        // 다른 앱이 그 사이 명령을 바꿨으면 끌 때 덮어쓰지 않는다.
        const string foreignCommand = @"""C:\Other\Viewer.exe"" ""%1""";
        WindowsIntegration.EnableFileAssociation(launcherExe, settingsDir, testRegistryRoot, testProgId, hasExistingSystemMapping: true);
        using (var foreign = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{testRegistryRoot}\{testProgId}\shell\open\command")!)
        {
            foreign.SetValue(null, foreignCommand);
        }
        WindowsIntegration.DisableFileAssociation(settingsDir, testRegistryRoot, testProgId);
        using (var kept = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{testRegistryRoot}\{testProgId}\shell\open\command"))
        {
            Check("assoc: leaves a command another app changed meanwhile", kept?.GetValue(null) as string == foreignCommand);
        }

        // 명령 판별
        Check("assoc: recognizes Hangul open commands",
            WindowsIntegration.IsHangulCommand(@"""C:\Program Files (x86)\HNC\Office 2022\HOffice120\bin\Hwp.exe"" ""%1""") &&
            WindowsIntegration.IsHangulCommand(@"C:\Hnc\Bin\hwp.exe ""%1""") &&
            !WindowsIntegration.IsHangulCommand(@"C:\Users\me\AppData\Local\HOP\hop-desktop.exe ""%1""") &&
            !WindowsIntegration.IsHangulCommand(null));
        Check("assoc: recognizes wrapper commands from any location",
            WindowsIntegration.IsWrapperCommand(WindowsIntegration.BuildWrapperCommand(otherLauncherExe)) &&
            !WindowsIntegration.IsWrapperCommand($"\"{launcherExe}\" \"%1\""));
        Check("assoc: wrapper keeps the original Hangul arguments",
            WindowsIntegration.BuildWrapperCommand(launcherExe, @"""C:\Hnc\Hwp.exe"" ""%1"" /n") == $"\"{launcherExe}\" --open \"%1\" /n" &&
            WindowsIntegration.BuildWrapperCommand(launcherExe, @"""C:\Hnc\Hwp.exe"" ""%1""") == WindowsIntegration.BuildWrapperCommand(launcherExe) &&
            WindowsIntegration.BuildWrapperCommand(launcherExe, @"C:\Hnc\Hwp.exe") == WindowsIntegration.BuildWrapperCommand(launcherExe) &&
            WindowsIntegration.BuildWrapperCommand(launcherExe, @"""C:\Other\Viewer.exe"" ""%1"" /x") == WindowsIntegration.BuildWrapperCommand(launcherExe));
    }
    finally
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testRegistryRoot, throwOnMissingSubKey: false); } catch { /* best effort */ }
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

// --- 장기 운용: 로그 정리, 자동 탐지 경로 캐시, 설정 읽기 재시도, 실행 중 한글 판별 ---
{
    string dir = NewTempDir();
    try
    {
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            string file = Path.Combine(dir, $"hwp-{i}.log");
            File.WriteAllText(file, "log");
            File.SetLastWriteTimeUtc(file, now.AddMinutes(-i));
        }
        string expired = Path.Combine(dir, "hwp-old.log");
        File.WriteAllText(expired, "log");
        File.SetLastWriteTimeUtc(expired, now.AddDays(-30));
        string unrelated = Path.Combine(dir, "keep.txt");
        File.WriteAllText(unrelated, "keep");

        int deleted = RejitLog.Prune(dir, TimeSpan.FromDays(14), maxFiles: 3, now);
        Check("logs: prune removes expired and surplus logs only",
            deleted == 3 && !File.Exists(expired) && File.Exists(unrelated) &&
            File.Exists(Path.Combine(dir, "hwp-0.log")) && File.Exists(Path.Combine(dir, "hwp-2.log")) &&
            !File.Exists(Path.Combine(dir, "hwp-3.log")));

        string settingsDir = Path.Combine(dir, "settings");
        Check("detected path: empty by default", SaveHookSettings.ReadDetectedPathRaw(settingsDir) is null);
        SaveHookSettings.TryWriteDetectedPath(@"C:\Hnc\Hwp.exe", settingsDir);
        Check("detected path: round-trips and stays separate from manual selection",
            SaveHookSettings.ReadDetectedPathRaw(settingsDir) == @"C:\Hnc\Hwp.exe" &&
            SaveHookSettings.ReadSelectedPathRaw(settingsDir) is null);
        SaveHookSettings.TryWriteDetectedPath(null, settingsDir);
        Check("detected path: cleared", SaveHookSettings.ReadDetectedPathRaw(settingsDir) is null);

        // 다른 창이 설정을 쓰는 중(공유 위반)에도 OFF로 잘못 읽지 않는다.
        SaveHookSettings.WriteHookEnabled(true, settingsDir);
        var locked = new FileStream(SaveHookSettings.GetModeFilePath(settingsDir), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(60); locked.Dispose(); });
        Check("settings: retries while another writer holds the file", SaveHookSettings.ReadHookEnabled(settingsDir));
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    int self = Environment.ProcessId;
    Check("processes: module probe finds a loaded module", HangulProcesses.HasModule(self, "kernel32.dll") == true);
    Check("processes: module probe reports a missing module", HangulProcesses.HasModule(self, "HwpSaveHookProfiler.dll") == false);
    if (Process.GetProcessesByName("Hwp").Length == 0)
    {
        Check("processes: no running Hangul -> None", HangulProcesses.GetState() == RunningHangulState.None);
    }
    // ISSUE-002: 동명 비한글 프로세스는 한글로 판정하지 않는다. 알 수 없으면 보수적으로 한글로 본다.
    Check("processes: unrelated executable is not Hangul",
        !HangulProcesses.LooksLikeHangulExecutable(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
    Check("processes: unknown or missing path stays conservative",
        HangulProcesses.LooksLikeHangulExecutable(null) &&
        HangulProcesses.LooksLikeHangulExecutable(Path.Combine(Path.GetTempPath(), "hwp-save-hook-no-such-file.exe")));

    // ISSUE-002 게이트 수준 확인: 동명 비한글 프로세스가 떠 있어도 실행 검사가 통과한다.
    // 실제 한글이 떠 있으면 판정이 섞이므로 그때는 건너뛴다.
    bool genuineRunning = false;
    foreach (Process running in Process.GetProcessesByName("Hwp"))
    {
        using (running)
        {
            if (HangulProcesses.LooksLikeHangulInstance(running)) { genuineRunning = true; break; }
        }
    }
    if (genuineRunning)
    {
        Console.WriteLine("  SKIP  processes: fake Hwp gate test (genuine Hangul running)");
    }
    else
    {
        string fakeDir = NewTempDir();
        Process? fake = null;
        bool gateOpen = false;
        try
        {
            string fakePath = Path.Combine(fakeDir, "Hwp.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "timeout.exe"), fakePath);
            fake = Process.Start(new ProcessStartInfo(fakePath, "/t 60 /nobreak")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            gateOpen = fake is not null && !SaveHookLauncher.IsHangulRunningInSession() &&
                HangulProcesses.GetState() == RunningHangulState.None;
        }
        catch { gateOpen = false; }
        finally
        {
            try
            {
                if (fake is not null)
                {
                    if (!fake.HasExited) fake.Kill();
                    fake.WaitForExit(5000);
                    fake.Dispose();
                }
            }
            catch { /* best effort */ }
            try { Directory.Delete(fakeDir, recursive: true); } catch { /* best effort */ }
        }
        Check("processes: unrelated Hwp process does not block the gate", gateOpen);
    }
}

// --- 저장 파일 형식 확인 ---
{
    string dir = NewTempDir();
    try
    {
        string hwp = Path.Combine(dir, "saved.hwp");
        File.WriteAllBytes(hwp, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00]);
        Check("savedfile: CFB magic is an HWP candidate",
            SavedFileInspector.Inspect(hwp).Kind == SavedFileKind.HwpCandidate);

        string hwpx = Path.Combine(dir, "saved.hwpx");
        using (var zip = ZipFile.Open(hwpx, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("mimetype").Open())) writer.Write("application/hwp+zip");
            using (var writer = new StreamWriter(zip.CreateEntry("Contents/header.xml").Open())) writer.Write("<x/>");
            using (var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open())) writer.Write("<x/>");
        }
        Check("savedfile: HWPX package is an HWPX candidate",
            SavedFileInspector.Inspect(hwpx).Kind == SavedFileKind.HwpxCandidate);

        string text = Path.Combine(dir, "saved.txt");
        File.WriteAllText(text, "hello");
        Check("savedfile: plain text is unknown",
            SavedFileInspector.Inspect(text).Kind == SavedFileKind.Unknown);
        Check("savedfile: missing file is unknown, not fatal",
            SavedFileInspector.Inspect(Path.Combine(dir, "no-such-file.hwp")).Kind == SavedFileKind.Unknown);
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

// --- 동시 실행: 시작 경계 직렬화 ---
{
    string mutex = @"Local\HwpSaveHook.Test." + Guid.NewGuid().ToString("N");
    int inside = 0;
    bool overlapped = false;
    int completed = 0;
    Func<int> guarded = () => SaveHookLauncher.StartSingleInstance(() => false, () =>
    {
        if (Interlocked.CompareExchange(ref inside, 1, 0) != 0) overlapped = true;
        Thread.Sleep(150);
        Volatile.Write(ref inside, 0);
        Interlocked.Increment(ref completed);
        return 1;
    }, mutex);
    var first = Task.Run(guarded);
    var second = Task.Run(guarded);
    Task.WaitAll(first, second);
    Check("concurrency: concurrent launches are serialized",
        !overlapped && completed == 2 && first.Result == 1 && second.Result == 1);
}

// --- 버전 호환: 실행 파일 구조 판정 ---
{
    HwpBuildInfo native = HwpBuildInspector.Inspect(Environment.ProcessPath!);
    Check("inspect: native apphost is classified as native", native.Kind == HwpBuildKind.Native && !native.HasCompatibleTarget);

    HwpBuildInfo anyCpu = HwpBuildInspector.Inspect(typeof(SaveHookLauncher).Assembly.Location);
    Check("inspect: managed AnyCPU library has no target and is not x86-only",
        anyCpu.Kind == HwpBuildKind.Managed && anyCpu.TargetToken is null && !anyCpu.IsX86 && anyCpu.Mvid is not null);

    Check("inspect: garbage bytes are unreadable",
        HwpBuildInspector.InspectImage([0x4D, 0x5A, 0x00]).Kind == HwpBuildKind.Unreadable);

    // ISSUE-001: 변조된 바이너리가 파서 예외를 밖으로 흘리면 안 된다.
    byte[] coreImage = File.ReadAllBytes(typeof(SaveHookLauncher).Assembly.Location);
    bool parserSurvives = true;
    for (int mutation = 0; mutation < 64 && parserSurvives; mutation++)
    {
        byte[] mutated = (byte[])coreImage.Clone();
        mutated[(mutation * 997) % mutated.Length] ^= 0xFF;
        try { HwpBuildInspector.InspectImage(mutated); }
        catch { parserSurvives = false; }
    }
    Check("inspect: single-byte mutations never escape the parser", parserSurvives);
    bool truncatedSurvives = true;
    try { HwpBuildInspector.InspectImage(coreImage[..1024]); }
    catch { truncatedSurvives = false; }
    Check("inspect: truncated image is unreadable, not fatal",
        truncatedSurvives && HwpBuildInspector.InspectImage(coreImage[..1024]).Kind == HwpBuildKind.Unreadable);

    Check("inspect: product names follow the HOffice major version",
        HwpBuildInspector.DescribeProduct("10.0.0.1") == "한글 2018" &&
        HwpBuildInspector.DescribeProduct("11.0.0.2") == "한글 2020" &&
        HwpBuildInspector.DescribeProduct("12.0.0.4645") == "한글 2022" &&
        HwpBuildInspector.DescribeProduct("13.0.0.100") == "한글 2024");

    // 실제 한글 2022가 설치된 PC에서는 실기 빌드로 카탈로그 값을 교차 확인한다.
    string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "HNC", "Office 2022", "HOffice120", "Bin", "Hwp.exe");
    KnownHwpBuild verified = HwpBuildCatalog.Verified[0];
    if (File.Exists(installed) && string.Equals(SaveHookLauncher.TryHashFile(installed), verified.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        HwpBuildInfo real = HwpBuildInspector.Inspect(installed);
        Check("inspect: installed Hangul 2022 matches the catalog MVID and token",
            real.HasCompatibleTarget && real.Mvid == verified.Mvid && real.TargetToken == verified.TargetToken);
        Check("inspect: installed Hangul 2022 target body matches the IL fingerprint",
            real.TargetSemanticsMatch && real.TargetCalls.Contains("IsGovernmentProduct") &&
            real.TargetCalls.Contains("IsEducationProduct") && real.TargetCalls.Contains("GetUniqueHwpxPath"));
        LaunchPlan realPlan = SaveHookLauncher.CreatePlan(installed, Environment.ProcessPath!, SaveHookLauncher.ProfilerClsid,
            ClrKind.NetFramework, SaveHookLauncher.DefaultExpectedSha256);
        Check("plan: verified build uses verified mode and catalog token",
            realPlan.Hooked && realPlan.TargetMode == TargetMode.Verified &&
            realPlan.ProfilerEnvironment[SaveHookLauncher.TargetModeVariable] == "verified" &&
            realPlan.ProfilerEnvironment[SaveHookLauncher.TargetTokenVariable] == "0x06000244");
    }
    else
    {
        Console.WriteLine("  SKIP  inspect: Hangul 2022 12.0.0.4645 is not installed");
    }

    var compatible = new HwpBuildInfo(HwpBuildKind.Managed, "13.0.0.1", "한글 2024", true, Guid.NewGuid(), "v4.0.30319", 0x06000250, null)
    {
        TargetCalls = ["IsNullOrEmpty", "IsEducationProduct", "GetUniqueHwpxPath"],
    };
    Check("structure: compatible build needs opt-in",
        SaveHookLauncher.EvaluateStructure(compatible, "AA", allowCompatible: false) is
            { Decision: HookDecision.RunWithoutHook, Reason: "compatible-needs-opt-in" });
    Check("structure: compatible build hooks after opt-in",
        SaveHookLauncher.EvaluateStructure(compatible, "AA", allowCompatible: true) is
            { Decision: HookDecision.Hook, Reason: "compatible-signature" });
    Check("structure: native build is never hooked",
        SaveHookLauncher.EvaluateStructure(compatible with { Kind = HwpBuildKind.Native, TargetToken = null }, "AA", true) is
            { Decision: HookDecision.RunWithoutHook, Reason: "native-build-unsupported" });
    Check("structure: managed build without the method is not hooked",
        SaveHookLauncher.EvaluateStructure(compatible with { TargetToken = null }, "AA", true) is
            { Reason: "target-not-found" });
    Check("structure: same signature without edition check and HWPX path is not hooked",
        SaveHookLauncher.EvaluateStructure(compatible with { TargetCalls = ["IsNullOrEmpty", "EndsWith"] }, "AA", true) is
            { Decision: HookDecision.RunWithoutHook, Reason: "target-semantics-differ" });
    Check("structure: government edition check also satisfies the fingerprint",
        SaveHookLauncher.EvaluateStructure(compatible with { TargetCalls = ["IsGovernmentProduct", "GetUniqueHwpxPath"] }, "AA", true) is
            { Reason: "compatible-signature" });
    Check("structure: AnyCPU build is not hooked by the x86 profiler",
        SaveHookLauncher.EvaluateStructure(compatible with { IsX86 = false }, "AA", true) is
            { Reason: "target-not-found" });

    const int pid = 77;
    string line = $"2026-11-27 10:00:00.000 [pid {pid}] ReJITState: token=0x06000250 state=";
    Check("ReJIT: custom target token is tracked",
        RejitLog.ReadStatus([line + "il-installed", line + "compiled"], pid, 0x06000250) == RejitStatus.Applied &&
        RejitLog.ReadStatus([line + "il-installed", line + "compiled"], pid) == RejitStatus.NotObserved);

    string settings = NewTempDir();
    try
    {
        Check("settings: compatible mode defaults to off", !SaveHookSettings.ReadCompatibleEnabled(settings));
        SaveHookSettings.WriteCompatibleEnabled(true, settings);
        Check("settings: compatible mode persists", SaveHookSettings.ReadCompatibleEnabled(settings));
    }
    finally
    {
        try { Directory.Delete(settings, recursive: true); } catch { /* best effort */ }
    }
}

// --- 업데이터: 서명 매니페스트 ---
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    ECParameters parameters = key.ExportParameters(false);
    string publicKey = Convert.ToBase64String([.. parameters.Q.X!, .. parameters.Q.Y!]);
    string sha = new string('a', 64);
    string Manifest(string version = "9.0.0", string url = "https://example.invalid/a.zip", string expires = "2099-01-01T00:00:00Z",
        long size = 100, string? signVersion = null)
    {
        byte[] payload = UpdateManifestVerifier.CanonicalPayload(signVersion ?? version, url, sha, size, expires);
        string signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));
        return $"{{\"version\":\"{version}\",\"artifact_url\":\"{url}\",\"sha256\":\"{sha}\",\"size\":{size},\"expires_at\":\"{expires}\",\"signature\":\"{signature}\"}}";
    }

    var current = new Version(1, 2, 0);
    UpdateManifest ok = UpdateManifestVerifier.Verify(Manifest(), publicKey, current);
    Check("update: valid signed manifest is accepted", ok.Version == new Version(9, 0, 0) && ok.Size == 100);

    bool Rejects<T>(Func<UpdateManifest> verify) where T : Exception
    {
        try { verify(); return false; }
        catch (T) { return true; }
    }
    Check("update: tampered version fails the signature",
        Rejects<CryptographicException>(() => UpdateManifestVerifier.Verify(Manifest(version: "9.0.1", signVersion: "9.0.0"), publicKey, current)));
    Check("update: same version is not an update",
        Rejects<NoUpdateAvailableException>(() => UpdateManifestVerifier.Verify(Manifest(version: "1.2"), publicKey, current)));
    Check("update: expired manifest is rejected",
        Rejects<FormatException>(() => UpdateManifestVerifier.Verify(Manifest(expires: "2000-01-01T00:00:00Z"), publicKey, current)));
    Check("update: expired manifest throws the expiry subtype",
        Rejects<ManifestExpiredException>(() => UpdateManifestVerifier.Verify(Manifest(expires: "2000-01-01T00:00:00Z"), publicKey, current)));
    Check("update: HTTP artifact is rejected even when signed",
        Rejects<FormatException>(() => UpdateManifestVerifier.Verify(Manifest(url: "http://example.invalid/a.zip"), publicKey, current)));
    using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    ECParameters otherParameters = other.ExportParameters(false);
    Check("update: another key's public key rejects the manifest",
        Rejects<CryptographicException>(() => UpdateManifestVerifier.Verify(Manifest(),
            Convert.ToBase64String([.. otherParameters.Q.X!, .. otherParameters.Q.Y!]), current)));
    Check("update: duplicate JSON keys are rejected",
        Rejects<FormatException>(() => UpdateManifestVerifier.Verify(
            Manifest().Replace("{\"version\"", "{\"version\":\"9.9.9\",\"version\""), publicKey, current)));
    Check("update: shipped build carries an update public key", UpdateInstaller.IsConfigured);
}

// --- 업데이터: 패키지 검사와 파일 교체·롤백 ---
{
    string dir = NewTempDir();
    try
    {
        string MakeZip(string name, params (string Path, string Text)[] entries)
        {
            string path = Path.Combine(dir, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (entryPath, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
                writer.Write(text);
            }
            return path;
        }

        var complete = new[] { ("HwpSaveHook.exe", "new-exe"), ("HwpSaveHook.exe.config", "new-config"),
            ("profiler/HwpSaveHookProfiler.dll", "new-dll"), ("LICENSE", "new-license") };
        string good = MakeZip("good.zip", complete);
        string payload = Path.Combine(dir, "payload");
        UpdateInstaller.ExtractPackage(good, payload);
        Check("package: valid ZIP extracts all files", File.ReadAllText(Path.Combine(payload, "profiler", "HwpSaveHookProfiler.dll")) == "new-dll");

        bool ExtractFails(string zip)
        {
            try { UpdateInstaller.ExtractPackage(zip, Path.Combine(dir, "x-" + Guid.NewGuid().ToString("N"))); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check("package: path traversal is rejected",
            ExtractFails(MakeZip("evil.zip", [.. complete, ("../escape.txt", "x")])));
        Check("package: missing profiler DLL is rejected",
            ExtractFails(MakeZip("partial.zip", ("HwpSaveHook.exe", "a"), ("HwpSaveHook.exe.config", "b"))));

        string artifact = Path.Combine(dir, "artifact.bin");
        File.WriteAllText(artifact, "artifact");
        string artifactHash = SaveHookLauncher.TryHashFile(artifact)!;
        Check("package: matching hash and size pass", Try(() => UpdateInstaller.VerifyArtifact(artifact, artifactHash, 8)));
        Check("package: wrong hash fails", !Try(() => UpdateInstaller.VerifyArtifact(artifact, new string('0', 64), 8)));

        string install = Path.Combine(dir, "install");
        Directory.CreateDirectory(Path.Combine(install, "profiler"));
        void SeedInstall()
        {
            File.WriteAllText(Path.Combine(install, "HwpSaveHook.exe"), "old-exe");
            File.WriteAllText(Path.Combine(install, "HwpSaveHook.exe.config"), "old-config");
            File.WriteAllText(Path.Combine(install, "profiler", "HwpSaveHookProfiler.dll"), "old-dll");
            File.WriteAllText(Path.Combine(install, "user-note.txt"), "keep");
            if (File.Exists(Path.Combine(install, "LICENSE"))) File.Delete(Path.Combine(install, "LICENSE"));
        }

        SeedInstall();
        UpdateResult failed = UpdateInstaller.ReplaceFiles(payload, install, "9.0.0", _ => false);
        Check("apply: failed smoke check rolls back every file",
            failed.Status == "rolled-back" &&
            File.ReadAllText(Path.Combine(install, "HwpSaveHook.exe")) == "old-exe" &&
            File.ReadAllText(Path.Combine(install, "profiler", "HwpSaveHookProfiler.dll")) == "old-dll" &&
            !File.Exists(Path.Combine(install, "LICENSE")) &&
            Directory.GetDirectories(install, ".update-backup-*").Length == 0 &&
            Directory.GetFiles(install, "*.update-*", SearchOption.AllDirectories).Length == 0);

        string? smoked = null;
        UpdateResult applied = UpdateInstaller.ReplaceFiles(payload, install, "9.0.0", exe => { smoked = exe; return true; });
        Check("apply: successful update replaces files and keeps unrelated ones",
            applied.Status == "applied" &&
            File.ReadAllText(Path.Combine(install, "HwpSaveHook.exe")) == "new-exe" &&
            File.ReadAllText(Path.Combine(install, "profiler", "HwpSaveHookProfiler.dll")) == "new-dll" &&
            File.ReadAllText(Path.Combine(install, "user-note.txt")) == "keep" &&
            Directory.GetDirectories(install, ".update-backup-*").Length == 0);
        Check("apply: smoke check runs the installed launcher", smoked == Path.Combine(install, "HwpSaveHook.exe"));

        // ISSUE-003: 예전 실패분의 백업은 다음 업데이트가 지우면 안 된다.
        string staleBackup = Path.Combine(install, ".update-backup-old");
        Directory.CreateDirectory(staleBackup);
        File.WriteAllText(Path.Combine(staleBackup, "HwpSaveHook.exe"), "older");
        UpdateResult preserved = UpdateInstaller.ReplaceFiles(payload, install, "9.0.1", _ => true);
        Check("apply: stale backups from failed restores are preserved",
            preserved.Status == "applied" && Directory.Exists(staleBackup) &&
            File.ReadAllText(Path.Combine(install, "HwpSaveHook.exe")) == "new-exe");

        string staging = Path.Combine(dir, "updates");
        Check("apply: only Stage work folders are cleaned after success",
            UpdateInstaller.StagedWorkDirectory(Path.Combine(staging, "update-9.0.0-ab", "payload"), staging)
                == Path.Combine(staging, "update-9.0.0-ab") &&
            UpdateInstaller.StagedWorkDirectory(payload, staging) is null &&
            UpdateInstaller.StagedWorkDirectory(Path.Combine(dir, "update-x", "payload"), staging) is null);

        Check("apply: helper arguments survive spaces and trailing backslashes",
            UpdateInstaller.QuoteArgument(@"C:\Program Files\Hwp\") == "\"C:\\Program Files\\Hwp\\\\\"");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

static bool Try(Action action)
{
    try { action(); return true; }
    catch (InvalidDataException) { return false; }
}

Console.WriteLine();
Console.WriteLine($"{total - failures}/{total} passed, {failures} failed.");
return failures == 0 ? 0 : 1;
