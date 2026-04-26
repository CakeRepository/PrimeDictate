using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SharpHook.Data;

namespace PrimeDictate;

internal partial class SettingsWindow : Window
{
    private sealed record BackendChoice(TranscriptionBackendKind Kind, string Label, string Description);
    private sealed record HotkeyPrimaryOption(string Label, KeyCode KeyCode);

    private static readonly IReadOnlyList<HotkeyPrimaryOption> PrimaryKeyOptions = BuildPrimaryKeyOptions();
    private static readonly IReadOnlyList<BackendChoice> BackendChoices =
    [
        new(
            TranscriptionBackendKind.Whisper,
            "Whisper",
            "Best multilingual coverage and the most polished text quality. PrimeDictate supports the familiar GGML Whisper models you already have."),
        new(
            TranscriptionBackendKind.Parakeet,
            "Parakeet",
            "A newer sherpa-onnx backend for fully local English transcription. Useful for testing non-Whisper accuracy and speed in the same workflow."),
        new(
            TranscriptionBackendKind.Moonshine,
            "Moonshine",
            "A compact sherpa-onnx English backend that favors lightweight local transcription and fast turnaround on Windows.")
    ];

    private bool isCapturingHotkey;
    private bool suppressModelChoiceChanged;
    private bool suppressBackendChoiceChanged;
    private bool suppressModelPathTextChanged;
    private HotkeyGesture currentHotkey;
    private readonly bool isFirstRun;
    private readonly bool isOverlaySticky;
    private CancellationTokenSource? modelDownloadCts;
    private TranscriptionBackendKind currentBackend;

