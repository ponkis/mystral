using System.Globalization;

namespace Mystral.Models;

public sealed record GlobeProfile(
    string Username,
    string Name,
    string AvatarUrl,
    int CdCount = 0)
{
    public string UsernameWithoutAt => Username.Trim().TrimStart('@');

    public string DisplayUsername => string.IsNullOrWhiteSpace(UsernameWithoutAt)
        ? string.Empty
        : "@" + UsernameWithoutAt;

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? UsernameWithoutAt
        : Name.Trim();
}

public enum GlobeConnectionStatus
{
    Unlinked,
    Linking,
    Validating,
    Linked
}

public sealed record GlobeConnectionState(
    GlobeConnectionStatus Status,
    GlobeProfile? Profile = null,
    bool IsChecking = false,
    string ErrorMessage = "")
{
    public bool IsLinked => Status == GlobeConnectionStatus.Linked;

    public bool IsBusy => Status is GlobeConnectionStatus.Linking or GlobeConnectionStatus.Validating
                          || IsChecking;
}

public sealed class GlobeConnectionStateChangedEventArgs(GlobeConnectionState state) : EventArgs
{
    public GlobeConnectionState State { get; } = state;
}

public enum GlobeLinkRevocationSource
{
    StatusCheck,
    BurnRequest
}

public sealed class GlobeLinkRevokedEventArgs(
    GlobeLinkRevocationSource source,
    string message) : EventArgs
{
    public GlobeLinkRevocationSource Source { get; } = source;

    public string Message { get; } = message;
}

public sealed record class GlobeBurnShareRequest
{
    public const int MaximumTrackCount = 999;

    public GlobeBurnShareRequest(
        string album,
        string artist,
        DateTimeOffset burnedAt,
        int trackCount,
        byte[]? cover = null,
        string? burnId = null)
    {
        if (trackCount is < 0 or > MaximumTrackCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trackCount),
                $"Track count must be from 0 to {MaximumTrackCount}.");
        }

        Album = string.IsNullOrWhiteSpace(album) ? "Unknown album" : album.Trim();
        Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown artist" : artist.Trim();
        BurnedAt = burnedAt;
        TrackCount = trackCount;
        Cover = cover?.ToArray();
        BurnId = string.IsNullOrWhiteSpace(burnId)
            ? Guid.NewGuid().ToString("N")
            : burnId.Trim();
        if (BurnId.Length is < 8 or > 128
            || BurnId.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException(
                "Burn id must be 8-128 ASCII letters, digits, dots, underscores, colons, or hyphens.",
                nameof(burnId));
        }
    }

    public string Album { get; init; }

    public string Artist { get; init; }

    public DateTimeOffset BurnedAt { get; init; }

    public int TrackCount { get; init; }

    public byte[]? Cover { get; init; }

    /// <summary>
    /// Stable idempotency key. Reuse this request instance when retrying.
    /// </summary>
    public string BurnId { get; init; }

    public static GlobeBurnShareRequest FromDraft(
        BurnTrackDraft draft,
        DateTimeOffset? burnedAt = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var trackCount = int.TryParse(
            draft.TrackTotal,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsedTrackCount)
            ? parsedTrackCount
            : 0;
        return new GlobeBurnShareRequest(
            draft.Album,
            draft.Artist,
            burnedAt ?? DateTimeOffset.UtcNow,
            Math.Clamp(trackCount, 0, MaximumTrackCount),
            draft.CoverArtwork?.Data);
    }
}

public sealed record GlobeBurnShareResult(
    string PostId,
    string CollectionEntryId,
    string Message);

public sealed record class GlobeConnectionOptions
{
    public TimeSpan LinkPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan LinkTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromMinutes(5);
}

internal enum GlobeLinkClaimStatus
{
    Pending,
    Claimed,
    Expired
}

internal sealed record GlobeLinkClaimResult(
    GlobeLinkClaimStatus Status,
    string Token = "",
    GlobeProfile? Profile = null,
    string Message = "");
