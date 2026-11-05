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
    public string RawJson { get; init; } = "";
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public string DisplayEndpoint => !string.IsNullOrWhiteSpace(Server)
        ? $"{Server}:{Port}"
        : Protocol.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? "Imported Xray configuration"
            : "No endpoint";
    public string ProtocolLabel => Protocol.ToUpperInvariant();
}