    internal SettingsWindow(AppSettings settings, bool isFirstRun)
    {
        InitializeComponent();
        this.isFirstRun = isFirstRun;
        this.isOverlaySticky = settings.IsOverlaySticky;
        this.currentHotkey = settings.DictationHotkey;
        this.currentBackend = settings.TranscriptionBackend;

        this.PrimaryKeyComboBox.ItemsSource = PrimaryKeyOptions;
        this.PrimaryKeyComboBox.DisplayMemberPath = nameof(HotkeyPrimaryOption.Label);
        this.ModelBackendComboBox.ItemsSource = BackendChoices;
        this.ModelBackendComboBox.DisplayMemberPath = nameof(BackendChoice.Label);
        this.ModelChoiceComboBox.DisplayMemberPath = nameof(WhisperModelOption.DisplayName);

        this.ApplyHotkeyToBuilder(this.currentHotkey);
        this.HotkeyValueText.Text = this.currentHotkey.ToString();
        this.TrayBehaviorComboBox.SelectedIndex = settings.TrayClickBehavior == TrayClickBehavior.SingleClickOpensSettings ? 0 : 1;
        this.ExclusiveMicAccessCheckBox.IsChecked = settings.ExclusiveMicAccessWhileDictating;
        this.AutoCommitSilenceSecondsTextBox.Text = settings.AutoCommitSilenceSeconds.ToString(CultureInfo.InvariantCulture);
        this.SendEnterAfterCommitCheckBox.IsChecked = settings.SendEnterAfterCommit;
        this.ReturnToStartTargetCheckBox.IsChecked = settings.ReturnToStartTargetOnCommit;
        this.PlayAudioCuesCheckBox.IsChecked = settings.PlayAudioCues;
        this.OverlayModeComboBox.SelectedIndex = settings.OverlayMode == OverlayMode.FullPanel ? 1 : 0;
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

        this.WelcomeTab.Header = isFirstRun ? "Welcome" : "Overview";
        this.HeaderText.Text = isFirstRun ? "PrimeDictate first-run setup" : "PrimeDictate settings";
        this.WelcomeFooterText.Text = isFirstRun
            ? "Work through the tabs from left to right. When you finish, PrimeDictate is ready to dictate into Windows apps."
            : "You can switch models or tweak dictation behavior here whenever your workflow changes.";
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

    private void InitializeModelSelection(AppSettings settings)
    {
        var backendChoice = BackendChoices.FirstOrDefault(choice => choice.Kind == settings.TranscriptionBackend)
            ?? BackendChoices.First(choice => choice.Kind == TranscriptionBackendKind.Whisper);

        this.suppressBackendChoiceChanged = true;
        this.ModelBackendComboBox.SelectedItem = backendChoice;
        this.suppressBackendChoiceChanged = false;

        this.ApplyBackendSelection(
            backendChoice.Kind,
            settings.SelectedModelId,
            settings.ModelPath);
    }

    private void ApplyBackendSelection(
        TranscriptionBackendKind backend,
        string? preferredModelId,
        string? configuredModelPath)
    {
        this.currentBackend = backend;
        object selectedOption = backend switch
        {
            TranscriptionBackendKind.Moonshine => SelectMoonshineModel(preferredModelId, configuredModelPath),
            TranscriptionBackendKind.Parakeet => SelectParakeetModel(preferredModelId, configuredModelPath),
            _ => SelectWhisperModel(preferredModelId, configuredModelPath)
        };

        this.suppressModelChoiceChanged = true;
        this.ModelChoiceComboBox.SelectedItem = null;
        this.ModelChoiceComboBox.ItemsSource = null;
        this.ModelChoiceComboBox.ItemsSource = backend switch
        {
            TranscriptionBackendKind.Moonshine => MoonshineModelCatalog.Options,
            TranscriptionBackendKind.Parakeet => ParakeetModelCatalog.Options,
            _ => WhisperModelCatalog.Options
        };
        this.ModelChoiceComboBox.SelectedItem = selectedOption;
        this.suppressModelChoiceChanged = false;

        var initialPath = backend switch
        {
            TranscriptionBackendKind.Moonshine => ResolveInitialMoonshinePath(selectedOption as MoonshineModelOption, configuredModelPath),
            TranscriptionBackendKind.Parakeet => ResolveInitialParakeetPath(selectedOption as ParakeetModelOption, configuredModelPath),
            _ => ResolveInitialWhisperPath(selectedOption as WhisperModelOption, configuredModelPath)
        };

        this.SetModelPathText(initialPath);
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

    private static string ResolveInitialWhisperPath(WhisperModelOption? option, string? configuredModelPath)
    {
        if (ModelFileLocator.TryResolveExactPath(configuredModelPath, out var resolvedConfiguredPath))
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

    private void OnCaptureHotkeyClick(object sender, RoutedEventArgs e)
    {
        this.isCapturingHotkey = true;
        this.CaptureButton.Content = "Press keys...";
        this.HotkeyHintText.Text = "Press the desired key combination now.";
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
            this.HotkeyHintText.Text = "Unsupported key. Try letters, digits, Space, or F1-F12.";
            return;
        }

        if (!candidate.IsValid(out var error))
        {
            this.HotkeyHintText.Text = error;
            return;
        }

        this.currentHotkey = candidate;
        this.ApplyHotkeyToBuilder(candidate);
        this.HotkeyValueText.Text = candidate.ToString();
        this.HotkeyHintText.Text = "Hotkey captured.";
        this.CaptureButton.Content = "Capture hotkey";
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
                case TranscriptionBackendKind.Parakeet:
                    await this.DownloadSelectedParakeetModelAsync(this.modelDownloadCts.Token).ConfigureAwait(true);
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
            case TranscriptionBackendKind.Parakeet:
                this.BrowseParakeetModelPath();
                break;
            default:
                this.BrowseWhisperModelPath();
                break;
        }
    }

    private void BrowseWhisperModelPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Whisper model file",
            Filter = "Whisper model (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        this.SetModelPathText(dialog.FileName);
        var matchingCatalogOption = WhisperModelCatalog.TryGetByPath(dialog.FileName);
        if (matchingCatalogOption is not null)
        {
            this.suppressModelChoiceChanged = true;
            this.ModelChoiceComboBox.SelectedItem = matchingCatalogOption;
            this.suppressModelChoiceChanged = false;
        }

        this.ShowModelDownloadMessage($"Using model file: {dialog.FileName}");
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
            case TranscriptionBackendKind.Parakeet:
                this.OnParakeetModelChoiceChanged();
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

    private void OnHotkeyBuilderChanged(object sender, RoutedEventArgs e)
    {
        if (this.isCapturingHotkey)
        {
            return;
        }

        var candidate = this.BuildHotkeyFromBuilder();
        this.currentHotkey = candidate;
        this.HotkeyValueText.Text = candidate.ToString();
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
            this.HeaderSubtextText.Text = "Manage your local model, hotkey, and dictation behavior.";
            this.BackButton.Visibility = Visibility.Collapsed;
            this.CancelButton.Visibility = Visibility.Visible;
            this.HistoryButton.Visibility = Visibility.Visible;
            this.SaveButton.Content = "Save";
            this.SaveButton.IsEnabled = this.modelDownloadCts is null;
            return;
        }

        var stepIndex = Math.Clamp(this.SetupTabControl.SelectedIndex, 0, this.SetupTabControl.Items.Count - 1);
        var stepNumber = stepIndex + 1;
        var stepLabel = stepIndex switch
        {
            0 => "Welcome",
            1 => "Choose your model",
            _ => "Tune dictation behavior"
        };

        this.HeaderSubtextText.Text = $"Step {stepNumber} of {this.SetupTabControl.Items.Count}: {stepLabel}";
        this.BackButton.Visibility = Visibility.Visible;
        this.BackButton.IsEnabled = stepIndex > 0 && this.modelDownloadCts is null;
        this.CancelButton.Visibility = Visibility.Collapsed;
        this.HistoryButton.Visibility = Visibility.Collapsed;
        this.SaveButton.Content = stepIndex == this.SetupTabControl.Items.Count - 1 ? "Finish" : "Next";
        this.SaveButton.IsEnabled = this.modelDownloadCts is null;
    }

