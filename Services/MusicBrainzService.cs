using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

public sealed class MusicBrainzService : IDisposable
{
    private const int MaxArtworkRedirects = 8;
    private const int MaxArtworkBytes = 20 * 1024 * 1024;
    private static readonly SemaphoreSlim MusicBrainzRateGate = new(1, 1);
    private static DateTimeOffset _nextMusicBrainzRequestAt = DateTimeOffset.MinValue;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly bool _enforceTrustedArtworkHosts;

    public MusicBrainzService()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal MusicBrainzService(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        // The injected client is used by the isolated service tests. The app-created
        // client only follows artwork redirects to Cover Art Archive/Internet Archive.
        _enforceTrustedArtworkHosts = ownsHttpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // Cover Art Archive can return an HTTP redirect target even when the
            // original request used HTTPS. .NET correctly refuses that downgrade,
            // so redirects are handled below and upgraded after host validation.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                                     | DecompressionMethods.Deflate
                                     | DecompressionMethods.Brotli
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<MusicBrainzTrackData?> FetchTrackDataAsync(
        string title,
        string artist,
        string album,
        string isrc,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var query = BuildRecordingQuery(title, artist, album, isrc);
        if (query.Length == 0)
        {
            return null;
        }

        var searchUri = new Uri(
            $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=8");
        using var response = await SendMusicBrainzAsync(searchUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        RecordingSearchResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<RecordingSearchResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("MusicBrainz returned an invalid response.", ex);
        }

        var recording = payload?.Recordings
            .OrderByDescending(item => ScoreRecording(item, artist, album, duration))
            .FirstOrDefault();
        if (recording is null || !IsConfidentRecording(recording, title, artist, album, isrc, duration))
        {
            return null;
        }

        var release = recording.Releases
            .OrderByDescending(item => ScoreRelease(item, album))
            .FirstOrDefault();
        var artistName = JoinArtistCredit(recording.ArtistCredit);
        var genre = recording.Tags
            .OrderByDescending(tag => tag.Count)
            .Select(tag => tag.Name?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        var matchingMedium = release?.Media
            .FirstOrDefault(medium => medium.Tracks.Any(track => !string.IsNullOrWhiteSpace(track.Number)))
            ?? release?.Media.FirstOrDefault();
        var trackNumber = matchingMedium?.Tracks
            .Select(track => track.Number?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        var trackTotal = matchingMedium?.TrackCount is > 0
            ? matchingMedium.TrackCount.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var year = ExtractYear(release?.Date);
        if (year.Length == 0)
        {
            year = ExtractYear(recording.FirstReleaseDate);
        }

        byte[]? coverArtwork = null;
        byte[]? discArtwork = null;
        if (release is not null)
        {
            var art = await FetchReleaseArtworkAsync(release, cancellationToken);
            coverArtwork = art.Cover;
            discArtwork = art.Disc;
        }

        return new MusicBrainzTrackData(
            recording.Title?.Trim() ?? string.Empty,
            artistName,
            genre,
            year,
            release?.Title?.Trim() ?? string.Empty,
            trackNumber,
            trackTotal,
            coverArtwork,
            discArtwork);
    }

    private async Task<(byte[]? Cover, byte[]? Disc)> FetchReleaseArtworkAsync(
        ReleaseResult release,
        CancellationToken cancellationToken)
    {
        CoverArtResponse? payload = null;
        try
        {
            using var response = await SendArtworkRequestAsync(
                new Uri($"https://coverartarchive.org/release/{release.Id}/"),
                "application/json",
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                payload = await response.Content.ReadFromJsonAsync<CoverArtResponse>(cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsOptionalArtworkFailure(ex))
        {
        }

        var front = payload?.Images.FirstOrDefault(image => image.Approved && image.Front)
            ?? payload?.Images.FirstOrDefault(image => image.Approved && image.Types.Contains("Front", StringComparer.OrdinalIgnoreCase));
        var medium = payload?.Images.FirstOrDefault(image => image.Approved && image.Types.Contains("Medium", StringComparer.OrdinalIgnoreCase));

        var coverTask = FetchCoverArtworkAsync(release, front, cancellationToken);
        var discTask = medium is null
            ? Task.FromResult<byte[]?>(null)
            : TryOptionalArtworkRequestAsync(
                () => DownloadArtworkAsync(medium, cancellationToken),
                cancellationToken);
        await Task.WhenAll(coverTask, discTask);
        return (await coverTask, await discTask);
    }

    private static async Task<byte[]?> TryOptionalArtworkRequestAsync(
        Func<Task<byte[]?>> request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await request();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsOptionalArtworkFailure(ex))
        {
            return null;
        }
    }

    private static bool IsOptionalArtworkFailure(Exception exception)
    {
        return exception is HttpRequestException
            or IOException
            or InvalidDataException
            or JsonException
            or NotSupportedException
            or TaskCanceledException
            or TimeoutException;
    }

    private async Task<byte[]?> FetchCoverArtworkAsync(
        ReleaseResult release,
        CoverArtImage? front,
        CancellationToken cancellationToken)
    {
        if (front is not null)
        {
            var indexedArtwork = await TryOptionalArtworkRequestAsync(
                () => DownloadArtworkAsync(front, cancellationToken),
                cancellationToken);
            if (indexedArtwork is not null)
            {
                return indexedArtwork;
            }
        }

        var releaseArtwork = await TryOptionalArtworkRequestAsync(
            () => TryDownloadFrontAsync("release", release.Id, cancellationToken),
            cancellationToken);
        if (releaseArtwork is not null)
        {
            return releaseArtwork;
        }

        return await TryOptionalArtworkRequestAsync(
            () => TryDownloadReleaseGroupFrontAsync(release.ReleaseGroup?.Id, cancellationToken),
            cancellationToken);
    }

    private async Task<byte[]?> TryDownloadReleaseGroupFrontAsync(
        string? releaseGroupId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupId))
        {
            return null;
        }

        return await TryDownloadFrontAsync("release-group", releaseGroupId, cancellationToken);
    }

    private async Task<byte[]?> TryDownloadFrontAsync(
        string entity,
        string id,
        CancellationToken cancellationToken)
    {
        using var response = await SendArtworkRequestAsync(
            new Uri($"https://coverartarchive.org/{entity}/{id}/front-500"),
            "image/*",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadArtworkBytesAsync(response, cancellationToken);
    }

    private async Task<byte[]?> DownloadArtworkAsync(CoverArtImage image, CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            image.Thumbnails.GetValueOrDefault("1200"),
            image.Thumbnails.GetValueOrDefault("500"),
            image.Image
        };
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || !attempted.Add(candidate)
                || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                continue;
            }

            try
            {
                using var response = await SendArtworkRequestAsync(uri, "image/*", cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await ReadArtworkBytesAsync(response, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsOptionalArtworkFailure(ex))
            {
                // Try the next thumbnail/original URL before giving up on this image.
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendArtworkRequestAsync(
        Uri uri,
        string accept,
        CancellationToken cancellationToken)
    {
        var current = NormalizeArtworkUri(uri);
        for (var redirectCount = 0; redirectCount <= MaxArtworkRedirects; redirectCount++)
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
                throw new HttpRequestException("The artwork service returned an invalid redirect.");
            }

            var redirected = location.IsAbsoluteUri ? location : new Uri(current, location);
            current = NormalizeArtworkUri(redirected);
        }

        throw new HttpRequestException("The artwork service returned too many redirects.");
    }

    private Uri NormalizeArtworkUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new HttpRequestException("The artwork service returned an invalid address.");
        }

        if (_enforceTrustedArtworkHosts && !IsTrustedArtworkHost(uri.IdnHost))
        {
            throw new HttpRequestException("The artwork service returned an untrusted address.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var builder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1
            };
            uri = builder.Uri;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("The artwork service returned an unsupported address.");
        }

        return uri;
    }

    private static bool IsTrustedArtworkHost(string host)
    {
        return string.Equals(host, "coverartarchive.org", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".coverartarchive.org", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "archive.org", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static async Task<byte[]> ReadArtworkBytesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxArtworkBytes)
        {
            throw new InvalidDataException("The artwork response is too large.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType)
            && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The artwork service returned an invalid image.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaxArtworkBytes)
            {
                throw new InvalidDataException("The artwork response is too large.");
            }

            output.Write(buffer, 0, read);
        }

        if (output.Length == 0)
        {
            throw new InvalidDataException("The artwork response is empty.");
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendMusicBrainzAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await MusicBrainzRateGate.WaitAsync(cancellationToken);
            try
            {
                var wait = _nextMusicBrainzRequestAt - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }

                var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                _nextMusicBrainzRequestAt = DateTimeOffset.UtcNow.AddSeconds(1.05);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable || attempt == 1)
                {
                    return response;
                }

                response.Dispose();
            }
            finally
            {
                MusicBrainzRateGate.Release();
            }
        }

        throw new HttpRequestException("MusicBrainz is temporarily unavailable.");
    }

