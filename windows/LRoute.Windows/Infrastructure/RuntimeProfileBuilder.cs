using System.Text.Json;
using System.Text.Json.Nodes;
using LRoute.Windows.Domain;

namespace LRoute.Windows.Infrastructure;

/// <summary>Builds a minimal Xray JSON document for a single parsed proxy profile.</summary>
public static class RuntimeProfileBuilder
{
    public static string Build(ProxyProfile profile, int localSocksPort = 2080, int localHttpPort = 2081)
    {
        if (!string.IsNullOrWhiteSpace(profile.RawJson))
            return EnsureLocalProxyInbounds(profile.RawJson, localSocksPort, localHttpPort);
        var stream = new JsonObject { ["network"] = profile.Network };
        if (profile.Security == "tls")
        {
            stream["security"] = "tls";
            stream["tlsSettings"] = new JsonObject { ["serverName"] = string.IsNullOrWhiteSpace(profile.Sni) ? profile.Server : profile.Sni,
                ["fingerprint"] = profile.Fingerprint, ["alpn"] = new JsonArray(profile.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
        }
        if (profile.Security == "reality")
        {
            stream["security"] = "reality";
            stream["realitySettings"] = new JsonObject { ["serverName"] = profile.Sni, ["fingerprint"] = profile.Fingerprint,
                ["publicKey"] = profile.PublicKey, ["shortId"] = profile.ShortId };
        }
        if (profile.Network == "ws") stream["wsSettings"] = new JsonObject { ["path"] = profile.Path, ["headers"] = new JsonObject { ["Host"] = profile.Host } };
        if (profile.Network == "grpc") stream["grpcSettings"] = new JsonObject { ["serviceName"] = profile.Path, ["authority"] = profile.Host };
        if (profile.Network == "httpupgrade") stream["httpupgradeSettings"] = new JsonObject { ["path"] = profile.Path, ["host"] = profile.Host };

        var settings = new JsonObject();
        switch (profile.Protocol)
        {
            case "vless":
                settings["vnext"] = new JsonArray(new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = profile.UserId, ["encryption"] = "none", ["flow"] = profile.Flow }) });
                break;
            case "vmess":
                settings["vnext"] = new JsonArray(new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = profile.UserId, ["security"] = profile.Method is "" ? "auto" : profile.Method, ["alterId"] = profile.AlterId }) });
                break;
            case "trojan":
                settings["servers"] = new JsonArray(new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port, ["password"] = profile.Password });
                break;
            case "shadowsocks":
                settings["servers"] = new JsonArray(new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port, ["method"] = profile.Method, ["password"] = profile.Password });
                break;
            case "socks":
            case "http":
                var server = new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port };
                if (!string.IsNullOrEmpty(profile.UserId) || !string.IsNullOrEmpty(profile.Password))
                    server["users"] = new JsonArray(new JsonObject { ["user"] = profile.UserId, ["pass"] = profile.Password });
                settings["servers"] = new JsonArray(server);
                break;
            default: throw new NotSupportedException($"Protocol '{profile.Protocol}' is not supported by this runtime builder yet.");
        }
        var outbound = new JsonObject { ["tag"] = "proxy", ["protocol"] = profile.Protocol, ["settings"] = settings, ["streamSettings"] = stream };
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(
                new JsonObject { ["tag"] = "socks-in", ["listen"] = "127.0.0.1", ["port"] = localSocksPort, ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true, ["auth"] = "noauth" } },
                new JsonObject { ["tag"] = "http-in", ["listen"] = "127.0.0.1", ["port"] = localHttpPort, ["protocol"] = "http", ["settings"] = new JsonObject() }),
            ["outbounds"] = new JsonArray(outbound, new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }),
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = new JsonArray() }
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string EnsureLocalProxyInbounds(string sourceJson, int socksPort, int httpPort)
    {
        var root = JsonNode.Parse(sourceJson) as JsonObject
            ?? throw new InvalidDataException("Imported Xray configuration must be a JSON object.");
        var outbounds = root["outbounds"] as JsonArray
            ?? throw new InvalidDataException("Imported Xray configuration has no outbounds array.");
        var proxy = outbounds.OfType<JsonObject>().FirstOrDefault(item =>
        {
            var protocol = item["protocol"]?.GetValue<string>()?.ToLowerInvariant();
            return protocol is not null && protocol is not ("freedom" or "blackhole" or "dns" or "loopback");
        }) ?? throw new InvalidDataException("Imported Xray configuration has no proxy outbound.");

        var proxyTag = proxy["tag"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(proxyTag))
        {
            proxyTag = UniqueTag(outbounds.OfType<JsonObject>(), "lroute-proxy", item => item["tag"]?.GetValue<string>());
            proxy["tag"] = proxyTag;
        }

        var inbounds = root["inbounds"] as JsonArray;
        if (inbounds is null)
        {
            inbounds = new JsonArray();
            root["inbounds"] = inbounds;
        }

        var socksTag = EnsureLocalInbound(inbounds, "socks", socksPort, "lroute-socks-in");
        var httpTag = EnsureLocalInbound(inbounds, "http", httpPort, "lroute-http-in");

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

        var tags = new[] { socksTag, httpTag };
        var alreadyRouted = rules.OfType<JsonObject>().Any(rule =>
            string.Equals(rule["outboundTag"]?.GetValue<string>(), proxyTag, StringComparison.Ordinal) &&
            rule["inboundTag"] is JsonArray inboundTags &&
            tags.All(tag => inboundTags.Any(value => string.Equals(value?.GetValue<string>(), tag, StringComparison.Ordinal))));
        if (!alreadyRouted)
        {
            var insertAt = rules.Count;
            for (var i = 0; i < rules.Count; i++)
            {
                if (rules[i] is not JsonObject rule || rule["outboundTag"] is null) continue;
                var hasDestinationCondition = new[]
                {
                    "domain", "ip", "port", "source", "sourcePort",
                    "network", "protocol", "user", "attrs"
                }.Any(key => rule[key] is not null);
                if (!hasDestinationCondition)
                {
                    insertAt = i;
                    break;
                }
            }

            rules.Insert(insertAt, new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(socksTag, httpTag),
                ["outboundTag"] = proxyTag
            });
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string EnsureLocalInbound(JsonArray inbounds, string protocol, int port, string preferredTag)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var existing = inbounds.OfType<JsonObject>().FirstOrDefault(item => InboundUsesPort(item, port));

        if (existing is not null)
        {
            if (!string.Equals(existing["protocol"]?.GetValue<string>(), protocol, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Imported Xray configuration already uses local port {port} for a different inbound protocol.");
            existing["listen"] = "127.0.0.1";
            var existingTag = existing["tag"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(existingTag)) return existingTag;
            var generatedTag = UniqueTag(inbounds.OfType<JsonObject>(), preferredTag, item => item["tag"]?.GetValue<string>());
            existing["tag"] = generatedTag;
            return generatedTag;
        }

        var tag = UniqueTag(inbounds.OfType<JsonObject>(), preferredTag, item => item["tag"]?.GetValue<string>());
        var settings = protocol == "socks"
            ? new JsonObject { ["auth"] = "noauth", ["udp"] = true }
            : new JsonObject();
        inbounds.Add(new JsonObject
        {
            ["tag"] = tag,
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = protocol,
            ["settings"] = settings
        });
        return tag;
    }

    private static bool InboundUsesPort(JsonObject inbound, int targetPort)
    {
        if (inbound["port"] is not JsonValue value) return false;
        if (value.TryGetValue<int>(out var numericPort)) return numericPort == targetPort;
        if (!value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return false;

        foreach (var segment in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = segment.Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == targetPort)
                return true;
            if (range.Length == 2 &&
                int.TryParse(range[0], out var first) &&
                int.TryParse(range[1], out var last) &&
                targetPort >= Math.Min(first, last) && targetPort <= Math.Max(first, last))
                return true;
        }
        return false;
    }

    private static string UniqueTag<T>(
        IEnumerable<T> items,
        string preferred,
        Func<T, string?> getTag)
    {
        var tags = new HashSet<string>(
            items.Select(getTag).Where(tag => !string.IsNullOrWhiteSpace(tag)).Cast<string>(),
            StringComparer.OrdinalIgnoreCase);
        if (!tags.Contains(preferred)) return preferred;
        var suffix = 2;
        while (tags.Contains(preferred + "-" + suffix)) suffix++;
        return preferred + "-" + suffix;
    }
}
