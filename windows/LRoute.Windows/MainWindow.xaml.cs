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
    private readonly WireGuardEngine _wireGuard = new();
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
                ".json" => "Xray JSON",
                ".txt" => "Text configuration",
                _ => "Unknown configuration"
            };

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

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProfileItem selected) return;
        ConnectButton.IsEnabled = false;
        try
        {
            if (_isConnected && ReferenceEquals(selected, _activeProfile))
            {
                await _wireGuard.DisconnectAsync(selected);
                _isConnected = false;
                _activeProfile = null;
                ConnectionMessage.Text = "Disconnected";
            }
            else
            {
                if (selected.Type != "WireGuard")
                    throw new NotSupportedException(
                        "This profile format is not enabled yet. Standard WireGuard is supported first; AmneziaWG and Xray adapters are next.");

                ConnectionMessage.Text = "Connecting… approve the Windows administrator prompt.";
                await _wireGuard.ConnectAsync(selected);
                _activeProfile = selected;
                _isConnected = true;
                ConnectionMessage.Text = $"Connected • {selected.Name}";
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ConnectionMessage.Text = "Administrator permission was cancelled.";
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _activeProfile = null;
            ConnectionMessage.Text = "Connection failed";
            MessageBox.Show(this, ex.Message, "LRoute • WireGuard",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            UpdateEngineStatus();
            UpdateSelectedProfileState();
        }
    }

    private async void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedProfileState();
        if (ProfilesList.SelectedItem is not ProfileItem selected || _isConnected) return;
        try
        {
            if (await _wireGuard.IsConnectedAsync(selected))
            {
                _activeProfile = selected;
                _isConnected = true;
                UpdateEngineStatus();
                UpdateSelectedProfileState();
            }
        }
        catch { /* A status refresh must not interrupt profile selection. */ }
    }

    private void UpdateSelectedProfileState()
    {
        var selected = ProfilesList.SelectedItem as ProfileItem;
        var isActive = selected is not null && ReferenceEquals(selected, _activeProfile);
        ConnectButton.Content = isActive && _isConnected ? "DISCONNECT" : "CONNECT";
        ConnectButton.IsEnabled = selected is not null && (!_isConnected || isActive);
        if (selected is not null && selected.Type != "WireGuard" && !_isConnected)
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
        var installed = _wireGuard.FindExecutable() is not null;
        EngineStatus.Text = installed ? "WireGuard engine available" : "WireGuard engine not installed";
        ConnectionStatus.Text = _isConnected ? "●  CONNECTED" : "●  DISCONNECTED";
        ConnectionMessage.Text = _isConnected
            ? $"Connected • {_activeProfile?.Name}"
            : installed ? "Select a WireGuard profile to connect."
            : "Install WireGuard for Windows to enable VPN connections.";
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
