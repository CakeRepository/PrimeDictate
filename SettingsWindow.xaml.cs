using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SharpHook.Data;

namespace PrimeDictate;

internal partial class SettingsWindow : Window
{
    private sealed record HotkeyPrimaryOption(string Label, KeyCode KeyCode);

    private static readonly IReadOnlyList<HotkeyPrimaryOption> PrimaryKeyOptions = BuildPrimaryKeyOptions();

    private bool isCapturingHotkey;
    private HotkeyGesture currentHotkey;
    private readonly bool isFirstRun;

    internal SettingsWindow(AppSettings settings, bool isFirstRun)
    {
        InitializeComponent();
        this.isFirstRun = isFirstRun;
        this.currentHotkey = settings.DictationHotkey;
        this.PrimaryKeyComboBox.ItemsSource = PrimaryKeyOptions;
        this.PrimaryKeyComboBox.DisplayMemberPath = nameof(HotkeyPrimaryOption.Label);
        this.ApplyHotkeyToBuilder(this.currentHotkey);
        this.HotkeyValueText.Text = this.currentHotkey.ToString();
        this.HeaderText.Text = isFirstRun ? "PrimeDictate first-run setup" : "PrimeDictate settings";
        this.HistoryButton.Visibility = isFirstRun ? Visibility.Collapsed : Visibility.Visible;
        this.CancelButton.Visibility = isFirstRun ? Visibility.Collapsed : Visibility.Visible;
        this.TrayBehaviorComboBox.SelectedIndex = settings.TrayClickBehavior == TrayClickBehavior.SingleClickOpensSettings ? 0 : 1;
        this.ModelPathTextBox.Text = settings.ModelPath ?? string.Empty;
        this.ExclusiveMicAccessCheckBox.IsChecked = settings.ExclusiveMicAccessWhileDictating;
        this.AutoCommitSilenceSecondsTextBox.Text = settings.AutoCommitSilenceSeconds.ToString(CultureInfo.InvariantCulture);
        this.SendEnterAfterCommitCheckBox.IsChecked = settings.SendEnterAfterCommit;
    }

    internal event Action<AppSettings>? SettingsSaved;
    internal event Action? HistoryRequested;

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

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var candidate = this.BuildHotkeyFromBuilder();
        if (!candidate.IsValid(out var error))
        {
            System.Windows.MessageBox.Show(this, error, "Invalid hotkey", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        this.currentHotkey = candidate;

        var selectedBehavior = ((ComboBoxItem)this.TrayBehaviorComboBox.SelectedItem).Tag?.ToString();
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
            return;
        }

        var modelPath = this.ModelPathTextBox.Text.Trim();
        var settings = new AppSettings
        {
            FirstRunCompleted = true,
            DictationHotkey = this.currentHotkey,
            TrayClickBehavior = selectedBehavior == "Single"
                ? TrayClickBehavior.SingleClickOpensSettings
                : TrayClickBehavior.DoubleClickOpensSettings,
            ModelPath = string.IsNullOrWhiteSpace(modelPath) ? null : modelPath,
            ExclusiveMicAccessWhileDictating = this.ExclusiveMicAccessCheckBox.IsChecked == true,
            AutoCommitSilenceSeconds = autoCommitSeconds,
            SendEnterAfterCommit = this.SendEnterAfterCommitCheckBox.IsChecked == true
        };

        this.SettingsSaved?.Invoke(settings);
        this.Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (this.isFirstRun)
        {
            return;
        }

        this.Close();
    }

    private void OnBrowseModelPathClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Whisper model file",
            Filter = "Whisper model (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            this.ModelPathTextBox.Text = dialog.FileName;
        }
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
