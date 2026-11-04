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
    private readonly XrayEngine _xray = new();
    private readonly WireGuardEngine _wireGuard = new();
    private ProfileItem? _activeProfile;
    private ProfileItem? _activeWireGuardProfile;

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
                ".txt" => "Text configuration (engine pending)",
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
            if (selected.Type == "WireGuard")
            {
                if (ReferenceEquals(selected, _activeWireGuardProfile))
                {
                    await _wireGuard.DisconnectAsync(selected);
                    _activeWireGuardProfile = null;
                    ConnectionMessage.Text = "WireGuard tunnel disconnected.";
                }
                else
                {
                    if (_xray.IsRunning)
                    {
                        await _xray.DisconnectAsync();
                        _activeProfile = null;
                    }
                    if (_activeWireGuardProfile is not null)
                    {
                        await _wireGuard.DisconnectAsync(_activeWireGuardProfile);
                        _activeWireGuardProfile = null;
                    }

                    ConnectionMessage.Text = "Requesting administrator approval and starting WireGuard tunnel…";
                    await _wireGuard.ConnectAsync(selected);
                    if (!await _wireGuard.IsConnectedAsync(selected))
                        throw new InvalidOperationException("WireGuard did not report a RUNNING tunnel service.");
                    _activeWireGuardProfile = selected;
                    ConnectionMessage.Text = $"WireGuard connected • {selected.Name}";
                }
            }
            else if (selected.Type == "Xray JSON")
            {
                if (_xray.IsRunning && ReferenceEquals(selected, _activeProfile))
                {
                    await _xray.DisconnectAsync();
                    _activeProfile = null;
                    ConnectionMessage.Text = "Xray-core stopped.";
                }
                else
                {
                    if (_activeWireGuardProfile is not null)
                    {
                        await _wireGuard.DisconnectAsync(_activeWireGuardProfile);
                        _activeWireGuardProfile = null;
                    }
                    ConnectionMessage.Text = "Validating Xray JSON and starting engine…";
                    await _xray.ConnectAsync(selected.FilePath);
                    _activeProfile = selected;
                    ConnectionMessage.Text = $"Xray-core running • {selected.Name} (local proxy mode)";
                }
            }
            else
            {
                throw new NotSupportedException(
                    "This profile type is not enabled yet. Standard WireGuard and Xray JSON are supported in this build.");
            }
        }
        catch (Exception ex)
        {
            ConnectionMessage.Text = "Connection failed";
            MessageBox.Show(this, ex.Message, "LRoute • Connection",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateEngineStatus();
            UpdateSelectedProfileState();
        }
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedProfileState();
    }

    private void UpdateSelectedProfileState()
    {
        var selected = ProfilesList.SelectedItem as ProfileItem;
        var activeXray = _xray.IsRunning && selected is not null && ReferenceEquals(selected, _activeProfile);
        var activeWireGuard = selected is not null && ReferenceEquals(selected, _activeWireGuardProfile);
        var active = activeXray || activeWireGuard;
        ConnectButton.Content = active ? "DISCONNECT" : "CONNECT";
        ConnectButton.IsEnabled = selected?.Type is "Xray JSON" or "WireGuard" || active;
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
            {
                var normalized = item.Type == "Xray JSON (engine pending)"
                    ? item with { Type = "Xray JSON" }
                    : item;
                _profiles.Add(normalized.EnsureTunnelName());
            }
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
        if (_activeWireGuardProfile is not null)
        {
            EngineStatus.Text = "WireGuard tunnel service: RUNNING";
            ConnectionStatus.Text = "●  CONNECTED";
            ConnectionMessage.Text = $"WireGuard connected • {_activeWireGuardProfile.Name}";
        }
        else if (_xray.IsRunning)
        {
            EngineStatus.Text = "Xray-core process running";
            ConnectionStatus.Text = "●  ENGINE RUNNING";
            ConnectionMessage.Text = $"Xray-core running • {_activeProfile?.Name} (local proxy mode)";
        }
        else
        {
            EngineStatus.Text = _xray.FindExecutable() is not null
                ? "Xray-core ready"
                : "Xray-core runtime missing";
            ConnectionStatus.Text = "●  DISCONNECTED";
            ConnectionMessage.Text = "Select a supported profile to start a connection.";
        }
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

    protected override void OnClosed(EventArgs e)
    {
        if (_activeWireGuardProfile is not null)
        {
            try { _wireGuard.DisconnectAsync(_activeWireGuardProfile).GetAwaiter().GetResult(); }
            catch { /* Leave Windows service diagnostics available if shutdown cleanup fails. */ }
        }
        _xray.Dispose();
        base.OnClosed(e);
    }
}

public sealed record ProfileItem(string Name, string Type, string FilePath, string TunnelName = "")
{
    public ProfileItem EnsureTunnelName() =>
        string.IsNullOrWhiteSpace(TunnelName)
            ? this with { TunnelName = "LRoute-" + Guid.NewGuid().ToString("N")[..12] }
            : this;
}
