using System.Windows;
using System.Windows.Controls;
using GameMoments.App.ViewModels;

namespace GameMoments.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private static MainWindow? Host => System.Windows.Application.Current.MainWindow as MainWindow;

    private async void Capture_Click(object sender, RoutedEventArgs e) => await Vm.ToggleCaptureAsync();

    private async void Save_Click(object sender, RoutedEventArgs e) => await Vm.SaveReplayAsync();

    private async void Record_Click(object sender, RoutedEventArgs e) => await Vm.ToggleRecordAsync();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Host?.Navigate("settings");

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Host?.Navigate("logs");

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => Host?.Navigate("library");

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Vm.OpenFolder(Vm.Settings.SaveFolder);
}
