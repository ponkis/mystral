using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

/// <summary>The server is unreachable or busy right now; try again later.</summary>
public sealed class ListenBrainzUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server refused the token (revoked, deleted, or never valid).</summary>
public sealed class ListenBrainzUnauthorizedException(string message) : Exception(message);

/// <summary>The server refused what was sent; sending it again would fail the same way.</summary>
public sealed class ListenBrainzRefusedException(string message) : Exception(message);

/// <summary>
/// Speaks ListenBrainz's scrobbling API (https://listenbrainz.readthedocs.io):
/// <c>GET 1/validate-token</c> and <c>POST 1/submit-listens</c> with
/// <c>Authorization: Token …</c>. Works with listenbrainz.org and with any
/// server that implements the same API, at whatever address it has. Only this
/// class and <see cref="ListenBrainzConnectionService"/> see the token.
/// </summary>
public sealed class ListenBrainzClient : IDisposable
{
    internal const int MaximumListensPerRequest = 1000;
    private const int MaximumResponseBytes = 1024 * 1024;
    internal const int MaximumAvatarBytes = 2 * 1024 * 1024;
    private const int MaximumTextLength = 512;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ListenBrainzClient()
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = MaximumResponseBytes
        }, ownsHttpClient: true)
    {
    }

    internal ListenBrainzClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        }
    }

    /// <summary>
    /// The server's API root from what someone typed: "listenbrainz.org",
    /// "https://example.com/listenbrainz/" or a pasted ".../1/submit-listens"
    /// all work. HTTPS only, except for this computer (a server under
    /// development). Returns false with a reason when it can't be used.
    /// </summary>
    public static bool TryNormalizeServer(string? text, out Uri server, out string error)
    {
        server = null!;
        error = string.Empty;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "Type the server's address.";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            error = "That isn't a web address.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Use just the server's address, without a name, password, ? or #.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "The address has to start with https:// (plain http only works for this computer).";
            return false;
        }

        // listenbrainz.org's website and its API live on different hosts.
        var host = uri.Host.Equals("listenbrainz.org", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.listenbrainz.org", StringComparison.OrdinalIgnoreCase)
            ? "api.listenbrainz.org"
            : uri.Host;

        var path = uri.AbsolutePath;
        foreach (var tail in new[] { "/1/submit-listens", "/1/validate-token", "/1/", "/1" })
        {
            if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^tail.Length];
                break;
            }
        }

        if (!path.EndsWith('/'))
        {
            path += "/";
        }

        server = new UriBuilder(uri.Scheme, host, uri.IsDefaultPort ? -1 : uri.Port, path).Uri;
        return true;
    }

    /// <summary>Whose token this is, or <see cref="ListenBrainzUnauthorizedException"/> if the server won't take it.</summary>
    public async Task<ListenBrainzAccount> ValidateTokenAsync(Uri server, string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, "1/validate-token"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ListenBrainzUnauthorizedException("The server refused the token.");
        }

        ThrowForStatus(response, body);
        if (body?["valid"]?.GetValueKind() != JsonValueKind.True)
        {
            throw new ListenBrainzUnauthorizedException("The server says that token isn't valid. Check you copied all of it.");
        }

        var userName = Clean(body["user_name"]);
        var siteName = Clean(body["site_name"]);
        Uri? avatar = null;
        if (Uri.TryCreate(Clean(body["avatar_url"]), UriKind.Absolute, out var avatarUri) && IsSafeAvatarUri(avatarUri, server))
        {
            avatar = avatarUri;
        }

        return new ListenBrainzAccount(
            userName.Length > 0 ? userName : "Connected",
            siteName.Length > 0 ? siteName : null,
            avatar);
    }

    /// <summary>Sends finished plays ("single" for one, "import" for more), up to 1000 at a time.</summary>
    public Task SubmitListensAsync(Uri server, string token, IReadOnlyList<ListenBrainzListen> listens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listens);
        if (listens.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (listens.Count > MaximumListensPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(listens), $"At most {MaximumListensPerRequest} listens a request.");
        }

        var payload = new JsonArray();
        foreach (var listen in listens)
        {
            var item = new JsonObject
            {
                ["listened_at"] = listen.ListenedAt,
                ["track_metadata"] = Metadata(listen.Artist, listen.Track, listen.Album, listen.DurationSeconds)
            };
            payload.Add(item);
        }

        return PostListensAsync(server, token, listens.Count == 1 ? "single" : "import", payload, cancellationToken);
    }

    /// <summary>Tells the server what is playing right now.</summary>
    public Task SetPlayingNowAsync(Uri server, string token, ListenBrainzNowPlaying track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var payload = new JsonArray
        {
            new JsonObject { ["track_metadata"] = Metadata(track.Artist, track.Track, track.Album, track.DurationSeconds) }
        };
        return PostListensAsync(server, token, "playing_now", payload, cancellationToken);
    }

    /// <summary>
    /// The account's picture, at most 2 MB, from the server's own host only
    /// (see <see cref="IsSafeAvatarUri"/>). The token goes along, since it's
    /// the same server and some serve an uploaded picture only to its owner
    /// (globe does). Null when there is none to show.
    /// </summary>
    public async Task<byte[]?> DownloadAvatarAsync(Uri avatar, Uri server, string token, CancellationToken cancellationToken = default)
    {
        if (!IsSafeAvatarUri(avatar, server))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, avatar);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is > MaximumAvatarBytes
            || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (output.Length + read > MaximumAvatarBytes)
            {
                return null;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A picture is only loaded from the server's own host, over HTTPS (or
    /// plain HTTP on this computer), with nothing hidden in the address.
    /// </summary>
    internal static bool IsSafeAvatarUri(Uri avatar, Uri server)
    {
        return avatar.IsAbsoluteUri
            && (avatar.Scheme == Uri.UriSchemeHttps || (avatar.Scheme == Uri.UriSchemeHttp && avatar.IsLoopback))
            && string.IsNullOrEmpty(avatar.UserInfo)
            && string.IsNullOrEmpty(avatar.Fragment)
            && string.Equals(avatar.Host, server.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject Metadata(string artist, string track, string album, int durationSeconds)
    {
        var info = new JsonObject
        {
            ["submission_client"] = AppMetadata.Name,
            ["submission_client_version"] = AppMetadata.Version,
            ["media_player"] = AppMetadata.Name
        };
        if (durationSeconds > 0)
        {
            info["duration_ms"] = durationSeconds * 1000L;
        }

        var metadata = new JsonObject
        {
            ["artist_name"] = Truncate(artist),
            ["track_name"] = Truncate(track),
            ["additional_info"] = info
        };
        if (!string.IsNullOrWhiteSpace(album))
        {
            metadata["release_name"] = Truncate(album);
        }

        return metadata;
    }

    private async Task PostListensAsync(Uri server, string token, string listenType, JsonArray payload, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["listen_type"] = listenType, ["payload"] = payload };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "1/submit-listens"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await SendAsync(request, cancellationToken);
        var answer = await ReadJsonAsync(response, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ListenBrainzUnauthorizedException("The server refused the token.");
        }

        ThrowForStatus(response, answer);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ListenBrainzUnavailableException("The server couldn't be reached.", ex);
        }
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void ThrowForStatus(HttpResponseMessage response, JsonNode? body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = Clean(body?["error"]);
        var status = (int)response.StatusCode;
        if (status == 429 || status >= 500)
        {
            throw new ListenBrainzUnavailableException(message.Length > 0 ? message : $"The server answered {status}.");
        }

        // Redirects aren't followed, so the token never goes anywhere else.
        if (status is >= 300 and < 400)
        {
            throw new ListenBrainzRefusedException("The server sent Mystral to another address. Check the server address (it should be the one the server's settings show).");
        }

        throw new ListenBrainzRefusedException(message.Length > 0 ? message : $"The server answered {status}.");
    }

    private static string Clean(JsonNode? node)
    {
        string text;
        try
        {
            text = node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : string.Empty;
        }
        catch (InvalidOperationException)
        {
            text = string.Empty;
        }

        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private static string Truncate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > MaximumTextLength ? text[..MaximumTextLength] : text;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
