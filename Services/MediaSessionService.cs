using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using Mystral.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Mystral.Services;

public sealed class MediaSessionService : IDisposable
{
    private const int MaxArtworkDecodePixelWidth = 1536;
    private const int MaxOnlineCoverArtCacheEntries = 64;
    private static readonly TimeSpan OnlineCoverArtSettleDelay = TimeSpan.FromMilliseconds(600);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _coverArtCacheKey = string.Empty;
    private BitmapImage? _coverArtCache;
    private string _coverArtCacheFingerprint = string.Empty;
    private CancellationTokenSource? _delayedArtworkRefreshCts;
    private readonly object _onlineCoverArtSync = new();
    private readonly Dictionary<string, CoverArtResult> _onlineCoverArtCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _onlineCoverArtCacheOrder = new();
    private string _onlineCoverArtPendingKey = string.Empty;
    private CancellationTokenSource? _onlineCoverArtCts;
    private bool _disposed;

    public event EventHandler<MediaSnapshot>? SnapshotChanged;

    /// <summary>
    /// Looks up the now-playing track's cover online and returns its encoded bytes,
    /// or null when none is found.
    /// </summary>
    public Func<MediaSnapshot, CancellationToken, Task<byte[]?>>? OnlineCoverArtProvider { get; set; }

