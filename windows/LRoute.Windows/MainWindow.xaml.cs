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
    private LRouteUserSettings _settings = LRouteUserSettings.Load();
    private ProxyProfile? _active;
    private bool _storeReady = true;
    private bool _allowClose;
    private bool _closingInProgress;
    private string? _activeRuntimeConfigPath;
    private string RuntimeDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LRoute", "Runtime");

    public MainWindow()
    {
        InitializeComponent();
        _engine.ProcessExited += Engine_ProcessExited;
        try { ShowProxyRestoreConflicts(_systemProxy.RecoverPreviousSession()); }
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
                var subscriptionUrl = GetSubscriptionUrl(source);
                source = await FetchSubscriptionIfUrlAsync(source);
                var result = ProxyLinkParser.ParseManyDetailed(source);
                foreach (var profile in result.Profiles)
                {
                    _profiles.Add(profile with
                    {
                        Name = profile.Name == "New profile" ? Path.GetFileNameWithoutExtension(file) : profile.Name,
                        SubscriptionUrl = subscriptionUrl ?? ""
                    });
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

    private async void RefreshSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            MessageBox.Show(this, "Disconnect before refreshing a subscription.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ProfilesList.SelectedItem is not ProxyProfile selected ||
            string.IsNullOrWhiteSpace(selected.SubscriptionUrl))
        {
            MessageBox.Show(this, "Select a profile that was imported from a subscription URL.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var url = selected.SubscriptionUrl;
            var content = await _subscriptions.GetContentAsync(url);
            var result = ProxyLinkParser.ParseManyDetailed(content);
            if (result.Profiles.Count == 0)
                throw new FormatException(result.Errors.Count > 0
                    ? string.Join(Environment.NewLine, result.Errors)
                    : "The subscription returned no supported profiles.");

            var oldProfiles = _profiles
                .Where(profile => string.Equals(profile.SubscriptionUrl, url, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var insertIndex = oldProfiles.Length > 0 ? _profiles.IndexOf(oldProfiles[0]) : _profiles.IndexOf(selected);
            foreach (var oldProfile in oldProfiles) _profiles.Remove(oldProfile);

            var refreshed = result.Profiles.Select(profile => profile with
            {
                SubscriptionUrl = url
            }).ToArray();
            for (var i = 0; i < refreshed.Length; i++)
                _profiles.Insert(Math.Clamp(insertIndex + i, 0, _profiles.Count), refreshed[i]);

            Persist();
            ProfilesList.SelectedItem = refreshed[0];
            RefreshUi();
            if (result.Errors.Count > 0) ShowImportErrors(result.Errors);
            MessageBox.Show(this, $"Subscription refreshed: {refreshed.Length} profile(s).",
                "Refresh complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Subscription refresh failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProxyProfile profile)
        {
            MessageBox.Show(this, "Select a profile to export first.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export LRoute profile",
            Filter = "Xray JSON profile (*.json)|*.json|Text profile (*.txt)|*.txt",
            FileName = string.IsNullOrWhiteSpace(safeName) ? "LRoute-profile" : safeName,
            DefaultExt = ".json",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var canExportShareLink = !string.IsNullOrWhiteSpace(profile.SourceUri) &&
                !profile.SourceUri.StartsWith("json:", StringComparison.OrdinalIgnoreCase);
            var content = dialog.FilterIndex == 2 && canExportShareLink
                ? profile.SourceUri
                : RuntimeProfileBuilder.Build(profile);
            File.WriteAllText(dialog.FileName, content);
            MessageBox.Show(this,
                "Profile exported. The file contains connection credentials; keep it private.",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
                var subscriptionUrl = GetSubscriptionUrl(input.Text);
                var source = await FetchSubscriptionIfUrlAsync(input.Text);
                var result = ProxyLinkParser.ParseManyDetailed(source);
                if (result.Profiles.Count == 0)
                    throw new FormatException(result.Errors.Count > 0
                        ? string.Join(Environment.NewLine, result.Errors)
                        : "No supported VLESS, VMess, Trojan, Shadowsocks, SOCKS or HTTP profile was found.");
                foreach (var profile in result.Profiles)
                    _profiles.Add(profile with { SubscriptionUrl = subscriptionUrl ?? "" });
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
                DeleteActiveRuntimeConfig();
                RestoreSystemProxyWithWarning();
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
            var config = RuntimeConfigOptions.Apply(
                RuntimeProfileBuilder.Build(profile), _settings.DnsServers, _settings.BypassDomains);
            var path = Path.Combine(RuntimeDirectory, $"profile-{profile.Id}.json");
            await File.WriteAllTextAsync(path, config);
            _activeRuntimeConfigPath = path;
            await _engine.ConnectAsync(path, _settings.UseTunMode);
            if (!_settings.UseTunMode)
            {
                try { _systemProxy.Activate(); }
                catch
                {
                    await _engine.DisconnectAsync();
                    DeleteActiveRuntimeConfig();
                    throw;
                }
            }
            _active = profile;
        }
        catch (Exception ex)
        {
            DeleteActiveRuntimeConfig();
            MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshUi();
    }

    private void Engine_ProcessExited(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { RestoreSystemProxyWithWarning(); }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Xray stopped, but Windows proxy settings could not be restored. Check Windows Settings > Network & Internet > Proxy.\\n\\n{ex.Message}",
                    "Proxy recovery required", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            DeleteActiveRuntimeConfig();
            _active = null;
            RefreshUi();
        }));
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (!_storeReady)
        {
            MessageBox.Show(this, "The profile vault could not be read. Resolve that issue before creating a complete backup.",
                "Backup unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Create encrypted LRoute profile backup",
            Filter = "Encrypted LRoute backup (*.lroute)|*.lroute",
            FileName = "LRoute-profiles.lroute",
            DefaultExt = ".lroute",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _store.ExportBackup(dialog.FileName, _profiles);
            MessageBox.Show(this,
                "Encrypted profile backup created. It can be restored only under the same Windows user account.",
                "Backup complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            MessageBox.Show(this, "Disconnect before restoring a profile backup.", "LRoute",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_storeReady)
        {
            MessageBox.Show(this, "The profile vault could not be read. Resolve that issue before restoring a backup.",
                "Restore unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Restore encrypted LRoute profile backup",
            Filter = "Encrypted LRoute backup (*.lroute)|*.lroute|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
            "Restoring this backup will replace all currently saved profiles. Continue?",
            "Replace saved profiles", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var restored = _store.ImportBackup(dialog.FileName);
            var previous = _profiles.ToArray();
            try
            {
                _profiles.Clear();
                foreach (var profile in restored) _profiles.Add(profile);
                _store.Save(_profiles);
            }
            catch
            {
                _profiles.Clear();
                foreach (var profile in previous) _profiles.Add(profile);
                throw;
            }

            _active = null;
            RefreshUi();
            MessageBox.Show(this, $"Restored {restored.Count} profile(s).",
                "Restore complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ValidateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProxyProfile profile)
        {
            MessageBox.Show(this, "Select a profile to validate first.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? sourcePath = null;
        string? validationPath = null;
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            var config = RuntimeConfigOptions.Apply(
                RuntimeProfileBuilder.Build(profile), _settings.DnsServers, _settings.BypassDomains);
            sourcePath = Path.Combine(RuntimeDirectory, $"validate-source-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(sourcePath, config);
            validationPath = _settings.UseTunMode
                ? await XrayTunConfigBuilder.CreateAsync(sourcePath, RuntimeDirectory)
                : sourcePath;

            await _engine.TestConfigAsync(validationPath);
            MessageBox.Show(this,
                "Xray accepted this profile and its current DNS/split-routing settings. No network connection was started.",
                "Profile validation passed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Profile validation failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            foreach (var path in new[] { validationPath, sourcePath }.OfType<string>().Distinct())
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".error.log")) File.Delete(path + ".error.log");
                }
                catch { /* Best-effort cleanup of temporary validation files only. */ }
            }
        }
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
        var dialog = new Window
        {
            Title = "LRoute • Connection settings",
            Owner = this,
            Width = 650,
            Height = 620,
            MinWidth = 560,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(25, 27, 31)),
            Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTION MODE",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(242, 138, 53)),
            Margin = new Thickness(0, 0, 0, 10)
        });
        var tun = new CheckBox
        {
            Content = "EXPERIMENTAL: use full-device TUN mode (requires administrator approval)",
            IsChecked = _settings.UseTunMode,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(tun);
        panel.Children.Add(new TextBlock
        {
            Text = "TUN routes device traffic through the Wintun adapter and requests administrator approval. This mode has automated config tests but still needs physical Windows route, DNS and disconnect testing. If disabled, LRoute uses the Windows system proxy for WinINET-aware applications.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 167, 177)),
            Margin = new Thickness(0, 0, 0, 20)
        });
        panel.Children.Add(new TextBlock { Text = "DNS SERVERS", FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 0, 0, 7) });
        var dns = new TextBox
        {
            Text = _settings.DnsServers,
            Height = 38,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromRgb(16, 17, 19)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 73))
        };
        panel.Children.Add(dns);
        panel.Children.Add(new TextBlock
        {
            Text = "Leave blank to keep the imported profile's DNS settings. Otherwise enter comma-separated IP addresses or HTTPS DNS endpoints, for example 1.1.1.1, 8.8.8.8 or https://dns.google/dns-query.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 167, 177)),
            Margin = new Thickness(0, 6, 0, 20)
        });
        panel.Children.Add(new TextBlock { Text = "BYPASS DOMAINS (SPLIT ROUTING)", FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 0, 0, 7) });
        var bypass = new TextBox
        {
            Text = _settings.BypassDomains,
            Height = 150,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromRgb(16, 17, 19)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 73)),
            FontFamily = new FontFamily("Consolas")
        };
        panel.Children.Add(bypass);
        panel.Children.Add(new TextBlock
        {
            Text = "One domain per line. Matching domains bypass the proxy and use the direct connection. Example: example.com or *.example.org.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 167, 177)),
            Margin = new Thickness(0, 6, 0, 18)
        });
        var save = new Button
        {
            Content = "SAVE SETTINGS",
            Height = 42,
            Background = new SolidColorBrush(Color.FromRgb(242, 138, 53)),
            Foreground = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromRgb(242, 138, 53))
        };
        save.Click += (_, _) =>
        {
            try
            {
                var next = new LRouteUserSettings
                {
                    UseTunMode = tun.IsChecked == true,
                    DnsServers = dns.Text.Trim(),
                    BypassDomains = bypass.Text.Trim()
                };
                const string validationConfig = """{"outbounds":[{"tag":"proxy","protocol":"freedom"},{"tag":"direct","protocol":"freedom"}],"routing":{"rules":[{"type":"field","outboundTag":"proxy"}]}}""";
                RuntimeConfigOptions.Apply(validationConfig, next.DnsServers, next.BypassDomains);
                next.Save();
                _settings = next;
                dialog.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, ex.Message, "Invalid connection settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        panel.Children.Add(save);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }
    private void Menu_Click(object sender, RoutedEventArgs e) { MessageBox.Show(this, "LRoute Windows • WireGuard and QR scanning are excluded.", "About LRoute"); }

    private void DeleteActiveRuntimeConfig()
    {
        var path = _activeRuntimeConfigPath;
        _activeRuntimeConfigPath = null;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var runtimeRoot = Path.GetFullPath(RuntimeDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(fullPath)) File.Delete(fullPath);
            if (File.Exists(fullPath + ".error.log")) File.Delete(fullPath + ".error.log");
        }
        catch { /* Best-effort cleanup of LRoute-owned runtime files only. */ }
    }

    private void RestoreSystemProxyWithWarning()
    {
        ShowProxyRestoreConflicts(_systemProxy.Restore());
    }

    private void ShowProxyRestoreConflicts(IReadOnlyList<string> conflicts)
    {
        if (conflicts.Count == 0) return;
        MessageBox.Show(this,
            "LRoute restored the proxy settings it still owned, but left these settings unchanged because another application or user changed them while LRoute was connected: " +
            string.Join(", ", conflicts) +
            ". Review Windows Settings > Network & Internet > Proxy.",
            "Proxy settings changed externally", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

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

    private static string? GetSubscriptionUrl(string value)
    {
        var candidate = value.Trim();
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? candidate
            : null;
    }

    private async Task<string> FetchSubscriptionIfUrlAsync(string value)
    {
        var url = GetSubscriptionUrl(value);
        return url is not null ? await _subscriptions.GetContentAsync(url) : value;
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
        StatusText.Text = running ? (_engine.IsTunMode ? "TUN active" : "System proxy active") : "Disconnected";
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
                DeleteActiveRuntimeConfig();
                RestoreSystemProxyWithWarning();
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