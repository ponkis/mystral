using System.IO;
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
    private static readonly SemaphoreSlim MusicBrainzRateGate = new(1, 1);
    private static DateTimeOffset _nextMusicBrainzRequestAt = DateTimeOffset.MinValue;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public MusicBrainzService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ownsHttpClient: true)
    {
    }

    internal MusicBrainzService(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        }
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
        var trackNumber = release?.Media
            .SelectMany(medium => medium.Tracks)
            .Select(track => track.Number?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;

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
            release?.Date?.Trim() ?? recording.FirstReleaseDate?.Trim() ?? string.Empty,
            release?.Title?.Trim() ?? string.Empty,
            trackNumber,
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
            using var response = await _httpClient.GetAsync(
                $"https://coverartarchive.org/release/{release.Id}/",
                HttpCompletionOption.ResponseHeadersRead,
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

        var coverTask = TryOptionalArtworkRequestAsync(
            () => front is null
                ? TryDownloadReleaseGroupFrontAsync(release.ReleaseGroup?.Id, cancellationToken)
                : DownloadArtworkAsync(front, cancellationToken),
            cancellationToken);
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
            or JsonException
            or NotSupportedException
            or TaskCanceledException
            or TimeoutException;
    }

    private async Task<byte[]?> TryDownloadReleaseGroupFrontAsync(
        string? releaseGroupId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupId))
        {
            return null;
        }

        using var response = await _httpClient.GetAsync(
            $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<byte[]?> DownloadArtworkAsync(CoverArtImage image, CancellationToken cancellationToken)
    {
        var url = image.Thumbnails.GetValueOrDefault("1200")
            ?? image.Thumbnails.GetValueOrDefault("500")
            ?? image.Image;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url[7..];
        }

        return await _httpClient.GetByteArrayAsync(url, cancellationToken);
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
