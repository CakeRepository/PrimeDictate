using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using SharpHook;

namespace PrimeDictate;

public partial class App : System.Windows.Application
{
    private readonly Icon appIcon = AppIconProvider.LoadWindowIcon();
    private readonly Icon trayReadyIcon = AppIconProvider.CreateTrayIcon(TrayVisualState.Ready);
    private readonly Icon trayRecordingIcon = AppIconProvider.CreateTrayIcon(TrayVisualState.Recording);
    private readonly Icon trayProcessingIcon = AppIconProvider.CreateTrayIcon(TrayVisualState.Processing);
    private readonly Icon trayErrorIcon = AppIconProvider.CreateTrayIcon(TrayVisualState.Error);
    private readonly DictationAudioCuePlayer audioCuePlayer = new();
    private readonly DispatcherTimer errorStateTimer;
    private Forms.NotifyIcon? notifyIcon;
    private DictationController? dictationController;
    private GlobalHotkeyListener? hotkeyListener;
    private SettingsStore? settingsStore;
    private TranscriptionHistoryStore? historyStore;
    private DictationStatsStore? statsStore;
    private GitHubUpdateService? updateService;
    private DictationStatsState? stats;
    private AppSettings? settings;
    private Task? hookTask;
    private SettingsWindow? settingsWindow;
    private MainWindow? workspaceWindow;
    private HistoryWindow? historyWindow;
    private TranscriptionOverlayWindow? transcriptionOverlayWindow;
    private Forms.ToolStripMenuItem? checkForUpdatesMenuItem;
    private readonly DictationWorkspaceViewModel workspaceViewModel = new();
    private readonly TranscriptionHistoryViewModel historyViewModel = new();
    private string overlayTranscript = string.Empty;
    private bool isRecording;
    private bool isProcessing;
    private bool isCheckingForUpdates;
    private bool isInstallingUpdate;
    private bool overlayExpandedFromCompact;
    private DateTime errorStateUntilUtc = DateTime.MinValue;

    public App()
    {
        this.errorStateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        this.errorStateTimer.Tick += this.OnErrorStateTimerTick;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryHandleValidationCommandLine(e.Args, out var validationExitCode))
        {
            this.Shutdown(validationExitCode);
            return;
        }

        if (LaunchAtLoginManager.TryHandleCommandLine(e.Args, out var exitCode))
        {
            this.Shutdown(exitCode);
            return;
        }

        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        this.settingsStore = new SettingsStore();
        this.historyStore = new TranscriptionHistoryStore();
        this.statsStore = new DictationStatsStore();
        this.updateService = new GitHubUpdateService();
        this.settings = this.settingsStore.LoadOrDefault();
        var settingsChanged = TranscriptionRuntimeSupport.NormalizeSettingsForCurrentMachine(this.settings);
        settingsChanged |= NormalizeShortcutSettings(this.settings);
        if (settingsChanged)
        {
            this.settingsStore.Save(this.settings);
        }

        var historyEntries = this.historyStore.Load();
        this.historyViewModel.Load(historyEntries);
        this.stats = this.statsStore.LoadOrCreate(historyEntries);
        this.UpdateRuntimeStatusUi();

        this.dictationController = new DictationController(
            this.settings.ExclusiveMicAccessWhileDictating,
            this.settings.SelectedInputDeviceId,
            this.settings.InputGainMultiplier,
            TimeSpan.FromSeconds(this.settings.AutoCommitSilenceSeconds),
            this.settings.SendEnterAfterCommit,
            this.settings.ReturnToStartTargetOnCommit,
            this.settings.TranscriptionBackend,
            this.settings.TranscriptionComputeInterface,
            this.settings.SelectedModelId,
            this.settings.ModelPath,
            this.settings.EnableOllamaPostProcessing,
            this.settings.OllamaEndpoint,
            this.settings.OllamaModel,
            this.settings.OllamaMode,
            this.settings.EnableVoiceCommands,
            this.settings.VoiceDictationPhrase,
            this.settings.VoiceStopPhrase,
            this.settings.VoiceHistoryPhrase,
            this.settings.VoiceShellCommands ?? new List<VoiceShellCommand>(),
            this.settings.TranscriptReplacements ?? new List<TranscriptReplacementRule>());
        this.dictationController.RecordingStateChanged += this.OnRecordingStateChanged;
        this.dictationController.ProcessingStateChanged += this.OnProcessingStateChanged;
        this.dictationController.ThreadStarted += this.OnThreadStarted;
        this.dictationController.ThreadCompleted += this.OnThreadCompleted;
        this.dictationController.ThreadTranscriptUpdated += this.OnThreadTranscriptUpdated;
        this.dictationController.TranscriptCommitted += this.OnTranscriptCommitted;
        this.dictationController.AudioLevelUpdated += this.OnAudioLevelUpdated;
        this.dictationController.HistoryRequested += this.OnHistoryRequested;
        this.hotkeyListener = new GlobalHotkeyListener(
            this.dictationController.ToggleRecordingAsync,
            this.dictationController.StopRecordingAsync,
            this.ShowHistoryFromHotkeyAsync,
            this.settings.DictationHotkey,
            this.settings.StopHotkey,
            this.settings.HistoryHotkey);
        this.hookTask = this.hotkeyListener.RunAsync();
        AppLog.EntryWritten += this.OnAppLogEntryWritten;

