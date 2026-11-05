using System.IO;
using System.Net;
using System.Net.Http;

namespace LRoute.Windows.Infrastructure;

public sealed record SubscriptionUserInfo(long UploadBytes, long DownloadBytes, long TotalBytes, long ExpiresAt)
{
    public long UsedBytes => UploadBytes + DownloadBytes;
}

public sealed record SubscriptionFetchResult(string Content, SubscriptionUserInfo? UserInfo);

/// <summary>Downloads a text subscription with bounded response size and timeout.</summary>
public sealed class SubscriptionFetcher
{
    private const int MaxBytes = 5 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };


    public async Task<string> GetContentAsync(string url, CancellationToken cancellationToken = default) =>
        (await FetchAsync(url, cancellationToken)).Content;

    public async Task<SubscriptionFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new FormatException("Subscription must be a valid HTTP or HTTPS URL.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LRoute-Windows/1.0");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaxBytes)
            throw new InvalidDataException("Subscription is larger than the 5 MB import limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaxBytes)
                throw new InvalidDataException("Subscription is larger than the 5 MB import limit.");
            output.Write(buffer, 0, read);
        }

        response.Headers.TryGetValues("subscription-userinfo", out var values);
        var info = ParseUserInfoHeader(values?.FirstOrDefault());
        return new SubscriptionFetchResult(System.Text.Encoding.UTF8.GetString(output.ToArray()), info);
    }

    public static SubscriptionUserInfo? ParseUserInfoHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator].Trim();
            if (long.TryParse(part[(separator + 1)..].Trim(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0)
                values[key] = value;
        }

        if (!values.ContainsKey("upload") && !values.ContainsKey("download") &&
            !values.ContainsKey("total") && !values.ContainsKey("expire")) return null;
        return new SubscriptionUserInfo(values.GetValueOrDefault("upload"),
            values.GetValueOrDefault("download"), values.GetValueOrDefault("total"),
            values.GetValueOrDefault("expire"));
    }
}