    private static string BuildRecordingQuery(string title, string artist, string album, string isrc)
    {
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            return $"isrc:{QuoteQueryValue(isrc)}";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var parts = new List<string> { $"recording:{QuoteQueryValue(title)}" };
        if (!string.IsNullOrWhiteSpace(artist))
        {
            parts.Add($"artist:{QuoteQueryValue(artist)}");
        }

        if (!string.IsNullOrWhiteSpace(album))
        {
            parts.Add($"release:{QuoteQueryValue(album)}");
        }

        return string.Join(" AND ", parts);
    }

    private static string QuoteQueryValue(string value)
    {
        return '"' + value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }

    private static double ScoreRecording(
        RecordingResult recording,
        string requestedArtist,
        string requestedAlbum,
        TimeSpan requestedDuration)
    {
        double score = recording.Score;
        if (!string.IsNullOrWhiteSpace(requestedArtist)
            && TextEquals(JoinArtistCredit(recording.ArtistCredit), requestedArtist))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(requestedAlbum)
            && recording.Releases.Any(release => TextEquals(release.Title, requestedAlbum)))
        {
            score += 18;
        }

        if (requestedDuration > TimeSpan.Zero && recording.Length is > 0)
        {
            var difference = Math.Abs(recording.Length.Value - requestedDuration.TotalMilliseconds) / 1000;
            score -= Math.Min(25, difference / 2);
        }

