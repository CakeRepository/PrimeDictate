using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using SharpHook.Data;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace PrimeDictate;

internal partial class SettingsWindow : Window
{
    private enum HotkeyCaptureTarget
    {
        Dictation,
        Stop,
        History
    }

    private sealed record BackendChoice(TranscriptionBackendKind Kind, string Label, string Description);
    private sealed record InputDeviceOption(string? DeviceId, string Label);
    private sealed record StatsDayBar(string Label, long Words, double BarHeight, MediaBrush Fill);
    private sealed record AchievementDisplay(string Title, string Detail, string Status, MediaBrush Accent);

    private static readonly IReadOnlyList<BackendChoice> AllBackendChoices =
    [
        new(
            TranscriptionBackendKind.Whisper,
            "Whisper ONNX",
            "Portable Whisper models through sherpa-onnx. Use the same ONNX model folder across supported CPU runtimes and future hardware providers."),
        new(
            TranscriptionBackendKind.Parakeet,
            "Parakeet ONNX",
            "A newer ONNX backend for fully local English transcription. Useful for testing non-Whisper accuracy and speed in the same workflow."),
        new(
            TranscriptionBackendKind.Moonshine,
            "Moonshine ONNX",
            "A compact ONNX backend that favors lightweight local English transcription and fast turnaround."),
        new(
            TranscriptionBackendKind.WhisperNet,
            "Whisper.net (GGML)",
            "Native Whisper.net GGML models with GPU support through CUDA/Vulkan, optional OpenVINO NPU sidecars, and access to larger V3 models."),
        new(
            TranscriptionBackendKind.QualcommQnn,
            "Qualcomm AI Hub Whisper QNN",
            "Experimental Qualcomm AI Hub Whisper backend driven by ONNX Runtime QNN on native Windows ARM64. Uses precompiled EPContext ONNX wrappers around QNN context binaries and disables CPU fallback for NPU sessions.")
    ];

    private bool isCapturingHotkey;
    private bool suppressModelChoiceChanged;
    private bool suppressBackendChoiceChanged;
    private bool suppressComputeChoiceChanged;
    private bool suppressModelPathTextChanged;
    private HotkeyCaptureTarget? hotkeyCaptureTarget;
    private HotkeyGesture currentDictationHotkey;
    private HotkeyGesture currentStopHotkey;
    private HotkeyGesture currentHistoryHotkey;
    private readonly bool isFirstRun;
    private readonly bool isOverlaySticky;
    private readonly IReadOnlyList<BackendChoice> availableBackendChoices;
    private CancellationTokenSource? modelDownloadCts;
    private TranscriptionBackendKind currentBackend;
    private readonly ObservableCollection<VoiceShellCommand> voiceShellCommands = new();
    private readonly ObservableCollection<TranscriptReplacementRule> transcriptReplacementRules = new();

    internal SettingsWindow(AppSettings settings, bool isFirstRun, DictationStatsState? statsState = null)
    {
        InitializeComponent();
        this.isFirstRun = isFirstRun;
        this.isOverlaySticky = settings.IsOverlaySticky;
        this.currentDictationHotkey = settings.DictationHotkey;
        this.currentStopHotkey = settings.StopHotkey;
        this.currentHistoryHotkey = settings.HistoryHotkey;
        this.currentBackend = settings.TranscriptionBackend;
        this.availableBackendChoices = GetAvailableBackendChoices(settings.TranscriptionBackend);

        this.InputDeviceComboBox.DisplayMemberPath = nameof(InputDeviceOption.Label);
        this.ModelBackendComboBox.ItemsSource = this.availableBackendChoices;
        this.ModelBackendComboBox.DisplayMemberPath = nameof(BackendChoice.Label);
        this.ModelChoiceComboBox.DisplayMemberPath = nameof(WhisperModelOption.DisplayName);
        this.ComputeInterfaceComboBox.DisplayMemberPath = nameof(TranscriptionComputeChoice.Label);
        this.VoiceShellCommandBehaviorColumn.ItemsSource = Enum.GetValues<VoiceShellCommandCompletionBehavior>();

        this.UpdateHotkeyLabels();
        this.TrayBehaviorComboBox.SelectedIndex = settings.TrayClickBehavior == TrayClickBehavior.SingleClickOpensSettings ? 0 : 1;
        this.LaunchAtLoginComboBox.SelectedIndex = GetLaunchAtLoginComboBoxIndex(ResolveLaunchAtLoginScope(settings.LaunchAtLoginScope));
        this.ExclusiveMicAccessCheckBox.IsChecked = settings.ExclusiveMicAccessWhileDictating;
        this.InitializeInputDeviceOptions(settings.SelectedInputDeviceId);
        this.InputGainTextBox.Text = settings.InputGainMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
        this.AutoCommitSilenceSecondsTextBox.Text = settings.AutoCommitSilenceSeconds.ToString(CultureInfo.InvariantCulture);
        this.SendEnterAfterCommitCheckBox.IsChecked = settings.SendEnterAfterCommit;
        this.ReturnToStartTargetCheckBox.IsChecked = settings.ReturnToStartTargetOnCommit;
        this.PlayAudioCuesCheckBox.IsChecked = settings.PlayAudioCues;
        this.OverlayModeComboBox.SelectedIndex = settings.OverlayMode == OverlayMode.FullPanel ? 1 : 0;
        this.EnableVoiceCommandsCheckBox.IsChecked = settings.EnableVoiceCommands;
        this.VoiceStopPhraseTextBox.Text = settings.VoiceStopPhrase;
        this.VoiceHistoryPhraseTextBox.Text = settings.VoiceHistoryPhrase;
        foreach (var command in settings.VoiceShellCommands ?? [])
        {
            this.voiceShellCommands.Add(new VoiceShellCommand
            {
                Enabled = command.Enabled,
                Phrase = command.Phrase,
                CompletionBehavior = command.CompletionBehavior,
                Command = command.Command
            });
        }

        this.VoiceShellCommandsDataGrid.ItemsSource = this.voiceShellCommands;
        this.EnableOllamaCheckBox.IsChecked = settings.EnableOllamaPostProcessing;
        this.OllamaEndpointTextBox.Text = settings.OllamaEndpoint;
        this.OllamaModelTextBox.Text = settings.OllamaModel;
        
        for (int i = 0; i < this.OllamaModeComboBox.Items.Count; i++)
        {
            if (this.OllamaModeComboBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == settings.OllamaMode.ToString())
            {
                this.OllamaModeComboBox.SelectedIndex = i;
                break;
            }
        }
        if (this.OllamaModeComboBox.SelectedIndex == -1)
        {
            this.OllamaModeComboBox.SelectedIndex = 0;
        }

        foreach (var rule in settings.TranscriptReplacements ?? [])
        {
            this.transcriptReplacementRules.Add(new TranscriptReplacementRule
            {
                Find = rule.Find,
                Replace = rule.Replace
            });
        }

        this.ReplacementRulesDataGrid.ItemsSource = this.transcriptReplacementRules;
        this.InitializeStatsTab(settings, statsState);

        this.WelcomeTab.Header = isFirstRun ? "Welcome" : "Overview";
        this.HeaderText.Text = isFirstRun ? "PrimeDictate first-run setup" : "PrimeDictate settings";
        this.WelcomeFooterText.Text = isFirstRun
            ? "Choose a model, configure your commands, and PrimeDictate is ready to dictate into Windows apps."
            : "You can switch models or tweak dictation behavior here whenever your workflow changes.";
        this.ReplacementsTab.Visibility = isFirstRun ? Visibility.Collapsed : Visibility.Visible;
        this.ImpactTab.Visibility = isFirstRun ? Visibility.Collapsed : Visibility.Visible;
        this.HistoryButton.Visibility = isFirstRun ? Visibility.Collapsed : Visibility.Visible;
        this.CancelButton.Visibility = isFirstRun ? Visibility.Collapsed : Visibility.Visible;
        this.BackButton.Visibility = isFirstRun ? Visibility.Visible : Visibility.Collapsed;
        this.SetupTabControl.SelectedIndex = isFirstRun ? 0 : 1;

        this.InitializeModelSelection(settings);
        this.UpdateWindowChrome();
    }

    internal event Action<AppSettings>? SettingsSaved;
    internal event Action? HistoryRequested;

