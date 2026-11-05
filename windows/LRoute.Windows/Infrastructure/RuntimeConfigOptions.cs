using System.IO;
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
        string? bypassDomains,
        string? blockedDomains = null)
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

            if (root["dns"] is not null && root["dns"] is not JsonObject)
                throw new InvalidDataException("Xray DNS configuration must be an object.");
            var dnsConfig = root["dns"] as JsonObject;
            if (dnsConfig is null)
            {
                dnsConfig = new JsonObject();
                root["dns"] = dnsConfig;
            }
            dnsConfig["servers"] = new JsonArray(dns.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            if (dnsConfig["queryStrategy"] is null) dnsConfig["queryStrategy"] = "UseIPv4";
        }

        var domains = SplitValues(bypassDomains)
            .Select(NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (domains.Length > 0)
        {
            var outbounds = root["outbounds"] as JsonArray
                ?? throw new InvalidDataException("Xray configuration has no outbounds array.");
            var directTag = "direct";
            var directNameConflict = outbounds.OfType<JsonObject>().Any(item =>
                string.Equals(item["tag"]?.GetValue<string>(), directTag, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item["protocol"]?.GetValue<string>(), "freedom", StringComparison.OrdinalIgnoreCase));
            if (directNameConflict)
            {
                directTag = "lroute-direct";
                var suffix = 2;
                while (outbounds.OfType<JsonObject>().Any(item =>
                    string.Equals(item["tag"]?.GetValue<string>(), directTag, StringComparison.OrdinalIgnoreCase)))
                    directTag = "lroute-direct-" + suffix++;
            }

            var hasDirect = outbounds.OfType<JsonObject>().Any(item =>
                string.Equals(item["tag"]?.GetValue<string>(), directTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item["protocol"]?.GetValue<string>(), "freedom", StringComparison.OrdinalIgnoreCase));
            if (!hasDirect)
                outbounds.Add(new JsonObject { ["tag"] = directTag, ["protocol"] = "freedom" });

            if (root["routing"] is not null && root["routing"] is not JsonObject)
                throw new InvalidDataException("Xray routing configuration must be an object.");
            var routing = root["routing"] as JsonObject;
            if (routing is null)
            {
                routing = new JsonObject();
                root["routing"] = routing;
            }
            if (routing["rules"] is not null && routing["rules"] is not JsonArray)
                throw new InvalidDataException("Xray routing rules must be an array.");
            var rules = routing["rules"] as JsonArray;
            if (rules is null)
            {
                rules = new JsonArray();
                routing["rules"] = rules;
            }
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (!string.Equals(rule["outboundTag"]?.GetValue<string>(), directTag, StringComparison.OrdinalIgnoreCase) ||
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
                    if (rules[i] is not JsonObject rule || (rule["outboundTag"] is null && rule["balancerTag"] is null)) continue;
                    var hasCondition = new[]
                    {
                        "domain", "ip", "port", "source", "sourcePort",
                        "network", "protocol", "user", "attrs"
                    }.Any(key => rule[key] is not null);
                    if (!hasCondition) { insertAt = i; break; }
                }

                var domainArray = new JsonArray(missing
                    .Select(domain => (JsonNode?)JsonValue.Create("domain:" + domain)).ToArray());
                rules.Insert(insertAt, new JsonObject
                {
                    ["type"] = "field",
                    ["domain"] = domainArray,
                    ["outboundTag"] = directTag
                });
            }

        }


        // Domain blocking is applied before bypass and catch-all rules so a domain
        // cannot escape the block list through a later direct/proxy rule.
        var blocked = SplitValues(blockedDomains)
            .Select(NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (blocked.Length > 0)
        {
            var outbounds = root["outbounds"] as JsonArray
                ?? throw new InvalidDataException("Xray configuration has no outbounds array.");
            var blockTag = outbounds.OfType<JsonObject>().FirstOrDefault(item =>
                string.Equals(item["protocol"]?.GetValue<string>(), "blackhole", StringComparison.OrdinalIgnoreCase))
                ?["tag"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(blockTag))
            {
                blockTag = "lroute-block";
                var suffix = 2;
                while (outbounds.OfType<JsonObject>().Any(item =>
                    string.Equals(item["tag"]?.GetValue<string>(), blockTag, StringComparison.OrdinalIgnoreCase)))
                    blockTag = "lroute-block-" + suffix++;
                outbounds.Add(new JsonObject { ["tag"] = blockTag, ["protocol"] = "blackhole" });
            }

            if (root["routing"] is not JsonObject routing)
                throw new InvalidDataException("Xray routing configuration must be an object when domain blocking is enabled.");
            if (routing["rules"] is not JsonArray rules)
                throw new InvalidDataException("Xray routing rules must be an array when domain blocking is enabled.");

            var existingBlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (!string.Equals(rule["outboundTag"]?.GetValue<string>(), blockTag, StringComparison.OrdinalIgnoreCase) ||
                    rule["domain"] is not JsonArray ruleDomains) continue;
                foreach (var value in ruleDomains)
                    if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                        existingBlocked.Add(text.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ? text[7..] : text);
            }

            var missingBlocked = blocked.Where(domain => !existingBlocked.Contains(domain)).ToArray();
            if (missingBlocked.Length > 0)
            {
                rules.Insert(0, new JsonObject
                {
                    ["type"] = "field",
                    ["domain"] = new JsonArray(missingBlocked.Select(domain =>
                        (JsonNode?)JsonValue.Create("domain:" + domain)).ToArray()),
                    ["outboundTag"] = blockTag
                });
            }
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string[] SplitValues(string? input) =>
        (input ?? "").Split(new[] { ',', ';', '\r', '\n' },
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
