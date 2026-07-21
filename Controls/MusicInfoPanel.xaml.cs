using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Mystral.Models;
using Mystral.Services;
using static Mystral.Services.ArtworkTint;

namespace Mystral.Controls;

internal enum MusicInfoPage
{
    Track,
    Artist,
    Album
}

internal readonly record struct MusicInfoLookupFailure(
    string Message,
    bool CanRetry,
    bool IsTransient);

internal readonly record struct CachedMusicInfoLookupFailure(
    MusicInfoLookupFailure Failure,
    DateTimeOffset RetryNotBefore);

internal sealed record ArtistHeroPhoto(
    BitmapSource Artwork,
    string SourcePageUrl,
    string Attribution,
    string LicenseName);

public partial class MusicInfoPanel : UserControl, IDisposable
{
    private static readonly Color DefaultTint = Color.FromRgb(74, 82, 88);
    private static readonly ImageSource CurrentAlbumTrackHighlightSource =
        IconImageSource.LoadSiteImage("Resources/Images/cd_thing.png");
    private static readonly TimeSpan LookupRetryCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ArtistPhotoRetryCooldown = TimeSpan.FromMinutes(2);
    private const int MaxArtistPhotoCacheEntries = 24;
    private const long MaxArtistPhotoCachePixels = 16_000_000;
    private readonly ImageArtworkLoader _artworkLoader = new();
    private readonly Dictionary<string, MusicBrainzArtistInfo> _artistCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArtistHeroPhoto> _artistPhotoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _artistPhotoCacheOrder = new();
    private readonly Dictionary<string, DateTimeOffset> _artistPhotoRetryNotBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedMusicInfoLookupFailure> _artistLookupErrors = new(StringComparer.OrdinalIgnoreCase);
    private MusicBrainzService? _musicBrainzService;
    private ArtistArtworkService? _artistArtworkService;
    private CancellationTokenSource? _trackLookupCts;
    private CancellationTokenSource? _artistLookupCts;
    private CancellationTokenSource? _albumLookupCts;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private MusicBrainzTrackInfo? _trackInfo;
    private MusicBrainzAlbumInfo? _albumInfo;
    private string _snapshotKey = string.Empty;
    private string _trackLookupInFlightKey = string.Empty;
    private string _trackLookupCompletedKey = string.Empty;
    private MusicInfoLookupFailure? _trackLookupFailure;
    private DateTimeOffset _trackRetryNotBefore = DateTimeOffset.MinValue;
    private string _artistLookupInFlightId = string.Empty;
    private string _albumLookupInFlightId = string.Empty;
    private CachedMusicInfoLookupFailure? _albumLookupFailure;
    private int _trackLookupGeneration;
    private int _artistLookupGeneration;
    private int _albumLookupGeneration;
    private int _animationGeneration;
    private long _artistPhotoCachedPixels;
    private bool _isLoaded;
    private bool _isActive;
    private bool _isHeroTransitioning;
    private bool _hasSnapshot;
    private bool _isUpdatingArtistSelector;

    public MusicInfoPanel()
    {
        InitializeComponent();
    }

    internal Image AnimatedArtworkOverlay => HeroArtVideoImage;

    internal Image FrozenArtworkOverlay => HeroArtFrozenFrameImage;

    internal Visual ShellMaterialVisual => InfoShellSurface;

    internal Brush ShellBorderBrush => InfoShell.BorderBrush;

    internal event Action<Color>? TintChanged;

    internal void Initialize(
        MusicBrainzService musicBrainzService,
        ArtistArtworkService artistArtworkService)
    {
        ArgumentNullException.ThrowIfNull(musicBrainzService);
        ArgumentNullException.ThrowIfNull(artistArtworkService);
        if (_musicBrainzService is not null && !ReferenceEquals(_musicBrainzService, musicBrainzService))
        {
            throw new InvalidOperationException("The music information panel is already initialized.");
        }

        if (_artistArtworkService is not null && !ReferenceEquals(_artistArtworkService, artistArtworkService))
        {
            throw new InvalidOperationException("The music information panel is already initialized.");
        }

        _musicBrainzService = musicBrainzService;
        _artistArtworkService = artistArtworkService;
    }

    internal void Activate(MediaSnapshot snapshot, MusicInfoPage page, Rect artworkOrigin)
    {
        if (_musicBrainzService is null)
        {
            throw new InvalidOperationException("The music information panel has not been initialized.");
        }

        _isActive = false;
        _isHeroTransitioning = true;
        Visibility = Visibility.Visible;
        ShowTrack(snapshot, page);
        _isActive = true;
        ShowArtworkHero(snapshot.CoverArt);
        UpdateLayout();
        PlayEntranceAnimation(artworkOrigin);
        if (_isLoaded)
        {
            _ = _trackInfo is null ? LoadTrackAsync() : EnsureSelectedPageLoadedAsync();
        }
    }

    internal void ShowTrack(MediaSnapshot snapshot, MusicInfoPage? page = null)
    {
        snapshot = AppleMusicMediaMetadata.NormalizeLyricsLookup(snapshot);
        var key = CreateSnapshotKey(snapshot);
        var isSameTrack = _hasSnapshot
            && string.Equals(key, _snapshotKey, StringComparison.Ordinal);
        _hasSnapshot = true;
        _snapshot = snapshot;

        if (page is { } requestedPage)
        {
            SelectPage(requestedPage);
        }

        if (isSameTrack)
        {
            UpdateHeroForSelectedPage();
            if (_isActive && _isLoaded)
            {
                _ = _trackInfo is null ? LoadTrackAsync() : EnsureSelectedPageLoadedAsync();
            }
            return;
        }

        _snapshotKey = key;
        CancelLookups();
        ResetDetails();
        UpdateHeaderForSelectedPage();
        UpdateHeroForSelectedPage();

        if (!_isActive || !_isLoaded)
        {
            return;
        }

        _ = LoadTrackAsync();
    }

