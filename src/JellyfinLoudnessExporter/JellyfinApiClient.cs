using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JellyfinLoudnessExporter;

internal sealed class JellyfinApiClient : IDisposable
{
    public const long MaxResponseBytes = 64L * 1024L * 1024L; // 64 MiB per response
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestTimeout;

    public JellyfinApiClient(string baseUrl, string token, HttpMessageHandler? handler = null, bool allowHttp = false, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "token_required");
        }

        _requestTimeout = timeout ?? DefaultTimeout;
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new SafeErrorException(SafeError.Category.Usage, "timeout_invalid");
        }
        var baseUri = NormalizeBaseUri(baseUrl, allowHttp);

        if (handler is null)
        {
            handler = new HttpClientHandler { AllowAutoRedirect = false };
            _ownsClient = true;
        }
        else
        {
            _ownsClient = true; // we wrap it
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // linked request deadline covers headers AND streamed body
            BaseAddress = baseUri
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("X-Emby-Token", token);
        _http.MaxResponseContentBufferSize = MaxResponseBytes;
    }

    internal static Uri NormalizeBaseUri(string baseUrl, bool allowHttp)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "base_url_required");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "base_url_invalid");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "base_url_userinfo");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "base_url_query_or_fragment");
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!allowHttp)
            {
                throw new SafeErrorException(SafeError.Category.Usage, "base_url_http_not_allowed");
            }
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SafeErrorException(SafeError.Category.Usage, "base_url_scheme");
        }

        // Ensure trailing slash so relative "Items" resolves under /jellyfin/ correctly.
        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        builder.Query = null;
        builder.Fragment = null;
        return builder.Uri;
    }

    public async Task<string?> GetServerVersionAsync(CancellationToken ct)
    {
        var (_, bytes) = await GetResponseBytesAsync("System/Info/Public", ct).ConfigureAwait(false);
        OfflineItemsReader.RejectDuplicateProperties(bytes);
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        return null;
    }

    public async Task<string?> TryGetUserIdFromMeAsync(CancellationToken ct)
    {
        var (status, bytes) = await GetResponseBytesAsync("Users/Me", ct, allowNonSuccess: true).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            return null;
        }

        OfflineItemsReader.RejectDuplicateProperties(bytes);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.TryGetProperty("Id", out var id) ? id.GetString() : null;
    }

    public async Task<List<SourceItem>> FetchAudioItemsAsync(string? userId, CancellationToken ct)
    {
        const int pageSize = 200;
        var start = 0;
        int? total = null;
        var items = new List<SourceItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var qs = new StringBuilder("Items?includeItemTypes=Audio&recursive=true&fields=Path");
            qs.Append("&enableTotalRecordCount=true");
            qs.Append("&sortBy=SortName&sortOrder=Ascending");
            qs.Append(System.Globalization.CultureInfo.InvariantCulture, $"&startIndex={start}&limit={pageSize}");
            if (!string.IsNullOrWhiteSpace(userId))
            {
                qs.Append("&userId=").Append(Uri.EscapeDataString(userId));
            }

            var (_, bytes) = await GetResponseBytesAsync(qs.ToString(), ct).ConfigureAwait(false);
            OfflineItemsReader.RejectDuplicateProperties(bytes);

            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                throw new SafeErrorException(SafeError.Category.Http, "items_missing");
            }

            if (!root.TryGetProperty("TotalRecordCount", out var totalEl) ||
                totalEl.ValueKind != JsonValueKind.Number || !totalEl.TryGetInt32(out var t) || t < 0)
            {
                throw new SafeErrorException(SafeError.Category.Http, "total_missing_or_invalid");
            }

            if (total is null) total = t;
            else if (total.Value != t)
            {
                throw new SafeErrorException(SafeError.Category.Http, "total_changed");
            }

            var pageCount = 0;
            foreach (var el in arr.EnumerateArray())
            {
                pageCount++;
                var item = ParseApiItem(el);
                if (!seenIds.Add(item.Id))
                {
                    throw new SafeErrorException(SafeError.Category.Http, "duplicate_item_id");
                }

                items.Add(item);
            }

            // Progress check: startIndex must advance
            if (pageCount == 0)
            {
                break;
            }

            var newStart = start + pageCount;
            if (newStart <= start)
            {
                throw new SafeErrorException(SafeError.Category.Http, "pagination_no_progress");
            }

            start = newStart;
            if (total is not null && start >= total.Value) break;
            if (pageCount < pageSize) break;
        }

        if (total is not null && items.Count != total.Value)
        {
            throw new SafeErrorException(SafeError.Category.Http, "count_mismatch");
        }

        return items;
    }

    internal static SourceItem ParseApiItem(JsonElement el)
    {
        OfflineItemsReader.RejectLogicalAliasDupesPublic(el);

        string? id = null;
        string? albumId = null;
        string? path = null;
        var trackStatus = GainStatus.Missing;
        var albumStatus = GainStatus.Missing;
        double? trackGain = null;
        double? albumGain = null;

        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "Id":
                    id = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    break;
                case "AlbumId":
                    if (prop.Value.ValueKind != JsonValueKind.Null)
                    {
                        albumId = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    }
                    break;
                case "Path":
                    if (prop.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new SafeErrorException(SafeError.Category.Json, "path_type");
                    }
                    path = prop.Value.GetString();
                    break;
                case "NormalizationGain":
                {
                    var g = GainParser.Parse(prop.Value);
                    trackStatus = Map(g);
                    trackGain = g.Value;
                    break;
                }
                case "AlbumNormalizationGain":
                {
                    var g = GainParser.Parse(prop.Value);
                    albumStatus = Map(g);
                    albumGain = g.Value;
                    break;
                }
                // unknown ignored
            }
        }

        if (string.IsNullOrWhiteSpace(id) || path is null)
        {
            throw new SafeErrorException(SafeError.Category.Json, "api_item_incomplete");
        }

        return new SourceItem
        {
            Id = id,
            AlbumId = albumId,
            Path = path,
            NormalizationGain = trackGain,
            AlbumNormalizationGain = albumGain,
            TrackGainStatus = trackStatus,
            AlbumGainStatus = albumStatus
        };
    }

    private static GainStatus Map(GainParser.ParseResult g) => g.Kind switch
    {
        GainParser.ResultKind.Missing => GainStatus.Missing,
        GainParser.ResultKind.Finite => GainStatus.Finite,
        GainParser.ResultKind.NonFinite => GainStatus.NonFinite,
        _ => GainStatus.InvalidType
    };

    // One deadline owns the complete request, including response stream creation and every body read.
    private async Task<(HttpStatusCode Status, byte[] Bytes)> GetResponseBytesAsync(
        string relative, CancellationToken ct, bool allowNonSuccess = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relative);
            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                throw new SafeErrorException(SafeError.Category.Http, "redirect_refused");
            }

            if (!resp.IsSuccessStatusCode)
            {
                if (allowNonSuccess) return (resp.StatusCode, []); // never log or read error bodies
                throw new SafeErrorException(SafeError.Category.Http, $"status_{(int)resp.StatusCode}");
            }

            var bytes = await ReadBoundedAsync(resp, deadline.Token).ConfigureAwait(false);
            return (resp.StatusCode, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new SafeErrorException(SafeError.Category.Http, "timeout");
        }
        catch (HttpRequestException)
        {
            // DNS failure, connection refused, and TLS errors land here. The message can contain the host, so it is not printed.
            throw new SafeErrorException(SafeError.Category.Http, "unreachable");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
        {
            throw new SafeErrorException(SafeError.Category.Http, "response_too_large");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (total > MaxResponseBytes)
            {
                throw new SafeErrorException(SafeError.Category.Http, "response_too_large");
            }

            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

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
                if (sb.Length > 0) sb.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }

        return sb.ToString();
    }
}
