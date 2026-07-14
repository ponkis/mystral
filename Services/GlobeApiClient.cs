using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

/// <summary>
/// Low-level client for the first-party Mystral endpoints. UI code should use
/// <see cref="GlobeConnectionService"/> so bearer tokens never leave the
/// connection layer.
/// </summary>
public sealed class GlobeApiClient : IDisposable
{
    private const int MaximumCoverBytes = 5 * 1024 * 1024;
    private const int MaximumUsernameLength = 80;
    private const int MaximumDisplayNameLength = 160;
    private const int MaximumAvatarUrlLength = 2048;
    private const string LinkCancelledMessage = "The globe account link was canceled.";
    private const string LinkExpiredMessage =
        "The globe link request expired. Start again from Mystral.";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GlobeApiClient()
        : this(
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
            AppMetadata.GlobeBaseUri,
            ownsHttpClient: true)
    {
    }

    public GlobeApiClient(HttpClient httpClient, Uri? baseUri = null)
        : this(httpClient, baseUri ?? httpClient.BaseAddress ?? AppMetadata.GlobeBaseUri, ownsHttpClient: false)
    {
    }

    private GlobeApiClient(HttpClient httpClient, Uri baseUri, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("globe base URI must be absolute.", nameof(baseUri));
        }

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        BaseUri = EnsureTrailingSlash(baseUri);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        }
    }

    public Uri BaseUri { get; }

    public Uri CreateApprovalUri(string linkCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkCode);
        var builder = new UriBuilder(new Uri(BaseUri, "settings/connections/mystral/approve"))
        {
            Query = "code=" + Uri.EscapeDataString(linkCode)
        };
        return builder.Uri;
    }

    internal Task<GlobeLinkClaimResult> RegisterLinkAsync(
        string linkCode,
        string codeChallenge,
        CancellationToken cancellationToken = default)
    {
        ValidatePkceValue(codeChallenge, nameof(codeChallenge));
        return SendLinkClaimAsync(
            new LinkCodePayload
            {
                LinkCode = linkCode,
                CodeChallenge = codeChallenge
            },
            cancellationToken);
    }

    internal Task<GlobeLinkClaimResult> ClaimLinkAsync(
        string linkCode,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        ValidatePkceValue(codeVerifier, nameof(codeVerifier));
        return SendLinkClaimAsync(
            new LinkCodePayload
            {
                LinkCode = linkCode,
                CodeVerifier = codeVerifier
            },
            cancellationToken);
    }

    private async Task<GlobeLinkClaimResult> SendLinkClaimAsync(
        LinkCodePayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.LinkCode);
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            "api/mystral/link/claim",
            payload);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
        {
            return new GlobeLinkClaimResult(GlobeLinkClaimStatus.Pending);
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            var errorCode = ReadErrorCode(body);
            if (errorCode.Equals("link_code_cancelled", StringComparison.OrdinalIgnoreCase)
                || errorCode.Equals("link_cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new GlobeLinkClaimResult(
                    GlobeLinkClaimStatus.Cancelled,
                    Message: LinkCancelledMessage);
            }

            return new GlobeLinkClaimResult(
                GlobeLinkClaimStatus.Expired,
                Message: LinkExpiredMessage);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response, body, "globe could not claim the link code.");
        }

        using var document = ParseJson(body, "globe returned an invalid link response.");
        var root = UnwrapData(document.RootElement);
        var status = ReadString(root, "status");
        if (ReadBoolean(root, "pending") || status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            return new GlobeLinkClaimResult(GlobeLinkClaimStatus.Pending);
        }

        if (ReadBoolean(root, "expired") || status.Equals("expired", StringComparison.OrdinalIgnoreCase))
        {
            return new GlobeLinkClaimResult(
                GlobeLinkClaimStatus.Expired,
                Message: LinkExpiredMessage);
        }

        if (ReadBoolean(root, "cancelled")
            || ReadBoolean(root, "canceled")
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new GlobeLinkClaimResult(
                GlobeLinkClaimStatus.Cancelled,
                Message: LinkCancelledMessage);
        }

        var token = ReadString(root, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new GlobeApiException("globe approved the link but did not return a token.");
        }

        return new GlobeLinkClaimResult(
            GlobeLinkClaimStatus.Claimed,
            token,
            ReadProfile(root));
    }

    internal async Task AcknowledgeLinkAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Post, "api/mystral/link/ack", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfAuthenticationRejected(response);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response, body, "globe could not finish the account link.");
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }

        using var document = ParseJson(body, "globe returned an invalid link acknowledgement.");
        var root = UnwrapData(document.RootElement);
        if (!ReadBoolean(root, "linked") || !ReadBoolean(root, "acknowledged"))
        {
            throw new GlobeApiException("globe did not confirm the account link.");
        }
    }

    internal async Task<GlobeProfile> GetStatusAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "api/mystral/link/status", token);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        request.Headers.Pragma.ParseAdd("no-cache");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfAuthenticationRejected(response);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response, body, "globe could not validate the account link.");
        }

        using var document = ParseJson(body, "globe returned an invalid link status.");
        var root = UnwrapData(document.RootElement);
        if (TryReadBoolean(root, "linked", out var linked) && !linked)
        {
            throw new GlobeAuthenticationException("Your globe account is no longer linked.");
        }

        return ReadProfile(root);
    }

    internal async Task RevokeAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Post, "api/mystral/link/revoke", token);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode
            || response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw CreateApiException(response, body, "globe could not unlink the account.");
    }

    internal async Task<GlobeBurnShareResult> ShareBurnAsync(
        string token,
        GlobeBurnShareRequest burn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(burn);
        if (string.IsNullOrWhiteSpace(burn.BurnId))
        {
            throw new ArgumentException("A stable burn id is required for safe retries.", nameof(burn));
        }

        using var request = CreateJsonRequest(
            HttpMethod.Post,
            "api/mystral/burns",
            new BurnPayload
            {
                BurnId = burn.BurnId,
                Album = burn.Album,
                Artist = burn.Artist,
                BurnedAt = burn.BurnedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                TrackCount = burn.TrackCount,
                Cover = burn.Cover is null ? null : CreateCoverDataUrl(burn.Cover)
            });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeToken(token));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", burn.BurnId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfAuthenticationRejected(response);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response, body, "globe could not share the burned CD.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new GlobeApiException("globe did not confirm that the burned CD was shared.");
        }

        using var document = ParseJson(body, "globe returned an invalid burn response.");
        var root = UnwrapData(document.RootElement);
        if (!ReadBoolean(root, "shared"))
        {
            throw new GlobeApiException("globe did not confirm that the burned CD was shared.");
        }

        var postId = ReadString(root, "post_id", "postId");
        if (string.IsNullOrWhiteSpace(postId)
            && root.TryGetProperty("post", out var post)
            && post.ValueKind == JsonValueKind.Object)
        {
            postId = ReadString(post, "id");
        }

        var collectionEntryId = ReadString(root, "collection_entry_id", "collectionEntryId", "cd_id");
        if (root.TryGetProperty("burn", out var savedBurn) && savedBurn.ValueKind == JsonValueKind.Object)
        {
            postId = FirstNonEmpty(postId, ReadString(savedBurn, "post_id", "postId"));
            collectionEntryId = FirstNonEmpty(collectionEntryId, ReadString(savedBurn, "id"));
        }

        if (string.IsNullOrWhiteSpace(postId) || string.IsNullOrWhiteSpace(collectionEntryId))
        {
            throw new GlobeApiException("globe returned an incomplete burned CD response.");
        }

        return new GlobeBurnShareResult(
            postId,
            collectionEntryId,
            "The burned CD was shared to globe.",
            TryReadBoolean(root, "created", out var created) && created);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private GlobeProfile ReadProfile(JsonElement root)
    {
        var source = root;
        if (root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
        {
            source = profile;
        }

        var username = ReadString(source, "username").Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username) || username.Length > MaximumUsernameLength)
        {
            throw new GlobeApiException("globe did not return the linked username.");
        }

        var avatarUrl = ReadString(source, "avatar_url", "avatarUrl", "profile_picture", "profilePicture");
        if (avatarUrl.Length > MaximumAvatarUrlLength)
        {
            avatarUrl = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(avatarUrl))
        {
            if (!Uri.TryCreate(avatarUrl, UriKind.RelativeOrAbsolute, out var avatarUri))
            {
                avatarUrl = string.Empty;
            }
            else
            {
                avatarUri = avatarUri.IsAbsoluteUri
                    ? avatarUri
                    : new Uri(BaseUri, avatarUri);
                avatarUrl = AppMetadata.IsTrustedGlobeAvatarUri(avatarUri, BaseUri)
                    ? avatarUri.AbsoluteUri
                    : string.Empty;
            }
        }

        var displayName = ReadString(source, "name", "display_name", "displayName").Trim();
        if (displayName.Length > MaximumDisplayNameLength)
        {
            displayName = displayName[..MaximumDisplayNameLength];
        }

        return new GlobeProfile(
            username,
            displayName,
            avatarUrl,
            ReadInteger(source, "cd_count", "burn_count", "cdCount", "burnCount"));
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string relativePath, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeToken(token));
        return request;
    }

    private HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string relativePath, T payload)
    {
        return new HttpRequestMessage(method, new Uri(BaseUri, relativePath))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
    }

    private static string NormalizeToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return token.Trim();
    }

    private static void ValidatePkceValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length is < 43 or > 128
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_' or '.' or '~')))
        {
            throw new ArgumentException("The PKCE value is invalid.", parameterName);
        }
    }

    private static void ThrowIfAuthenticationRejected(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new GlobeAuthenticationException(
                "Your globe account is no longer linked.",
                response.StatusCode);
        }
    }

    private static GlobeApiException CreateApiException(
        HttpResponseMessage response,
        string body,
        string fallback)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero)
            {
                retryAfter = TimeSpan.Zero;
            }
        }

        var errorCode = ReadErrorCode(body);
        return new GlobeApiException(
            FriendlyErrorMessage(response.StatusCode, errorCode, fallback),
            response.StatusCode,
            errorCode,
            retryAfter);
    }

    private static string FriendlyErrorMessage(
        HttpStatusCode statusCode,
        string errorCode,
        string fallback)
    {
        var code = errorCode.Trim().ToLowerInvariant();
        if (statusCode == HttpStatusCode.TooManyRequests || code == "too_many_requests")
        {
            return "globe is receiving too many requests. Please wait a moment and try again.";
        }

        if (code == "invalid_cover")
        {
            return "globe couldn't use the cover image. Choose another image and try again.";
        }

        if (code is "cover_validation_busy" or "cover_storage_unavailable")
        {
            return "globe couldn't process the cover right now. Please try again.";
        }

        if (code is "invalid_burn" or "invalid_idempotency_key")
        {
            return "globe couldn't share this CD. Check the album details and try again.";
        }

        if (code is "invalid_link_code"
            or "invalid_code_challenge"
            or "invalid_code_verifier"
            or "link_code_used"
            or "link_code_already_approved"
            or "link_already_completed"
            or "link_not_approved"
            or "desktop_not_waiting"
            or "link_claim_failed"
            or "acknowledgement_failed")
        {
            return "Mystral couldn't complete the account link. Please try again.";
        }

        if (statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout)
        {
            return "globe is unavailable right now. Please try again.";
        }

        if ((int)statusCode >= 500)
        {
            return "globe couldn't complete the request right now. Please try again.";
        }

        // Every fallback is authored locally for its operation. Server prose is
        // deliberately ignored so internal validation details never reach WPF.
        return fallback;
    }

    private static JsonDocument ParseJson(string json, string message)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new GlobeApiException(message, innerException: ex);
        }
    }

    private static JsonElement UnwrapData(JsonElement root)
    {
        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;
    }

    private static string ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var code = ReadString(root, "code", "error_code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                return code;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("error", out error) && error.ValueKind == JsonValueKind.Object)
            {
                return ReadString(error, "code");
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }

            if (element.TryGetProperty(propertyName, out property)
                && property.ValueKind == JsonValueKind.Number)
            {
                return property.GetRawText();
            }
        }

        return string.Empty;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return TryReadBoolean(element, propertyName, out var value) && value;
    }

    private static bool TryReadBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && bool.TryParse(property.GetString(), out value);
    }

    private static int ReadInteger(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            {
                return Math.Max(0, number);
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return Math.Max(0, number);
            }
        }

        return 0;
    }

    private static string FirstNonEmpty(string? first, string fallback)
    {
        return string.IsNullOrWhiteSpace(first) ? fallback : first;
    }

    private static string? CreateCoverDataUrl(byte[] cover)
    {
        if (cover.Length > MaximumCoverBytes)
        {
            return null;
        }

        var mimeType = cover.AsSpan() switch
        {
            var bytes when bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) => "image/png",
            var bytes when bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) => "image/jpeg",
            var bytes when bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) => "image/gif",
            var bytes when bytes.Length >= 12
                           && bytes.StartsWith("RIFF"u8)
                           && bytes[8..].StartsWith("WEBP"u8) => "image/webp",
            _ => null
        };
        if (mimeType is null)
        {
            // globe supplies its jewel-case placeholder when no supported
            // cover is sent; an unusual local image must not fail the share.
            return null;
        }

        return $"data:{mimeType};base64,{Convert.ToBase64String(cover)}";
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }

    private sealed class LinkCodePayload
    {
        [JsonPropertyName("link_code")]
        public required string LinkCode { get; init; }

        [JsonPropertyName("code_challenge")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CodeChallenge { get; init; }

        [JsonPropertyName("code_verifier")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CodeVerifier { get; init; }
    }

    private sealed class BurnPayload
    {
        [JsonPropertyName("client_burn_id")]
        public required string BurnId { get; init; }

        [JsonPropertyName("album")]
        public required string Album { get; init; }

        [JsonPropertyName("artist")]
        public required string Artist { get; init; }

        [JsonPropertyName("burned_at")]
        public required string BurnedAt { get; init; }

        [JsonPropertyName("track_count")]
        public int TrackCount { get; init; }

        [JsonPropertyName("cover")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Cover { get; init; }
    }
}
