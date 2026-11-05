using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace LRoute.Windows.Infrastructure;

/// <summary>
/// Temporarily routes WinINET-aware Windows applications through LRoute's local HTTP proxy.
/// The user's previous proxy/PAC settings are DPAPI-protected and restored on disconnect
/// or recovered on the next launch after an unclean shutdown.
/// </summary>
public sealed class SystemProxyManager
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LRoute.SystemProxy.v1");
    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LRoute", "system-proxy.backup");
    private static readonly string[] ManagedValues =
        ["ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect"];

    public sealed record ValueSnapshot(bool Exists, string? Text, int? Number, int Kind);
    public sealed record ProxySnapshot(
        Dictionary<string, ValueSnapshot> Values,
        Dictionary<string, ValueSnapshot>? AppliedValues = null);

    public bool IsActive => File.Exists(BackupPath);

    /// <summary>Restore a previous session's settings before the new UI starts.</summary>
    public void RecoverPreviousSession()
    {
        if (File.Exists(BackupPath)) Restore();
    }

    public void Activate(int httpPort = 2081)
    {
        if (httpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(httpPort));
        if (File.Exists(BackupPath))
            throw new InvalidOperationException("A previous LRoute system-proxy backup exists. Restore it before connecting again.");

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's Windows Internet Settings registry key.");

        var values = new Dictionary<string, ValueSnapshot>(StringComparer.Ordinal);
        var existingNames = new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in ManagedValues)
        {
            if (!existingNames.Contains(name))
            {
                values[name] = new ValueSnapshot(false, null, null, (int)RegistryValueKind.Unknown);
                continue;
            }
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name] = raw switch
            {
                int number => new ValueSnapshot(true, null, number, (int)kind),
                string text => new ValueSnapshot(true, text, null, (int)kind),
                _ => throw new InvalidOperationException($"Unsupported existing Windows proxy registry value type: {name}.")
            };
        }

        var appliedValues = new Dictionary<string, ValueSnapshot>(StringComparer.Ordinal)
        {
            ["ProxyEnable"] = new(true, null, 1, (int)RegistryValueKind.DWord),
            ["ProxyServer"] = new(true, $"http=127.0.0.1:{httpPort};https=127.0.0.1:{httpPort}", null, (int)RegistryValueKind.String),
            ["ProxyOverride"] = new(true, "<local>;localhost;127.*;[::1]", null, (int)RegistryValueKind.String),
            ["AutoConfigURL"] = new(false, null, null, (int)RegistryValueKind.Unknown),
            ["AutoDetect"] = new(true, null, 0, (int)RegistryValueKind.DWord)
        };
        SaveBackup(new ProxySnapshot(values, appliedValues));
        try
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"http=127.0.0.1:{httpPort};https=127.0.0.1:{httpPort}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", "<local>;localhost;127.*;[::1]", RegistryValueKind.String);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
            RefreshWinInet();
        }
        catch
        {
            try { Restore(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Restore only values that still match LRoute's own proxy configuration.
    /// Values changed by the user or another application are preserved.
    /// </summary>
    public IReadOnlyList<string> Restore()
    {
        if (!File.Exists(BackupPath)) return [];

        var encrypted = File.ReadAllBytes(BackupPath);
        var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var snapshot = JsonSerializer.Deserialize<ProxySnapshot>(json)
                ?? throw new InvalidDataException("The saved Windows proxy backup is invalid.");
            if (snapshot.AppliedValues is null)
                throw new InvalidDataException(
                    "This proxy backup predates conflict-safe recovery. It was preserved; review Windows proxy settings before manual recovery.");

            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the current user's Windows Internet Settings registry key.");

            var conflicts = new List<string>();
            foreach (var name in ManagedValues)
            {
                if (!snapshot.Values.TryGetValue(name, out var original) ||
                    !snapshot.AppliedValues.TryGetValue(name, out var applied))
                    throw new InvalidDataException($"The Windows proxy backup is missing {name}.");

                var current = ReadValue(key, name);
                if (current == original) continue; // A previous restore attempt already completed this value.
                if (current != applied)
                {
                    conflicts.Add(name);
                    continue; // Another actor changed this setting; do not overwrite it.
                }

                WriteValue(key, name, original);
            }

            RefreshWinInet();
            File.Delete(BackupPath);
            return conflicts;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static ValueSnapshot ReadValue(RegistryKey key, string name)
    {
        var names = new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name))
            return new ValueSnapshot(false, null, null, (int)RegistryValueKind.Unknown);

        var kind = key.GetValueKind(name);
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return raw switch
        {
            int number => new ValueSnapshot(true, null, number, (int)kind),
            string text => new ValueSnapshot(true, text, null, (int)kind),
            _ => throw new InvalidDataException($"Unsupported Windows proxy registry value type: {name}.")
        };
    }

    private static void WriteValue(RegistryKey key, string name, ValueSnapshot value)
    {
        if (!value.Exists)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        var kind = (RegistryValueKind)value.Kind;
        if (value.Text is not null) key.SetValue(name, value.Text, kind);
        else if (value.Number.HasValue) key.SetValue(name, value.Number.Value, kind);
        else throw new InvalidDataException($"The Windows proxy backup contains an invalid value for {name}.");
    }

    private static void SaveBackup(ProxySnapshot snapshot)
    {
        var folder = Path.GetDirectoryName(BackupPath)!;
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var temp = BackupPath + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, BackupPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void RefreshWinInet()
    {
        const int InternetOptionSettingsChanged = 39;
        const int InternetOptionRefresh = 37;
        if (!InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the proxy settings change.");
        if (!InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not refresh the proxy settings.");
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