    /// <summary>
    /// When set, snapshots carry the online cover once it resolves; the media
    /// session thumbnail is used until then and whenever no online cover exists.
    /// </summary>
    public bool PreferOnlineCoverArt { get; set; }

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += Manager_SessionsChanged;
        AttachSession(_manager.GetCurrentSession());
        await RefreshAsync();
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        await session.TryTogglePlayPauseAsync();
        await RefreshAsync();
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        await session.TrySkipNextAsync();
        await RefreshAsync();
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        await session.TrySkipPreviousAsync();
        await RefreshAsync();
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        var accepted = await session.TryChangePlaybackPositionAsync(position.Ticks);
        await RefreshAsync();
        return accepted;
    }

    private async void Manager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        InvalidateCoverArtCache();
        AttachSession(sender.GetCurrentSession());
        await RefreshAsync();
    }

    private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        InvalidateCoverArtCache();
        await RefreshAsync();
        ScheduleDelayedArtworkRefresh();
    }

    private async void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        await RefreshAsync();
    }

    private async void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        await RefreshAsync();
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }

        _session = session;
        InvalidateCoverArtCache();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }
    }

    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            if (_manager is not null)
            {
                AttachSession(_manager.GetCurrentSession());
            }

            var snapshot = _session is null
                ? MediaSnapshot.Empty
                : await CreateSnapshotAsync(_session);

            SnapshotChanged?.Invoke(this, snapshot);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<MediaSnapshot> CreateSnapshotAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var properties = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var timelineObservedAt = DateTimeOffset.Now;
        var controls = playback.Controls;
        var status = playback.PlaybackStatus;
        var isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var app = Clean(session.SourceAppUserModelId, "Media app");
        var title = Clean(properties.Title, "Unknown track");
        var artist = AppleMusicMediaMetadata.ResolveArtist(
            properties.Artist,
            properties.AlbumArtist,
            app);
        var album = Clean(properties.AlbumTitle, string.Empty);
        var description = BuildDescription(artist, album, app);
        var duration = timeline.EndTime > timeline.StartTime
            ? timeline.EndTime - timeline.StartTime
            : TimeSpan.Zero;
        var coverArtKey = $"{app}|{title}|{artist}|{album}";
        var coverArt = await GetCoverArtAsync(properties.Thumbnail, coverArtKey);
        var timelineUpdatedAt = PlaybackTimelineStabilizer.ResolveSourceAnchorTimestamp(
            timeline.LastUpdatedTime,
            timelineObservedAt,
            duration,
            out var hasReliableTimelineUpdatedAt);
        var position = PlaybackTimelineStabilizer.ProjectSourcePosition(
            timeline.Position - timeline.StartTime,
            duration,
            isPlaying: false,
            sourceUpdatedAt: timelineUpdatedAt,
            observedAt: timelineObservedAt);

        var snapshot = new MediaSnapshot(
            HasSession: true,
            Title: title,
            Artist: artist,
            Album: album,
            SourceApp: app,
            Description: description,
            StatusText: isPlaying ? "Playing" : StatusToText(status),
            Position: position,
            Duration: duration,
            IsPlaying: isPlaying,
            CanPlay: controls.IsPlayEnabled,
            CanPause: controls.IsPauseEnabled,
            CanNext: controls.IsNextEnabled,
            CanPrevious: controls.IsPreviousEnabled,
            CanSeek: controls.IsPlaybackPositionEnabled,
            CoverArt: coverArt.Image)
        {
            TimelineUpdatedAt = timelineUpdatedAt,
            HasReliableTimelineUpdatedAt = hasReliableTimelineUpdatedAt,
            CoverArtFingerprint = coverArt.Fingerprint
        };

        return ApplyOnlineCoverArt(snapshot);
    }

    private MediaSnapshot ApplyOnlineCoverArt(MediaSnapshot snapshot)
    {
        if (!PreferOnlineCoverArt || OnlineCoverArtProvider is null)
        {
            return snapshot;
        }

        var lookup = AppleMusicMediaMetadata.NormalizeLyricsLookup(snapshot);
        var key = CreateOnlineCoverArtKey(lookup);
        if (key.Length == 0)
        {
            return snapshot;
        }

        lock (_onlineCoverArtSync)
        {
            if (_onlineCoverArtCache.TryGetValue(key, out var cached))
            {
                return cached.Image is null
                    ? snapshot
                    : snapshot with
                    {
                        CoverArt = cached.Image,
                        CoverArtFingerprint = cached.Fingerprint
                    };
            }

            if (key != _onlineCoverArtPendingKey)
            {
                // Track changes publish metadata in pieces; the settle delay lets
                // partial keys be superseded before they reach MusicBrainz.
                _onlineCoverArtCts?.Cancel();
                _onlineCoverArtCts?.Dispose();
                _onlineCoverArtCts = new CancellationTokenSource();
                _onlineCoverArtPendingKey = key;
                _ = FetchOnlineCoverArtAsync(key, lookup, _onlineCoverArtCts.Token);
            }
        }

        return snapshot;
    }

    private static string CreateOnlineCoverArtKey(MediaSnapshot snapshot)
    {
        if (!snapshot.HasSession
            || string.IsNullOrWhiteSpace(snapshot.Artist)
            || string.IsNullOrWhiteSpace(snapshot.Title)
            || snapshot.Title == "Unknown track")
        {
            return string.Empty;
        }

        return $"{snapshot.Title.Trim()}|{snapshot.Artist.Trim()}|{snapshot.Album.Trim()}"
            .ToUpperInvariant();
    }

    private async Task FetchOnlineCoverArtAsync(
        string key,
        MediaSnapshot lookup,
        CancellationToken cancellationToken)
    {
        var provider = OnlineCoverArtProvider;
        if (provider is null)
        {
            return;
        }

        CoverArtResult result;
        try
        {
            await Task.Delay(OnlineCoverArtSettleDelay, cancellationToken);
            var bytes = await provider(lookup, cancellationToken);
            result = bytes is { Length: > 0 } ? DecodeCoverArt(bytes) : CoverArtResult.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Online art is optional; remember the miss so polling does not retry
            // the lookup, and keep showing the media session thumbnail.
            result = CoverArtResult.Empty;
        }

        lock (_onlineCoverArtSync)
        {
            if (_onlineCoverArtCache.TryAdd(key, result))
            {
                _onlineCoverArtCacheOrder.Enqueue(key);
                while (_onlineCoverArtCacheOrder.Count > MaxOnlineCoverArtCacheEntries)
                {
                    _onlineCoverArtCache.Remove(_onlineCoverArtCacheOrder.Dequeue());
                }
            }

            if (_onlineCoverArtPendingKey == key)
            {
                _onlineCoverArtPendingKey = string.Empty;
            }
        }

        if (result.Image is not null && !cancellationToken.IsCancellationRequested)
        {
            await RefreshAsync();
        }
    }

    private async Task<CoverArtResult> GetCoverArtAsync(
        IRandomAccessStreamReference? thumbnail,
        string cacheKey)
    {
        if (thumbnail is null)
        {
            _coverArtCacheKey = cacheKey;
            _coverArtCache = null;
            _coverArtCacheFingerprint = string.Empty;
            return CoverArtResult.Empty;
        }

        if (cacheKey == _coverArtCacheKey)
        {
            return new CoverArtResult(_coverArtCache, _coverArtCacheFingerprint);
        }

        _coverArtCacheKey = cacheKey;
        var result = await LoadCoverArtAsync(thumbnail);
        _coverArtCache = result.Image;
        _coverArtCacheFingerprint = result.Fingerprint;
        return result;
    }

    private void InvalidateCoverArtCache()
    {
        _coverArtCacheKey = string.Empty;
        _coverArtCache = null;
        _coverArtCacheFingerprint = string.Empty;
    }

    private void ScheduleDelayedArtworkRefresh()
    {
        _delayedArtworkRefreshCts?.Cancel();
        _delayedArtworkRefreshCts?.Dispose();
        _delayedArtworkRefreshCts = new CancellationTokenSource();
        _ = RefreshArtworkAfterMediaSettlesAsync(_delayedArtworkRefreshCts.Token);
    }

    private async Task RefreshArtworkAfterMediaSettlesAsync(CancellationToken cancellationToken)
    {
        foreach (var delay in new[] { 350, 1000 })
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            InvalidateCoverArtCache();
            await RefreshAsync();
        }
    }

    private static string BuildDescription(string artist, string album, string app)
    {
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album))
        {
            return $"{artist} - {album}";
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            return artist;
        }

        if (!string.IsNullOrWhiteSpace(album))
        {
            return album;
        }

        return app;
    }

    private static string StatusToText(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Paused",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "Stopped",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => "Closed",
            _ => "Ready"
        };
    }

    private static string Clean(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static async Task<CoverArtResult> LoadCoverArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return CoverArtResult.Empty;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > int.MaxValue)
            {
                return CoverArtResult.Empty;
            }

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return DecodeCoverArt(bytes);
        }
        catch
        {
            return CoverArtResult.Empty;
        }
    }

    private static CoverArtResult DecodeCoverArt(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = MaxArtworkDecodePixelWidth;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return new CoverArtResult(
                bitmap,
                Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch
        {
            return CoverArtResult.Empty;
        }
    }

    private readonly record struct CoverArtResult(BitmapImage? Image, string Fingerprint)
    {
        public static CoverArtResult Empty { get; } = new(null, string.Empty);
    }

    public void Dispose()
    {
        _disposed = true;

        if (_manager is not null)
        {
            _manager.SessionsChanged -= Manager_SessionsChanged;
        }

        AttachSession(null);
        _delayedArtworkRefreshCts?.Cancel();
        _delayedArtworkRefreshCts?.Dispose();
        lock (_onlineCoverArtSync)
        {
            _onlineCoverArtCts?.Cancel();
            _onlineCoverArtCts?.Dispose();
            _onlineCoverArtCts = null;
        }

        _refreshLock.Dispose();
    }
}
