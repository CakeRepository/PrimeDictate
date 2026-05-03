using System.Text;
using System.Windows;

namespace PrimeDictate;

internal partial class HistoryWindow : Window
{
    internal HistoryWindow(TranscriptionHistoryViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
        this.Loaded += (_, _) => this.SearchTextBox.Focus();
    }

    private void OnCopyTranscriptClick(object sender, RoutedEventArgs e)
    {
        if (this.DataContext is not TranscriptionHistoryViewModel { SelectedEntry: { } entry })
        {
            return;
        }

        System.Windows.Clipboard.SetText(entry.Transcript);
    }

    private void OnCopyDetailsClick(object sender, RoutedEventArgs e)
    {
        if (this.DataContext is not TranscriptionHistoryViewModel { SelectedEntry: { } entry })
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Timestamp (UTC): {entry.TimestampUtc:O}");
        sb.AppendLine($"Thread: {entry.ThreadId}");
        sb.AppendLine($"Delivery: {entry.DeliveryStatus}");
        sb.AppendLine($"Target app: {entry.TargetAppDisplayName}");
        sb.AppendLine($"Target window: {entry.TargetWindowDisplayName}");
        sb.AppendLine($"Audio seconds: {entry.AudioDurationSeconds:N1}");
        if (!string.IsNullOrWhiteSpace(entry.Error))
        {
            sb.AppendLine($"Error: {entry.Error}");
        }

        if (!string.IsNullOrWhiteSpace(entry.OriginalTranscript))
        {
            sb.AppendLine();
            sb.AppendLine("Original Transcript:");
            sb.AppendLine(entry.OriginalTranscript);
            sb.AppendLine();
            sb.AppendLine("Ollama System Prompt:");
            sb.AppendLine(entry.OllamaSystemPrompt);
            sb.AppendLine();
            sb.AppendLine("Final Injected Transcript:");
        }

        sb.AppendLine();
        sb.AppendLine(entry.Transcript);
        System.Windows.Clipboard.SetText(sb.ToString());
    }

    private void OnClearRetrievalClick(object sender, RoutedEventArgs e)
    {
        if (this.DataContext is TranscriptionHistoryViewModel viewModel)
        {
            viewModel.ClearRetrievalFilters();
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
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
}
