using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

public sealed class ArtistArtworkService : IDisposable
{
    private const int MaxAttempts = 3;
    private const int MaxRedirects = 5;
    private const int MaxMetadataBytes = 512 * 1024;
    private const int MaxArtworkBytes = 8 * 1024 * 1024;
    private const int MaxArtworkDimension = 4096;
    private const long MaxArtworkPixels = 8_000_000;
    private const int MaxCachedArtworkEntries = 24;
    private const long MaxCachedArtworkBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly Regex HtmlTagPattern = new(
        "<[^>]*>",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, ArtistArtworkInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private long _cachedArtworkBytes;
    private bool _disposed;

    public ArtistArtworkService()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal ArtistArtworkService(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        }
    }

    public async Task<ArtistArtworkInfo?> FetchAsync(
        string artistId,
        string imagePageUrl,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedArtistId = artistId?.Trim() ?? string.Empty;
        if (normalizedArtistId.Length == 0
            || normalizedArtistId.Length > 128
            || !TryParseCommonsFilePage(imagePageUrl, out var sourcePage, out var fileTitle))
        {
            return null;
        }

        lock (_cacheSync)
        {
            if (_cache.TryGetValue(normalizedArtistId, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var metadataUri = BuildMetadataUri(fileTitle);
            using var metadataResponse = await SendWithRetriesAsync(
                metadataUri,
                RequestKind.Metadata,
                "application/json",
                cancellationToken);
            if (!metadataResponse.IsSuccessStatusCode
                || !IsJsonContentType(metadataResponse.Content.Headers.ContentType?.MediaType))
            {
                return null;
            }

            var metadataBytes = await ReadBoundedWithDeadlineAsync(
                metadataResponse.Content,
                MaxMetadataBytes,
                cancellationToken);
            var payload = JsonSerializer.Deserialize<CommonsResponse>(metadataBytes);
            var imageInfo = (payload?.Query?.Pages ?? [])
                .SelectMany(page => page.ImageInfo ?? [])
                .FirstOrDefault();
            if (imageInfo is null
                || imageInfo.ThumbWidth is not > 0
                || imageInfo.ThumbHeight is not > 0
                || imageInfo.ThumbWidth > MaxArtworkDimension
                || imageInfo.ThumbHeight > MaxArtworkDimension
                || (long)imageInfo.ThumbWidth.Value * imageInfo.ThumbHeight.Value > MaxArtworkPixels
                || !TryNormalizeUploadUri(imageInfo.ThumbUrl, out var artworkUri))
            {
                return null;
            }

            var expectedMime = NormalizeSupportedImageMime(imageInfo.ThumbMime ?? imageInfo.Mime);
            if (expectedMime.Length == 0)
            {
                return null;
            }

            using var artworkResponse = await SendWithRetriesAsync(
                artworkUri,
                RequestKind.Artwork,
                "image/*",
                cancellationToken);
            if (!artworkResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var actualMime = NormalizeSupportedImageMime(
                artworkResponse.Content.Headers.ContentType?.MediaType);
            if (actualMime.Length == 0 || !string.Equals(actualMime, expectedMime, StringComparison.Ordinal))
            {
                return null;
            }

            var data = await ReadBoundedWithDeadlineAsync(
                artworkResponse.Content,
                MaxArtworkBytes,
                cancellationToken);
            var decoded = ImageArtworkLoader.Load(data, cancellationToken).Preview;
            if (decoded.PixelWidth > MaxArtworkDimension
                || decoded.PixelHeight > MaxArtworkDimension
                || (long)decoded.PixelWidth * decoded.PixelHeight > MaxArtworkPixels)
            {
                return null;
            }

            var result = new ArtistArtworkInfo(
                normalizedArtistId,
                data,
                sourcePage.AbsoluteUri,
                ReadMetadataText(imageInfo.ExtMetadata ?? [], "Artist", "Credit", 200),
                ReadMetadataText(imageInfo.ExtMetadata ?? [], "LicenseShortName", null, 80),
                ReadHttpsUrl(imageInfo.ExtMetadata ?? [], "LicenseUrl"));

            lock (_cacheSync)
            {
                if (_cache.TryGetValue(normalizedArtistId, out var previous))
                {
                    _cachedArtworkBytes -= previous.Data.LongLength;
                }
                else
                {
                    _cacheOrder.Enqueue(normalizedArtistId);
                }

                _cache[normalizedArtistId] = result;
                _cachedArtworkBytes += result.Data.LongLength;
                while ((_cache.Count > MaxCachedArtworkEntries
                        || _cachedArtworkBytes > MaxCachedArtworkBytes)
                       && _cacheOrder.TryDequeue(out var oldestArtistId))
                {
                    if (_cache.Remove(oldestArtistId, out var evicted))
                    {
                        _cachedArtworkBytes -= evicted.Data.LongLength;
                    }
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsOptionalFailure(ex))
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_cacheSync)
        {
            _cache.Clear();
            _cacheOrder.Clear();
            _cachedArtworkBytes = 0;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                                     | DecompressionMethods.Deflate
                                     | DecompressionMethods.Brotli
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Uri uri,
        RequestKind kind,
        string accept,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCancellation.CancelAfter(AttemptTimeout);
                response = await SendOnceAsync(uri, kind, accept, attemptCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransientTransportFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                continue;
            }

            if (attempt >= MaxAttempts || !IsTransientStatus(response.StatusCode))
            {
                return response;
            }

            var delay = GetRetryDelay(response, attempt);
            if (delay is null)
            {
                return response;
            }

            response.Dispose();
            if (delay.Value > TimeSpan.Zero)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Uri uri,
        RequestKind kind,
        string accept,
        CancellationToken cancellationToken)
    {
        var current = NormalizeRequestUri(uri, kind);
        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd(accept);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException("The artist image service returned an invalid redirect.");
            }

            var redirected = location.IsAbsoluteUri ? location : new Uri(current, location);
            current = NormalizeRequestUri(redirected, kind);
        }

        throw new InvalidDataException("The artist image service returned too many redirects.");
    }

    private static Uri NormalizeRequestUri(Uri uri, RequestKind kind)
    {
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("The artist image service returned an invalid address.");
        }

        var expectedHost = kind == RequestKind.Metadata
            ? "commons.wikimedia.org"
            : "upload.wikimedia.org";
        if (!string.Equals(uri.IdnHost, expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The artist image service returned an untrusted address.");
        }

        return uri;
    }

    private static bool TryParseCommonsFilePage(
        string? value,
        out Uri pageUri,
        out string fileTitle)
    {
        pageUri = null!;
        fileTitle = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !candidate.IsDefaultPort
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.Equals(candidate.IdnHost, "commons.wikimedia.org", StringComparison.OrdinalIgnoreCase)
            || !candidate.AbsolutePath.StartsWith("/wiki/File:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName;
        try
        {
            fileName = Uri.UnescapeDataString(candidate.AbsolutePath["/wiki/File:".Length..]).Trim();
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (fileName.Length is 0 or > 384
            || fileName.Any(character => char.IsControl(character)))
        {
            return false;
        }

        pageUri = new Uri(candidate.GetLeftPart(UriPartial.Path));
        fileTitle = "File:" + fileName;
        return true;
    }

    private static bool TryNormalizeUploadUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate))
        {
            return false;
        }

        try
        {
            uri = NormalizeRequestUri(candidate, RequestKind.Artwork);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static Uri BuildMetadataUri(string fileTitle)
    {
        var query = string.Join(
            '&',
            "action=query",
            "format=json",
            "formatversion=2",
            "redirects=1",
            "prop=imageinfo",
            "iiprop=url%7Csize%7Cmime%7Cextmetadata",
            "iiurlwidth=512",
            "iiextmetadatafilter=Artist%7CCredit%7CLicenseShortName%7CLicenseUrl%7CAttributionRequired",
            "titles=" + Uri.EscapeDataString(fileTitle));
        return new Uri("https://commons.wikimedia.org/w/api.php?" + query);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("The artist image response is too large.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The artist image response is too large.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async Task<byte[]> ReadBoundedWithDeadlineAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AttemptTimeout);
        try
        {
            return await ReadBoundedAsync(content, maximumBytes, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The artist image response took too long.");
        }
    }

    private static string ReadMetadataText(
        IReadOnlyDictionary<string, CommonsMetadataValue> metadata,
        string primaryKey,
        string? fallbackKey,
        int maximumLength)
    {
        var raw = metadata.GetValueOrDefault(primaryKey)?.Value;
        if (string.IsNullOrWhiteSpace(raw) && fallbackKey is not null)
        {
            raw = metadata.GetValueOrDefault(fallbackKey)?.Value;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var withoutTags = HtmlTagPattern.Replace(raw, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var compact = string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maximumLength
            ? compact
            : compact[..maximumLength].TrimEnd() + "…";
    }

    private static string ReadHttpsUrl(
        IReadOnlyDictionary<string, CommonsMetadataValue> metadata,
        string key)
    {
        var value = metadata.GetValueOrDefault(key)?.Value?.Trim();
        return value is not null
               && value.Length <= 512
               && Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : string.Empty;
    }

    private static string NormalizeSupportedImageMime(string? mime)
    {
        return mime?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/png" or "image/x-png" => "image/png",
            "image/gif" => "image/gif",
            "image/bmp" or "image/x-ms-bmp" => "image/bmp",
            "image/tiff" => "image/tiff",
            _ => string.Empty
        };
    }

    private static bool IsJsonContentType(string? mime)
    {
        return string.Equals(mime, "application/json", StringComparison.OrdinalIgnoreCase)
               || mime?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsOptionalFailure(Exception exception)
    {
        return exception is HttpRequestException
            or IOException
            or InvalidDataException
            or JsonException
            or NotSupportedException
            or RegexMatchTimeoutException
            or TaskCanceledException
            or TimeoutException;
    }

    private static bool IsTransientTransportFailure(Exception exception)
    {
        return exception is HttpRequestException
            or IOException
            or TaskCanceledException
            or TimeoutException;
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        delay ??= TimeSpan.FromMilliseconds(200 * attempt);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay <= MaxRetryDelay ? delay : null;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private enum RequestKind
    {
        Metadata,
        Artwork
    }

    private sealed class CommonsResponse
    {
        [JsonPropertyName("query")]
        public CommonsQuery? Query { get; init; }
    }

    private sealed class CommonsQuery
    {
        [JsonPropertyName("pages")]
        public List<CommonsPage>? Pages { get; init; } = [];
    }

    private sealed class CommonsPage
    {
        [JsonPropertyName("imageinfo")]
        public List<CommonsImageInfo>? ImageInfo { get; init; } = [];
    }

    private sealed class CommonsImageInfo
    {
        [JsonPropertyName("thumburl")]
        public string? ThumbUrl { get; init; }

        [JsonPropertyName("thumbwidth")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? ThumbWidth { get; init; }

        [JsonPropertyName("thumbheight")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? ThumbHeight { get; init; }

        [JsonPropertyName("mime")]
        public string? Mime { get; init; }

        [JsonPropertyName("thumbmime")]
        public string? ThumbMime { get; init; }

        [JsonPropertyName("extmetadata")]
        public Dictionary<string, CommonsMetadataValue>? ExtMetadata { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CommonsMetadataValue
    {
        [JsonPropertyName("value")]
        public string? Value { get; init; }
    }
}
