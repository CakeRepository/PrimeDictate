using System.Windows;

namespace PrimeDictate;

internal partial class MainWindow : Window
{
    internal MainWindow(DictationWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ShowSettings();
        }
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ShowHistory();
        }
    }
}
