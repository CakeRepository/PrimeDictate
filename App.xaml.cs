using System.Drawing;
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
    private readonly DispatcherTimer errorStateTimer;
    private Forms.NotifyIcon? notifyIcon;
    private DictationController? dictationController;
    private GlobalHotkeyListener? hotkeyListener;
    private SettingsStore? settingsStore;
    private TranscriptionHistoryStore? historyStore;
    private AppSettings? settings;
    private Task? hookTask;
    private SettingsWindow? settingsWindow;
    private MainWindow? workspaceWindow;
    private HistoryWindow? historyWindow;
    private TranscriptionOverlayWindow? transcriptionOverlayWindow;
    private readonly DictationWorkspaceViewModel workspaceViewModel = new();
    private readonly TranscriptionHistoryViewModel historyViewModel = new();
    private string overlayTranscript = string.Empty;
    private bool isRecording;
    private bool isProcessing;
    private DateTime errorStateUntilUtc = DateTime.MinValue;

    public App()
    {
        this.errorStateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        this.errorStateTimer.Tick += this.OnErrorStateTimerTick;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        this.settingsStore = new SettingsStore();
        this.historyStore = new TranscriptionHistoryStore();
        this.settings = this.settingsStore.LoadOrDefault();
        this.historyViewModel.Load(this.historyStore.Load());
        this.ApplyModelPathOverride(this.settings);

        var configured = this.settings.DictationHotkey.IsValid(out _)
            ? this.settings.DictationHotkey
            : HotkeyGesture.Default;
        this.settings.DictationHotkey = configured;

        this.dictationController = new DictationController(
            this.settings.ExclusiveMicAccessWhileDictating,
            TimeSpan.FromSeconds(this.settings.AutoCommitSilenceSeconds),
            this.settings.SendEnterAfterCommit);
        this.dictationController.RecordingStateChanged += this.OnRecordingStateChanged;
        this.dictationController.ProcessingStateChanged += this.OnProcessingStateChanged;
        this.dictationController.ThreadStarted += this.OnThreadStarted;
        this.dictationController.ThreadCompleted += this.OnThreadCompleted;
        this.dictationController.ThreadTranscriptUpdated += this.OnThreadTranscriptUpdated;
        this.dictationController.TranscriptCommitted += this.OnTranscriptCommitted;
        this.dictationController.AudioLevelUpdated += this.OnAudioLevelUpdated;
        this.hotkeyListener = new GlobalHotkeyListener(this.dictationController.ToggleRecordingAsync, configured);
        this.hookTask = this.hotkeyListener.RunAsync();
        AppLog.EntryWritten += this.OnAppLogEntryWritten;

        this.notifyIcon = this.CreateNotifyIcon();
        this.UpdateTrayState();

        if (!this.settings.FirstRunCompleted)
        {
            this.ShowSettingsWindow(isFirstRun: true);
        }
    }

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
            await this.dictationController.DisposeAsync().ConfigureAwait(false);
        }

        AppLog.EntryWritten -= this.OnAppLogEntryWritten;

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

        this.settingsWindow = new SettingsWindow(this.settings, isFirstRun);
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
        this.ShowHistoryWindow();
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        if (this.settingsStore is null || this.hotkeyListener is null)
        {
            return;
        }

        this.settings = newSettings;
        this.settingsStore.Save(newSettings);
        this.hotkeyListener.UpdateHotkey(newSettings.DictationHotkey);
        this.dictationController?.UpdateCaptureOptions(
            newSettings.ExclusiveMicAccessWhileDictating,
            TimeSpan.FromSeconds(newSettings.AutoCommitSilenceSeconds),
            newSettings.SendEnterAfterCommit);
        this.ApplyModelPathOverride(newSettings);
        this.UpdateTranscriptionOverlay();
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
                TrayVisualState.Processing => "PrimeDictate - Processing transcript",
                TrayVisualState.Error => "PrimeDictate - Error",
                _ => "PrimeDictate - Ready"
            };
        }
    }

    private string GetRecordingTooltipText()
    {
        var mode = this.dictationController?.ActiveMicAccessModeLabel ?? "Unknown";
        return mode switch
        {
            "Exclusive" => "PrimeDictate - Listening [Exclusive]",
            "Shared" => "PrimeDictate - Listening [Shared]",
            _ => "PrimeDictate - Listening"
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
            if (this.settings.IsOverlaySticky)
            {
                this.transcriptionOverlayWindow.SetSticky(true);
            }
            this.transcriptionOverlayWindow.Show();
        }

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

        if (this.settings?.IsOverlaySticky == true)
        {
            return;
        }

        this.transcriptionOverlayWindow.Close();
        this.transcriptionOverlayWindow = null;
        this.overlayTranscript = string.Empty;
    }

    private void UpdateTranscriptionOverlay()
    {
        if (this.transcriptionOverlayWindow is null || this.settings is null)
        {
            return;
        }

        if (!this.isRecording && !this.isProcessing)
        {
            this.transcriptionOverlayWindow.SetReadyState();
            return;
        }

        this.transcriptionOverlayWindow.UpdateTranscript(this.overlayTranscript, this.isProcessing);
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

            if (this.settings?.IsOverlaySticky == true)
            {
                this.overlayTranscript = string.Empty;
                this.isProcessing = false;
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
            Error: commit.Error,
            AudioDurationSeconds: commit.AudioDuration.TotalSeconds,
            SendEnterAfterCommit: commit.SendEnterAfterCommit);

        this.historyStore?.Append(entry);

        this.Dispatcher.Invoke(() =>
        {
            this.historyViewModel.Add(entry);
        });
    }

    private void ApplyModelPathOverride(AppSettings configuredSettings)
    {
        if (string.IsNullOrWhiteSpace(configuredSettings.ModelPath))
        {
            Environment.SetEnvironmentVariable("PRIME_DICTATE_MODEL", null);
            return;
        }

        Environment.SetEnvironmentVariable("PRIME_DICTATE_MODEL", configuredSettings.ModelPath);
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
