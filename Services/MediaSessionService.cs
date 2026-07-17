using System.IO;
using System.Windows.Media.Imaging;
using Mystral.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Mystral.Services;

public sealed class MediaSessionService : IDisposable
{
    private const int MaxArtworkDecodePixelWidth = 1536;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _coverArtCacheKey = string.Empty;
    private BitmapImage? _coverArtCache;
    private CancellationTokenSource? _delayedArtworkRefreshCts;
    private bool _disposed;

    public event EventHandler<MediaSnapshot>? SnapshotChanged;

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

        var title = Clean(properties.Title, "Unknown track");
        var artist = Clean(properties.Artist, string.Empty);
        var album = Clean(properties.AlbumTitle, string.Empty);
        var app = Clean(session.SourceAppUserModelId, "Media app");
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

        return new MediaSnapshot(
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
            CoverArt: coverArt)
        {
            TimelineUpdatedAt = timelineUpdatedAt,
            HasReliableTimelineUpdatedAt = hasReliableTimelineUpdatedAt
        };
    }

    private async Task<BitmapImage?> GetCoverArtAsync(IRandomAccessStreamReference? thumbnail, string cacheKey)
    {
        if (thumbnail is null)
        {
            _coverArtCacheKey = cacheKey;
            _coverArtCache = null;
            return null;
        }

        if (cacheKey == _coverArtCacheKey)
        {
            return _coverArtCache;
        }

        _coverArtCacheKey = cacheKey;
        _coverArtCache = await LoadCoverArtAsync(thumbnail);
        return _coverArtCache;
    }

    private void InvalidateCoverArtCache()
    {
        _coverArtCacheKey = string.Empty;
        _coverArtCache = null;
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

    private static async Task<BitmapImage?> LoadCoverArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > int.MaxValue)
            {
                return null;
            }

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);

            using var memory = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = MaxArtworkDecodePixelWidth;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
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
        _refreshLock.Dispose();
    }
}
