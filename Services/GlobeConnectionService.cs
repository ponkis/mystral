using System.IO;
using System.Security.Cryptography;
using Mystral.Models;

namespace Mystral.Services;

/// <summary>
/// Owns the protected Globe token and exposes an observable, token-free state
/// for WPF consumers. Events are raised on the thread that completes the
/// operation; UI subscribers must dispatch to their Dispatcher when needed.
/// </summary>
public sealed class GlobeConnectionService : IDisposable, IAsyncDisposable
{
    internal const string TokenCredentialKey = "globe.api-token.v1";

    private readonly GlobeApiClient _apiClient;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly AppSettingsService _settingsService;
    private readonly GlobeConnectionOptions _options;
    private readonly bool _ownsApiClient;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _linkGate = new(1, 1);
    private readonly SemaphoreSlim _validationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private GlobeConnectionState _state;
    private string? _token;
    private bool _disposed;

    public GlobeConnectionService(
        AppSettingsService settingsService,
        GlobeConnectionOptions? options = null)
        : this(
            new GlobeApiClient(),
            settingsService?.CredentialStore ?? throw new ArgumentNullException(nameof(settingsService)),
            settingsService,
            options,
            ownsApiClient: true)
    {
    }

    public GlobeConnectionService(
        GlobeApiClient apiClient,
        AppSettingsService settingsService,
        GlobeConnectionOptions? options = null)
        : this(
            apiClient,
            settingsService?.CredentialStore ?? throw new ArgumentNullException(nameof(settingsService)),
            settingsService,
            options,
            ownsApiClient: false)
    {
    }

    internal GlobeConnectionService(
        GlobeApiClient apiClient,
        ISecureCredentialStore credentialStore,
        AppSettingsService settingsService,
        GlobeConnectionOptions? options = null,
        bool ownsApiClient = false)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(settingsService);
        _apiClient = apiClient;
        _credentialStore = credentialStore;
        _settingsService = settingsService;
        _options = options ?? new GlobeConnectionOptions();
        ValidateOptions(_options);
        _ownsApiClient = ownsApiClient;

        string loadError = string.Empty;
        try
        {
            _token = NormalizeStoredToken(_credentialStore.Read(TokenCredentialKey));
        }
        catch (Exception ex)
        {
            loadError = "Mystral could not read the protected Globe token: " + ex.Message;
            _token = null;
        }

