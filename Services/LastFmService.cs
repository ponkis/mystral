using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

public sealed class LastFmService : IDisposable
{
    private const string BaseUrl = "https://ws.audioscrobbler.com/2.0/";
    public const string KeyRefusedMessage = "Last.fm rejected your API key.";

    // Last.fm's errors for a key it will not serve: 10 is an invalid key, 26 a
    // suspended one.
    private static readonly HashSet<int> KeyRefusedCodes = [10, 26];

    private readonly AppSettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, LastFmTrackInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string _sessionKey = string.Empty;
    private string _sessionCredentialKey = string.Empty;
    // The API key Last.fm last refused. Lookups stop while it is the saved key,
    // until settings are saved again or the app restarts.
    private volatile string _refusedApiKey = string.Empty;

    public LastFmService(AppSettingsService settingsService)
        : this(settingsService, new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
    }

    internal LastFmService(AppSettingsService settingsService, HttpClient httpClient)
    {
        _settingsService = settingsService;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        _settingsService.SettingsChanged += SettingsService_SettingsChanged;
    }

    public bool IsConfigured => _settingsService.Settings.LastFm.HasViewerCredentials;
    /// <summary>Last.fm refused the saved API key.</summary>
    public bool IsKeyRefused => IsKeyRefusedFor(_settingsService.Settings.LastFm.ApiKey);

    /// <summary>Last.fm refused this API key the last time it was used.</summary>
    public bool IsKeyRefusedFor(string apiKey) =>
        !string.IsNullOrEmpty(_refusedApiKey)
        && string.Equals(_refusedApiKey, apiKey.Trim(), StringComparison.Ordinal);

    public bool IsScrobblingEnabled =>
        _settingsService.Settings.LastFm.ScrobblingEnabled
        && _settingsService.Settings.LastFm.HasScrobblingCredentials;

