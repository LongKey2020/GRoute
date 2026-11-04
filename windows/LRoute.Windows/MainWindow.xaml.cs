using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace LRoute.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProfileItem> _profiles = new();
    private ProfileItem? _activeProfile;
    private bool _isConnected;

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LRoute");
    private static readonly string ProfileIndexPath = Path.Combine(DataDirectory, "profiles.json");

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        LoadProfiles();
        RefreshProfileView();
        UpdateEngineStatus();
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
                ".conf" => DetectWireGuardType(file),
                ".json" => "Xray JSON (engine pending)",
                ".txt" => "Text configuration (engine pending)",
                _ => "Unknown configuration"
            };
            if (type == "WireGuard")
                type = "WireGuard (temporarily disabled)";

            if (_profiles.Any(p => string.Equals(p.FilePath, file, StringComparison.OrdinalIgnoreCase)))
                continue;

            _profiles.Add(new ProfileItem(
                Path.GetFileNameWithoutExtension(file), type, file,
                "LRoute-" + Guid.NewGuid().ToString("N")[..12]));
            imported++;
        }

        SaveProfiles();
        RefreshProfileView();
        MessageBox.Show(this,
            imported == 0 ? "The selected profiles were already in your list." : $"Imported {imported} profile(s).",
            "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "WireGuard support is temporarily disabled while the Windows tunnel engine is being reworked.",
            "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedProfileState();
    }

    private void UpdateSelectedProfileState()
    {
        ConnectButton.Content = "UNAVAILABLE";
        ConnectButton.IsEnabled = false;
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileItem profile) return;
        if (ReferenceEquals(profile, _activeProfile))
        {
            MessageBox.Show(this, "Disconnect this profile before removing it.", "LRoute",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
            foreach (var item in saved.Where(p => File.Exists(p.FilePath)))
                _profiles.Add(item.EnsureTunnelName());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Saved profile list could not be loaded.\n{ex.Message}",
                "LRoute", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(ProfileIndexPath,
                JsonSerializer.Serialize(_profiles.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Profile list could not be saved.\n{ex.Message}",
                "LRoute", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshProfileView()
    {
        var hasProfiles = _profiles.Count > 0;
        EmptyState.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
        ProfilesList.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEngineStatus()
    {
        EngineStatus.Text = "WireGuard temporarily disabled";
        ConnectionStatus.Text = "●  DISCONNECTED";
        ConnectionMessage.Text = "WireGuard support is temporarily paused.";
        UpdateSelectedProfileState();
    }

    private static string DetectWireGuardType(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var awgKeys = new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4" };
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
                var key = trimmed.Split('=', 2)[0].Trim();
                if (awgKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    return "AmneziaWG (engine pending)";
            }
            return text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
                ? "WireGuard" : "Unknown .conf";
        }
        catch { return "Unreadable configuration"; }
    }
}

public sealed record ProfileItem(string Name, string Type, string FilePath, string TunnelName = "")
{
    public ProfileItem EnsureTunnelName() =>
        string.IsNullOrWhiteSpace(TunnelName)
            ? this with { TunnelName = "LRoute-" + Guid.NewGuid().ToString("N")[..12] }
            : this;
}