        _state = _token is null
            ? new GlobeConnectionState(GlobeConnectionStatus.Unlinked, ErrorMessage: loadError)
            : new GlobeConnectionState(GlobeConnectionStatus.Validating);
    }

    public event EventHandler<GlobeConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised once when a stored token is rejected by Globe. Manual unlinking
    /// does not raise this warning event.
    /// </summary>
    public event EventHandler<GlobeLinkRevokedEventArgs>? LinkRevoked;

    public GlobeConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool HasStoredToken
    {
        get
        {
            lock (_sync)
            {
                return _token is not null;
            }
        }
    }

    /// <summary>
    /// Starts an immediate status validation and then periodic checks. Calling
    /// this more than once does not create duplicate monitor loops.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await ValidateAsync(cancellationToken);

        lock (_sync)
        {
            if (_monitorTask is not null)
            {
                return;
            }

            _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _monitorTask = MonitorAsync(_monitorCancellation.Token);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? monitor;
        lock (_sync)
        {
            cancellation = _monitorCancellation;
            monitor = _monitorTask;
            _monitorCancellation = null;
            _monitorTask = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (monitor is not null)
            {
                await monitor;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Opens a browser through <paramref name="openApprovalPage"/> and polls the
    /// single-use claim endpoint until approval, expiration, or cancellation.
    /// </summary>
    public async Task<GlobeProfile> LinkAsync(
        Action<Uri> openApprovalPage,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(openApprovalPage);
        if (!await _linkGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A Globe linking attempt is already in progress.");
        }

        try
        {
            if (HasStoredToken)
            {
                throw new InvalidOperationException("A Globe token is already stored. Unlink it before linking another account.");
            }

            PublishState(new GlobeConnectionState(GlobeConnectionStatus.Linking));
            var linkCode = CreateLinkCode();
            var approvalUri = _apiClient.CreateApprovalUri(linkCode);
            openApprovalPage(approvalUri);

            var deadline = DateTimeOffset.UtcNow + _options.LinkTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var claim = await _apiClient.ClaimLinkAsync(linkCode, cancellationToken);
                if (claim.Status == GlobeLinkClaimStatus.Claimed)
                {
                    if (claim.Profile is null)
                    {
                        throw new GlobeApiException("Globe approved the link but did not return profile information.");
                    }

                    _credentialStore.Write(TokenCredentialKey, claim.Token);
                    lock (_sync)
                    {
                        _token = claim.Token.Trim();
                    }

                    var settingsError = TrySetSettingsConnectionState(isLinked: true);
                    PublishState(new GlobeConnectionState(
                        GlobeConnectionStatus.Linked,
                        claim.Profile,
                        ErrorMessage: settingsError));
                    return claim.Profile;
                }

                if (claim.Status == GlobeLinkClaimStatus.Expired)
                {
                    throw new GlobeLinkExpiredException(
                        string.IsNullOrWhiteSpace(claim.Message)
                            ? "The Globe link code expired. Start linking again."
                            : claim.Message);
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                    remaining < _options.LinkPollInterval ? remaining : _options.LinkPollInterval,
                    cancellationToken);
            }

            throw new GlobeLinkExpiredException("The Globe link code expired. Start linking again.");
        }
        catch (OperationCanceledException)
        {
            PublishState(new GlobeConnectionState(GlobeConnectionStatus.Unlinked));
            throw;
        }
        catch (Exception ex)
        {
            PublishState(new GlobeConnectionState(
                GlobeConnectionStatus.Unlinked,
                ErrorMessage: ex.Message));
            throw;
        }
        finally
        {
            _linkGate.Release();
        }
    }

    /// <summary>
    /// Validates the current token. Transient failures are reported in State
    /// without clearing a link; a rejected token is cleared and raises
    /// <see cref="LinkRevoked"/> exactly once for that stored token.
    /// </summary>
    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _validationGate.WaitAsync(cancellationToken);
        try
        {
            var token = GetToken();
            if (token is null)
            {
                var settingsError = TrySetSettingsConnectionState(isLinked: false);
                PublishState(new GlobeConnectionState(
                    GlobeConnectionStatus.Unlinked,
                    ErrorMessage: settingsError));
                return false;
            }

            var previous = State;
            PublishState(previous.IsLinked
                ? previous with { IsChecking = true, ErrorMessage = string.Empty }
                : new GlobeConnectionState(
                    GlobeConnectionStatus.Validating,
                    previous.Profile,
                    IsChecking: true));

            try
            {
                var profile = await _apiClient.GetStatusAsync(token, cancellationToken);
                var settingsError = TrySetSettingsConnectionState(isLinked: true);
                PublishState(new GlobeConnectionState(
                    GlobeConnectionStatus.Linked,
                    profile,
                    ErrorMessage: settingsError));
                return true;
            }
            catch (GlobeAuthenticationException ex)
            {
                ClearLocalLink(
                    GlobeLinkRevocationSource.StatusCheck,
                    ex.Message,
                    notifyRevoked: true);
                return false;
            }
            catch (OperationCanceledException)
            {
                PublishState(previous with { IsChecking = false });
                throw;
            }
            catch (Exception ex)
            {
                PublishState(previous.IsLinked
                    ? previous with { IsChecking = false, ErrorMessage = ex.Message }
                    : new GlobeConnectionState(
                        GlobeConnectionStatus.Validating,
                        previous.Profile,
                        ErrorMessage: ex.Message));
                return false;
            }
        }
        finally
        {
            _validationGate.Release();
        }
    }

    /// <summary>
    /// Revokes the server token before clearing it locally. Transient server
    /// failures leave the token intact so the user can retry safely.
    /// </summary>
    public async Task UnlinkAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _validationGate.WaitAsync(cancellationToken);
        try
        {
            var token = GetToken();
            if (token is null)
            {
                var localError = ClearLocalLink(
                    GlobeLinkRevocationSource.StatusCheck,
                    string.Empty,
                    notifyRevoked: false);
                ThrowIfLocalClearFailed(localError);
                return;
            }

            var previous = State;
            PublishState(previous with { IsChecking = true, ErrorMessage = string.Empty });
            try
            {
                await _apiClient.RevokeAsync(token, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PublishState(previous with { IsChecking = false });
                throw;
            }
            catch (Exception ex)
            {
                PublishState(previous with { IsChecking = false, ErrorMessage = ex.Message });
                throw;
            }

            var clearError = ClearLocalLink(
                GlobeLinkRevocationSource.StatusCheck,
                string.Empty,
                notifyRevoked: false);
            ThrowIfLocalClearFailed(clearError);
        }
        finally
        {
            _validationGate.Release();
        }
    }

    /// <summary>
    /// Shares one immutable burn request. Retry with the same request instance
    /// so its BurnId remains the same and Globe can deduplicate it.
    /// </summary>
    public async Task<GlobeBurnShareResult> ShareBurnAsync(
        GlobeBurnShareRequest burn,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(burn);
        var token = GetToken();
        if (token is null || !State.IsLinked)
        {
            throw new GlobeNotLinkedException();
        }

        try
        {
            return await _apiClient.ShareBurnAsync(token, burn, cancellationToken);
        }
        catch (GlobeAuthenticationException ex)
        {
            ClearLocalLink(
                GlobeLinkRevocationSource.BurnRequest,
                ex.Message,
                notifyRevoked: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _monitorCancellation?.Cancel();
        try
        {
            _monitorTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _monitorCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
        if (_ownsApiClient)
        {
            _apiClient.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.StatusPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ValidateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Exception? ClearLocalLink(
        GlobeLinkRevocationSource source,
        string message,
        bool notifyRevoked)
    {
        bool hadToken;
        lock (_sync)
        {
            hadToken = _token is not null;
            _token = null;
        }

        Exception? localError = null;
        try
        {
            _credentialStore.Delete(TokenCredentialKey);
        }
        catch (Exception ex)
        {
            localError = ex;
        }

        var settingsError = TrySetSettingsConnectionState(isLinked: false);
        var combinedError = FirstNonEmpty(
            localError?.Message,
            settingsError);
        PublishState(new GlobeConnectionState(
            GlobeConnectionStatus.Unlinked,
            ErrorMessage: combinedError));

        if (notifyRevoked && hadToken)
        {
            RaiseLinkRevoked(new GlobeLinkRevokedEventArgs(
                source,
                string.IsNullOrWhiteSpace(message)
                    ? "Your Globe account is no longer linked."
                    : message));
        }

        return localError ?? (string.IsNullOrWhiteSpace(settingsError)
            ? null
            : new IOException(settingsError));
    }

    private string TrySetSettingsConnectionState(bool isLinked)
    {
        try
        {
            _settingsService.SetGlobeConnectionState(isLinked);
            return string.Empty;
        }
        catch (Exception ex)
        {
            return "Mystral could not update Globe settings: " + ex.Message;
        }
    }

    private string? GetToken()
    {
        lock (_sync)
        {
            return _token;
        }
    }

    private void PublishState(GlobeConnectionState state)
    {
        bool changed;
        lock (_sync)
        {
            changed = _state != state;
            _state = state;
        }

        if (changed)
        {
            var args = new GlobeConnectionStateChangedEventArgs(state);
            foreach (EventHandler<GlobeConnectionStateChangedEventArgs> handler
                     in StateChanged?.GetInvocationList() ?? [])
            {
                try
                {
                    handler(this, args);
                }
                catch
                {
                    // A presentation subscriber must not corrupt token state or
                    // turn a completed server operation into a false failure.
                }
            }
        }
    }

    private void RaiseLinkRevoked(GlobeLinkRevokedEventArgs args)
    {
        foreach (EventHandler<GlobeLinkRevokedEventArgs> handler
                 in LinkRevoked?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
            }
        }
    }

    private static string CreateLinkCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string? NormalizeStoredToken(string? token)
    {
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private static void ValidateOptions(GlobeConnectionOptions options)
    {
        if (options.LinkPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Link poll interval must be positive.");
        }

        if (options.LinkTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Link timeout must be positive.");
        }

        if (options.StatusPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Status poll interval must be positive.");
        }
    }

    private static void ThrowIfLocalClearFailed(Exception? error)
    {
        if (error is not null)
        {
            throw new GlobeApiException(
                "Globe was unlinked, but Mystral could not clear all local connection data.",
                innerException: error);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string FirstNonEmpty(string? first, string? second)
    {
        return !string.IsNullOrWhiteSpace(first)
            ? first
            : second ?? string.Empty;
    }
}
