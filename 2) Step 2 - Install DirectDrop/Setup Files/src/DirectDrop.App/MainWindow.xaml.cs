using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DirectDrop.App.Models;
using DirectDrop.App.Services;
using DirectDrop.Core;
using DirectDrop.Core.Models;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;

namespace DirectDrop.App;

public partial class MainWindow : Window
{
    private readonly DirectDropSettingsService _settingsService = new();
    private DirectDropSettings _settings = new();
    private static readonly TimeSpan DeviceConsideredConnectedWindow = TimeSpan.FromSeconds(20);
    private readonly SessionCoordinator _coordinator = new();
    private readonly DispatcherTimer _pollTimer;
    private CancellationTokenSource? _startCts;
    private string? _lastConnectionUrl;
    private string? _wifiSsid;
    private string? _wifiPassword;
    private bool _hasWifiCredentials;
    private bool _startupAttemptInProgress;
    private bool _networkAccessPromptShown;
    private bool _deviceWasConnected;
    private bool _fastModePromptInProgress;
    private InAppPromptKind _activePromptKind;
    private TaskCompletionSource<bool>? _promptCompletion;
    private readonly HashSet<string> _fadingCancelledTransfers = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        // Set the window icon after XAML initialization. Keeping the ICO out of
        // the Window.Icon XAML attribute avoids WPF TypeConverter/MarkupExtension
        // startup failures on some Windows installations. The executable still
        // carries the ICO for the Windows taskbar/Start Menu shell icon.
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/DirectDrop.png", UriKind.Absolute));
        }
        catch
        {
            // A missing cosmetic icon must never prevent DirectDrop from starting.
        }
        _settings = _settingsService.Load();
        _coordinator.PhaseChanged += OnPhaseChanged;
        _coordinator.HotspotExternallyChanged += OnHotspotExternallyChanged;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _pollTimer.Tick += (_, _) =>
        {
            RefreshActiveSessionUi();
            _coordinator.CheckHotspotHealth();
        };

        Loaded += MainWindow_Loaded;
        Closing += async (_, _) =>
        {
            _startCts?.Cancel();
            if (_coordinator.CurrentSession is not null)
                await _coordinator.StopAsync(turnOffHotspotIfWeStartedIt: false);
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_settings.HasCompletedFirstRunSetup)
        {
            var setup = new SettingsWindow(_settingsService, _settings, firstRun: true) { Owner = this };
            bool? result = setup.ShowDialog();
            if (result == true)
            {
                _settings = _settingsService.Load();
            }
            else
            {
                // Keep the app usable even if the user dismisses onboarding.
                _settings.HasCompletedFirstRunSetup = true;
                _settingsService.Save(_settings);
            }
        }

        if (!await EnsureNetworkAccessAsync())
        {
            Close();
            return;
        }

        await StartDirectDropAsync();
    }

    private enum InAppPromptKind
    {
        NetworkAccess,
        FastMode
    }

    private Task<bool> ShowInAppPromptAsync(
        InAppPromptKind kind,
        string title,
        string subtitle,
        string body,
        string primaryText,
        string secondaryText,
        bool showRemember)
    {
        _activePromptKind = kind;
        _promptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        PromptTitle.Text = title;
        PromptSubtitle.Text = subtitle;
        PromptBody.Text = body;
        PromptPrimaryButton.Content = primaryText;
        PromptSecondaryButton.Content = secondaryText;
        PromptSecondaryButton.Visibility = string.IsNullOrWhiteSpace(secondaryText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PromptRememberCheck.Visibility = showRemember ? Visibility.Visible : Visibility.Collapsed;
        PromptRememberCheck.IsChecked = true;

        // Keep the alert centered over the entire application and softly blur
        // the app content underneath it so the modal is the visual focus.
        var blur = new BlurEffect { Radius = 7 };
        RootLayout.Children[0].Effect = blur;
        RootLayout.Children[1].Effect = blur;
        PromptOverlay.Visibility = Visibility.Visible;
        PromptOverlay.UpdateLayout();
        PromptPrimaryButton.Focus();

        return _promptCompletion.Task;
    }

    private void CompleteInAppPrompt(bool accepted)
    {
        if (PromptOverlay.Visibility != Visibility.Visible) return;
        PromptOverlay.Visibility = Visibility.Collapsed;
        RootLayout.Children[0].Effect = null;
        RootLayout.Children[1].Effect = null;
        _promptCompletion?.TrySetResult(accepted);
        _promptCompletion = null;
    }

    private void PromptPrimaryButton_Click(object sender, RoutedEventArgs e)
        => CompleteInAppPrompt(true);

    private void PromptSecondaryButton_Click(object sender, RoutedEventArgs e)
        => CompleteInAppPrompt(false);

    private async Task<bool> EnsureNetworkAccessAsync()
    {
        if (_settings.HasGrantedNetworkAccess) return true;
        if (_networkAccessPromptShown) return true;
        _networkAccessPromptShown = true;

        bool allow = await ShowInAppPromptAsync(
            InAppPromptKind.NetworkAccess,
            "Allow network access?",
            "Required for phone transfers",
            "Windows may ask you to allow DirectDrop through the firewall. Choose Allow access.",
            "Continue",
            "Exit",
            showRemember: false);

        if (!allow) return false;

        _settings.HasGrantedNetworkAccess = true;
        _settingsService.Save(_settings);
        return true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settingsService, _settings) { Owner = this };
        if (dialog.ShowDialog() == true)
            _settings = _settingsService.Load();
    }


    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Visible;
    }

    private void CloseHelpButton_Click(object sender, RoutedEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void OpenTransferFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = _settings.UploadDestination;
            if (string.IsNullOrWhiteSpace(folder))
                folder = DirectDropSettingsService.DefaultUploadDestination;

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not open transfer folder.", ex);
            System.Windows.MessageBox.Show(this, "Could not open the transfer folder.", "DirectDrop",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartDirectDropAsync()
    {
        if (_startupAttemptInProgress || _coordinator.CurrentSession is not null) return;
        _startupAttemptInProgress = true;
        _lastConnectionUrl = null;
        ShowStartup();
        SetIdleStatus("Setting up…");

        _startCts?.Dispose();
        _startCts = new CancellationTokenSource();
        SessionStartOutcome outcome;
        try
        {
            string sessionShareFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DirectDrop", "SessionShare");
            outcome = await _coordinator.StartAsync(sessionShareFolder, _settings.UploadDestination, _settings.PreferredPort, _startCts.Token);
        }
        catch (OperationCanceledException)
        {
            _startupAttemptInProgress = false;
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Unexpected startup failure", ex);
            outcome = new SessionStartOutcome(false, null, null, null, "DirectDrop couldn't initialize the local connection.");
        }

        _startupAttemptInProgress = false;
        if (!outcome.Success)
        {
            // Treat a local server/bind failure as recoverable. The coordinator
            // already retries with a fresh adapter; this final retry keeps the
            // UI on the startup step instead of forcing the user to quit and
            // relaunch the whole application just to refresh state.
            SetIdleStatus("Setting up…");
            try
            {
                await Task.Delay(1000, _startCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_coordinator.CurrentSession is null && !_startupAttemptInProgress)
                await StartDirectDropAsync();
            return;
        }

        _lastConnectionUrl = outcome.ConnectionUrl;
        _wifiSsid = outcome.WifiNetworkName;
        _wifiPassword = outcome.WifiPassword;
        PreparePairing(outcome.ConnectionUrl!, _wifiSsid, _wifiPassword);
        _pollTimer.Start();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        bool turnOffHotspot = false;
        if (_coordinator.DidWeStartTheHotspot)
        {
            turnOffHotspot = await ShowInAppPromptAsync(
                InAppPromptKind.NetworkAccess,
                "End DirectDrop session?",
                "Mobile Hotspot is active",
                "DirectDrop turned on Mobile Hotspot for this session. Turn it off when the session ends?",
                "Turn off hotspot",
                "Keep hotspot on",
                showRemember: false);
        }

        _pollTimer.Stop();
        StopButton.IsEnabled = false;
        await _coordinator.StopAsync(turnOffHotspot);
        FastModeToggle.IsChecked = false;
                FastModeStatusText.Text = "Normal Windows settings";
        _lastConnectionUrl = null;
        _wifiSsid = null;
        _wifiPassword = null;
        _hasWifiCredentials = false;
        StopButton.IsEnabled = true;
        await StartDirectDropAsync();
    }

    private void OpenHotspotSettingsButton_Click(object sender, RoutedEventArgs e) => HotspotService.OpenHotspotSettings();

    private async void FastModeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (FastModeToggle.IsChecked == true)
        {
            if (!_settings.FastModePreferenceSet)
            {
                FastModeToggle.IsChecked = false;
                await PromptAndEnableFastModeAsync(saveChoice: true);
            }
            else
            {
                await ApplyFastModeAsync();
            }
        }
        else
        {
            await DisableFastModeAsync();
        }
    }

    private async Task PromptAndEnableFastModeAsync(bool saveChoice)
    {
        if (_fastModePromptInProgress) return;
        _fastModePromptInProgress = true;

        bool disconnectWifi = _coordinator.ContentionRiskFromPcWifi;
        string body =
            "Fast Mode improves local transfer performance." +
            "\n\nWarning: Internet may not work until DirectDrop is closed. " +
            "Fast Mode may switch Windows to High Performance and disconnect the PC's current Wi‑Fi connection when needed. " +
            "Bluetooth stays on. Changes are restored when Fast Mode is turned off or DirectDrop closes.";

        bool enable = await ShowInAppPromptAsync(
            InAppPromptKind.FastMode,
            "Enable Fast Mode?",
            "Optimize this transfer session",
            body,
            "Enable Fast Mode",
            "Not now",
            showRemember: saveChoice);

        bool remember = PromptRememberCheck.IsChecked == true;
        if (saveChoice && remember)
        {
            _settings.FastModePreferenceSet = true;
            _settings.PreferFastMode = enable;
            _settingsService.Save(_settings);
        }

        if (enable)
        {
            FastModeToggle.IsChecked = true;
            await ApplyFastModeAsync();
        }

        _fastModePromptInProgress = false;
    }

    private async Task ApplyFastModeAsync()
    {
        FastModeToggle.IsEnabled = false;
        bool enabled = false;
        try { enabled = await _coordinator.EnableFastModeAsync(); }
        catch (Exception ex) { AppLogger.Error("Fast Mode enable failed", ex); }
        FastModeToggle.IsEnabled = true;

        if (!enabled)
        {
            FastModeToggle.IsChecked = false;
            FastModeStatusText.Text = "Normal Windows settings";
            return;
        }

        FastModeStatusText.Text = _coordinator.ContentionRiskFromPcWifi
            ? "Fast Mode on · High Performance + Wi‑Fi station disconnected"
            : "Fast Mode on · High Performance power plan active";
    }

    private async Task DisableFastModeAsync()
    {
        FastModeToggle.IsEnabled = false;
        try { await _coordinator.DisableFastModeAsync(); }
        catch (Exception ex) { AppLogger.Error("Fast Mode restore failed", ex); }
        FastModeToggle.IsEnabled = true;
        FastModeStatusText.Text = "Normal Windows settings";
    }

    private async void OnHotspotExternallyChanged(bool isOn)
    {
        if (isOn)
        {
            await Dispatcher.InvokeAsync(() => HotspotDroppedPanel.Visibility = Visibility.Collapsed);
            return;
        }

        if (_coordinator.CurrentSession is null) return;

        await Dispatcher.InvokeAsync(() => HotspotDroppedPanel.Visibility = Visibility.Visible);
        HotspotResult result = await _coordinator.RetryEnableHotspotAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            if (result.Success)
                HotspotDroppedPanel.Visibility = Visibility.Collapsed;
        });
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        SessionState? session = _coordinator.CurrentSession;
        if (session is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Choose files to send to your phone", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        QueueLocalFiles(session, dialog.FileNames);
        RefreshActiveSessionUi();
        await Task.CompletedTask;
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        SessionState? session = _coordinator.CurrentSession;
        if (session is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to send to your phone" };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        string root = dialog.FolderName;
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        QueueLocalFiles(session, files, root);
        RefreshActiveSessionUi();
        await Task.CompletedTask;
    }

    private void QueueLocalFiles(SessionState session, IEnumerable<string> sourcePaths, string? sourceRoot = null)
    {
        foreach (string sourcePath in sourcePaths)
        {
            try
            {
                if (!File.Exists(sourcePath)) continue;
                var info = new FileInfo(sourcePath);
                string displayName = sourceRoot is null ? Path.GetFileName(sourcePath) : Path.GetRelativePath(sourceRoot, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
                string transferId = session.QueueOutgoingTransfer(displayName, info.Length);
                _ = Task.Run(() =>
                {
                    try
                    {
                        string publishedPath = sourceRoot is null ? CopyFileIntoShared(sourcePath, session.SharedFolder) : CopyFileIntoSharedPreservingRelativePath(sourcePath, sourceRoot, session.SharedFolder);
                        if (session.Transfers.TryGetValue(transferId, out TransferItem? transfer) && transfer.Status != TransferStatus.Cancelled)
                        {
                            session.RegisterSharedFile(publishedPath, transferId);
                            transfer.LocalPath = publishedPath;
                        }
                        else { try { File.Delete(publishedPath); } catch { } }
                    }
                    catch (Exception ex)
                    {
                        if (session.Transfers.TryGetValue(transferId, out TransferItem? transfer)) transfer.MarkFailed(ex.Message);
                        AppLogger.Error($"Could not stage selected item for phone transfer: {sourcePath}", ex);
                    }
                    Dispatcher.Invoke(RefreshActiveSessionUi);
                });
            }
            catch (Exception ex) { AppLogger.Error($"Could not prepare selected item for DirectDrop: {sourcePath}", ex); }
        }
    }



    private async void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.CommandParameter is not string transferId || string.IsNullOrWhiteSpace(transferId)) return;
        button.IsEnabled = false;
        try
        {
            await _coordinator.CancelTransferAsync(transferId);

            // Let the cancelled row visibly leave the list instead of
            // instantly snapping away. The coordinator keeps the cancelled
            // state authoritative; the UI simply fades the old row out.
            if (FindTransferViewModel(transferId) is TransferViewModel vm)
            {
                await FadeOutTransferRowAsync(vm);
            }

            RefreshActiveSessionUi();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Could not cancel transfer {transferId}", ex);
            button.IsEnabled = true;
        }
    }

    private TransferViewModel? FindTransferViewModel(string transferId) =>
        (TransfersList.ItemsSource as IEnumerable<TransferViewModel>)?.FirstOrDefault(t => t.TransferId == transferId);

    private async Task FadeOutTransferRowAsync(TransferViewModel viewModel)
    {
        // ItemContainerGenerator can be a little behind immediately after a
        // refresh, so gracefully skip animation if WPF has no realized row.
        if (TransfersList.ItemContainerGenerator.ContainerFromItem(viewModel) is not FrameworkElement container)
            return;

        Border? rowBorder = FindVisualChild<Border>(container);
        if (rowBorder is null) return;

        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(320),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        rowBorder.BeginAnimation(UIElement.OpacityProperty, animation);
        await Task.Delay(340);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            T? nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OpenTransferFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.CommandParameter is not string path || string.IsNullOrWhiteSpace(path)) return;

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                string? parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    OpenFolder(parent);
                return;
            }

            // Use Explorer's native /select mode so the individual transfer
            // action always reveals the exact file instead of merely opening
            // the transfer directory.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { AppLogger.Error($"Could not reveal transfer file: {path}", ex); }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLogger.Error("Could not open folder", ex); }
    }

    private void OnPhaseChanged(SessionPhase phase, string? detail)
    {
        Dispatcher.Invoke(() =>
        {
            switch (phase)
            {
                case SessionPhase.DetectingNetwork:
                case SessionPhase.EnablingHotspot:
                case SessionPhase.WaitingForManualHotspot:
                case SessionPhase.ConfiguringFirewall:
                case SessionPhase.StartingServer:
                    SetIdleStatus("Setting up…");
                    break;
                case SessionPhase.Ready: break;
            }
        });
    }

    private void SetIdleStatus(string text, bool isError = false, bool isWarning = false)
    {
        // Startup intentionally exposes no internal hotspot/network stages.
        // Keep the user-facing state to the single, neutral "Setting up" label.
        IdleStatusText.Text = "Setting up";
        StartupProgress.Visibility = Visibility.Visible;
    }

    private void ShowStartup()
    {
        StartupPanel.Visibility = Visibility.Visible;
        PairingPanel.Visibility = Visibility.Collapsed;
        TransferPanel.Visibility = Visibility.Collapsed;
    }

    private void PreparePairing(string connectionUrl, string? wifiSsid, string? wifiPassword)
    {
        _hasWifiCredentials = !string.IsNullOrWhiteSpace(wifiSsid);
        RefreshActiveSessionUi();
    }

    private void ShowPairing()
    {
        StartupPanel.Visibility = Visibility.Collapsed;
        PairingPanel.Visibility = Visibility.Visible;
        TransferPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowTransferWorkspace()
    {
        StartupPanel.Visibility = Visibility.Collapsed;
        PairingPanel.Visibility = Visibility.Collapsed;
        TransferPanel.Visibility = Visibility.Visible;
        FastModePanel.Visibility = Visibility.Visible;
    }

    private void RefreshActiveSessionUi()
    {
        SessionState? session = _coordinator.CurrentSession;
        if (session is null) return;

        var recentDevices = session.ConnectedDevices.Values
            .Where(d => DateTimeOffset.UtcNow - d.LastSeenUtc < DeviceConsideredConnectedWindow)
            .OrderByDescending(d => d.LastSeenUtc)
            .ToList();
        bool hasHttpDevice = recentDevices.Count > 0;

        if (hasHttpDevice)
        {
            bool newlyConnected = !_deviceWasConnected;
            _deviceWasConnected = true;
            ShowTransferWorkspace();
            ConnectionStatusDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            ConnectionStatusText.Text = $"{recentDevices[0].DeviceName ?? "Phone"} connected";
            DevicesList.ItemsSource = recentDevices.Select(d => $"· {d.DeviceName ?? "Connected phone"}").ToList();

            if (newlyConnected)
            {
                if (_settings.FastModePreferenceSet && _settings.PreferFastMode)
                {
                    FastModeToggle.IsChecked = true;
                    _ = ApplyFastModeAsync();
                }
                else if (!_settings.FastModePreferenceSet)
                {
                    _ = PromptAndEnableFastModeAsync(saveChoice: true);
                }
            }
        }
        else
        {
            _deviceWasConnected = false;
            ShowPairing();
            DevicesList.ItemsSource = null;
            RefreshPairingStepUi();
        }

        OtherDeviceBlockedText.Visibility = session.BlockedDeviceAttempts.Values.Any(seenAt => DateTimeOffset.UtcNow - seenAt < DeviceConsideredConnectedWindow)
            ? Visibility.Visible : Visibility.Collapsed;

        var cancelledTransfers = session.Transfers.Values.Where(t => t.Status == TransferStatus.Cancelled).ToList();
        var transfers = session.Transfers.Values
            .Where(t => t.Status != TransferStatus.Cancelled || !_fadingCancelledTransfers.Contains(t.Id))
            .OrderByDescending(t => t.StartedAt)
            .Take(30)
            .Select(TransferViewModel.From)
            .ToList();
        TransfersList.ItemsSource = transfers;

        foreach (var cancelledTransfer in cancelledTransfers)
        {
            if (_fadingCancelledTransfers.Add(cancelledTransfer.Id)) _ = FadeOutCancelledTransferAsync(cancelledTransfer.Id);
        }
        NoTransfersText.Visibility = transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPairingStepUi()
    {
        if (_lastConnectionUrl is null) return;
        int? clientCount = _coordinator.GetHotspotClientCount();
        bool joinedWifi = clientCount.HasValue && clientCount.Value > 0;
        bool showWifi = _hasWifiCredentials && !joinedWifi;
        if (showWifi)
        {
            PairingEyebrow.Text = "STEP 1 OF 2";
            PairingTitle.Text = "Connect to this Wi‑Fi";
            PairingSubtitle.Text = "Scan this QR with your phone camera to join DirectDrop's local Wi‑Fi.";
            PairingStatusText.Text = "Waiting for Wi‑Fi connection";
            PairingStatusDot.Fill = (System.Windows.Media.Brush)FindResource("WarningBrush");
            PairingQrImage.Source = QrCodeService.GeneratePng(QrCodeService.BuildWifiQrPayload(_wifiSsid!, _wifiPassword ?? ""));
            PairingPrimaryText.Text = _wifiSsid ?? "DirectDrop Wi‑Fi";
            PairingSecondaryText.Text = string.IsNullOrEmpty(_wifiPassword) ? "Open network" : $"Password: {_wifiPassword}";
            ConnectionDetailsTitle.Text = "Connection details";
            ConnectionDetailsText.Text = string.IsNullOrEmpty(_wifiPassword) ? $"Wi‑Fi: {_wifiSsid}" : $"Wi‑Fi: {_wifiSsid}\nPassword: {_wifiPassword}";
            ConnectionDetailsCard.Visibility = Visibility.Visible;
        }
        else
        {
            PairingEyebrow.Text = "STEP 2 OF 2";
            PairingTitle.Text = "Scan this QR to open the DirectDrop site";
            PairingSubtitle.Text = "Use your phone camera to open the DirectDrop transfer site.";
            PairingStatusText.Text = joinedWifi ? "Wi‑Fi connected · waiting for site" : "Waiting for device";
            PairingStatusDot.Fill = (System.Windows.Media.Brush)FindResource(joinedWifi ? "SuccessBrush" : "WarningBrush");
            PairingQrImage.Source = QrCodeService.GeneratePng(_lastConnectionUrl);
            PairingPrimaryText.Text = "Scan to open the DirectDrop site";
            PairingSecondaryText.Text = "The transfer workspace opens automatically when your phone connects.";
            ConnectionDetailsCard.Visibility = Visibility.Collapsed;
        }
    }


    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = (_coordinator.CurrentSession is not null && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        SessionState? session = _coordinator.CurrentSession;
        if (session is null || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        foreach (string path in (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (string publishedPath in CopyDirectoryIntoShared(path, session.SharedFolder))
                        session.RegisterSharedFile(publishedPath);
                }
                else if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    string transferId = session.QueueOutgoingTransfer(Path.GetFileName(path), info.Length);
                    string publishedPath = CopyFileIntoShared(path, session.SharedFolder);
                    if (session.Transfers.TryGetValue(transferId, out TransferItem? transfer) && transfer.Status != TransferStatus.Cancelled)
                    {
                        session.RegisterSharedFile(publishedPath, transferId);
                        transfer.LocalPath = publishedPath;
                    }
                    else
                    {
                        try { File.Delete(publishedPath); } catch { }
                    }
                }
            }
            catch (IOException ex) { AppLogger.Error("Could not stage dropped item for phone transfer", ex); }
        }
        RefreshActiveSessionUi();
    }

    private static string CopyFileIntoShared(string sourcePath, string sharedFolder)
    {
        string safeName = PathSecurity.SanitizeFileName(Path.GetFileName(sourcePath));
        string destination = PathSecurity.MakeUniquePath(Path.Combine(sharedFolder, safeName));
        File.Copy(sourcePath, destination);
        return destination;
    }

    private static string CopyFileIntoSharedPreservingRelativePath(string sourcePath, string sourceRoot, string sharedFolder)
    {
        string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
        string relativeDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        string safeFileName = PathSecurity.SanitizeFileName(Path.GetFileName(relativePath));

        string destinationRoot = Path.Combine(sharedFolder, PathSecurity.SanitizeFileName(new DirectoryInfo(sourceRoot).Name));
        string destinationDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
            ? destinationRoot
            : Path.Combine(destinationRoot, relativeDirectory);

        Directory.CreateDirectory(destinationDirectory);
        string destination = PathSecurity.MakeUniquePath(Path.Combine(destinationDirectory, safeFileName));
        File.Copy(sourcePath, destination);
        return destination;
    }

    private async Task FadeOutCancelledTransferAsync(string transferId)
    {
        // RefreshActiveSessionUi intentionally leaves a newly-cancelled row
        // visible for one frame so the user can see it fade rather than having
        // it disappear abruptly.
        await Dispatcher.InvokeAsync(() =>
        {
            if (FindTransferViewModel(transferId) is not TransferViewModel vm) return;
            if (TransfersList.ItemContainerGenerator.ContainerFromItem(vm) is not FrameworkElement container) return;

            Border? rowBorder = FindVisualChild<Border>(container);
            if (rowBorder is null) return;

            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(350),
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            rowBorder.BeginAnimation(UIElement.OpacityProperty, animation);
        });

        await Task.Delay(380);
        await Dispatcher.InvokeAsync(RefreshActiveSessionUi);
    }

    private static List<string> CopyDirectoryIntoShared(string sourceDir, string sharedFolder)
    {
        string destinationRoot = PathSecurity.MakeUniquePath(Path.Combine(sharedFolder, PathSecurity.SanitizeFileName(new DirectoryInfo(sourceDir).Name)));
        Directory.CreateDirectory(destinationRoot);
        var copiedFiles = new List<string>();
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            copiedFiles.Add(destination);
        }
        return copiedFiles;
    }
}
