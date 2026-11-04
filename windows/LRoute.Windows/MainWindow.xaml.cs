using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace LRoute.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProfileItem> _profiles = new();
    private readonly WireGuardEngine _wireGuard = new();
    private ProfileItem? _activeProfile;
    private bool _isConnected;
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

        MessageBox.Show(this,
            imported == 0 ? "The selected profiles were already in your list." : $"Imported {imported} profile(s).",
            "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProfileItem selected)
        {
            MessageBox.Show(this, "Select a WireGuard profile first.", "LRoute",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            if (_isConnected)
            {
                await _wireGuard.DisconnectAsync(selected);
                _isConnected = false;
                _activeProfile = null;
                ConnectionMessage.Text = "Disconnected";
            }
            else
            {
                if (selected.Type != "WireGuard / AmneziaWG")
                    throw new NotSupportedException("Only standard WireGuard profiles are enabled in this build. Xray and AmneziaWG support will be added after their engine adapters are ready.");
                ConnectionMessage.Text = "Connecting… approve the Windows administrator prompt.";
                await _wireGuard.ConnectAsync(selected);
                _isConnected = true;
                _activeProfile = selected;
                ConnectionMessage.Text = $"Connected • {selected.Name}";
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ConnectionMessage.Text = "Administrator permission was cancelled.";
        }
        catch (Exception ex)
        {
            ConnectionMessage.Text = "Connection failed";
            MessageBox.Show(this, ex.Message, "LRoute • WireGuard",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            UpdateEngineStatus();
        }
    }

    private void ProfilesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ConnectButton.IsEnabled = ProfilesList.SelectedItem is ProfileItem;
        if (!_isConnected) return;
        ConnectButton.Content = ReferenceEquals(ProfilesList.SelectedItem, _activeProfile) ? "DISCONNECT" : "CONNECTED";
        ConnectButton.IsEnabled = ReferenceEquals(ProfilesList.SelectedItem, _activeProfile);
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
            foreach (var profile in saved.Where(p => File.Exists(p.FilePath)))
                _profiles.Add(profile.EnsureTunnelName());
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
            var json = JsonSerializer.Serialize(_profiles.ToArray(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProfileIndexPath, json);
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
        var installed = _wireGuard.FindExecutable() is not null;
        EngineStatus.Text = installed ? "WireGuard engine available" : "WireGuard engine not installed";
        ConnectionStatus.Text = _isConnected ? "●  CONNECTED" : "●  DISCONNECTED";
        ConnectButton.Content = _isConnected ? "DISCONNECT" : "CONNECT";
        ConnectionMessage.Text = _isConnected ? $"Connected • {_activeProfile?.Name}" :
            installed ? "Select a WireGuard profile to connect." : "Install WireGuard for Windows to enable VPN connections.";
    }
}

public sealed record ProfileItem(string Name, string Type, string FilePath, string TunnelName = "")
{
    public ProfileItem EnsureTunnelName() =>
        string.IsNullOrWhiteSpace(TunnelName) ? this with { TunnelName = "LRoute-" + Guid.NewGuid().ToString("N")[..12] } : this;
}
