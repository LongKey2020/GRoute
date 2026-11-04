using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LRoute.Windows.Domain;
using LRoute.Windows.Infrastructure;

namespace LRoute.Windows;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly ObservableCollection<ProxyProfile> _profiles = [];
    private readonly XrayEngine _engine = new();
    private readonly SystemProxyManager _systemProxy = new();
    private readonly SubscriptionFetcher _subscriptions = new();
    private ProxyProfile? _active;
    private bool _storeReady = true;
    private bool _allowClose;
    private bool _closingInProgress;
    private string RuntimeDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LRoute", "Runtime");

    public MainWindow()
    {
        InitializeComponent();
        _engine.ProcessExited += Engine_ProcessExited;
        try { _systemProxy.RecoverPreviousSession(); }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"LRoute could not restore the previous Windows proxy settings. Check Windows Settings > Network & Internet > Proxy before connecting again.\\n\\n{ex.Message}",
                "Proxy recovery required", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        ProfilesList.ItemsSource = _profiles;
        try
        {
            foreach (var profile in _store.Load()) _profiles.Add(profile);
        }
        catch (Exception ex)
        {
            // Never overwrite a profile vault that could not be decrypted or parsed.
            _storeReady = false;
            MessageBox.Show(this,
                $"LRoute could not read the saved profile vault. The original file has been left untouched.\\n\\n{ex.Message}\\n\\nExport or back up the vault before attempting recovery.",
                "Profile storage unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshUi();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "LRoute profiles (*.json;*.txt;*.conf)|*.json;*.txt;*.conf|All files (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        var added = 0;
        var importErrors = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var source = await File.ReadAllTextAsync(file);
                source = await FetchSubscriptionIfUrlAsync(source);
                var result = ProxyLinkParser.ParseManyDetailed(source);
                foreach (var profile in result.Profiles)
                {
                    _profiles.Add(profile with { Name = profile.Name == "New profile" ? Path.GetFileNameWithoutExtension(file) : profile.Name });
                    added++;
                }
                importErrors.AddRange(result.Errors.Select(error => $"{Path.GetFileName(file)}: {error}"));
            }
            catch (Exception ex) { importErrors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        Persist();
        RefreshUi();
        if (importErrors.Count > 0) ShowImportErrors(importErrors);
        if (added == 0 && importErrors.Count == 0)
            MessageBox.Show(this, "No supported Xray profiles were found. WireGuard and QR import are not included.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        var editor = new Window { Title = "LRoute • Manual profile", Owner = this, Width = 650, Height = 430, MinWidth = 500, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(25,27,31)), Foreground = Brushes.White };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var save = new Button { Content = "IMPORT PROFILE TEXT", Height = 42, Margin = new Thickness(0,12,0,0), Background = new SolidColorBrush(Color.FromRgb(242,138,53)), Foreground = Brushes.Black };
        DockPanel.SetDock(save, Dock.Bottom);
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(16,17,19)), Foreground = Brushes.White, CaretBrush = Brushes.White, Padding = new Thickness(12),
            FontFamily = new FontFamily("Consolas"), FontSize = 13 };
        panel.Children.Add(save); panel.Children.Add(input); editor.Content = panel;
        save.Click += async (_, _) =>
        {
            try
            {
                var source = await FetchSubscriptionIfUrlAsync(input.Text);
                var result = ProxyLinkParser.ParseManyDetailed(source);
                if (result.Profiles.Count == 0)
                    throw new FormatException(result.Errors.Count > 0
                        ? string.Join(Environment.NewLine, result.Errors)
                        : "No supported VLESS, VMess, Trojan, Shadowsocks, SOCKS or HTTP profile was found.");
                foreach (var profile in result.Profiles) _profiles.Add(profile);
                Persist(); RefreshUi(); editor.Close();
                if (result.Errors.Count > 0) ShowImportErrors(result.Errors);
            }
            catch (Exception ex) { MessageBox.Show(editor, ex.Message, "Invalid profile", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        editor.ShowDialog();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            try
            {
                await _engine.DisconnectAsync();
                _systemProxy.Restore();
                _active = null;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Disconnect failed", MessageBoxButton.OK, MessageBoxImage.Error); }
            RefreshUi(); return;
        }
        if (ProfilesList.SelectedItem is not ProxyProfile profile)
        {
            MessageBox.Show(this, "Select a profile first.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            var config = RuntimeProfileBuilder.Build(profile);
            var path = Path.Combine(RuntimeDirectory, $"profile-{profile.Id}.json");
            await File.WriteAllTextAsync(path, config);
            await _engine.ConnectAsync(path, false);
            try { _systemProxy.Activate(); }
            catch
            {
                await _engine.DisconnectAsync();
                throw;
            }
            _active = profile;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshUi();
    }

    private void Engine_ProcessExited(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { _systemProxy.Restore(); }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Xray stopped, but Windows proxy settings could not be restored. Check Windows Settings > Network & Internet > Proxy.\\n\\n{ex.Message}",
                    "Proxy recovery required", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            _active = null;
            RefreshUi();
        }));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProxyProfile profile) return;
        if (_active?.Id == profile.Id) { MessageBox.Show(this, "Disconnect before removing the active profile."); return; }
        _profiles.Remove(profile); Persist(); RefreshUi();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshUi();
    private void Overview_Click(object sender, RoutedEventArgs e) { PageTitle.Text = "Overview"; PageSubtitle.Text = "Route WinINET-aware apps through your selected Xray profile."; }
    private void Profiles_Click(object sender, RoutedEventArgs e) { PageTitle.Text = "Profiles"; PageSubtitle.Text = "Import and manage Xray-compatible profiles."; }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "This Windows release currently uses the Windows Internet Settings proxy (HTTP 127.0.0.1:2081).\\n\\n" +
            "WinINET-aware applications use LRoute. Some WinHTTP services, games and applications with their own network stack may not.\\n\\n" +
            "Advanced DNS, split routing, kill switch and TUN controls are not included in this release candidate.",
            "LRoute Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Menu_Click(object sender, RoutedEventArgs e) { MessageBox.Show(this, "LRoute Windows • WireGuard and QR scanning are excluded.", "About LRoute"); }

    private void ShowImportErrors(IReadOnlyList<string> errors)
    {
        const int maxShown = 20;
        var details = string.Join(Environment.NewLine, errors.Take(maxShown));
        if (errors.Count > maxShown)
            details += $"{Environment.NewLine}... and {errors.Count - maxShown} more error(s).";
        MessageBox.Show(this,
            $"Some entries could not be imported ({errors.Count}). Valid profiles were kept.{Environment.NewLine}{Environment.NewLine}{details}",
            "Import completed with warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task<string> FetchSubscriptionIfUrlAsync(string value)
    {
        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            return await _subscriptions.GetContentAsync(candidate);
        return value;
    }

    private void Persist()
    {
        if (!_storeReady)
        {
            MessageBox.Show(this,
                "These changes are only in memory and will not be saved. The unreadable profile vault remains untouched.",
                "Profile storage unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _store.Save(_profiles);
    }
    private void RefreshUi()
    {
        ProfileCount.Text = $"{_profiles.Count} saved profile{(_profiles.Count == 1 ? "" : "s")}";
        var running = _engine.IsRunning;
        StatusText.Text = running ? "System proxy active" : "Disconnected";
        StatusDot.Fill = running ? new SolidColorBrush(Color.FromRgb(65,190,125)) : new SolidColorBrush(Color.FromRgb(119,127,137));
        EngineState.Text = running ? "Running" : "Stopped";
        ConnectButton.Content = running ? "Disconnect" : "Connect";
        EndpointText.Text = _active?.DisplayEndpoint ?? "No active profile";
        ConnectButton.IsEnabled = running || ProfilesList.SelectedItem is ProxyProfile;
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (_engine.IsRunning || _systemProxy.IsActive)
        {
            e.Cancel = true;
            if (_closingInProgress) return;
            _closingInProgress = true;
            IsEnabled = false;
            try
            {
                if (_engine.IsRunning) await _engine.DisconnectAsync();
                _systemProxy.Restore();
                _allowClose = true;
                Close();
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                _closingInProgress = false;
                MessageBox.Show(this,
                    $"LRoute could not safely disconnect, so the application will remain open.\\n\\n{ex.Message}",
                    "Disconnect required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        base.OnClosing(e);
    }
}