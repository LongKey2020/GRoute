using System.Text;
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
                foreach (var item in doc.RootElement.EnumerateArray())
                    AddJson(item, result);
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
        var id = user;
        var password = "";
        if (scheme is "trojan" or "ss" && user.Contains(':'))
        {
            var split = user.Split(':', 2);
            id = split[0]; password = split[1];
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
            Method = q["encryption"] ?? "",
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
            Security = Get("tls", "none"), Sni = Get("sni"), Host = Get("host"), Path = Get("path"),
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
        if (root.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
        {
            foreach (var outbound in outbounds.EnumerateArray()) AddJsonOutbound(outbound, output, root);
        }
        else AddJsonOutbound(root, output, root);
    }

    private static void AddJsonOutbound(JsonElement outbound, List<ProxyProfile> output, JsonElement root)
    {
        if (!outbound.TryGetProperty("protocol", out var proto)) return;
        var protocol = proto.GetString() ?? "";
        if (protocol is "freedom" or "blackhole" or "dns" or "loopback") return;
        var raw = root.GetRawText();
        output.Add(new ProxyProfile { Name = outbound.TryGetProperty("tag", out var tag) ? tag.GetString() ?? protocol : protocol,
            Protocol = protocol, RawJson = raw, SourceUri = "json:" + raw });
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

    private static string PadBase64(string value) => value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
}
