using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LRoute.Windows.Domain;
using LRoute.Windows.Infrastructure;

namespace LRoute.Windows;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly ObservableCollection<ProxyProfile> _profiles = [];
    private ICollectionView? _profilesView;
    private bool _sortAscending = true;
    private readonly XrayEngine _engine = new();
    private readonly SystemProxyManager _systemProxy = new();
    private readonly SubscriptionFetcher _subscriptions = new();
    private readonly DispatcherTimer _subscriptionRefreshTimer = new();
    private bool _subscriptionRefreshInProgress;
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
        _profilesView = CollectionViewSource.GetDefaultView(_profiles);
        _profilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProxyProfile.GroupName)));
        _profilesView.Filter = item => item is ProxyProfile p && (string.IsNullOrWhiteSpace(SearchBox.Text) || (p.Name + " " + p.Server + " " + p.Port + " " + p.Protocol).Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        ProfilesList.ItemsSource = _profilesView;
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
        ConfigureSubscriptionRefreshTimer();
    }

    private void ConfigureSubscriptionRefreshTimer()
    {
        _subscriptionRefreshTimer.Stop();
        _subscriptionRefreshTimer.Tick -= SubscriptionRefreshTimer_Tick;
        if (!_settings.AutoRefreshSubscriptions) return;
        _subscriptionRefreshTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.SubscriptionRefreshMinutes, 15, 1440));
        _subscriptionRefreshTimer.Tick += SubscriptionRefreshTimer_Tick;
        _subscriptionRefreshTimer.Start();
    }

    private async void SubscriptionRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_subscriptionRefreshInProgress || _engine.IsRunning) return;
        _subscriptionRefreshInProgress = true;
        try
        {
            var urls = _profiles.Select(p => p.SubscriptionUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var url in urls)
            {
                try { await RefreshSubscriptionAsync(url); }
                catch { /* Keep the app running; a failed provider is retried at the next interval. */ }
            }
        }
        finally { _subscriptionRefreshInProgress = false; }
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
                SubscriptionFetchResult? fetched = null;
                if (subscriptionUrl is not null)
                {
                    fetched = await _subscriptions.FetchAsync(subscriptionUrl);
                    source = fetched.Content;
                }
                var result = ProxyLinkParser.ParseManyDetailed(source);
                foreach (var profile in result.Profiles)
                {
                    var imported = profile with
                    {
                        Name = profile.Name == "New profile" ? Path.GetFileNameWithoutExtension(file) : profile.Name,
                        SubscriptionUrl = subscriptionUrl ?? ""
                    };
                    _profiles.Add(ApplySubscriptionMetadata(imported, fetched));
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
            MessageBox.Show(this, "No supported Xray profiles were found.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RefreshSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            MessageBox.Show(this, "Disconnect before refreshing a subscription.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ProfilesList.SelectedItem is not ProxyProfile selected || string.IsNullOrWhiteSpace(selected.SubscriptionUrl))
        {
            MessageBox.Show(this, "Select a profile that was imported from a subscription URL.", "LRoute", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var count = await RefreshSubscriptionAsync(selected.SubscriptionUrl);
            MessageBox.Show(this, $"Subscription refreshed: {count} profile(s).", "Refresh complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Subscription refresh failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<int> RefreshSubscriptionAsync(string url)
    {
        var fetched = await _subscriptions.FetchAsync(url);
        var result = ProxyLinkParser.ParseManyDetailed(fetched.Content);
        if (result.Profiles.Count == 0)
            throw new FormatException(result.Errors.Count > 0 ? string.Join(Environment.NewLine, result.Errors) : "The subscription returned no supported profiles.");
        var oldProfiles = _profiles.Where(profile => string.Equals(profile.SubscriptionUrl, url, StringComparison.OrdinalIgnoreCase)).ToArray();
        var insertIndex = oldProfiles.Length > 0 ? _profiles.IndexOf(oldProfiles[0]) : _profiles.Count;
        var previousMetadata = oldProfiles.FirstOrDefault(profile =>
            profile.SubscriptionTotalBytes > 0 || profile.SubscriptionExpiresAt > 0 ||
            profile.SubscriptionUploadBytes > 0 || profile.SubscriptionDownloadBytes > 0);
        foreach (var oldProfile in oldProfiles) _profiles.Remove(oldProfile);
        var info = fetched.UserInfo;
        var refreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var refreshed = result.Profiles.Select(profile => profile with
        {
            SubscriptionUrl = url,
            SubscriptionUploadBytes = info?.UploadBytes ?? previousMetadata?.SubscriptionUploadBytes ?? 0,
            SubscriptionDownloadBytes = info?.DownloadBytes ?? previousMetadata?.SubscriptionDownloadBytes ?? 0,
            SubscriptionTotalBytes = info?.TotalBytes ?? previousMetadata?.SubscriptionTotalBytes ?? 0,
            SubscriptionExpiresAt = info?.ExpiresAt ?? previousMetadata?.SubscriptionExpiresAt ?? 0,
            SubscriptionLastUpdated = refreshedAt
        }).ToArray();
        for (var i = 0; i < refreshed.Length; i++)
            _profiles.Insert(Math.Clamp(insertIndex + i, 0, _profiles.Count), refreshed[i]);
        Persist();
        _profilesView?.Refresh();
        RefreshUi();
        if (result.Errors.Count > 0 && !_subscriptionRefreshTimer.IsEnabled) ShowImportErrors(result.Errors);
        return refreshed.Length;
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
                SubscriptionFetchResult? fetched = null;
                var source = input.Text;
                if (subscriptionUrl is not null)
                {
                    fetched = await _subscriptions.FetchAsync(subscriptionUrl);
                    source = fetched.Content;
                }
                var result = ProxyLinkParser.ParseManyDetailed(source);
                if (result.Profiles.Count == 0)
                    throw new FormatException(result.Errors.Count > 0
                        ? string.Join(Environment.NewLine, result.Errors)
                        : "No supported VLESS, VMess, Trojan, Shadowsocks, SOCKS or HTTP profile was found.");
                foreach (var profile in result.Profiles)
                    _profiles.Add(ApplySubscriptionMetadata(profile with { SubscriptionUrl = subscriptionUrl ?? "" }, fetched));
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
                RuntimeProfileBuilder.Build(profile), _settings.DnsServers, _settings.BypassDomains, _settings.AdBlockEnabled ? _settings.BlockedDomains : "");
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
            if (_settings.UseTunMode && !File.Exists(Path.Combine(AppContext.BaseDirectory, "xray", "wintun.dll")))
                throw new FileNotFoundException("Wintun runtime is missing. Reinstall the Windows package before testing TUN mode.");

            Directory.CreateDirectory(RuntimeDirectory);
            var config = RuntimeConfigOptions.Apply(
                RuntimeProfileBuilder.Build(profile), _settings.DnsServers, _settings.BypassDomains, _settings.AdBlockEnabled ? _settings.BlockedDomains : "");
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
    private void Search_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Visibility = SearchBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SearchBox.Visibility == Visibility.Collapsed) SearchBox.Clear(); else SearchBox.Focus();
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _profilesView?.Refresh();
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sortAscending = !_sortAscending;
        var items = (_sortAscending ? _profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase) : _profiles.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        _profiles.Clear(); foreach (var item in items) _profiles.Add(item);
        _profilesView?.Refresh();
    }
    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; button.IsEnabled = false; button.Content = "Testing servers...";
        try
        {
            using var gate = new SemaphoreSlim(24);
            var tasks = _profiles.ToArray().Select(async profile =>
            {
                var host = GetProbeHost(profile);
                if (string.IsNullOrWhiteSpace(host)) return (Name: profile.Name, Ms: (long?)null);
                await gate.WaitAsync();
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(host, 1200);
                    return (Name: profile.Name, Ms: reply.Status == IPStatus.Success ? (long?)reply.RoundtripTime : null);
                }
                catch { return (Name: profile.Name, Ms: (long?)null); }
                finally { gate.Release(); }
            }).ToArray();
            var results = await Task.WhenAll(tasks);
            var good = results.Count(x => x.Ms.HasValue);
            var fastest = results.Where(x => x.Ms.HasValue).OrderBy(x => x.Ms).Take(15).Select(x => x.Name + ": " + x.Ms + " ms");
            MessageBox.Show(this, "Tested: " + results.Length + "\nReachable: " + good + "\nUnreachable/blocked: " + (results.Length-good) + "\n\nFastest:\n" + string.Join(Environment.NewLine,fastest) + "\n\nICMP may be blocked. This does not measure VPN tunnel latency.", "Test all servers", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { button.Content = "▥   Test all"; button.IsEnabled = true; }
    }
    private void ProfileActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ProxyProfile profile) return;
        var menu = new ContextMenu();
        void Add(string name, Action action) { var item = new MenuItem { Header=name }; item.Click += (_,_)=>action(); menu.Items.Add(item); }
        Add("Select profile",()=>{ ProfilesList.SelectedItem=profile; });
        Add("Ping server",()=>_ = PingProfileAsync(profile));
        Add("Edit profile",()=>EditProfile(profile));
        Add("Copy share link",()=>CopyProfile(profile));
        Add("Export profile",()=>{ProfilesList.SelectedItem=profile;Export_Click(this,new RoutedEventArgs());});
        Add("Remove profile",()=>{ProfilesList.SelectedItem=profile;Remove_Click(this,new RoutedEventArgs());});
        button.ContextMenu=menu; menu.PlacementTarget=button; menu.IsOpen=true;
    }
    private async Task PingProfileAsync(ProxyProfile profile)
    {
        var host=GetProbeHost(profile);
        if(string.IsNullOrWhiteSpace(host)){MessageBox.Show(this,"No server address is available for this profile.","Ping check",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        try { using var ping=new Ping(); var reply=await ping.SendPingAsync(host,2000); MessageBox.Show(this,reply.Status==IPStatus.Success?host + "\nReachable • " + reply.RoundtripTime + " ms":host + "\n" + reply.Status + "\nICMP may be blocked.","Ping check",MessageBoxButton.OK,reply.Status==IPStatus.Success?MessageBoxImage.Information:MessageBoxImage.Warning); }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Ping check failed",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void CopyProfile(ProxyProfile profile)
    {
        var value=!string.IsNullOrWhiteSpace(profile.SourceUri)&&!profile.SourceUri.StartsWith("json:",StringComparison.OrdinalIgnoreCase)?profile.SourceUri:RuntimeProfileBuilder.Build(profile);
        Clipboard.SetText(value); MessageBox.Show(this,"Profile link/config copied. It contains connection credentials; keep it private.","Share server",MessageBoxButton.OK,MessageBoxImage.Information);
    }
    private void EditProfile(ProxyProfile profile)
    {
        if(!string.IsNullOrWhiteSpace(profile.SubscriptionUrl))
        {
            var url=Microsoft.VisualBasic.Interaction.InputBox("Edit subscription URL:","Edit subscription",profile.SubscriptionUrl);
            if(string.IsNullOrWhiteSpace(url)||url==profile.SubscriptionUrl)return;
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")){MessageBox.Show(this,"Enter a valid HTTP/HTTPS URL.");return;}
            foreach(var old in _profiles.Where(p=>p.SubscriptionUrl==profile.SubscriptionUrl).ToArray()){var i=_profiles.IndexOf(old);_profiles[i]=old with{SubscriptionUrl=url};}
            Persist();_profilesView?.Refresh();return;
        }
        var source=profile.SourceUri.StartsWith("json:",StringComparison.OrdinalIgnoreCase)?profile.RawJson:profile.SourceUri;
        var editor=new Window{Title="Edit server profile",Owner=this,Width=680,Height=460,MinWidth=520,MinHeight=360,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=new SolidColorBrush(Color.FromRgb(25,27,31)),Foreground=Brushes.White};
        var panel=new DockPanel{Margin=new Thickness(16)};var save=new Button{Content="SAVE CHANGES",Height=42,Margin=new Thickness(0,12,0,0),Background=new SolidColorBrush(Color.FromRgb(242,138,53)),Foreground=Brushes.Black};DockPanel.SetDock(save,Dock.Bottom);
        var input=new TextBox{Text=source,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(16,17,19)),Foreground=Brushes.White,CaretBrush=Brushes.White,Padding=new Thickness(12),FontFamily=new FontFamily("Consolas"),FontSize=13};
        panel.Children.Add(save);panel.Children.Add(input);editor.Content=panel;
        save.Click+=(_,_)=>{try{var parsed=ProxyLinkParser.ParseManyDetailed(input.Text);if(parsed.Profiles.Count!=1)throw new FormatException("Enter exactly one supported profile or Xray JSON config.");var replacement=parsed.Profiles[0] with{Id=profile.Id,SubscriptionUrl=profile.SubscriptionUrl,AddedAt=profile.AddedAt};var i=_profiles.IndexOf(profile);if(i>=0)_profiles[i]=replacement;Persist();_profilesView?.Refresh();editor.Close();}catch(Exception ex){MessageBox.Show(editor,ex.Message,"Invalid profile",MessageBoxButton.OK,MessageBoxImage.Warning);}};
        editor.ShowDialog();
    }

    private ProxyProfile[] GetGroupMembers(object sender)
    {
        if (sender is not Button button || button.Tag is not string groupName) return [];
        return _profiles.Where(p => p.GroupName == groupName).ToArray();
    }
    private void ShareGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetGroupMembers(sender);
        if (group.Length == 0 || string.IsNullOrWhiteSpace(group[0].SubscriptionUrl)) return;
        Clipboard.SetText(group[0].SubscriptionUrl);
        MessageBox.Show(this, "Subscription URL copied. Keep it private; it may grant access to your server list.", "Share subscription", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async void TestGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetGroupMembers(sender);
        if (group.Length == 0) return;
        using var gate = new SemaphoreSlim(16);
        var tasks = group.Select(async profile =>
        {
            var host = GetProbeHost(profile);
            if (string.IsNullOrWhiteSpace(host)) return false;
            await gate.WaitAsync();
            try { using var ping = new Ping(); return (await ping.SendPingAsync(host, 1200)).Status == IPStatus.Success; }
            catch { return false; }
            finally { gate.Release(); }
        });
        var results = await Task.WhenAll(tasks);
        MessageBox.Show(this, "Reachable: " + results.Count(x => x) + " / " + results.Length + "\nICMP does not measure VPN tunnel latency.", "Test server group", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetGroupMembers(sender);
        if (group.Length == 0 || string.IsNullOrWhiteSpace(group[0].SubscriptionUrl)) return;
        var oldUrl = group[0].SubscriptionUrl;
        var newUrl = Microsoft.VisualBasic.Interaction.InputBox("Edit subscription URL:", "Edit subscription", oldUrl);
        if (string.IsNullOrWhiteSpace(newUrl) || newUrl == oldUrl) return;
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "Enter a valid HTTP or HTTPS subscription URL.", "Invalid subscription", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var profile in group)
        {
            var index = _profiles.IndexOf(profile);
            if (index >= 0) _profiles[index] = profile with { SubscriptionUrl = newUrl };
        }
        Persist(); _profilesView?.Refresh();
    }
    private void RefreshGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetGroupMembers(sender);
        var profile = group.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.SubscriptionUrl));
        if (profile is null) return;
        ProfilesList.SelectedItem = profile;
        RefreshSubscription_Click(sender, e);
    }
    private void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetGroupMembers(sender);
        if (group.Length == 0) return;
        if (MessageBox.Show(this, "Remove all " + group.Length + " servers in '" + group[0].GroupName + "'?", "Remove server group", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (group.Any(p => _active?.Id == p.Id))
        {
            MessageBox.Show(this, "Disconnect before removing the active server.", "Remove server group", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var profile in group) _profiles.Remove(profile);
        Persist(); _profilesView?.Refresh(); RefreshUi();
    }

    private void GroupActions_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button button||button.Tag is not string groupName)return;
        var members=_profiles.Where(p=>p.GroupName==groupName).ToArray();if(members.Length==0)return;
        var menu=new ContextMenu();var share=new MenuItem{Header="Copy subscription URL",IsEnabled=!string.IsNullOrWhiteSpace(members[0].SubscriptionUrl)};share.Click+=(_,_)=>Clipboard.SetText(members[0].SubscriptionUrl);menu.Items.Add(share);
        var test=new MenuItem{Header="Test all servers in group"};test.Click+=async(_,_)=>{var ok=0;using var ping=new Ping();foreach(var p in members){var h=GetProbeHost(p);if(string.IsNullOrWhiteSpace(h))continue;try{if((await ping.SendPingAsync(h,1200)).Status==IPStatus.Success)ok++;}catch{}}MessageBox.Show(this,"Reachable: "+ok+" / "+members.Length+"\nICMP does not measure VPN tunnel latency.","Group test");};menu.Items.Add(test);
        var refresh=new MenuItem{Header="Refresh subscription",IsEnabled=!string.IsNullOrWhiteSpace(members[0].SubscriptionUrl)};refresh.Click+=(_,_)=>{ProfilesList.SelectedItem=members[0];RefreshSubscription_Click(this,new RoutedEventArgs());};menu.Items.Add(refresh);
        var remove=new MenuItem{Header="Remove group"};remove.Click+=(_,_)=>{if(MessageBox.Show(this,"Remove all "+members.Length+" servers in '"+groupName+"'?","Remove group",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;if(members.Any(p=>_active?.Id==p.Id)){MessageBox.Show(this,"Disconnect before removing the active profile.");return;}foreach(var p in members)_profiles.Remove(p);Persist();_profilesView?.Refresh();RefreshUi();};menu.Items.Add(remove);
        button.ContextMenu=menu;menu.PlacementTarget=button;menu.IsOpen=true;
    }

    private void Overview_Click(object sender, RoutedEventArgs e) { PageTitle.Text = "Overview"; PageSubtitle.Text = "Route WinINET-aware apps through your selected Xray profile."; }
    private void Profiles_Click(object sender, RoutedEventArgs e) { PageTitle.Text = "Profiles"; PageSubtitle.Text = "Import and manage Xray-compatible profiles."; }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "LRoute • Settings",
            Owner = this, Width = 820, Height = 760, MinWidth = 680, MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10)), Foreground = Brushes.White
        };
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "Settings", FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,18) });
        var muted = new SolidColorBrush(Color.FromRgb(174,174,174));
        var cardBrush = new SolidColorBrush(Color.FromRgb(25,25,25));
        var lineBrush = new SolidColorBrush(Color.FromRgb(72,72,72));
        Border Card(string title, string description)
        {
            var body = new StackPanel { Margin = new Thickness(16) };
            body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(description))
                body.Children.Add(new TextBlock { Text = description, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,12) });
            var border = new Border { Background = cardBrush, BorderBrush = lineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0,0,0,12), Child = body };
            root.Children.Add(border);
            return border;
        }
        TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,8,0,5) };
        TextBox Field(string value, int height = 38) => new()
        {
            Text = value, Height = height, Padding = new Thickness(10,8,10,8),
            Background = new SolidColorBrush(Color.FromRgb(13,13,13)), Foreground = Brushes.White,
            BorderBrush = lineBrush, CaretBrush = Brushes.White
        };
        Button Action(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Height = 38, Margin = new Thickness(0,8,8,0), Background = new SolidColorBrush(Color.FromRgb(35,35,35)), BorderBrush = lineBrush };
            button.Click += click; return button;
        }
        string FormatBytes(long value)
        {
            string[] units = ["B","KB","MB","GB","TB"]; double n = Math.Max(0,value); var i=0;
            while(n>=1024 && i<units.Length-1){n/=1024;i++;}
            return n.ToString("0.##") + " " + units[i];
        }

        // DATA USAGE: subscription quota metadata, not a fabricated live network counter.
        var usage = Card("◷  Data usage", "Subscription usage reported by your provider. This is not a live Windows adapter traffic counter.");
        var usageBody = (StackPanel)usage.Child;
        var subscriptions = _profiles.Where(p => !string.IsNullOrWhiteSpace(p.SubscriptionUrl))
            .GroupBy(p => p.SubscriptionUrl, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        long used = subscriptions.Sum(p => p.SubscriptionUsedBytes);
        long quota = subscriptions.Sum(p => p.SubscriptionTotalBytes);
        usageBody.Children.Add(new TextBlock { Text = "Reported usage: " + FormatBytes(used) + (quota > 0 ? " / " + FormatBytes(quota) : ""), FontSize = 22, FontWeight = FontWeights.SemiBold });
        usageBody.Children.Add(new TextBlock { Text = subscriptions.Length + " subscription(s) • " + _profiles.Count + " saved profile(s)", Foreground = muted, Margin = new Thickness(0,5,0,0) });
        foreach (var profile in subscriptions.Take(8))
            usageBody.Children.Add(new TextBlock { Text = profile.GroupName + "   " + profile.SubscriptionUsageLabel + "   " + profile.SubscriptionExpiryLabel, Foreground = muted, Margin = new Thickness(0,7,0,0), TextWrapping = TextWrapping.Wrap });

        // NETWORK RADAR
        var radar = Card("◉  Network Radar", "Check whether selected HTTPS websites respond from this Windows network. A failure can mean DNS, routing, firewall or remote outage; it does not prove censorship.");
        var radarBody = (StackPanel)radar.Child;
        radarBody.Children.Add(Label("Hosts / websites (one per line)"));
        var hosts = Field("https://www.google.com\nhttps://www.cloudflare.com\nhttps://github.com", 92);
        hosts.AcceptsReturn = true; hosts.TextWrapping = TextWrapping.Wrap; hosts.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        radarBody.Children.Add(hosts);
        var radarResult = new TextBlock { Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) };
        var radarButton = new Button { Content = "Run network radar", Height = 38, Margin = new Thickness(0,8,8,0), Background = new SolidColorBrush(Color.FromRgb(35,35,35)), BorderBrush = lineBrush };
        radarButton.Click += async (_, _) =>
        {
            radarButton.IsEnabled = false;
            radarButton.Content = "Checking...";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var output = new List<string>();
                foreach (var raw in hosts.Text.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var target = raw.Contains("://", StringComparison.Ordinal) ? raw : "https://" + raw;
                    if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "https") { output.Add(raw + " — invalid HTTPS URL"); continue; }
                    try { using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead); output.Add(uri.Host + " — HTTP " + (int)response.StatusCode); }
                    catch (Exception ex) { output.Add(uri.Host + " — no response (" + ex.GetBaseException().Message + ")"); }
                }
                radarResult.Text = string.Join(Environment.NewLine, output);
            }
            finally { radarButton.IsEnabled = true; radarButton.Content = "Run network radar"; }
        };
        radarBody.Children.Add(radarButton); radarBody.Children.Add(radarResult);

        // TOOLS
        var tools = Card("🔧  Tools", "Quick diagnostics for the selected server and a host.");
        var toolsBody = (StackPanel)tools.Child;
        var hostRow = new DockPanel();
        var hostInput = Field("example.com");
        var toolsResult = new TextBlock { Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) };
        var hostButton = Action("Check host", async (_, _) =>
        {
            var host = hostInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(host)) return;
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(host);
                toolsResult.Text = host + " resolves to: " + string.Join(", ", addresses.Select(a => a.ToString()));
                using var tcp = new System.Net.Sockets.TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await tcp.ConnectAsync(host, 443, timeout.Token);
                toolsResult.Text += Environment.NewLine + "TCP 443: reachable";
            }
            catch (Exception ex) { toolsResult.Text = host + " — " + ex.GetBaseException().Message; }
        });
        DockPanel.SetDock(hostButton, Dock.Right); hostRow.Children.Add(hostButton); hostRow.Children.Add(hostInput);
        toolsBody.Children.Add(Label("Check host (DNS + TCP/443)")); toolsBody.Children.Add(hostRow);
        toolsBody.Children.Add(toolsResult);
        var toolButtons = new WrapPanel();
        toolButtons.Children.Add(Action("Connection quality test", (_,_) => NetworkTest_Click(this, new RoutedEventArgs())));
        toolButtons.Children.Add(Action("Smart connect (fastest ping)", async (_,_) =>
        {
            if (_engine.IsRunning) { MessageBox.Show(dialog,"Disconnect before changing the active profile."); return; }
            var candidates = _profiles.Where(p => !string.IsNullOrWhiteSpace(GetProbeHost(p))).ToArray();
            var measured = new List<(ProxyProfile Profile,long Ms)>();
            foreach (var profile in candidates)
            {
                try { using var ping = new Ping(); var reply = await ping.SendPingAsync(GetProbeHost(profile),1200); if(reply.Status==IPStatus.Success) measured.Add((profile,reply.RoundtripTime)); } catch { }
            }
            var best = measured.OrderBy(x=>x.Ms).FirstOrDefault();
            if (best.Profile is null) { MessageBox.Show(dialog,"No server responded to ICMP Ping. Some servers block Ping."); return; }
            ProfilesList.SelectedItem = best.Profile; RefreshUi();
            MessageBox.Show(dialog,"Selected " + best.Profile.Name + " (" + best.Ms + " ms). Connect when ready. ICMP is only a reachability estimate.","Smart connect");
        }));
        toolButtons.Children.Add(Action("Adblocker: " + (_settings.AdBlockEnabled ? "ON" : "OFF"), (_,_) => MessageBox.Show(dialog, _settings.AdBlockEnabled ? "Domain blocklist is enabled. Changes take effect after reconnecting." : "Domain blocklist is currently disabled. Enable it under Connection Settings.", "Adblocker", MessageBoxButton.OK, MessageBoxImage.Information)));
        toolsBody.Children.Add(toolButtons);

        // CONNECTION SETTINGS
        var connection = Card("▣  Connection settings", "Routing, DNS and split tunneling options.");
        var connectionBody = (StackPanel)connection.Child;
        var tun = new CheckBox { Content = "Experimental full-device TUN mode (administrator approval)", IsChecked = _settings.UseTunMode, Foreground = Brushes.White, Margin = new Thickness(0,4,0,8) };
        connectionBody.Children.Add(tun);
        connectionBody.Children.Add(new TextBlock { Text = "TUN mode requires physical route, DNS and disconnect testing. System proxy mode only affects WinINET-aware applications.", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) });
        var kill = new CheckBox { Content = "Kill switch — not available in this build", IsEnabled = false, Foreground = muted, Margin = new Thickness(0,4,0,4) };
        var perApp = new CheckBox { Content = "Per-app proxy — not available in this build", IsEnabled = false, Foreground = muted, Margin = new Thickness(0,4,0,10) };
        connectionBody.Children.Add(kill); connectionBody.Children.Add(perApp);
        var adblock = new CheckBox { Content = "Enable domain ad / tracker blocking", IsChecked = _settings.AdBlockEnabled, Foreground = Brushes.White, Margin = new Thickness(0,8,0,8) };
        connectionBody.Children.Add(adblock);
        connectionBody.Children.Add(Label("Blocked domains (one per line; Xray blackhole outbound)"));
        var blockedDomains = Field(_settings.BlockedDomains, 86); blockedDomains.AcceptsReturn = true; blockedDomains.TextWrapping = TextWrapping.Wrap; connectionBody.Children.Add(blockedDomains);
        connectionBody.Children.Add(Label("DNS servers (comma-separated IP or HTTPS DNS URL)"));
        var dns = Field(_settings.DnsServers); connectionBody.Children.Add(dns);
        connectionBody.Children.Add(Label("Bypass domains (one per line)"));
        var bypass = Field(_settings.BypassDomains, 92); bypass.AcceptsReturn = true; bypass.TextWrapping = TextWrapping.Wrap; connectionBody.Children.Add(bypass);

        // PREFERENCES
        var preferences = Card("☷  Preferences", "Application appearance, language and subscription maintenance.");
        var prefBody = (StackPanel)preferences.Child;
        var themeRow = new DockPanel(); themeRow.Children.Add(Label("Theme")); var theme = new ComboBox { Width=150, SelectedIndex=0, ItemsSource=new[]{"Dark"} }; DockPanel.SetDock(theme,Dock.Right); themeRow.Children.Add(theme); prefBody.Children.Add(themeRow);
        var languageRow = new DockPanel(); languageRow.Children.Add(Label("Language")); var language = new ComboBox { Width=150, SelectedIndex=0, ItemsSource=new[]{"English"} }; DockPanel.SetDock(language,Dock.Right); languageRow.Children.Add(language); prefBody.Children.Add(languageRow);
        prefBody.Children.Add(new TextBlock { Text = "Dark theme and English UI are currently available. Additional themes and translations are not enabled yet.", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,8) });
        var autoRefresh = new CheckBox { Content = "Automatically refresh subscriptions while disconnected", IsChecked = _settings.AutoRefreshSubscriptions, Foreground = Brushes.White, Margin = new Thickness(0,8,0,6) };
        prefBody.Children.Add(autoRefresh);
        var intervalRow = new DockPanel();
        intervalRow.Children.Add(Label("Refresh interval"));
        var interval = new ComboBox { Width=150, ItemsSource=new[] { 15, 30, 60, 180, 360, 720, 1440 }, SelectedItem=_settings.SubscriptionRefreshMinutes, HorizontalAlignment=HorizontalAlignment.Right };
        interval.DisplayMemberPath = null;
        intervalRow.Children.Add(interval); prefBody.Children.Add(intervalRow);
        prefBody.Children.Add(new TextBlock { Text = "Auto-refresh is paused while a VPN session is active. Failed subscriptions are retried at the next interval.", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,8) });
        prefBody.Children.Add(Action("Refresh selected subscription now", (_,_) => RefreshSubscription_Click(this,new RoutedEventArgs())));

        // ABOUT
        var about = Card("ⓘ  About", "LRoute for Windows");
        var aboutBody = (StackPanel)about.Child;
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "Development";
        aboutBody.Children.Add(new TextBlock { Text = "Version " + version, FontSize = 15, FontWeight = FontWeights.SemiBold });
        aboutBody.Children.Add(new TextBlock { Text = "Developer: LongKey2020\nRuntime: Xray-core Windows x64\nPrivacy: profiles are stored in the local Windows user profile; profile vault protection uses Windows DPAPI.", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,0) });

        // BACKUP / RESTORE
        var backupRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,4,0,12) };
        backupRow.Children.Add(Action("⇧  Create backup", Backup_Click));
        backupRow.Children.Add(Action("⇩  Restore backup", RestoreBackup_Click));
        root.Children.Add(backupRow);
        var save = new Button { Content = "SAVE CONNECTION SETTINGS", Height = 44, Background = new SolidColorBrush(Color.FromRgb(242,138,53)), Foreground = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromRgb(242,138,53)), Margin = new Thickness(0,0,0,12) };
        save.Click += (_,_) =>
        {
            try
            {
                var next = _settings with { UseTunMode=tun.IsChecked==true, DnsServers=dns.Text.Trim(), BypassDomains=bypass.Text.Trim(), AdBlockEnabled=adblock.IsChecked==true, BlockedDomains=blockedDomains.Text.Trim(), AutoRefreshSubscriptions=autoRefresh.IsChecked==true, SubscriptionRefreshMinutes=interval.SelectedItem is int minutes ? minutes : 60, Theme="Dark", Language="English" };
                const string validationConfig = """{"outbounds":[{"tag":"proxy","protocol":"freedom"},{"tag":"direct","protocol":"freedom"}],"routing":{"rules":[{"type":"field","outboundTag":"proxy"}]}}""";
                RuntimeConfigOptions.Apply(validationConfig,next.DnsServers,next.BypassDomains,next.AdBlockEnabled ? next.BlockedDomains : "");
                next.Save(); _settings=next; ConfigureSubscriptionRefreshTimer();
                MessageBox.Show(dialog,"Connection settings saved. Reconnect for changes to affect the running tunnel.","Settings saved",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            catch(Exception ex){MessageBox.Show(dialog,ex.Message,"Invalid settings",MessageBoxButton.OK,MessageBoxImage.Warning);}
        };
        root.Children.Add(save);
        dialog.Content = new ScrollViewer { Content=root, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    private async void NetworkTest_Click(object sender, RoutedEventArgs e)
    {
        var profile = _active ?? ProfilesList.SelectedItem as ProxyProfile;
        if (profile is null)
        {
            MessageBox.Show(this, "Select a profile first. Network Test will ping that profile's server.",
                "Network test", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var host = GetProbeHost(profile);
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this,
                "This imported Xray configuration does not expose a single server address for Ping Check. Select a standard server profile instead.",
                "Network test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var button = (Button)sender;
        button.IsEnabled = false;
        button.Content = "Testing...";
        try
        {
            const int attempts = 4;
            const int timeoutMs = 1500;
            var samples = new List<long>();
            var failures = 0;
            var statuses = new List<string>();
            using var ping = new Ping();

            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(host, timeoutMs);
                    if (reply.Status == IPStatus.Success)
                        samples.Add(reply.RoundtripTime);
                    else
                    {
                        failures++;
                        statuses.Add(reply.Status.ToString());
                    }
                }
                catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException)
                {
                    failures++;
                    statuses.Add(ex.GetBaseException().Message);
                }
            }

            var loss = failures * 100.0 / attempts;
            var result = samples.Count > 0
                ? $"Target: {host}\nProfile: {profile.Name}\n\nPackets: {attempts}\nReceived: {samples.Count}\nLost: {failures} ({loss:0.#}%)\nMinimum: {samples.Min()} ms\nAverage: {samples.Average():0.#} ms\nMaximum: {samples.Max()} ms"
                : $"Target: {host}\nProfile: {profile.Name}\n\nPing failed: {attempts}/{attempts} packets lost (100%).\n{string.Join(Environment.NewLine, statuses.Distinct().Take(2))}\n\nThe server or network may block ICMP Ping.";
            MessageBox.Show(this,
                result + "\n\nNote: ICMP Ping checks reachability to the server address. It does not measure VPN tunnel latency or prove that proxy traffic is working.",
                samples.Count > 0 ? "Network test result" : "Ping check failed",
                MessageBoxButton.OK,
                samples.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = "Network test";
            button.IsEnabled = true;
        }
    }

    private static string GetProbeHost(ProxyProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Server)) return profile.Server;
        if (string.IsNullOrWhiteSpace(profile.RawJson)) return "";

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(profile.RawJson);
            if (!document.RootElement.TryGetProperty("outbounds", out var outbounds) ||
                outbounds.ValueKind != System.Text.Json.JsonValueKind.Array) return "";
            foreach (var outbound in outbounds.EnumerateArray())
            {
                if (!outbound.TryGetProperty("settings", out var settings)) continue;
                foreach (var collectionName in new[] { "vnext", "servers", "peers" })
                {
                    if (!settings.TryGetProperty(collectionName, out var collection) ||
                        collection.ValueKind != System.Text.Json.JsonValueKind.Array ||
                        collection.GetArrayLength() == 0) continue;
                    var first = collection[0];
                    foreach (var field in new[] { "address", "host", "server" })
                        if (first.TryGetProperty(field, out var value) &&
                            value.ValueKind == System.Text.Json.JsonValueKind.String)
                            return value.GetString() ?? "";
                }
            }
        }
        catch (System.Text.Json.JsonException) { }
        return "";
    }

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

    private static ProxyProfile ApplySubscriptionMetadata(ProxyProfile profile, SubscriptionFetchResult? fetched)
    {
        if (fetched is null) return profile;
        var info = fetched.UserInfo;
        return profile with
        {
            SubscriptionUploadBytes = info?.UploadBytes ?? 0,
            SubscriptionDownloadBytes = info?.DownloadBytes ?? 0,
            SubscriptionTotalBytes = info?.TotalBytes ?? 0,
            SubscriptionExpiresAt = info?.ExpiresAt ?? 0,
            SubscriptionLastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
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