    private void UpdateBackendUi()
    {
        var backendChoice = BackendChoices.FirstOrDefault(choice => choice.Kind == this.currentBackend);
        this.ModelBackendDescriptionText.Text = backendChoice?.Description ?? string.Empty;
        this.BrowseModelPathButton.Content = this.currentBackend is TranscriptionBackendKind.Parakeet or TranscriptionBackendKind.Moonshine
            ? "Browse folder..."
            : "Browse...";
        this.ModelPathLabelText.Text = this.currentBackend is TranscriptionBackendKind.Parakeet or TranscriptionBackendKind.Moonshine
            ? "Resolved model folder path"
            : "Resolved model file path";
        this.ModelStorageHintText.Text = this.currentBackend switch
        {
            TranscriptionBackendKind.Parakeet => $"Downloaded Parakeet models are stored in {ParakeetModelCatalog.GetManagedModelsDirectory()}.",
            TranscriptionBackendKind.Moonshine => $"Downloaded Moonshine models are stored in {MoonshineModelCatalog.GetManagedModelsDirectory()}.",
            _ => $"Downloaded Whisper models are stored in {ModelFileLocator.GetManagedModelsDirectory()}."
        };
    }

    private void UpdateModelSelectionUi()
    {
        switch (this.currentBackend)
        {
            case TranscriptionBackendKind.Moonshine:
                this.UpdateMoonshineModelSelectionUi();
                return;
            case TranscriptionBackendKind.Parakeet:
                this.UpdateParakeetModelSelectionUi();
                return;
            default:
                this.UpdateWhisperModelSelectionUi();
                return;
        }
    }

