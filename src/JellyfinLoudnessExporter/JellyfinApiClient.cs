using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JellyfinLoudnessExporter;

internal sealed class JellyfinApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public JellyfinApiClient(string baseUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is required.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token is required.");
        }

        if (baseUrl.Contains('@') || baseUrl.Contains("://") && baseUrl.IndexOf('@') > baseUrl.IndexOf("://", StringComparison.Ordinal))
        {
            // Reject userinfo in URL (credentials in URL).
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var probe) && !string.IsNullOrEmpty(probe.UserInfo))
            {
                throw new ArgumentException("Base URL must not contain credentials.");
            }
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Base URL must be an absolute http or https URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Base URL must not contain credentials.");
        }

        _baseUri = uri;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false // avoid cross-origin redirect credential leaks
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
            BaseAddress = _baseUri
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Jellyfin accepts media browser token header; do not log this value.
        _http.DefaultRequestHeaders.Add("X-Emby-Token", token);
    }

    public async Task<string> GetServerVersionAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "System/Info/Public"), ct).ConfigureAwait(false);
        await EnsureSuccess(resp).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString() ?? "unknown";
        }

        return "unknown";
    }

    public async Task<(string? UserId, List<SourceItem> Items)> FetchAllAudioItemsAsync(CancellationToken ct)
    {
        // Prefer API-key style: /Items requires userId unless using elevated key that still needs userId on recent servers.
        // Resolve a user id via /Users/Me when token is a user session; for API keys require --user-id.
        string? userId = null;
        try
        {
            using var me = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "Users/Me"), ct).ConfigureAwait(false);
            if (me.StatusCode == HttpStatusCode.OK)
            {
                await using var stream = await me.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("Id", out var idEl))
                {
                    userId = idEl.GetString();
                }
            }
        }
        catch
        {
            // Fall through; caller may supply user id.
        }

        return (userId, []);
    }

    public async Task<List<SourceItem>> FetchAudioItemsAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("userId is required for /Items queries.");
        }

        const int pageSize = 200;
        var start = 0;
        int? total = null;
        var items = new List<SourceItem>();

        while (true)
        {
            var relative =
                $"Items?userId={Uri.EscapeDataString(userId)}" +
                $"&includeItemTypes=Audio&recursive=true&fields=Path" +
                $"&startIndex={start}&limit={pageSize}&enableTotalRecordCount=true";

            using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, relative), ct).ConfigureAwait(false);
            await EnsureSuccess(resp).ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            OfflineItemsReader.RejectDuplicateProperties(bytes);

            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Items response missing Items array.");
            }

            if (root.TryGetProperty("TotalRecordCount", out var totalEl) && totalEl.TryGetInt32(out var t))
            {
                if (total is null)
                {
                    total = t;
                }
                else if (total.Value != t)
                {
                    throw new InvalidDataException("TotalRecordCount changed between pages.");
                }
            }

            var pageCount = 0;
            foreach (var el in arr.EnumerateArray())
            {
                pageCount++;
                items.Add(ParseApiItem(el));
            }

            start += pageCount;
            if (pageCount == 0)
            {
                break;
            }

            if (total is not null && start >= total.Value)
            {
                break;
            }

            if (pageCount < pageSize)
            {
                break;
            }
        }

        if (total is not null && items.Count != total.Value)
        {
            throw new InvalidDataException(
                $"Item count mismatch: fetched {items.Count}, TotalRecordCount={total.Value}.");
        }

        return items;
    }

    private static SourceItem ParseApiItem(JsonElement el)
    {
        string? id = el.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
        string? albumId = null;
        if (el.TryGetProperty("AlbumId", out var albumEl) && albumEl.ValueKind != JsonValueKind.Null)
        {
            albumId = albumEl.ToString();
        }

        string? path = el.TryGetProperty("Path", out var pathEl) ? pathEl.GetString() : null;
        double? trackGain = null;
        double? albumGain = null;
        if (el.TryGetProperty("NormalizationGain", out var ng) && ng.ValueKind == JsonValueKind.Number && ng.TryGetDouble(out var ngv))
        {
            trackGain = ngv;
        }

        if (el.TryGetProperty("AlbumNormalizationGain", out var ag) && ag.ValueKind == JsonValueKind.Number && ag.TryGetDouble(out var agv))
        {
            albumGain = agv;
        }

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("API item missing Id or Path.");
        }

        return new SourceItem
        {
            Id = id,
            AlbumId = string.IsNullOrWhiteSpace(albumId) ? null : albumId,
            Path = path,
            NormalizationGain = trackGain,
            AlbumNormalizationGain = albumGain
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is >= 300 and < 400)
        {
            resp.Dispose();
            throw new InvalidOperationException("Refusing to follow HTTP redirects (credential leak protection).");
        }

        return resp;
    }

    private static async Task EnsureSuccess(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var code = (int)resp.StatusCode;
        resp.Dispose();
        throw new InvalidOperationException($"Jellyfin HTTP {code}");
    }

    public void Dispose() => _http.Dispose();

    public static string ReadTokenMasked()
    {
        Console.Write("Jellyfin API token (input hidden; not stored): ");
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }

        return sb.ToString();
    }
}
