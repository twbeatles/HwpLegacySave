using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HwpSaveHook.Launcher;
using Microsoft.Win32;

namespace HwpSaveHook.Gui;

public partial class MainWindow : Window
{
    private LaunchPlan? _plan;
    private string? _logPath;
    private bool _useHook;
    private bool _busy;
    private bool _isUpdatingUi;
    private IntegrationStatus _shortcutState;
    private IntegrationStatus _assocState;
    private CancellationTokenSource? _observation;

    public MainWindow()
    {
        InitializeComponent();
        RefreshMode();
        RefreshIntegrationStatus();
        ModeSwitch.Checked += ModeChanged;
        ModeSwitch.Unchecked += ModeChanged;
        AssocSwitch.Checked += AssocChanged;
        AssocSwitch.Unchecked += AssocChanged;
        Activated += (_, _) =>
        {
            if (!_busy)
            {
                RefreshMode();
                RefreshIntegrationStatus();
            }
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await Scan(restoreSelection: true);
    private async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "한글 실행 파일 선택", Filter = "한글 실행 파일|Hwp.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true)
        {
            await Scan(picker.FileName);
        }
    }

    private async Task Scan(string? selectedPath = null, bool restoreSelection = false)
    {
        await RunOperation(async () =>
        {
            ActivityStatus.Text = "설치된 한글을 확인하는 중입니다.";
            _plan = null;
            HwpPath.Text = "";
            BuildStatus.Text = "";
            var (plan, manual) = await HwpInstallations.InspectWithTimeoutAsync(token =>
            {
                string? path = restoreSelection ? ReadSelectedPath(token)
                    : selectedPath is not null ? HwpInstallations.FindPreferred(selectedPath, token) : null;
                bool isManual = path is not null;
                if (path is null && selectedPath is null) path = HwpInstallations.FindPreferred(cancellationToken: token);
                token.ThrowIfCancellationRequested();
                return (path is null ? null : CreatePlan(path), isManual);
            });
            _plan = plan;
            HwpPath.Text = _plan?.HwpPath ?? "";
            string buildStatus = _plan?.Reason switch
            {
                "build-hash-match" => "한글 2022 12.0.0.4645 x86 · HWP 저장 사용 가능",
                "profiler-missing" => "프로파일러 DLL이 없습니다. OFF로 바꾸면 일반 실행할 수 있습니다.",
                "build-hash-mismatch" => "다른 한글 빌드입니다. OFF로 바꾸면 일반 실행할 수 있습니다.",
                "hwp-unreadable" => "한글 실행 파일을 읽을 수 없습니다.",
                _ => "한글 실행 파일을 찾지 못했습니다."
            };
            BuildStatus.Text = _plan is null ? buildStatus : $"{(manual ? "직접 지정" : "자동 선택")} · {buildStatus}";
            ActivityStatus.Text = _plan is null
                ? "찾아보기에서 설치 폴더의 Hwp.exe를 선택하세요."
                : "실행 중인 한글이 있으면 문서를 저장하고 종료한 뒤 이 창에서 열어 주세요.";

            if (_plan is not null && selectedPath is not null)
            {
                SaveHookSettings.WriteSelectedPath(_plan.HwpPath);
            }
            else if (!restoreSelection && selectedPath is null)
            {
                SaveHookSettings.WriteSelectedPath(null);
            }
        });
    }

    private static string? ReadSelectedPath(CancellationToken token)
    {
        string? stored = SaveHookSettings.ReadSelectedPathRaw();
        if (stored is null) return null;
        try { return HwpInstallations.FindPreferred(stored, token); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        RefreshMode();
        await Start(_useHook);
    }

    private void RefreshMode()
    {
        _isUpdatingUi = true;
        try
        {
            _useHook = SaveHookSettings.ReadHookEnabled();
            ModeSwitch.IsChecked = _useHook;
        }
        finally
        {
            _isUpdatingUi = false;
        }
        UpdateButtons();
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        bool selected = ModeSwitch.IsChecked == true;
        if (selected == _useHook) return;

        try
        {
            SaveHookSettings.WriteHookEnabled(selected);
            _useHook = selected;
            ActivityStatus.Text = selected
                ? "다음 실행부터 HWP 저장 모드를 사용합니다."
                : "다음 실행부터 일반 모드를 사용합니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            _isUpdatingUi = true;
            try { ModeSwitch.IsChecked = _useHook; }
            finally { _isUpdatingUi = false; }
            ActivityStatus.Text = $"실행 모드를 저장하지 못했습니다. {error.Message}";
        }
        UpdateButtons();
    }

    private void RefreshIntegrationStatus()
    {
        string launcherExe = WindowsIntegration.GetCurrentLauncherExecutablePath();
        _shortcutState = WindowsIntegration.GetShortcutStatus(WindowsIntegration.GetDesktopShortcutPath(), launcherExe);
        _assocState = WindowsIntegration.GetFileAssociationStatus(launcherExe);

        ShortcutButton.Content = _shortcutState switch
        {
            IntegrationStatus.Enabled => "삭제",
            IntegrationStatus.PointsElsewhere => "다시 만들기",
            _ => "만들기"
        };
        ShortcutStatus.Text = _shortcutState switch
        {
            IntegrationStatus.Enabled => $"바탕화면에 '{WindowsIntegration.DefaultShortcutName}' 바로가기가 있습니다.",
            IntegrationStatus.PointsElsewhere => "다른 위치의 런처를 가리키고 있습니다. 다시 만들기로 갱신하세요.",
            _ => "바탕화면에 한글(HWP 저장) 실행 바로가기를 만듭니다."
        };

        _isUpdatingUi = true;
        try
        {
            AssocSwitch.IsChecked = _assocState == IntegrationStatus.Enabled;
        }
        finally
        {
            _isUpdatingUi = false;
        }

        AssocStatus.Text = _assocState switch
        {
            IntegrationStatus.Enabled => "탐색기에서 .hwp 파일을 열 때 자동으로 HWP 저장 모드로 실행됩니다.",
            IntegrationStatus.PointsElsewhere => "다른 위치의 런처와 연결되어 있습니다. ON으로 바꾸면 현재 위치로 갱신됩니다.",
            _ => "탐색기에서 .hwp 파일을 더블클릭할 때 자동으로 적용합니다."
        };
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string shortcutPath = WindowsIntegration.GetDesktopShortcutPath();
        string launcherExe = WindowsIntegration.GetCurrentLauncherExecutablePath();

        try
        {
            if (_shortcutState == IntegrationStatus.Enabled)
            {
                WindowsIntegration.RemoveShortcut(shortcutPath);
                ActivityStatus.Text = "바탕화면 바로가기를 삭제했습니다.";
            }
            else
            {
                WindowsIntegration.CreateShortcut(shortcutPath, launcherExe, _plan?.HwpPath);
                ActivityStatus.Text = $"바탕화면에 '{WindowsIntegration.DefaultShortcutName}' 바로가기를 만들었습니다.";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or System.Runtime.InteropServices.COMException)
        {
            ActivityStatus.Text = $"바탕화면 바로가기를 처리하지 못했습니다. {error.Message}";
        }

        RefreshIntegrationStatus();
    }

    private void AssocChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || _busy) return;
        bool enable = AssocSwitch.IsChecked == true;
        string launcherExe = WindowsIntegration.GetCurrentLauncherExecutablePath();

        try
        {
            if (enable)
            {
                WindowsIntegration.EnableFileAssociation(launcherExe);
                ActivityStatus.Text = "이제 탐색기에서 .hwp 파일을 더블클릭하면 자동으로 HWP 저장 모드가 적용됩니다.";
            }
            else
            {
                WindowsIntegration.DisableFileAssociation();
                ActivityStatus.Text = ".hwp 파일 더블클릭 연동을 해제하고 원래 한글 설정으로 복구했습니다.";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ActivityStatus.Text = $".hwp 파일 연결을 변경하지 못했습니다. {error.Message}";
        }

        RefreshIntegrationStatus();
    }

    private async Task Start(bool useHook)
    {
        if (_plan is not LaunchPlan selected) return;
        await RunOperation(async () =>
        {
            ActivityStatus.Text = "한글을 시작하는 중입니다.";
            LaunchPlan plan = await HwpInstallations.InspectWithTimeoutAsync(token =>
            {
                string? path = HwpInstallations.FindPreferred(selected.HwpPath, token);
                if (path is null) throw new InvalidOperationException("선택한 한글 실행 파일을 찾을 수 없습니다.");
                return CreatePlan(path, forceNormal: !useHook);
            });
            if (useHook && !plan.Hooked)
            {
                _plan = plan;
                BuildStatus.Text = "HWP 저장을 사용할 수 없습니다. 실행 파일과 DLL을 다시 확인하세요.";
                ActivityStatus.Text = "한글을 시작하지 않았습니다.";
                return;
            }

            string? log = useHook ? RejitLog.NewPath() : null;
            Process process = SaveHookLauncher.Launch(plan, profilerLog: log);
            if (log is null)
            {
                using (process)
                {
                    ActivityStatus.Text = await Task.Run(() => process.WaitForExit(300))
                        ? "시작한 한글 프로세스가 바로 종료됐습니다."
                        : "한글을 일반 모드로 시작했습니다.";
                }
                return;
            }

            ActivityStatus.Text = "한글이 시작됐습니다. HWP 저장 준비를 확인하는 중입니다.";
            _observation?.Cancel();
            var observation = new CancellationTokenSource();
            _observation = observation;
            bool continueObserving = false;
            try
            {
                RejitStatus status = await RejitLog.WaitAsync(process, log, waitForCompilation: false,
                    timeout: TimeSpan.FromSeconds(6), cancellationToken: observation.Token);
                ShowRejitStatus(status, log);
                continueObserving = status is RejitStatus.RequestAccepted or RejitStatus.NotObserved;
                if (continueObserving) _ = ObserveCompilation(process, log, observation);
            }
            catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
            finally
            {
                if (!continueObserving) EndObservation(process, observation);
            }
        });
    }

    private static LaunchPlan CreatePlan(string path, bool forceNormal = false) =>
        SaveHookLauncher.CreatePlan(path, SaveHookLauncher.DefaultProfilerPath, SaveHookLauncher.ProfilerClsid,
            ClrKind.NetFramework, SaveHookLauncher.DefaultExpectedSha256, forceRunWithoutHook: forceNormal);

    private async Task ObserveCompilation(Process process, string log, CancellationTokenSource observation)
    {
        try
        {
            RejitStatus status = await RejitLog.WaitAsync(process, log, waitForCompilation: true, timeout: null,
                cancellationToken: observation.Token);
            if (_observation == observation && !observation.IsCancellationRequested) ShowRejitStatus(status, log);
        }
        catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            if (_observation == observation) ActivityStatus.Text = $"HWP 저장 상태를 확인하지 못했습니다. {error.Message}";
        }
        finally { EndObservation(process, observation); }
    }

    private void EndObservation(Process process, CancellationTokenSource observation)
    {
        if (_observation == observation) _observation = null;
        observation.Dispose();
        process.Dispose();
    }

    private void ShowRejitStatus(RejitStatus status, string log)
    {
        _logPath = File.Exists(log) ? log : null;
        LogButton.Visibility = _logPath is null ? Visibility.Collapsed : Visibility.Visible;
        ActivityStatus.Text = status switch
        {
            RejitStatus.RequestAccepted => "HWP 저장 요청이 접수됐습니다. 첫 저장에서 적용 결과를 확인합니다.",
            RejitStatus.Applied => "이 한글 프로세스에 HWP 저장 모드가 적용됐습니다.",
            RejitStatus.Failed => "HWP 저장 모드 적용에 실패했습니다. 실행 로그와 실제 저장 형식을 확인하세요.",
            RejitStatus.ProcessExited => "시작한 한글이 HWP 저장 모드 적용 확인 전에 종료됐습니다.",
            _ => "HWP 저장 모드가 아직 확인되지 않았습니다. 한글의 저장 형식을 확인하세요."
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _observation?.Cancel();
        base.OnClosed(e);
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_logPath is null) return;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            ActivityStatus.Text = $"실행 로그를 열지 못했습니다. {error.Message}";
        }
    }

    private async Task RunOperation(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        try { await action(); }
        catch (HangulAlreadyRunningException)
        {
            ActivityStatus.Text = "실행 중인 한글을 종료한 뒤 다시 시도하세요.";
        }
        catch (TimeoutException)
        {
            ActivityStatus.Text = "한글 설치 경로 확인이 지연됐습니다. 찾아보기에서 Hwp.exe를 직접 지정하거나 다시 찾으세요.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
                                      or Win32Exception or InvalidOperationException or SecurityException)
        {
            ActivityStatus.Text = error.Message;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        LaunchButton.IsEnabled = !_busy && _plan is not null && (!_useHook || _plan.Hooked);
        ModeSwitch.IsEnabled = !_busy;
        ScanButton.IsEnabled = !_busy;
        BrowseButton.IsEnabled = !_busy;
        ShortcutButton.IsEnabled = !_busy;
        AssocSwitch.IsEnabled = !_busy;
    }
}
