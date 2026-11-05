using System.IO;
using System.Text.Json;

namespace LRoute.Windows.Infrastructure;

/// <summary>
/// Non-secret Windows preferences. Proxy credentials remain in the DPAPI profile vault.
/// </summary>
public sealed record LRouteUserSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LRoute", "settings.json");

    public bool UseTunMode { get; init; }
    public string DnsServers { get; init; } = "1.1.1.1, 8.8.8.8";
    public string BypassDomains { get; init; } = "";

    public static LRouteUserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new LRouteUserSettings();
            return JsonSerializer.Deserialize<LRouteUserSettings>(File.ReadAllText(SettingsPath))
                ?? new LRouteUserSettings();
        }
        catch
        {
            // A malformed preferences file must not prevent the app from launching.
            return new LRouteUserSettings();
        }
    }

    public void Save()
    {
        var folder = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(folder);
        var tempPath = SettingsPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