    internal void Deactivate()
    {
        _isActive = false;
        _isHeroTransitioning = false;
        ResetHeroTransform();
        CancelLookups();
        _trackLookupInFlightKey = string.Empty;
        _artistLookupInFlightId = string.Empty;
        _albumLookupInFlightId = string.Empty;
    }

    internal void PlayExitAnimation(Rect artworkTarget, BitmapSource? compactArtwork, Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var generation = ++_animationGeneration;
        _isHeroTransitioning = true;
        ShowArtworkHero(compactArtwork);
        UpdateLayout();
        var heroPosition = new Point(
            Canvas.GetLeft(HeroArtworkFrame),
            Canvas.GetTop(HeroArtworkFrame));
        var targetScale = HeroArtworkFrame.ActualWidth > 0
            ? Math.Clamp(artworkTarget.Width / HeroArtworkFrame.ActualWidth, 0.05, 1)
            : 1;
        var duration = TimeSpan.FromMilliseconds(235);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        InfoShell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(175))
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        InfoShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        InfoShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(targetScale, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(artworkTarget.X - heroPosition.X, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        var translateY = new DoubleAnimation(artworkTarget.Y - heroPosition.Y, duration)
        {
            EasingFunction = easing
        };
        translateY.Completed += (_, _) =>
        {
            if (generation == _animationGeneration)
            {
                completed();
            }
        };
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.YProperty, translateY, HandoffBehavior.SnapshotAndReplace);
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (_isActive)
        {
            _ = _trackInfo is null ? LoadTrackAsync() : EnsureSelectedPageLoadedAsync();
        }
    }