    protected override void OnClosed(EventArgs e)
    {
        this.modelDownloadCts?.Cancel();
        this.modelDownloadCts?.Dispose();
        this.modelDownloadCts = null;
        base.OnClosed(e);
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            this.DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void InitializeModelSelection(AppSettings settings)
    {
        var backendChoice = this.availableBackendChoices.FirstOrDefault(choice => choice.Kind == settings.TranscriptionBackend)
            ?? this.availableBackendChoices.First(choice => choice.Kind == TranscriptionBackendKind.Whisper);

        this.suppressBackendChoiceChanged = true;
        this.ModelBackendComboBox.SelectedItem = backendChoice;
        this.suppressBackendChoiceChanged = false;

        this.ApplyBackendSelection(
            backendChoice.Kind,
            settings.SelectedModelId,
            settings.ModelPath,
            settings.TranscriptionComputeInterface);
    }

    private void ApplyBackendSelection(
        TranscriptionBackendKind backend,
        string? preferredModelId,
        string? configuredModelPath,
        TranscriptionComputeInterface? preferredComputeInterface = null)
    {
        this.currentBackend = backend;
        object selectedOption = backend switch
        {
            TranscriptionBackendKind.Moonshine => SelectMoonshineModel(preferredModelId, configuredModelPath),
            TranscriptionBackendKind.Parakeet => SelectParakeetModel(preferredModelId, configuredModelPath),
            TranscriptionBackendKind.QualcommQnn => SelectQualcommAihubWhisperModel(preferredModelId, configuredModelPath),
            TranscriptionBackendKind.WhisperNet => SelectWhisperNetModel(preferredModelId, configuredModelPath),
            _ => SelectWhisperModel(preferredModelId, configuredModelPath)
        };

        this.suppressModelChoiceChanged = true;
        this.ModelChoiceComboBox.SelectedItem = null;
        this.ModelChoiceComboBox.ItemsSource = null;
        this.ModelChoiceComboBox.ItemsSource = backend switch
        {
            TranscriptionBackendKind.Moonshine => MoonshineModelCatalog.Options,
            TranscriptionBackendKind.Parakeet => ParakeetModelCatalog.Options,
            TranscriptionBackendKind.QualcommQnn => QualcommAihubWhisperModelCatalog.Options,
            TranscriptionBackendKind.WhisperNet => WhisperNetModelCatalog.Options,
            _ => WhisperModelCatalog.Options
        };
        this.ModelChoiceComboBox.SelectedItem = selectedOption;
        this.suppressModelChoiceChanged = false;

        var initialPath = backend switch
        {
            TranscriptionBackendKind.Moonshine => ResolveInitialMoonshinePath(selectedOption as MoonshineModelOption, configuredModelPath),
            TranscriptionBackendKind.Parakeet => ResolveInitialParakeetPath(selectedOption as ParakeetModelOption, configuredModelPath),
            TranscriptionBackendKind.QualcommQnn => ResolveInitialQualcommAihubWhisperPath(selectedOption as QualcommAihubWhisperModelOption, configuredModelPath),
            TranscriptionBackendKind.WhisperNet => ResolveInitialWhisperNetPath(selectedOption as WhisperNetModelOption, configuredModelPath),
            _ => ResolveInitialWhisperPath(selectedOption as WhisperModelOption, configuredModelPath)
        };

        this.SetModelPathText(initialPath);
        this.UpdateComputeInterfaceAvailability(preferredComputeInterface);
        this.UpdateBackendUi();
        this.UpdateModelSelectionUi();
    }

    private static WhisperModelOption SelectWhisperModel(string? preferredModelId, string? configuredModelPath)
    {
        var optionFromPath = WhisperModelCatalog.TryGetByPath(configuredModelPath);
        if (WhisperModelCatalog.TryGetById(preferredModelId, out var preferredOption))
        {
            return preferredOption;
        }

        return optionFromPath
            ?? WhisperModelCatalog.Options.FirstOrDefault(option => option.Recommended && WhisperModelCatalog.TryResolveInstalledPath(option, out _))
            ?? WhisperModelCatalog.Options.FirstOrDefault(option => WhisperModelCatalog.TryResolveInstalledPath(option, out _))
            ?? WhisperModelCatalog.Options.FirstOrDefault(option => option.Recommended)
            ?? WhisperModelCatalog.Options.First();
    }

    private static ParakeetModelOption SelectParakeetModel(string? preferredModelId, string? configuredModelPath)
    {
        var optionFromPath = ParakeetModelCatalog.TryGetByPath(configuredModelPath);
        if (ParakeetModelCatalog.TryGetById(preferredModelId, out var preferredOption))
        {
            return preferredOption;
        }

        return optionFromPath
            ?? ParakeetModelCatalog.Options.FirstOrDefault(option => option.Recommended && ParakeetModelCatalog.TryResolveInstalledPath(option, out _))
            ?? ParakeetModelCatalog.Options.FirstOrDefault(option => ParakeetModelCatalog.TryResolveInstalledPath(option, out _))
            ?? ParakeetModelCatalog.Options.FirstOrDefault(option => option.Recommended)
            ?? ParakeetModelCatalog.Options.First();
    }

    private static MoonshineModelOption SelectMoonshineModel(string? preferredModelId, string? configuredModelPath)
    {
        var optionFromPath = MoonshineModelCatalog.TryGetByPath(configuredModelPath);
        if (MoonshineModelCatalog.TryGetById(preferredModelId, out var preferredOption))
        {
            return preferredOption;
        }

        return optionFromPath
            ?? MoonshineModelCatalog.Options.FirstOrDefault(option => option.Recommended && MoonshineModelCatalog.TryResolveInstalledPath(option, out _))
            ?? MoonshineModelCatalog.Options.FirstOrDefault(option => MoonshineModelCatalog.TryResolveInstalledPath(option, out _))
            ?? MoonshineModelCatalog.Options.FirstOrDefault(option => option.Recommended)
            ?? MoonshineModelCatalog.Options.First();
    }

    private static QualcommAihubWhisperModelOption SelectQualcommAihubWhisperModel(string? preferredModelId, string? configuredModelPath)
    {
        var optionFromPath = QualcommAihubWhisperModelCatalog.TryGetByPath(configuredModelPath);
        if (QualcommAihubWhisperModelCatalog.TryGetById(preferredModelId, out var preferredOption))
        {
            return preferredOption;
        }

        return optionFromPath
            ?? QualcommAihubWhisperModelCatalog.Options.FirstOrDefault(option => option.Recommended && QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out _))
            ?? QualcommAihubWhisperModelCatalog.Options.FirstOrDefault(option => QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out _))
            ?? QualcommAihubWhisperModelCatalog.Options.FirstOrDefault(option => option.Recommended)
            ?? QualcommAihubWhisperModelCatalog.Options.First();
    }

    private static WhisperNetModelOption SelectWhisperNetModel(string? preferredModelId, string? configuredModelPath)
    {
        var optionFromPath = WhisperNetModelCatalog.TryGetByPath(configuredModelPath);
        if (WhisperNetModelCatalog.TryGetById(preferredModelId, out var preferredOption))
        {
            return preferredOption;
        }

        return optionFromPath
            ?? WhisperNetModelCatalog.Options.FirstOrDefault(option => option.Recommended && WhisperNetModelCatalog.TryResolveInstalledPath(option, out _))
            ?? WhisperNetModelCatalog.Options.FirstOrDefault(option => WhisperNetModelCatalog.TryResolveInstalledPath(option, out _))
            ?? WhisperNetModelCatalog.Options.FirstOrDefault(option => option.Recommended)
            ?? WhisperNetModelCatalog.Options.First();
    }

    private static string ResolveInitialWhisperPath(WhisperModelOption? option, string? configuredModelPath)
    {
        if (WhisperModelCatalog.TryResolveDirectory(configuredModelPath, out var resolvedConfiguredPath))
        {
            return resolvedConfiguredPath;
        }

        if (option is not null && WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            return installedPath;
        }

        return string.Empty;
    }

    private static string ResolveInitialParakeetPath(ParakeetModelOption? option, string? configuredModelPath)
    {
        if (ParakeetModelCatalog.TryResolveDirectory(configuredModelPath, out var resolvedConfiguredPath))
        {
            return resolvedConfiguredPath;
        }

        if (option is not null && ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            return installedPath;
        }

        return string.Empty;
    }

    private static string ResolveInitialMoonshinePath(MoonshineModelOption? option, string? configuredModelPath)
    {
        if (MoonshineModelCatalog.TryResolveDirectory(configuredModelPath, out var resolvedConfiguredPath))
        {
            return resolvedConfiguredPath;
        }

        if (option is not null && MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            return installedPath;
        }

        return string.Empty;
    }

    private static string ResolveInitialQualcommAihubWhisperPath(QualcommAihubWhisperModelOption? option, string? configuredModelPath)
    {
        if (QualcommAihubWhisperModelCatalog.TryResolveDirectory(configuredModelPath, out var resolvedConfiguredPath))
        {
            return resolvedConfiguredPath;
        }

        if (option is not null && QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            return installedPath;
        }

        return string.Empty;
    }

    private static string ResolveInitialWhisperNetPath(WhisperNetModelOption? option, string? configuredModelPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredModelPath) && System.IO.File.Exists(configuredModelPath))
        {
            return configuredModelPath;
        }

        if (option is not null && WhisperNetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            return installedPath;
        }

        return string.Empty;
    }

    private void OnCaptureHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag } ||
            !Enum.TryParse<HotkeyCaptureTarget>(tag, out var target))
        {
            return;
        }

        this.hotkeyCaptureTarget = target;
        this.isCapturingHotkey = true;
        this.ResetCaptureButtonText();
        ((System.Windows.Controls.Button)sender).Content = "Press...";
        this.HotkeyHintText.Text = $"Press the new {GetHotkeyTargetLabel(target)} shortcut.";
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!this.isCapturingHotkey)
        {
            return;
        }

        e.Handled = true;
        var candidate = BuildCandidateHotkey(e);
        if (candidate is null)
        {
            this.HotkeyHintText.Text = "Unsupported key. Try letters, digits, Space, Enter, Esc, or F1-F12.";
            return;
        }

        if (!candidate.IsValid(out var error))
        {
            this.HotkeyHintText.Text = error;
            return;
        }

        switch (this.hotkeyCaptureTarget)
        {
            case HotkeyCaptureTarget.Stop:
                this.currentStopHotkey = candidate;
                break;
            case HotkeyCaptureTarget.History:
                this.currentHistoryHotkey = candidate;
                break;
            default:
                this.currentDictationHotkey = candidate;
                break;
        }

        this.UpdateHotkeyLabels();
        this.HotkeyHintText.Text = $"{GetHotkeyTargetLabel(this.hotkeyCaptureTarget ?? HotkeyCaptureTarget.Dictation)} shortcut captured.";
        this.ResetCaptureButtonText();
        this.hotkeyCaptureTarget = null;
        this.isCapturingHotkey = false;
    }

    private async void OnDownloadSelectedModelClick(object sender, RoutedEventArgs e)
    {
        if (this.modelDownloadCts is not null)
        {
            return;
        }

        this.modelDownloadCts = new CancellationTokenSource();
        this.ModelDownloadProgressBar.IsIndeterminate = true;
        this.ModelDownloadProgressBar.Value = 0;
        this.ModelDownloadProgressBar.Visibility = Visibility.Visible;
        this.CancelModelDownloadButton.Visibility = Visibility.Visible;
        this.ShowModelDownloadMessage("Preparing model download...");
        this.UpdateWindowChrome();
        this.UpdateModelSelectionUi();

        try
        {
            switch (this.currentBackend)
            {
                case TranscriptionBackendKind.Moonshine:
                    await this.DownloadSelectedMoonshineModelAsync(this.modelDownloadCts.Token).ConfigureAwait(true);
                    break;
                case TranscriptionBackendKind.QualcommQnn:
                    await this.DownloadSelectedQualcommAihubWhisperModelAsync(this.modelDownloadCts.Token).ConfigureAwait(true);
                    break;
                case TranscriptionBackendKind.Parakeet:
                    await this.DownloadSelectedParakeetModelAsync(this.modelDownloadCts.Token).ConfigureAwait(true);
                    break;
                case TranscriptionBackendKind.WhisperNet:
                    await this.DownloadSelectedWhisperNetModelAsync(this.modelDownloadCts.Token).ConfigureAwait(true);
                    break;
                default:
                    await this.DownloadSelectedWhisperModelAsync(this.modelDownloadCts.Token).ConfigureAwait(true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            this.ShowModelDownloadMessage("Model download canceled.");
        }
        catch (Exception ex)
        {
            this.ShowModelDownloadMessage($"Download failed: {ex.Message}");
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Model download failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            this.modelDownloadCts?.Dispose();
            this.modelDownloadCts = null;
            this.CancelModelDownloadButton.Visibility = Visibility.Collapsed;
            this.ModelDownloadProgressBar.Visibility = Visibility.Collapsed;
            this.UpdateWindowChrome();
            this.UpdateModelSelectionUi();
        }
    }

    private async Task DownloadSelectedWhisperModelAsync(CancellationToken cancellationToken)
    {
        if (this.ModelChoiceComboBox.SelectedItem is not WhisperModelOption option)
        {
            return;
        }

        if (WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
            this.ShowModelDownloadMessage($"{option.DisplayName} is already installed and ready to use.");
            return;
        }

        this.ShowModelDownloadMessage($"Downloading {option.DisplayName}...");
        var progress = new Progress<WhisperModelDownloadProgress>(downloadProgress =>
        {
            if (downloadProgress.Percentage is double percentage)
            {
                this.ModelDownloadProgressBar.IsIndeterminate = false;
                this.ModelDownloadProgressBar.Value = percentage;
            }

            this.ShowModelDownloadMessage($"Downloading {option.DisplayName} - {downloadProgress.ProgressLabel}");
        });

        var downloadedPath = await WhisperModelDownloader
            .DownloadAsync(option, progress, cancellationToken)
            .ConfigureAwait(true);

        this.SetModelPathText(downloadedPath);
        this.ModelDownloadProgressBar.IsIndeterminate = false;
        this.ModelDownloadProgressBar.Value = 100;
        this.ShowModelDownloadMessage($"Downloaded {option.DisplayName} to {downloadedPath}.");
    }

    private async Task DownloadSelectedQualcommAihubWhisperModelAsync(CancellationToken cancellationToken)
    {
        if (this.ModelChoiceComboBox.SelectedItem is not QualcommAihubWhisperModelOption option)
        {
            return;
        }

        if (QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
            this.ShowModelDownloadMessage($"{option.DisplayName} is already installed and ready to use.");
            return;
        }

        this.ShowModelDownloadMessage($"Downloading {option.DisplayName}...");
        var progress = new Progress<QualcommAihubWhisperModelDownloadProgress>(downloadProgress =>
        {
            if (downloadProgress.Percentage is double percentage)
            {
                this.ModelDownloadProgressBar.IsIndeterminate = false;
                this.ModelDownloadProgressBar.Value = percentage;
            }

            this.ShowModelDownloadMessage($"{option.DisplayName} - {downloadProgress.ProgressLabel}");
        });

        var downloadedPath = await QualcommAihubWhisperModelDownloader
            .DownloadAsync(option, progress, cancellationToken)
            .ConfigureAwait(true);

        this.SetModelPathText(downloadedPath);
        this.ModelDownloadProgressBar.IsIndeterminate = false;
        this.ModelDownloadProgressBar.Value = 100;
        this.ShowModelDownloadMessage($"Downloaded {option.DisplayName} to {downloadedPath}.");
    }

    private async Task DownloadSelectedParakeetModelAsync(CancellationToken cancellationToken)
    {
        if (this.ModelChoiceComboBox.SelectedItem is not ParakeetModelOption option)
        {
            return;
        }

        if (ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
            this.ShowModelDownloadMessage($"{option.DisplayName} is already installed and ready to use.");
            return;
        }

        this.ShowModelDownloadMessage($"Downloading {option.DisplayName}...");
        var progress = new Progress<ParakeetModelDownloadProgress>(downloadProgress =>
        {
            if (downloadProgress.Percentage is double percentage)
            {
                this.ModelDownloadProgressBar.IsIndeterminate = false;
                this.ModelDownloadProgressBar.Value = percentage;
            }

            this.ShowModelDownloadMessage($"{option.DisplayName} - {downloadProgress.ProgressLabel}");
        });

        var downloadedPath = await ParakeetModelDownloader
            .DownloadAsync(option, progress, cancellationToken)
            .ConfigureAwait(true);

        this.SetModelPathText(downloadedPath);
        this.ModelDownloadProgressBar.IsIndeterminate = false;
        this.ModelDownloadProgressBar.Value = 100;
        this.ShowModelDownloadMessage($"Downloaded {option.DisplayName} to {downloadedPath}.");
    }

    private async Task DownloadSelectedMoonshineModelAsync(CancellationToken cancellationToken)
    {
        if (this.ModelChoiceComboBox.SelectedItem is not MoonshineModelOption option)
        {
            return;
        }

        if (MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
            this.ShowModelDownloadMessage($"{option.DisplayName} is already installed and ready to use.");
            return;
        }

        this.ShowModelDownloadMessage($"Downloading {option.DisplayName}...");
        var progress = new Progress<MoonshineModelDownloadProgress>(downloadProgress =>
        {
            if (downloadProgress.Percentage is double percentage)
            {
                this.ModelDownloadProgressBar.IsIndeterminate = false;
                this.ModelDownloadProgressBar.Value = percentage;
            }

            this.ShowModelDownloadMessage($"{option.DisplayName} - {downloadProgress.ProgressLabel}");
        });

        var downloadedPath = await MoonshineModelDownloader
            .DownloadAsync(option, progress, cancellationToken)
            .ConfigureAwait(true);

        this.SetModelPathText(downloadedPath);
        this.ModelDownloadProgressBar.IsIndeterminate = false;
        this.ModelDownloadProgressBar.Value = 100;
        this.ShowModelDownloadMessage($"Downloaded {option.DisplayName} to {downloadedPath}.");
    }

    private async Task DownloadSelectedWhisperNetModelAsync(CancellationToken cancellationToken)
    {
        if (this.ModelChoiceComboBox.SelectedItem is not WhisperNetModelOption option)
        {
            return;
        }

        var canUseOpenVinoOnThisMachine = option.SupportsOpenVinoBundle && PlatformSupport.SupportsWhisperNetOpenVino;

        if (WhisperNetModelCatalog.TryResolveInstalledArtifacts(option, out var installedArtifacts) &&
            (installedArtifacts.Value.HasOpenVinoSidecars || !canUseOpenVinoOnThisMachine))
        {
            this.SetModelPathText(installedArtifacts.Value.ModelPath);
            this.ShowModelDownloadMessage($"{option.DisplayName} is already installed and ready to use.");
            return;
        }

        this.ShowModelDownloadMessage(
            installedArtifacts is { } && canUseOpenVinoOnThisMachine
                ? $"Repairing {option.DisplayName} OpenVINO files..."
                : $"Downloading {option.DisplayName}...");
        var progress = new Progress<WhisperNetModelDownloadProgress>(downloadProgress =>
        {
            if (downloadProgress.Percentage is double percentage)
            {
                this.ModelDownloadProgressBar.IsIndeterminate = false;
                this.ModelDownloadProgressBar.Value = percentage;
            }

            this.ShowModelDownloadMessage($"{option.DisplayName} - {downloadProgress.ProgressLabel}");
        });

        var downloadedPath = await WhisperNetModelDownloader
            .DownloadAsync(option, progress, cancellationToken)
            .ConfigureAwait(true);

        this.SetModelPathText(downloadedPath);
        this.ModelDownloadProgressBar.IsIndeterminate = false;
        this.ModelDownloadProgressBar.Value = 100;
        this.ShowModelDownloadMessage($"Downloaded {option.DisplayName} to {downloadedPath}.");
    }

    private void OnCancelModelDownloadClick(object sender, RoutedEventArgs e)
    {
        this.modelDownloadCts?.Cancel();
    }

    private void OnBrowseModelPathClick(object sender, RoutedEventArgs e)
    {
        switch (this.currentBackend)
        {
            case TranscriptionBackendKind.Moonshine:
                this.BrowseMoonshineModelPath();
                break;
            case TranscriptionBackendKind.QualcommQnn:
                this.BrowseQualcommAihubWhisperModelPath();
                break;
            case TranscriptionBackendKind.Parakeet:
                this.BrowseParakeetModelPath();
                break;
            case TranscriptionBackendKind.WhisperNet:
                this.BrowseWhisperNetModelPath();
                break;
            default:
                this.BrowseWhisperModelPath();
                break;
        }
    }

    private void BrowseWhisperModelPath()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Whisper ONNX model folder",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        this.SetModelPathText(dialog.SelectedPath);
        var matchingCatalogOption = WhisperModelCatalog.TryGetByPath(dialog.SelectedPath);
        if (matchingCatalogOption is not null && !ReferenceEquals(this.ModelChoiceComboBox.SelectedItem, matchingCatalogOption))
        {
            this.suppressModelChoiceChanged = true;
            this.ModelChoiceComboBox.SelectedItem = matchingCatalogOption;
            this.suppressModelChoiceChanged = false;
        }

        this.ShowModelDownloadMessage($"Using model folder: {dialog.SelectedPath}");
        this.UpdateModelSelectionUi();
    }

    private void BrowseQualcommAihubWhisperModelPath()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Qualcomm AI Hub Whisper precompiled_qnn_onnx model folder",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        this.SetModelPathText(dialog.SelectedPath);
        if (QualcommAihubWhisperModelCatalog.IsRawContextOnlyDirectory(dialog.SelectedPath))
        {
            this.ShowModelDownloadMessage("Selected folder contains raw QNN context binaries only. Download the app catalog model to install the required ONNX Runtime wrappers.");
        }
        else
        {
            this.ShowModelDownloadMessage($"Using model folder: {dialog.SelectedPath}");
        }

        this.UpdateModelSelectionUi();
    }

    private void BrowseParakeetModelPath()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Parakeet model folder",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        this.SetModelPathText(dialog.SelectedPath);
        var matchingCatalogOption = ParakeetModelCatalog.TryGetByPath(dialog.SelectedPath);
        if (matchingCatalogOption is not null && !ReferenceEquals(this.ModelChoiceComboBox.SelectedItem, matchingCatalogOption))
        {
            this.suppressModelChoiceChanged = true;
            this.ModelChoiceComboBox.SelectedItem = matchingCatalogOption;
            this.suppressModelChoiceChanged = false;
        }

        this.ShowModelDownloadMessage($"Using model folder: {dialog.SelectedPath}");
        this.UpdateModelSelectionUi();
    }

    private void BrowseMoonshineModelPath()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Moonshine model folder",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        this.SetModelPathText(dialog.SelectedPath);
        var matchingCatalogOption = MoonshineModelCatalog.TryGetByPath(dialog.SelectedPath);
        if (matchingCatalogOption is not null && !ReferenceEquals(this.ModelChoiceComboBox.SelectedItem, matchingCatalogOption))
        {
            this.suppressModelChoiceChanged = true;
            this.ModelChoiceComboBox.SelectedItem = matchingCatalogOption;
            this.suppressModelChoiceChanged = false;
        }

        this.ShowModelDownloadMessage($"Using model folder: {dialog.SelectedPath}");
        this.UpdateModelSelectionUi();
    }

    private void BrowseWhisperNetModelPath()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Select Whisper GGML model (.bin)",
            Filter = "GGML Model Files (*.bin)|*.bin|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        this.SetModelPathText(dialog.FileName);
        var matchingCatalogOption = WhisperNetModelCatalog.TryGetByPath(dialog.FileName);
        if (matchingCatalogOption is not null && !ReferenceEquals(this.ModelChoiceComboBox.SelectedItem, matchingCatalogOption))
        {
            this.suppressModelChoiceChanged = true;
            this.ModelChoiceComboBox.SelectedItem = matchingCatalogOption;
            this.suppressModelChoiceChanged = false;
        }

        this.ShowModelDownloadMessage($"Using model file: {dialog.FileName}");
        this.UpdateModelSelectionUi();
    }

    private void OnModelChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.suppressModelChoiceChanged)
        {
            this.UpdateModelSelectionUi();
            return;
        }

        switch (this.currentBackend)
        {
            case TranscriptionBackendKind.Moonshine:
                this.OnMoonshineModelChoiceChanged();
                break;
            case TranscriptionBackendKind.QualcommQnn:
                this.OnQualcommAihubWhisperModelChoiceChanged();
                break;
            case TranscriptionBackendKind.Parakeet:
                this.OnParakeetModelChoiceChanged();
                break;
            case TranscriptionBackendKind.WhisperNet:
                this.OnWhisperNetModelChoiceChanged();
                break;
            default:
                this.OnWhisperModelChoiceChanged();
                break;
        }
    }

    private void OnWhisperModelChoiceChanged()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not WhisperModelOption option)
        {
            this.UpdateModelSelectionUi();
            return;
        }

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var currentPathMatchesCatalog = WhisperModelCatalog.TryGetByPath(currentPath) is not null;

        if (WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
        }
        else if (string.IsNullOrWhiteSpace(currentPath) || currentPathMatchesCatalog)
        {
            this.SetModelPathText(string.Empty);
        }

        this.UpdateModelSelectionUi();
    }

    private void OnQualcommAihubWhisperModelChoiceChanged()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not QualcommAihubWhisperModelOption option)
        {
            this.UpdateModelSelectionUi();
            return;
        }

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var currentPathMatchesCatalog = QualcommAihubWhisperModelCatalog.TryGetByPath(currentPath) is not null;

        if (QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
        }
        else if (string.IsNullOrWhiteSpace(currentPath) || currentPathMatchesCatalog)
        {
            this.SetModelPathText(string.Empty);
        }

        this.UpdateModelSelectionUi();
    }

    private void OnParakeetModelChoiceChanged()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not ParakeetModelOption option)
        {
            this.UpdateModelSelectionUi();
            return;
        }

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var currentPathMatchesCatalog = ParakeetModelCatalog.TryGetByPath(currentPath) is not null;

        if (ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
        }
        else if (string.IsNullOrWhiteSpace(currentPath) || currentPathMatchesCatalog)
        {
            this.SetModelPathText(string.Empty);
        }

        this.UpdateModelSelectionUi();
    }

    private void OnMoonshineModelChoiceChanged()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not MoonshineModelOption option)
        {
            this.UpdateModelSelectionUi();
            return;
        }

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var currentPathMatchesCatalog = MoonshineModelCatalog.TryGetByPath(currentPath) is not null;

        if (MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
        }
        else if (string.IsNullOrWhiteSpace(currentPath) || currentPathMatchesCatalog)
        {
            this.SetModelPathText(string.Empty);
        }

        this.UpdateModelSelectionUi();
    }

    private void OnWhisperNetModelChoiceChanged()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not WhisperNetModelOption option)
        {
            this.UpdateModelSelectionUi();
            return;
        }

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var currentPathMatchesCatalog = WhisperNetModelCatalog.TryGetByPath(currentPath) is not null;

        if (WhisperNetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            this.SetModelPathText(installedPath);
        }
        else if (string.IsNullOrWhiteSpace(currentPath) || currentPathMatchesCatalog)
        {
            this.SetModelPathText(string.Empty);
        }

        this.UpdateModelSelectionUi();
    }

    private void OnModelPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (this.suppressModelPathTextChanged)
        {
            return;
        }

        var currentPath = this.ModelPathTextBox.Text.Trim();
        object? matchingCatalogOption = this.currentBackend switch
        {
            TranscriptionBackendKind.Moonshine => MoonshineModelCatalog.TryGetByPath(currentPath),
            TranscriptionBackendKind.Parakeet => ParakeetModelCatalog.TryGetByPath(currentPath),
            TranscriptionBackendKind.QualcommQnn => QualcommAihubWhisperModelCatalog.TryGetByPath(currentPath),
            TranscriptionBackendKind.WhisperNet => WhisperNetModelCatalog.TryGetByPath(currentPath),
            _ => WhisperModelCatalog.TryGetByPath(currentPath)
        };

        if (matchingCatalogOption is not null && !ReferenceEquals(this.ModelChoiceComboBox.SelectedItem, matchingCatalogOption))
        {
            this.suppressModelChoiceChanged = true;
            this.ModelChoiceComboBox.SelectedItem = matchingCatalogOption;
            this.suppressModelChoiceChanged = false;
        }

        this.UpdateModelSelectionUi();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (this.isFirstRun)
        {
            if (ReferenceEquals(this.SetupTabControl.SelectedItem, this.WelcomeTab))
            {
                this.SetupTabControl.SelectedItem = this.ModelTab;
                return;
            }

            if (ReferenceEquals(this.SetupTabControl.SelectedItem, this.ModelTab))
            {
                if (!this.TryResolveModelSelectionForSave(out _, out _))
                {
                    return;
                }

                this.SetupTabControl.SelectedItem = this.PreferencesTab;
                return;
            }

        }

        if (!this.TryBuildSettings(out var settings))
        {
            return;
        }

        if (!LaunchAtLoginManager.TryApply(settings.LaunchAtLoginScope, out var launchAtLoginError))
        {
            System.Windows.MessageBox.Show(
                this,
                launchAtLoginError,
                "Could not update startup shortcut",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        this.SettingsSaved?.Invoke(settings);
        this.Close();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (!this.isFirstRun)
        {
            return;
        }

        if (this.SetupTabControl.SelectedIndex > 0)
        {
            this.SetupTabControl.SelectedIndex--;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (this.isFirstRun)
        {
            return;
        }

        this.Close();
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        this.HistoryRequested?.Invoke();
    }

    private void OnModelBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.suppressBackendChoiceChanged || this.ModelBackendComboBox.SelectedItem is not BackendChoice choice)
        {
            this.UpdateBackendUi();
            this.UpdateModelSelectionUi();
            return;
        }

        this.ApplyBackendSelection(choice.Kind, preferredModelId: null, configuredModelPath: null);
        this.UpdateBackendUi();
    }

    private void OnComputeInterfaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.suppressComputeChoiceChanged)
        {
            return;
        }

        this.UpdateComputeInterfaceDescription();
    }

    private void OnSetupTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, this.SetupTabControl))
        {
            return;
        }

        this.UpdateWindowChrome();
    }

    private void UpdateWindowChrome()
    {
        if (this.HeaderSubtextText is null ||
            this.BackButton is null ||
            this.CancelButton is null ||
            this.HistoryButton is null ||
            this.SaveButton is null ||
            this.SetupTabControl is null)
        {
            return;
        }

        if (!this.isFirstRun)
        {
            this.HeaderSubtextText.Text = "Manage your local model, commands, and dictation behavior.";
            this.BackButton.Visibility = Visibility.Collapsed;
            this.CancelButton.Visibility = Visibility.Visible;
            this.HistoryButton.Visibility = Visibility.Visible;
            this.SaveButton.Content = "Save";
            this.SaveButton.IsEnabled = this.modelDownloadCts is null;
            return;
        }

        var stepIndex = Math.Clamp(this.SetupTabControl.SelectedIndex, 0, 2);
        var stepNumber = stepIndex + 1;
        var stepLabel = stepIndex switch
        {
            0 => "Welcome",
            1 => "Choose your model",
            _ => "Configure commands"
        };

        this.HeaderSubtextText.Text = $"Step {stepNumber} of 3: {stepLabel}";
        this.BackButton.Visibility = Visibility.Visible;
        this.BackButton.IsEnabled = stepIndex > 0 && this.modelDownloadCts is null;
        this.CancelButton.Visibility = Visibility.Collapsed;
        this.HistoryButton.Visibility = Visibility.Collapsed;
        this.SaveButton.Content = ReferenceEquals(this.SetupTabControl.SelectedItem, this.PreferencesTab) ? "Finish" : "Next";
        this.SaveButton.IsEnabled = this.modelDownloadCts is null;
    }

    private void UpdateBackendUi()
    {
        var backendChoice = AllBackendChoices.FirstOrDefault(choice => choice.Kind == this.currentBackend);
        this.ModelBackendDescriptionText.Text = this.currentBackend switch
        {
            TranscriptionBackendKind.WhisperNet => PlatformSupport.WhisperNetRuntimeSummary,
            TranscriptionBackendKind.QualcommQnn => PlatformSupport.QualcommQnnRuntimeSummary,
            _ => backendChoice?.Description ?? string.Empty
        };
        this.BrowseModelPathButton.Content = this.currentBackend == TranscriptionBackendKind.WhisperNet
            ? "Browse file..."
            : "Browse folder...";
        this.ModelPathLabelText.Text = this.currentBackend == TranscriptionBackendKind.WhisperNet
            ? "Resolved model file path"
            : "Resolved model folder path";
        this.ModelStorageHintText.Text = this.currentBackend switch
        {
            TranscriptionBackendKind.Parakeet => $"Downloaded Parakeet models are stored in {ParakeetModelCatalog.GetManagedModelsDirectory()}.",
            TranscriptionBackendKind.Moonshine => $"Downloaded Moonshine models are stored in {MoonshineModelCatalog.GetManagedModelsDirectory()}.",
            TranscriptionBackendKind.QualcommQnn => $"Downloaded Qualcomm AI Hub Whisper models are stored in {QualcommAihubWhisperModelCatalog.GetManagedModelsDirectory()}. The raw context-binary ZIP needs ONNX Runtime wrapper files, so the app installs the matching precompiled_qnn_onnx package.",
            TranscriptionBackendKind.WhisperNet => $"Downloaded Whisper.net models are stored in {WhisperNetModelCatalog.GetManagedModelsDirectory()}.",
            _ => $"Downloaded Whisper ONNX models are stored in {WhisperModelCatalog.GetManagedModelsDirectory()}."
        };

        this.UpdateComputeInterfaceAvailability();
    }

    private void UpdateModelSelectionUi()
    {
        this.UpdateComputeInterfaceAvailability(this.GetSelectedComputeInterface());

        switch (this.currentBackend)
        {
            case TranscriptionBackendKind.Moonshine:
                this.UpdateMoonshineModelSelectionUi();
                return;
            case TranscriptionBackendKind.QualcommQnn:
                this.UpdateQualcommQnnModelSelectionUi();
                return;
            case TranscriptionBackendKind.Parakeet:
                this.UpdateParakeetModelSelectionUi();
                return;
            case TranscriptionBackendKind.WhisperNet:
                this.UpdateWhisperNetModelSelectionUi();
                return;
            default:
                this.UpdateWhisperModelSelectionUi();
                return;
        }
    }

    private void UpdateWhisperNetModelSelectionUi()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not WhisperNetModelOption option)
        {
            this.ModelChoiceTitleText.Text = "Select a Whisper.net model";
            this.ModelChoiceMetaText.Text = string.Empty;
            this.ModelChoiceDescriptionText.Text = string.Empty;
            this.ModelChoiceStatusText.Text = "Pick a Whisper.net (GGML) model to download or browse to an existing model file.";
            this.RecommendedBadgeBorder.Visibility = Visibility.Collapsed;
            this.DownloadSelectedModelButton.IsEnabled = false;
            return;
        }

        this.ModelChoiceTitleText.Text = option.DisplayName;
        this.ModelChoiceMetaText.Text = $"Approx. {option.ApproximateSizeLabel} download";
        this.ModelChoiceDescriptionText.Text = option.Description;
        this.RecommendedBadgeBorder.Visibility = option.Recommended ? Visibility.Visible : Visibility.Collapsed;

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var hasConfiguredPath = WhisperNetModelCatalog.TryGetByPath(currentPath) is not null;
        var isInstalled = WhisperNetModelCatalog.TryResolveInstalledArtifacts(option, out var installedArtifacts);
        var resolvedArtifacts = installedArtifacts.GetValueOrDefault();
        var hasOpenVinoSidecars = isInstalled && resolvedArtifacts.HasOpenVinoSidecars;
        var canUseOpenVinoOnThisMachine = option.SupportsOpenVinoBundle && PlatformSupport.SupportsWhisperNetOpenVino;
        var installedPath = isInstalled ? resolvedArtifacts.ModelPath : string.Empty;
        var configuredPathMatchesInstalled =
            hasConfiguredPath &&
            isInstalled &&
            string.Equals(Path.GetFullPath(currentPath), installedPath, StringComparison.OrdinalIgnoreCase);

        if (configuredPathMatchesInstalled && canUseOpenVinoOnThisMachine && !hasOpenVinoSidecars)
        {
            this.ModelChoiceStatusText.Text = $"GGML model found at {installedPath}, but the Intel OpenVINO sidecars are missing. Click Download model to install the NPU bundle.";
        }
        else if (hasConfiguredPath)
        {
            var configuredOption = WhisperNetModelCatalog.TryGetByPath(currentPath);
            if (configuredOption is not null && configuredOption.Id == option.Id)
            {
                this.ModelChoiceStatusText.Text = $"Selected model ready: {currentPath}";
            }
            else
            {
                this.ModelChoiceStatusText.Text = $"Using a custom Whisper.net model file: {currentPath}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentPath) && System.IO.File.Exists(currentPath))
        {
            this.ModelChoiceStatusText.Text = PlatformSupport.SupportsWhisperNetOpenVino
                ? $"Using a custom Whisper.net model file: {currentPath}"
                : $"Using a custom Whisper.net model file: {currentPath}. Native ARM64 will run it on CPU.";
        }
        else if (!string.IsNullOrWhiteSpace(currentPath))
        {
            this.ModelChoiceStatusText.Text = "The file in the textbox is missing or invalid.";
        }
        else if (!PlatformSupport.SupportsWhisperNetOpenVino && hasOpenVinoSidecars)
        {
            this.ModelChoiceStatusText.Text = $"Installed on this PC: {installedPath}. This native ARM64 build will still use CPU; the OpenVINO sidecars are only usable from an x64 build.";
        }
        else if (hasOpenVinoSidecars)
        {
            this.ModelChoiceStatusText.Text = $"Installed on this PC: {installedPath}";
        }
        else if (isInstalled && canUseOpenVinoOnThisMachine)
        {
            this.ModelChoiceStatusText.Text = $"GGML model found at {installedPath}, but the Intel OpenVINO sidecars are missing. Click Download model to install the NPU bundle.";
        }
        else if (isInstalled)
        {
            this.ModelChoiceStatusText.Text = PlatformSupport.SupportsWhisperNetOpenVino
                ? $"Installed on this PC: {installedPath}. Intel does not publish an OpenVINO bundle for this model, so NPU sidecars are unavailable."
                : $"Installed on this PC: {installedPath}. Native ARM64 uses CPU for Whisper.net; OpenVINO/NPU requires an x64 build.";
        }
        else if (!PlatformSupport.SupportsWhisperNetOpenVino)
        {
            this.ModelChoiceStatusText.Text = $"Not downloaded yet. Download {option.DisplayName} for native ARM64 CPU use. Whisper.net OpenVINO/NPU requires an x64 build today.";
        }
        else if (!option.SupportsOpenVinoBundle)
        {
            this.ModelChoiceStatusText.Text = $"Not downloaded yet. Download {option.DisplayName} for GGML use, but note that Intel does not publish an OpenVINO bundle for this model.";
        }
        else
        {
            this.ModelChoiceStatusText.Text = $"Not downloaded yet. Download {option.DisplayName} or browse to a GGML .bin file.";
        }

        this.DownloadSelectedModelButton.Content = (hasOpenVinoSidecars && canUseOpenVinoOnThisMachine) || (isInstalled && !canUseOpenVinoOnThisMachine)
            ? "Already installed"
            : isInstalled && canUseOpenVinoOnThisMachine
                ? "Repair download"
                : "Download model";
        this.DownloadSelectedModelButton.IsEnabled =
            ((!isInstalled) || (canUseOpenVinoOnThisMachine && !hasOpenVinoSidecars)) &&
            this.modelDownloadCts is null;
        this.BrowseModelPathButton.IsEnabled = this.modelDownloadCts is null;
    }

    private void UpdateWhisperModelSelectionUi()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not WhisperModelOption option)
        {
            this.ModelChoiceTitleText.Text = "Select a Whisper model";
            this.ModelChoiceMetaText.Text = string.Empty;
            this.ModelChoiceDescriptionText.Text = string.Empty;
            this.ModelChoiceStatusText.Text = "Pick a Whisper ONNX model to download or browse to an existing model folder.";
            this.RecommendedBadgeBorder.Visibility = Visibility.Collapsed;
            this.DownloadSelectedModelButton.IsEnabled = false;
            return;
        }

        this.ModelChoiceTitleText.Text = option.DisplayName;
        this.ModelChoiceMetaText.Text = $"Approx. {option.ApproximateSizeLabel} download";
        this.ModelChoiceDescriptionText.Text = option.Description;
        this.RecommendedBadgeBorder.Visibility = option.Recommended ? Visibility.Visible : Visibility.Collapsed;

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var hasConfiguredPath = WhisperModelCatalog.TryResolveDirectory(currentPath, out var resolvedConfiguredPath);
        var isInstalled = WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath);

        if (hasConfiguredPath)
        {
            var configuredOption = WhisperModelCatalog.TryGetByPath(resolvedConfiguredPath);
            if (configuredOption is not null && configuredOption.Id == option.Id)
            {
                this.ModelChoiceStatusText.Text = $"Selected model ready: {resolvedConfiguredPath}";
            }
            else
            {
                this.ModelChoiceStatusText.Text = $"Using a custom Whisper ONNX model folder: {resolvedConfiguredPath}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentPath))
        {
            this.ModelChoiceStatusText.Text = "The model folder in the textbox is missing required Whisper ONNX files.";
        }
        else if (isInstalled)
        {
            this.ModelChoiceStatusText.Text = $"Installed on this PC: {installedPath}";
        }
        else
        {
            this.ModelChoiceStatusText.Text = $"Not downloaded yet. Download {option.DisplayName} or browse to a folder containing a Whisper ONNX encoder, decoder, and tokens file.";
        }

        this.DownloadSelectedModelButton.Content = isInstalled ? "Already installed" : "Download model";
        this.DownloadSelectedModelButton.IsEnabled = !isInstalled && this.modelDownloadCts is null;
        this.BrowseModelPathButton.IsEnabled = this.modelDownloadCts is null;
    }

    private void UpdateParakeetModelSelectionUi()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not ParakeetModelOption option)
        {
            this.ModelChoiceTitleText.Text = "Select a Parakeet model";
            this.ModelChoiceMetaText.Text = string.Empty;
            this.ModelChoiceDescriptionText.Text = string.Empty;
            this.ModelChoiceStatusText.Text = "Pick a Parakeet model to download or browse to an existing model folder.";
            this.RecommendedBadgeBorder.Visibility = Visibility.Collapsed;
            this.DownloadSelectedModelButton.IsEnabled = false;
            return;
        }

        this.ModelChoiceTitleText.Text = option.DisplayName;
        this.ModelChoiceMetaText.Text = $"Approx. {option.ApproximateSizeLabel} download";
        this.ModelChoiceDescriptionText.Text = option.Description;
        this.RecommendedBadgeBorder.Visibility = option.Recommended ? Visibility.Visible : Visibility.Collapsed;

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var hasConfiguredPath = ParakeetModelCatalog.TryResolveDirectory(currentPath, out var resolvedConfiguredPath);
        var isInstalled = ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath);

        if (hasConfiguredPath)
        {
            var configuredOption = ParakeetModelCatalog.TryGetByPath(resolvedConfiguredPath);
            if (configuredOption is not null && configuredOption.Id == option.Id)
            {
                this.ModelChoiceStatusText.Text = $"Selected model ready: {resolvedConfiguredPath}";
            }
            else
            {
                this.ModelChoiceStatusText.Text = $"Using a custom Parakeet model folder: {resolvedConfiguredPath}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentPath))
        {
            this.ModelChoiceStatusText.Text = "The model folder in the textbox is missing required Parakeet files.";
        }
        else if (isInstalled)
        {
            this.ModelChoiceStatusText.Text = $"Installed on this PC: {installedPath}";
        }
        else
        {
            this.ModelChoiceStatusText.Text = $"Not downloaded yet. Download {option.DisplayName} or browse to a folder containing encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx, and tokens.txt.";
        }

        this.DownloadSelectedModelButton.Content = isInstalled ? "Already installed" : "Download model";
        this.DownloadSelectedModelButton.IsEnabled = !isInstalled && this.modelDownloadCts is null;
        this.BrowseModelPathButton.IsEnabled = this.modelDownloadCts is null;
    }

    private void UpdateMoonshineModelSelectionUi()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not MoonshineModelOption option)
        {
            this.ModelChoiceTitleText.Text = "Select a Moonshine model";
            this.ModelChoiceMetaText.Text = string.Empty;
            this.ModelChoiceDescriptionText.Text = string.Empty;
            this.ModelChoiceStatusText.Text = "Pick a Moonshine model to download or browse to an existing model folder.";
            this.RecommendedBadgeBorder.Visibility = Visibility.Collapsed;
            this.DownloadSelectedModelButton.IsEnabled = false;
            return;
        }

        this.ModelChoiceTitleText.Text = option.DisplayName;
        this.ModelChoiceMetaText.Text = $"Approx. {option.ApproximateSizeLabel} download";
        this.ModelChoiceDescriptionText.Text = option.Description;
        this.RecommendedBadgeBorder.Visibility = option.Recommended ? Visibility.Visible : Visibility.Collapsed;

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var hasConfiguredPath = MoonshineModelCatalog.TryResolveDirectory(currentPath, out var resolvedConfiguredPath);
        var isInstalled = MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath);

        if (hasConfiguredPath)
        {
            var configuredOption = MoonshineModelCatalog.TryGetByPath(resolvedConfiguredPath);
            if (configuredOption is not null && configuredOption.Id == option.Id)
            {
                this.ModelChoiceStatusText.Text = $"Selected model ready: {resolvedConfiguredPath}";
            }
            else
            {
                this.ModelChoiceStatusText.Text = $"Using a custom Moonshine model folder: {resolvedConfiguredPath}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentPath))
        {
            this.ModelChoiceStatusText.Text = "The model folder in the textbox is missing required Moonshine files.";
        }
        else if (isInstalled)
        {
            this.ModelChoiceStatusText.Text = $"Installed on this PC: {installedPath}";
        }
        else
        {
            this.ModelChoiceStatusText.Text = $"Not downloaded yet. Download {option.DisplayName} or browse to a folder containing preprocess.onnx, encode.int8.onnx, uncached_decode.int8.onnx, cached_decode.int8.onnx, and tokens.txt.";
        }

        this.DownloadSelectedModelButton.Content = isInstalled ? "Already installed" : "Download model";
        this.DownloadSelectedModelButton.IsEnabled = !isInstalled && this.modelDownloadCts is null;
        this.BrowseModelPathButton.IsEnabled = this.modelDownloadCts is null;
    }

    private void UpdateQualcommQnnModelSelectionUi()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not QualcommAihubWhisperModelOption option)
        {
            this.ModelChoiceTitleText.Text = "Select a Qualcomm QNN model";
            this.ModelChoiceMetaText.Text = string.Empty;
            this.ModelChoiceDescriptionText.Text = string.Empty;
            this.ModelChoiceStatusText.Text = "Pick a Qualcomm AI Hub Whisper model to download or browse to an extracted precompiled_qnn_onnx folder.";
            this.RecommendedBadgeBorder.Visibility = Visibility.Collapsed;
            this.DownloadSelectedModelButton.IsEnabled = false;
            return;
        }

        this.ModelChoiceTitleText.Text = option.DisplayName;
        this.ModelChoiceMetaText.Text = $"Approx. {option.ApproximateSizeLabel} download";
        this.ModelChoiceDescriptionText.Text = option.Description;
        this.RecommendedBadgeBorder.Visibility = option.Recommended ? Visibility.Visible : Visibility.Collapsed;

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var hasConfiguredPath = QualcommAihubWhisperModelCatalog.TryResolveDirectory(currentPath, out var resolvedConfiguredPath);
        var hasRawContextOnlyPath = QualcommAihubWhisperModelCatalog.IsRawContextOnlyDirectory(currentPath);
        var isInstalled = QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath);

        if (hasConfiguredPath)
        {
            var configuredOption = QualcommAihubWhisperModelCatalog.TryGetByPath(resolvedConfiguredPath);
            if (!PlatformSupport.SupportsQualcommQnnHtp)
            {
                this.ModelChoiceStatusText.Text = $"Selected Qualcomm AI Hub Whisper model is installed, but QNN HTP is unavailable in this process: {resolvedConfiguredPath}";
            }
            else if (configuredOption is not null && configuredOption.Id == option.Id)
            {
                this.ModelChoiceStatusText.Text = $"Selected Qualcomm AI Hub Whisper model ready for QNN HTP: {resolvedConfiguredPath}";
            }
            else
            {
                this.ModelChoiceStatusText.Text = $"Using a custom Qualcomm AI Hub Whisper precompiled_qnn_onnx folder: {resolvedConfiguredPath}";
            }
        }
        else if (hasRawContextOnlyPath)
        {
            this.ModelChoiceStatusText.Text = "The selected folder contains only raw qnn_context_binary files (encoder.bin and decoder.bin). PrimeDictate needs encoder.onnx and decoder.onnx EPContext wrappers; click Download model to install the runnable precompiled_qnn_onnx package.";
        }
        else if (!string.IsNullOrWhiteSpace(currentPath))
        {
            this.ModelChoiceStatusText.Text = "The model folder in the textbox is missing required Qualcomm AI Hub Whisper files.";
        }
        else if (isInstalled)
        {
            this.ModelChoiceStatusText.Text = PlatformSupport.SupportsQualcommQnnHtp
                ? $"Installed on this PC: {installedPath}. This package is ready for QNN HTP transcription."
                : $"Installed on this PC: {installedPath}. QNN HTP requires a native Windows ARM64 process with QNN runtime assets present.";
        }
        else
        {
            this.ModelChoiceStatusText.Text = PlatformSupport.SupportsQualcommQnnHtp
                ? $"Not downloaded yet. Download {option.DisplayName} or browse to an extracted precompiled_qnn_onnx folder."
                : $"Not downloaded yet. Download {option.DisplayName} to stage the model, but Qualcomm QNN HTP requires a native Windows ARM64 build with QNN runtime assets present.";
        }

        this.DownloadSelectedModelButton.Content = isInstalled ? "Already installed" : "Download model";
        this.DownloadSelectedModelButton.IsEnabled = !isInstalled && this.modelDownloadCts is null;
        this.BrowseModelPathButton.IsEnabled = this.modelDownloadCts is null;
    }

    private bool TryBuildSettings(out AppSettings settings)
    {
        settings = null!;

        if (!ValidateShortcut(this.currentDictationHotkey, "Start / stop dictation", out var hotkeyError) ||
            !ValidateShortcut(this.currentStopHotkey, "Emergency stop", out hotkeyError) ||
            !ValidateShortcut(this.currentHistoryHotkey, "Open history", out hotkeyError))
        {
            System.Windows.MessageBox.Show(this, hotkeyError, "Invalid shortcut", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (AreSameGesture(this.currentDictationHotkey, this.currentStopHotkey) ||
            AreSameGesture(this.currentDictationHotkey, this.currentHistoryHotkey) ||
            AreSameGesture(this.currentStopHotkey, this.currentHistoryHotkey))
        {
            System.Windows.MessageBox.Show(
                this,
                "Each keyboard shortcut must use a different key combination.",
                "Duplicate shortcuts",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (!this.TryResolveModelSelectionForSave(out var resolvedModelPath, out var selectedModelId))
        {
            return false;
        }

        if (!int.TryParse(
                this.AutoCommitSilenceSecondsTextBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var autoCommitSeconds) ||
            autoCommitSeconds is < 0 or > 30)
        {
            System.Windows.MessageBox.Show(
                this,
                "Auto-commit silence must be a whole number from 0 to 30 seconds. Use 0 to commit only with the start / stop shortcut.",
                "Invalid auto-commit delay",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (!double.TryParse(
                this.InputGainTextBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var inputGainMultiplier) ||
            inputGainMultiplier < 0.5 ||
            inputGainMultiplier > 4.0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Input gain must be a number from 0.5 to 4.0.",
                "Invalid input gain",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(
                this.BaselineTypingSpeedTextBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var baselineTypingSpeedWpm) ||
            baselineTypingSpeedWpm is < 20 or > 120)
        {
            System.Windows.MessageBox.Show(
                this,
                "Baseline typing speed must be a whole number from 20 to 120 WPM.",
                "Invalid typing speed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var selectedBehavior = ((ComboBoxItem)this.TrayBehaviorComboBox.SelectedItem).Tag?.ToString();
        var selectedLaunchAtLoginScope = GetSelectedLaunchAtLoginScope();
        var selectedOverlayMode = ((ComboBoxItem)this.OverlayModeComboBox.SelectedItem).Tag?.ToString();
        var enableVoiceCommands = this.EnableVoiceCommandsCheckBox.IsChecked == true;
        var voiceStopPhrase = this.VoiceStopPhraseTextBox.Text.Trim();
        var voiceHistoryPhrase = this.VoiceHistoryPhraseTextBox.Text.Trim();
        if (enableVoiceCommands &&
            string.IsNullOrWhiteSpace(voiceStopPhrase) &&
            string.IsNullOrWhiteSpace(voiceHistoryPhrase))
        {
            System.Windows.MessageBox.Show(
                this,
                "Voice commands need at least one phrase.",
                "Voice command phrase required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(voiceStopPhrase) &&
            !string.IsNullOrWhiteSpace(voiceHistoryPhrase) &&
            string.Equals(voiceStopPhrase, voiceHistoryPhrase, StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(
                this,
                "Use different phrases for Stop and History voice commands.",
                "Duplicate voice commands",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (!this.TryBuildVoiceShellCommands(
                voiceStopPhrase,
                voiceHistoryPhrase,
                out var voiceShellCommandsForSave))
        {
            return false;
        }

        var selectedOllamaModeStr = ((ComboBoxItem)this.OllamaModeComboBox.SelectedItem)?.Tag?.ToString() ?? "Default";
        if (!Enum.TryParse<OllamaMode>(selectedOllamaModeStr, out var selectedOllamaMode))
        {
            selectedOllamaMode = OllamaMode.Default;
        }

        settings = new AppSettings
        {
            FirstRunCompleted = true,
            DictationHotkey = this.currentDictationHotkey,
            StopHotkey = this.currentStopHotkey,
            HistoryHotkey = this.currentHistoryHotkey,
            EnableVoiceCommands = enableVoiceCommands,
            VoiceStopPhrase = string.IsNullOrWhiteSpace(voiceStopPhrase) ? "potato farmer" : voiceStopPhrase,
            VoiceHistoryPhrase = string.IsNullOrWhiteSpace(voiceHistoryPhrase) ? "show me the money" : voiceHistoryPhrase,
            VoiceShellCommands = voiceShellCommandsForSave,
            TrayClickBehavior = selectedBehavior == "Single"
                ? TrayClickBehavior.SingleClickOpensSettings
                : TrayClickBehavior.DoubleClickOpensSettings,
            LaunchAtLoginScope = selectedLaunchAtLoginScope,
            TranscriptionBackend = this.currentBackend,
            TranscriptionComputeInterface = this.GetSelectedComputeInterface(),
            SelectedModelId = selectedModelId,
            ModelPath = resolvedModelPath,
            ExclusiveMicAccessWhileDictating = this.ExclusiveMicAccessCheckBox.IsChecked == true,
            SelectedInputDeviceId = (this.InputDeviceComboBox.SelectedItem as InputDeviceOption)?.DeviceId,
            InputGainMultiplier = inputGainMultiplier,
            AutoCommitSilenceSeconds = autoCommitSeconds,
            SendEnterAfterCommit = this.SendEnterAfterCommitCheckBox.IsChecked == true,
            ReturnToStartTargetOnCommit = this.ReturnToStartTargetCheckBox.IsChecked == true,
            PlayAudioCues = this.PlayAudioCuesCheckBox.IsChecked != false,
            BaselineTypingSpeedWpm = baselineTypingSpeedWpm,
            OverlayMode = selectedOverlayMode == "Full"
                ? OverlayMode.FullPanel
                : OverlayMode.CompactMicrophone,
            IsOverlaySticky = this.isOverlaySticky,
            EnableOllamaPostProcessing = this.EnableOllamaCheckBox.IsChecked == true,
            OllamaEndpoint = this.OllamaEndpointTextBox.Text.Trim(),
            OllamaModel = this.OllamaModelTextBox.Text.Trim(),
            OllamaMode = selectedOllamaMode,
            TranscriptReplacements = this.transcriptReplacementRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Find))
                .Select(r => new TranscriptReplacementRule { Find = r.Find.Trim(), Replace = r.Replace.Trim() })
                .ToList()
        };

        return true;
    }

    private bool TryBuildVoiceShellCommands(
        string voiceStopPhrase,
        string voiceHistoryPhrase,
        out List<VoiceShellCommand> commands)
    {
        commands = new List<VoiceShellCommand>();
        var phraseOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddReservedVoicePhrase(phraseOwners, voiceStopPhrase, "Stop phrase");
        AddReservedVoicePhrase(phraseOwners, voiceHistoryPhrase, "History phrase");

        foreach (var row in this.voiceShellCommands)
        {
            var phrase = row.Phrase.Trim();
            var command = row.Command.Trim();
            if (phrase.Length == 0 && command.Length == 0)
            {
                continue;
            }

            if (phrase.Length == 0 || command.Length == 0)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Each computer command needs both a spoken phrase and a command prompt command.",
                    "Incomplete computer command",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var phraseKey = NormalizeVoicePhraseKey(phrase);
            if (phraseKey.Length == 0)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Computer command phrases need at least one letter or number.",
                    "Invalid computer command phrase",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (phraseOwners.TryGetValue(phraseKey, out var existingOwner))
            {
                System.Windows.MessageBox.Show(
                    this,
                    $"The computer command phrase \"{phrase}\" conflicts with {existingOwner}.",
                    "Duplicate voice command",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            phraseOwners.Add(phraseKey, $"computer command \"{phrase}\"");
            commands.Add(new VoiceShellCommand
            {
                Enabled = row.Enabled,
                Phrase = phrase,
                CompletionBehavior = row.CompletionBehavior,
                Command = command
            });
        }

        return true;
    }

    private static void AddReservedVoicePhrase(
        IDictionary<string, string> phraseOwners,
        string phrase,
        string owner)
    {
        var phraseKey = NormalizeVoicePhraseKey(phrase);
        if (phraseKey.Length > 0)
        {
            phraseOwners[phraseKey] = owner;
        }
    }

    private static string NormalizeVoicePhraseKey(string phrase)
    {
        var parts = new List<string>();
        var current = new List<char>();
        foreach (var character in phrase)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Add(char.ToLowerInvariant(character));
                continue;
            }

            if (current.Count == 0)
            {
                continue;
            }

            parts.Add(new string(current.ToArray()));
            current.Clear();
        }

        if (current.Count > 0)
        {
            parts.Add(new string(current.ToArray()));
        }

        return string.Join(" ", parts);
    }

    private static bool ValidateShortcut(HotkeyGesture hotkey, string label, out string error)
    {
        if (hotkey.IsValid(out var validationError))
        {
            error = string.Empty;
            return true;
        }

        error = $"{label}: {validationError}";
        return false;
    }

    private static bool AreSameGesture(HotkeyGesture left, HotkeyGesture right) =>
        left.KeyCode == right.KeyCode &&
        left.Ctrl == right.Ctrl &&
        left.Shift == right.Shift &&
        left.Alt == right.Alt;

    private TranscriptionComputeInterface GetSelectedComputeInterface()
    {
        if (this.ComputeInterfaceComboBox.SelectedItem is TranscriptionComputeChoice choice)
        {
            return choice.Kind;
        }

        return TranscriptionComputeInterface.Cpu;
    }

    private void UpdateComputeInterfaceAvailability(TranscriptionComputeInterface? preferredComputeInterface = null)
    {
        var currentPath = this.ModelPathTextBox.Text.Trim();
        var selectedModelId = this.GetSelectedModelIdForCurrentBackend();
        var choices = TranscriptionRuntimeSupport.GetComputeChoices(
            this.currentBackend,
            selectedModelId,
            currentPath);
        var requestedComputeInterface = preferredComputeInterface ?? this.GetSelectedComputeInterface();
        var selectedChoice =
            choices.FirstOrDefault(choice => choice.Kind == requestedComputeInterface) ??
            choices.FirstOrDefault(choice => choice.Kind == TranscriptionRuntimeSupport.GetBestComputeInterface(
                this.currentBackend,
                selectedModelId,
                currentPath)) ??
            choices[0];

        this.suppressComputeChoiceChanged = true;
        this.ComputeInterfaceComboBox.ItemsSource = choices;
        this.ComputeInterfaceComboBox.SelectedItem = selectedChoice;
        this.suppressComputeChoiceChanged = false;
        this.UpdateComputeInterfaceDescription();
    }

    private string? GetSelectedModelIdForCurrentBackend() =>
        this.currentBackend switch
        {
            TranscriptionBackendKind.Moonshine => (this.ModelChoiceComboBox.SelectedItem as MoonshineModelOption)?.Id,
            TranscriptionBackendKind.Parakeet => (this.ModelChoiceComboBox.SelectedItem as ParakeetModelOption)?.Id,
            TranscriptionBackendKind.QualcommQnn => (this.ModelChoiceComboBox.SelectedItem as QualcommAihubWhisperModelOption)?.Id,
            TranscriptionBackendKind.WhisperNet => (this.ModelChoiceComboBox.SelectedItem as WhisperNetModelOption)?.Id,
            _ => (this.ModelChoiceComboBox.SelectedItem as WhisperModelOption)?.Id
        };

    private void UpdateComputeInterfaceDescription()
    {
        this.ComputeInterfaceDescriptionText.Text = this.ComputeInterfaceComboBox.SelectedItem is TranscriptionComputeChoice choice
            ? choice.Description
            : "Only supported compute configurations are shown for the selected backend and model.";
    }

    private void InitializeStatsTab(AppSettings settings, DictationStatsState? statsState)
    {
        var state = statsState ?? new DictationStatsState();
        var baselineWpm = NormalizeBaselineTypingSpeed(settings.BaselineTypingSpeedWpm);
        var averageWpm = state.TotalAudioSeconds > 0
            ? state.TotalWords / (state.TotalAudioSeconds / 60.0)
            : 0;
        var estimatedTypingTime = TimeSpan.FromMinutes(state.TotalWords / (double)baselineWpm);
        var netTimeSaved = estimatedTypingTime - TimeSpan.FromSeconds(state.TotalAudioSeconds);
        if (netTimeSaved < TimeSpan.Zero)
        {
            netTimeSaved = TimeSpan.Zero;
        }

        this.BaselineTypingSpeedTextBox.Text = baselineWpm.ToString(CultureInfo.InvariantCulture);
        this.StatsTotalWordsText.Text = state.TotalWords.ToString("N0", CultureInfo.InvariantCulture);
        this.StatsTimeSavedText.Text = FormatDuration(netTimeSaved);
        this.StatsAverageWpmText.Text = averageWpm > 0
            ? $"{averageWpm:N0} WPM"
            : "0 WPM";
        this.StatsSessionCountText.Text = state.InjectedSessions.ToString("N0", CultureInfo.InvariantCulture);
        this.DailyStatsItemsControl.ItemsSource = BuildDailyBars(state);
        this.AchievementItemsControl.ItemsSource = BuildAchievementItems(state);
    }

    private static int NormalizeBaselineTypingSpeed(int baselineWpm) =>
        baselineWpm is >= 20 and <= 120
            ? baselineWpm
            : AppSettings.DefaultBaselineTypingSpeedWpm;

    private static IReadOnlyList<StatsDayBar> BuildDailyBars(DictationStatsState state)
    {
        var byDate = state.DailyStats
            .Where(day => !string.IsNullOrWhiteSpace(day.Date))
            .GroupBy(day => day.Date, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(day => day.Words), StringComparer.Ordinal);
        var today = DateTime.Today;
        var days = Enumerable.Range(0, 14)
            .Select(offset => today.AddDays(offset - 13))
            .Select(day =>
            {
                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return (Date: day, Words: byDate.TryGetValue(key, out var words) ? words : 0);
            })
            .ToList();
        var maxWords = Math.Max(1, days.Max(day => day.Words));
        var palette = new[]
        {
            CreateBrush(0x38, 0xBD, 0xF8),
            CreateBrush(0xA7, 0x8B, 0xFA),
            CreateBrush(0x34, 0xD3, 0x99),
            CreateBrush(0xF5, 0x9E, 0x0B)
        };

        return days.Select((day, index) =>
        {
            var barHeight = day.Words == 0
                ? 4
                : Math.Max(8, Math.Round(day.Words / (double)maxWords * 112));
            return new StatsDayBar(
                day.Date.ToString("M/d", CultureInfo.InvariantCulture),
                day.Words,
                barHeight,
                palette[index % palette.Length]);
        }).ToList();
    }

    private static IReadOnlyList<AchievementDisplay> BuildAchievementItems(DictationStatsState state)
    {
        var achievedBrush = CreateBrush(0x34, 0xD3, 0x99);
        var pendingBrush = CreateBrush(0x64, 0x74, 0x8B);
        return DictationStatsStore.Achievements
            .Select(achievement =>
            {
                var unlocked = state.UnlockedAchievementIds.Contains(achievement.Id) ||
                    state.TotalWords >= achievement.WordThreshold;
                var remaining = Math.Max(0, achievement.WordThreshold - state.TotalWords);
                var percent = achievement.WordThreshold == 0
                    ? 100
                    : Math.Min(100, state.TotalWords * 100.0 / achievement.WordThreshold);
                return new AchievementDisplay(
                    achievement.Title,
                    unlocked
                        ? achievement.Message
                        : $"{percent:N0}% complete - {remaining:N0} words to go.",
                    unlocked ? "Unlocked" : "Locked",
                    unlocked ? achievedBrush : pendingBrush);
            })
            .ToList();
    }

    private static MediaBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(MediaColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return "<1 min";
        }

        if (duration.TotalHours < 1)
        {
            return $"{duration.TotalMinutes:N0} min";
        }

        if (duration.TotalDays < 1)
        {
            return $"{duration.TotalHours:N1} hr";
        }

        return $"{duration.TotalDays:N1} days";
    }

    private void OnAddReplacementRuleClick(object sender, RoutedEventArgs e)
    {
        var rule = new TranscriptReplacementRule();
        this.transcriptReplacementRules.Add(rule);
        this.ReplacementRulesDataGrid.SelectedItem = rule;
    }

    private void OnRemoveReplacementRuleClick(object sender, RoutedEventArgs e)
    {
        if (this.ReplacementRulesDataGrid.SelectedItem is TranscriptReplacementRule selected)
        {
            this.transcriptReplacementRules.Remove(selected);
        }
    }

    private void OnAddVoiceShellCommandClick(object sender, RoutedEventArgs e)
    {
        var command = new VoiceShellCommand
        {
            Enabled = true,
            CompletionBehavior = VoiceShellCommandCompletionBehavior.Stop
        };
        this.voiceShellCommands.Add(command);
        this.VoiceShellCommandsDataGrid.SelectedItem = command;
    }

    private void OnRemoveVoiceShellCommandClick(object sender, RoutedEventArgs e)
    {
        if (this.VoiceShellCommandsDataGrid.SelectedItem is VoiceShellCommand selected)
        {
            this.voiceShellCommands.Remove(selected);
        }
    }

    private void InitializeInputDeviceOptions(string? selectedDeviceId)
    {
        var options = new List<InputDeviceOption>
        {
            new(DeviceId: null, Label: "System default microphone")
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in devices)
            {
                options.Add(new InputDeviceOption(device.ID, device.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            options.Add(new InputDeviceOption(null, $"System default microphone (device list unavailable: {ex.Message})"));
        }

        this.InputDeviceComboBox.ItemsSource = options;
        this.InputDeviceComboBox.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.DeviceId, selectedDeviceId, StringComparison.Ordinal))
            ?? options[0];
    }

    private bool TryResolveModelSelectionForSave(out string resolvedModelPath, out string? selectedModelId)
    {
        resolvedModelPath = string.Empty;
        selectedModelId = null;
        var configuredPath = this.ModelPathTextBox.Text.Trim();

        if (this.modelDownloadCts is not null)
        {
            System.Windows.MessageBox.Show(
                this,
                "Wait for the current model download to finish or cancel it before saving.",
                "Model download in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return this.currentBackend switch
        {
            TranscriptionBackendKind.Moonshine => this.TryResolveMoonshineSelectionForSave(configuredPath, out resolvedModelPath, out selectedModelId),
            TranscriptionBackendKind.Parakeet => this.TryResolveParakeetSelectionForSave(configuredPath, out resolvedModelPath, out selectedModelId),
            TranscriptionBackendKind.QualcommQnn => this.TryResolveQualcommAihubWhisperSelectionForSave(configuredPath, out resolvedModelPath, out selectedModelId),
            TranscriptionBackendKind.WhisperNet => this.TryResolveWhisperNetSelectionForSave(configuredPath, out resolvedModelPath, out selectedModelId),
            _ => this.TryResolveWhisperSelectionForSave(configuredPath, out resolvedModelPath, out selectedModelId)
        };
    }

    private static IReadOnlyList<BackendChoice> GetAvailableBackendChoices(TranscriptionBackendKind selectedBackend)
    {
        return AllBackendChoices
            .Where(choice =>
                TranscriptionRuntimeSupport.IsBackendSupportedOnCurrentMachine(choice.Kind) ||
                choice.Kind == selectedBackend)
            .ToArray();
    }

    private bool TryResolveWhisperSelectionForSave(
        string configuredPath,
        out string resolvedModelPath,
        out string? selectedModelId)
    {
        resolvedModelPath = string.Empty;
        selectedModelId = (this.ModelChoiceComboBox.SelectedItem as WhisperModelOption)?.Id;

        if (WhisperModelCatalog.TryResolveDirectory(configuredPath, out var explicitPath))
        {
            resolvedModelPath = explicitPath;
            selectedModelId = WhisperModelCatalog.TryGetByPath(explicitPath)?.Id ?? selectedModelId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "The selected model folder is incomplete. PrimeDictate needs a Whisper ONNX encoder, decoder, and tokens file.",
                "Model folder not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (this.ModelChoiceComboBox.SelectedItem is WhisperModelOption option &&
            WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            resolvedModelPath = installedPath;
            selectedModelId = option.Id;
            this.SetModelPathText(installedPath);
            return true;
        }

        System.Windows.MessageBox.Show(
            this,
            "Choose a Whisper ONNX model to download or browse to an existing model folder before saving.",
            "Model required",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private bool TryResolveParakeetSelectionForSave(
        string configuredPath,
        out string resolvedModelPath,
        out string? selectedModelId)
    {
        resolvedModelPath = string.Empty;
        selectedModelId = (this.ModelChoiceComboBox.SelectedItem as ParakeetModelOption)?.Id;

        if (ParakeetModelCatalog.TryResolveDirectory(configuredPath, out var explicitDirectory))
        {
            resolvedModelPath = explicitDirectory;
            selectedModelId = ParakeetModelCatalog.TryGetByPath(explicitDirectory)?.Id ?? selectedModelId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "The selected model folder is incomplete. PrimeDictate needs encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx, and tokens.txt.",
                "Model folder not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (this.ModelChoiceComboBox.SelectedItem is ParakeetModelOption option &&
            ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            resolvedModelPath = installedPath;
            selectedModelId = option.Id;
            this.SetModelPathText(installedPath);
            return true;
        }

        System.Windows.MessageBox.Show(
            this,
            "Choose a Parakeet model to download or browse to an existing model folder before saving.",
            "Model required",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private bool TryResolveMoonshineSelectionForSave(
        string configuredPath,
        out string resolvedModelPath,
        out string? selectedModelId)
    {
        resolvedModelPath = string.Empty;
        selectedModelId = (this.ModelChoiceComboBox.SelectedItem as MoonshineModelOption)?.Id;

        if (MoonshineModelCatalog.TryResolveDirectory(configuredPath, out var explicitDirectory))
        {
            resolvedModelPath = explicitDirectory;
            selectedModelId = MoonshineModelCatalog.TryGetByPath(explicitDirectory)?.Id ?? selectedModelId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "The selected model folder is incomplete. PrimeDictate needs preprocess.onnx, encode.int8.onnx, uncached_decode.int8.onnx, cached_decode.int8.onnx, and tokens.txt.",
                "Model folder not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (this.ModelChoiceComboBox.SelectedItem is MoonshineModelOption option &&
            MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            resolvedModelPath = installedPath;
            selectedModelId = option.Id;
            this.SetModelPathText(installedPath);
            return true;
        }

        System.Windows.MessageBox.Show(
            this,
            "Choose a Moonshine model to download or browse to an existing model folder before saving.",
            "Model required",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private bool TryResolveQualcommAihubWhisperSelectionForSave(
        string configuredPath,
        out string resolvedModelPath,
        out string? selectedModelId)
    {
        resolvedModelPath = string.Empty;
        selectedModelId = (this.ModelChoiceComboBox.SelectedItem as QualcommAihubWhisperModelOption)?.Id;

        if (QualcommAihubWhisperModelCatalog.TryResolveDirectory(configuredPath, out var explicitDirectory))
        {
            resolvedModelPath = explicitDirectory;
            selectedModelId = QualcommAihubWhisperModelCatalog.TryGetByPath(explicitDirectory)?.Id ?? selectedModelId;
            return true;
        }

        if (QualcommAihubWhisperModelCatalog.IsRawContextOnlyDirectory(configuredPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "That folder contains the raw qnn_context_binary package only. PrimeDictate needs the matching precompiled_qnn_onnx package because ONNX Runtime loads Qualcomm context binaries through encoder.onnx and decoder.onnx wrapper files. Use Download model to install the runnable package.",
                "Raw context package selected",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "The selected model folder is incomplete. PrimeDictate needs encoder.onnx, decoder.onnx, encoder_qairt_context.bin, decoder_qairt_context.bin, metadata.json, and multilingual.tiktoken.",
                "Model folder not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (this.ModelChoiceComboBox.SelectedItem is QualcommAihubWhisperModelOption option &&
            QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            resolvedModelPath = installedPath;
            selectedModelId = option.Id;
            this.SetModelPathText(installedPath);
            return true;
        }

        System.Windows.MessageBox.Show(
            this,
            "Choose a Qualcomm AI Hub Whisper model to download or browse to an extracted precompiled_qnn_onnx model folder before saving.",
            "Model required",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private bool TryResolveWhisperNetSelectionForSave(
        string configuredPath,
        out string resolvedModelPath,
        out string? selectedModelId)
    {
        resolvedModelPath = string.Empty;
        selectedModelId = (this.ModelChoiceComboBox.SelectedItem as WhisperNetModelOption)?.Id;

        if (!string.IsNullOrWhiteSpace(configuredPath) && System.IO.File.Exists(configuredPath))
        {
            resolvedModelPath = configuredPath;
            selectedModelId = WhisperNetModelCatalog.TryGetByPath(configuredPath)?.Id ?? selectedModelId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "The selected model file was not found. PrimeDictate needs a Whisper GGML .bin file.",
                "Model file not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (this.ModelChoiceComboBox.SelectedItem is WhisperNetModelOption option &&
            WhisperNetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            resolvedModelPath = installedPath;
            selectedModelId = option.Id;
            this.SetModelPathText(installedPath);
            return true;
        }

        System.Windows.MessageBox.Show(
            this,
            "Choose a Whisper.net model to download or browse to an existing model file before saving.",
            "Model required",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private void SetModelPathText(string? value)
    {
        this.suppressModelPathTextChanged = true;
        this.ModelPathTextBox.Text = value ?? string.Empty;
        this.suppressModelPathTextChanged = false;
    }

    private void ShowModelDownloadMessage(string message)
    {
        this.ModelDownloadStatusText.Text = message;
        this.ModelDownloadStatusText.Visibility = Visibility.Visible;
    }

    private static HotkeyGesture? BuildCandidateHotkey(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!TryMapWpfKeyToSharpHook(key, out var keyCode))
        {
            return null;
        }

        return new HotkeyGesture
        {
            KeyCode = keyCode,
            Ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            Shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
            Alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
        };
    }

    private void UpdateHotkeyLabels()
    {
        this.DictationHotkeyValueText.Text = this.currentDictationHotkey.ToString();
        this.StopHotkeyValueText.Text = this.currentStopHotkey.ToString();
        this.HistoryHotkeyValueText.Text = this.currentHistoryHotkey.ToString();
    }

    private void ResetCaptureButtonText()
    {
        this.CaptureDictationHotkeyButton.Content = "Change";
        this.CaptureStopHotkeyButton.Content = "Change";
        this.CaptureHistoryHotkeyButton.Content = "Change";
    }

    private LaunchAtLoginScope GetSelectedLaunchAtLoginScope()
    {
        var tag = (this.LaunchAtLoginComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<LaunchAtLoginScope>(tag, out var scope)
            ? scope
            : LaunchAtLoginScope.CurrentUser;
    }

    private static LaunchAtLoginScope ResolveLaunchAtLoginScope(LaunchAtLoginScope configuredScope)
    {
        if (configuredScope != LaunchAtLoginScope.NotConfigured)
        {
            return configuredScope;
        }

        var installedScope = LaunchAtLoginManager.GetConfiguredScope();
        return installedScope == LaunchAtLoginScope.Disabled
            ? LaunchAtLoginScope.CurrentUser
            : installedScope;
    }

    private static int GetLaunchAtLoginComboBoxIndex(LaunchAtLoginScope scope) =>
        scope switch
        {
            LaunchAtLoginScope.Disabled => 0,
            LaunchAtLoginScope.AllUsers => 2,
            _ => 1
        };

    private static string GetHotkeyTargetLabel(HotkeyCaptureTarget target) =>
        target switch
        {
            HotkeyCaptureTarget.Stop => "emergency stop",
            HotkeyCaptureTarget.History => "history",
            _ => "start / stop"
        };

    private static bool TryMapWpfKeyToSharpHook(Key key, out KeyCode keyCode)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            keyCode = Enum.Parse<KeyCode>($"Vc{key}");
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            var n = (int)(key - Key.D0);
            keyCode = Enum.Parse<KeyCode>($"Vc{n}");
            return true;
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            var f = (int)(key - Key.F1) + 1;
            keyCode = Enum.Parse<KeyCode>($"VcF{f}");
            return true;
        }

        if (key == Key.Space)
        {
            keyCode = KeyCode.VcSpace;
            return true;
        }

        if (key == Key.Return)
        {
            keyCode = KeyCode.VcEnter;
            return true;
        }

        if (key == Key.Escape)
        {
            keyCode = KeyCode.VcEscape;
            return true;
        }

        keyCode = default;
        return false;
    }
}
