using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

public sealed class MusicBrainzService : IDisposable
{
    private const int MaxMusicBrainzAttempts = 3;
    private const int MaxMusicBrainzPayloadBytes = 8 * 1024 * 1024;
    private const int MaxArtworkRedirects = 8;
    private const int MaxArtworkAttempts = 3;
    private const int MaxArtworkBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan MusicBrainzRequestSpacing = TimeSpan.FromSeconds(1.05);
    private static readonly TimeSpan MusicBrainzAttemptTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxMusicBrainzRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxArtworkRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim MusicBrainzRateGate = new(1, 1);
    private static DateTimeOffset _nextMusicBrainzRequestAt = DateTimeOffset.MinValue;
    private static readonly object MusicBrainzBackoffSync = new();
    private static DateTimeOffset _musicBrainzRetryNotBefore = DateTimeOffset.MinValue;
    private static HttpStatusCode _musicBrainzBackoffStatus = HttpStatusCode.ServiceUnavailable;
    private static readonly object ArtworkBackoffSync = new();
    private static DateTimeOffset _artworkRetryNotBefore = DateTimeOffset.MinValue;
    private const string ArtworkOriginHost = "coverartarchive.org";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly bool _enforceTrustedArtworkHosts;
    private readonly IArtworkDiagnostics _diagnostics;

    public MusicBrainzService()
        : this(CreateHttpClient(), ownsHttpClient: true, diagnostics: new FileArtworkDiagnostics())
    {
    }

