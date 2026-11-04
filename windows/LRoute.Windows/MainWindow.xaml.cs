using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace LRoute.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProfileItem> _profiles = new();
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LRoute");
    private static readonly string ProfileIndexPath = Path.Combine(DataDirectory, "profiles.json");

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        LoadProfiles();
        RefreshProfileView();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import VPN profile",
            Filter = "VPN profiles (*.conf;*.json;*.txt)|*.conf;*.json;*.txt|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        var imported = 0;
        foreach (var file in dialog.FileNames)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var type = extension switch
            {
                ".conf" => "WireGuard / AmneziaWG",
                ".json" => "Xray JSON",
                ".txt" => "Text configuration",
                _ => "Unknown configuration"
            };

            if (_profiles.Any(p => string.Equals(p.FilePath, file, StringComparison.OrdinalIgnoreCase)))
                continue;

            _profiles.Add(new ProfileItem(Path.GetFileNameWithoutExtension(file), type, file));
            imported++;
        }

        SaveProfiles();
        RefreshProfileView();

        MessageBox.Show(
            this,
            imported == 0 ? "The selected profiles were already in your list." : $"Imported {imported} profile(s).",
            "LRoute",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileItem profile) return;
        _profiles.Remove(profile);
        SaveProfiles();
        RefreshProfileView();
    }

    private void LoadProfiles()
    {
        try
        {
            if (!File.Exists(ProfileIndexPath)) return;
            var saved = JsonSerializer.Deserialize<List<ProfileItem>>(File.ReadAllText(ProfileIndexPath));
            if (saved is null) return;

            foreach (var profile in saved.Where(p => File.Exists(p.FilePath)))
                _profiles.Add(profile);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Saved profile list could not be loaded.\\n{ex.Message}",
                "LRoute", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(_profiles.ToArray(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProfileIndexPath, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Profile list could not be saved.\\n{ex.Message}",
                "LRoute", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshProfileView()
    {
        var hasProfiles = _profiles.Count > 0;
        EmptyState.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
        ProfilesList.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
    }
}

public sealed record ProfileItem(string Name, string Type, string FilePath);
