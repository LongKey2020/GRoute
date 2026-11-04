using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LRoute.Windows.Domain;

namespace LRoute.Windows.Infrastructure;

/// <summary>
/// Persists profiles encrypted with Windows DPAPI (CurrentUser scope).
/// Legacy plaintext profiles.json files are migrated on the next successful load.
/// </summary>
public sealed class ProfileStore
{
    private const string EntropyText = "LRoute.ProfileStore.v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(EntropyText);

    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LRoute");

    private string EncryptedFilePath => Path.Combine(_folder, "profiles.dat");
    private string LegacyFilePath => Path.Combine(_folder, "profiles.json");

    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public IReadOnlyList<ProxyProfile> Load()
    {
        Directory.CreateDirectory(_folder);

        if (File.Exists(EncryptedFilePath))
        {
            try
            {
                var encrypted = File.ReadAllBytes(EncryptedFilePath);
                var plaintext = ProtectedData.Unprotect(
                    encrypted, Entropy, DataProtectionScope.CurrentUser);

                return JsonSerializer.Deserialize<List<ProxyProfile>>(
                    plaintext, _options) ?? [];
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "LRoute could not decrypt the saved profiles for this Windows account. " +
                    "The profiles file may have been copied from another Windows user or damaged.",
                    ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "The encrypted LRoute profiles file is invalid or corrupted.", ex);
            }
        }

        // One-time migration from the previous plaintext format.
        if (File.Exists(LegacyFilePath))
        {
            var legacyJson = File.ReadAllText(LegacyFilePath, Encoding.UTF8);
            var profiles = JsonSerializer.Deserialize<List<ProxyProfile>>(
                legacyJson, _options) ?? [];

            Save(profiles);
            File.Delete(LegacyFilePath);
            return profiles;
        }

        return [];
    }

    public void Save(IEnumerable<ProxyProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        Directory.CreateDirectory(_folder);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(profiles, _options);
        var encrypted = ProtectedData.Protect(
            plaintext, Entropy, DataProtectionScope.CurrentUser);

        var tempPath = EncryptedFilePath + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, encrypted);
            File.Move(tempPath, EncryptedFilePath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
