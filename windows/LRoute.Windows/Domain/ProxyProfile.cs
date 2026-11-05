namespace LRoute.Windows.Domain;

public sealed record ProxyProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "New profile";
    public string Protocol { get; init; } = "unknown";
    public string Server { get; init; } = "";
    public int Port { get; init; }
    public string UserId { get; init; } = "";
    public int AlterId { get; init; } = 0;
    public string Password { get; init; } = "";
    public string Method { get; init; } = "";
    public string Network { get; init; } = "tcp";
    public string Security { get; init; } = "none";
    public string Sni { get; init; } = "";
    public string Host { get; init; } = "";
    public string Path { get; init; } = "";
    public string Flow { get; init; } = "";
    public string PublicKey { get; init; } = "";
    public string ShortId { get; init; } = "";
    public string Fingerprint { get; init; } = "chrome";
    public string Alpn { get; init; } = "";
    public string SourceUri { get; init; } = "";
    public string SubscriptionUrl { get; init; } = "";
    public long SubscriptionUploadBytes { get; init; }
    public long SubscriptionDownloadBytes { get; init; }
    public long SubscriptionTotalBytes { get; init; }
    public long SubscriptionExpiresAt { get; init; }
    public long SubscriptionLastUpdated { get; init; }
    public long SubscriptionUsedBytes => SubscriptionUploadBytes + SubscriptionDownloadBytes;
    public string SubscriptionUsageLabel => SubscriptionTotalBytes > 0
        ? $"{FormatBytes(SubscriptionUsedBytes)} / {FormatBytes(SubscriptionTotalBytes)}"
        : SubscriptionUsedBytes > 0 ? $"{FormatBytes(SubscriptionUsedBytes)} used" : "Usage unavailable";
    public string SubscriptionExpiryLabel => SubscriptionExpiresAt > 0
        ? DateTimeOffset.FromUnixTimeSeconds(SubscriptionExpiresAt).ToLocalTime().ToString("yyyy-MM-dd")
        : "Expiry unavailable";
    public string SubscriptionUpdatedLabel => SubscriptionLastUpdated > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(SubscriptionLastUpdated).ToLocalTime().ToString("g")
        : "Not refreshed";
    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "Unknown";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
    public string RawJson { get; init; } = "";
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public string DisplayEndpoint => !string.IsNullOrWhiteSpace(Server)
        ? $"{Server}:{Port}"
        : Protocol.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? "Imported Xray configuration"
            : "No endpoint";
    public string ProtocolLabel => Protocol.ToUpperInvariant();
}
