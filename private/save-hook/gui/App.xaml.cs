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

    private static async Task<int> RunTransparentAsync(string[] forwardedArgs)
    {
        try
        {
            string? hwpPath = await HwpInstallations.InspectWithTimeoutAsync(token =>
            {
                string? stored = SaveHookSettings.ReadSelectedPathRaw();
                if (stored is not null)
                {
                    string? validated = HwpInstallations.FindPreferred(stored, token);
                    if (validated is not null) return validated;
                }
                return HwpInstallations.FindPreferred(cancellationToken: token);
            });

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
}
