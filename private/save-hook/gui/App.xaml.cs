using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HwpSaveHook.Launcher;

namespace HwpSaveHook.Gui;

public partial class App : Application
{
    private async void App_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == "--smoke")
        {
            // 업데이트 교체 직후 새 버전이 시작되고 필수 파일이 있는지만 확인한다. 창을 띄우지 않는다.
            Shutdown(File.Exists(SaveHookLauncher.DefaultProfilerPath) ? 0 : 1);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Task.Run(() => ApplyUpdate(e.Args)));
            return;
        }

        var (isOpenMode, forwardedArgs) = SaveHookSettings.ParseOpenArgs(e.Args);
        if (!isOpenMode)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int exitCode = await RunTransparentAsync(forwardedArgs);
        Shutdown(exitCode);
    }

    /// <summary>임시 폴더에 복사된 헬퍼로 실행될 때 설치 폴더를 교체한다.</summary>
    private static int ApplyUpdate(string[] args)
    {
        string? Value(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        string? payload = Value("--payload");
        string? installDir = Value("--install-dir");
        if (payload is null || installDir is null ||
            !int.TryParse(Value("--parent-pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int parent))
        {
            return 2;
        }

        UpdateResult result = UpdateInstaller.Apply(payload, installDir, parent, SaveHookLauncher.IsHangulRunningInSession);
        if (args.Contains("--relaunch"))
        {
            string launcher = Path.Combine(installDir, "HwpSaveHook.exe");
            try
            {
                using Process? restarted = Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = installDir });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                MessageBox.Show($"업데이트 후 런처를 다시 열지 못했습니다.\n{launcher}\n{error.Message}",
                    "한글 HWP 저장 런처", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        return result.Status == "applied" ? 0 : 1;
    }

    private const string Title = "한글 HWP 저장 런처";

    private static async Task<int> RunTransparentAsync(string[] forwardedArgs)
    {
        try
        {
            // 한컴 업데이트나 기본 앱 변경으로 풀린 한글 문서 연결을 다시 감싼다(연동을 켠 사용자만).
            await Task.Run(TryRepairAssociation);

            string? hwpPath = await HwpInstallations.InspectWithTimeoutAsync(token =>
            {
                // 직접 지정 → 마지막 자동 탐지 → 전체 탐지 순서. 매번 전체 탐지를 하면
                // 절전 중인 드라이브 등 때문에 시간이 초과돼 더블클릭이 실패할 수 있다.
                foreach (string? stored in new[] { SaveHookSettings.ReadSelectedPathRaw(), SaveHookSettings.ReadDetectedPathRaw() })
                {
                    if (stored is null) continue;
                    string? validated = TryValidate(stored, token);
                    if (validated is not null) return validated;
                }
                string? found = HwpInstallations.FindPreferred(cancellationToken: token);
                SaveHookSettings.TryWriteDetectedPath(found);
                return found;
            }, TimeSpan.FromSeconds(15));

            if (hwpPath is null)
            {
                MessageBox.Show(
                    "한글 실행 파일(Hwp.exe)을 찾을 수 없습니다.\n한글 HWP 저장 런처(HwpSaveHook.exe)를 먼저 실행해 한글 경로를 지정해 주세요.",
                    "한글 HWP 저장 런처",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return 3;
            }

            bool useHook = SaveHookSettings.ReadHookEnabled();
            LaunchPlan plan = SaveHookLauncher.CreatePlan(
                hwpPath,
                SaveHookLauncher.DefaultProfilerPath,
                SaveHookLauncher.ProfilerClsid,
                ClrKind.NetFramework,
                SaveHookLauncher.DefaultExpectedSha256,
                forceRunWithoutHook: !useHook,
                allowCompatible: SaveHookSettings.ReadCompatibleEnabled());

            if (useHook && !plan.Hooked)
            {
                // 예: 한컴 자동 업데이트로 Hwp.exe 빌드가 바뀐 경우. 조용히 일반 모드로 열면 HWPX로 저장된다.
                MessageBoxResult answer = MessageBox.Show(
                    "HWP 저장 모드를 적용할 수 없어 한글을 일반 모드로 엽니다.\n" +
                    "이 상태로 저장하면 HWPX(.hwpx)로 바뀔 수 있습니다.\n\n" +
                    $"사유: {SaveHookLauncher.DescribePlan(plan)}\n\n" +
                    "일반 모드로 열까요? 한글 HWP 저장 런처(HwpSaveHook.exe)를 열면 자세한 상태를 볼 수 있습니다.",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (answer != MessageBoxResult.OK) return 0;
            }
            else if (plan.Hooked && HangulProcesses.GetState() == RunningHangulState.SomeUnhooked)
            {
                // 한글은 실행 중인 창으로 문서를 넘긴다. 그 창이 프로파일러 없이 시작됐다면 저장이 HWPX로 바뀐다.
                MessageBoxResult answer = MessageBox.Show(
                    "이미 실행 중인 한글이 HWP 저장 모드 없이 시작됐습니다.\n" +
                    "(시작 메뉴·작업 표시줄이나 다른 프로그램으로 한글을 먼저 연 경우입니다.)\n\n" +
                    "이 문서는 그 한글 창에서 열리므로 저장하면 HWPX(.hwpx)로 바뀔 수 있습니다.\n\n" +
                    "[예] 그래도 그 창에서 열기\n" +
                    "[아니요] 열지 않기 — 열린 문서를 저장하고 한글을 모두 닫은 뒤 다시 열면 HWP 저장 모드가 적용됩니다.",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return 0;
            }

            string? log = plan.Hooked ? RejitLog.NewPath() : null;
            TransparentLaunchResult result = SaveHookLauncher.LaunchTransparent(plan, forwardedArgs, profilerLog: log);
            result.Process.Dispose();
            return 0;
        }
        catch (TimeoutException)
        {
            MessageBox.Show(
                "한글 설치 경로 확인 시간이 초과되었습니다.\n한글 HWP 저장 런처를 실행해 Hwp.exe 경로를 직접 지정해 주세요.",
                "한글 HWP 저장 런처",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 4;
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"한글을 실행하지 못했습니다.\n{error.Message}",
                "한글 HWP 저장 런처",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    private static string? TryValidate(string stored, System.Threading.CancellationToken token)
    {
        try { return HwpInstallations.FindPreferred(stored, token); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException
                                      or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void TryRepairAssociation()
    {
        try
        {
            WindowsIntegration.RepairFileAssociation(WindowsIntegration.GetCurrentLauncherExecutablePath());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException or InvalidOperationException)
        {
            // 연결 복구는 부가 기능이다. 실패해도 문서는 연다.
        }
    }
}
