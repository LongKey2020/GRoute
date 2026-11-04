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
    ("shadowsocks", "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:test-password")) + "@example.com:8388#ss-test")
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
    }
    finally
    {
        if (manager.IsActive) manager.Restore();
    }
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { }
}
