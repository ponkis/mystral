namespace Mystral.Models;

/// <summary>
/// One finished play for a ListenBrainz-compatible scrobbling server
/// (listenbrainz.org, or any server that speaks its API).
/// </summary>
public sealed record ListenBrainzListen(
    string Artist,
    string Track,
    string Album,
    long ListenedAt,
    int DurationSeconds);

/// <summary>The song playing right now, for a ListenBrainz-compatible server.</summary>
public sealed record ListenBrainzNowPlaying(
    string Artist,
    string Track,
    string Album,
    int DurationSeconds);

/// <summary>
/// What a server says about a token: whose it is, and (from servers that send
/// them, not listenbrainz.org itself) a picture and a name for the site.
/// </summary>
public sealed record ListenBrainzAccount(
    string UserName,
    string? SiteName,
    Uri? AvatarUri);

public enum ListenBrainzConnectionStatus
{
    Disconnected,
    Connecting,
    Connected
}

/// <summary>A token-free view of the connection, for the UI.</summary>
public sealed record ListenBrainzConnectionState(
    ListenBrainzConnectionStatus Status,
    Uri? Server = null,
    string UserName = "",
    string? SiteName = null,
    Uri? AvatarUri = null,
    string Message = "")
{
    public bool IsConnected => Status == ListenBrainzConnectionStatus.Connected;

    /// <summary>The server's host, as the settings show it ("listenbrainz.org").</summary>
    public string Host => SiteName is { Length: > 0 } siteName
        ? siteName
        : Server is null
            ? string.Empty
            : Server.IsDefaultPort ? Server.Host : $"{Server.Host}:{Server.Port}";
}