        return score;
    }

    private static bool IsConfidentRecording(
        RecordingResult recording,
        string requestedTitle,
        string requestedArtist,
        string requestedAlbum,
        string isrc,
        TimeSpan requestedDuration)
    {
        if (recording.Score < 70)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(isrc) || TextEquals(recording.Title, requestedTitle))
        {
            return true;
        }

        var artistMatches = !string.IsNullOrWhiteSpace(requestedArtist)
            && TextEquals(JoinArtistCredit(recording.ArtistCredit), requestedArtist);
        var albumMatches = !string.IsNullOrWhiteSpace(requestedAlbum)
            && recording.Releases.Any(release => TextEquals(release.Title, requestedAlbum));
        var durationMatches = requestedDuration > TimeSpan.Zero
            && recording.Length is > 0
            && Math.Abs(recording.Length.Value - requestedDuration.TotalMilliseconds) <= 5000;
        return (artistMatches && albumMatches)
            || (artistMatches && durationMatches)
            || (albumMatches && durationMatches);
    }

    private static double ScoreRelease(ReleaseResult release, string requestedAlbum)
    {
        double score = 0;
        if (!string.IsNullOrWhiteSpace(requestedAlbum) && TextEquals(release.Title, requestedAlbum))
        {
            score += 100;
        }

        if (string.Equals(release.Status, "Official", StringComparison.OrdinalIgnoreCase))
        {
            score += 14;
        }

        if (release.Media.Any(medium => string.Equals(medium.Format, "CD", StringComparison.OrdinalIgnoreCase)))
        {
            score += 5;
        }

        if (string.Equals(release.ReleaseGroup?.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        return score;
    }

    private static string JoinArtistCredit(IEnumerable<ArtistCredit> credits)
    {
        return string.Concat(credits.Select(credit =>
            (credit.Name ?? credit.Artist?.Name ?? string.Empty) + (credit.JoinPhrase ?? string.Empty))).Trim();
    }

    private static bool TextEquals(string? left, string? right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractYear(string? date)
    {
        var value = date?.Trim();
        if (value is null
            || value.Length < 4
            || (value.Length > 4 && value[4] != '-')
            || !int.TryParse(
                value.AsSpan(0, 4),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var year)
            || year is < 1 or > 9999)
        {
            return string.Empty;
        }

        return year.ToString("0000", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class RecordingSearchResponse
    {
        [JsonPropertyName("recordings")]
        public List<RecordingResult> Recordings { get; init; } = [];
    }

    private sealed class RecordingResult
    {
        [JsonPropertyName("score")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Score { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("length")]
        public double? Length { get; init; }

        [JsonPropertyName("first-release-date")]
        public string? FirstReleaseDate { get; init; }

        [JsonPropertyName("artist-credit")]
        public List<ArtistCredit> ArtistCredit { get; init; } = [];

        [JsonPropertyName("releases")]
        public List<ReleaseResult> Releases { get; init; } = [];

        [JsonPropertyName("tags")]
        public List<TagResult> Tags { get; init; } = [];
    }

    private sealed class ArtistCredit
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("joinphrase")]
        public string? JoinPhrase { get; init; }

        [JsonPropertyName("artist")]
        public ArtistResult? Artist { get; init; }
    }

    private sealed class ArtistResult
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class TagResult
    {
        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class ReleaseResult
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("date")]
        public string? Date { get; init; }

        [JsonPropertyName("release-group")]
        public ReleaseGroupResult? ReleaseGroup { get; init; }

        [JsonPropertyName("media")]
        public List<MediumResult> Media { get; init; } = [];
    }

    private sealed class ReleaseGroupResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("primary-type")]
        public string? PrimaryType { get; init; }
    }

    private sealed class MediumResult
    {
        [JsonPropertyName("format")]
        public string? Format { get; init; }

        [JsonPropertyName("track-count")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? TrackCount { get; init; }

        [JsonPropertyName("track")]
        public List<TrackResult> Tracks { get; init; } = [];
    }

    private sealed class TrackResult
    {
        [JsonPropertyName("number")]
        public string? Number { get; init; }
    }

    private sealed class CoverArtResponse
    {
        [JsonPropertyName("images")]
        public List<CoverArtImage> Images { get; init; } = [];
    }

    private sealed class CoverArtImage
    {
        [JsonPropertyName("image")]
        public string? Image { get; init; }

        [JsonPropertyName("thumbnails")]
        public Dictionary<string, string> Thumbnails { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("types")]
        public List<string> Types { get; init; } = [];

        [JsonPropertyName("front")]
        public bool Front { get; init; }

        [JsonPropertyName("approved")]
        public bool Approved { get; init; }
    }
}