        this.notifyIcon = this.CreateNotifyIcon();
        this.UpdateTrayState();

        if (!this.settings.FirstRunCompleted)
        {
            this.ShowSettingsWindow(isFirstRun: true);
        }
        else
        {
            this.QueueAutomaticUpdateCheck();
        }
    }

    private static bool TryHandleValidationCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        try
        {
            if (string.Equals(args[0], "--qnn-aihub-whisper-transcribe", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3)
                {
                    throw new ArgumentException(
                        "Usage: --qnn-aihub-whisper-transcribe <model-directory> <pcm16khz-mono-wav> [output-file]");
                }

                WriteValidationResult(
                    QualcommQnnWhisperValidationHarness.RunAihubWavTranscription(args[1], args[2]),
                    args.Length >= 4 ? args[3] : null);
                return true;
            }

            if (string.Equals(args[0], "--qnn-whisper-smoke", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3)
                {
                    throw new ArgumentException("Usage: --qnn-whisper-smoke <model-directory> <Cpu|Npu> [output-file]");
                }

                WriteValidationResult(
                    QualcommQnnWhisperValidationHarness.RunBackendSmokeValidation(args[1], args[2]),
                    args.Length >= 4 ? args[3] : null);
                return true;
            }

            if (string.Equals(args[0], "--qnn-whisper-proof", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    throw new ArgumentException("Usage: --qnn-whisper-proof <model-directory> [true|false] [output-file]");
                }

                var strictValidation = args.Length < 3 || bool.Parse(args[2]);
                var outputFile = args.Length >= 4 ? args[3] : null;
                WriteValidationResult(
                    QualcommQnnWhisperValidationHarness.RunSmokeValidation(args[1], strictValidation),
                    outputFile);
                return true;
            }

            if (string.Equals(args[0], "--qnn-smoke", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3)
                {
                    throw new ArgumentException("Usage: --qnn-smoke <model-directory> <Cpu|Npu> [output-file]");
                }

                WriteValidationResult(
                    QualcommQnnValidationHarness.RunBackendSmokeValidation(args[1], args[2]),
                    args.Length >= 4 ? args[3] : null);
                return true;
            }

            if (string.Equals(args[0], "--qnn-proof", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    throw new ArgumentException("Usage: --qnn-proof <model-directory> [true|false] [output-file]");
                }

                var strictValidation = args.Length < 3 || bool.Parse(args[2]);
                var outputFile = args.Length >= 4 ? args[3] : null;
                WriteValidationResult(
                    QualcommQnnValidationHarness.RunSmokeValidation(args[1], strictValidation),
                    outputFile);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
            return true;
        }

        return false;
    }

    private static void WriteValidationResult(string result, string? outputFile)
    {
        Console.WriteLine(result);

        if (string.IsNullOrWhiteSpace(outputFile))
        {
            return;
        }

        var fullOutputPath = Path.GetFullPath(outputFile);
        var directory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullOutputPath, result);
    }

    private static bool NormalizeShortcutSettings(AppSettings settings)
    {
        var changed = false;

        if (settings.DictationHotkey is null || !settings.DictationHotkey.IsValid(out _))
        {
            settings.DictationHotkey = HotkeyGesture.Default;
            changed = true;
        }

        if (settings.StopHotkey is null || !settings.StopHotkey.IsValid(out _) ||
            AreSameGesture(settings.StopHotkey, settings.DictationHotkey))
        {
            settings.StopHotkey = AreSameGesture(HotkeyGesture.DefaultStop, settings.DictationHotkey)
                ? new HotkeyGesture { KeyCode = SharpHook.Data.KeyCode.VcEnter, Ctrl = true, Shift = false, Alt = true }
                : HotkeyGesture.DefaultStop;
            changed = true;
        }

        if (settings.HistoryHotkey is null || !settings.HistoryHotkey.IsValid(out _) ||
            AreSameGesture(settings.HistoryHotkey, settings.DictationHotkey) ||
            AreSameGesture(settings.HistoryHotkey, settings.StopHotkey))
        {
            settings.HistoryHotkey = AreSameGesture(HotkeyGesture.DefaultHistory, settings.DictationHotkey) ||
                AreSameGesture(HotkeyGesture.DefaultHistory, settings.StopHotkey)
                    ? new HotkeyGesture { KeyCode = SharpHook.Data.KeyCode.VcH, Ctrl = true, Shift = false, Alt = true }
                    : HotkeyGesture.DefaultHistory;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.VoiceDictationPhrase))
        {
            settings.VoiceDictationPhrase = AppSettings.DefaultVoiceDictationPhrase;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.VoiceStopPhrase) ||
            VoicePhrasesMatch(settings.VoiceStopPhrase, settings.VoiceDictationPhrase))
        {
            settings.VoiceStopPhrase = GetNonConflictingDefaultVoicePhrase(
                AppSettings.DefaultVoiceStopPhrase,
                settings.VoiceDictationPhrase);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.VoiceHistoryPhrase) ||
            VoicePhrasesMatch(settings.VoiceHistoryPhrase, settings.VoiceDictationPhrase) ||
            VoicePhrasesMatch(settings.VoiceHistoryPhrase, settings.VoiceStopPhrase))
        {
            settings.VoiceHistoryPhrase = GetNonConflictingDefaultVoicePhrase(
                AppSettings.DefaultVoiceHistoryPhrase,
                settings.VoiceDictationPhrase,
                settings.VoiceStopPhrase);
            changed = true;
        }

        return changed;
    }

    private static bool VoicePhrasesMatch(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string GetNonConflictingDefaultVoicePhrase(string defaultPhrase, params string?[] existingPhrases) =>
        existingPhrases.Any(existingPhrase => VoicePhrasesMatch(defaultPhrase, existingPhrase))
            ? string.Empty
            : defaultPhrase;

    private static bool AreSameGesture(HotkeyGesture left, HotkeyGesture right) =>
        left.KeyCode == right.KeyCode &&
        left.Ctrl == right.Ctrl &&
        left.Shift == right.Shift &&
        left.Alt == right.Alt;

    protected override async void OnExit(ExitEventArgs e)
    {
        if (this.hotkeyListener is not null)
        {
            this.hotkeyListener.Dispose();
        }

        if (this.hookTask is not null)
        {
            await StopHookAsync(this.hookTask).ConfigureAwait(false);
        }

        if (this.dictationController is not null)
        {
            this.dictationController.RecordingStateChanged -= this.OnRecordingStateChanged;
            this.dictationController.ProcessingStateChanged -= this.OnProcessingStateChanged;
            this.dictationController.ThreadStarted -= this.OnThreadStarted;
            this.dictationController.ThreadCompleted -= this.OnThreadCompleted;
            this.dictationController.ThreadTranscriptUpdated -= this.OnThreadTranscriptUpdated;
            this.dictationController.TranscriptCommitted -= this.OnTranscriptCommitted;
            this.dictationController.AudioLevelUpdated -= this.OnAudioLevelUpdated;
            this.dictationController.HistoryRequested -= this.OnHistoryRequested;
            await this.dictationController.DisposeAsync().ConfigureAwait(false);
        }

        AppLog.EntryWritten -= this.OnAppLogEntryWritten;

        this.updateService?.Dispose();
        this.updateService = null;

        if (this.notifyIcon is not null)
        {
            this.notifyIcon.Visible = false;
            this.notifyIcon.Dispose();
        }

        if (this.transcriptionOverlayWindow is not null)
        {
            this.transcriptionOverlayWindow.Close();
            this.transcriptionOverlayWindow = null;
        }

        if (this.historyWindow is not null)
        {
            this.historyWindow.Close();
            this.historyWindow = null;
        }

        this.errorStateTimer.Stop();
        this.errorStateTimer.Tick -= this.OnErrorStateTimerTick;
        this.trayReadyIcon.Dispose();
        this.trayRecordingIcon.Dispose();
        this.trayProcessingIcon.Dispose();
        this.trayErrorIcon.Dispose();
        this.appIcon.Dispose();

        base.OnExit(e);
    }

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Workspace", null, (_, _) => this.ShowWorkspaceWindow());
        menu.Items.Add("Settings", null, (_, _) => this.ShowSettingsWindow(isFirstRun: false));
        menu.Items.Add("History", null, (_, _) => this.ShowHistoryWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        this.checkForUpdatesMenuItem = new Forms.ToolStripMenuItem("Check for updates", null, async (_, _) => await this.CheckForUpdatesAsync(showUpToDateMessage: true));
        menu.Items.Add(this.checkForUpdatesMenuItem);
        menu.Items.Add("Exit", null, (_, _) => this.Shutdown());

        var icon = new Forms.NotifyIcon
        {
            Icon = this.trayReadyIcon,
            Text = "PrimeDictate - Idle",
            Visible = true,
            ContextMenuStrip = menu
        };

        icon.Click += (_, _) =>
        {
            if (this.settings?.TrayClickBehavior == TrayClickBehavior.SingleClickOpensSettings)
            {
                this.ShowWorkspaceWindow();
            }
        };
        icon.DoubleClick += (_, _) =>
        {
            if (this.settings?.TrayClickBehavior == TrayClickBehavior.DoubleClickOpensSettings)
            {
                this.ShowWorkspaceWindow();
            }
        };

        return icon;
    }

    internal void ShowSettings() => this.ShowSettingsWindow(false);

    internal void ShowHistory() => this.ShowHistoryWindow();

    private void QueueAutomaticUpdateCheck()
    {
        if (this.settings?.CheckForUpdatesAutomatically != true ||
            this.settings.FirstRunCompleted != true)
        {
            return;
        }

        if (this.settings.LastUpdateCheckUtc is { } lastCheckUtc &&
            DateTime.UtcNow - DateTime.SpecifyKind(lastCheckUtc, DateTimeKind.Utc) < TimeSpan.FromHours(24))
        {
            return;
        }

        _ = this.CheckForUpdatesAfterStartupAsync();
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await this.Dispatcher.InvokeAsync(() =>
        {
            _ = this.CheckForUpdatesAsync(showUpToDateMessage: false);
        });
    }

    private async Task CheckForUpdatesAsync(bool showUpToDateMessage)
    {
        if (this.updateService is null || this.settings is null || this.settingsStore is null)
        {
            return;
        }

        if (this.isCheckingForUpdates || this.isInstallingUpdate)
        {
            if (showUpToDateMessage)
            {
                System.Windows.MessageBox.Show(
                    "PrimeDictate is already checking for or installing an update.",
                    "Update in progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        this.isCheckingForUpdates = true;
        this.SetCheckForUpdatesMenuState("Checking for updates...", enabled: false);
        AppLog.Info("Checking GitHub Releases for PrimeDictate updates...");

        try
        {
            var update = await this.updateService.CheckForUpdateAsync(CancellationToken.None);
            this.settings.LastUpdateCheckUtc = DateTime.UtcNow;
            this.settingsStore.Save(this.settings);

            if (update is null)
            {
                AppLog.Info($"PrimeDictate is up to date ({GitHubUpdateService.CurrentApplicationVersionText}).");
                if (showUpToDateMessage)
                {
                    System.Windows.MessageBox.Show(
                        $"PrimeDictate is up to date.\n\nInstalled version: {GitHubUpdateService.CurrentApplicationVersionText}",
                        "No update available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            AppLog.Info($"PrimeDictate {update.DisplayVersion} is available from GitHub Releases.");
            await this.PromptForUpdateAsync(update);
        }
        catch (Exception ex)
        {
            var message = $"Update check failed: {ex.Message}";
            if (showUpToDateMessage)
            {
                AppLog.Error(message);
                System.Windows.MessageBox.Show(
                    message,
                    "Update check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                AppLog.Info(message);
            }
        }
        finally
        {
            this.isCheckingForUpdates = false;
            if (!this.isInstallingUpdate)
            {
                this.SetCheckForUpdatesMenuState("Check for updates", enabled: true);
            }
        }
    }

    private async Task PromptForUpdateAsync(AppUpdateInfo update)
    {
        var releaseDateText = update.PublishedAt is { } publishedAt
            ? $"{Environment.NewLine}Published: {publishedAt.LocalDateTime:g}"
            : string.Empty;
        var message =
            $"PrimeDictate {update.DisplayVersion} is available.{Environment.NewLine}{Environment.NewLine}" +
            $"Installed version: {GitHubUpdateService.CurrentApplicationVersionText}{Environment.NewLine}" +
            $"Installer: {update.InstallerAssetName}{releaseDateText}{Environment.NewLine}{Environment.NewLine}" +
            "Download, verify, and start the MSI installer now? PrimeDictate will close before Windows Installer starts.";

        var result = System.Windows.MessageBox.Show(
            message,
            "PrimeDictate update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes)
        {
            AppLog.Info($"PrimeDictate {update.DisplayVersion} update deferred by the user.");
            return;
        }

        await this.DownloadAndInstallUpdateAsync(update);
    }

    private async Task DownloadAndInstallUpdateAsync(AppUpdateInfo update)
    {
        if (this.updateService is null)
        {
            return;
        }

        this.isInstallingUpdate = true;
        this.SetCheckForUpdatesMenuState("Downloading update...", enabled: false);
        this.notifyIcon?.ShowBalloonTip(
            5000,
            "PrimeDictate update",
            $"Downloading {update.InstallerAssetName}...",
            Forms.ToolTipIcon.Info);

        var progress = new Progress<UpdateDownloadProgress>(downloadProgress =>
        {
            var text = downloadProgress.Percent is { } percent
                ? $"Downloading update... {percent}%"
                : "Downloading update...";
            this.SetCheckForUpdatesMenuState(text, enabled: false);
        });

        try
        {
            var installerPath = await this.updateService.DownloadAndVerifyInstallerAsync(update, progress, CancellationToken.None);
            AppLog.Info($"Downloaded and verified PrimeDictate update installer: {installerPath}");
            GitHubUpdateService.StartInstaller(installerPath, this.settings);
            AppLog.Info("PrimeDictate update installer handoff started.");
            this.notifyIcon?.ShowBalloonTip(
                3000,
                "PrimeDictate update",
                "PrimeDictate will close, then Windows Installer will start.",
                Forms.ToolTipIcon.Info);
            this.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppLog.Info("PrimeDictate update canceled before Windows Installer started.");
            System.Windows.MessageBox.Show(
                "The update was canceled before Windows Installer started.",
                "Update canceled",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update installation failed: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"The update could not be installed: {ex.Message}",
                "Update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            this.isInstallingUpdate = false;
            if (!this.isCheckingForUpdates)
            {
                this.SetCheckForUpdatesMenuState("Check for updates", enabled: true);
            }
        }
    }

    private void SetCheckForUpdatesMenuState(string text, bool enabled)
    {
        if (this.checkForUpdatesMenuItem is null)
        {
            return;
        }

        this.checkForUpdatesMenuItem.Text = text;
        this.checkForUpdatesMenuItem.Enabled = enabled;
    }

    private Task ShowHistoryFromHotkeyAsync()
    {
        this.Dispatcher.Invoke(this.ShowHistoryWindow);
        return Task.CompletedTask;
    }

    private void ShowSettingsWindow(bool isFirstRun)
    {
        if (this.settings is null)
        {
            return;
        }

        if (this.settingsWindow is { IsLoaded: true })
        {
            this.settingsWindow.Activate();
            return;
        }

        this.settingsWindow = new SettingsWindow(this.settings, isFirstRun, this.stats);
        this.settingsWindow.Icon = this.CreateWindowIcon();
        this.settingsWindow.SettingsSaved += this.OnSettingsSaved;
        this.settingsWindow.HistoryRequested += this.OnHistoryRequested;
        this.settingsWindow.Closed += (_, _) =>
        {
            if (this.settingsWindow is not null)
            {
                this.settingsWindow.HistoryRequested -= this.OnHistoryRequested;
            }

            this.settingsWindow = null;
        };
        this.settingsWindow.Show();
        this.settingsWindow.Activate();
    }

    private void OnHistoryRequested()
    {
        if (this.Dispatcher.CheckAccess())
        {
            this.ShowHistoryWindow();
            return;
        }

        this.Dispatcher.Invoke(this.ShowHistoryWindow);
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        if (this.settingsStore is null || this.hotkeyListener is null)
        {
            return;
        }

        this.overlayExpandedFromCompact = false;
        var shouldQueueFirstSavedUpdateCheck = this.settings?.FirstRunCompleted != true &&
            newSettings.FirstRunCompleted &&
            newSettings.CheckForUpdatesAutomatically;
        NormalizeShortcutSettings(newSettings);
        newSettings.LastUpdateCheckUtc = this.settings?.LastUpdateCheckUtc ?? newSettings.LastUpdateCheckUtc;
        this.settings = newSettings;
        this.settingsStore.Save(newSettings);
        this.UpdateRuntimeStatusUi();
        this.hotkeyListener.UpdateHotkeys(
            newSettings.DictationHotkey,
            newSettings.StopHotkey,
            newSettings.HistoryHotkey);
        this.dictationController?.UpdateCaptureOptions(
            newSettings.ExclusiveMicAccessWhileDictating,
            newSettings.SelectedInputDeviceId,
            newSettings.InputGainMultiplier,
            TimeSpan.FromSeconds(newSettings.AutoCommitSilenceSeconds),
            newSettings.SendEnterAfterCommit,
            newSettings.ReturnToStartTargetOnCommit,
            newSettings.TranscriptionBackend,
            newSettings.TranscriptionComputeInterface,
            newSettings.SelectedModelId,
            newSettings.ModelPath,
            newSettings.EnableOllamaPostProcessing,
            newSettings.OllamaEndpoint,
            newSettings.OllamaModel,
            newSettings.OllamaMode,
            newSettings.EnableVoiceCommands,
            newSettings.VoiceDictationPhrase,
            newSettings.VoiceStopPhrase,
            newSettings.VoiceHistoryPhrase,
            newSettings.VoiceShellCommands ?? new List<VoiceShellCommand>(),
            newSettings.TranscriptReplacements ?? new List<TranscriptReplacementRule>());
        this.UpdateTranscriptionOverlay();

        if (shouldQueueFirstSavedUpdateCheck)
        {
            this.QueueAutomaticUpdateCheck();
        }
    }

    private void OnRecordingStateChanged(bool isRecording)
    {
        this.Dispatcher.Invoke(() =>
        {
            this.isRecording = isRecording;
            this.UpdateTrayState();
            this.UpdateTranscriptionOverlay();
        });

        if (isRecording)
        {
            if (this.settings?.PlayAudioCues == true)
            {
                this.audioCuePlayer.Play(DictationAudioCueKind.Start);
            }

            PulseMousePointerCueSoon();
        }
    }

    private void OnProcessingStateChanged(bool isProcessing)
    {
        this.Dispatcher.Invoke(() =>
        {
            this.isProcessing = isProcessing;
            this.UpdateTrayState();
            this.UpdateTranscriptionOverlay();
        });

        if (isProcessing)
        {
            if (this.settings?.PlayAudioCues == true)
            {
                this.audioCuePlayer.Play(DictationAudioCueKind.Stop);
            }

            PulseMousePointerCueSoon();
        }
    }

    private void UpdateTrayState()
    {
        if (this.notifyIcon is not null)
        {
            var trayState = this.GetTrayState();
            this.notifyIcon.Icon = trayState switch
            {
                TrayVisualState.Recording => this.trayRecordingIcon,
                TrayVisualState.Processing => this.trayProcessingIcon,
                TrayVisualState.Error => this.trayErrorIcon,
                _ => this.trayReadyIcon
            };

            this.notifyIcon.Text = trayState switch
            {
                TrayVisualState.Recording => this.GetRecordingTooltipText(),
                TrayVisualState.Processing => $"PrimeDictate - Processing [{this.GetActiveBackendLabel()}]",
                TrayVisualState.Error => "PrimeDictate - Error",
                _ => $"PrimeDictate - Ready [{this.GetActiveBackendLabel()}]"
            };
        }
    }

    private string GetRecordingTooltipText()
    {
        var backendLabel = this.GetActiveBackendLabel();
        var mode = this.dictationController?.ActiveMicAccessModeLabel ?? "Unknown";
        return mode switch
        {
            "Exclusive" => $"PrimeDictate - Listening [{backendLabel}, Exclusive]",
            "Shared" => $"PrimeDictate - Listening [{backendLabel}, Shared]",
            _ => $"PrimeDictate - Listening [{backendLabel}]"
        };
    }

    private void ShowWorkspaceWindow()
    {
        if (this.workspaceWindow is { IsLoaded: true })
        {
            this.workspaceWindow.Activate();
            return;
        }

        this.workspaceWindow = new MainWindow(this.workspaceViewModel);
        this.workspaceWindow.Icon = this.CreateWindowIcon();
        this.workspaceWindow.Closed += (_, _) => this.workspaceWindow = null;
        this.workspaceWindow.Show();
        this.workspaceWindow.Activate();
    }

    private void ShowHistoryWindow()
    {
        if (this.historyWindow is { IsLoaded: true })
        {
            this.historyWindow.Activate();
            return;
        }

        this.historyWindow = new HistoryWindow(this.historyViewModel);
        this.historyWindow.Icon = this.CreateWindowIcon();
        this.historyWindow.Closed += (_, _) => this.historyWindow = null;
        this.historyWindow.Show();
        this.historyWindow.Activate();
    }

    private void ShowTranscriptionOverlay()
    {
        if (this.settings is null)
        {
            return;
        }

        if (this.transcriptionOverlayWindow is null)
        {
            this.transcriptionOverlayWindow = new TranscriptionOverlayWindow();
            this.transcriptionOverlayWindow.Closed += (_, _) => this.transcriptionOverlayWindow = null;
            this.transcriptionOverlayWindow.Show();
        }
        else if (!this.transcriptionOverlayWindow.IsVisible)
        {
            this.transcriptionOverlayWindow.Show();
        }

        this.ApplyOverlayPreferences();
        this.UpdateTranscriptionOverlay();
    }

    private void ApplyOverlayPreferences()
    {
        if (this.transcriptionOverlayWindow is null || this.settings is null)
        {
            return;
        }

        this.transcriptionOverlayWindow.SetSticky(this.settings.IsOverlaySticky);
        this.transcriptionOverlayWindow.SetOverlayMode(this.GetEffectiveOverlayMode());
    }

    internal void ExpandOverlayPanel()
    {
        if (this.settings?.OverlayMode != OverlayMode.CompactMicrophone)
        {
            return;
        }

        this.overlayExpandedFromCompact = true;
        this.ShowTranscriptionOverlay();
        this.transcriptionOverlayWindow?.PositionFullPanelInLowerCenter();
    }

    internal void CollapseOverlayPanel()
    {
        if (this.settings?.OverlayMode != OverlayMode.CompactMicrophone)
        {
            return;
        }

        if (this.settings.IsOverlaySticky)
        {
            return;
        }

        this.overlayExpandedFromCompact = false;
        this.ApplyOverlayPreferences();
        this.UpdateTranscriptionOverlay();
    }

    internal void SaveStickyState(bool isSticky)
    {
        if (this.settings is not null && this.settingsStore is not null)
        {
            this.settings.IsOverlaySticky = isSticky;
            this.settingsStore.Save(this.settings);
        }
    }

    private void HideTranscriptionOverlay()
    {
        if (this.transcriptionOverlayWindow is null)
        {
            return;
        }

        if (this.ShouldPersistOverlay())
        {
            return;
        }

        this.transcriptionOverlayWindow.Close();
        this.transcriptionOverlayWindow = null;
        this.overlayTranscript = string.Empty;
        this.overlayExpandedFromCompact = false;
    }

    private void UpdateTranscriptionOverlay()
    {
        if (this.transcriptionOverlayWindow is null || this.settings is null)
        {
            return;
        }

        this.ApplyOverlayPreferences();

        if (!this.isRecording && !this.isProcessing)
        {
            this.transcriptionOverlayWindow.SetReadyState(this.GetActiveBackendLabel());
            return;
        }

        this.transcriptionOverlayWindow.UpdateTranscript(this.overlayTranscript, this.isProcessing, this.GetActiveBackendLabel());
    }

    private void OnAppLogEntryWritten(AppLogEntry entry)
    {
        this.Dispatcher.Invoke(() =>
        {
            this.workspaceViewModel.AppendEntry(entry);
            if (entry.Level == AppLogLevel.Error)
            {
                this.errorStateUntilUtc = DateTime.UtcNow.AddSeconds(10);
                if (!this.errorStateTimer.IsEnabled)
                {
                    this.errorStateTimer.Start();
                }
            }

            this.UpdateTrayState();
        });
    }

    private void OnThreadStarted(Guid threadId)
    {
        this.Dispatcher.Invoke(() =>
        {
            this.overlayTranscript = string.Empty;
            this.workspaceViewModel.StartThread(threadId);
            this.ShowTranscriptionOverlay();
        });
    }

    private void OnThreadCompleted(Guid threadId)
    {
        this.Dispatcher.Invoke(() =>
        {
            this.workspaceViewModel.MarkThreadCompleted(threadId);

            if (this.ShouldPersistOverlay())
            {
                this.overlayTranscript = string.Empty;
                this.isProcessing = false;
                this.overlayExpandedFromCompact = false;
                this.UpdateTranscriptionOverlay();
            }
            else
            {
                this.HideTranscriptionOverlay();
            }
        });
    }

    private void OnAudioLevelUpdated(double rms)
    {
        this.transcriptionOverlayWindow?.UpdateAudioLevel(rms);
    }

    private void OnThreadTranscriptUpdated(Guid threadId, string transcript)
    {
        this.Dispatcher.Invoke(() =>
        {
            if (this.workspaceViewModel.GetThread(threadId) is { } thread)
            {
                thread.LatestTranscript = transcript;
            }

            this.overlayTranscript = transcript;
            this.UpdateTranscriptionOverlay();
        });
    }

    private void OnTranscriptCommitted(TranscriptCommittedEvent commit)
    {
        var entry = new TranscriptHistoryEntry(
            Id: Guid.NewGuid(),
            ThreadId: commit.ThreadId,
            TimestampUtc: commit.TimestampUtc,
            Transcript: commit.Transcript,
            DeliveryStatus: commit.DeliveryStatus,
            TargetDisplayName: commit.TargetDisplayName,
            TargetAppName: commit.TargetAppName,
            TargetWindowTitle: commit.TargetWindowTitle,
            Error: commit.Error,
            AudioDurationSeconds: commit.AudioDuration.TotalSeconds,
            SendEnterAfterCommit: commit.SendEnterAfterCommit,
            OriginalTranscript: commit.OriginalTranscript,
            OllamaSystemPrompt: commit.OllamaSystemPrompt);

        this.historyStore?.Append(entry);
        var statsUpdate = this.statsStore?.RecordCommit(entry);
        if (statsUpdate is not null)
        {
            this.stats = statsUpdate.State;
        }

        this.Dispatcher.Invoke(() =>
        {
            this.historyViewModel.Add(entry);
            if (statsUpdate?.NewAchievements.Count > 0)
            {
                this.ShowAchievementNotification(statsUpdate.NewAchievements[^1]);
            }
        });
    }

    private void ShowAchievementNotification(DictationAchievement achievement)
    {
        this.notifyIcon?.ShowBalloonTip(
            5000,
            achievement.Title,
            achievement.Message,
            Forms.ToolTipIcon.Info);
    }

    private void UpdateRuntimeStatusUi()
    {
        if (this.settings is null)
        {
            return;
        }

        this.workspaceViewModel.SetRuntimeStatus(this.GetActiveBackendLabel(), this.GetActiveModelLabel());
        this.UpdateTrayState();
        if (this.ShouldPersistOverlay())
        {
            this.ShowTranscriptionOverlay();
        }
        else if (this.transcriptionOverlayWindow is not null)
        {
            this.UpdateTranscriptionOverlay();
        }
    }

    private bool ShouldPersistOverlay() =>
        this.settings is not null &&
        (this.settings.IsOverlaySticky || this.settings.OverlayMode == OverlayMode.CompactMicrophone);

    private OverlayMode GetEffectiveOverlayMode() =>
        this.overlayExpandedFromCompact ? OverlayMode.FullPanel : this.settings?.OverlayMode ?? OverlayMode.CompactMicrophone;

    private string GetActiveBackendLabel() => this.settings?.TranscriptionBackend switch
    {
        TranscriptionBackendKind.QualcommQnn => "Qualcomm AI Hub Whisper QNN",
        TranscriptionBackendKind.Moonshine => "Moonshine ONNX",
        TranscriptionBackendKind.Parakeet => "Parakeet ONNX",
        TranscriptionBackendKind.WhisperNet => "Whisper.net (GGML)",
        _ => "Whisper ONNX"
    };

    private string GetActiveModelLabel()
    {
        if (this.settings is null)
        {
            return "Default";
        }

        return this.settings.TranscriptionBackend switch
        {
            TranscriptionBackendKind.QualcommQnn => GetQualcommAihubWhisperModelLabel(this.settings),
            TranscriptionBackendKind.Moonshine => GetMoonshineModelLabel(this.settings),
            TranscriptionBackendKind.Parakeet => GetParakeetModelLabel(this.settings),
            TranscriptionBackendKind.WhisperNet => GetWhisperNetModelLabel(this.settings),
            _ => GetWhisperModelLabel(this.settings)
        };
    }

    private static string GetWhisperModelLabel(AppSettings settings)
    {
        if (WhisperModelCatalog.TryGetById(settings.SelectedModelId, out var option))
        {
            return option.DisplayName;
        }

        var optionFromPath = WhisperModelCatalog.TryGetByPath(settings.ModelPath);
        if (optionFromPath is not null)
        {
            return optionFromPath.DisplayName;
        }

        return string.IsNullOrWhiteSpace(settings.ModelPath)
            ? "Default"
            : Path.GetFileName(settings.ModelPath);
    }

    private static string GetParakeetModelLabel(AppSettings settings)
    {
        if (ParakeetModelCatalog.TryGetById(settings.SelectedModelId, out var option))
        {
            return option.DisplayName;
        }

        var optionFromPath = ParakeetModelCatalog.TryGetByPath(settings.ModelPath);
        if (optionFromPath is not null)
        {
            return optionFromPath.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(settings.ModelPath))
        {
            return "Default";
        }

        return Path.GetFileName(settings.ModelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string GetMoonshineModelLabel(AppSettings settings)
    {
        if (MoonshineModelCatalog.TryGetById(settings.SelectedModelId, out var option))
        {
            return option.DisplayName;
        }

        var optionFromPath = MoonshineModelCatalog.TryGetByPath(settings.ModelPath);
        if (optionFromPath is not null)
        {
            return optionFromPath.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(settings.ModelPath))
        {
            return "Default";
        }

        return Path.GetFileName(settings.ModelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string GetQualcommAihubWhisperModelLabel(AppSettings settings)
    {
        if (QualcommAihubWhisperModelCatalog.TryGetById(settings.SelectedModelId, out var option))
        {
            return option.DisplayName;
        }

        var optionFromPath = QualcommAihubWhisperModelCatalog.TryGetByPath(settings.ModelPath);
        if (optionFromPath is not null)
        {
            return optionFromPath.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(settings.ModelPath))
        {
            return "Auto";
        }

        return Path.GetFileName(settings.ModelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string GetWhisperNetModelLabel(AppSettings settings)
    {
        if (WhisperNetModelCatalog.TryGetById(settings.SelectedModelId, out var option))
        {
            return option.DisplayName;
        }

        var optionFromPath = WhisperNetModelCatalog.TryGetByPath(settings.ModelPath);
        if (optionFromPath is not null)
        {
            return optionFromPath.DisplayName;
        }

        return string.IsNullOrWhiteSpace(settings.ModelPath)
            ? "Default"
            : Path.GetFileName(settings.ModelPath);
    }

    private BitmapSource CreateWindowIcon() =>
        Imaging.CreateBitmapSourceFromHIcon(
            this.appIcon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

    private TrayVisualState GetTrayState()
    {
        if (this.isRecording)
        {
            return TrayVisualState.Recording;
        }

        if (this.isProcessing)
        {
            return TrayVisualState.Processing;
        }

        if (DateTime.UtcNow <= this.errorStateUntilUtc)
        {
            return TrayVisualState.Error;
        }

        return TrayVisualState.Ready;
    }

    private void OnErrorStateTimerTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow > this.errorStateUntilUtc)
        {
            this.errorStateTimer.Stop();
            this.UpdateTrayState();
        }
    }

    private static void PulseMousePointerCueSoon()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            try
            {
                WindowsMousePointerIndicator.PulseIfMouseSonarEnabled();
            }
            catch (Exception ex)
            {
                AppLog.Info($"Windows Mouse Sonar cue unavailable: {ex.Message}");
            }
        });
    }

    private static async Task StopHookAsync(Task hookTask)
    {
        try
        {
            await hookTask.ConfigureAwait(false);
        }
        catch (HookException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
