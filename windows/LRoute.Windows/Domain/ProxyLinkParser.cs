using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Web;

namespace LRoute.Windows.Domain;

/// <summary>Parses common Xray share links and preserves raw Xray JSON profiles.</summary>
public static class ProxyLinkParser
{
    public static IReadOnlyList<ProxyProfile> ParseMany(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return [];
        if (text.StartsWith("{") || text.StartsWith("["))
        {
            using var doc = JsonDocument.Parse(text);
            var result = new List<ProxyProfile>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var items = doc.RootElement.EnumerateArray().ToArray();
                if (items.Length > 0 && items.All(item =>
                    item.ValueKind == JsonValueKind.Object && item.TryGetProperty("protocol", out _)))
                    AddOutboundBundle(items, result);
                else
                    foreach (var item in items) AddJson(item, result);
            }
            else AddJson(doc.RootElement, result);
            return result;
        }

        var decoded = DecodeSubscription(text);
        var profiles = new List<ProxyProfile>();
        foreach (var line in decoded.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var profile = ParseLink(line);
            if (profile is not null) profiles.Add(profile);
        }
        return profiles;
    }

    public static ProxyProfile? ParseLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("vless" or "vmess" or "trojan" or "ss" or "socks" or "socks5" or "http")) return null;

        if (scheme == "vmess") return ParseVmess(uri, value);
        if (scheme == "ss") return ParseShadowsocks(uri, value);

        var user = Uri.UnescapeDataString(uri.UserInfo);
        var id = "";
        var password = "";
        if (scheme is "vless" or "vmess")
            id = user;
        else if (scheme is "trojan" or "socks" or "socks5" or "http")
        {
            var split = user.Split(':', 2);
            id = split[0];
            if (split.Length == 2) password = split[1];
        }
        var q = HttpUtility.ParseQueryString(uri.Query);
        var protocol = scheme switch { "socks5" => "socks", _ => scheme };
        return new ProxyProfile
        {
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')) is { Length: > 0 } n ? n : $"{uri.Host}:{uri.Port}",
            Protocol = protocol, Server = uri.Host, Port = uri.Port, UserId = id, Password = password,
            Network = q["type"] ?? q["network"] ?? "tcp",
            Security = q["security"] ?? (q["sni"] is null ? "none" : "tls"),
            Sni = q["sni"] ?? q["serverName"] ?? "",
            Host = q["host"] ?? q["authority"] ?? "",
            Path = q["path"] ?? "",
            Flow = q["flow"] ?? "",
            PublicKey = q["pbk"] ?? q["publicKey"] ?? "",
            ShortId = q["sid"] ?? q["shortId"] ?? "",
            Fingerprint = q["fp"] ?? "chrome",
            Alpn = q["alpn"] ?? "",
            Method = q["encryption"] ?? q["method"] ?? "",
            SourceUri = value
        };
    }

    private static ProxyProfile ParseVmess(Uri uri, string original)
    {
        var encoded = original["vmess://".Length..].Split('#')[0];
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(encoded))));
        var root = doc.RootElement;
        string Get(string key, string fallback = "") => root.TryGetProperty(key, out var v) ? v.ToString() : fallback;
        return new ProxyProfile
        {
            Name = Uri.UnescapeDataString(original.Split('#').LastOrDefault() ?? "") is { Length: > 0 } n ? n : Get("ps", Get("add")),
            Protocol = "vmess", Server = Get("add"), Port = int.TryParse(Get("port"), out var p) ? p : 0,
            UserId = Get("id"), Method = Get("scy", "auto"), Network = Get("net", "tcp"),
            Security = NormalizeSecurity(Get("tls", "none")), Sni = Get("sni"), Host = Get("host"), Path = Get("path"),
            SourceUri = original
        };
    }

    private static ProxyProfile ParseShadowsocks(Uri uri, string original)
    {
        var raw = original["ss://".Length..].Split('#')[0];
        var authority = raw.Split('@');
        string methodPassword;
        string hostPort;
        if (authority.Length == 2) { methodPassword = authority[0]; hostPort = authority[1]; }
        else
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(raw)));
            var parts = decoded.Split('@', 2);
            if (parts.Length != 2) throw new FormatException("Invalid Shadowsocks link.");
            methodPassword = parts[0]; hostPort = parts[1];
        }
        if (methodPassword.Contains(':') == false)
            methodPassword = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(methodPassword)));
        var auth = methodPassword.Split(':', 2);
        var endpoint = new Uri("ss://" + Uri.EscapeDataString(auth[0]) + ":" + Uri.EscapeDataString(auth[1]) + "@" + hostPort);
        return new ProxyProfile { Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')) is { Length: > 0 } n ? n : endpoint.Host,
            Protocol = "shadowsocks", Server = endpoint.Host, Port = endpoint.Port, Method = auth[0], Password = auth[1], SourceUri = original };
    }

    private static void AddJson(JsonElement root, List<ProxyProfile> output)
    {
        if (root.ValueKind != JsonValueKind.Object) return;

        // A complete Xray document is one importable profile. Keep the entire
        // document together so routing rules, DNS, inbounds and outbound tags
        // are not lost or duplicated when the user selects it.
        if (root.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
        {
            var firstProxy = outbounds.EnumerateArray()
                .FirstOrDefault(IsProxyOutbound);
            if (firstProxy.ValueKind == JsonValueKind.Undefined) return;

            var protocol = firstProxy.TryGetProperty("protocol", out var proto)
                ? proto.GetString() ?? "json" : "json";
            var name = firstProxy.TryGetProperty("tag", out var tag)
                ? tag.GetString() ?? protocol : protocol;
            var raw = root.GetRawText();
            output.Add(new ProxyProfile
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Imported Xray configuration" : name,
                Protocol = "json",
                RawJson = raw,
                SourceUri = "json:" + raw
            });
            return;
        }

        // A standalone outbound object (or an array of outbound objects) is not
        // a complete Xray config. Wrap it with local proxy inbounds and routing
        // so it can be validated and launched by Xray-core.
        if (root.TryGetProperty("protocol", out _))
            AddOutboundBundle([root], output);
    }

    private static void AddOutboundBundle(IEnumerable<JsonElement> items, List<ProxyProfile> output)
    {
        var proxyOutbounds = items
            .Where(IsProxyOutbound)
            .Select(item => JsonNode.Parse(item.GetRawText()))
            .Where(node => node is not null)
            .Cast<JsonNode>()
            .ToArray();
        if (proxyOutbounds.Length == 0) return;

        // Ensure every outbound has a stable tag; route the local listeners to
        // the first proxy outbound while retaining the remaining choices.
        var firstTag = "proxy";
        for (var i = 0; i < proxyOutbounds.Length; i++)
        {
            if (proxyOutbounds[i] is not JsonObject outbound) continue;
            if (string.IsNullOrWhiteSpace(outbound["tag"]?.GetValue<string>()))
                outbound["tag"] = i == 0 ? firstTag : $"proxy-{i + 1}";
            if (i == 0) firstTag = outbound["tag"]!.GetValue<string>();
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "socks-in", ["listen"] = "127.0.0.1", ["port"] = 2080,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true }
                },
                new JsonObject
                {
                    ["tag"] = "http-in", ["listen"] = "127.0.0.1", ["port"] = 2081,
                    ["protocol"] = "http", ["settings"] = new JsonObject()
                }),
            ["outbounds"] = new JsonArray(
                proxyOutbounds.Select(node => node.DeepClone()).ToArray()),
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["inboundTag"] = new JsonArray("socks-in", "http-in"),
                        ["outboundTag"] = firstTag
                    })
            }
        };

        var raw = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        output.Add(new ProxyProfile
        {
            Name = proxyOutbounds.Length == 1 ? firstTag : $"Imported Xray bundle ({proxyOutbounds.Length})",
            Protocol = "json",
            RawJson = raw,
            SourceUri = "json:" + raw
        });
    }

    private static bool IsProxyOutbound(JsonElement outbound)
    {
        if (outbound.ValueKind != JsonValueKind.Object ||
            !outbound.TryGetProperty("protocol", out var proto)) return false;
        var protocol = proto.GetString() ?? "";
        return protocol is not ("" or "freedom" or "blackhole" or "dns" or "loopback");
    }

    private static string DecodeSubscription(string text)
    {
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return text;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(text)));
            if (decoded.Contains("://")) return decoded;
        }
        catch { }
        return text;
    }

    private static string NormalizeSecurity(string value) =>
        value.Equals("tls", StringComparison.OrdinalIgnoreCase) ? "tls" :
        value.Equals("reality", StringComparison.OrdinalIgnoreCase) ? "reality" : "none";

    private static string PadBase64(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return normalized + new string('=', (4 - normalized.Length % 4) % 4);
    }
}
