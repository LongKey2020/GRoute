using System.Text.Json;
using System.Text.Json.Nodes;
using LRoute.Windows.Domain;

namespace LRoute.Windows.Infrastructure;

/// <summary>Builds a minimal Xray JSON document for a single parsed proxy profile.</summary>
public static class RuntimeProfileBuilder
{
    public static string Build(ProxyProfile profile, int localSocksPort = 2080, int localHttpPort = 2081)
    {
        if (!string.IsNullOrWhiteSpace(profile.RawJson)) return profile.RawJson;
        var stream = new JsonObject { ["network"] = profile.Network };
        if (profile.Security == "tls")
            stream["tlsSettings"] = new JsonObject { ["serverName"] = string.IsNullOrWhiteSpace(profile.Sni) ? profile.Server : profile.Sni,
                ["fingerprint"] = profile.Fingerprint, ["alpn"] = new JsonArray(profile.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
        if (profile.Security == "reality")
            stream["realitySettings"] = new JsonObject { ["serverName"] = profile.Sni, ["fingerprint"] = profile.Fingerprint,
                ["publicKey"] = profile.PublicKey, ["shortId"] = profile.ShortId };
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
}