    private void UpdateWhisperModelSelectionUi()
    {
        if (this.ModelChoiceComboBox.SelectedItem is not WhisperModelOption option)
        {
            this.ModelChoiceTitleText.Text = "Select a Whisper model";
            this.ModelChoiceMetaText.Text = string.Empty;
            this.ModelChoiceDescriptionText.Text = string.Empty;
            this.ModelChoiceStatusText.Text = "Pick a Whisper model to download or browse to an existing .bin file.";
            this.RecommendedBadgeBorder.Visibility = Visibility.Collapsed;
            this.DownloadSelectedModelButton.IsEnabled = false;
            return;
        }

        this.ModelChoiceTitleText.Text = option.DisplayName;
        this.ModelChoiceMetaText.Text = $"Approx. {option.ApproximateSizeLabel} download";
        this.ModelChoiceDescriptionText.Text = option.Description;
        this.RecommendedBadgeBorder.Visibility = option.Recommended ? Visibility.Visible : Visibility.Collapsed;

        var currentPath = this.ModelPathTextBox.Text.Trim();
        var hasConfiguredPath = ModelFileLocator.TryResolveExactPath(currentPath, out var resolvedConfiguredPath);
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
                this.ModelChoiceStatusText.Text = $"Using a custom model file: {resolvedConfiguredPath}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentPath))
        {
            this.ModelChoiceStatusText.Text = "The model path in the textbox does not exist yet.";
        }
        else if (isInstalled)
        {
            this.ModelChoiceStatusText.Text = $"Installed on this PC: {installedPath}";
        }
        else
        {
            this.ModelChoiceStatusText.Text = $"Not downloaded yet. Download {option.DisplayName} or browse to an existing .bin file.";
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

    private bool TryBuildSettings(out AppSettings settings)
    {
        settings = null!;

        var candidate = this.BuildHotkeyFromBuilder();
        if (!candidate.IsValid(out var hotkeyError))
        {
            System.Windows.MessageBox.Show(this, hotkeyError, "Invalid hotkey", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            autoCommitSeconds is < 1 or > 30)
        {
            System.Windows.MessageBox.Show(
                this,
                "Auto-commit silence must be a whole number from 1 to 30 seconds.",
                "Invalid auto-commit delay",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var selectedBehavior = ((ComboBoxItem)this.TrayBehaviorComboBox.SelectedItem).Tag?.ToString();
        var selectedOverlayMode = ((ComboBoxItem)this.OverlayModeComboBox.SelectedItem).Tag?.ToString();
        this.currentHotkey = candidate;

        var selectedOllamaModeStr = ((ComboBoxItem)this.OllamaModeComboBox.SelectedItem)?.Tag?.ToString() ?? "Default";
        if (!Enum.TryParse<OllamaMode>(selectedOllamaModeStr, out var selectedOllamaMode))
        {
            selectedOllamaMode = OllamaMode.Default;
        }

        settings = new AppSettings
        {
            FirstRunCompleted = true,
            DictationHotkey = this.currentHotkey,
            TrayClickBehavior = selectedBehavior == "Single"
                ? TrayClickBehavior.SingleClickOpensSettings
                : TrayClickBehavior.DoubleClickOpensSettings,
            TranscriptionBackend = this.currentBackend,
            SelectedModelId = selectedModelId,
            ModelPath = resolvedModelPath,
            ExclusiveMicAccessWhileDictating = this.ExclusiveMicAccessCheckBox.IsChecked == true,
            AutoCommitSilenceSeconds = autoCommitSeconds,
            SendEnterAfterCommit = this.SendEnterAfterCommitCheckBox.IsChecked == true,
            ReturnToStartTargetOnCommit = this.ReturnToStartTargetCheckBox.IsChecked == true,
            PlayAudioCues = this.PlayAudioCuesCheckBox.IsChecked != false,
            OverlayMode = selectedOverlayMode == "Full"
                ? OverlayMode.FullPanel
                : OverlayMode.CompactMicrophone,
            IsOverlaySticky = this.isOverlaySticky,
            EnableOllamaPostProcessing = this.EnableOllamaCheckBox.IsChecked == true,
            OllamaEndpoint = this.OllamaEndpointTextBox.Text.Trim(),
            OllamaModel = this.OllamaModelTextBox.Text.Trim(),
            OllamaMode = selectedOllamaMode
        };

        return true;
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
            _ => this.TryResolveWhisperSelectionForSave(configuredPath, out resolvedModelPath, out selectedModelId)
        };
    }

    private bool TryResolveWhisperSelectionForSave(
        string configuredPath,
        out string resolvedModelPath,
        out string? selectedModelId)
    {
        resolvedModelPath = string.Empty;
        selectedModelId = (this.ModelChoiceComboBox.SelectedItem as WhisperModelOption)?.Id;

        if (ModelFileLocator.TryResolveExactPath(configuredPath, out var explicitPath))
        {
            resolvedModelPath = explicitPath;
            selectedModelId = WhisperModelCatalog.TryGetByPath(explicitPath)?.Id ?? selectedModelId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "The selected model path does not exist. Download the model first or browse to an existing Whisper .bin file.",
                "Model file not found",
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
            "Choose a Whisper model to download or browse to an existing Whisper .bin file before saving.",
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

    private HotkeyGesture BuildHotkeyFromBuilder()
    {
        var selectedOption = this.PrimaryKeyComboBox.SelectedItem as HotkeyPrimaryOption;
        var primaryKey = selectedOption?.KeyCode ?? KeyCode.VcSpace;

        return new HotkeyGesture
        {
            KeyCode = primaryKey,
            Ctrl = this.CtrlModifierCheckBox.IsChecked == true,
            Shift = this.ShiftModifierCheckBox.IsChecked == true,
            Alt = this.AltModifierCheckBox.IsChecked == true
        };
    }

    private void ApplyHotkeyToBuilder(HotkeyGesture hotkey)
    {
        this.CtrlModifierCheckBox.IsChecked = hotkey.Ctrl;
        this.ShiftModifierCheckBox.IsChecked = hotkey.Shift;
        this.AltModifierCheckBox.IsChecked = hotkey.Alt;
        this.PrimaryKeyComboBox.SelectedItem = PrimaryKeyOptions.FirstOrDefault(option => option.KeyCode == hotkey.KeyCode);
    }

    private static IReadOnlyList<HotkeyPrimaryOption> BuildPrimaryKeyOptions()
    {
        var options = new List<HotkeyPrimaryOption>
        {
            new("Space", KeyCode.VcSpace)
        };

        for (var c = 'A'; c <= 'Z'; c++)
        {
            var keyCode = Enum.Parse<KeyCode>($"Vc{c}");
            options.Add(new HotkeyPrimaryOption(c.ToString(), keyCode));
        }

        for (var i = 0; i <= 9; i++)
        {
            var keyCode = Enum.Parse<KeyCode>($"Vc{i}");
            options.Add(new HotkeyPrimaryOption(i.ToString(), keyCode));
        }

        for (var i = 1; i <= 12; i++)
        {
            var keyCode = Enum.Parse<KeyCode>($"VcF{i}");
            options.Add(new HotkeyPrimaryOption($"F{i}", keyCode));
        }

        return options;
    }

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

        keyCode = default;
        return false;
    }
}
