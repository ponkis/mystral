using System.IO;
using System.Text.Json;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

/// <summary>
/// Mystral's connection to a ListenBrainz-compatible scrobbling server
/// (Settings → Scrobbling): listenbrainz.org, or any server that speaks its
/// API, such as a personal website. Someone pastes the server's address and a
/// token the server gave them; once the server accepts it, finished plays and
/// the song playing now go there until they disconnect.
///
/// The token is kept only in the DPAPI store (<see cref="TokenCredentialKey"/>)
/// and only this class and <see cref="ListenBrainzClient"/> see it; the UI
/// reads the token-free <see cref="State"/>. The rest of the connection (the
/// server, whose account it is, how many plays were sent) is in
/// <c>listenbrainz-connection.json</c> in the local app data folder, not in
/// settings.json, so sending a play never rewrites the settings.
/// It is independent of the globe link.
/// </summary>
public sealed class ListenBrainzConnectionService : IDisposable
{
    internal const string TokenCredentialKey = "listenbrainz.token.v1";
    public const string RefusedTokenMessage = "Disconnected: the server refused the token.";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ISecureCredentialStore _credentialStore;
    private readonly ListenBrainzClient _client;
    private readonly bool _ownsClient;
    private readonly ListenBrainzQueueService _queue;
    private readonly string _connectionPath;
    private readonly object _lock = new();
    private string? _token;
    private ConnectionFile _connection;
    private ListenBrainzConnectionState _state;

    public ListenBrainzConnectionService(AppSettingsService settingsService)
        : this(
            settingsService?.CredentialStore ?? throw new ArgumentNullException(nameof(settingsService)),
            new ListenBrainzClient(),
            new ListenBrainzQueueService(),
            Path.Combine(AppMetadata.LocalApplicationDataDirectory, "listenbrainz-connection.json"),
            ownsClient: true)
    {
    }

    internal ListenBrainzConnectionService(
        ISecureCredentialStore credentialStore,
        ListenBrainzClient client,
        ListenBrainzQueueService queue,
        string connectionPath,
        bool ownsClient = false)
    {
        _credentialStore = credentialStore;
        _client = client;
        _queue = queue;
        _connectionPath = connectionPath;
        _ownsClient = ownsClient;
        _connection = ReadConnectionFile();

        try
        {
            _token = NormalizeToken(_credentialStore.Read(TokenCredentialKey));
        }
        catch
        {
            _token = null;
        }

        _state = _token is not null && TryServer(out var server)
            ? new ListenBrainzConnectionState(
                ListenBrainzConnectionStatus.Connected,
                server,
                _connection.UserName,
                NullIfEmpty(_connection.SiteName),
                AvatarOf(server))
            : new ListenBrainzConnectionState(ListenBrainzConnectionStatus.Disconnected, TryServer(out var last) ? last : null);
    }

    /// <summary>Raised on whichever thread changed the state.</summary>
    public event EventHandler? StateChanged;

    public ListenBrainzConnectionState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public bool IsConnected => State.IsConnected;

    /// <summary>The address last used, to fill the box in with (listenbrainz.org's when there's none).</summary>
    public string LastServerText => TryServer(out var server) ? server.ToString() : "https://api.listenbrainz.org/";

    /// <summary>Plays the server has taken since connecting.</summary>
    public int PlaysSent
    {
        get
        {
            lock (_lock)
            {
                return _connection.PlaysSent;
            }
        }
    }

    public int WaitingCount => _queue.Count;

    /// <summary>
    /// Checks the token with the server and keeps it if the server accepts it.
    /// Throws with a message to show when it doesn't (the state goes back to
    /// disconnected).
    /// </summary>
    public async Task<ListenBrainzAccount> ConnectAsync(string serverText, string token, CancellationToken cancellationToken = default)
    {
        if (!ListenBrainzClient.TryNormalizeServer(serverText, out var server, out var error))
        {
            throw new ListenBrainzRefusedException(error);
        }

        var cleanToken = NormalizeToken(token)
            ?? throw new ListenBrainzRefusedException("Paste the token the server gave you.");

        SetState(new ListenBrainzConnectionState(ListenBrainzConnectionStatus.Connecting, server));
        try
        {
            var account = await _client.ValidateTokenAsync(server, cleanToken, cancellationToken);
            _credentialStore.Write(TokenCredentialKey, cleanToken);
            lock (_lock)
            {
                var sameServer = string.Equals(_connection.ServerUrl, server.ToString(), StringComparison.OrdinalIgnoreCase);
                _token = cleanToken;
                _connection = new ConnectionFile
                {
                    ServerUrl = server.ToString(),
                    UserName = account.UserName,
                    SiteName = account.SiteName ?? string.Empty,
                    AvatarUrl = account.AvatarUri?.ToString() ?? string.Empty,
                    PlaysSent = sameServer ? _connection.PlaysSent : 0
                };
                WriteConnectionFile(_connection);
            }

            SetState(new ListenBrainzConnectionState(ListenBrainzConnectionStatus.Connected, server, account.UserName, account.SiteName, account.AvatarUri));
            return account;
        }
        catch (Exception ex)
        {
            SetState(new ListenBrainzConnectionState(
                ListenBrainzConnectionStatus.Disconnected,
                server,
                Message: ex is OperationCanceledException ? string.Empty : ex.Message));
            throw;
        }
    }

