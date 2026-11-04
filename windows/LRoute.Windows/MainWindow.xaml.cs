using Microsoft.Win32;
using System.ComponentModel;
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
    private ProfileItem? _activeProfile;

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

    private void ProfileSelector_Click(object sender, RoutedEventArgs e) => ImportButton_Click(sender, e);

    private void VpnSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "DNS and Split Tunnel controls are not connected in this Windows port yet.",
            "LRoute • VPN Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ManualConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new Window
        {
            Title = "LRoute • Manual Config", Width = 680, Height = 520,
            MinWidth = 520, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, Background = System.Windows.Media.Brushes.Black,
            Foreground = System.Windows.Media.Brushes.White, WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.CanResize
        };
        var layout = new DockPanel { Margin = new Thickness(16) };
        var save = new Button { Content = "SAVE PROFILE", Height = 44, Margin = new Thickness(0, 12, 0, 0),
            Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.White };
        DockPanel.SetDock(save, Dock.Bottom);
        var input = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20,20,20)),
            Foreground = System.Windows.Media.Brushes.White, CaretBrush = System.Windows.Media.Brushes.White,
            Padding = new Thickness(10), Text = "{\n  \"log\": { \"loglevel\": \"warning\" },\n  \"inbounds\": [],\n  \"outbounds\": []\n}" };
        layout.Children.Add(save); layout.Children.Add(input); editor.Content = layout;
        save.Click += (_, _) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(input.Text);
                Directory.CreateDirectory(DataDirectory);
                var path = Path.Combine(DataDirectory, "manual-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                File.WriteAllText(path, input.Text);
                _profiles.Add(new ProfileItem(Path.GetFileNameWithoutExtension(path), "Xray JSON", path,
                    "LRoute-" + Guid.NewGuid().ToString("N")[..12]));
                SaveProfiles(); RefreshProfileView(); editor.DialogResult = true; editor.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(editor, "Invalid JSON or could not save the profile.\\n" + ex.Message,
                    "LRoute • Manual Config", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        editor.ShowDialog();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import VPN profile",
            Filter = "Xray profiles (*.json;*.txt)|*.json;*.txt|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var imported = 0;
        foreach (var file in dialog.FileNames)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".json" or ".txt"))
            {
                MessageBox.Show(this, "This profile format is not supported in this release.",
                    "LRoute • Unsupported profile", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }
            var type = extension == ".json" ? "Xray JSON" : "Text configuration (engine pending)";
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
        if (ProfilesList.SelectedItem is not ProfileItem selected || selected.Type != "Xray JSON") return;
        ConnectButton.IsEnabled = false;
        try
        {
            if (_xray.IsRunning && ReferenceEquals(selected, _activeProfile))
            {
                await _xray.DisconnectAsync();
                _activeProfile = null;
                ConnectionMessage.Text = "Xray-core stopped.";
            }
            else
            {
                if (_xray.IsRunning)
                {
                    await _xray.DisconnectAsync();
                    _activeProfile = null;
                }
                var tunMode = TunModeCheck.IsChecked == true;
                ConnectionMessage.Text = tunMode
                    ? "Requesting administrator approval and preparing the Windows TUN adapter…"
                    : "Validating Xray JSON and starting local proxy engine…";
                await _xray.ConnectAsync(selected.FilePath, tunMode);
                _activeProfile = selected;
                ConnectionMessage.Text = tunMode
                    ? $"Xray TUN engine running • {selected.Name}"
                    : $"Xray-core running • {selected.Name} (local proxy mode)";
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
        var active = activeXray;
        ConnectButton.Content = active ? "DISCONNECT" : "CONNECT";
        ConnectButton.IsEnabled = selected?.Type == "Xray JSON" || active;
        TunModeCheck.IsEnabled = !_xray.IsRunning;
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
        if (_xray.IsRunning)
        {
            EngineStatus.Text = _xray.IsTunMode
                ? "Xray TUN engine running • Wintun"
                : "Xray-core process running • local proxy";
            ConnectionStatus.Text = _xray.IsTunMode ? "●  TUN ENGINE RUNNING" : "●  ENGINE RUNNING";
            ConnectionPillText.Text = _xray.IsTunMode ? "● TUN RUNNING" : "● ENGINE RUNNING";
            ConnectionPillText.Foreground = System.Windows.Media.Brushes.White;
            ConnectionPillBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 35));
            ConnectionPillBorder.BorderBrush = System.Windows.Media.Brushes.Gray;
            ConnectionMessage.Text = _xray.IsTunMode
                ? $"Windows TUN engine running • {_activeProfile?.Name}"
                : $"Xray-core running • {_activeProfile?.Name} (local proxy mode)";
        }
        else
        {
            EngineStatus.Text = _xray.FindExecutable() is not null
                ? "Xray-core ready"
                : "Xray-core runtime missing";
            ConnectionStatus.Text = "●  DISCONNECTED";
            ConnectionPillText.Text = "● DISCONNECTED";
            ConnectionPillText.Foreground = System.Windows.Media.Brushes.LightGray;
            ConnectionPillBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32));
            ConnectionPillBorder.BorderBrush = System.Windows.Media.Brushes.Gray;
            ConnectionMessage.Text = "Select a supported profile to start a connection.";
        }
        UpdateSelectedProfileState();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try
        {
            if (_xray.IsTunMode)
                _xray.DisconnectAsync().GetAwaiter().GetResult();

        }
        catch (Exception ex)
        {
            e.Cancel = true;
            MessageBox.Show(this,
                $"The active VPN could not be shut down safely. LRoute will remain open so you can retry disconnecting.\n\n{ex.Message}",
                "LRoute • Safe shutdown", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
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
