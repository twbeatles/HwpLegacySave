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
    private bool _allowCompatible;
    private UpdateManifest? _pendingUpdate;
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
        CompatSwitch.Checked += CompatChanged;
        CompatSwitch.Unchecked += CompatChanged;
        VersionText.Text = $"버전 {FormatVersion(UpdateInstaller.CurrentVersion)}";
        Activated += (_, _) =>
        {
            if (!_busy)
            {
                RefreshMode();
                RefreshIntegrationStatus();
            }
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Scan(restoreSelection: true);
        UpdateResult? previous = UpdateInstaller.ConsumeResult();
        if (previous is not null)
        {
            ActivityStatus.Text = previous.Status == "applied"
                ? $"{previous.Version} 버전으로 업데이트했습니다."
                : $"업데이트를 완료하지 못했습니다. {previous.Message}";
            return;
        }
        await CheckForUpdate(userInitiated: false);
    }
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
            int others = 0;
            var (plan, manual) = await HwpInstallations.InspectWithTimeoutAsync(token =>
            {
                // 여러 버전이 설치된 PC에서 다른 설치본이 있음을 알린다.
                others = Math.Max(0, HwpInstallations.FindAll(token).Count - 1);
                string? path = restoreSelection ? ReadSelectedPath(token)
                    : selectedPath is not null ? HwpInstallations.FindPreferred(selectedPath, token) : null;
                bool isManual = path is not null;
                if (path is null && selectedPath is null) path = HwpInstallations.FindPreferred(cancellationToken: token);
                token.ThrowIfCancellationRequested();
                return (path is null ? null : CreatePlan(path), isManual);
            });
            _plan = plan;
            HwpPath.Text = _plan?.HwpPath ?? "";
            string buildStatus = DescribeBuild(_plan);
            BuildStatus.Text = _plan is null ? buildStatus : $"{(manual ? "직접 지정" : "자동 선택")} · {buildStatus}";
            if (_plan is not null && others > 0)
            {
                BuildStatus.Text += $"\n이 PC에서 한글 설치본 {others}개를 더 찾았습니다. 다른 버전을 쓰려면 찾아보기로 선택하세요.";
            }
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

    private static string DescribeBuild(LaunchPlan? plan)
    {
        if (plan is null) return "한글 실행 파일을 찾지 못했습니다.";
        string label = plan.BuildLabel ?? "알 수 없는 한글 빌드";
        return plan.Reason switch
        {
            "build-hash-match" => $"{label} · 검증된 빌드 · HWP 저장 사용 가능",
            "compatible-signature" => $"{label} · 검증되지 않은 빌드(실험) · 저장 형식 변환 코드 구조 일치",
            "compatible-needs-opt-in" => $"{label} · 검증되지 않은 빌드입니다. 구조가 같아 아래 '검증되지 않은 한글 버전에도 시도'를 켜면 적용할 수 있습니다.",
            "native-build-unsupported" => $"{label} · 이 버전의 Hwp.exe는 .NET 기반이 아니어서 이 방식으로 바꿀 수 없습니다. OFF로 일반 실행하세요.",
            "target-not-found" => $"{label} · 저장 형식 변환 코드를 찾지 못해 지원하지 않습니다. OFF로 일반 실행하세요.",
            "target-semantics-differ" => $"{label} · 저장 형식 변환 코드의 동작이 검증 빌드와 달라 적용하지 않습니다. OFF로 일반 실행하세요.",
            "profiler-missing" => "프로파일러 DLL이 없습니다. OFF로 바꾸면 일반 실행할 수 있습니다.",
            "build-hash-mismatch" => "다른 한글 빌드입니다. OFF로 바꾸면 일반 실행할 수 있습니다.",
            "hwp-unreadable" => "한글 실행 파일을 읽을 수 없습니다.",
            "forced-no-hook" => $"{label} · 일반 실행",
            _ => label,
        };
    }

    private static string FormatVersion(Version version) =>
        version.Build > 0 || version.Revision <= 0 ? version.ToString(3) : version.ToString();

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
            _allowCompatible = SaveHookSettings.ReadCompatibleEnabled();
            CompatSwitch.IsChecked = _allowCompatible;
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

    private async void CompatChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        bool selected = CompatSwitch.IsChecked == true;
        if (selected == _allowCompatible) return;

        try
        {
            SaveHookSettings.WriteCompatibleEnabled(selected);
            _allowCompatible = selected;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            _isUpdatingUi = true;
            try { CompatSwitch.IsChecked = _allowCompatible; }
            finally { _isUpdatingUi = false; }
            ActivityStatus.Text = $"설정을 저장하지 못했습니다. {error.Message}";
            return;
        }

        if (_plan is LaunchPlan current)
        {
            await RunOperation(async () =>
            {
                _plan = await HwpInstallations.InspectWithTimeoutAsync(_ => CreatePlan(current.HwpPath));
                BuildStatus.Text = DescribeBuild(_plan);
            });
        }
        ActivityStatus.Text = selected
            ? "검증되지 않은 버전에도 시도합니다. 저장한 뒤 파일 형식을 꼭 확인하세요."
            : "검증된 한글 빌드에서만 HWP 저장 모드를 사용합니다.";
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            await CheckForUpdate(userInitiated: true);
            return;
        }
        await InstallUpdate(_pendingUpdate);
    }

    private async Task CheckForUpdate(bool userInitiated)
    {
        if (!UpdateInstaller.IsConfigured)
        {
            if (userInitiated) ActivityStatus.Text = "이 빌드에는 업데이트 서명 키가 없어 자동 업데이트를 사용할 수 없습니다.";
            return;
        }

        // 시작 시 자동 확인은 다른 버튼을 잠그지 않는다(오프라인이면 요청 시한까지 걸릴 수 있다).
        Func<Func<Task>, Task> run = userInitiated ? RunOperation : action => action();
        await run(async () =>
        {
            if (userInitiated) ActivityStatus.Text = "새 버전을 확인하는 중입니다.";
            try
            {
                UpdateManifest? manifest = await Task.Run(() => UpdateInstaller.Check());
                _pendingUpdate = manifest;
                UpdateButton.Content = manifest is null ? "업데이트 확인" : $"{FormatVersion(manifest.Version)} 설치";
                if (!userInitiated && _busy) return; // 진행 중인 작업의 안내 문구를 덮지 않는다.
                if (manifest is not null)
                {
                    ActivityStatus.Text = $"새 버전 {FormatVersion(manifest.Version)}이 있습니다. '{UpdateButton.Content}'를 눌러 설치하세요.";
                }
                else if (userInitiated)
                {
                    ActivityStatus.Text = "현재 최신 버전입니다.";
                }
            }
            catch (Exception error) when (!userInitiated && error is IOException or InvalidDataException or FormatException
                                          or System.Security.Cryptography.CryptographicException or InvalidOperationException
                                          or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 시작 시 자동 확인 실패는 조용히 넘긴다(오프라인 등).
            }
            catch (Exception error) when (error is InvalidDataException or FormatException
                                          or System.Security.Cryptography.CryptographicException)
            {
                ActivityStatus.Text = $"업데이트 정보를 확인할 수 없습니다. {error.Message}";
            }
        });
    }

    private async Task InstallUpdate(UpdateManifest manifest)
    {
        string launcher = WindowsIntegration.GetCurrentLauncherExecutablePath();
        string installDir = Path.GetDirectoryName(launcher)!;
        if (!UpdateInstaller.CanWrite(installDir))
        {
            ActivityStatus.Text = "설치 폴더에 쓸 수 없어 자동 업데이트를 할 수 없습니다. 릴리스 페이지에서 새 ZIP을 받아 직접 교체하세요.";
            OpenUrl(UpdateInstaller.ReleasesUrl);
            return;
        }

        var answer = MessageBox.Show(this,
            $"버전 {FormatVersion(manifest.Version)}을 내려받아 설치합니다.\n설치 중 런처가 닫혔다가 다시 열립니다. 열려 있는 한글은 먼저 저장하고 닫아 주세요.",
            "한글 HWP 저장 런처 업데이트", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;

        bool launched = false;
        await RunOperation(async () =>
        {
            if (SaveHookLauncher.IsHangulRunningInSession())
            {
                ActivityStatus.Text = "한글이 실행 중입니다. 문서를 저장하고 한글을 닫은 뒤 다시 시도하세요.";
                return;
            }
            ActivityStatus.Text = "새 버전을 내려받는 중입니다.";
            string payload = await Task.Run(() => UpdateInstaller.Stage(manifest));
            using Process helper = UpdateInstaller.LaunchHelper(payload, installDir, launcher, relaunch: true);
            launched = true;
        });
        if (launched) Application.Current.Shutdown();
    }

    private void OpenUrl(string url)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ActivityStatus.Text += $" ({url})";
        }
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
                BuildStatus.Text = DescribeBuild(plan);
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
                    timeout: TimeSpan.FromSeconds(6), cancellationToken: observation.Token, targetToken: plan.TargetToken);
                ShowRejitStatus(status, log);
                continueObserving = status is RejitStatus.RequestAccepted or RejitStatus.NotObserved;
                if (continueObserving) _ = ObserveCompilation(process, log, observation, plan.TargetToken);
            }
            catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
            finally
            {
                if (!continueObserving) EndObservation(process, observation);
            }
        });
    }

    private LaunchPlan CreatePlan(string path, bool forceNormal = false) =>
        SaveHookLauncher.CreatePlan(path, SaveHookLauncher.DefaultProfilerPath, SaveHookLauncher.ProfilerClsid,
            ClrKind.NetFramework, SaveHookLauncher.DefaultExpectedSha256, forceRunWithoutHook: forceNormal,
            allowCompatible: _allowCompatible);

    private async Task ObserveCompilation(Process process, string log, CancellationTokenSource observation, int targetToken)
    {
        try
        {
            RejitStatus status = await RejitLog.WaitAsync(process, log, waitForCompilation: true, timeout: null,
                cancellationToken: observation.Token, targetToken: targetToken);
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
        CompatSwitch.IsEnabled = !_busy;
        UpdateButton.IsEnabled = !_busy;
    }
}