    /// <summary>Forgets the token and the plays still waiting for the server.</summary>
    public void Disconnect(string message = "")
    {
        Uri? server;
        lock (_lock)
        {
            _token = null;
            TryServer(out server);
            _connection = new ConnectionFile { ServerUrl = _connection.ServerUrl };
            WriteConnectionFile(_connection);
        }

        try
        {
            _credentialStore.Delete(TokenCredentialKey);
        }
        catch
        {
            // The token is already forgotten in memory; a stale protected copy
            // is replaced on the next connect.
        }

        _queue.Clear();
        SetState(new ListenBrainzConnectionState(ListenBrainzConnectionStatus.Disconnected, server, Message: message));
    }

    /// <summary>Keeps a finished play until the server has it (see <see cref="FlushAsync"/>).</summary>
    public void Enqueue(ListenBrainzListen listen)
    {
        if (IsConnected)
        {
            _queue.Enqueue(listen);
        }
    }

    /// <summary>Sends the waiting plays. Returns how many the server took.</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!TryConnection(out var server, out var token))
        {
            return 0;
        }

        var sent = await _queue.FlushAsync(async (batch, ct) =>
        {
            try
            {
                await _client.SubmitListensAsync(server, token, batch, ct);
            }
            catch (ListenBrainzUnauthorizedException)
            {
                Disconnect(RefusedTokenMessage);
                throw;
            }
        }, cancellationToken);
        CountSent(sent);
        return sent;
    }

    /// <summary>
    /// Sends plays straight away, 1000 a request (Settings' "Send my play
    /// history"). Throws when the server can't take them.
    /// </summary>
    public async Task<int> SubmitHistoryAsync(IReadOnlyList<ListenBrainzListen> listens, CancellationToken cancellationToken = default)
    {
        if (!TryConnection(out var server, out var token))
        {
            throw new ListenBrainzUnauthorizedException("Connect to a server first.");
        }

        var sent = 0;
        try
        {
            foreach (var chunk in listens.Chunk(ListenBrainzClient.MaximumListensPerRequest))
            {
                await _client.SubmitListensAsync(server, token, chunk, cancellationToken);
                sent += chunk.Length;
            }
        }
        catch (ListenBrainzUnauthorizedException)
        {
            Disconnect(RefusedTokenMessage);
            throw;
        }
        finally
        {
            CountSent(sent);
        }

        return sent;
    }

    /// <summary>Shows the song on the server while it plays. Failures are ignored: it lapses there on its own.</summary>
    public async Task SetPlayingNowAsync(ListenBrainzNowPlaying track, CancellationToken cancellationToken = default)
    {
        if (!TryConnection(out var server, out var token))
        {
            return;
        }

        try
        {
            await _client.SetPlayingNowAsync(server, token, track, cancellationToken);
        }
        catch (ListenBrainzUnauthorizedException)
        {
            Disconnect(RefusedTokenMessage);
        }
        catch (Exception ex) when (ex is ListenBrainzUnavailableException or ListenBrainzRefusedException or OperationCanceledException)
        {
        }
    }

    /// <summary>The account's picture from the server, or null when it has none Mystral may show.</summary>
    public async Task<byte[]?> DownloadAvatarAsync(CancellationToken cancellationToken = default)
    {
        var state = State;
        if (!state.IsConnected || state.AvatarUri is null || !TryConnection(out var server, out var token))
        {
            return null;
        }

        try
        {
            return await _client.DownloadAvatarAsync(state.AvatarUri, server, token, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private bool TryConnection(out Uri server, out string token)
    {
        lock (_lock)
        {
            token = _token ?? string.Empty;
            if (_token is null || !TryServer(out server))
            {
                server = null!;
                return false;
            }

            return true;
        }
    }

    private void CountSent(int sent)
    {
        if (sent <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_token is null)
            {
                return;
            }

            _connection = _connection with { PlaysSent = _connection.PlaysSent + sent };
            WriteConnectionFile(_connection);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryServer(out Uri server)
    {
        return ListenBrainzClient.TryNormalizeServer(_connection.ServerUrl, out server, out _)
               && !string.IsNullOrWhiteSpace(_connection.ServerUrl);
    }

    private Uri? AvatarOf(Uri server)
    {
        return Uri.TryCreate(_connection.AvatarUrl, UriKind.Absolute, out var avatar)
               && ListenBrainzClient.IsSafeAvatarUri(avatar, server)
            ? avatar
            : null;
    }

    private void SetState(ListenBrainzConnectionState state)
    {
        lock (_lock)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? NormalizeToken(string? token)
    {
        var value = token?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 1024 || value.Any(char.IsWhiteSpace) ? null : value;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private ConnectionFile ReadConnectionFile()
    {
        try
        {
            return File.Exists(_connectionPath)
                ? JsonSerializer.Deserialize<ConnectionFile>(File.ReadAllText(_connectionPath), JsonOptions) ?? new ConnectionFile()
                : new ConnectionFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ConnectionFile();
        }
    }

    private void WriteConnectionFile(ConnectionFile connection)
    {
        try
        {
            var directory = Path.GetDirectoryName(_connectionPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _connectionPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(connection, JsonOptions));
            File.Move(temporaryPath, _connectionPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only presentation and the sent count are lost; the token is safe.
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed record ConnectionFile
    {
        public string ServerUrl { get; init; } = string.Empty;
        public string UserName { get; init; } = string.Empty;
        public string SiteName { get; init; } = string.Empty;
        public string AvatarUrl { get; init; } = string.Empty;
        public int PlaysSent { get; init; }
    }
}
