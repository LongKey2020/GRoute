using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace LRoute.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProfileItem> _profiles = new();

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import VPN profile",
            Filter = "VPN profiles (*.conf;*.json;*.txt)|*.conf;*.json;*.txt|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var type = extension switch
            {
                ".conf" => "WireGuard / AmneziaWG configuration",
                ".json" => "JSON configuration (Xray or compatible)",
                _ => "Text configuration"
            };
            _profiles.Add(new ProfileItem(Path.GetFileNameWithoutExtension(file), type, file));
        }

        EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesList.Visibility = _profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

public sealed record ProfileItem(string Name, string Type, string FilePath);
