using System.Text;
using System.Windows;

namespace PrimeDictate;

internal partial class HistoryWindow : Window
{
    internal HistoryWindow(TranscriptionHistoryViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
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
        sb.AppendLine($"Target: {entry.TargetDisplayName ?? "Unknown"}");
        sb.AppendLine($"Audio seconds: {entry.AudioDurationSeconds:N1}");
        if (!string.IsNullOrWhiteSpace(entry.Error))
        {
            sb.AppendLine($"Error: {entry.Error}");
        }

        sb.AppendLine();
        sb.AppendLine(entry.Transcript);
        System.Windows.Clipboard.SetText(sb.ToString());
    }
}