    internal MusicBrainzService(
        HttpClient httpClient,
        bool ownsHttpClient = false,
        IArtworkDiagnostics? diagnostics = null,
        bool enforceTrustedArtworkHosts = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        // App-created clients only follow artwork redirects to Cover Art Archive or
        // Internet Archive; isolated boundary tests can opt into the same policy.
        _enforceTrustedArtworkHosts = ownsHttpClient || enforceTrustedArtworkHosts;
        _diagnostics = diagnostics ?? NullArtworkDiagnostics.Instance;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        }
    }

    private readonly record struct ArtworkFetch(byte[]? Data, ArtworkFetchOutcome Outcome)
    {
        public static ArtworkFetch Retrieved(byte[] data) => new(data, ArtworkFetchOutcome.Retrieved);
        public static readonly ArtworkFetch NotAvailable = new(null, ArtworkFetchOutcome.NotAvailable);
        public static readonly ArtworkFetch Failed = new(null, ArtworkFetchOutcome.Failed);
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
        var match = await FindRecordingAsync(title, artist, album, isrc, duration, cancellationToken);
        if (match is null)
        {
            return null;
        }

        var recording = match.Recording;
        var release = match.Release;
        var artistName = JoinArtistCredit(recording.ArtistCredit);
        var genre = MapGenres(recording.Genres, recording.Tags).FirstOrDefault() ?? string.Empty;
        var trackLocation = FindRecordingTrack(release, recording.Id);
        var trackNumber = trackLocation.Track?.Number?.Trim() ?? string.Empty;
        var trackTotal = trackLocation.Medium?.TrackCount is > 0
            ? trackLocation.Medium.TrackCount.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var year = ExtractYear(release?.Date);
        if (year.Length == 0)
        {
            year = ExtractYear(recording.FirstReleaseDate);
        }

        byte[]? coverArtwork = null;
        byte[]? discArtwork = null;
        var coverOutcome = ArtworkFetchOutcome.NotAvailable;
        var discOutcome = ArtworkFetchOutcome.NotAvailable;
        if (release is not null)
        {
            var art = await FetchReleaseArtworkAsync(release, cancellationToken);
            coverArtwork = art.Cover.Data;
            discArtwork = art.Disc.Data;
            coverOutcome = art.Cover.Outcome;
            discOutcome = art.Disc.Outcome;
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
            discArtwork,
            coverOutcome,
            discOutcome);
    }

    public async Task<MusicBrainzTrackInfo?> FetchTrackInfoAsync(
        string title,
        string artist,
        string album,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var match = await FindRecordingAsync(title, artist, album, string.Empty, duration, cancellationToken);
        if (match is null)
        {
            return null;
        }

        var recording = match.Recording;
        var release = match.Release;
        var trackLocation = FindRecordingTrack(release, recording.Id);
        return new MusicBrainzTrackInfo(
            Clean(recording.Id),
            Clean(recording.Title),
            JoinArtistCredit(recording.ArtistCredit),
            MapArtistCredits(recording.ArtistCredit),
            ToDuration(recording.Length),
            Clean(recording.FirstReleaseDate),
            NormalizeStrings(recording.Isrcs.Select(ReadIsrc)),
            MapGenres(recording.Genres, recording.Tags),
            Clean(recording.Disambiguation),
            Clean(release?.Id),
            Clean(release?.ReleaseGroup?.Id),
            Clean(release?.Title),
            Clean(trackLocation.Track?.Number),
            trackLocation.Medium?.TrackCount is > 0
                ? trackLocation.Medium.TrackCount.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
    }

    public async Task<MusicBrainzArtistInfo> FetchArtistInfoAsync(
        string artistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            throw new ArgumentException("A MusicBrainz artist ID is required.", nameof(artistId));
        }

        var uri = new Uri(
            $"https://musicbrainz.org/ws/2/artist/{Uri.EscapeDataString(artistId.Trim())}?inc=aliases+annotation+genres+tags+url-rels&fmt=json");
        using var response = await SendMusicBrainzAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await ReadMusicBrainzPayloadAsync<ArtistResult>(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload.Id))
        {
            throw new InvalidDataException("MusicBrainz returned an invalid artist response.");
        }

        return new MusicBrainzArtistInfo(
            Clean(payload.Id),
            Clean(payload.Name),
            Clean(payload.SortName),
            Clean(payload.Type),
            Clean(payload.Gender),
            Clean(payload.Country),
            Clean(payload.Area?.Name),
            Clean(payload.BeginArea?.Name),
            Clean(payload.EndArea?.Name),
            Clean(payload.LifeSpan?.Begin),
            Clean(payload.LifeSpan?.End),
            payload.LifeSpan?.Ended ?? false,
            Clean(payload.Disambiguation),
            Clean(payload.Annotation),
            FindArtistImagePageUrl(payload.Relations),
            NormalizeStrings(payload.Aliases.Select(alias => alias.Name)),
            MapGenres(payload.Genres, payload.Tags));
    }

    public async Task<MusicBrainzAlbumInfo> FetchAlbumInfoAsync(
        string releaseId,
        string recordingId,
        CancellationToken cancellationToken = default,
        bool includeArtwork = true)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            throw new ArgumentException("A MusicBrainz release ID is required.", nameof(releaseId));
        }

        var uri = new Uri(
            $"https://musicbrainz.org/ws/2/release/{Uri.EscapeDataString(releaseId.Trim())}?inc=artist-credits+labels+recordings+release-groups+genres+tags&fmt=json");
        using var response = await SendMusicBrainzAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var release = await ReadMusicBrainzPayloadAsync<ReleaseResult>(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(release.Id))
        {
            throw new InvalidDataException("MusicBrainz returned an invalid release response.");
        }

        var artwork = includeArtwork
            ? await FetchReleaseArtworkAsync(release, cancellationToken, includeDisc: false)
            : (Cover: ArtworkFetch.NotAvailable, Disc: ArtworkFetch.NotAvailable);
        var tracks = MapAlbumTracks(release, recordingId);
        var labels = release.LabelInfo
            .Select(item => new MusicBrainzLabelInfo(
                Clean(item.Label?.Name),
                Clean(item.CatalogNumber)))
            .Where(item => item.Name.Length > 0 || item.CatalogNumber.Length > 0)
            .ToArray();
        var formats = NormalizeStrings(release.Media.Select(medium => medium.Format));
        var trackTotal = release.Media.Sum(medium => medium.TrackCount is > 0
            ? medium.TrackCount.Value
            : GetMediumTracks(medium).Count);

        return new MusicBrainzAlbumInfo(
            Clean(release.Id),
            Clean(release.ReleaseGroup?.Id),
            Clean(release.Title),
            JoinArtistCredit(release.ArtistCredit),
            Clean(release.ReleaseGroup?.FirstReleaseDate),
            Clean(release.Date),
            Clean(release.ReleaseGroup?.PrimaryType),
            NormalizeStrings(release.ReleaseGroup?.SecondaryTypes ?? []),
            Clean(release.Status),
            Clean(release.Country),
            Clean(release.Barcode),
            Clean(release.Packaging),
            Clean(release.Disambiguation),
            labels,
            formats,
            trackTotal,
            MapGenres(
                release.ReleaseGroup?.Genres ?? [],
                release.ReleaseGroup?.Tags ?? [],
                release.Genres,
                release.Tags),
            tracks,
            artwork.Cover.Data,
            artwork.Cover.Outcome);
    }

    private async Task<MatchedRecording?> FindRecordingAsync(
        string title,
        string artist,
        string album,
        string isrc,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var queries = BuildRecordingQueries(title, artist, album, isrc);
        if (queries.Count == 0)
        {
            return null;
        }

        foreach (var query in queries)
        {
            var searchUri = new Uri(
                $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=15");
            using var response = await SendMusicBrainzAsync(searchUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await ReadMusicBrainzPayloadAsync<RecordingSearchResponse>(response, cancellationToken);
            var recording = payload.Recordings
                .OrderByDescending(item => ScoreRecording(item, title, artist, album, duration))
                .FirstOrDefault(item => IsConfidentRecording(item, title, artist, album, isrc, duration));
            if (recording is not null)
            {
                var release = recording.Releases
                    .OrderByDescending(item => ScoreRelease(item, album))
                    .FirstOrDefault();
                return new MatchedRecording(recording, release);
            }
        }

        return null;
    }

    private static async Task<T> ReadMusicBrainzPayloadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("MusicBrainz returned an invalid response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("MusicBrainz returned an invalid response.", ex);
        }
    }

    private async Task<(ArtworkFetch Cover, ArtworkFetch Disc)> FetchReleaseArtworkAsync(
        ReleaseResult release,
        CancellationToken cancellationToken,
        bool includeDisc = true)
    {
        CoverArtResponse? payload = null;
        var metadataFailed = false;
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
            // The release index could not be read, so we cannot tell whether art exists;
            // treat downstream absence as a failure rather than "no artwork".
            metadataFailed = true;
            _diagnostics.RecordArtworkFailure(
                "metadata", release.Id, ArtworkOriginHost, ExtractStatusCode(ex), ex.GetType().Name);
        }

        var front = payload?.Images.FirstOrDefault(image => image.Approved && image.Front)
            ?? payload?.Images.FirstOrDefault(image => image.Approved && image.Types.Contains("Front", StringComparer.OrdinalIgnoreCase));
        var medium = payload?.Images.FirstOrDefault(image => image.Approved && image.Types.Contains("Medium", StringComparer.OrdinalIgnoreCase));

        var coverTask = FetchCoverArtworkAsync(release, front, metadataFailed, cancellationToken);
        var discTask = !includeDisc || medium is null
            ? Task.FromResult(metadataFailed ? ArtworkFetch.Failed : ArtworkFetch.NotAvailable)
            : DownloadArtworkAsync(medium, "disc", release.Id, cancellationToken);
        await Task.WhenAll(coverTask, discTask);
        return (await coverTask, await discTask);
    }

    private static HttpStatusCode? ExtractStatusCode(Exception exception)
    {
        return (exception as HttpRequestException)?.StatusCode;
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

    private async Task<ArtworkFetch> FetchCoverArtworkAsync(
        ReleaseResult release,
        CoverArtImage? front,
        bool metadataFailed,
        CancellationToken cancellationToken)
    {
        // Once we could not determine what exists (or a download errored), a later
        // absence is a failure to fetch, not proof that the artwork is missing.
        var failed = metadataFailed;

        if (front is not null)
        {
            var indexed = await DownloadArtworkAsync(front, "cover-indexed", release.Id, cancellationToken);
            if (indexed.Outcome == ArtworkFetchOutcome.Retrieved)
            {
                return indexed;
            }

            failed |= indexed.Outcome == ArtworkFetchOutcome.Failed;
        }

        var releaseFront = await TryDownloadFrontAsync(
            "release", release.Id, "cover-release-front", release.Id, cancellationToken);
        if (releaseFront.Outcome == ArtworkFetchOutcome.Retrieved)
        {
            return releaseFront;
        }

        failed |= releaseFront.Outcome == ArtworkFetchOutcome.Failed;

        var groupFront = await TryDownloadReleaseGroupFrontAsync(
            release.ReleaseGroup?.Id, release.Id, cancellationToken);
        if (groupFront.Outcome == ArtworkFetchOutcome.Retrieved)
        {
            return groupFront;
        }

        failed |= groupFront.Outcome == ArtworkFetchOutcome.Failed;

        return failed ? ArtworkFetch.Failed : ArtworkFetch.NotAvailable;
    }

    private async Task<ArtworkFetch> TryDownloadReleaseGroupFrontAsync(
        string? releaseGroupId,
        string? releaseId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupId))
        {
            return ArtworkFetch.NotAvailable;
        }

        return await TryDownloadFrontAsync(
            "release-group", releaseGroupId, "cover-release-group-front", releaseId, cancellationToken);
    }

    private async Task<ArtworkFetch> TryDownloadFrontAsync(
        string entity,
        string id,
        string stage,
        string? releaseId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://coverartarchive.org/{entity}/{id}/front-500");
        try
        {
            using var response = await SendArtworkRequestAsync(uri, "image/*", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ArtworkFetch.NotAvailable;
            }

            response.EnsureSuccessStatusCode();
            return ArtworkFetch.Retrieved(await ReadArtworkBytesAsync(response, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsOptionalArtworkFailure(ex))
        {
            _diagnostics.RecordArtworkFailure(stage, releaseId, uri.Host, ExtractStatusCode(ex), ex.GetType().Name);
            return ArtworkFetch.Failed;
        }
    }

    private async Task<ArtworkFetch> DownloadArtworkAsync(
        CoverArtImage image,
        string stage,
        string? releaseId,
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            image.Thumbnails.GetValueOrDefault("1200"),
            image.Thumbnails.GetValueOrDefault("500"),
            image.Image
        };
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = false;
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
                return ArtworkFetch.Retrieved(await ReadArtworkBytesAsync(response, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsOptionalArtworkFailure(ex))
            {
                // Try the next thumbnail/original URL before giving up on this image.
                failed = true;
                _diagnostics.RecordArtworkFailure(stage, releaseId, uri.Host, ExtractStatusCode(ex), ex.GetType().Name);
            }
        }

        return failed ? ArtworkFetch.Failed : ArtworkFetch.NotAvailable;
    }

    private async Task<HttpResponseMessage> SendArtworkRequestAsync(
        Uri uri,
        string accept,
        CancellationToken cancellationToken)
    {
        var target = NormalizeArtworkUri(uri);
        ThrowIfArtworkBackoffActive();
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendArtworkRequestOnceAsync(target, accept, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                attempt < MaxArtworkAttempts
                && IsTransientArtworkTransportFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                continue;
            }

            if (attempt >= MaxArtworkAttempts || !IsTransientArtworkStatus(response.StatusCode))
            {
                return response;
            }

            // Cover Art Archive / Internet Archive can answer with a transient 429/503;
            // retry a couple of times. A long Retry-After is returned to the caller
            // instead of contacting the service again before its requested time.
            var delay = GetArtworkRetryDelay(response, attempt);
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

    private async Task<HttpResponseMessage> SendArtworkRequestOnceAsync(
        Uri target,
        string accept,
        CancellationToken cancellationToken)
    {
        var current = target;
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
                throw new ArtworkProtocolException("The artwork service returned an invalid redirect.");
            }

            var redirected = location.IsAbsoluteUri ? location : new Uri(current, location);
            current = NormalizeArtworkUri(redirected);
        }

        throw new ArtworkProtocolException("The artwork service returned too many redirects.");
    }

    private static bool IsTransientArtworkTransportFailure(Exception exception)
    {
        return exception is HttpRequestException and not ArtworkProtocolException
            or IOException
            or TaskCanceledException
            or TimeoutException;
    }

    private static bool IsTransientArtworkStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan? GetArtworkRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? hint = null;
        if (retryAfter?.Delta is { } delta)
        {
            hint = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            hint = date - DateTimeOffset.UtcNow;
        }

        var delay = hint ?? TimeSpan.FromMilliseconds(250 * attempt);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        if (delay > MaxArtworkRetryDelay)
        {
            lock (ArtworkBackoffSync)
            {
                var now = DateTimeOffset.UtcNow;
                var retryAt = delay >= DateTimeOffset.MaxValue - now
                    ? DateTimeOffset.MaxValue
                    : now + delay;
                if (retryAt > _artworkRetryNotBefore)
                {
                    _artworkRetryNotBefore = retryAt;
                }
            }

            return null;
        }

        return delay;
    }

    private static void ThrowIfArtworkBackoffActive()
    {
        lock (ArtworkBackoffSync)
        {
            if (_artworkRetryNotBefore > DateTimeOffset.UtcNow)
            {
                throw new ArtworkProtocolException("The artwork service asked Mystral to wait before trying again.");
            }

            _artworkRetryNotBefore = DateTimeOffset.MinValue;
        }
    }

    private Uri NormalizeArtworkUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArtworkProtocolException("The artwork service returned an invalid address.");
        }

        if (_enforceTrustedArtworkHosts && !IsTrustedArtworkHost(uri.IdnHost))
        {
            throw new ArtworkProtocolException("The artwork service returned an untrusted address.");
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
            throw new ArtworkProtocolException("The artwork service returned an unsupported address.");
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
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            TimeSpan? retryDelay;
            try
            {
                var result = await SendMusicBrainzOnceAsync(uri, attempt, cancellationToken);
                response = result.Response;
                retryDelay = result.RetryDelay;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                attempt < MaxMusicBrainzAttempts
                && IsTransientMusicBrainzTransportFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                continue;
            }

            if (!IsTransientMusicBrainzStatus(response.StatusCode)
                || attempt >= MaxMusicBrainzAttempts
                || retryDelay is null)
            {
                return response;
            }

            response.Dispose();
            if (retryDelay.Value > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay.Value, cancellationToken);
            }
        }
    }

    private async Task<(HttpResponseMessage Response, TimeSpan? RetryDelay)> SendMusicBrainzOnceAsync(
        Uri uri,
        int attempt,
        CancellationToken cancellationToken)
    {
        await MusicBrainzRateGate.WaitAsync(cancellationToken);
        try
        {
            await WaitForMusicBrainzTurnAsync(cancellationToken);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(MusicBrainzAttemptTimeout);
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCancellation.Token);
                if (response.IsSuccessStatusCode)
                {
                    await BufferMusicBrainzContentAsync(response, attemptCancellation.Token);
                }
            }
            catch
            {
                response?.Dispose();
                throw;
            }
            finally
            {
                // A request may have reached the service even when the transport
                // failed, so every started attempt advances the shared rate gate.
                _nextMusicBrainzRequestAt = DateTimeOffset.UtcNow + MusicBrainzRequestSpacing;
            }

            // Publish a server-requested cooldown before releasing the gate so a
            // queued artist or album lookup cannot slip through it.
            var retryDelay = IsTransientMusicBrainzStatus(response.StatusCode)
                ? GetMusicBrainzRetryDelay(response, attempt)
                : null;
            return (response, retryDelay);
        }
        finally
        {
            MusicBrainzRateGate.Release();
        }
    }

    private static bool IsTransientMusicBrainzTransportFailure(Exception exception)
    {
        return exception is HttpRequestException { StatusCode: null }
            or IOException
            or OperationCanceledException
            or TimeoutException;
    }

    private static async Task BufferMusicBrainzContentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = response.Content;
        if (content.Headers.ContentLength is > MaxMusicBrainzPayloadBytes)
        {
            throw new InvalidDataException("MusicBrainz returned too much information.");
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

            if (output.Length + read > MaxMusicBrainzPayloadBytes)
            {
                throw new InvalidDataException("MusicBrainz returned too much information.");
            }

            output.Write(buffer, 0, read);
        }

        var bufferedContent = new ByteArrayContent(output.ToArray());
        foreach (var header in content.Headers)
        {
            bufferedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = bufferedContent;
        content.Dispose();
    }

    internal static bool IsTransientMusicBrainzStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan? GetMusicBrainzRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? hint = null;
        if (retryAfter?.Delta is { } delta)
        {
            hint = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            hint = date - DateTimeOffset.UtcNow;
        }

        var delay = hint ?? TimeSpan.FromMilliseconds(250 * attempt);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        lock (MusicBrainzBackoffSync)
        {
            var now = DateTimeOffset.UtcNow;
            var retryAt = delay >= DateTimeOffset.MaxValue - now
                ? DateTimeOffset.MaxValue
                : now + delay;
            if (retryAt > _musicBrainzRetryNotBefore)
            {
                _musicBrainzRetryNotBefore = retryAt;
                _musicBrainzBackoffStatus = response.StatusCode;
            }
        }

        return delay <= MaxMusicBrainzRetryDelay ? delay : null;
    }

    private static async Task WaitForMusicBrainzTurnAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset retryNotBefore;
            HttpStatusCode backoffStatus;
            lock (MusicBrainzBackoffSync)
            {
                if (_musicBrainzRetryNotBefore <= now)
                {
                    _musicBrainzRetryNotBefore = DateTimeOffset.MinValue;
                    _musicBrainzBackoffStatus = HttpStatusCode.ServiceUnavailable;
                }

                retryNotBefore = _musicBrainzRetryNotBefore;
                backoffStatus = _musicBrainzBackoffStatus;
            }

            var retryWait = retryNotBefore - now;
            if (retryWait > MaxMusicBrainzRetryDelay)
            {
                throw new HttpRequestException(
                    "MusicBrainz asked Mystral to wait before trying again.",
                    null,
                    backoffStatus);
            }

            var waitUntil = _nextMusicBrainzRequestAt > retryNotBefore
                ? _nextMusicBrainzRequestAt
                : retryNotBefore;
            var wait = waitUntil - now;
            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(wait, cancellationToken);
            // Retry-After can be extended while this request is queued. Re-read it
            // immediately before sending rather than relying on a stale snapshot.
        }
    }

    private static (MediumResult? Medium, TrackResult? Track) FindRecordingTrack(
        ReleaseResult? release,
        string? recordingId)
    {
        if (release is null)
        {
            return (null, null);
        }

        if (!string.IsNullOrWhiteSpace(recordingId))
        {
            foreach (var medium in release.Media)
            {
                var matchingTrack = GetMediumTracks(medium).FirstOrDefault(track =>
                    TextEquals(track.Recording?.Id, recordingId));
                if (matchingTrack is not null)
                {
                    return (medium, matchingTrack);
                }
            }
        }

        var matchingMedium = release.Media
            .FirstOrDefault(medium => GetMediumTracks(medium).Any(track => !string.IsNullOrWhiteSpace(track.Number)))
            ?? release.Media.FirstOrDefault();
        TrackResult? track = null;
        if (matchingMedium is not null)
        {
            var mediumTracks = GetMediumTracks(matchingMedium);
            track = mediumTracks.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Number))
                ?? mediumTracks.FirstOrDefault();
        }

        return (matchingMedium, track);
    }

    private static IReadOnlyList<TrackResult> GetMediumTracks(MediumResult medium)
    {
        return medium.LookupTracks.Count > 0 ? medium.LookupTracks : medium.Tracks;
    }

    private static IReadOnlyList<MusicBrainzArtistCredit> MapArtistCredits(
        IEnumerable<ArtistCredit> credits)
    {
        return credits
            .Select(credit => new MusicBrainzArtistCredit(
                Clean(credit.Artist?.Id),
                Clean(credit.Name ?? credit.Artist?.Name),
                credit.JoinPhrase ?? string.Empty))
            .Where(credit => credit.Name.Length > 0 || credit.ArtistId.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<MusicBrainzAlbumTrack> MapAlbumTracks(
        ReleaseResult release,
        string recordingId)
    {
        // Selection remains a presentation concern; each row carries its recording
        // identity so the active recording can be matched without title heuristics.
        _ = recordingId;
        var albumArtist = JoinArtistCredit(release.ArtistCredit);
        var tracks = new List<MusicBrainzAlbumTrack>();
        var fallbackMediumPosition = 0;
        foreach (var medium in release.Media)
        {
            fallbackMediumPosition++;
            var mediumPosition = medium.Position is > 0
                ? medium.Position.Value
                : fallbackMediumPosition;
            var fallbackTrackPosition = 0;
            foreach (var track in GetMediumTracks(medium))
            {
                fallbackTrackPosition++;
                var recording = track.Recording;
                var trackArtist = JoinArtistCredit(track.ArtistCredit);
                if (trackArtist.Length == 0)
                {
                    trackArtist = JoinArtistCredit(recording?.ArtistCredit ?? []);
                }

                if (trackArtist.Length == 0)
                {
                    trackArtist = albumArtist;
                }

                tracks.Add(new MusicBrainzAlbumTrack(
                    Clean(recording?.Id),
                    mediumPosition,
                    Clean(medium.Title),
                    Clean(medium.Format),
                    track.Position is > 0 ? track.Position.Value : fallbackTrackPosition,
                    Clean(track.Number),
                    Clean(track.Title ?? recording?.Title),
                    trackArtist,
                    ToDuration(track.Length ?? recording?.Length)));
            }
        }

        return tracks;
    }

    private static IReadOnlyList<string> MapGenres(params IEnumerable<TagResult>[] sources)
    {
        return sources
            .SelectMany(source => source)
            .Select(item => (Name: Clean(item.Name), item.Count))
            .Where(item => item.Name.Length > 0)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.First().Name, Count: group.Max(item => item.Count)))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Name)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeStrings(IEnumerable<string?> values)
    {
        return values
            .Select(Clean)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FindArtistImagePageUrl(IEnumerable<UrlRelationResult> relations)
    {
        foreach (var relation in relations)
        {
            if (!string.Equals(relation.Type, "image", StringComparison.OrdinalIgnoreCase)
                || relation.Ended
                || !string.IsNullOrWhiteSpace(relation.End))
            {
                continue;
            }

            var value = Clean(relation.Url?.Resource);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsDefaultPort
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.Equals(uri.IdnHost, "commons.wikimedia.org", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith("/wiki/File:", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Length <= "/wiki/File:".Length)
            {
                continue;
            }

            return uri.GetLeftPart(UriPartial.Path);
        }

        return string.Empty;
    }

    private static string? ReadIsrc(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
    }

    private static TimeSpan ToDuration(double? milliseconds)
    {
        return milliseconds is > 0 && milliseconds <= TimeSpan.MaxValue.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(milliseconds.Value)
            : TimeSpan.Zero;
    }

    private static string Clean(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static IReadOnlyList<string> BuildRecordingQueries(
        string title,
        string artist,
        string album,
        string isrc)
    {
        var queries = new List<string>();

        static void AddDistinct(List<string> target, string query)
        {
            if (query.Length > 0 && !target.Contains(query, StringComparer.Ordinal))
            {
                target.Add(query);
            }
        }

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            AddDistinct(queries, BuildRecordingQuery(title, artist, album, isrc));
            return queries;
        }

        var primaryArtist = StripFeaturingQualifier(artist);
        var coreTitle = StripFeaturingQualifier(title);

        // Prefer all available metadata first. The artist constraint covers both
        // the credited recording name and the artist entity's current name. This
        // matters when an artist has been renamed (for example, older recordings
        // credited to "Kanye West" now point at the artist entity named "Ye").
        AddDistinct(queries, BuildRecordingQuery(title, primaryArtist, album, string.Empty));

        // Media sessions often append a featured artist to the title and expose
        // store-specific album editions. MusicBrainz keeps guest artists in the
        // artist credit and commonly stores the recording under its core title,
        // so retry without those two brittle constraints when the strict search
        // does not yield a confident match.
        AddDistinct(queries, BuildRecordingQuery(coreTitle, primaryArtist, string.Empty, string.Empty));
        AddDistinct(queries, BuildRecordingQuery(coreTitle, string.Empty, string.Empty, string.Empty));
        return queries;
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
            var quotedArtist = QuoteQueryValue(artist);
            parts.Add($"(creditname:{quotedArtist} OR artistname:{quotedArtist})");
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
        string requestedTitle,
        string requestedArtist,
        string requestedAlbum,
        TimeSpan requestedDuration)
    {
        double score = recording.Score;
        if (TitleMatches(recording.Title, requestedTitle))
        {
            score += 30;
        }

        if (ArtistMatches(recording.ArtistCredit, requestedArtist))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(requestedAlbum)
            && recording.Releases.Any(release => MetadataTextEquals(release.Title, requestedAlbum)))
        {
            score += 18;
        }

        if (requestedDuration > TimeSpan.Zero)
        {
            if (recording.Length is > 0)
            {
                var difference = Math.Abs(recording.Length.Value - requestedDuration.TotalMilliseconds) / 1000;
                score -= Math.Min(25, difference / 2);
            }
            else
            {
                // A durationless result must not outrank a known studio recording
                // merely because MusicBrainz assigned the former a higher text score.
                score -= 25;
            }
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

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            return true;
        }

        var titleMatches = TitleMatches(recording.Title, requestedTitle);
        var artistProvided = !string.IsNullOrWhiteSpace(requestedArtist);
        var albumProvided = !string.IsNullOrWhiteSpace(requestedAlbum);
        var durationProvided = requestedDuration > TimeSpan.Zero;
        var artistMatches = ArtistMatches(recording.ArtistCredit, requestedArtist);
        var albumMatches = !string.IsNullOrWhiteSpace(requestedAlbum)
            && recording.Releases.Any(release => MetadataTextEquals(release.Title, requestedAlbum));
        var durationMatches = requestedDuration > TimeSpan.Zero
            && recording.Length is > 0
            && Math.Abs(recording.Length.Value - requestedDuration.TotalMilliseconds) <= 5000;

        if (titleMatches)
        {
            if (artistProvided || albumProvided)
            {
                return artistMatches || albumMatches;
            }

            return !durationProvided || durationMatches;
        }

        if (durationProvided && recording.Length is > 0)
        {
            return durationMatches && (artistMatches || albumMatches);
        }

        return artistMatches && albumMatches;
    }

    private static double ScoreRelease(ReleaseResult release, string requestedAlbum)
    {
        double score = 0;
        if (!string.IsNullOrWhiteSpace(requestedAlbum)
            && MetadataTextEquals(release.Title, requestedAlbum))
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

    private static bool TitleMatches(string? candidate, string? requested)
    {
        return MetadataTextEquals(candidate, requested)
            || MetadataTextEquals(
                StripFeaturingQualifier(candidate ?? string.Empty),
                StripFeaturingQualifier(requested ?? string.Empty));
    }

    private static bool ArtistMatches(
        IReadOnlyList<ArtistCredit> candidateCredits,
        string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        if (MetadataTextEquals(JoinArtistCredit(candidateCredits), requested))
        {
            return true;
        }

        var requestedPrimary = StripFeaturingQualifier(requested);
        return candidateCredits.Any(credit =>
        {
            var creditedName = credit.Name ?? credit.Artist?.Name;
            return MetadataTextEquals(creditedName, requested)
                || MetadataTextEquals(creditedName, requestedPrimary);
        });
    }

    private static bool MetadataTextEquals(string? left, string? right)
    {
        var normalizedLeft = NormalizeMetadataText(left);
        return normalizedLeft.Length > 0
            && string.Equals(normalizedLeft, NormalizeMetadataText(right), StringComparison.Ordinal);
    }

    private static string NormalizeMetadataText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToUpperInvariant(character));
            }
        }

        return normalized.ToString();
    }

    private static string StripFeaturingQualifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string[] markers =
        [
            " (feat.", " (feat ", " [feat.", " [feat ",
            " (ft.", " (ft ", " [ft.", " [ft ",
            " (featuring ", " [featuring ",
            " feat.", " feat ", " ft.", " ft ", " featuring "
        ];
        var end = value.Length;
        foreach (var marker in markers)
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0 && index < end)
            {
                end = index;
            }
        }

        return value[..end].Trim().TrimEnd('-', '\u2013', '\u2014', ':').TrimEnd();
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

    private sealed record MatchedRecording(
        RecordingResult Recording,
        ReleaseResult? Release);

    private sealed class ArtworkProtocolException(string message) : HttpRequestException(message);

    private sealed class RecordingSearchResponse
    {
        [JsonPropertyName("recordings")]
        public List<RecordingResult> Recordings { get; init; } = [];
    }

    private sealed class RecordingResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("score")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Score { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("length")]
        public double? Length { get; init; }

        [JsonPropertyName("first-release-date")]
        public string? FirstReleaseDate { get; init; }

        [JsonPropertyName("disambiguation")]
        public string? Disambiguation { get; init; }

        [JsonPropertyName("isrcs")]
        public List<JsonElement> Isrcs { get; init; } = [];

        [JsonPropertyName("artist-credit")]
        public List<ArtistCredit> ArtistCredit { get; init; } = [];

        [JsonPropertyName("releases")]
        public List<ReleaseResult> Releases { get; init; } = [];

        [JsonPropertyName("tags")]
        public List<TagResult> Tags { get; init; } = [];

        [JsonPropertyName("genres")]
        public List<TagResult> Genres { get; init; } = [];
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
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("sort-name")]
        public string? SortName { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("gender")]
        public string? Gender { get; init; }

        [JsonPropertyName("country")]
        public string? Country { get; init; }

        [JsonPropertyName("area")]
        public AreaResult? Area { get; init; }

        [JsonPropertyName("begin-area")]
        public AreaResult? BeginArea { get; init; }

        [JsonPropertyName("end-area")]
        public AreaResult? EndArea { get; init; }

        [JsonPropertyName("life-span")]
        public LifeSpanResult? LifeSpan { get; init; }

        [JsonPropertyName("disambiguation")]
        public string? Disambiguation { get; init; }

        [JsonPropertyName("annotation")]
        public string? Annotation { get; init; }

        [JsonPropertyName("aliases")]
        public List<AliasResult> Aliases { get; init; } = [];

        [JsonPropertyName("tags")]
        public List<TagResult> Tags { get; init; } = [];

        [JsonPropertyName("genres")]
        public List<TagResult> Genres { get; init; } = [];

        [JsonPropertyName("relations")]
        public List<UrlRelationResult> Relations { get; init; } = [];
    }

    private sealed class UrlRelationResult
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("ended")]
        public bool Ended { get; init; }

        [JsonPropertyName("end")]
        public string? End { get; init; }

        [JsonPropertyName("url")]
        public UrlResult? Url { get; init; }
    }

    private sealed class UrlResult
    {
        [JsonPropertyName("resource")]
        public string? Resource { get; init; }
    }

    private sealed class AreaResult
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class LifeSpanResult
    {
        [JsonPropertyName("begin")]
        public string? Begin { get; init; }

        [JsonPropertyName("end")]
        public string? End { get; init; }

        [JsonPropertyName("ended")]
        public bool? Ended { get; init; }
    }

    private sealed class AliasResult
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class TagResult
    {
        [JsonPropertyName("count")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
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

        [JsonPropertyName("country")]
        public string? Country { get; init; }

        [JsonPropertyName("barcode")]
        public string? Barcode { get; init; }

        [JsonPropertyName("packaging")]
        public string? Packaging { get; init; }

        [JsonPropertyName("disambiguation")]
        public string? Disambiguation { get; init; }

        [JsonPropertyName("artist-credit")]
        public List<ArtistCredit> ArtistCredit { get; init; } = [];

        [JsonPropertyName("label-info")]
        public List<LabelInfoResult> LabelInfo { get; init; } = [];

        [JsonPropertyName("tags")]
        public List<TagResult> Tags { get; init; } = [];

        [JsonPropertyName("genres")]
        public List<TagResult> Genres { get; init; } = [];

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

        [JsonPropertyName("secondary-types")]
        public List<string> SecondaryTypes { get; init; } = [];

        [JsonPropertyName("first-release-date")]
        public string? FirstReleaseDate { get; init; }

        [JsonPropertyName("tags")]
        public List<TagResult> Tags { get; init; } = [];

        [JsonPropertyName("genres")]
        public List<TagResult> Genres { get; init; } = [];
    }

    private sealed class LabelInfoResult
    {
        [JsonPropertyName("catalog-number")]
        public string? CatalogNumber { get; init; }

        [JsonPropertyName("label")]
        public LabelResult? Label { get; init; }
    }

    private sealed class LabelResult
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class MediumResult
    {
        [JsonPropertyName("position")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Position { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("format")]
        public string? Format { get; init; }

        [JsonPropertyName("track-count")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? TrackCount { get; init; }

        [JsonPropertyName("track")]
        public List<TrackResult> Tracks { get; init; } = [];

        [JsonPropertyName("tracks")]
        public List<TrackResult> LookupTracks { get; init; } = [];
    }

    private sealed class TrackResult
    {
        [JsonPropertyName("position")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Position { get; init; }

        [JsonPropertyName("number")]
        public string? Number { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("length")]
        public double? Length { get; init; }

        [JsonPropertyName("artist-credit")]
        public List<ArtistCredit> ArtistCredit { get; init; } = [];

        [JsonPropertyName("recording")]
        public RecordingResult? Recording { get; init; }
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
