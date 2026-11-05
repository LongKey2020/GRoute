using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using LRoute.Windows.Domain;
using LRoute.Windows.Infrastructure;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("TEST FAILED: " + message);
}

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the path to the bundled xray.exe.");

var xray = Path.GetFullPath(args[0]);
var vmessJson = JsonSerializer.Serialize(new
{
    v = "2", ps = "vmess-test", add = "example.com", port = "443",
    id = "11111111-1111-4111-8111-111111111111", aid = "4",
    scy = "auto", net = "tcp", type = "none", tls = ""
});
var vmessLink = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessJson));

var cases = new[]
{
    ("vless", "vless://11111111-1111-4111-8111-111111111111@example.com:443?type=tcp&security=tls&sni=example.com#vless-test"),
    ("vmess", vmessLink),
    ("trojan", "trojan://test-password@example.com:443?security=tls&sni=example.com#trojan-test"),
    ("shadowsocks", "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:test-password")) + "@example.com:8388#ss-test"),
    ("socks", "socks://test-user:test-password@example.com:1080#socks-test"),
    ("http", "http://example.com:8080#http-test")
};

var temp = Path.Combine(Path.GetTempPath(), "LRoute-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    foreach (var (expectedProtocol, link) in cases)
    {
        var parsed = ProxyLinkParser.ParseMany(link);
        Assert(parsed.Count == 1, expectedProtocol + " parser should return exactly one profile.");
        var profile = parsed[0];
        Assert(profile.Protocol == expectedProtocol, expectedProtocol + " protocol mismatch.");
        if (expectedProtocol == "vmess")
            Assert(profile.AlterId == 4, "VMess AlterId must be preserved.");
        if (expectedProtocol == "trojan")
            Assert(profile.Password == "test-password", "Trojan URI userinfo must be mapped to the outbound password.");

        var config = RuntimeProfileBuilder.Build(profile);
        using var document = JsonDocument.Parse(config);
        Assert(document.RootElement.GetProperty("outbounds").GetArrayLength() >= 3,
            expectedProtocol + " runtime config should include proxy/direct/block outbounds.");
        if (expectedProtocol == "vless")
            Assert(document.RootElement.GetProperty("outbounds")[0].GetProperty("streamSettings")
                .GetProperty("security").GetString() == "tls", "VLESS TLS transport security must be emitted.");

        var configPath = Path.Combine(temp, expectedProtocol + ".json");
        await File.WriteAllTextAsync(configPath, config);
        var start = new ProcessStartInfo(xray)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(xray)!
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("-test");
        start.ArgumentList.Add("-config");
        start.ArgumentList.Add(configPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Xray validator.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + Environment.NewLine + (await stderr);
        Assert(process.ExitCode == 0, expectedProtocol + " generated config rejected by Xray: " + output);
        Console.WriteLine("PASS: " + expectedProtocol + " parse + config validation");
    }

    var jsonOutbound = """{"protocol":"vless","tag":"proxy","settings":{"vnext":[{"address":"example.com","port":443,"users":[{"id":"11111111-1111-4111-8111-111111111111","encryption":"none"}]}]}}""";
    var wrapped = ProxyLinkParser.ParseMany(jsonOutbound);
    Assert(wrapped.Count == 1 && wrapped[0].Protocol == "json",
        "Standalone Xray outbound should be wrapped as a complete config.");
    Console.WriteLine("PASS: standalone Xray outbound wrapping");

    var mixed = ProxyLinkParser.ParseManyDetailed(cases[0].Item2 + Environment.NewLine + "unsupported://example.com");
    Assert(mixed.Profiles.Count == 1 && mixed.Errors.Count == 1,
        "A malformed/unsupported subscription entry must not discard valid entries.");
    Console.WriteLine("PASS: partial subscription import diagnostics");

    var unauthenticatedHttp = ProxyLinkParser.ParseMany("http://example.com:8080");
    Assert(unauthenticatedHttp.Count == 1, "Unauthenticated HTTP proxy should parse.");
    var httpConfig = JsonDocument.Parse(RuntimeProfileBuilder.Build(unauthenticatedHttp[0]));
    Assert(!httpConfig.RootElement.GetProperty("outbounds")[0].GetProperty("settings")
        .GetProperty("servers")[0].TryGetProperty("users", out _),
        "Unauthenticated HTTP proxy must not emit an empty users array.");
    Console.WriteLine("PASS: unauthenticated HTTP proxy config");

    // A rejected caller-owned profile must survive validation failure unchanged.
    var invalidConfigPath = Path.Combine(temp, "caller-owned-invalid.json");
    const string invalidConfigContents = "{ invalid json";
    await File.WriteAllTextAsync(invalidConfigPath, invalidConfigContents);
    using (var engine = new LRoute.Windows.XrayEngine(xray))
    {
        var validationRejected = false;
        try { await engine.ConnectAsync(invalidConfigPath); }
        catch (InvalidOperationException) { validationRejected = true; }
        Assert(validationRejected, "Xray must reject malformed caller-owned JSON.");
        Assert(File.Exists(invalidConfigPath), "Validation failure must not delete a caller-owned profile config.");
        Assert(await File.ReadAllTextAsync(invalidConfigPath) == invalidConfigContents,
            "Validation failure must not modify a caller-owned profile config.");
        Console.WriteLine("PASS: rejected caller-owned profile survives Xray validation failure");
    }

    // Exercise the real Xray process lifecycle on Windows, not only JSON validation.
    var lifecycleSocksPort = Random.Shared.Next(30000, 45000);
    var lifecycleHttpPort = lifecycleSocksPort == 44999 ? 30001 : lifecycleSocksPort + 1;
    var lifecycleConfigPath = Path.Combine(temp, "lifecycle.json");
    await File.WriteAllTextAsync(lifecycleConfigPath,
        RuntimeProfileBuilder.Build(unauthenticatedHttp[0], lifecycleSocksPort, lifecycleHttpPort));
    using (var engine = new LRoute.Windows.XrayEngine(xray))
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ProcessExited += (_, _) => exited.TrySetResult();
        await engine.ConnectAsync(lifecycleConfigPath);
        Assert(engine.IsRunning, "Xray engine must report running after a successful start.");

        var duplicateStartRejected = false;
        try { await engine.ConnectAsync(lifecycleConfigPath); }
        catch (InvalidOperationException) { duplicateStartRejected = true; }
        Assert(duplicateStartRejected, "A second ConnectAsync must not start a duplicate Xray process.");

        await engine.DisconnectAsync();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!engine.IsRunning, "Xray engine must report stopped after disconnect.");
        Assert(File.Exists(lifecycleConfigPath), "Disconnect must not delete the caller-owned profile config.");
        Console.WriteLine("PASS: Xray process start, duplicate-start guard, exit event and caller config preservation");
    }

    using (var engine = new LRoute.Windows.XrayEngine(xray))
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350)))
    {
        var cancelled = false;
        try { await engine.ConnectAsync(lifecycleConfigPath, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Cancelling Xray startup should cancel ConnectAsync.");
        Assert(!engine.IsRunning, "Cancelling Xray startup must not leave a child process running.");
        Assert(File.Exists(lifecycleConfigPath), "Cancelled startup must not delete the caller-owned profile config.");
        Console.WriteLine("PASS: Xray startup cancellation cleanup and caller config preservation");
    }

    const string internetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    var manager = new SystemProxyManager();
    manager.RecoverPreviousSession();
    var managedValues = new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect" };
    static Dictionary<string, (bool Exists, object? Value, RegistryValueKind Kind)> CaptureProxySettings(
        string path, IEnumerable<string> names)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false)
            ?? throw new InvalidOperationException("Windows Internet Settings registry key is unavailable.");
        var existing = new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
        var snapshot = new Dictionary<string, (bool, object?, RegistryValueKind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var exists = existing.Contains(name);
            snapshot[name] = exists
                ? (true, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(name))
                : (false, null, RegistryValueKind.Unknown);
        }
        return snapshot;
    }
    void AssertProxySettingsRestored(Dictionary<string, (bool Exists, object? Value, RegistryValueKind Kind)> expected)
    {
        var actual = CaptureProxySettings(internetSettingsPath, managedValues);
        foreach (var name in managedValues)
        {
            Assert(actual[name].Exists == expected[name].Exists, $"Windows proxy value existence was not restored: {name}");
            Assert(Equals(actual[name].Value, expected[name].Value), $"Windows proxy value was not restored: {name}");
            Assert(actual[name].Kind == expected[name].Kind, $"Windows proxy registry type was not restored: {name}");
        }
    }

    var originalProxySettings = CaptureProxySettings(internetSettingsPath, managedValues);
    try
    {
        manager.Activate(2081);
        Assert(manager.IsActive, "System proxy backup should exist after activation.");
        using (var key = Registry.CurrentUser.OpenSubKey(internetSettingsPath, writable: false))
        {
            Assert(Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) == 1,
                "Windows Internet Settings proxy should be enabled.");
            Assert(Equals(key?.GetValue("ProxyServer"), "http=127.0.0.1:2081;https=127.0.0.1:2081"),
                "Windows Internet Settings should point to LRoute's local HTTP proxy.");
        }
        manager.Restore();
        Assert(!manager.IsActive, "System proxy backup should be removed after restore.");
        AssertProxySettingsRestored(originalProxySettings);
        Console.WriteLine("PASS: Windows system proxy activation and exact restoration");

        manager.Activate(2081);
        new SystemProxyManager().RecoverPreviousSession();
        Assert(!manager.IsActive, "Startup recovery should remove the stale system proxy backup.");
        AssertProxySettingsRestored(originalProxySettings);
        Console.WriteLine("PASS: Windows system proxy crash recovery");

        manager.Activate(2081);
        const string externallySetProxy = "http=127.0.0.1:9999;https=127.0.0.1:9999";
        using (var key = Registry.CurrentUser.OpenSubKey(internetSettingsPath, writable: true))
            key!.SetValue("ProxyServer", externallySetProxy, RegistryValueKind.String);
        var conflicts = manager.Restore();
        Assert(conflicts.Contains("ProxyServer"), "Restore must report proxy values changed by another actor.");
        using (var key = Registry.CurrentUser.OpenSubKey(internetSettingsPath, writable: false))
            Assert(Equals(key?.GetValue("ProxyServer"), externallySetProxy),
                "Restore must preserve an externally changed proxy server value.");
        var expectedAfterConflict = new Dictionary<string, (bool Exists, object? Value, RegistryValueKind Kind)>(originalProxySettings)
        {
            ["ProxyServer"] = (true, externallySetProxy, RegistryValueKind.String)
        };
        AssertProxySettingsRestored(expectedAfterConflict);
        Assert(!manager.IsActive, "Conflict-safe restore should finish and clear its backup.");
        Console.WriteLine("PASS: external Windows proxy changes are preserved during restore");
    }
    finally
    {
        if (manager.IsActive) manager.Restore();
    }

    var profileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LRoute");
    Directory.CreateDirectory(profileFolder);
    var encryptedProfilesPath = Path.Combine(profileFolder, "profiles.dat");
    var legacyProfilesPath = Path.Combine(profileFolder, "profiles.json");
    var originalEncryptedProfiles = File.Exists(encryptedProfilesPath) ? File.ReadAllBytes(encryptedProfilesPath) : null;
    var originalLegacyProfiles = File.Exists(legacyProfilesPath) ? File.ReadAllBytes(legacyProfilesPath) : null;
    try
    {
        File.Delete(encryptedProfilesPath);
        File.Delete(legacyProfilesPath);
        var profileStore = new ProfileStore();
        var testProfile = new ProxyProfile
        {
            Name = "dpapi-roundtrip-test", Protocol = "vless", Server = "profile-test.invalid",
            Port = 443, UserId = "11111111-1111-4111-8111-111111111111"
        };
        profileStore.Save([testProfile]);
        var encryptedBytes = File.ReadAllBytes(encryptedProfilesPath);
        Assert(!Encoding.UTF8.GetString(encryptedBytes).Contains(testProfile.Name, StringComparison.Ordinal),
            "Saved profile data must not be plaintext.");
        var roundTrip = profileStore.Load();
        Assert(roundTrip.Count == 1 && roundTrip[0].Name == testProfile.Name && roundTrip[0].UserId == testProfile.UserId,
            "DPAPI profile save/load round trip failed.");
        Console.WriteLine("PASS: DPAPI encrypted profile storage round trip");

        File.Delete(encryptedProfilesPath);
        var legacyProfile = testProfile with { Name = "legacy-migration-test" };
        File.WriteAllText(legacyProfilesPath, JsonSerializer.Serialize(new[] { legacyProfile }));
        var migrated = profileStore.Load();
        Assert(migrated.Count == 1 && migrated[0].Name == legacyProfile.Name,
            "Legacy plaintext profile migration returned incorrect profiles.");
        Assert(File.Exists(encryptedProfilesPath) && !File.Exists(legacyProfilesPath),
            "Legacy profile file must migrate to encrypted storage and then be deleted.");
        Console.WriteLine("PASS: legacy profile migration to DPAPI");

        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(encryptedProfilesPath, corruptBytes);
        var rejectedCorruptVault = false;
        try { profileStore.Load(); }
        catch (InvalidOperationException) { rejectedCorruptVault = true; }
        Assert(rejectedCorruptVault, "Corrupt encrypted profile vault should be rejected.");
        Assert(File.ReadAllBytes(encryptedProfilesPath).SequenceEqual(corruptBytes),
            "Corrupt profile vault must remain untouched after a failed load.");
        Console.WriteLine("PASS: corrupt profile vault preservation");
    }
    finally
    {
        if (originalEncryptedProfiles is null) File.Delete(encryptedProfilesPath);
        else File.WriteAllBytes(encryptedProfilesPath, originalEncryptedProfiles);
        if (originalLegacyProfiles is null) File.Delete(legacyProfilesPath);
        else File.WriteAllBytes(legacyProfilesPath, originalLegacyProfiles);
    }
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { }
}
