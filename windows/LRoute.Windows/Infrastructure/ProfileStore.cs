using System.Text.Json;
using LRoute.Windows.Domain;

namespace LRoute.Windows.Infrastructure;

public sealed class ProfileStore
{
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LRoute");
    private string FilePath => Path.Combine(_folder, "profiles.json");
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public IReadOnlyList<ProxyProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<ProxyProfile>>(File.ReadAllText(FilePath), _options) ?? [];
        }
        catch { return []; }
    }

    public void Save(IEnumerable<ProxyProfile> profiles)
    {
        Directory.CreateDirectory(_folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles, _options));
        File.Move(temp, FilePath, true);
    }
}
