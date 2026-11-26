using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using HwpSaveHook.Launcher;

namespace HwpSaveHook.Gui;

public partial class App : Application
{
    private async void App_Startup(object sender, StartupEventArgs e)
    {
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
                forceRunWithoutHook: !useHook);

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