    public async Task<LastFmValidationResult> ValidateCredentialsAsync(LastFmCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (!credentials.HasViewerCredentials)
        {
            return new LastFmValidationResult(false, "Fill in the Last.fm API key and username.");
        }

        if (credentials.ScrobblingEnabled && !credentials.HasScrobblingCredentials)
        {
            return new LastFmValidationResult(false, "Scrobbling also needs the API secret and password.");
        }

        try
        {
            if (credentials.ScrobblingEnabled)
            {
                var session = await GetSessionKeyAsync(credentials, forceRefresh: true, cancellationToken);
                if (string.IsNullOrWhiteSpace(session))
                {
                    return new LastFmValidationResult(false, "Last.fm did not return a scrobbling session.");
                }

                ClearRefusal(credentials.ApiKey);
                return new LastFmValidationResult(true, "Last.fm account and scrobbling verified.");
            }

            var url = $"{BaseUrl}?method=user.getInfo" +
                      $"&api_key={Uri.EscapeDataString(credentials.ApiKey.Trim())}" +
                      $"&user={Uri.EscapeDataString(credentials.Username.Trim())}" +
                      "&format=json";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (response.IsSuccessStatusCode && doc.RootElement.TryGetProperty("user", out _))
            {
                ClearRefusal(credentials.ApiKey);
                return LastFmValidationResult.Success;
            }

            if (NoteRefusal(doc, credentials.ApiKey))
            {
                return new LastFmValidationResult(false, KeyRefusedMessage);
            }

            var message = TryGetLastFmErrorMessage(doc) ?? $"Last.fm rejected the credentials ({(int)response.StatusCode}).";
            return new LastFmValidationResult(false, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return new LastFmValidationResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new LastFmValidationResult(false, $"Could not reach Last.fm: {ex.Message}");
        }
    }

    private void SettingsService_SettingsChanged(object? sender, EventArgs e)
    {
        _cache.Clear();
        _sessionKey = string.Empty;
        _sessionCredentialKey = string.Empty;
        _refusedApiKey = string.Empty;
    }

    public async Task<LastFmTrackInfo?> GetTrackInfoAsync(LastFmTrackQuery query, CancellationToken cancellationToken = default)
    {
        var credentials = _settingsService.Settings.LastFm;
        if (!credentials.HasViewerCredentials
            || string.IsNullOrWhiteSpace(query.ArtistName)
            || string.IsNullOrWhiteSpace(query.TrackName)
            || IsKeyRefusedFor(credentials.ApiKey))
        {
            return null;
        }

        var key = $"{query.ArtistName.Trim().ToLowerInvariant()}|{query.TrackName.Trim().ToLowerInvariant()}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var url = $"{BaseUrl}?method=track.getInfo" +
                      $"&api_key={Uri.EscapeDataString(credentials.ApiKey)}" +
                      $"&artist={Uri.EscapeDataString(query.ArtistName.Trim())}" +
                      $"&track={Uri.EscapeDataString(query.TrackName.Trim())}" +
                      "&format=json&autocorrect=1";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (NoteRefusal(doc, credentials.ApiKey))
            {
                // Not cached: the track is looked up again once the key works.
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _cache[key] = null;
                return null;
            }

            if (!doc.RootElement.TryGetProperty("track", out var trackElement))
            {
                _cache[key] = null;
                return null;
            }

            var trackUrl = trackElement.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(trackUrl))
            {
                _cache[key] = null;
                return null;
            }

            var trackName = trackElement.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString()
                : query.TrackName;

            var artistName = trackElement.TryGetProperty("artist", out var artistProp)
                             && artistProp.TryGetProperty("name", out var artistNameProp)
                ? artistNameProp.GetString()
                : query.ArtistName;

            var albumName = ReadAlbumName(trackElement, query.AlbumName);
            var duration = ReadDuration(trackElement);

            var result = new LastFmTrackInfo(
                TrackName: LastFmMetadataCleaner.CleanTrackName(trackName ?? query.TrackName),
                ArtistName: LastFmMetadataCleaner.CleanArtistName(artistName ?? query.ArtistName, albumName),
                Url: trackUrl,
                AlbumName: albumName,
                Duration: duration);

            _cache[key] = result;
            return result;
        }
        catch
        {
            _cache[key] = null;
            return null;
        }
    }

    public async Task<LastFmSubmitResult> UpdateNowPlayingAsync(LastFmTrackInfo track, CancellationToken cancellationToken = default)
    {
        var credentials = _settingsService.Settings.LastFm;
        if (!IsScrobblingEnabled)
        {
            return LastFmSubmitResult.Failure("Scrobbling is disabled.");
        }

        if (IsKeyRefusedFor(credentials.ApiKey))
        {
            return LastFmSubmitResult.Failure(KeyRefusedMessage);
        }

        try
        {
            var sessionKey = await GetSessionKeyAsync(credentials, forceRefresh: false, cancellationToken);
            var parameters = CreateTrackParameters("track.updateNowPlaying", track, credentials.ApiKey, sessionKey);
            var result = await PostSignedAsync(parameters, credentials.ApiSecret, cancellationToken);
            return result.IsSuccess
                ? LastFmSubmitResult.Success("Now playing sent to Last.fm.")
                : LastFmSubmitResult.Failure(result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LastFmSubmitResult.Failure(IsKeyRefusedFor(credentials.ApiKey) ? KeyRefusedMessage : $"Last.fm now playing failed: {ex.Message}");
        }
    }

    public async Task<LastFmSubmitResult> ScrobbleAsync(LastFmTrackInfo track, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        var credentials = _settingsService.Settings.LastFm;
        if (!IsScrobblingEnabled)
        {
            return LastFmSubmitResult.Failure("Scrobbling is disabled.");
        }

        if (IsKeyRefusedFor(credentials.ApiKey))
        {
            return LastFmSubmitResult.Failure(KeyRefusedMessage);
        }

        try
        {
            var sessionKey = await GetSessionKeyAsync(credentials, forceRefresh: false, cancellationToken);
            var parameters = CreateTrackParameters("track.scrobble", track, credentials.ApiKey, sessionKey);
            parameters["timestamp"] = startedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var result = await PostSignedAsync(parameters, credentials.ApiSecret, cancellationToken);
            return result.IsSuccess
                ? LastFmSubmitResult.Success($"Scrobbled {track.ArtistName} - {track.TrackName}.")
                : LastFmSubmitResult.Failure(result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LastFmSubmitResult.Failure(IsKeyRefusedFor(credentials.ApiKey) ? KeyRefusedMessage : $"Last.fm scrobble failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        _httpClient.Dispose();
    }

    private async Task<string> GetSessionKeyAsync(LastFmCredentials credentials, bool forceRefresh, CancellationToken cancellationToken)
    {
        var credentialKey = $"{credentials.ApiKey.Trim()}|{credentials.Username.Trim()}|{credentials.Password}";
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_sessionKey) && credentialKey == _sessionCredentialKey)
        {
            return _sessionKey;
        }

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "auth.getMobileSession",
            ["username"] = credentials.Username.Trim(),
            ["password"] = credentials.Password,
            ["api_key"] = credentials.ApiKey.Trim()
        };

        var result = await PostSignedAsync(parameters, credentials.ApiSecret, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Message);
        }

        if (string.IsNullOrWhiteSpace(result.SessionKey))
        {
            throw new InvalidOperationException("Last.fm did not return a session key.");
        }

        _sessionKey = result.SessionKey;
        _sessionCredentialKey = credentialKey;
        return _sessionKey;
    }

    private async Task<SignedPostResult> PostSignedAsync(SortedDictionary<string, string> parameters, string apiSecret, CancellationToken cancellationToken)
    {
        parameters["api_sig"] = CreateSignature(parameters, apiSecret.Trim());
        parameters["format"] = "json";

        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _httpClient.PostAsync(BaseUrl, content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (response.IsSuccessStatusCode
            && doc.RootElement.TryGetProperty("session", out var session)
            && session.TryGetProperty("key", out var sessionKey))
        {
            return SignedPostResult.Success(sessionKey.GetString() ?? string.Empty);
        }

        if (response.IsSuccessStatusCode && !doc.RootElement.TryGetProperty("error", out _))
        {
            return SignedPostResult.Success();
        }

        if (NoteRefusal(doc, parameters.TryGetValue("api_key", out var apiKey) ? apiKey : string.Empty))
        {
            return SignedPostResult.Failure(KeyRefusedMessage);
        }

        var message = TryGetLastFmErrorMessage(doc) ?? $"Last.fm rejected the request ({(int)response.StatusCode}).";
        return SignedPostResult.Failure(message);
    }

    private static SortedDictionary<string, string> CreateTrackParameters(string method, LastFmTrackInfo track, string apiKey, string sessionKey)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = method,
            ["api_key"] = apiKey.Trim(),
            ["sk"] = sessionKey,
            ["artist"] = track.ArtistName,
            ["track"] = track.TrackName
        };

        if (!string.IsNullOrWhiteSpace(track.AlbumName))
        {
            parameters["album"] = track.AlbumName;
        }

        if (track.Duration > TimeSpan.Zero)
        {
            parameters["duration"] = Math.Round(track.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return parameters;
    }

    private static string CreateSignature(SortedDictionary<string, string> parameters, string apiSecret)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (key.Equals("format", StringComparison.OrdinalIgnoreCase)
                || key.Equals("callback", StringComparison.OrdinalIgnoreCase)
                || key.Equals("api_sig", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(key);
            builder.Append(value);
        }

        builder.Append(apiSecret);
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ReadAlbumName(JsonElement trackElement, string fallback)
    {
        if (trackElement.TryGetProperty("album", out var albumElement)
            && albumElement.TryGetProperty("title", out var titleElement))
        {
            var title = LastFmMetadataCleaner.CleanAlbumName(titleElement.GetString() ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return LastFmMetadataCleaner.CleanAlbumName(fallback);
    }

    private static TimeSpan ReadDuration(JsonElement trackElement)
    {
        if (!trackElement.TryGetProperty("duration", out var durationElement))
        {
            return TimeSpan.Zero;
        }

        var durationText = durationElement.ValueKind == JsonValueKind.Number
            ? durationElement.GetInt64().ToString(CultureInfo.InvariantCulture)
            : durationElement.GetString();

        return long.TryParse(durationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
               && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.Zero;
    }

    /// <summary>Remembers the key when Last.fm's answer refuses it.</summary>
    private bool NoteRefusal(JsonDocument doc, string apiKey)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Number
            || !error.TryGetInt32(out var code)
            || !KeyRefusedCodes.Contains(code))
        {
            return false;
        }

        _refusedApiKey = apiKey.Trim();
        return true;
    }

    private void ClearRefusal(string apiKey)
    {
        if (IsKeyRefusedFor(apiKey))
        {
            _refusedApiKey = string.Empty;
        }
    }

    private static string? TryGetLastFmErrorMessage(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("message", out var message))
        {
            return null;
        }

        var text = message.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private sealed record SignedPostResult(bool IsSuccess, string Message, string SessionKey = "")
    {
        public static SignedPostResult Success(string sessionKey = "") => new(true, string.Empty, sessionKey);
        public static SignedPostResult Failure(string message) => new(false, message);
    }
}