    private void PlayEntranceAnimation(Rect artworkOrigin)
    {
        _animationGeneration++;
        ResetHeroTransform();
        UpdateLayout();
        var heroPosition = HeroArtworkFrame.TranslatePoint(new Point(), this);
        var startScale = HeroArtworkFrame.ActualWidth > 0
            ? Math.Clamp(artworkOrigin.Width / HeroArtworkFrame.ActualWidth, 0.05, 1)
            : 1;
        var duration = TimeSpan.FromMilliseconds(255);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        InfoShell.BeginAnimation(OpacityProperty, null);
        InfoShell.Opacity = 0;
        InfoShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        InfoShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        InfoShellScale.ScaleX = 0.985;
        InfoShellScale.ScaleY = 0.985;
        HeroArtworkFrame.BeginAnimation(OpacityProperty, null);
        HeroArtworkFrame.Opacity = 1;
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HeroArtworkScale.ScaleX = startScale;
        HeroArtworkScale.ScaleY = startScale;
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        HeroArtworkTranslate.X = artworkOrigin.X - heroPosition.X;
        HeroArtworkTranslate.Y = artworkOrigin.Y - heroPosition.Y;

        InfoShell.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(210))
        {
            BeginTime = TimeSpan.FromMilliseconds(25),
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        InfoShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        InfoShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        var generation = _animationGeneration;
        var translateY = new DoubleAnimation(0, duration)
        {
            EasingFunction = easing
        };
        translateY.Completed += (_, _) =>
        {
            if (generation == _animationGeneration && _isActive)
            {
                ResetHeroTransform();
                _isHeroTransitioning = false;
                UpdateHeroForSelectedPage();
            }
        };
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.YProperty, translateY, HandoffBehavior.SnapshotAndReplace);
    }

    private async Task LoadTrackAsync(bool force = false)
    {
        var musicBrainzService = _musicBrainzService;
        if (!_isActive || musicBrainzService is null)
        {
            return;
        }

        var snapshot = _snapshot;
        if (!snapshot.HasSession || string.IsNullOrWhiteSpace(snapshot.Title))
        {
            SetTrackStatus("Play a track to see its information.", isLoading: false, canRetry: false);
            SetArtistStatus("Play a track to see artist information.", isLoading: false, canRetry: false);
            SetAlbumStatus("Play a track to see album information.", isLoading: false, canRetry: false);
            return;
        }

        var key = _snapshotKey;
        if (!force && string.Equals(_trackLookupCompletedKey, key, StringComparison.Ordinal))
        {
            return;
        }

        if (!force
            && _trackLookupFailure is { IsTransient: true }
            && DateTimeOffset.UtcNow < _trackRetryNotBefore)
        {
            return;
        }

        if (!force
            && string.Equals(_trackLookupInFlightKey, key, StringComparison.Ordinal)
            && _trackLookupCts is { IsCancellationRequested: false })
        {
            return;
        }

        _trackLookupCts?.Cancel();
        _trackLookupCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _trackLookupCts = cancellation;
        var generation = ++_trackLookupGeneration;
        _trackLookupCompletedKey = string.Empty;
        _trackLookupFailure = null;
        _trackRetryNotBefore = DateTimeOffset.MinValue;
        _trackLookupInFlightKey = key;

        SetTrackStatus("Finding track information...", isLoading: true, canRetry: false);
        SetArtistStatus(
            "Finding the matching artist...",
            isLoading: true,
            canRetry: false);
        SetAlbumStatus(
            "Finding the matching album...",
            isLoading: true,
            canRetry: false);
        try
        {
            var info = await musicBrainzService.FetchTrackInfoAsync(
                snapshot.Title,
                snapshot.Artist,
                snapshot.Album,
                snapshot.Duration,
                cancellation.Token);
            if (!IsCurrentTrackLookup(cancellation, generation, key))
            {
                return;
            }

            if (info is null)
            {
                _trackLookupCompletedKey = key;
                SetTrackStatus(
                    "No music information was found for this track.",
                    isLoading: false,
                    canRetry: true);
                SetArtistStatus("No artist information is available for this track.", isLoading: false, canRetry: false);
                SetAlbumStatus("No album information is available for this track.", isLoading: false, canRetry: false);
                return;
            }

            _trackInfo = info;
            _trackLookupCompletedKey = key;
            PopulateTrackDetails(info);
            SetTrackReady();
            ConfigureArtistSelector(info.ArtistCredits);
            UpdateHeaderForSelectedPage();
            SetArtistStatus("Loading artist information...", isLoading: false, canRetry: false);
            SetAlbumStatus("Loading album information...", isLoading: false, canRetry: false);
            await EnsureSelectedPageLoadedAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentTrackLookup(cancellation, generation, key))
            {
                return;
            }

            var failure = ClassifyLookupFailure(ex, isEntityLookup: false);
            _trackLookupFailure = failure;
            if (failure.IsTransient)
            {
                _trackLookupCompletedKey = string.Empty;
                _trackRetryNotBefore = DateTimeOffset.UtcNow + LookupRetryCooldown;
            }
            else
            {
                _trackLookupCompletedKey = key;
            }

            SetTrackStatus(failure.Message, isLoading: false, canRetry: failure.CanRetry);
            SetArtistStatus("Artist information could not be loaded.", isLoading: false, canRetry: failure.CanRetry);
            SetAlbumStatus("Album information could not be loaded.", isLoading: false, canRetry: failure.CanRetry);
        }
        finally
        {
            if (ReferenceEquals(_trackLookupCts, cancellation))
            {
                _trackLookupInFlightKey = string.Empty;
            }
        }
    }

    private async Task EnsureSelectedPageLoadedAsync()
    {
        if (_trackInfo is null)
        {
            return;
        }

        if (InfoTabs.SelectedItem == ArtistTab)
        {
            await LoadSelectedArtistAsync();
        }
        else if (InfoTabs.SelectedItem == AlbumTab)
        {
            await LoadAlbumAsync();
        }

    }

    private async Task LoadSelectedArtistAsync(bool force = false)
    {
        var musicBrainzService = _musicBrainzService;
        if (!_isActive || musicBrainzService is null || _trackInfo is null)
        {
            return;
        }

        var credit = ArtistSelector.SelectedItem as MusicBrainzArtistCredit
            ?? _trackInfo.ArtistCredits.FirstOrDefault();
        if (credit is null || string.IsNullOrWhiteSpace(credit.ArtistId))
        {
            SetArtistStatus(
                "Artist details are not available for this recording.",
                isLoading: false,
                canRetry: false);
            return;
        }

        if (!force && _artistCache.TryGetValue(credit.ArtistId, out var cached))
        {
            PopulateArtistDetails(cached, _trackInfo.Artist);
            SetArtistReady();
            UpdateHeaderForSelectedPage();
            UpdateHeroForSelectedPage();
            await LoadArtistPhotoForCachedInfoAsync(cached);
            return;
        }

        if (!force
            && _artistLookupErrors.TryGetValue(credit.ArtistId, out var previousError))
        {
            if (!IsLookupRetryDue(previousError))
            {
                SetArtistStatus(
                    previousError.Failure.Message,
                    isLoading: false,
                    canRetry: previousError.Failure.CanRetry);
                return;
            }

            _artistLookupErrors.Remove(credit.ArtistId);
        }

        if (!force
            && string.Equals(_artistLookupInFlightId, credit.ArtistId, StringComparison.OrdinalIgnoreCase)
            && _artistLookupCts is { IsCancellationRequested: false })
        {
            return;
        }

        _artistLookupCts?.Cancel();
        _artistLookupCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _artistLookupCts = cancellation;
        var generation = ++_artistLookupGeneration;
        var trackGeneration = _trackLookupGeneration;
        var artistId = credit.ArtistId;
        _artistLookupErrors.Remove(artistId);
        _artistLookupInFlightId = artistId;

        SetArtistStatus("Loading artist information...", isLoading: true, canRetry: false);
        try
        {
            var info = await musicBrainzService.FetchArtistInfoAsync(artistId, cancellation.Token);
            if (!IsCurrentArtistLookup(cancellation, generation, trackGeneration, artistId))
            {
                return;
            }

            _artistCache[artistId] = info;
            PopulateArtistDetails(info, _trackInfo.Artist);
            SetArtistReady();
            UpdateHeaderForSelectedPage();
            UpdateHeroForSelectedPage();
            await TryLoadArtistPhotoAsync(
                info,
                cancellation,
                generation,
                trackGeneration,
                artistId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentArtistLookup(cancellation, generation, trackGeneration, artistId))
            {
                return;
            }

            var failure = ClassifyLookupFailure(ex);
            _artistLookupErrors[artistId] = CacheLookupFailure(failure);
            SetArtistStatus(failure.Message, isLoading: false, canRetry: failure.CanRetry);
        }
        finally
        {
            if (ReferenceEquals(_artistLookupCts, cancellation))
            {
                _artistLookupInFlightId = string.Empty;
            }
        }
    }

    private async Task LoadArtistPhotoForCachedInfoAsync(MusicBrainzArtistInfo info)
    {
        if (_artistArtworkService is null
            || string.IsNullOrWhiteSpace(info.ImagePageUrl)
            || !IsArtistPhotoRetryDue(info.ArtistId)
            || (string.Equals(_artistLookupInFlightId, info.ArtistId, StringComparison.OrdinalIgnoreCase)
                && _artistLookupCts is { IsCancellationRequested: false }))
        {
            return;
        }

        _artistLookupCts?.Cancel();
        _artistLookupCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _artistLookupCts = cancellation;
        var generation = ++_artistLookupGeneration;
        var trackGeneration = _trackLookupGeneration;
        _artistLookupInFlightId = info.ArtistId;

        try
        {
            await TryLoadArtistPhotoAsync(
                info,
                cancellation,
                generation,
                trackGeneration,
                info.ArtistId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_artistLookupCts, cancellation))
            {
                _artistLookupInFlightId = string.Empty;
            }
        }
    }

    private async Task TryLoadArtistPhotoAsync(
        MusicBrainzArtistInfo info,
        CancellationTokenSource cancellation,
        int generation,
        int trackGeneration,
        string artistId)
    {
        var artistArtworkService = _artistArtworkService;
        if (artistArtworkService is null
            || string.IsNullOrWhiteSpace(info.ImagePageUrl)
            || !IsArtistPhotoRetryDue(artistId))
        {
            return;
        }

        try
        {
            var details = await artistArtworkService.FetchAsync(
                artistId,
                info.ImagePageUrl,
                cancellation.Token);
            if (!IsCurrentArtistLookup(cancellation, generation, trackGeneration, artistId))
            {
                return;
            }

            if (details is null)
            {
                DeferArtistPhotoRetry(artistId);
                return;
            }

            var artwork = await _artworkLoader.LoadAsync(details.Data, cancellation.Token);
            if (!IsCurrentArtistLookup(cancellation, generation, trackGeneration, artistId))
            {
                return;
            }

            CacheArtistPhoto(
                artistId,
                new ArtistHeroPhoto(
                    artwork.Preview,
                    details.SourcePageUrl,
                    details.Attribution,
                    details.LicenseName));
            _artistPhotoRetryNotBefore.Remove(artistId);
            UpdateHeroForSelectedPage();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or IOException
                                   or InvalidDataException
                                   or NotSupportedException
                                   or ObjectDisposedException)
        {
            // Artist details remain useful even when the optional photo cannot be shown.
            if (IsCurrentArtistLookup(cancellation, generation, trackGeneration, artistId))
            {
                DeferArtistPhotoRetry(artistId);
            }
        }
    }

    private bool IsArtistPhotoRetryDue(string artistId)
    {
        if (_artistPhotoCache.ContainsKey(artistId))
        {
            return false;
        }

        if (!_artistPhotoRetryNotBefore.TryGetValue(artistId, out var retryNotBefore))
        {
            return true;
        }

        if (DateTimeOffset.UtcNow < retryNotBefore)
        {
            return false;
        }

        _artistPhotoRetryNotBefore.Remove(artistId);
        return true;
    }

    private void DeferArtistPhotoRetry(string artistId)
    {
        _artistPhotoRetryNotBefore[artistId] = DateTimeOffset.UtcNow + ArtistPhotoRetryCooldown;
    }

    private void CacheArtistPhoto(string artistId, ArtistHeroPhoto photo)
    {
        if (_artistPhotoCache.TryGetValue(artistId, out var previous))
        {
            _artistPhotoCachedPixels -= GetArtworkPixels(previous.Artwork);
        }
        else
        {
            _artistPhotoCacheOrder.Enqueue(artistId);
        }

        _artistPhotoCache[artistId] = photo;
        _artistPhotoCachedPixels += GetArtworkPixels(photo.Artwork);
        while ((_artistPhotoCache.Count > MaxArtistPhotoCacheEntries
                || _artistPhotoCachedPixels > MaxArtistPhotoCachePixels)
               && _artistPhotoCacheOrder.TryDequeue(out var oldestArtistId))
        {
            if (_artistPhotoCache.Remove(oldestArtistId, out var evicted))
            {
                _artistPhotoCachedPixels -= GetArtworkPixels(evicted.Artwork);
            }
        }
    }

    private static long GetArtworkPixels(BitmapSource artwork)
    {
        return (long)artwork.PixelWidth * artwork.PixelHeight;
    }

    private async Task LoadAlbumAsync(bool force = false)
    {
        var musicBrainzService = _musicBrainzService;
        if (!_isActive || musicBrainzService is null || _trackInfo is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_trackInfo.ReleaseId))
        {
            SetAlbumStatus(
                "Album details are not available for this recording.",
                isLoading: false,
                canRetry: false);
            return;
        }

        if (!force && _albumInfo is not null)
        {
            PopulateAlbumDetails(_albumInfo);
            SetAlbumReady();
            return;
        }

        if (!force && _albumLookupFailure is { } previousFailure)
        {
            if (!IsLookupRetryDue(previousFailure))
            {
                SetAlbumStatus(
                    previousFailure.Failure.Message,
                    isLoading: false,
                    canRetry: previousFailure.Failure.CanRetry);
                return;
            }

            _albumLookupFailure = null;
        }

        if (!force
            && string.Equals(_albumLookupInFlightId, _trackInfo.ReleaseId, StringComparison.OrdinalIgnoreCase)
            && _albumLookupCts is { IsCancellationRequested: false })
        {
            return;
        }

        _albumLookupCts?.Cancel();
        _albumLookupCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _albumLookupCts = cancellation;
        var generation = ++_albumLookupGeneration;
        var trackGeneration = _trackLookupGeneration;
        var releaseId = _trackInfo.ReleaseId;
        var recordingId = _trackInfo.RecordingId;
        _albumLookupFailure = null;
        _albumLookupInFlightId = releaseId;

        SetAlbumStatus("Loading album information...", isLoading: true, canRetry: false);
        try
        {
            var info = await musicBrainzService.FetchAlbumInfoAsync(
                releaseId,
                recordingId,
                includeArtwork: false,
                cancellationToken: cancellation.Token);
            if (!IsCurrentAlbumLookup(cancellation, generation, trackGeneration, releaseId))
            {
                return;
            }

            _albumInfo = info;
            PopulateAlbumDetails(info);
            SetAlbumReady();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentAlbumLookup(cancellation, generation, trackGeneration, releaseId))
            {
                return;
            }

            var failure = ClassifyLookupFailure(ex);
            _albumLookupFailure = CacheLookupFailure(failure);
            SetAlbumStatus(failure.Message, isLoading: false, canRetry: failure.CanRetry);
        }
        finally
        {
            if (ReferenceEquals(_albumLookupCts, cancellation))
            {
                _albumLookupInFlightId = string.Empty;
            }
        }
    }

    private bool IsCurrentTrackLookup(
        CancellationTokenSource cancellation,
        int generation,
        string key)
    {
        return _isActive
            && !cancellation.IsCancellationRequested
            && ReferenceEquals(_trackLookupCts, cancellation)
            && generation == _trackLookupGeneration
            && string.Equals(key, _snapshotKey, StringComparison.Ordinal);
    }

    private bool IsCurrentArtistLookup(
        CancellationTokenSource cancellation,
        int generation,
        int trackGeneration,
        string artistId)
    {
        var selectedId = (ArtistSelector.SelectedItem as MusicBrainzArtistCredit)?.ArtistId
            ?? _trackInfo?.ArtistCredits.FirstOrDefault()?.ArtistId;
        return _isActive
            && !cancellation.IsCancellationRequested
            && ReferenceEquals(_artistLookupCts, cancellation)
            && generation == _artistLookupGeneration
            && trackGeneration == _trackLookupGeneration
            && string.Equals(artistId, selectedId, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentAlbumLookup(
        CancellationTokenSource cancellation,
        int generation,
        int trackGeneration,
        string releaseId)
    {
        return _isActive
            && !cancellation.IsCancellationRequested
            && ReferenceEquals(_albumLookupCts, cancellation)
            && generation == _albumLookupGeneration
            && trackGeneration == _trackLookupGeneration
            && string.Equals(releaseId, _trackInfo?.ReleaseId, StringComparison.OrdinalIgnoreCase);
    }

    private void PopulateTrackDetails(MusicBrainzTrackInfo info)
    {
        TrackDetailsPanel.Children.Clear();
        AddSection(TrackDetailsPanel, "Track");
        AddDetail(TrackDetailsPanel, "Title", info.Title, prominent: true);
        AddDetail(TrackDetailsPanel, "Artist", info.Artist, prominent: true);
        AddDetail(TrackDetailsPanel, "Album", info.Album, prominent: true);
        AddDetail(TrackDetailsPanel, "First released", info.FirstReleaseDate);
        AddDetail(TrackDetailsPanel, "Track", FormatTrackNumber(info.TrackNumber, info.TrackTotal));
        AddDetail(TrackDetailsPanel, "Genres", JoinValues(info.Genres));
    }

    private void PopulateArtistDetails(MusicBrainzArtistInfo info, string fullCredit)
    {
        ArtistDetailsPanel.Children.Clear();
        AddSection(ArtistDetailsPanel, "Artist");
        AddDetail(ArtistDetailsPanel, "Name", info.Name);
        AddDetail(ArtistDetailsPanel, "Track credit", fullCredit);
        AddDetail(ArtistDetailsPanel, "Country", info.Country);
        AddDetail(ArtistDetailsPanel, "Aliases", JoinValues(info.Aliases));
        AddDetail(ArtistDetailsPanel, "Genres", JoinValues(info.Genres));
        AddDetail(ArtistDetailsPanel, "About", info.Annotation);
        UpdateArtistIdentity(info.Name);
    }

    private void PopulateAlbumDetails(MusicBrainzAlbumInfo info)
    {
        AlbumDetailsPanel.Children.Clear();
        AddSection(AlbumDetailsPanel, "Album");
        AddDetail(AlbumDetailsPanel, "Title", info.Title);
        AddDetail(AlbumDetailsPanel, "Artist", info.Artist);
        AddDetail(AlbumDetailsPanel, "First released", info.FirstReleaseDate);
        AddDetail(AlbumDetailsPanel, "Genres", JoinValues(info.Genres));

        if (info.Tracks.Count > 0)
        {
            AddSection(AlbumDetailsPanel, "Track list");
            var showMediumHeaders = info.Tracks
                .Select(track => track.MediumPosition)
                .Distinct()
                .Skip(1)
                .Any();
            var currentMedium = -1;
            var currentRecordingId = _trackInfo?.RecordingId;
            foreach (var track in info.Tracks)
            {
                if (showMediumHeaders && track.MediumPosition != currentMedium)
                {
                    currentMedium = track.MediumPosition;
                    AddAlbumMediumHeader(AlbumDetailsPanel, track);
                }

                var isCurrentTrack = !string.IsNullOrWhiteSpace(currentRecordingId)
                    && string.Equals(
                        track.RecordingId,
                        currentRecordingId,
                        StringComparison.OrdinalIgnoreCase);
                AddAlbumTrack(AlbumDetailsPanel, track, info.Artist, isCurrentTrack);
            }
        }

    }

    private static void AddSection(Panel panel, string title)
    {
        panel.Children.Add(new TextBlock
        {
            Margin = panel.Children.Count == 0 ? new Thickness(0, 0, 0, 8) : new Thickness(0, 12, 0, 8),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Effect = new DropShadowEffect
            {
                BlurRadius = 2,
                ShadowDepth = 1,
                Opacity = 0.42,
                Color = Colors.Black
            },
            Text = title
        });
    }

    private static void AddDetail(Panel panel, string label, string? value, bool prominent = false)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var row = new Grid { Margin = new Thickness(0, 0, 0, prominent ? 6 : 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, prominent ? 2 : 1, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(157, 177, 182)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Text = label
        };
        var valueBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(235, 241, 239)),
            FontSize = prominent ? 14 : 13,
            FontWeight = prominent ? FontWeights.SemiBold : FontWeights.Normal,
            Text = value,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        panel.Children.Add(row);
    }

    private static void AddAlbumTrack(
        Panel panel,
        MusicBrainzAlbumTrack track,
        string albumArtist,
        bool isCurrentTrack)
    {
        var row = new Grid
        {
            MinHeight = 38
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        if (isCurrentTrack)
        {
            var highlight = new Image
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Source = CurrentAlbumTrackHighlightSource,
                Stretch = Stretch.Fill,
                Opacity = 0.42,
                IsHitTestVisible = false
            };
            Grid.SetColumn(highlight, 1);
            Grid.SetColumnSpan(highlight, 2);
            row.Children.Add(highlight);
        }

        var number = !string.IsNullOrWhiteSpace(track.Number)
            ? track.Number
            : track.Position > 0 ? track.Position.ToString() : string.Empty;
        row.Children.Add(new TextBlock
        {
            Margin = new Thickness(3, 7, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Color.FromRgb(183, 197, 200)),
            FontSize = 11.5,
            Text = number
        });

        var titlePanel = new StackPanel { Margin = new Thickness(0, 5, 10, 6) };
        titlePanel.Children.Add(new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(239, 244, 242)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Text = track.Title,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var trackArtist = !string.IsNullOrWhiteSpace(track.Artist)
            ? track.Artist.Trim()
            : albumArtist?.Trim();
        if (!string.IsNullOrWhiteSpace(trackArtist))
        {
            titlePanel.Children.Add(new TextBlock
            {
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(183, 198, 201)),
                Text = trackArtist,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        Grid.SetColumn(titlePanel, 1);
        row.Children.Add(titlePanel);

        var duration = new TextBlock
        {
            Margin = new Thickness(4, 7, 9, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Color.FromRgb(190, 203, 205)),
            FontSize = 11.5,
            Text = FormatDuration(track.Duration)
        };
        Grid.SetColumn(duration, 2);
        row.Children.Add(duration);
        panel.Children.Add(row);
    }

    private static void AddAlbumMediumHeader(Panel panel, MusicBrainzAlbumTrack track)
    {
        var title = !string.IsNullOrWhiteSpace(track.MediumTitle)
            ? track.MediumTitle
            : track.MediumPosition > 0 ? $"Disc {track.MediumPosition}" : "Disc";
        if (!string.IsNullOrWhiteSpace(track.MediumFormat)
            && !string.Equals(title, track.MediumFormat, StringComparison.OrdinalIgnoreCase))
        {
            title += $"  ·  {track.MediumFormat}";
        }

        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(2, 11, 0, 6),
            Foreground = Brushes.White,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Text = title
        });
    }

    private void ConfigureArtistSelector(IReadOnlyList<MusicBrainzArtistCredit> credits)
    {
        _isUpdatingArtistSelector = true;
        try
        {
            ArtistSelector.ItemsSource = credits;
            ArtistSelector.SelectedIndex = credits.Count > 0 ? 0 : -1;
            ArtistSelectorPanel.Visibility = credits.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isUpdatingArtistSelector = false;
        }

        UpdateHeaderForSelectedPage();
        UpdateHeroForSelectedPage();
    }

    private void ResetDetails()
    {
        _trackInfo = null;
        _albumInfo = null;
        _artistCache.Clear();
        _artistLookupErrors.Clear();
        _trackLookupInFlightKey = string.Empty;
        _trackLookupCompletedKey = string.Empty;
        _trackLookupFailure = null;
        _trackRetryNotBefore = DateTimeOffset.MinValue;
        _artistLookupInFlightId = string.Empty;
        _albumLookupInFlightId = string.Empty;
        _albumLookupFailure = null;
        TrackDetailsPanel.Children.Clear();
        ArtistDetailsPanel.Children.Clear();
        AlbumDetailsPanel.Children.Clear();
        _isUpdatingArtistSelector = true;
        ArtistSelector.ItemsSource = null;
        ArtistSelectorPanel.Visibility = Visibility.Collapsed;
        _isUpdatingArtistSelector = false;
        SetTrackStatus("Finding track information...", isLoading: true, canRetry: false);
        SetArtistStatus("Finding the matching artist...", isLoading: false, canRetry: false);
        SetAlbumStatus("Finding the matching album...", isLoading: false, canRetry: false);
        UpdateHeroForSelectedPage();
    }

    private void SetTrackReady()
    {
        TrackStatusPanel.Visibility = Visibility.Collapsed;
        TrackScrollViewer.Visibility = Visibility.Visible;
    }

    private void SetArtistReady()
    {
        ArtistStatusPanel.Visibility = Visibility.Collapsed;
        ArtistScrollViewer.Visibility = Visibility.Visible;
    }

    private void SetAlbumReady()
    {
        AlbumStatusPanel.Visibility = Visibility.Collapsed;
        AlbumScrollViewer.Visibility = Visibility.Visible;
    }

    private void SetTrackStatus(string message, bool isLoading, bool canRetry)
    {
        TrackScrollViewer.Visibility = Visibility.Collapsed;
        TrackStatusPanel.Visibility = Visibility.Visible;
        TrackStatusText.Text = message;
        TrackProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        TrackRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetArtistStatus(string message, bool isLoading, bool canRetry)
    {
        ArtistScrollViewer.Visibility = Visibility.Collapsed;
        ArtistStatusPanel.Visibility = Visibility.Visible;
        ArtistStatusText.Text = message;
        ArtistProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ArtistRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetAlbumStatus(string message, bool isLoading, bool canRetry)
    {
        AlbumScrollViewer.Visibility = Visibility.Collapsed;
        AlbumStatusPanel.Visibility = Visibility.Visible;
        AlbumStatusText.Text = message;
        AlbumProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        AlbumRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHeaderFromSnapshot(MediaSnapshot snapshot)
    {
        HeaderTitleText.Text = snapshot.HasSession && !string.IsNullOrWhiteSpace(snapshot.Title)
            ? snapshot.Title.Trim()
            : "No active track";
        HeaderArtistText.Text = snapshot.Artist.Trim();
    }

    private void UpdateHeaderFromMatch(MusicBrainzTrackInfo info)
    {
        HeaderTitleText.Text = FirstNonEmpty(info.Title, _snapshot.Title);
        HeaderArtistText.Text = FirstNonEmpty(info.Artist, _snapshot.Artist);
    }

    private void UpdateHeaderForSelectedPage()
    {
        if (InfoTabs.SelectedItem == ArtistTab)
        {
            var selectedCredit = ArtistSelector.SelectedItem as MusicBrainzArtistCredit
                ?? _trackInfo?.ArtistCredits.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(selectedCredit?.ArtistId)
                && _artistCache.TryGetValue(selectedCredit.ArtistId, out var artistInfo))
            {
                HeaderTitleText.Text = FirstNonEmpty(artistInfo.Name, selectedCredit.Name, _snapshot.Artist);
                HeaderArtistText.Text = FirstNonEmpty(artistInfo.Aliases.FirstOrDefault(
                    alias => !string.IsNullOrWhiteSpace(alias)));
                return;
            }

            HeaderTitleText.Text = FirstNonEmpty(selectedCredit?.Name, _snapshot.Artist, "Artist");
            HeaderArtistText.Text = string.Empty;
            return;
        }

        if (_trackInfo is not null)
        {
            UpdateHeaderFromMatch(_trackInfo);
            return;
        }

        UpdateHeaderFromSnapshot(_snapshot);
    }

    private void UpdateHeroForSelectedPage()
    {
        if (_isHeroTransitioning)
        {
            return;
        }

        if (InfoTabs.SelectedItem == ArtistTab)
        {
            var selectedCredit = ArtistSelector.SelectedItem as MusicBrainzArtistCredit
                ?? _trackInfo?.ArtistCredits.FirstOrDefault();
            var artistName = selectedCredit?.Name
                ?? _snapshot.Artist;
            if (!string.IsNullOrWhiteSpace(selectedCredit?.ArtistId)
                && _artistPhotoCache.TryGetValue(selectedCredit.ArtistId, out var photo))
            {
                ShowArtistPhoto(photo);
                return;
            }

            UpdateArtistIdentity(artistName);
            ArtistIdentityPanel.Visibility = Visibility.Visible;
            HeroArtworkPlaceholder.Visibility = Visibility.Collapsed;
            HeroArtworkImage.Visibility = Visibility.Collapsed;
            HeroArtworkImage.ToolTip = null;
            HeroArtVideoImage.Visibility = Visibility.Collapsed;
            HeroArtFrozenFrameImage.Visibility = Visibility.Collapsed;
            SetBackdropArtwork(_snapshot.CoverArt);
            return;
        }

        ShowArtworkHero(_snapshot.CoverArt);
    }

    private void ShowArtworkHero(BitmapSource? artwork, bool showAnimatedArtwork = true)
    {
        ArtistIdentityPanel.Visibility = Visibility.Collapsed;
        HeroArtworkPlaceholder.Visibility = artwork is null ? Visibility.Visible : Visibility.Collapsed;
        HeroArtworkImage.Visibility = Visibility.Visible;
        HeroArtVideoImage.Visibility = showAnimatedArtwork ? Visibility.Visible : Visibility.Collapsed;
        HeroArtFrozenFrameImage.Visibility = showAnimatedArtwork ? Visibility.Visible : Visibility.Collapsed;
        HeroArtworkImage.Source = artwork;
        HeroArtworkImage.ToolTip = null;
        SetBackdropArtwork(artwork);
    }

    private void ShowArtistPhoto(ArtistHeroPhoto photo)
    {
        ArtistIdentityPanel.Visibility = Visibility.Collapsed;
        HeroArtworkPlaceholder.Visibility = Visibility.Collapsed;
        HeroArtworkImage.Visibility = Visibility.Visible;
        HeroArtVideoImage.Visibility = Visibility.Collapsed;
        HeroArtFrozenFrameImage.Visibility = Visibility.Collapsed;
        HeroArtworkImage.Source = photo.Artwork;
        HeroArtworkImage.ToolTip = FormatArtistPhotoTooltip(photo);
        SetBackdropArtwork(photo.Artwork);
    }

    private static string? FormatArtistPhotoTooltip(ArtistHeroPhoto photo)
    {
        var lines = new[]
        {
            photo.Attribution.Trim(),
            photo.LicenseName.Trim(),
            photo.SourcePageUrl.Trim()
        }.Where(value => value.Length > 0);
        var tooltip = string.Join(Environment.NewLine, lines);
        return tooltip.Length > 0 ? tooltip : null;
    }

    private void SetBackdropArtwork(BitmapSource? artwork)
    {
        BlurredCoverImage.Source = artwork;
        ApplyArtworkTint(artwork);
    }

    private void UpdateArtistIdentity(string? artistName)
    {
        artistName = artistName?.Trim() ?? string.Empty;
        ArtistIdentityNameText.Text = artistName.Length > 0 ? artistName : "Artist";
        var words = artistName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ArtistInitialsText.Text = words.Length switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => string.Concat(words[0][..1], words[^1][..1]).ToUpperInvariant()
        };
    }

    private void ApplyArtworkTint(BitmapSource? cover)
    {
        var tint = ExtractDominantTint(cover) ?? DefaultTint;
        TintChanged?.Invoke(tint);
        AnimateColor(ShellTopStop, WithAlpha(Blend(tint, Colors.White, 0.18), 0xA8));
        AnimateColor(ShellUpperStop, WithAlpha(Blend(tint, Colors.Black, 0.10), 0x98));
        AnimateColor(ShellLowerStop, WithAlpha(Blend(tint, Colors.Black, 0.48), 0xA8));
        AnimateColor(ShellBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.68), 0xB5));
        AnimateColor(ShellGlowStop, WithAlpha(Blend(tint, Colors.White, 0.58), 0x70));
        AnimateColor(ArtistIdentityGlowStop, WithAlpha(Blend(tint, Colors.White, 0.38), 0xD5));
        AnimateColor(ArtistIdentityBaseStop, WithAlpha(Blend(tint, Colors.Black, 0.42), 0xF0));
        var shellBorder = WithAlpha(Blend(tint, Colors.White, 0.60), 0x9B);
        AnimateBrushColor(InfoShell.BorderBrush, shellBorder);
        AnimateBrushColor(InfoShellTopBorder.Background, shellBorder);
        AnimateBrushColor(InfoShellTopRightBorder.Background, shellBorder);
    }

    private void AnimateColor(GradientStop stop, Color color)
    {
        if (!_isLoaded)
        {
            stop.Color = color;
            return;
        }

        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation
        {
            To = color,
            Duration = TimeSpan.FromMilliseconds(460),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateBrushColor(Brush brush, Color color)
    {
        if (brush is not SolidColorBrush solidBrush)
        {
            return;
        }

        if (!_isLoaded)
        {
            solidBrush.Color = color;
            return;
        }

        solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = color,
            Duration = TimeSpan.FromMilliseconds(460),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void InfoTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, InfoTabs))
        {
            return;
        }

        UpdateHeaderForSelectedPage();
        UpdateHeroForSelectedPage();
        if (_isActive)
        {
            _ = EnsureSelectedPageLoadedAsync();
        }
    }

    private void ArtistSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingArtistSelector)
        {
            return;
        }

        _artistLookupCts?.Cancel();
        ArtistDetailsPanel.Children.Clear();
        UpdateHeaderForSelectedPage();
        UpdateHeroForSelectedPage();
        _ = LoadSelectedArtistAsync();
    }

    private void TrackRetryButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadTrackAsync(force: true);
    }

    private void ArtistRetryButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _trackInfo is null
            ? LoadTrackAsync(force: true)
            : LoadSelectedArtistAsync(force: true);
    }

    private void AlbumRetryButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _trackInfo is null
            ? LoadTrackAsync(force: true)
            : LoadAlbumAsync(force: true);
    }

    private void ResetHeroTransform()
    {
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HeroArtworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        HeroArtworkTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        HeroArtworkScale.ScaleX = 1;
        HeroArtworkScale.ScaleY = 1;
        HeroArtworkTranslate.X = 0;
        HeroArtworkTranslate.Y = 0;
    }

    private void SelectPage(MusicInfoPage page)
    {
        InfoTabs.SelectedIndex = page switch
        {
            MusicInfoPage.Artist => 1,
            MusicInfoPage.Album => 2,
            _ => 0
        };
    }

    private void RoundedSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyRoundedClip(sender, e, 9);
    }

    private void ArtworkSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyRoundedClip(sender, e, 8);
    }

    private static void ApplyRoundedClip(object sender, SizeChangedEventArgs e, double radius)
    {
        if (sender is not FrameworkElement element || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        var geometry = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
        geometry.Freeze();
        element.Clip = geometry;
    }

    public void Dispose()
    {
        _isActive = false;
        _animationGeneration++;
        CancelLookups();
        _trackLookupCts?.Dispose();
        _artistLookupCts?.Dispose();
        _albumLookupCts?.Dispose();
        _trackLookupCts = null;
        _artistLookupCts = null;
        _albumLookupCts = null;
        _artistCache.Clear();
        _artistPhotoCache.Clear();
        _artistPhotoCacheOrder.Clear();
        _artistPhotoRetryNotBefore.Clear();
        _artistPhotoCachedPixels = 0;
    }

    private void CancelLookups()
    {
        _trackLookupCts?.Cancel();
        _artistLookupCts?.Cancel();
        _albumLookupCts?.Cancel();
        _trackLookupGeneration++;
        _artistLookupGeneration++;
        _albumLookupGeneration++;
    }

    private static string CreateSnapshotKey(MediaSnapshot snapshot)
    {
        return snapshot.HasSession
            ? $"{snapshot.Title.Trim().ToLowerInvariant()}|{snapshot.Artist.Trim().ToLowerInvariant()}|{snapshot.Album.Trim().ToLowerInvariant()}"
            : string.Empty;
    }

    private static string FormatTrackNumber(string number, string total)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(total) ? number.Trim() : $"{number.Trim()} of {total.Trim()}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    private static string JoinValues(IEnumerable<string> values)
    {
        return string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static CachedMusicInfoLookupFailure CacheLookupFailure(MusicInfoLookupFailure failure)
    {
        return new CachedMusicInfoLookupFailure(
            failure,
            failure.IsTransient
                ? DateTimeOffset.UtcNow + LookupRetryCooldown
                : DateTimeOffset.MaxValue);
    }

    internal static bool IsLookupRetryDue(
        CachedMusicInfoLookupFailure failure,
        DateTimeOffset? now = null)
    {
        return failure.Failure.IsTransient
            && (now ?? DateTimeOffset.UtcNow) >= failure.RetryNotBefore;
    }

    internal static MusicInfoLookupFailure ClassifyLookupFailure(
        Exception exception,
        bool isEntityLookup = true)
    {
        ArgumentNullException.ThrowIfNull(exception);
        HttpRequestException? requestFailure = null;
        var cause = exception;
        while (true)
        {
            requestFailure ??= cause as HttpRequestException;
            if (cause.InnerException is null)
            {
                break;
            }

            cause = cause.InnerException;
        }

        if (exception is OperationCanceledException or TimeoutException
            || cause is OperationCanceledException or TimeoutException)
        {
            return new MusicInfoLookupFailure(
                "These details took too long to load. Try again.",
                CanRetry: true,
                IsTransient: true);
        }

        if (exception is IOException || cause is IOException)
        {
            return new MusicInfoLookupFailure(
                "These details aren't available right now. Try again.",
                CanRetry: true,
                IsTransient: true);
        }

        if (requestFailure?.StatusCode == HttpStatusCode.NotFound)
        {
            if (!isEntityLookup)
            {
                return new MusicInfoLookupFailure(
                    "These details aren't available right now. Try again.",
                    CanRetry: true,
                    IsTransient: true);
            }

            return new MusicInfoLookupFailure(
                "These details are no longer available.",
                CanRetry: false,
                IsTransient: false);
        }

        if (requestFailure?.StatusCode is { } statusCode
            && MusicBrainzService.IsTransientMusicBrainzStatus(statusCode))
        {
            return new MusicInfoLookupFailure(
                "These details aren't available right now. Try again.",
                CanRetry: true,
                IsTransient: true);
        }

        if (requestFailure?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new MusicInfoLookupFailure(
                "These details aren't available right now. Try again.",
                CanRetry: true,
                IsTransient: true);
        }

        if (requestFailure is { StatusCode: null })
        {
            return new MusicInfoLookupFailure(
                "These details aren't available right now. Try again.",
                CanRetry: true,
                IsTransient: true);
        }

        if (requestFailure?.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            return new MusicInfoLookupFailure(
                "These details couldn't be loaded.",
                CanRetry: false,
                IsTransient: false);
        }

        if (exception is InvalidDataException || cause is InvalidDataException)
        {
            return new MusicInfoLookupFailure(
                "These details couldn't be read. Try again.",
                CanRetry: true,
                IsTransient: true);
        }

        return new MusicInfoLookupFailure(
            "These details couldn't be loaded. Try again.",
            CanRetry: true,
            IsTransient: false);
    }
}
