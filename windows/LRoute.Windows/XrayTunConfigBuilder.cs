using System.IO;
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
        tun["settings"] = new JsonObject
        {
            ["name"] = "lroute0",
            ["desc"] = "LRoute",
            ["mtu"] = 1500,
            ["gateway"] = new JsonArray("10.0.0.1/16"),
            ["dns"] = new JsonArray("1.1.1.1", "8.8.8.8"),
            ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0"),
            ["autoOutboundsInterface"] = "auto",
            ["autoSystemWfpBlockLeak"] = new JsonArray("dns", "misconfigtun")
        };

        var routing = root["routing"] as JsonObject;
        if (routing is null)
        {
            routing = new JsonObject();
            root["routing"] = routing;
        }

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
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(tunTag),
                ["outboundTag"] = proxyTag
            });
        }

        Directory.CreateDirectory(runtimeDirectory);
        var runtimePath = Path.Combine(runtimeDirectory, $"xray-tun-{Guid.NewGuid():N}.json");
        var log = root["log"] as JsonObject ?? new JsonObject();
        if (log["loglevel"] is null) log["loglevel"] = "warning";
        log["error"] = runtimePath + ".error.log";
        root["log"] = log;
        await File.WriteAllTextAsync(runtimePath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        }), cancellationToken);
        return runtimePath;
    }
}
