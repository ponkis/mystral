using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    internal const string ProfileCacheCredentialKey = "globe.profile-cache.v1";
    internal const string LinkAckPendingCredentialKey = "globe.link-ack-pending.v1";
    internal const int MaximumCachedAvatarBytes = 2 * 1024 * 1024;
    internal const string OfflineMessage =
        "globe might be offline. Mystral kept your account and will check again automatically. Sharing is disabled until the connection returns.";

    private readonly GlobeApiClient _apiClient;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly AppSettingsService _settingsService;
    private readonly GlobeConnectionOptions _options;
    private readonly bool _ownsApiClient;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _linkGate = new(1, 1);
    private readonly SemaphoreSlim _validationGate = new(1, 1);
    private readonly SemaphoreSlim _shareGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private GlobeConnectionState _state;
    private string? _token;
    private CachedGlobeProfile? _profileCache;
    private bool _linkAcknowledgementPending;
    private bool _serverUnavailableRaised;
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
            loadError = "Mystral could not read the protected globe token: " + ex.Message;
            _token = null;
        }

        if (_token is not null)
        {
            try
            {
                _profileCache = TryReadProfileCache();
            }
            catch
            {
                // A corrupt optional presentation cache must never sign the
                // user out or prevent authoritative server validation.
                _profileCache = null;
                TryDeleteProfileCache();
            }

            try
            {
                _linkAcknowledgementPending = string.Equals(
                    _credentialStore.Read(LinkAckPendingCredentialKey),
                    "pending",
                    StringComparison.Ordinal);
            }
            catch
            {
                _linkAcknowledgementPending = false;
            }
        }

        _state = _token is null
            ? new GlobeConnectionState(GlobeConnectionStatus.Unlinked, ErrorMessage: loadError)
            : new GlobeConnectionState(
                GlobeConnectionStatus.Validating,
                _profileCache?.Profile,
                IsChecking: true);
        if (_state.IsLinked)
        {
            var settingsError = TrySetSettingsConnectionState(isLinked: true);
            if (!string.IsNullOrWhiteSpace(settingsError))
            {
                _state = _state with { ErrorMessage = settingsError };
            }
        }
    }

    public event EventHandler<GlobeConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised once when a stored token is rejected by Globe. Manual unlinking
    /// does not raise this warning event.
    /// </summary>
    public event EventHandler<GlobeLinkRevokedEventArgs>? LinkRevoked;

    /// <summary>
    /// Raised once per status-check outage. A successful validation resets the
    /// warning so a later, distinct outage can be surfaced.
    /// </summary>
    public event EventHandler<GlobeServerUnavailableEventArgs>? ServerUnavailable;

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
    /// Returns a defensive copy of the last safely decoded avatar for this
    /// exact profile URL. The cache is protected by the same current-user
    /// DPAPI store as the token.
    /// </summary>
    public byte[]? GetCachedAvatar(string avatarUrl)
    {
        lock (_sync)
        {
            return _profileCache is { AvatarBytes.Length: > 0 } cache
                   && string.Equals(
                       cache.Profile.AvatarUrl,
                       avatarUrl,
                       StringComparison.Ordinal)
                ? cache.AvatarBytes.ToArray()
                : null;
        }
    }

    public void CacheAvatar(GlobeProfile profile, byte[] avatarBytes)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(avatarBytes);
        if (avatarBytes.Length is 0 or > MaximumCachedAvatarBytes)
        {
            return;
        }

        CachedGlobeProfile cache;
        lock (_sync)
        {
            if (_state.Profile is not { } current
                || !string.Equals(current.UsernameWithoutAt, profile.UsernameWithoutAt, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.AvatarUrl, profile.AvatarUrl, StringComparison.Ordinal))
            {
                return;
            }

            cache = new CachedGlobeProfile(current, avatarBytes.ToArray());
            _profileCache = cache;
        }

        TryWriteProfileCache(cache);
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
            throw new InvalidOperationException("A globe linking attempt is already in progress.");
        }

        try
        {
            if (HasStoredToken)
            {
                throw new InvalidOperationException(
                    "Unlink your current globe account before linking another one.");
            }

            PublishState(new GlobeConnectionState(GlobeConnectionStatus.Linking));
            var linkCode = CreateLinkCode();
            var codeVerifier = CreateCodeVerifier();
            var codeChallenge = CreateCodeChallenge(codeVerifier);
            var approvalUri = _apiClient.CreateApprovalUri(linkCode);

            // Register a live desktop poll before opening the browser. Globe's
            // approval endpoint deliberately rejects codes with no recent
            // desktop listener, so a fast click cannot race the first poll.
            var initialClaim = await _apiClient.RegisterLinkAsync(
                linkCode,
                codeChallenge,
                cancellationToken);
            if (initialClaim.Status != GlobeLinkClaimStatus.Pending)
            {
                throw new GlobeApiException("globe could not start a new account link.");
            }

            openApprovalPage(approvalUri);

            var deadline = DateTimeOffset.UtcNow + _options.LinkTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var claim = await _apiClient.ClaimLinkAsync(
                    linkCode,
                    codeVerifier,
                    cancellationToken);
                if (claim.Status == GlobeLinkClaimStatus.Claimed)
                {
                    if (claim.Profile is null)
                    {
                        throw new GlobeApiException("globe approved the link but did not return profile information.");
                    }

                    try
                    {
                        _credentialStore.Write(TokenCredentialKey, claim.Token);
                    }
                    catch (Exception ex)
                    {
                        throw new GlobeApiException(
                            "Mystral couldn't safely store the new account link. Please try again.",
                            innerException: ex);
                    }

                    lock (_sync)
                    {
                        _token = claim.Token.Trim();
                        _linkAcknowledgementPending = true;
                    }

                    CacheProvisionalProfile(claim.Profile);
                    try
                    {
                        _credentialStore.Write(LinkAckPendingCredentialKey, "pending");
                    }
                    catch (Exception ex)
                    {
                        MarkAcknowledgementPending(claim.Profile);
                        throw new GlobeUnavailableException(
                            "Mystral couldn't finish linking right now. It will keep trying automatically.",
                            ex);
                    }

                    bool acknowledged;
                    try
                    {
                        acknowledged = await TryAcknowledgeLinkAsync(
                            claim.Token,
                            cancellationToken);
                    }
                    catch (GlobeAuthenticationException ex)
                    {
                        ClearLocalLink(
                            GlobeLinkRevocationSource.StatusCheck,
                            string.Empty,
                            notifyRevoked: false);
                        throw new GlobeApiException(
                            "Mystral couldn't finish linking your account. Please try again.",
                            innerException: ex);
                    }
                    catch (Exception ex) when (!IsServerUnavailable(ex)
                                               && ex is not OperationCanceledException)
                    {
                        ClearLocalLink(
                            GlobeLinkRevocationSource.StatusCheck,
                            string.Empty,
                            notifyRevoked: false);
                        throw new GlobeApiException(
                            "Mystral couldn't finish linking your account. Please try again.",
                            innerException: ex);
                    }

                    if (!acknowledged)
                    {
                        var confirmedProfile = await TryAcknowledgeViaStatusAsync(
                            claim.Token,
                            cancellationToken);
                        if (confirmedProfile is not null)
                        {
                            CompleteLinkAcknowledgement();
                            var confirmedSettingsError = TrySetSettingsConnectionState(isLinked: true);
                            PublishLinkedProfile(confirmedProfile, confirmedSettingsError);
                            return confirmedProfile;
                        }

                        MarkAcknowledgementPending(claim.Profile);
                        throw new GlobeUnavailableException(
                            "Mystral couldn't finish linking right now. It will keep trying automatically.");
                    }

                    CompleteLinkAcknowledgement();

                    var settingsError = TrySetSettingsConnectionState(isLinked: true);
                    PublishLinkedProfile(claim.Profile, settingsError);
                    return claim.Profile;
                }

                if (claim.Status == GlobeLinkClaimStatus.Cancelled)
                {
                    throw new GlobeLinkCancelledException(
                        string.IsNullOrWhiteSpace(claim.Message)
                            ? "The globe account link was canceled."
                            : claim.Message);
                }

                if (claim.Status == GlobeLinkClaimStatus.Expired)
                {
                    throw new GlobeLinkExpiredException(
                        string.IsNullOrWhiteSpace(claim.Message)
                            ? "The globe link request expired. Start again from Mystral."
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

            throw new GlobeLinkExpiredException("The globe link request expired. Start again from Mystral.");
        }
        catch (OperationCanceledException)
        {
            if (HasStoredToken)
            {
                MarkAcknowledgementPending(_profileCache?.Profile);
            }
            else
            {
                PublishState(new GlobeConnectionState(GlobeConnectionStatus.Unlinked));
            }
            throw;
        }
        catch (GlobeAuthenticationException ex)
        {
            if (HasStoredToken)
            {
                ClearLocalLink(
                    GlobeLinkRevocationSource.StatusCheck,
                    string.Empty,
                    notifyRevoked: false);
            }
            else
            {
                PublishState(new GlobeConnectionState(
                    GlobeConnectionStatus.Unlinked,
                    ErrorMessage: ex.Message));
            }

            throw new GlobeApiException(
                "Mystral couldn't finish linking your account. Please try again.",
                innerException: ex);
        }
        catch (GlobeApiException ex) when (HasStoredToken && !IsServerUnavailable(ex))
        {
            ClearLocalLink(
                GlobeLinkRevocationSource.StatusCheck,
                string.Empty,
                notifyRevoked: false);
            throw new GlobeApiException(
                "Mystral couldn't finish linking your account. Please try again.",
                innerException: ex);
        }
        catch (Exception ex)
        {
            if (HasStoredToken)
            {
                if (State.Status == GlobeConnectionStatus.Linking)
                {
                    MarkAcknowledgementPending(_profileCache?.Profile);
                }
            }
            else
            {
                PublishState(new GlobeConnectionState(
                    GlobeConnectionStatus.Unlinked,
                    ErrorMessage: ex.Message));
            }
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
                if (IsLinkAcknowledgementPending())
                {
                    var acknowledged = await TryAcknowledgeLinkAsync(token, cancellationToken);
                    if (!acknowledged)
                    {
                        var confirmedProfile = await TryAcknowledgeViaStatusAsync(
                            token,
                            cancellationToken);
                        if (confirmedProfile is null)
                        {
                            MarkAcknowledgementPending(previous.Profile ?? _profileCache?.Profile);
                            return false;
                        }

                        CompleteLinkAcknowledgement();
                        var confirmedSettingsError = TrySetSettingsConnectionState(isLinked: true);
                        PublishLinkedProfile(confirmedProfile, confirmedSettingsError);
                        return true;
                    }

                    CompleteLinkAcknowledgement();
                }

                var profile = await _apiClient.GetStatusAsync(token, cancellationToken);
                var settingsError = TrySetSettingsConnectionState(isLinked: true);
                PublishLinkedProfile(profile, settingsError);
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
            catch (GlobeApiException ex) when (IsLinkAcknowledgementPending()
                                               && !IsServerUnavailable(ex))
            {
                ClearLocalLink(
                    GlobeLinkRevocationSource.StatusCheck,
                    string.Empty,
                    notifyRevoked: true);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishState(previous with { IsChecking = false });
                throw;
            }
            catch (Exception)
            {
                MarkServerUnavailable(previous.Profile, notifyUser: true);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishState(previous with { IsChecking = false });
                throw;
            }
            catch (Exception ex)
            {
                if (IsServerUnavailable(ex))
                {
                    MarkServerUnavailable(previous.Profile, notifyUser: false);
                    throw new GlobeUnavailableException(OfflineMessage, ex);
                }

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
    /// so its BurnId remains the same and globe can deduplicate it.
    /// </summary>
    public async Task<GlobeBurnShareResult> ShareBurnAsync(
        GlobeBurnShareRequest burn,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(burn);
        await _shareGate.WaitAsync(cancellationToken);
        try
        {
            return await ShareBurnCoreAsync(burn, cancellationToken);
        }
        finally
        {
            _shareGate.Release();
        }
    }

    private async Task<GlobeBurnShareResult> ShareBurnCoreAsync(
        GlobeBurnShareRequest burn,
        CancellationToken cancellationToken)
    {
        var token = GetToken();
        var state = State;
        if (token is null || !state.IsLinked)
        {
            throw new GlobeNotLinkedException();
        }

        if (state.IsOffline)
        {
            _ = await ValidateAsync(cancellationToken);
            token = GetToken();
            state = State;
            if (token is null || !state.IsLinked)
            {
                throw new GlobeNotLinkedException();
            }
        }

        if (!state.CanShare)
        {
            throw new GlobeUnavailableException(OfflineMessage);
        }

        try
        {
            var result = await _apiClient.ShareBurnAsync(token, burn, cancellationToken);
            if (result.Created)
            {
                IncrementCachedBurnCount();
            }
            else
            {
                await TryReconcileProfileAfterDuplicateShareAsync(token, cancellationToken);
            }
            return result;
        }
        catch (GlobeAuthenticationException ex)
        {
            ClearLocalLink(
                GlobeLinkRevocationSource.BurnRequest,
                ex.Message,
                notifyRevoked: true);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsServerUnavailable(ex))
        {
            MarkServerUnavailable(State.Profile, notifyUser: false);
            throw new GlobeUnavailableException(OfflineMessage, ex);
        }
    }

    private async Task TryReconcileProfileAfterDuplicateShareAsync(
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await _apiClient.GetStatusAsync(token, cancellationToken);
            var settingsError = TrySetSettingsConnectionState(isLinked: true);
            PublishLinkedProfile(profile, settingsError);
        }
        catch
        {
            // The burn response is authoritative. A lost first response can
            // make a retry return created:false, so refresh the live count when
            // possible without ever turning that successful retry into failure.
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
            _profileCache = null;
            _linkAcknowledgementPending = false;
            _serverUnavailableRaised = false;
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

        try
        {
            _credentialStore.Delete(ProfileCacheCredentialKey);
        }
        catch (Exception ex)
        {
            localError ??= ex;
        }


        try
        {
            _credentialStore.Delete(LinkAckPendingCredentialKey);
        }
        catch (Exception ex)
        {
            localError ??= ex;
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
                    ? "Your globe account is no longer linked."
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
            return "Mystral could not update globe settings: " + ex.Message;
        }
    }

    private string? GetToken()
    {
        lock (_sync)
        {
            return _token;
        }
    }

    private bool IsLinkAcknowledgementPending()
    {
        lock (_sync)
        {
            return _linkAcknowledgementPending;
        }
    }

    private async Task<bool> TryAcknowledgeLinkAsync(
        string token,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                await _apiClient.AcknowledgeLinkAsync(token, cancellationToken);
                return true;
            }
            catch (GlobeAuthenticationException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsServerUnavailable(ex))
            {
                if (attempt == maximumAttempts)
                {
                    return false;
                }

                await Task.Delay(_options.LinkPollInterval, cancellationToken);
            }
        }

        return false;
    }

    private async Task<GlobeProfile?> TryAcknowledgeViaStatusAsync(
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _apiClient.GetStatusAsync(token, cancellationToken);
        }
        catch (GlobeAuthenticationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsServerUnavailable(ex))
        {
            return null;
        }
    }

    private void CacheProvisionalProfile(GlobeProfile profile)
    {
        CachedGlobeProfile cache;
        lock (_sync)
        {
            var avatarBytes = _profileCache is { } existing
                              && string.Equals(
                                  existing.Profile.AvatarUrl,
                                  profile.AvatarUrl,
                                  StringComparison.Ordinal)
                ? existing.AvatarBytes.ToArray()
                : [];
            cache = new CachedGlobeProfile(profile, avatarBytes);
            _profileCache = cache;
        }

        TryWriteProfileCache(cache);
    }

    private void MarkAcknowledgementPending(GlobeProfile? profile)
    {
        if (profile is not null)
        {
            CacheProvisionalProfile(profile);
        }

        _ = TrySetSettingsConnectionState(isLinked: true);
        PublishState(new GlobeConnectionState(
            GlobeConnectionStatus.Offline,
            profile ?? _profileCache?.Profile,
            ErrorMessage: "Mystral is finishing your globe account link and will keep trying automatically."));
    }

    private void CompleteLinkAcknowledgement()
    {
        lock (_sync)
        {
            _linkAcknowledgementPending = false;
        }

        try
        {
            _credentialStore.Delete(LinkAckPendingCredentialKey);
        }
        catch
        {
            // Ack is idempotent. A stale protected marker only causes a safe
            // repeat acknowledgement on the next launch.
        }
    }

    private void PublishLinkedProfile(GlobeProfile profile, string settingsError)
    {
        CachedGlobeProfile cache;
        lock (_sync)
        {
            var avatarBytes = _profileCache is { } existing
                              && string.Equals(
                                  existing.Profile.AvatarUrl,
                                  profile.AvatarUrl,
                                  StringComparison.Ordinal)
                ? existing.AvatarBytes.ToArray()
                : [];
            cache = new CachedGlobeProfile(profile, avatarBytes);
            _profileCache = cache;
            _serverUnavailableRaised = false;
        }

        TryWriteProfileCache(cache);
        PublishState(new GlobeConnectionState(
            GlobeConnectionStatus.Linked,
            profile,
            ErrorMessage: settingsError));
    }

    private void IncrementCachedBurnCount()
    {
        var current = State;
        if (current.Status != GlobeConnectionStatus.Linked || current.Profile is not { } profile)
        {
            return;
        }

        var updated = profile with { CdCount = profile.CdCount == int.MaxValue ? int.MaxValue : profile.CdCount + 1 };
        CachedGlobeProfile cache;
        lock (_sync)
        {
            var avatarBytes = _profileCache?.AvatarBytes.ToArray() ?? [];
            cache = new CachedGlobeProfile(updated, avatarBytes);
            _profileCache = cache;
        }

        TryWriteProfileCache(cache);
        PublishState(current with { Profile = updated });
    }

    private void MarkServerUnavailable(GlobeProfile? profile, bool notifyUser)
    {
        _ = TrySetSettingsConnectionState(isLinked: true);
        bool raiseUnavailable = false;
        lock (_sync)
        {
            profile ??= _profileCache?.Profile;
            if (notifyUser && !_serverUnavailableRaised)
            {
                _serverUnavailableRaised = true;
                raiseUnavailable = true;
            }
        }

        PublishState(new GlobeConnectionState(
            GlobeConnectionStatus.Offline,
            profile,
            ErrorMessage: OfflineMessage));
        if (raiseUnavailable)
        {
            RaiseServerUnavailable(new GlobeServerUnavailableEventArgs(OfflineMessage));
        }
    }

    private CachedGlobeProfile? TryReadProfileCache()
    {
        var json = _credentialStore.Read(ProfileCacheCredentialKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        if (json.Length > (MaximumCachedAvatarBytes * 2) + 16_384)
        {
            throw new InvalidDataException("The protected globe profile cache is too large.");
        }

        var value = JsonSerializer.Deserialize<CachedGlobeProfileData>(json)
            ?? throw new InvalidDataException("The protected globe profile cache is invalid.");
        var username = value.Username.Trim().TrimStart('@');
        if (username.Length is 0 or > 80
            || value.Name.Length > 160
            || value.AvatarUrl.Length > 2048
            || value.CdCount < 0
            || value.AvatarBytes.Length > MaximumCachedAvatarBytes)
        {
            throw new InvalidDataException("The protected globe profile cache is invalid.");
        }

        return new CachedGlobeProfile(
            new GlobeProfile(username, value.Name, value.AvatarUrl, value.CdCount),
            value.AvatarBytes.ToArray());
    }

    private void TryWriteProfileCache(CachedGlobeProfile cache)
    {
        try
        {
            _credentialStore.Write(
                ProfileCacheCredentialKey,
                JsonSerializer.Serialize(new CachedGlobeProfileData
                {
                    Username = cache.Profile.UsernameWithoutAt,
                    Name = cache.Profile.Name,
                    AvatarUrl = cache.Profile.AvatarUrl,
                    CdCount = cache.Profile.CdCount,
                    AvatarBytes = cache.AvatarBytes
                }));
        }
        catch
        {
            // The token remains authoritative. A presentation cache failure is
            // safe to ignore and can be retried after the next status check.
        }
    }

    private void TryDeleteProfileCache()
    {
        try
        {
            _credentialStore.Delete(ProfileCacheCredentialKey);
        }
        catch
        {
        }
    }

    private static bool IsServerUnavailable(Exception exception)
    {
        return exception is HttpRequestException
            or TimeoutException
            or OperationCanceledException
            or GlobeUnavailableException
            || exception is GlobeApiException apiException
               && (apiException.StatusCode is null
                   || apiException.StatusCode is HttpStatusCode.RequestTimeout
                       or HttpStatusCode.BadGateway
                       or HttpStatusCode.ServiceUnavailable
                       or HttpStatusCode.GatewayTimeout
                   || (int)apiException.StatusCode.Value >= 500);
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

    private void RaiseServerUnavailable(GlobeServerUnavailableEventArgs args)
    {
        foreach (EventHandler<GlobeServerUnavailableEventArgs> handler
                 in ServerUnavailable?.GetInvocationList() ?? [])
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
            return Base64UrlEncode(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Base64UrlEncode(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var verifierBytes = Encoding.ASCII.GetBytes(codeVerifier);
        try
        {
            var hash = SHA256.HashData(verifierBytes);
            try
            {
                return Base64UrlEncode(hash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifierBytes);
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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
                "Mystral couldn't finish unlinking your account. Please try again.",
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

    private sealed record CachedGlobeProfile(GlobeProfile Profile, byte[] AvatarBytes);

    private sealed class CachedGlobeProfileData
    {
        public string Username { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string AvatarUrl { get; init; } = string.Empty;
        public int CdCount { get; init; }
        public byte[] AvatarBytes { get; init; } = [];
    }
}
