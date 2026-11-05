using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LRoute.Windows.Infrastructure;

/// <summary>Applies user DNS and domain-bypass preferences to a generated Xray document.</summary>
public static class RuntimeConfigOptions
{
    public static string Apply(
        string sourceJson,
        string? dnsServers,
        string? bypassDomains)
    {
        var root = JsonNode.Parse(sourceJson) as JsonObject
            ?? throw new InvalidDataException("Xray configuration must be a JSON object.");

        var dns = SplitValues(dnsServers);
        if (dns.Length > 0)
        {
            foreach (var server in dns)
            {
                if (IPAddress.TryParse(server, out _)) continue;
                if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host))
                    throw new FormatException($"Invalid DNS server '{server}'. Use an IP address or HTTPS DNS URL.");
            }

            var dnsConfig = root["dns"] as JsonObject ?? new JsonObject();
            dnsConfig["servers"] = new JsonArray(dns.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            if (dnsConfig["queryStrategy"] is null) dnsConfig["queryStrategy"] = "UseIPv4";
            root["dns"] = dnsConfig;
        }

        var domains = SplitValues(bypassDomains)
            .Select(NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (domains.Length > 0)
        {
            var outbounds = root["outbounds"] as JsonArray
                ?? throw new InvalidDataException("Xray configuration has no outbounds array.");
            var hasDirect = outbounds.OfType<JsonObject>().Any(item =>
                string.Equals(item["tag"]?.GetValue<string>(), "direct", StringComparison.OrdinalIgnoreCase));
            if (!hasDirect)
                outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });

            var routing = root["routing"] as JsonObject ?? new JsonObject();
            var rules = routing["rules"] as JsonArray ?? new JsonArray();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (!string.Equals(rule["outboundTag"]?.GetValue<string>(), "direct", StringComparison.OrdinalIgnoreCase) ||
                    rule["domain"] is not JsonArray ruleDomains) continue;
                foreach (var value in ruleDomains)
                    if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                        existing.Add(text.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ? text[7..] : text);
            }

            var missing = domains.Where(domain => !existing.Contains(domain)).ToArray();
            if (missing.Length > 0)
            {
                var insertAt = rules.Count;
                for (var i = 0; i < rules.Count; i++)
                {
                    if (rules[i] is not JsonObject rule || rule["outboundTag"] is null) continue;
                    var hasCondition = new[]
                    {
                        "domain", "ip", "port", "source", "sourcePort",
                        "network", "protocol", "user", "attrs", "inboundTag"
                    }.Any(key => rule[key] is not null);
                    if (!hasCondition) { insertAt = i; break; }
                }

                var domainArray = new JsonArray(missing
                    .Select(domain => (JsonNode?)JsonValue.Create("domain:" + domain)).ToArray());
                rules.Insert(insertAt, new JsonObject
                {
                    ["type"] = "field",
                    ["domain"] = domainArray,
                    ["outboundTag"] = "direct"
                });
            }

            routing["rules"] = rules;
            root["routing"] = routing;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string[] SplitValues(string? input) =>
        (input ?? "").Split(new[] { ',', ';', '\\r', '\\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeDomain(string value)
    {
        var domain = value.Trim().TrimEnd('.');
        if (domain.StartsWith("*.", StringComparison.Ordinal)) domain = domain[2..];
        if (domain.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)) domain = domain[7..];
        if (domain.Length == 0 || domain.Contains('/') || domain.Contains(':') ||
            Uri.CheckHostName(domain) != UriHostNameType.Dns)
            throw new FormatException($"Invalid bypass domain '{value}'. Enter a domain such as example.com.");
        return domain.ToLowerInvariant();
    }
}
