using System.IO;
using System.Net;
using System.Text.Json.Nodes;

namespace LRoute.Windows;

public static class XrayTunConfigBuilder
{
    public static async Task<string> CreateAsync(
        string sourceConfigPath,
        string runtimeDirectory,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(sourceConfigPath, cancellationToken);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Xray configuration must be a JSON object.");

        var outbounds = root["outbounds"] as JsonArray
            ?? throw new InvalidDataException("Xray configuration has no outbounds array.");

        var proxy = outbounds
            .OfType<JsonObject>()
            .FirstOrDefault(item =>
            {
                var protocol = item["protocol"]?.GetValue<string>()?.ToLowerInvariant();
                return protocol is not null && protocol is not ("freedom" or "blackhole" or "dns" or "loopback")
                    && !string.Equals(item["tag"]?.GetValue<string>(), "direct", StringComparison.OrdinalIgnoreCase);
            });

        if (proxy is null)
            throw new InvalidDataException("No proxy outbound was found. Add a VLESS, VMess, Trojan, or Shadowsocks outbound first.");

        var proxyTag = proxy["tag"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(proxyTag))
        {
            proxyTag = "lroute-proxy";
            proxy["tag"] = proxyTag;
        }

        if (root["inbounds"] is not null && root["inbounds"] is not JsonArray)
            throw new InvalidDataException("Xray inbounds must be an array.");
        var inbounds = root["inbounds"] as JsonArray;
        if (inbounds is null)
        {
            inbounds = new JsonArray();
            root["inbounds"] = inbounds;
        }

        var tun = inbounds.OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(
                item["protocol"]?.GetValue<string>(), "tun", StringComparison.OrdinalIgnoreCase));

        if (tun is null)
        {
            tun = new JsonObject();
            inbounds.Insert(0, tun);
        }

        var previousTunTag = tun["tag"]?.GetValue<string>();
        const string tunTag = "lroute-tun-in";
        tun["tag"] = tunTag;
        tun["port"] = 0;
        tun["protocol"] = "tun";
        var configuredDns = (root["dns"] as JsonObject)?["servers"] as JsonArray;
        var tunDnsServers = configuredDns?
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var server) ? server : null)
            .Where(server => server is not null && IPAddress.TryParse(server, out _))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (tunDnsServers.Length == 0)
            tunDnsServers = ["1.1.1.1", "8.8.8.8", "2606:4700:4700::1111", "2001:4860:4860::8888"];

        tun["settings"] = new JsonObject
        {
            ["name"] = "lroute0",
            ["desc"] = "LRoute",
            ["mtu"] = 1500,
            ["gateway"] = new JsonArray("10.0.0.1/16", "fc00::1/64"),
            ["dns"] = new JsonArray(tunDnsServers.Select(server => (JsonNode?)JsonValue.Create(server)).ToArray()),
            ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0", "::/0"),
            ["autoOutboundsInterface"] = "auto",
            ["autoSystemWfpBlockLeak"] = new JsonArray("dns", "misconfigtun")
        };

        if (root["routing"] is not null && root["routing"] is not JsonObject)
            throw new InvalidDataException("Xray routing must be an object.");
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

        if (!string.IsNullOrWhiteSpace(previousTunTag) && previousTunTag != tunTag)
        {
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (rule["inboundTag"] is not JsonArray tags) continue;
                for (var i = 0; i < tags.Count; i++)
                {
                    if (string.Equals(tags[i]?.GetValue<string>(), previousTunTag, StringComparison.Ordinal))
                        tags[i] = tunTag;
                }
            }
        }

        var hasDefaultTunRoute = rules.OfType<JsonObject>().Any(rule =>
            rule["inboundTag"] is JsonArray tags &&
            tags.Any(tag => string.Equals(tag?.GetValue<string>(), tunTag, StringComparison.Ordinal)) &&
            string.Equals(rule["outboundTag"]?.GetValue<string>(), proxyTag, StringComparison.Ordinal));

        if (!hasDefaultTunRoute)
        {
            var fallbackIndex = rules.Count;
            for (var i = 0; i < rules.Count; i++)
            {
                if (rules[i] is not JsonObject rule ||
                    (rule["outboundTag"] is null && rule["balancerTag"] is null) ||
                    rule["inboundTag"] is JsonArray)
                    continue;

                var hasMatchCondition = new[]
                {
                    "domain", "ip", "port", "source", "sourcePort",
                    "network", "protocol", "user", "attrs"
                }.Any(key => rule[key] is not null);

                if (!hasMatchCondition)
                {
                    fallbackIndex = i;
                    break;
                }
            }

            rules.Insert(fallbackIndex, new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(tunTag),
                ["outboundTag"] = proxyTag
            });
        }

        Directory.CreateDirectory(runtimeDirectory);
        var runtimePath = Path.Combine(runtimeDirectory, $"xray-tun-{Guid.NewGuid():N}.json");
        if (root["log"] is not null && root["log"] is not JsonObject)
            throw new InvalidDataException("Xray log configuration must be an object.");
        var log = root["log"] as JsonObject;
        if (log is null)
        {
            log = new JsonObject();
            root["log"] = log;
        }
        if (log["loglevel"] is null) log["loglevel"] = "warning";
        log["error"] = runtimePath + ".error.log";
        await File.WriteAllTextAsync(runtimePath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        }), cancellationToken);
        return runtimePath;
    }
}
