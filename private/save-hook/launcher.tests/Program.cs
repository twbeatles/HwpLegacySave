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
    async Task<(int Code, string Output, string Error)> RunPlan(params string[] arguments)
    {
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { cli, "plan" }.Concat(arguments)) start.ArgumentList.Add(arg);
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
            () => false,
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
            () => true,
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
        Check("transparent: existing session opens document without profiler env",
            !existingResult.AttachedProfiler &&
            capturedExisting is not null &&
            !capturedExisting.Environment.ContainsKey("COR_ENABLE_PROFILING") &&
            !capturedExisting.Environment.ContainsKey("HWP_SAVEHOOK_LOG") &&
            capturedExisting.ArgumentList.SequenceEqual([@"C:\test\doc2.hwp"]));

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
    }
    finally
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testRegistryRoot, throwOnMissingSubKey: false); } catch { /* best effort */ }
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
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

    var compatible = new HwpBuildInfo(HwpBuildKind.Managed, "13.0.0.1", "한글 2024", true, Guid.NewGuid(), "v4.0.30319", 0x06000250, null);
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
