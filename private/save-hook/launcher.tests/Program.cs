using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

Console.WriteLine();
Console.WriteLine($"{total - failures}/{total} passed, {failures} failed.");
return failures == 0 ? 0 : 1;
