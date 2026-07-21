using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Mystral.Configuration;
using Mystral.Controls;
using Mystral.Models;
using Mystral.Services;
using static Mystral.Services.ArtworkTint;

namespace Mystral.Views;

public partial class MainWindow : Window
{
    private enum SliderInteractionKind
    {
        Seek,
        Volume
    }

    private sealed class SliderPointerInteraction
    {
        public SliderPointerInteraction(SliderInteractionKind kind)
        {
            Kind = kind;
        }

        public SliderInteractionKind Kind { get; }
        public Slider? ActiveSlider { get; set; }
        public bool IsPointerDown { get; set; }
        public bool IsThumbDrag { get; set; }
        public bool OwnsMouseCapture { get; set; }
    }

    private readonly MediaSessionService _mediaService;
    private readonly LyricsService _lyricsService;
    private readonly AnimatedArtworkService _animatedArtworkService;
    private readonly VolumeService _volumeService;
    private readonly LastFmService _lastFmService;
    private readonly MusicBrainzService _musicBrainzService;
    private readonly ArtistArtworkService _artistArtworkService;
    private readonly GlobeConnectionService _globeConnectionService;
    private readonly AppSettingsService _settingsService;
    private readonly DispatcherTimer _mediaPollTimer;
    private readonly DispatcherTimer _loadingIconTimer;
    private readonly DispatcherTimer _seekToolTipHideTimer;
    private readonly DispatcherTimer _volumeToolTipHideTimer;
    private readonly PlaybackTimelineStabilizer _timelineStabilizer = new();
    private readonly SliderPointerInteraction _seekSliderInteraction = new(SliderInteractionKind.Seek);
    private readonly SliderPointerInteraction _volumeSliderInteraction = new(SliderInteractionKind.Volume);
    private readonly Dictionary<Slider, SliderPointerInteraction> _sliderPointerInteractions = [];
    private readonly CancellationTokenSource _burnDiscArtworkCts = new();
    private readonly List<TextBlock> _lyricBlocks = [];
    private readonly List<LyricWaitIndicator> _lyricWaitIndicators = [];
    private readonly List<BitmapImage> _loadingIconFrames = [];
    private CancellationTokenSource? _lyricsRefreshCts;
    private CancellationTokenSource? _lastFmCts;
    private CancellationTokenSource? _lastFmScrobbleCts;
    private CancellationTokenSource? _animatedArtworkCts;
    private MediaPlayer? _animatedArtPlayer;
    private DrawingImage? _animatedArtSource;
    private MediaPlayer? _heldAnimatedArtPlayer;
    private DrawingImage? _heldAnimatedArtSource;
    private DispatcherTimer? _animatedArtRevealTimer;
    private DispatcherTimer? _frozenArtHoldTimer;
    private EventHandler? _animatedArtPresentationRenderingHandler;
    private EventHandler? _heldArtStaticReleaseRenderingHandler;
    private MediaPlayer? _pendingAnimatedArtPresentationPlayer;
    private int _pendingAnimatedArtPresentationGeneration;
    private string _animatedArtworkKey = string.Empty;
    private string? _animatedArtworkFile;
    private string _lastAppliedCoverArtFingerprint = string.Empty;
    private string _heldCoverArtFingerprint = string.Empty;
    private int _animatedArtworkGeneration;
    private int _heldArtFadeEpoch;
    private int _heldArtTimerGeneration;
    private int _heldArtStaticReleaseGeneration;
    private bool _isAnimatedArtPresented;
    private string _lyricsTrackKey = string.Empty;
    private string _lastFmTrackKey = string.Empty;
    private string _lastFmLookupCompletedKey = string.Empty;
    private string _scrobblingStatusText = "Scrobbling: disabled";
    private string _trayNowPlayingTrackName = string.Empty;
    private ScrobblePlaybackState? _scrobbleState;
    private bool _isExpanded;
    private bool _isLyricsMode;
    private bool _restoreExpandedAfterLyrics;
    private bool _isFullscreen;
    private bool _isFullscreenLyrics;
    private double _preFullscreenLeft;
    private double _preFullscreenTop;
    private double _preFullscreenWidth;
    private double _preFullscreenHeight;
    private WindowState _preFullscreenState;
    private bool _preFullscreenExpanded;
    private bool _preFullscreenLyrics;
    private int _activeFullscreenLyricIndex = -1;
    private int _activeFullscreenLyricWaitIndicatorIndex = -1;
    private double _fullscreenLyricsScrollTarget;
    private readonly List<TextBlock> _fullscreenLyricBlocks = [];
    private readonly List<LyricWaitIndicator> _fullscreenLyricWaitIndicators = [];
    private bool _isMinimizing;
    private bool _isClosing;
    private bool _allowClose;
    private bool _progressSliderUpdating;
    private bool _isBurnDiscPointerDown;
    private bool _isBurnDiscDragging;
    private bool _isBurnDiscInserting;
    private bool _isOpeningBurningWindow;
    private BurningWindow? _burningWindow;
    private bool _isBurnPresentationDetached;
    private bool _isBurnPresentationReading;
    private bool _isBurningWindowHiddenByDiscRemoval;
    private bool _isWaitingForBurningWindowClose;
    private bool _isWaitingForSettingsWindowClose;
    private BitmapSource? _defaultCompactBurnDiscArtwork;
    private BitmapSource? _burnPresentationDiscSource;
    private CancellationTokenSource? _burnPresentationArtworkCts;
    private CancellationTokenSource? _burnDiscReadingCts;
    private int _burnPresentationArtworkGeneration;
    private int _burnDiscReadingGeneration;
    private bool _isBurnDiscOverflowActive;
    private bool _suppressBurnDiscHoverUntilLeave;
    private double _burnDiscDragStartY;
    private double _burnDiscDragStartAngle;
    private double _burnDiscPullDistance;
    private double _burnDiscLastPointerY;
    private double _burnDiscReleaseVelocityY;
    private long _burnDiscDragStartedAt;
    private long _burnDiscLastSampleAt;
    private int _burnDiscAnimationGeneration;
    private bool _isUserBrowsingLyrics;
    private bool _isUserBrowsingFullscreenLyrics;
    private bool _isLyricsScrollAnimating;
    private readonly DispatcherTimer _fullscreenLyricsInactivityTimer;
    private FrameworkElement? _lyricsFooterPanel;
    private FrameworkElement? _fullscreenLyricsFooterPanel;
    private Border? _fullscreenLyricsTopSpacer;
    private Border? _fullscreenLyricsBottomSpacer;
    private SettingsWindow? _settingsWindow;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private ContextMenu? _trayContextMenu;
    private int _activeLyricIndex = -1;
    private int _activeLyricWaitIndicatorIndex = -1;
    private double _lyricsScrollTarget;
    private TimeSpan _lastLyricsPosition = TimeSpan.Zero;
    private int _seekRequestGeneration;
    private int _loadingIconFrameIndex;
    private int _musicInfoTransitionGeneration;
    private bool _isMusicInfoMode;
    private bool _isMusicInfoTransitioning;
    private MusicInfoPage? _pendingMusicInfoPageAfterFullscreen;
    private Rect _musicInfoCompactBounds = Rect.Empty;
    private Rect _musicInfoArtworkTarget = Rect.Empty;
    private double _musicInfoScale = 1;
    private Brush? _musicInfoRootBackground;
    private Brush? _musicInfoGlassBackground;
    private Brush? _musicInfoActionBackground;
    private Brush? _musicInfoTitleBarChromeBackground;
    private Brush? _musicInfoTitleBarChromeBorderBrush;
    private Thickness _musicInfoRootBorderThickness;
    private Thickness _musicInfoActionBorderThickness;
    private Thickness _musicInfoTitleBarChromeBorderThickness;
    private Thickness _musicInfoTitleBarMargin;
    private Visibility _musicInfoBlurredArtVisibility;
    private Visibility _musicInfoGlassGlowVisibility;
    private Visibility _musicInfoGlassGlossVisibility;
    private Visibility _musicInfoInnerBorderVisibility;
    private Visibility _musicInfoMediaPanelVisibility;
    private bool _musicInfoControlsOnlyPresentation;
    private bool _restoreExpandedAfterMusicInfo;
    private bool _restoreLyricsAfterMusicInfo;
    private bool _restoreExpandedBehindLyricsAfterMusicInfo;
    private bool _restoreFullscreenAfterMusicInfo;
    private const double CompactWidth = 352;
    private const double CompactHeight = 172;
    private const double ExpandedSize = 352;
    private const double LyricsWidth = ExpandedSize;
    private const double LyricsHeight = 620;
    private const double MusicInfoWidth = 720;
    private const double MusicInfoHeight = 456;
    private const double MusicInfoPlayerOffsetX = MusicInfoWidth - CompactWidth - 12;
    private const double MusicInfoPlayerOffsetY = 10;
    private const double MusicInfoControlsOffsetY = 91;
    private const double MusicInfoControlsHeight = 91;
    private const double MusicInfoTitleBarGapHeight = 7;
    private const double BurnDiscRetractedOffsetY = 1.30;
    private const double BurnDiscEjectedOffsetY = 1.03;
    private const double BurnDiscMaxPulledOffsetY = 0.28;
    private const double BurnDiscEjectedAngle = 72;
    private const double BurnDiscPullSpinPerDip = 3.6;
    private const double BurnDiscMaxPullDistance = 100;
    private const double BurnDiscPresentationDetachDistance = BurnDiscMaxPullDistance * 0.65;
    private static readonly TimeSpan BurnDiscReadingDelay = TimeSpan.FromSeconds(2);
    private const double BurnDiscCollapsedSurfaceHeight = 10;
    private const double BurnDiscEjectedSurfaceHeight = 43;
    private const double BurnDiscMaxSurfaceHeight = 142;
    private const double BurnDiscOverflowWindowHeight = CompactHeight + 80;
    private const double BurnDiscPullOffsetPerDip =
        (BurnDiscEjectedOffsetY - BurnDiscMaxPulledOffsetY) / BurnDiscMaxPullDistance;
    private const double LyricsStackVerticalPadding = 132;
    private const double LyricsEndTailSpacerHeight = 180;
    private static readonly TimeSpan LyricWaitMinimumGap = TimeSpan.FromMilliseconds(4200);
    private static readonly TimeSpan LyricWaitAfterLineDelay = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan LyricWaitMinimumDuration = TimeSpan.FromMilliseconds(1600);
    private static readonly string WindowPlacementPath = Path.Combine(
        AppMetadata.LocalApplicationDataDirectory,
        "window-placement.json");
    private static readonly string LastRunVersionPath = Path.Combine(
        AppMetadata.LocalApplicationDataDirectory,
        "last-version.txt");
    private static readonly Color DefaultTint = Color.FromRgb(74, 82, 88);
    private static readonly TimeSpan LyricActivationLead = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan LyricRegressionTolerance = TimeSpan.FromMilliseconds(1750);
    private bool _hasAppliedArtworkTint;
    private BitmapSource? _lastArtworkTintSource;
    private bool _isExitingFromTray;
    private string _lastNotificationTrackTitle = string.Empty;
    private string _lastNotificationTrackArtist = string.Empty;
    private bool _globeRevocationWarningPending;
    private bool _globeServerWarningPending;

    private MediaSnapshot Snapshot { get; set; } = MediaSnapshot.Empty;
    private LyricsResult Lyrics { get; set; } = LyricsResult.Empty;
    private LastFmTrackInfo? CurrentLastFmInfo { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new AppSettingsService();
        _mediaService = new MediaSessionService();
        _lyricsService = new LyricsService();
        _animatedArtworkService = new AnimatedArtworkService();
        _volumeService = new VolumeService();
        _lastFmService = new LastFmService(_settingsService);
        _musicBrainzService = new MusicBrainzService();
        _artistArtworkService = new ArtistArtworkService();
        _globeConnectionService = new GlobeConnectionService(_settingsService);
        MusicInfoPanel.Initialize(_musicBrainzService, _artistArtworkService);

        _mediaService.SnapshotChanged += OnSnapshotChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _globeConnectionService.LinkRevoked += GlobeConnectionService_LinkRevoked;
        _globeConnectionService.ServerUnavailable += GlobeConnectionService_ServerUnavailable;

        _mediaPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mediaPollTimer.Tick += async (_, _) => await _mediaService.RefreshAsync();

        _fullscreenLyricsInactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.0) };
        _fullscreenLyricsInactivityTimer.Tick += (_, _) =>
        {
            _fullscreenLyricsInactivityTimer.Stop();
            if (_isFullscreen && _isFullscreenLyrics)
            {
                _isUserBrowsingFullscreenLyrics = false;
                ApplyLyricBlockVisualState(_activeFullscreenLyricIndex, isFullscreen: true);
                CenterActiveLyric(_activeFullscreenLyricIndex, isFullscreen: true);
            }
        };

        _loadingIconTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        _loadingIconTimer.Tick += (_, _) => AdvanceLoadingIconFrame();
        _seekToolTipHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _seekToolTipHideTimer.Tick += (_, _) => HideSliderToolTip(_seekSliderInteraction);
        _volumeToolTipHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _volumeToolTipHideTimer.Tick += (_, _) => HideSliderToolTip(_volumeSliderInteraction);
        LoadLoadingIconFrames();
        _ = LoadCompactBurnDiscArtworkAsync();

        RootCard.Opacity = 0;
        WindowScale.ScaleX = 0.96;
        WindowScale.ScaleY = 0.96;
        InitializeInteractiveToolTips();
        ApplyPlayerAppearance();
        ShowInfoButtons();
        InitializeTrayIcon();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowPlacement();
        Topmost = _settingsService.Settings.Behavior.AlwaysOnTop;
        SetTopmostUi();
        SetStartup(_settingsService.Settings.Behavior.StartWithWindows);
        PlayOpenAnimation();
        CompositionTarget.Rendering += TimelineCompositionTarget_Rendering;
        _mediaPollTimer.Start();
        ShowUpdatedSuccessfullyIfNeeded();
        _ = StartGlobeConnectionAsync();
        if (_settingsService.Settings.Behavior.CheckForUpdatesOnStartup)
        {
            _ = SettingsWindow.CheckForUpdatesAsync(this, showNoUpdateMessage: false, showErrors: false);
        }

        try
        {
            await _mediaService.StartAsync();
        }
        catch (Exception ex)
        {
            TitleText.Text = "Media controls unavailable";
            DescriptionText.Text = ex.Message;
            SetTransportEnabled(false);
        }
    }

    private async Task StartGlobeConnectionAsync()
    {
        try
        {
            await _globeConnectionService.StartAsync();
        }
        catch (Exception ex)
        {
            if (IsLoaded && !_isClosing)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "Could not check globe link",
                    ex.Message);
            }
        }
    }

    private void ShowUpdatedSuccessfullyIfNeeded()
    {
        try
        {
            Directory.CreateDirectory(AppMetadata.LocalApplicationDataDirectory);
            var previousVersion = File.Exists(LastRunVersionPath)
                ? File.ReadAllText(LastRunVersionPath).Trim()
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(previousVersion)
                && SettingsWindow.IsNewerRelease(AppMetadata.Version, previousVersion))
            {
                var message = $"Mystral was updated to version {AppMetadata.Version}.";
                var compareUri = GitHubReleaseLinks.CreateCompareUri(
                    previousVersion,
                    AppMetadata.Version);
                if (compareUri is null)
                {
                    AppDialogWindow.ShowConfirmation(this, "Update installed", message);
                }
                else
                {
                    AppDialogWindow.ShowConfirmationWithLink(
                        this,
                        "Update installed",
                        message,
                        "What's new?",
                        compareUri);
                }
            }

            File.WriteAllText(LastRunVersionPath, AppMetadata.Version);
        }
        catch
        {
        }
    }

    private void InitializeInteractiveToolTips()
    {
        AlbumArtSurface.ToolTip = null;
        LyricsBackButton.ToolTip = "Back";

        RegisterInteractiveSliders(
            ProgressSliders,
            _seekSliderInteraction,
            CreateSeekToolTip);
        RegisterInteractiveSliders(
            VolumeSliders,
            _volumeSliderInteraction,
            CreateVolumeToolTip);
    }

    private Button[] PreviousButtons => [PreviousButton, ExpandedPreviousButton, LyricsPreviousButton, FullscreenPreviousButton];
    private Button[] NextButtons => [NextButton, ExpandedNextButton, LyricsNextButton, FullscreenNextButton];
    private Button[] PlayPauseButtons => [PlayPauseButton, ExpandedPlayPauseButton, LyricsPlayPauseButton, FullscreenPlayPauseButton];
    private Button[] VolumeButtons => [CompactVolumeButton, ExpandedVolumeButton, LyricsVolumeButton, FullscreenVolumeButton];
    private Button[] InfoButtons => [CompactInfoButton, ExpandedInfoButton, LyricsInfoButton, FullscreenInfoButton];
    private Button[] LyricsButtons => [CompactLyricsButton, ExpandedLyricsButton, FullscreenLyricsToggle];
    private Slider[] ProgressSliders => [ProgressSlider, ExpandedProgressSlider, LyricsProgressSlider, FullscreenProgressSlider];
    private Slider[] VolumeSliders => [CompactVolumeSlider, ExpandedVolumeSlider, LyricsVolumeSlider, FullscreenVolumeSlider];
    private TextBlock[] ElapsedTexts => [ElapsedText, ExpandedElapsedText, LyricsElapsedText, FullscreenElapsedText];
    private TextBlock[] DurationTexts => [DurationText, ExpandedDurationText, LyricsDurationText, FullscreenDurationText];

    private void RegisterInteractiveSliders(
        IEnumerable<Slider> sliders,
        SliderPointerInteraction interaction,
        Func<Slider, ToolTip> createToolTip)
    {
        foreach (var slider in sliders)
        {
            _sliderPointerInteractions.Add(slider, interaction);
            slider.ToolTip = createToolTip(slider);
            ToolTipService.SetInitialShowDelay(slider, 0);
            ToolTipService.SetBetweenShowDelay(slider, 0);
            ToolTipService.SetShowDuration(slider, 60000);
            slider.AddHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(InteractiveSlider_PreviewMouseLeftButtonDown),
                handledEventsToo: true);
            slider.AddHandler(
                UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(InteractiveSlider_PreviewMouseLeftButtonUp),
                handledEventsToo: true);
            slider.AddHandler(
                UIElement.MouseMoveEvent,
                new MouseEventHandler(InteractiveSlider_MouseMove),
                handledEventsToo: true);
            slider.MouseLeave += InteractiveSlider_MouseLeave;
            slider.LostMouseCapture += InteractiveSlider_LostMouseCapture;
        }
    }

    private void InteractiveSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider
            || !_sliderPointerInteractions.TryGetValue(slider, out var interaction))
        {
            return;
        }

        if (IsControlDisabled(slider))
        {
            CancelSliderPointerInteraction(interaction);
            e.Handled = true;
            return;
        }

        BeginSliderPointerInteraction(interaction, slider, e);
    }

    private async void InteractiveSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider
            || !_sliderPointerInteractions.TryGetValue(slider, out var interaction))
        {
            return;
        }

        if (IsControlDisabled(slider))
        {
            CancelSliderPointerInteraction(interaction);
            e.Handled = true;
            return;
        }

        if (!interaction.IsPointerDown || !ReferenceEquals(interaction.ActiveSlider, slider))
        {
            return;
        }

        if (!interaction.IsThumbDrag)
        {
            SetSliderValueFromPointer(slider, e);
            e.Handled = true;
        }

        if (interaction.Kind == SliderInteractionKind.Seek)
        {
            await CompleteSeekAsync(slider);
        }
        else
        {
            CompleteVolumeInteraction(slider);
        }
    }

    private void InteractiveSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider
            || !_sliderPointerInteractions.TryGetValue(slider, out var interaction)
            || !interaction.IsPointerDown
            || !ReferenceEquals(interaction.ActiveSlider, slider)
            || IsControlDisabled(slider))
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed && !interaction.IsThumbDrag)
        {
            SetSliderValueFromPointer(slider, e);
        }

        ShowSliderToolTip(interaction, slider, includeSeekPosition: true);
    }

    private void InteractiveSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Slider slider
            && _sliderPointerInteractions.TryGetValue(slider, out var interaction)
            && !interaction.IsPointerDown
            && ReferenceEquals(interaction.ActiveSlider, slider))
        {
            BeginSliderToolTipLinger(interaction);
        }
    }

    private async void InteractiveSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider
            || !_sliderPointerInteractions.TryGetValue(slider, out var interaction)
            || !ReferenceEquals(interaction.ActiveSlider, slider))
        {
            return;
        }

        interaction.OwnsMouseCapture = false;
        if (interaction.IsPointerDown)
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                if (interaction.Kind == SliderInteractionKind.Seek)
                {
                    await CompleteSeekAsync(slider);
                }
                else
                {
                    CompleteVolumeInteraction(slider);
                }

                return;
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (interaction.IsPointerDown
                    && ReferenceEquals(interaction.ActiveSlider, slider)
                    && !slider.IsMouseCaptureWithin)
                {
                    CancelSliderPointerInteraction(interaction);
                }
            }, DispatcherPriority.Input);
            return;
        }

        BeginSliderToolTipLinger(interaction);
    }

    private void BeginSliderPointerInteraction(
        SliderPointerInteraction interaction,
        Slider slider,
        MouseButtonEventArgs e)
    {
        if (interaction.IsPointerDown && !ReferenceEquals(interaction.ActiveSlider, slider))
        {
            CancelSliderPointerInteraction(interaction);
        }

        if (interaction.ActiveSlider is { } previous
            && !ReferenceEquals(previous, slider))
        {
            CloseSliderToolTip(interaction, previous);
        }

        GetSliderToolTipTimer(interaction).Stop();
        interaction.ActiveSlider = slider;
        interaction.IsPointerDown = true;
        interaction.IsThumbDrag = IsPointerOverSliderThumb(e, slider);
        interaction.OwnsMouseCapture = false;

        if (!interaction.IsThumbDrag)
        {
            SetSliderValueFromPointer(slider, e);
            interaction.OwnsMouseCapture = slider.CaptureMouse();
            e.Handled = true;
        }

        ShowSliderToolTip(interaction, slider, includeSeekPosition: true);
    }

    private void CompleteVolumeInteraction(Slider slider)
    {
        var interaction = _volumeSliderInteraction;
        if (!interaction.IsPointerDown || !ReferenceEquals(interaction.ActiveSlider, slider))
        {
            return;
        }

        EndSliderPointerInteraction(interaction, slider, releaseNativeCapture: false);
        ShowSliderToolTip(interaction, slider, includeSeekPosition: false);
        BeginSliderToolTipLinger(interaction);
    }

    private void CancelSliderPointerInteraction(SliderPointerInteraction interaction)
    {
        var slider = interaction.ActiveSlider;
        interaction.IsPointerDown = false;
        interaction.IsThumbDrag = false;
        if (slider is not null)
        {
            ReleaseSliderMouseCapture(interaction, slider, releaseNativeCapture: true);
        }

        HideSliderToolTip(interaction);
        if (interaction.Kind == SliderInteractionKind.Seek)
        {
            UpdateTimelineUi();
        }
    }

    private static void EndSliderPointerInteraction(
        SliderPointerInteraction interaction,
        Slider slider,
        bool releaseNativeCapture)
    {
        interaction.IsPointerDown = false;
        interaction.IsThumbDrag = false;
        ReleaseSliderMouseCapture(interaction, slider, releaseNativeCapture);
    }

    private static void ReleaseSliderMouseCapture(
        SliderPointerInteraction interaction,
        Slider slider,
        bool releaseNativeCapture)
    {
        var ownsMouseCapture = interaction.OwnsMouseCapture;
        interaction.OwnsMouseCapture = false;
        if (ownsMouseCapture && slider.IsMouseCaptured)
        {
            slider.ReleaseMouseCapture();
            return;
        }

        if (releaseNativeCapture && IsMouseCaptureWithinSlider(slider))
        {
            Mouse.Capture(null);
        }
    }

    private static bool IsMouseCaptureWithinSlider(Slider slider)
    {
        if (Mouse.Captured is not DependencyObject captured)
        {
            return false;
        }

        return ReferenceEquals(captured, slider) || IsVisualDescendantOf(captured, slider);
    }

    private async Task LoadCompactBurnDiscArtworkAsync()
    {
        try
        {
            var artwork = await CdArtworkComposer.ComposeDefaultAsync(_burnDiscArtworkCts.Token);
            if (!_burnDiscArtworkCts.IsCancellationRequested)
            {
                _defaultCompactBurnDiscArtwork = artwork;
                if (_burningWindow is null || Snapshot.HasSession)
                {
                    CompactBurnDiscImage.Source = artwork;
                }
            }
        }
        catch (OperationCanceledException) when (_burnDiscArtworkCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to compose the burn CD artwork: {ex}");
        }
    }

    private void CompactBurnSlot_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Snapshot.HasSession
            || _isBurnDiscPointerDown
            || _isBurnDiscInserting
            || _suppressBurnDiscHoverUntilLeave)
        {
            return;
        }

        CompactBurnSlot.Height = BurnDiscEjectedSurfaceHeight;
        AnimateCompactBurnDisc(
            BurnDiscEjectedOffsetY,
            BurnDiscEjectedAngle,
            TimeSpan.FromMilliseconds(280),
            EasingMode.EaseOut);
    }

    private void CompactBurnSlot_MouseLeave(object sender, MouseEventArgs e)
    {
        var wasSuppressingHover = _suppressBurnDiscHoverUntilLeave;
        _suppressBurnDiscHoverUntilLeave = false;
        if (_isBurnDiscPointerDown
            || _isBurnDiscDragging
            || _isBurnDiscInserting
            || wasSuppressingHover)
        {
            return;
        }

        AnimateCompactBurnDisc(
            BurnDiscRetractedOffsetY,
            0,
            TimeSpan.FromMilliseconds(220),
            EasingMode.EaseIn,
            collapseSurfaceWhenComplete: true);
    }

    private void CompactBurnSlot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Snapshot.HasSession || _isBurnDiscInserting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        _suppressBurnDiscHoverUntilLeave = false;
        if (!Mouse.Capture(CompactBurnSlot, CaptureMode.Element))
        {
            return;
        }

        _isBurnDiscPointerDown = true;
        _isBurnDiscDragging = false;
        _burnDiscAnimationGeneration++;
        CompactBurnDiscEject.BeginAnimation(TranslateTransform3D.OffsetYProperty, null);
        CompactBurnDiscSpin.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        CompactBurnSlot.Height = BurnDiscEjectedSurfaceHeight;
        CompactBurnDiscEject.OffsetY = BurnDiscEjectedOffsetY;
        CompactBurnDiscSpin.Angle = BurnDiscEjectedAngle;

        var point = e.GetPosition(this);
        var now = Stopwatch.GetTimestamp();
        _burnDiscDragStartY = point.Y;
        _burnDiscDragStartAngle = BurnDiscEjectedAngle;
        _burnDiscPullDistance = 0;
        _burnDiscLastPointerY = point.Y;
        _burnDiscReleaseVelocityY = 0;
        _burnDiscDragStartedAt = now;
        _burnDiscLastSampleAt = now;
    }

    private void CompactBurnSlot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isBurnDiscPointerDown)
        {
            return;
        }

        e.Handled = true;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CompleteCompactBurnDiscPointerInteraction(
                releaseMouseCapture: true,
                allowClickAction: false);
            return;
        }

        var point = e.GetPosition(this);
        var downwardDelta = point.Y - _burnDiscDragStartY;
        if (!_isBurnDiscDragging)
        {
            if (downwardDelta < SystemParameters.MinimumVerticalDragDistance)
            {
                UpdateBurnDiscVelocitySample(point.Y);
                return;
            }

            _isBurnDiscDragging = true;
            EnableCompactBurnDiscOverflow();
            CompactBurnSlot.Height = BurnDiscMaxSurfaceHeight;
            CompactBurnSlot.Cursor = Cursors.SizeAll;
            Mouse.OverrideCursor = Cursors.SizeAll;
        }

        _burnDiscPullDistance = Math.Clamp(
            downwardDelta - SystemParameters.MinimumVerticalDragDistance,
            0,
            BurnDiscMaxPullDistance);
        if (!_isBurnPresentationDetached
            && _burningWindow is not null
            && !Snapshot.HasSession
            && _burnDiscPullDistance >= BurnDiscPresentationDetachDistance)
        {
            DetachBurnPresentationForDiscRemoval();
        }

        CompactBurnDiscEject.OffsetY = Math.Max(
            BurnDiscMaxPulledOffsetY,
            BurnDiscEjectedOffsetY - (_burnDiscPullDistance * BurnDiscPullOffsetPerDip));
        CompactBurnDiscSpin.Angle = _burnDiscDragStartAngle
            + (_burnDiscPullDistance * BurnDiscPullSpinPerDip);

        UpdateBurnDiscVelocitySample(point.Y);
    }

    private void CompactBurnSlot_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isBurnDiscPointerDown)
        {
            return;
        }

        e.Handled = true;
        UpdateBurnDiscVelocitySample(e.GetPosition(this).Y);
        CompleteCompactBurnDiscPointerInteraction(releaseMouseCapture: true);
    }

    private void CompactBurnSlot_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isBurnDiscPointerDown)
        {
            CompleteCompactBurnDiscPointerInteraction(
                releaseMouseCapture: false,
                allowClickAction: false);
        }
    }

    private void UpdateBurnDiscVelocitySample(double pointerY)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _burnDiscLastSampleAt) / (double)Stopwatch.Frequency;
        if (elapsedSeconds is > 0 and <= 0.14)
        {
            var instantVelocity = (pointerY - _burnDiscLastPointerY) / elapsedSeconds;
            _burnDiscReleaseVelocityY = (_burnDiscReleaseVelocityY * 0.55) + (instantVelocity * 0.45);
        }
        else if (elapsedSeconds > 0.14)
        {
            _burnDiscReleaseVelocityY = 0;
        }

        _burnDiscLastPointerY = pointerY;
        _burnDiscLastSampleAt = now;
    }

    private void CompleteCompactBurnDiscPointerInteraction(
        bool releaseMouseCapture,
        bool allowClickAction = true)
    {
        if (!_isBurnDiscPointerDown)
        {
            return;
        }

        var wasDragging = _isBurnDiscDragging;
        var now = Stopwatch.GetTimestamp();
        var secondsSinceLastSample = (now - _burnDiscLastSampleAt) / (double)Stopwatch.Frequency;
        var releaseVelocityY = secondsSinceLastSample <= 0.14
            ? _burnDiscReleaseVelocityY
            : 0;
        var interactionSeconds = Math.Max(
            0.05,
            (now - _burnDiscDragStartedAt) / (double)Stopwatch.Frequency);

        _isBurnDiscPointerDown = false;
        _isBurnDiscDragging = false;
        CompactBurnSlot.Cursor = Cursors.Hand;
        Mouse.OverrideCursor = null;

        if (!wasDragging)
        {
            if (releaseMouseCapture && CompactBurnSlot.IsMouseCaptured)
            {
                Mouse.Capture(null);
            }

            var shouldOpenBurningWindow = allowClickAction && CompactBurnSlot.IsMouseOver;
            if (shouldOpenBurningWindow)
            {
                CompactBurnSlot.Height = BurnDiscEjectedSurfaceHeight;
                AnimateCompactBurnDisc(
                    BurnDiscEjectedOffsetY,
                    BurnDiscEjectedAngle,
                    TimeSpan.FromMilliseconds(120),
                    EasingMode.EaseOut);
            }
            else
            {
                AnimateCompactBurnDisc(
                    BurnDiscRetractedOffsetY,
                    0,
                    TimeSpan.FromMilliseconds(220),
                    EasingMode.EaseIn,
                    collapseSurfaceWhenComplete: true);
            }

            if (shouldOpenBurningWindow)
            {
                Dispatcher.BeginInvoke(
                    () => _ = OpenBurningWindowAsync(),
                    DispatcherPriority.Input);
            }
            return;
        }

        _isBurnDiscInserting = true;
        if (releaseMouseCapture && CompactBurnSlot.IsMouseCaptured)
        {
            Mouse.Capture(null);
        }
        _suppressBurnDiscHoverUntilLeave = CompactBurnSlot.IsMouseOver;

        var pullRate = _burnDiscPullDistance / interactionSeconds;
        var inwardFlickSpeed = Math.Max(0, -releaseVelocityY);
        var speed = Math.Max(pullRate, inwardFlickSpeed * 0.75);
        var speedFactor = Math.Clamp((speed - 45) / 430, 0, 1);
        var remainingPull = BurnDiscMaxPullDistance - _burnDiscPullDistance;
        var coastDistance = Math.Clamp(
            Math.Max(0, releaseVelocityY) * 0.035,
            0,
            Math.Min(12, remainingPull));
        AnimateCompactBurnDiscInsertion(coastDistance, speedFactor);
    }

    private void AnimateCompactBurnDiscInsertion(double coastDistance, double speedFactor)
    {
        var currentOffsetY = CompactBurnDiscEject.OffsetY;
        var currentAngle = CompactBurnDiscSpin.Angle;
        var coastMilliseconds = coastDistance > 0.25
            ? Math.Clamp(45 + (coastDistance * 3), 45, 80)
            : 0;
        var insertMilliseconds = 360 - (230 * speedFactor);
        var coastTime = TimeSpan.FromMilliseconds(coastMilliseconds);
        var totalTime = coastTime + TimeSpan.FromMilliseconds(insertMilliseconds);
        var coastOffsetY = Math.Max(
            BurnDiscMaxPulledOffsetY,
            currentOffsetY - (coastDistance * BurnDiscPullOffsetPerDip));
        var coastAngle = currentAngle + (coastDistance * BurnDiscPullSpinPerDip);
        var finalAngle = currentAngle + 96 + (150 * speedFactor);
        var generation = ++_burnDiscAnimationGeneration;

        var offsetAnimation = new DoubleAnimationUsingKeyFrames();
        var spinAnimation = new DoubleAnimationUsingKeyFrames();
        if (coastMilliseconds > 0)
        {
            var coastSpline = new KeySpline(0.1, 0.7, 0.25, 1);
            offsetAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(
                coastOffsetY,
                KeyTime.FromTimeSpan(coastTime),
                coastSpline));
            spinAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(
                coastAngle,
                KeyTime.FromTimeSpan(coastTime),
                coastSpline));
        }

        var insertSpline = new KeySpline(0.5, 0, 0.85, 0.45);
        offsetAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(
            BurnDiscRetractedOffsetY,
            KeyTime.FromTimeSpan(totalTime),
            insertSpline));
        spinAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(
            finalAngle,
            KeyTime.FromTimeSpan(totalTime),
            insertSpline));
        offsetAnimation.Completed += (_, _) =>
        {
            if (generation != _burnDiscAnimationGeneration)
            {
                return;
            }

            _isBurnDiscInserting = false;
            CompactBurnDiscEject.BeginAnimation(TranslateTransform3D.OffsetYProperty, null);
            CompactBurnDiscSpin.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
            CompactBurnDiscEject.OffsetY = BurnDiscRetractedOffsetY;
            CompactBurnDiscSpin.Angle = 0;
            CompactBurnSlot.Height = BurnDiscCollapsedSurfaceHeight;
            ClearStaleBurnDiscHoverSuppression();
            DisableCompactBurnDiscOverflow();
            RestoreBurnPresentationAfterDiscInsertion();
        };

        CompactBurnDiscEject.BeginAnimation(
            TranslateTransform3D.OffsetYProperty,
            offsetAnimation,
            HandoffBehavior.SnapshotAndReplace);
        CompactBurnDiscSpin.BeginAnimation(
            AxisAngleRotation3D.AngleProperty,
            spinAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ClearStaleBurnDiscHoverSuppression()
    {
        if (!_suppressBurnDiscHoverUntilLeave)
        {
            return;
        }

        var pointer = Mouse.GetPosition(CompactBurnSlot);
        if (pointer.X < 0
            || pointer.X > CompactBurnSlot.ActualWidth
            || pointer.Y < 0
            || pointer.Y > BurnDiscCollapsedSurfaceHeight)
        {
            _suppressBurnDiscHoverUntilLeave = false;
        }
    }

    private async Task OpenBurningWindowAsync(bool allowDuringPlayback = false)
    {
        if (_burningWindow is { } openBurningWindow)
        {
            if (_isBurningWindowHiddenByDiscRemoval || !openBurningWindow.IsVisible)
            {
                CancelBurnDiscReading();
                _isBurnPresentationDetached = false;
                _isBurningWindowHiddenByDiscRemoval = false;
                openBurningWindow.ShowAfterDiscInsertion();
                RefreshCompactBurnPresentation();
                return;
            }

            if (openBurningWindow.WindowState == WindowState.Minimized)
            {
                openBurningWindow.WindowState = WindowState.Normal;
            }

            openBurningWindow.Activate();
            return;
        }

        if (_isOpeningBurningWindow || (!allowDuringPlayback && Snapshot.HasSession))
        {
            return;
        }

        _isOpeningBurningWindow = true;
        // Suppress topmost only while the modal file picker is up; the player keeps
        // its always-on-top state for the whole life of the burning window.
        PushDisableTopmost();
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose an audio file to burn to a CD",
                CheckFileExists = true,
                Multiselect = false,
                Filter = "Audio files|*.mp3;*.mp2;*.mp1;*.flac;*.m4a;*.m4b;*.aac;*.ogg;*.oga;*.opus;*.wav;*.aif;*.aiff;*.wma;*.asf;*.ape;*.wv;*.mpc;*.mpp;*.webm;*.dsf;*.aa;*.aax|All files|*.*"
            };
            var pickerAccepted = IsVisible
                ? picker.ShowDialog(this)
                : picker.ShowDialog();
            if (pickerAccepted != true)
            {
                return;
            }

            Mouse.OverrideCursor = Cursors.Wait;
            var artworkLoader = new ImageArtworkLoader();
            var audioTagService = new AudioTagService();
            BurnTrackDraft draft;
            try
            {
                draft = await audioTagService.ReadAsync(picker.FileName);
            }
            catch (Exception ex) when (ex is InvalidDataException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "Unsupported audio file",
                    "The selected file is not a supported audio file.");
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            var burningWindow = new BurningWindow(
                draft,
                audioTagService,
                artworkLoader,
                _settingsService,
                _globeConnectionService)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            _burningWindow = burningWindow;
            CancelBurnDiscReading();
            _isBurnPresentationDetached = false;
            _isBurningWindowHiddenByDiscRemoval = false;
            burningWindow.Closed += BurningWindow_Closed;
            burningWindow.PresentationChanged += BurningWindow_PresentationChanged;
            burningWindow.TrackReplaced += BurningWindow_TrackReplaced;
            burningWindow.BurnCompleted += BurningWindow_BurnCompleted;
            burningWindow.CloseRequestCanceled += BurningWindow_CloseRequestCanceled;
            try
            {
                burningWindow.Show();
                burningWindow.Activate();
                RefreshCompactBurnPresentation();
            }
            catch
            {
                burningWindow.Closed -= BurningWindow_Closed;
                burningWindow.PresentationChanged -= BurningWindow_PresentationChanged;
                burningWindow.TrackReplaced -= BurningWindow_TrackReplaced;
                burningWindow.BurnCompleted -= BurningWindow_BurnCompleted;
                burningWindow.CloseRequestCanceled -= BurningWindow_CloseRequestCanceled;
                _burningWindow = null;
                throw;
            }
        }
        catch (Exception ex)
        {
            AppDialogWindow.ShowError(
                this,
                "Could not open the CD burner",
                ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ResetCompactBurnDisc();
            PopRestoreTopmost();
            _isOpeningBurningWindow = false;
        }
    }

    private void BurningWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is BurningWindow burningWindow)
        {
            burningWindow.Closed -= BurningWindow_Closed;
            burningWindow.PresentationChanged -= BurningWindow_PresentationChanged;
            burningWindow.TrackReplaced -= BurningWindow_TrackReplaced;
            burningWindow.BurnCompleted -= BurningWindow_BurnCompleted;
            burningWindow.CloseRequestCanceled -= BurningWindow_CloseRequestCanceled;
        }

        if (ReferenceEquals(_burningWindow, sender))
        {
            _burningWindow = null;
        }

        CancelBurnDiscReading();
        _isBurnPresentationDetached = false;
        _isBurningWindowHiddenByDiscRemoval = false;
        RefreshCompactBurnPresentation();
        if (_isWaitingForBurningWindowClose)
        {
            _isWaitingForBurningWindowClose = false;
            Dispatcher.BeginInvoke(PlayCloseAnimation, DispatcherPriority.Normal);
        }
    }

    private void BurningWindow_PresentationChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_burningWindow, sender))
        {
            RefreshCompactBurnPresentation();
        }
    }

    private void BurningWindow_TrackReplaced(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_burningWindow, sender))
        {
            return;
        }

        CancelBurnDiscReading();
        _isBurnPresentationDetached = false;
        _isBurningWindowHiddenByDiscRemoval = false;
        RefreshCompactBurnPresentation();
    }

    private void BurningWindow_BurnCompleted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_burningWindow, sender) || sender is not BurningWindow burningWindow)
        {
            return;
        }

        var details = string.Join(
            " — ",
            new[] { burningWindow.PresentationTitle, burningWindow.PresentationArtist }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        ShowNotification(
            "Your CD has been burned!",
            details,
            burningWindow.PresentationCover);
    }

    private void BurningWindow_CloseRequestCanceled(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_burningWindow, sender))
        {
            return;
        }

        if (!_isWaitingForBurningWindowClose)
        {
            if (_isBurningWindowHiddenByDiscRemoval && sender is BurningWindow hiddenBurningWindow)
            {
                Dispatcher.BeginInvoke(
                    hiddenBurningWindow.HideForDiscRemoval,
                    DispatcherPriority.Normal);
            }
            return;
        }

        _isWaitingForBurningWindowClose = false;
        _isExitingFromTray = false;
        if (_isBurningWindowHiddenByDiscRemoval && sender is BurningWindow burningWindow)
        {
            CancelBurnDiscReading();
            _isBurnPresentationDetached = false;
            _isBurningWindowHiddenByDiscRemoval = false;
            burningWindow.ShowAfterDiscInsertion();
            RefreshCompactBurnPresentation();
        }
    }

    private void RefreshCompactBurnPresentation()
    {
        var burningWindow = _burningWindow;
        var isBurningPresentationActive = !Snapshot.HasSession
            && burningWindow is not null
            && !_isBurnPresentationDetached;
        if (!isBurningPresentationActive)
        {
            TitleText.Text = Snapshot.Title;
            DescriptionText.Text = Snapshot.Description;
            DescriptionText.Visibility = Visibility.Visible;
            SetImageSourceIfChanged(ArtImage, Snapshot.CoverArt);
            SetImageSourceIfChanged(BlurredArtImage, Snapshot.CoverArt);
            ArtImage.BeginAnimation(OpacityProperty, null);
            ArtImage.Opacity = 1;
            ArtPlaceholderText.Visibility = Snapshot.CoverArt is null
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyArtworkTint(Snapshot.CoverArt);
            if (burningWindow is null || Snapshot.HasSession)
            {
                ResetBurnPresentationDiscArtwork();
            }
            return;
        }

        if (_isBurnPresentationReading)
        {
            TitleText.Text = "Reading...";
            DescriptionText.Text = string.Empty;
            DescriptionText.Visibility = Visibility.Collapsed;
            SetImageSourceIfChanged(ArtImage, null);
            SetImageSourceIfChanged(BlurredArtImage, null);
            ArtImage.BeginAnimation(OpacityProperty, null);
            ArtImage.Opacity = 1;
            ArtPlaceholderText.Visibility = Visibility.Visible;
            ApplyArtworkTint(null);
            _ = UpdateBurnPresentationDiscArtworkAsync(burningWindow!.PresentationDisc);
            return;
        }

        TitleText.Text = "Burning...";
        var details = string.Join(
            " — ",
            new[] { burningWindow!.PresentationTitle, burningWindow.PresentationArtist }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        DescriptionText.Text = details;
        DescriptionText.Visibility = details.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        var cover = burningWindow.PresentationCover;
        SetImageSourceIfChanged(ArtImage, cover);
        SetImageSourceIfChanged(BlurredArtImage, cover);
        ArtImage.BeginAnimation(OpacityProperty, null);
        ArtImage.Opacity = 1;
        ArtPlaceholderText.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;
        ApplyArtworkTint(cover);
        _ = UpdateBurnPresentationDiscArtworkAsync(burningWindow.PresentationDisc);
    }

    private async Task UpdateBurnPresentationDiscArtworkAsync(BitmapSource artwork)
    {
        if (ReferenceEquals(_burnPresentationDiscSource, artwork))
        {
            return;
        }

        _burnPresentationDiscSource = artwork;
        var generation = ++_burnPresentationArtworkGeneration;
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _burnPresentationArtworkCts, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        try
        {
            var composed = await CdArtworkComposer.ComposeAsync(artwork, cancellation.Token);
            if (generation == _burnPresentationArtworkGeneration
                && !cancellation.IsCancellationRequested
                && _burningWindow is not null
                && !Snapshot.HasSession)
            {
                CompactBurnDiscImage.Source = composed;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to compose the active burn CD artwork: {ex}");
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _burnPresentationArtworkCts, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void ResetBurnPresentationDiscArtwork()
    {
        _burnPresentationDiscSource = null;
        _burnPresentationArtworkGeneration++;
        var cancellation = Interlocked.Exchange(ref _burnPresentationArtworkCts, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (_defaultCompactBurnDiscArtwork is not null)
        {
            CompactBurnDiscImage.Source = _defaultCompactBurnDiscArtwork;
        }
    }

    private void AnimateCompactBurnDisc(
        double offsetY,
        double angle,
        TimeSpan duration,
        EasingMode easingMode,
        bool collapseSurfaceWhenComplete = false)
    {
        var generation = ++_burnDiscAnimationGeneration;
        var offsetAnimation = new DoubleAnimation(offsetY, duration)
        {
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
        if (collapseSurfaceWhenComplete)
        {
            offsetAnimation.Completed += (_, _) =>
            {
                if (generation == _burnDiscAnimationGeneration
                    && !_isBurnDiscPointerDown
                    && !_isBurnDiscInserting)
                {
                    CompactBurnSlot.Height = BurnDiscCollapsedSurfaceHeight;
                }
            };
        }

        CompactBurnDiscEject.BeginAnimation(
            TranslateTransform3D.OffsetYProperty,
            offsetAnimation,
            HandoffBehavior.SnapshotAndReplace);

        CompactBurnDiscSpin.BeginAnimation(
            AxisAngleRotation3D.AngleProperty,
            new DoubleAnimation(angle, duration)
            {
                EasingFunction = new CubicEase { EasingMode = easingMode }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void EnableCompactBurnDiscOverflow()
    {
        if (_isBurnDiscOverflowActive || _isExpanded || _isLyricsMode || _isFullscreen)
        {
            return;
        }

        _isBurnDiscOverflowActive = true;
        RootCard.Height = CompactHeight;
        RootCard.VerticalAlignment = VerticalAlignment.Top;
        ClearRoundedWindowRegion();
        SetWindowSize(CompactWidth, BurnDiscOverflowWindowHeight);
    }

    private void DisableCompactBurnDiscOverflow()
    {
        if (!_isBurnDiscOverflowActive)
        {
            return;
        }

        _isBurnDiscOverflowActive = false;
        SetWindowSize(CompactWidth, CompactHeight);
        RootCard.ClearValue(HeightProperty);
        RootCard.ClearValue(VerticalAlignmentProperty);
        UpdateLayout();
        ApplyRoundedWindowRegion();
    }

    private void ResetCompactBurnDisc()
    {
        _burnDiscAnimationGeneration++;
        _isBurnDiscPointerDown = false;
        _isBurnDiscDragging = false;
        _isBurnDiscInserting = false;
        _suppressBurnDiscHoverUntilLeave = false;
        CompactBurnSlot.Cursor = Cursors.Hand;
        Mouse.OverrideCursor = null;
        if (CompactBurnSlot.IsMouseCaptured)
        {
            Mouse.Capture(null);
        }

        CompactBurnDiscEject.BeginAnimation(TranslateTransform3D.OffsetYProperty, null);
        CompactBurnDiscSpin.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        CompactBurnDiscEject.OffsetY = BurnDiscRetractedOffsetY;
        CompactBurnDiscSpin.Angle = 0;
        CompactBurnSlot.Height = BurnDiscCollapsedSurfaceHeight;
        DisableCompactBurnDiscOverflow();
    }

    private void RestoreBurnPresentationAfterDiscInsertion()
    {
        if (!_isBurnPresentationDetached || _burningWindow is not { } burningWindow)
        {
            return;
        }

        _isBurnPresentationDetached = false;
        if (!_isBurningWindowHiddenByDiscRemoval)
        {
            RefreshCompactBurnPresentation();
            return;
        }

        BeginBurnDiscReading(burningWindow);
    }

    private void DetachBurnPresentationForDiscRemoval()
    {
        if (_isBurnPresentationDetached || _burningWindow is not { } burningWindow)
        {
            return;
        }

        CancelBurnDiscReading();
        _isBurnPresentationDetached = true;
        _isBurningWindowHiddenByDiscRemoval = true;
        burningWindow.HideForDiscRemoval();

        RefreshCompactBurnPresentation();
    }

    private void BeginBurnDiscReading(BurningWindow burningWindow)
    {
        CancelBurnDiscReading();
        _isBurnPresentationReading = true;
        RefreshCompactBurnPresentation();

        var cancellation = new CancellationTokenSource();
        _burnDiscReadingCts = cancellation;
        var generation = ++_burnDiscReadingGeneration;
        _ = CompleteBurnDiscReadingAsync(burningWindow, generation, cancellation);
    }

    private async Task CompleteBurnDiscReadingAsync(
        BurningWindow burningWindow,
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(BurnDiscReadingDelay, cancellation.Token);
            if (generation != _burnDiscReadingGeneration
                || cancellation.IsCancellationRequested
                || !ReferenceEquals(_burningWindow, burningWindow)
                || _isBurnPresentationDetached
                || !_isBurnPresentationReading)
            {
                return;
            }

            _isBurnPresentationReading = false;
            _isBurningWindowHiddenByDiscRemoval = false;
            burningWindow.ShowAfterDiscInsertion();
            RefreshCompactBurnPresentation();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _burnDiscReadingCts, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void CancelBurnDiscReading()
    {
        _isBurnPresentationReading = false;
        _burnDiscReadingGeneration++;
        var cancellation = Interlocked.Exchange(ref _burnDiscReadingCts, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private static ToolTip CreateSeekToolTip(Slider slider)
    {
        return CreateSliderToolTip("Seek", slider);
    }

    private static ToolTip CreateSliderToolTip(object content, Slider slider)
    {
        var toolTip = new ToolTip
        {
            Content = content,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            PlacementTarget = slider,
            StaysOpen = true,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                // Center the tooltip above the placement rectangle (the thumb).
                new System.Windows.Controls.Primitives.CustomPopupPlacement(
                    new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height - 5),
                    System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
            ]
        };
        return toolTip;
    }

    // The thumb is laid out by value fraction across (width - thumb width); mirror
    // that so the tooltip rides the pill instead of hovering over the whole bar.
    private static void UpdateSliderToolTipPlacement(Slider slider, ToolTip toolTip)
    {
        const double thumbWidth = 16;
        var range = slider.Maximum - slider.Minimum;
        var fraction = range > 0
            ? Math.Clamp((slider.Value - slider.Minimum) / range, 0, 1)
            : 0;
        var width = Math.Max(slider.ActualWidth, thumbWidth);
        var left = fraction * (width - thumbWidth);
        toolTip.PlacementRectangle = new Rect(left, 0, thumbWidth, slider.ActualHeight);
        if (toolTip.IsOpen)
        {
            // Popups only re-place themselves when an offset changes; nudge it so
            // the tooltip follows the thumb while dragging.
            toolTip.HorizontalOffset = toolTip.HorizontalOffset == 0 ? 0.01 : 0;
        }
    }

    private static void UpdateSeekToolTip(Slider slider, bool includePosition)
    {
        if (slider.ToolTip is not ToolTip toolTip)
        {
            slider.ToolTip = toolTip = CreateSeekToolTip(slider);
        }

        toolTip.Content = includePosition
            ? $"Seek: {FormatTime(TimeSpan.FromSeconds(slider.Value))}"
            : "Seek";
    }

    private ToolTip CreateVolumeToolTip(Slider slider)
    {
        return CreateSliderToolTip(FormatVolume(GetVolumeTitleValue(slider)), slider);
    }

    private void UpdateVolumeToolTip(Slider slider)
    {
        if (slider.ToolTip is not ToolTip toolTip)
        {
            slider.ToolTip = toolTip = CreateVolumeToolTip(slider);
        }

        toolTip.Content = FormatVolume(GetVolumeTitleValue(slider));
    }

    private void ShowSliderToolTip(
        SliderPointerInteraction interaction,
        Slider slider,
        bool includeSeekPosition)
    {
        GetSliderToolTipTimer(interaction).Stop();
        if (interaction.ActiveSlider is { } previous
            && !ReferenceEquals(previous, slider))
        {
            CloseSliderToolTip(interaction, previous);
        }

        interaction.ActiveSlider = slider;
        if (interaction.Kind == SliderInteractionKind.Seek)
        {
            UpdateSeekToolTip(slider, includeSeekPosition);
        }
        else
        {
            UpdateVolumeToolTip(slider);
        }

        if (slider.ToolTip is ToolTip toolTip)
        {
            toolTip.PlacementTarget = slider;
            UpdateSliderToolTipPlacement(slider, toolTip);
            toolTip.IsOpen = true;
        }
    }

    private void BeginSliderToolTipLinger(SliderPointerInteraction interaction)
    {
        var timer = GetSliderToolTipTimer(interaction);
        timer.Stop();
        timer.Start();
    }

    private void HideSliderToolTip(SliderPointerInteraction interaction)
    {
        GetSliderToolTipTimer(interaction).Stop();
        if (interaction.ActiveSlider is { } activeSlider)
        {
            CloseSliderToolTip(interaction, activeSlider);
        }

        if (!interaction.IsPointerDown)
        {
            interaction.ActiveSlider = null;
        }
    }

    private DispatcherTimer GetSliderToolTipTimer(SliderPointerInteraction interaction)
    {
        return interaction.Kind == SliderInteractionKind.Seek
            ? _seekToolTipHideTimer
            : _volumeToolTipHideTimer;
    }

    private static void ResetSliderToolTipContent(
        SliderPointerInteraction interaction,
        ToolTip toolTip)
    {
        if (interaction.Kind == SliderInteractionKind.Seek)
        {
            toolTip.Content = "Seek";
        }
    }

    private static void CloseSliderToolTip(
        SliderPointerInteraction interaction,
        Slider slider)
    {
        if (slider.ToolTip is ToolTip toolTip)
        {
            toolTip.IsOpen = false;
            ResetSliderToolTipContent(interaction, toolTip);
        }
    }

    private static string FormatVolume(double value)
    {
        return $"Volume: {(int)Math.Round(Math.Clamp(value, 0, 100))}%";
    }

    private double GetVolumeTitleValue(Slider slider)
    {
        if (_volumeService.IsAvailable)
        {
            return _volumeService.IsMuted ? 0 : _volumeService.Volume * 100;
        }

        return CompactVolumeSlider?.Value > 0 ? CompactVolumeSlider.Value : slider.Value;
    }

    private void OnSnapshotChanged(object? sender, MediaSnapshot snapshot)
    {
        Dispatcher.Invoke(() =>
        {
            var observedAt = DateTimeOffset.Now;
            var mediaKey = CreateTimelineMediaKey(snapshot);
            var stabilizedPosition = _timelineStabilizer.Observe(
                mediaKey,
                snapshot.HasSession,
                snapshot.Position,
                snapshot.Duration,
                snapshot.IsPlaying,
                snapshot.TimelineUpdatedAt,
                snapshot.HasReliableTimelineUpdatedAt,
                observedAt);
            if (snapshot.HasSession
                && snapshot.Duration <= TimeSpan.Zero
                && _timelineStabilizer.Duration > TimeSpan.Zero)
            {
                snapshot = snapshot with
                {
                    Position = stabilizedPosition,
                    Duration = _timelineStabilizer.Duration
                };
            }

            var didRestartPlayback = _timelineStabilizer.LastObservationWasPlaybackRestart;

            Snapshot = snapshot;
            if (didRestartPlayback)
            {
                ResetLyricsForPlaybackRestart();
            }

            ApplySnapshot(snapshot);
        });
    }

    private void ShowNotification(string title, string artist, ImageSource? coverArt)
    {
        if (!_settingsService.Settings.Behavior.EnableNotifications)
        {
            return;
        }

        var popup = new TrackNotificationWindow(title, artist, coverArt);
        popup.Show();
    }

    private void ApplySnapshot(MediaSnapshot snapshot)
    {
        if (snapshot.HasSession && !string.IsNullOrWhiteSpace(snapshot.Title))
        {
            if (snapshot.Title != _lastNotificationTrackTitle || snapshot.Description != _lastNotificationTrackArtist)
            {
                _lastNotificationTrackTitle = snapshot.Title;
                _lastNotificationTrackArtist = snapshot.Description;
                ShowNotification(snapshot.Title, snapshot.Description, snapshot.CoverArt);
            }
            else
            {
                // Update any existing notification popups with the latest cover art / tint when they settle
                foreach (var popup in Application.Current.Windows.OfType<TrackNotificationWindow>().ToArray())
                {
                    if (popup.TrackTitleText.Text == snapshot.Title &&
                        popup.TrackArtistText.Text == snapshot.Description)
                    {
                        popup.UpdateArtwork(snapshot.CoverArt);
                    }
                }
            }
        }
        else
        {
            _lastNotificationTrackTitle = string.Empty;
            _lastNotificationTrackArtist = string.Empty;
        }

        TitleText.Text = snapshot.Title;
        ExpandedTitleText.Text = snapshot.Title;
        LyricsTitleText.Text = snapshot.Title;
        DescriptionText.Text = snapshot.Description;
        ExpandedDescriptionText.Text = snapshot.Description;
        LyricsArtistText.Text = snapshot.Description;

        // Establish the outgoing animated-art shield before replacing the
        // static thumbnails underneath it. WPF presents these retained visuals
        // later, so tearing down the video first leaves a one-frame gap.
        RefreshAnimatedArtworkForSnapshot(snapshot);
        SetImageSourceIfChanged(ArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(BlurredArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(ExpandedArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(LyricsArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(LyricsHeaderArtImage, snapshot.CoverArt);
        _lastAppliedCoverArtFingerprint = snapshot.CoverArtFingerprint;
        ReleaseHeldArtworkWhenStaticCoverIsReady(snapshot);
        ArtImage.BeginAnimation(OpacityProperty, null);
        ArtImage.Opacity = 1;
        ArtPlaceholderText.Visibility = snapshot.CoverArt is null ? Visibility.Visible : Visibility.Collapsed;
        LyricsHeaderArtPlaceholderText.Visibility = snapshot.CoverArt is null ? Visibility.Visible : Visibility.Collapsed;
        AlbumArtSurface.ToolTip = _isMusicInfoMode
            ? null
            : snapshot.CoverArt is not null ? "Expand" : null;
        AlbumArtSurface.Cursor = !_isMusicInfoMode && snapshot.CoverArt is not null
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        ApplyArtworkTint(snapshot.CoverArt);
        if (_isMusicInfoMode)
        {
            if (!snapshot.HasSession || string.IsNullOrWhiteSpace(snapshot.Title))
            {
                CloseMusicInfoMode(animate: false, restorePreviousMode: false);
            }
            else
            {
                MusicInfoPanel.ShowTrack(snapshot);
            }
        }

        if (_isExpanded && snapshot.CoverArt is null)
        {
            SetExpandedMode(false);
        }

        var canGoFullscreen = snapshot.HasSession && !string.IsNullOrWhiteSpace(snapshot.Title) && snapshot.CoverArt != null;
        FullscreenButton.Visibility = canGoFullscreen ? Visibility.Visible : Visibility.Collapsed;

        if (_isFullscreen && !canGoFullscreen)
        {
            SetFullscreenMode(false);
        }

        UpdateTrackActionVisibility(snapshot);

        foreach (var button in PreviousButtons)
        {
            SetControlDisabledState(button, snapshot.CanPrevious);
        }

        foreach (var button in NextButtons)
        {
            SetControlDisabledState(button, snapshot.CanNext);
        }

        var canPlayPause = snapshot.CanPlay || snapshot.CanPause;
        foreach (var button in PlayPauseButtons)
        {
            SetControlDisabledState(button, canPlayPause);
        }

        SetPlayPauseButtonState(snapshot.IsPlaying);
        var canSeek = snapshot.CanSeek && snapshot.Duration > TimeSpan.Zero;
        foreach (var slider in ProgressSliders)
        {
            SetControlDisabledState(slider, canSeek);
            slider.Maximum = Math.Max(1, snapshot.Duration.TotalSeconds);
        }
        UpdateSyncedLyricLineSeekability(snapshot);

        CompactProgressRow.Visibility = snapshot.HasSession ? Visibility.Visible : Visibility.Collapsed;
        CompactBurnSlot.Visibility = snapshot.HasSession ? Visibility.Collapsed : Visibility.Visible;
        if (!snapshot.HasSession)
        {
            ShowVolumeSliders(false);
        }
        else
        {
            ResetCompactBurnDisc();
        }

        if (_isFullscreen)
        {
            SyncFullscreenPlaybackState();
        }

        RefreshCompactBurnPresentation();
        RefreshLyricsForSnapshot(snapshot);
        RefreshLastFmForSnapshot(snapshot);
        UpdateScrobblingForSnapshot(snapshot);
        UpdateTimelineUi();
    }

    private void HideCompactContentImmediately()
    {
        MediaPanel.BeginAnimation(OpacityProperty, null);
        ActionBar.BeginAnimation(OpacityProperty, null);
        MediaPanel.Opacity = 0;
        ActionBar.Opacity = 0;
        MediaPanel.IsHitTestVisible = false;
        ActionBar.IsHitTestVisible = false;
    }

    private void UpdateTrackActionVisibility(MediaSnapshot snapshot)
    {
        var showVolume = snapshot.HasSession && _volumeService.IsAvailable;
        var volumeVisibility = showVolume ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in VolumeButtons)
        {
            button.Visibility = volumeVisibility;
        }

        if (!showVolume)
        {
            ShowVolumeSliders(false);
        }

        var lyricsVisibility = ShouldShowLyricsButton(snapshot) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in LyricsButtons)
        {
            button.Visibility = lyricsVisibility;
        }
    }

    private bool ShouldShowLyricsButton(MediaSnapshot snapshot)
    {
        return snapshot.HasSession && Lyrics.Status is LyricsStatus.Loading or LyricsStatus.Synced or LyricsStatus.Plain;
    }

    private void SetPlayPauseButtonState(bool isPlaying)
    {
        var state = isPlaying ? "Pause" : "Play";
        var tooltip = isPlaying ? "Pause" : "Play";

        foreach (var button in PlayPauseButtons)
        {
            button.CommandParameter = state;
            button.ToolTip = tooltip;
            RefreshPlayPauseButtonImage(button);
        }
    }

    private static void SetControlDisabledState(FrameworkElement element, bool isEnabled)
    {
        element.IsEnabled = isEnabled;
        element.Opacity = isEnabled ? 1.0 : element is Slider ? 0.42 : 0.45;
        element.Cursor = isEnabled
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.No;
        element.ForceCursor = !isEnabled;

        if (element is Button button)
        {
            RefreshPlayPauseButtonImage(button);
        }
    }

    private static bool IsControlDisabled(object sender)
    {
        return sender is FrameworkElement { IsEnabled: false };
    }

    private void PlayPauseButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button && !IsControlDisabled(button))
        {
            RefreshPlayPauseButtonImage(button);
        }
    }

    private void PlayPauseButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            RefreshPlayPauseButtonImage(button);
        }
    }

    private void PlayPauseButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button && !IsControlDisabled(button))
        {
            SetPlayPauseButtonImage(button, "pressed");
        }
    }

    private void PlayPauseButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button && !IsControlDisabled(button))
        {
            RefreshPlayPauseButtonImage(button);
        }
    }

    private static void RefreshPlayPauseButtonImage(Button button)
    {
        var visualState = !IsControlDisabled(button) && button.IsPressed ? "pressed" : !IsControlDisabled(button) && button.IsMouseOver ? "hover" : "normal";
        SetPlayPauseButtonImage(button, visualState);
    }

    private static void SetPlayPauseButtonImage(Button button, string visualState)
    {
        if (button.Template?.FindName("PlayPauseIcon", button) is not Image image)
        {
            return;
        }

        var action = button.CommandParameter as string == "Pause" ? "pause" : "play";
        var suffix = visualState switch
        {
            _ when IsControlDisabled(button) => "_disabled",
            "hover" => "_hover",
            "pressed" => "_pressed",
            _ => string.Empty
        };

        image.Source = GetSiteImageSource($"Resources/Images/{action}{suffix}.png");
    }

    private static ImageSource GetSiteImageSource(string relativePath)
    {
        return IconImageSource.LoadSiteImage(relativePath);
    }

    private static void SetImageSourceIfChanged(Image image, ImageSource? source)
    {
        if (!ReferenceEquals(image.Source, source))
        {
            image.Source = source;
        }
    }

    private void RoundedSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        var radius = ReferenceEquals(element, RootCard) ? 9.0 : ReferenceEquals(element, GlassSurface) ? 9.0 : ReferenceEquals(element, ExpandedSurface) ? 9.0 : 5.0;
        if (_isFullscreen)
        {
            radius = 0.0;
        }
        ApplyRoundedSurfaceClip(element, e.NewSize, radius);

        if (ReferenceEquals(element, LyricsSurface))
        {
            CenterLyricsBackgroundArtwork(e.NewSize);
        }

        if (ReferenceEquals(element, RootCard))
        {
            ApplyRoundedWindowRegion();
        }
    }

    private static void ApplyRoundedSurfaceClip(FrameworkElement element, Size size, double radius)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var clip = new RectangleGeometry(new Rect(0, 0, size.Width, size.Height), radius, radius);
        if (clip.CanFreeze)
        {
            clip.Freeze();
        }

        element.Clip = clip;
    }

    private void CenterLyricsBackgroundArtwork(Size surfaceSize)
    {
        var backgroundSize = Math.Max(surfaceSize.Width, surfaceSize.Height);
        LyricsArtImage.Width = backgroundSize;
        LyricsArtImage.Height = backgroundSize;
    }

    private void FullscreenArtGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16.0, 16.0);
            if (clip.CanFreeze)
            {
                clip.Freeze();
            }

            element.Clip = clip;
        }
    }

    private void UpdateTimelineUi()
    {
        var playbackPosition = GetCurrentPosition();
        var duration = Snapshot.Duration;
        var position = _seekSliderInteraction.IsPointerDown
            && _seekSliderInteraction.ActiveSlider is { } activeSlider
            ? TimeSpan.FromSeconds(activeSlider.Value)
            : playbackPosition;

        if (!_seekSliderInteraction.IsPointerDown)
        {
            var progressValue = duration > TimeSpan.Zero
                ? Math.Clamp(position.TotalSeconds, 0, Math.Max(1, duration.TotalSeconds))
                : 0;
            foreach (var slider in ProgressSliders)
            {
                SetSliderValueIfChanged(slider, progressValue);
            }
        }

        var elapsedText = FormatTime(position);
        foreach (var textBlock in ElapsedTexts)
        {
            SetTextIfChanged(textBlock, elapsedText);
        }

        var remaining = duration > TimeSpan.Zero ? duration - position : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var remainingText = "-" + FormatTime(remaining);
        foreach (var textBlock in DurationTexts)
        {
            SetTextIfChanged(textBlock, remainingText);
        }

        UpdateSyncedLyricsUi(playbackPosition);

        if (_isFullscreen && _isFullscreenLyrics)
        {
            UpdateSyncedLyricsUi(playbackPosition, isFullscreen: true);
        }
    }

    private void TimelineCompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            UpdateTimelineUi();
        }
    }

    private void ResetLyricsForPlaybackRestart()
    {
        _lastLyricsPosition = TimeSpan.Zero;
        _activeLyricIndex = -1;
        _activeFullscreenLyricIndex = -1;
        _activeLyricWaitIndicatorIndex = -1;
        _activeFullscreenLyricWaitIndicatorIndex = -1;
        _isUserBrowsingLyrics = false;
        _isUserBrowsingFullscreenLyrics = false;
        _fullscreenLyricsInactivityTimer.Stop();
        ScrollLyricsToTop();

        if (Lyrics.Status == LyricsStatus.Synced)
        {
            ApplyLyricBlockVisualState(-1);
            ApplyLyricBlockVisualState(-1, isFullscreen: true);
            UpdateLyricWaitIndicators(TimeSpan.Zero);
            UpdateFullscreenLyricWaitIndicators(TimeSpan.Zero);
        }
    }

    private void ScrollLyricsToTop()
    {
        StopLyricsScrollAnimation();
        _lyricsScrollTarget = 0;
        _fullscreenLyricsScrollTarget = 0;
        LyricsScrollViewer.ScrollToVerticalOffset(0);
        FullscreenLyricsScrollViewer.ScrollToVerticalOffset(0);
    }

    private static void SetSliderValueIfChanged(Slider slider, double value)
    {
        if (Math.Abs(slider.Value - value) > 0.001)
        {
            slider.Value = value;
        }
    }

    private static void SetTextIfChanged(TextBlock textBlock, string text)
    {
        if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
        {
            textBlock.Text = text;
        }
    }

    private void RefreshLyricsForSnapshot(MediaSnapshot snapshot)
    {
        var key = LyricsService.CreateTrackKey(snapshot);
        if (key == _lyricsTrackKey)
        {
            return;
        }

        _lyricsTrackKey = key;
        _activeLyricIndex = -1;
        _lastLyricsPosition = TimeSpan.Zero;
        _lyricsRefreshCts?.Cancel();
        _lyricsRefreshCts?.Dispose();
        _lyricsRefreshCts = null;

        if (!snapshot.HasSession || string.IsNullOrWhiteSpace(snapshot.Title))
        {
            Lyrics = LyricsResult.Empty;
            RenderLyricsState();
            return;
        }

        Lyrics = LyricsResult.Loading;
        RenderLyricsState();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(32));
        _lyricsRefreshCts = cts;
        _ = LoadLyricsAsync(snapshot, key, cts.Token);
    }

    private async Task LoadLyricsAsync(MediaSnapshot snapshot, string key, CancellationToken cancellationToken)
    {
        try
        {
            var lyrics = await _lyricsService.GetLyricsAsync(snapshot, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_lyricsTrackKey != key || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Lyrics = lyrics;
                _activeLyricIndex = -1;
                RenderLyricsState();
                UpdateSyncedLyricsUi(GetCurrentPosition());
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_lyricsTrackKey != key)
                {
                    return;
                }

                Lyrics = LyricsResult.Error("Lyrics timed out");
                RenderLyricsState();
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_lyricsTrackKey != key || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Lyrics = LyricsResult.Error("Lyrics unavailable");
                RenderLyricsState();
            });
        }
    }

    private Image[] AnimatedArtOverlays =>
        [ArtVideoImage, ExpandedArtVideoImage, LyricsHeaderArtVideoImage, FullscreenArtVideoImage, MusicInfoPanel.AnimatedArtworkOverlay];

    private Image[] FrozenArtOverlays =>
        [ArtFrozenFrameImage, ExpandedArtFrozenFrameImage, LyricsHeaderArtFrozenFrameImage, FullscreenArtFrozenFrameImage, MusicInfoPanel.FrozenArtworkOverlay];

    private void RefreshAnimatedArtworkForSnapshot(MediaSnapshot snapshot)
    {
        var key = AnimatedArtworkService.CreateArtworkKey(snapshot);
        if (key == _animatedArtworkKey)
        {
            return;
        }

        var generation = ++_animatedArtworkGeneration;
        CancelHeldArtStaticRelease();
        _animatedArtworkKey = key;
        _animatedArtworkCts?.Cancel();
        _animatedArtworkCts?.Dispose();
        _animatedArtworkCts = null;

        // Keep the outgoing MediaPlayer alive on the top artwork layer. A
        // bitmap capture plus Close/Open is not a presentation barrier in WPF:
        // the video surface can disappear one compositor frame before the
        // captured image is visible.
        if (_isAnimatedArtPresented)
        {
            HoldAnimatedArtworkFrame();
        }
        else
        {
            ReleaseActiveAnimatedArtPlayer();
            if (_heldAnimatedArtPlayer is not null)
            {
                RestartFrozenArtHoldTimer();
            }
            else
            {
                ClearAnimatedArtOverlays();
            }
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        _animatedArtworkCts = cts;
        _ = LoadAnimatedArtworkAsync(snapshot, key, generation, cts.Token);
    }

    private async Task LoadAnimatedArtworkAsync(
        MediaSnapshot snapshot,
        string key,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await _animatedArtworkService.GetAnimatedArtworkAsync(snapshot, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_animatedArtworkGeneration != generation
                    || _animatedArtworkKey != key
                    || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (file is null)
                {
                    // A cached negative can belong to a provisional artist/album
                    // pair while Windows is still settling the new metadata and
                    // thumbnail. Static-cover readiness or the bounded hold
                    // timeout decides when the outgoing surface may leave.
                    return;
                }

                StartAnimatedArtwork(file, generation);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Animated artwork is a best-effort enhancement; static art stays visible.
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_animatedArtworkGeneration == generation
                    && _animatedArtworkKey == key
                    && _heldAnimatedArtPlayer is not null)
                {
                    RestartFrozenArtHoldTimer();
                }
            });
        }
    }

    private void StartAnimatedArtwork(string file, int generation)
    {
        // A resolved animated replacement takes precedence over a pending
        // static swap. Its separate player can preroll under the held frame.
        CancelHeldArtStaticRelease();
        ReleaseActiveAnimatedArtPlayer();
        EnsureAnimatedArtPlayer(generation);
        _animatedArtworkFile = file;
        if (_heldAnimatedArtPlayer is not null)
        {
            RestartFrozenArtHoldTimer();
        }

        _animatedArtPlayer!.Open(new Uri(file));
    }

    private void EnsureAnimatedArtPlayer(int generation)
    {
        if (_animatedArtPlayer is not null)
        {
            return;
        }

        var player = new MediaPlayer { IsMuted = true, Volume = 0, ScrubbingEnabled = true };
        player.MediaOpened += (_, _) => OnAnimatedArtPlayerOpened(player, generation);
        player.MediaEnded += (_, _) => OnAnimatedArtPlayerEnded(player, generation);
        player.MediaFailed += (_, _) => OnAnimatedArtPlayerFailed(player, generation);
        _animatedArtPlayer = player;
        _animatedArtSource = new DrawingImage(new VideoDrawing { Player = player, Rect = new Rect(0, 0, 1, 1) });
    }

    private void OnAnimatedArtPlayerOpened(MediaPlayer player, int generation)
    {
        if (!ReferenceEquals(player, _animatedArtPlayer)
            || generation != _animatedArtworkGeneration
            || _animatedArtworkFile is null
            || _animatedArtSource is null)
        {
            return;
        }

        if (_animatedArtSource.Drawing is VideoDrawing drawing && player.NaturalVideoWidth > 0)
        {
            drawing.Rect = new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight);
        }

        // Scrubbing renders the first frame while paused, so the reveal fades in
        // real footage instead of a not-yet-decoded black surface.
        player.Position = TimeSpan.FromMilliseconds(1);
        ScheduleAnimatedArtworkReveal(player, generation);
    }

    private void ScheduleAnimatedArtworkReveal(MediaPlayer player, int generation)
    {
        _animatedArtRevealTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(timer, _animatedArtRevealTimer))
            {
                _animatedArtRevealTimer = null;
            }

            RevealAnimatedArtwork(player, generation);
        };
        _animatedArtRevealTimer = timer;
        timer.Start();
    }

    private void RevealAnimatedArtwork(MediaPlayer player, int generation)
    {
        if (!ReferenceEquals(player, _animatedArtPlayer)
            || generation != _animatedArtworkGeneration
            || _animatedArtworkFile is null
            || _animatedArtSource is null)
        {
            return;
        }

        player.Play();
        var heldPlayer = _heldAnimatedArtPlayer;
        foreach (var overlay in AnimatedArtOverlays)
        {
            overlay.Source = _animatedArtSource;
            if (heldPlayer is not null)
            {
                // The held player above covers this source until WPF has
                // presented it on actual composition frames.
                overlay.BeginAnimation(OpacityProperty, null);
                overlay.Opacity = 1;
            }
            else
            {
                AnimateDouble(overlay, OpacityProperty, 1.0, 640);
            }
        }

        ScheduleAnimatedArtworkPresentation(player, heldPlayer, generation);
    }

    private void ScheduleAnimatedArtworkPresentation(
        MediaPlayer player,
        MediaPlayer? heldPlayer,
        int generation)
    {
        CancelAnimatedArtworkPresentation();
        _pendingAnimatedArtPresentationPlayer = player;
        _pendingAnimatedArtPresentationGeneration = generation;
        var remainingFrames = 2;
        TimeSpan? lastRenderingTime = null;
        EventHandler? handler = null;
        handler = (_, eventArgs) =>
        {
            if (eventArgs is RenderingEventArgs renderingArgs)
            {
                if (lastRenderingTime == renderingArgs.RenderingTime)
                {
                    return;
                }

                lastRenderingTime = renderingArgs.RenderingTime;
            }

            if (--remainingFrames > 0)
            {
                return;
            }

            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_animatedArtPresentationRenderingHandler, handler))
            {
                _animatedArtPresentationRenderingHandler = null;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (generation != _animatedArtworkGeneration
                    || !ReferenceEquals(player, _animatedArtPlayer)
                    || !ReferenceEquals(player, _pendingAnimatedArtPresentationPlayer)
                    || generation != _pendingAnimatedArtPresentationGeneration)
                {
                    return;
                }

                _pendingAnimatedArtPresentationPlayer = null;
                _pendingAnimatedArtPresentationGeneration = 0;
                _isAnimatedArtPresented = true;
                if (heldPlayer is not null
                    && ReferenceEquals(heldPlayer, _heldAnimatedArtPlayer))
                {
                    FadeOutFrozenArtOverlays();
                }
            }, DispatcherPriority.Background);
        };
        _animatedArtPresentationRenderingHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private void CancelAnimatedArtworkPresentation()
    {
        if (_animatedArtPresentationRenderingHandler is not null)
        {
            CompositionTarget.Rendering -= _animatedArtPresentationRenderingHandler;
            _animatedArtPresentationRenderingHandler = null;
        }

        _pendingAnimatedArtPresentationPlayer = null;
        _pendingAnimatedArtPresentationGeneration = 0;
    }

    // Track-change hand-off: pause the outgoing player and keep its live video
    // surface on the top layer. The incoming cover gets a separate MediaPlayer,
    // so no visible surface is ever closed or reused during the swap.
    private void HoldAnimatedArtworkFrame()
    {
        var player = _animatedArtPlayer;
        var source = _animatedArtSource;
        if (!_isAnimatedArtPresented || player is null || source is null)
        {
            ReleaseActiveAnimatedArtPlayer();
            if (_heldAnimatedArtPlayer is not null)
            {
                RestartFrozenArtHoldTimer();
            }
            return;
        }

        _animatedArtRevealTimer?.Stop();
        var previousHeldPlayer = _heldAnimatedArtPlayer;
        foreach (var overlay in FrozenArtOverlays)
        {
            overlay.BeginAnimation(OpacityProperty, null);
            overlay.Source = source;
            overlay.Opacity = 1;
        }

        _heldAnimatedArtPlayer = player;
        _heldAnimatedArtSource = source;
        _heldCoverArtFingerprint = _lastAppliedCoverArtFingerprint;
        _animatedArtPlayer = null;
        _animatedArtSource = null;
        _animatedArtworkFile = null;
        _isAnimatedArtPresented = false;
        player.Pause();

        // The normal video overlays deliberately keep showing this same source
        // underneath the held layer until the destination is ready.
        if (previousHeldPlayer is not null && !ReferenceEquals(previousHeldPlayer, player))
        {
            previousHeldPlayer.Close();
        }

        RestartFrozenArtHoldTimer();
    }

    private void RestartFrozenArtHoldTimer()
    {
        if (_heldAnimatedArtPlayer is not null && _heldAnimatedArtSource is not null)
        {
            // A rapid metadata change or failed incoming player can arrive while
            // the previous fade is in flight. Restore full coverage before
            // extending the hold for the newest generation.
            foreach (var overlay in FrozenArtOverlays)
            {
                overlay.BeginAnimation(OpacityProperty, null);
                overlay.Source = _heldAnimatedArtSource;
                overlay.Opacity = 1;
            }
        }

        _heldArtFadeEpoch++;
        _frozenArtHoldTimer?.Stop();
        var heldPlayer = _heldAnimatedArtPlayer;
        if (heldPlayer is null)
        {
            _frozenArtHoldTimer = null;
            return;
        }

        var timerGeneration = ++_heldArtTimerGeneration;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(timer, _frozenArtHoldTimer))
            {
                _frozenArtHoldTimer = null;
            }

            OnFrozenArtHoldTimeout(heldPlayer, timerGeneration);
        };
        _frozenArtHoldTimer = timer;
        timer.Start();
    }

    private void OnFrozenArtHoldTimeout(MediaPlayer heldPlayer, int timerGeneration)
    {
        if (timerGeneration != _heldArtTimerGeneration
            || !ReferenceEquals(heldPlayer, _heldAnimatedArtPlayer))
        {
            return;
        }

        // Last-resort fallback for a provider that never publishes a distinct
        // thumbnail. The normal path releases as soon as the new cover bytes
        // differ, without making the user wait for this timeout.
        if (_animatedArtPlayer is not null && !_isAnimatedArtPresented)
        {
            ReleaseActiveAnimatedArtPlayer();
            ClearAnimatedArtOverlays();
        }

        ScheduleHeldArtStaticRelease(
            heldPlayer,
            _animatedArtworkGeneration,
            expectedFingerprint: null,
            force: true);
    }

    private void ReleaseHeldArtworkWhenStaticCoverIsReady(MediaSnapshot snapshot)
    {
        var heldPlayer = _heldAnimatedArtPlayer;
        if (heldPlayer is null
            || snapshot.CoverArt is null
            || string.IsNullOrWhiteSpace(snapshot.CoverArtFingerprint)
            || string.IsNullOrWhiteSpace(_heldCoverArtFingerprint)
            || string.Equals(
                snapshot.CoverArtFingerprint,
                _heldCoverArtFingerprint,
                StringComparison.Ordinal))
        {
            CancelHeldArtStaticRelease();
            return;
        }

        ScheduleHeldArtStaticRelease(
            heldPlayer,
            _animatedArtworkGeneration,
            snapshot.CoverArtFingerprint,
            force: false);
    }

    private void ScheduleHeldArtStaticRelease(
        MediaPlayer heldPlayer,
        int generation,
        string? expectedFingerprint,
        bool force)
    {
        CancelHeldArtStaticRelease();
        var releaseGeneration = _heldArtStaticReleaseGeneration;
        var remainingFrames = 2;
        TimeSpan? lastRenderingTime = null;
        EventHandler? handler = null;
        handler = (_, eventArgs) =>
        {
            if (eventArgs is RenderingEventArgs renderingArgs)
            {
                if (lastRenderingTime == renderingArgs.RenderingTime)
                {
                    return;
                }

                lastRenderingTime = renderingArgs.RenderingTime;
            }

            if (--remainingFrames > 0)
            {
                return;
            }

            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_heldArtStaticReleaseRenderingHandler, handler))
            {
                _heldArtStaticReleaseRenderingHandler = null;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (generation != _animatedArtworkGeneration
                    || releaseGeneration != _heldArtStaticReleaseGeneration
                    || !ReferenceEquals(heldPlayer, _heldAnimatedArtPlayer))
                {
                    return;
                }

                if (expectedFingerprint is not null
                    && (!string.Equals(
                            expectedFingerprint,
                            _lastAppliedCoverArtFingerprint,
                            StringComparison.Ordinal)
                        || string.Equals(
                            expectedFingerprint,
                            _heldCoverArtFingerprint,
                            StringComparison.Ordinal)))
                {
                    return;
                }

                if (!force && _animatedArtPlayer is not null)
                {
                    // A cached animated destination is already opening or
                    // crossing its presentation barrier. Keep the known-good
                    // held surface until that player wins or fails.
                    return;
                }

                if (!_isAnimatedArtPresented)
                {
                    ClearAnimatedArtOverlays();
                }

                ReleaseHeldAnimatedArtPlayer(heldPlayer);
            }, DispatcherPriority.Background);
        };
        _heldArtStaticReleaseRenderingHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private void CancelHeldArtStaticRelease()
    {
        _heldArtStaticReleaseGeneration++;
        if (_heldArtStaticReleaseRenderingHandler is null)
        {
            return;
        }

        CompositionTarget.Rendering -= _heldArtStaticReleaseRenderingHandler;
        _heldArtStaticReleaseRenderingHandler = null;
    }

    // The freshly opened video surface may not have composited a frame yet, so
    // the paused outgoing player stays on top and dissolves only after the new
    // player has been attached and playing underneath it.
    private void FadeOutFrozenArtOverlays()
    {
        FadeOutHeldAnimatedArtwork(beginDelayMilliseconds: 160, durationMilliseconds: 360);
    }

    private void FadeOutHeldAnimatedArtwork(int beginDelayMilliseconds, int durationMilliseconds)
    {
        var heldPlayer = _heldAnimatedArtPlayer;
        if (heldPlayer is null)
        {
            return;
        }

        _frozenArtHoldTimer?.Stop();
        _frozenArtHoldTimer = null;
        _heldArtTimerGeneration++;
        var fadeEpoch = ++_heldArtFadeEpoch;
        var fade = new DoubleAnimation
        {
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(beginDelayMilliseconds),
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => ReleaseHeldAnimatedArtPlayer(heldPlayer, fadeEpoch);
        foreach (var overlay in FrozenArtOverlays)
        {
            overlay.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void ReleaseHeldAnimatedArtPlayer(MediaPlayer expectedPlayer, int? expectedFadeEpoch = null)
    {
        if (!ReferenceEquals(expectedPlayer, _heldAnimatedArtPlayer)
            || expectedFadeEpoch is not null && expectedFadeEpoch != _heldArtFadeEpoch)
        {
            return;
        }

        _heldArtFadeEpoch++;
        _heldArtTimerGeneration++;
        _heldAnimatedArtPlayer = null;
        _heldAnimatedArtSource = null;
        _heldCoverArtFingerprint = string.Empty;
        _frozenArtHoldTimer?.Stop();
        _frozenArtHoldTimer = null;
        CancelHeldArtStaticRelease();
        expectedPlayer.Close();
        foreach (var overlay in FrozenArtOverlays)
        {
            overlay.BeginAnimation(OpacityProperty, null);
            overlay.Source = null;
            overlay.Opacity = 0;
        }
    }

    private void OnAnimatedArtPlayerEnded(MediaPlayer player, int generation)
    {
        if (!ReferenceEquals(player, _animatedArtPlayer)
            || generation != _animatedArtworkGeneration
            || _animatedArtworkFile is null)
        {
            return;
        }

        // The cached file is remuxed into a seekable MP4, so looping is a plain
        // rewind on the warm pipeline.
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    private void OnAnimatedArtPlayerFailed(MediaPlayer player, int generation)
    {
        if (ReferenceEquals(player, _heldAnimatedArtPlayer))
        {
            if (!_isAnimatedArtPresented)
            {
                ClearAnimatedArtOverlays();
            }

            ReleaseHeldAnimatedArtPlayer(player);
            return;
        }

        if (!ReferenceEquals(player, _animatedArtPlayer)
            || generation != _animatedArtworkGeneration)
        {
            return;
        }

        var file = _animatedArtworkFile;
        if (_heldAnimatedArtPlayer is not null)
        {
            // Restore the last known-good surface before the failed incoming
            // player is detached from the layer underneath it.
            RestartFrozenArtHoldTimer();
        }

        ReleaseActiveAnimatedArtPlayer();
        ClearAnimatedArtOverlays();
        ReleaseHeldArtworkWhenStaticCoverIsReady(Snapshot);

        if (file is not null)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private void ReleaseActiveAnimatedArtPlayer()
    {
        _animatedArtRevealTimer?.Stop();
        _animatedArtRevealTimer = null;
        CancelAnimatedArtworkPresentation();
        var player = _animatedArtPlayer;
        _animatedArtPlayer = null;
        _animatedArtSource = null;
        _animatedArtworkFile = null;
        _isAnimatedArtPresented = false;
        player?.Close();
    }

    private void ClearAnimatedArtOverlays()
    {
        foreach (var overlay in AnimatedArtOverlays)
        {
            overlay.BeginAnimation(OpacityProperty, null);
            overlay.Source = null;
            overlay.Opacity = 0;
        }
    }

    private void StopAnimatedArtwork()
    {
        _animatedArtRevealTimer?.Stop();
        _animatedArtRevealTimer = null;
        _frozenArtHoldTimer?.Stop();
        _frozenArtHoldTimer = null;
        _heldArtFadeEpoch++;
        _heldArtTimerGeneration++;
        CancelAnimatedArtworkPresentation();
        CancelHeldArtStaticRelease();
        ReleaseActiveAnimatedArtPlayer();
        ClearAnimatedArtOverlays();

        var heldPlayer = _heldAnimatedArtPlayer;
        _heldAnimatedArtPlayer = null;
        _heldAnimatedArtSource = null;
        _heldCoverArtFingerprint = string.Empty;
        heldPlayer?.Close();
        foreach (var overlay in FrozenArtOverlays)
        {
            overlay.BeginAnimation(OpacityProperty, null);
            overlay.Source = null;
            overlay.Opacity = 0;
        }
    }

    private void RenderLyricsState()
    {
        LyricsStatusText.Text = Lyrics.Message;
        LyricsStackPanel.Children.Clear();
        _lyricBlocks.Clear();
        _lyricWaitIndicators.Clear();
        _lyricsFooterPanel = null;
        _activeLyricIndex = -1;
        _activeLyricWaitIndicatorIndex = -1;
        _isUserBrowsingLyrics = false;
        _lastLyricsPosition = TimeSpan.Zero;
        StopLyricsScrollAnimation();
        LyricsScrollViewer.ScrollToVerticalOffset(0);
        SetLyricsMessageIcon(Lyrics.Status);

        RenderFullscreenLyricsState();

        var hasLyrics = Lyrics.Status is LyricsStatus.Synced or LyricsStatus.Plain;
        var isLoading = Lyrics.Status == LyricsStatus.Loading;
        var lyricsButtonVisibility = ShouldShowLyricsButton(Snapshot) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in LyricsButtons)
        {
            button.Visibility = lyricsButtonVisibility;
        }

        if (Lyrics.Status == LyricsStatus.Synced)
        {
            LyricsMessagePanel.Visibility = Visibility.Collapsed;
            RenderSyncedLyrics(LyricsStackPanel, _lyricBlocks, _lyricWaitIndicators, isFullscreen: false);
            AddLyricsInfoFooter();
            Dispatcher.BeginInvoke(() => UpdateSyncedLyricsUi(GetCurrentPosition()), DispatcherPriority.Loaded);
            if (_isFullscreen && _isFullscreenLyrics)
            {
                Dispatcher.BeginInvoke(() => UpdateSyncedLyricsUi(GetCurrentPosition(), isFullscreen: true), DispatcherPriority.Loaded);
            }
            return;
        }

        if (Lyrics.Status == LyricsStatus.Plain)
        {
            LyricsMessagePanel.Visibility = Visibility.Collapsed;
            RenderPlainLyrics(LyricsStackPanel, _lyricBlocks, isFullscreen: false);
            AddLyricsInfoFooter();
            return;
        }

        LyricsMessagePanel.Visibility = Visibility.Visible;
        LyricsMessageText.Text = Lyrics.Message;

        if (_isLyricsMode && !isLoading)
        {
            SetLyricsMode(false);
        }

        if (_isFullscreen)
        {
            SetFullscreenLyricsVisible(_isFullscreenLyrics && hasLyrics);
        }
    }

    private void LoadLoadingIconFrames()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Resources", "Images");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(directory, "Windows 7 Busy_page_*.png").OrderBy(static file => file))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _loadingIconFrames.Add(image);
        }

        if (_loadingIconFrames.Count > 0)
        {
            LyricsLoadingIcon.Source = _loadingIconFrames[0];
            FullscreenLyricsLoadingIcon.Source = _loadingIconFrames[0];
        }
    }

    private void SetLyricsMessageIcon(LyricsStatus status)
    {
        var isLoading = status == LyricsStatus.Loading && _loadingIconFrames.Count > 0;
        LyricsLoadingIcon.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        LyricsWarningIcon.Visibility = status == LyricsStatus.NotFound ? Visibility.Visible : Visibility.Collapsed;
        FullscreenLyricsLoadingIcon.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        FullscreenLyricsWarningIcon.Visibility = status == LyricsStatus.NotFound ? Visibility.Visible : Visibility.Collapsed;

        if (isLoading)
        {
            if (LyricsLoadingIcon.Source is null)
            {
                LyricsLoadingIcon.Source = _loadingIconFrames[0];
            }
            if (FullscreenLyricsLoadingIcon.Source is null)
            {
                FullscreenLyricsLoadingIcon.Source = _loadingIconFrames[0];
            }

            if (!_loadingIconTimer.IsEnabled)
            {
                _loadingIconTimer.Start();
            }
        }
        else
        {
            _loadingIconTimer.Stop();
        }
    }

    private void AdvanceLoadingIconFrame()
    {
        if (_loadingIconFrames.Count == 0)
        {
            _loadingIconTimer.Stop();
            return;
        }

        _loadingIconFrameIndex = (_loadingIconFrameIndex + 1) % _loadingIconFrames.Count;
        LyricsLoadingIcon.Source = _loadingIconFrames[_loadingIconFrameIndex];
        FullscreenLyricsLoadingIcon.Source = _loadingIconFrames[_loadingIconFrameIndex];
    }

    private static TextBlock AddLyricBlock(
        Panel panel,
        ICollection<TextBlock> blocks,
        string text,
        double fontSize,
        double opacity,
        Thickness margin,
        Color foreground)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(foreground),
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Margin = margin,
            Opacity = opacity,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        blocks.Add(block);
        panel.Children.Add(block);
        return block;
    }

    private void AddLyricWaitIndicatorIfNeeded(
        int lineIndex,
        TimeSpan previousLineTime,
        TimeSpan nextLineTime,
        Panel panel,
        ICollection<LyricWaitIndicator> indicators,
        bool isFullscreen)
    {
        var gap = nextLineTime - previousLineTime;
        if (gap < LyricWaitMinimumGap)
        {
            return;
        }

        var start = lineIndex == 0
            ? TimeSpan.Zero
            : previousLineTime + LyricWaitAfterLineDelay;
        var end = nextLineTime - LyricActivationLead;

        if (end <= start || end - start < LyricWaitMinimumDuration)
        {
            return;
        }

        AddLyricWaitIndicator(start, end, lineIndex, panel, indicators, isFullscreen);
    }

    private void AddLyricWaitIndicator(
        TimeSpan start,
        TimeSpan end,
        int lineIndex,
        Panel targetPanel,
        ICollection<LyricWaitIndicator> indicators,
        bool isFullscreen)
    {
        var indicatorPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Height = isFullscreen ? 24 : 18,
            Margin = isFullscreen ? new Thickness(0, 22, 0, 22) : new Thickness(0, 16, 0, 12),
            Opacity = isFullscreen ? 0.0 : 0.24,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var dotBrushes = new List<SolidColorBrush>();
        for (var i = 0; i < 3; i++)
        {
            var brush = new SolidColorBrush(Color.FromRgb(94, 101, 101));
            dotBrushes.Add(brush);

            indicatorPanel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = isFullscreen ? 7.5 : 5.5,
                Height = isFullscreen ? 7.5 : 5.5,
                Margin = new Thickness(i == 0 ? 0 : isFullscreen ? 9 : 7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = brush,
                SnapsToDevicePixels = true
            });
        }

        var indicator = new LyricWaitIndicator(start, end, indicatorPanel, dotBrushes, isFullscreen ? lineIndex : -1);
        SetLyricWaitIndicatorProgress(indicator, 0);
        indicators.Add(indicator);
        targetPanel.Children.Add(indicatorPanel);
    }

    private void UpdateLyricWaitIndicators(TimeSpan position)
    {
        UpdateLyricWaitIndicators(
            _lyricWaitIndicators,
            position,
            ref _activeLyricWaitIndicatorIndex,
            _activeLyricIndex,
            _isUserBrowsingLyrics,
            isFullscreen: false);
    }

    private void UpdateLyricWaitIndicators(
        IReadOnlyList<LyricWaitIndicator> indicators,
        TimeSpan position,
        ref int activeWaitIndicatorIndex,
        int activeLyricIndex,
        bool isUserBrowsing,
        bool isFullscreen)
    {
        if (indicators.Count == 0)
        {
            return;
        }

        var activeIndex = -1;
        for (var i = 0; i < indicators.Count; i++)
        {
            var indicator = indicators[i];
            if (position >= indicator.Start && position < indicator.End)
            {
                activeIndex = i;
            }

            SetLyricWaitIndicatorProgress(indicator, GetLyricWaitProgress(indicator, position));
        }

        var changed = activeIndex != activeWaitIndicatorIndex;
        activeWaitIndicatorIndex = activeIndex;

        if (isFullscreen || changed || !IsLoaded)
        {
            for (var i = 0; i < indicators.Count; i++)
            {
                var targetOpacity = isFullscreen
                    ? GetFullscreenWaitIndicatorOpacity(indicators[i], i == activeIndex, activeLyricIndex, isUserBrowsing)
                    : i == activeIndex && !isUserBrowsing
                        ? 1.0
                        : position < indicators[i].Start ? 0.28 : 0.18;
                AnimateDouble(indicators[i].Element, OpacityProperty, targetOpacity, 220);
            }
        }
    }

    private static double GetFullscreenWaitIndicatorOpacity(
        LyricWaitIndicator indicator,
        bool isActive,
        int activeLyricIndex,
        bool isUserBrowsing)
    {
        if (isActive)
        {
            return 1.0;
        }

        if (activeLyricIndex < 0)
        {
            return 0.0;
        }

        if (isUserBrowsing)
        {
            return 0.28;
        }

        if (indicator.LineIndex < activeLyricIndex)
        {
            return 0.0;
        }

        return indicator.LineIndex == activeLyricIndex ? 0.28 : 0.18;
    }

    private static double GetLyricWaitProgress(LyricWaitIndicator indicator, TimeSpan position)
    {
        if (position <= indicator.Start)
        {
            return 0;
        }

        if (position >= indicator.End)
        {
            return 1;
        }

        var duration = (indicator.End - indicator.Start).TotalMilliseconds;
        if (duration <= 0)
        {
            return 1;
        }

        return Math.Clamp((position - indicator.Start).TotalMilliseconds / duration, 0, 1);
    }

    private static void SetLyricWaitIndicatorProgress(LyricWaitIndicator indicator, double progress)
    {
        var dim = Color.FromRgb(94, 101, 101);
        var bright = Color.FromRgb(246, 249, 244);

        for (var i = 0; i < indicator.DotBrushes.Count; i++)
        {
            var dotProgress = Math.Clamp((progress * indicator.DotBrushes.Count) - i, 0, 1);
            dotProgress = dotProgress * dotProgress * (3 - (2 * dotProgress));
            var brush = indicator.DotBrushes[i];
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = Blend(dim, bright, dotProgress);
        }
    }

    private void AddLyricsInfoFooter()
    {
        var panel = AddLyricsInfoFooter(LyricsStackPanel, isFullscreen: false);
        if (panel is null)
        {
            return;
        }

        _lyricsFooterPanel = panel;
        LyricsStackPanel.Children.Add(new Border
        {
            Height = LyricsEndTailSpacerHeight,
            IsHitTestVisible = false,
            Opacity = 0
        });
    }

    private void AddFullscreenLyricsInfoFooter()
    {
        _fullscreenLyricsFooterPanel = AddLyricsInfoFooter(FullscreenLyricsStackPanel, isFullscreen: true);
    }

    private StackPanel? AddLyricsInfoFooter(Panel targetPanel, bool isFullscreen)
    {
        var infoLines = BuildLyricsInfoLines();
        if (infoLines.Count == 0)
        {
            return null;
        }

        var panel = new StackPanel
        {
            Margin = isFullscreen ? new Thickness(0, 24, 0, 24) : new Thickness(0, 22, 0, 24),
            Opacity = 0.84,
            HorizontalAlignment = isFullscreen ? HorizontalAlignment.Left : HorizontalAlignment.Stretch
        };

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 12),
            Background = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF))
        };
        if (isFullscreen)
        {
            divider.Width = 350;
            divider.HorizontalAlignment = HorizontalAlignment.Left;
        }
        panel.Children.Add(divider);

        foreach (var (label, value) in infoLines)
        {
            var block = new TextBlock
            {
                Margin = isFullscreen ? new Thickness(0, 4, 0, 4) : new Thickness(0, 3, 0, 3),
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 228)),
                FontSize = isFullscreen ? 13.5 : 12.6,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Effect = TryFindResource("TextShadow") as System.Windows.Media.Effects.Effect
            };

            block.Inlines.Add(new Run(label + ": ")
            {
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 247))
            });
            block.Inlines.Add(new Run(value));
            panel.Children.Add(block);
        }

        targetPanel.Children.Add(panel);
        return panel;
    }

    private List<(string Label, string Value)> BuildLyricsInfoLines()
    {
        var lines = new List<(string Label, string Value)>();
        var lyricsInfo = Lyrics.TrackInfo;
        var trackName = FirstNonEmpty(lyricsInfo?.TrackName, CurrentLastFmInfo?.TrackName, Snapshot.Title);
        var artistName = FirstNonEmpty(
            lyricsInfo?.ArtistName,
            CurrentLastFmInfo?.ArtistName,
            LastFmMetadataCleaner.CreateQuery(Snapshot).ArtistName,
            Snapshot.Artist);
        var albumName = FirstNonEmpty(lyricsInfo?.AlbumName, Snapshot.Album);
        var duration = lyricsInfo is { Duration: > 0 }
            ? TimeSpan.FromSeconds(lyricsInfo.Duration)
            : Snapshot.Duration;
        var source = FirstNonEmpty(lyricsInfo?.SourceName);

        AddInfoLine(lines, "Song", trackName);
        AddInfoLine(lines, "Artist", artistName);
        AddInfoLine(lines, "Album", albumName);
        if (duration > TimeSpan.Zero)
        {
            AddInfoLine(lines, "Duration", FormatTime(duration));
        }

        AddInfoLine(lines, "Lyrics From", source);
        return lines;
    }

    private static void AddInfoLine(List<(string Label, string Value)> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add((label, value.Trim()));
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private void SetPlainLyricStyle(IEnumerable<TextBlock> blocks)
    {
        foreach (var block in blocks)
        {
            block.Foreground = new SolidColorBrush(Color.FromRgb(221, 226, 226));
            block.FontWeight = FontWeights.SemiBold;
            block.Margin = new Thickness(0, 10, 0, 10);
            block.Opacity = 0.72;
        }
    }

    private void UpdateSyncedLyricsUi(TimeSpan position, bool isFullscreen = false)
    {
        if (Lyrics.Status != LyricsStatus.Synced || Lyrics.SyncedLines.Count == 0)
        {
            return;
        }

        var stabilizedPosition = StabilizeLyricPosition(position);
        if (isFullscreen)
        {
            UpdateFullscreenLyricWaitIndicators(stabilizedPosition);
        }
        else
        {
            UpdateLyricWaitIndicators(stabilizedPosition);
        }

        var activeIndex = FindActiveLyricIndex(Lyrics.SyncedLines, stabilizedPosition + LyricActivationLead);

        if (isFullscreen)
        {
            if (activeIndex == _activeFullscreenLyricIndex)
            {
                return;
            }

            _isUserBrowsingFullscreenLyrics = false;
            _fullscreenLyricsInactivityTimer.Stop();
            _activeFullscreenLyricIndex = activeIndex;
            ApplyLyricBlockVisualState(activeIndex, isFullscreen: true);

            Dispatcher.BeginInvoke(() => CenterActiveLyric(activeIndex, isFullscreen: true), DispatcherPriority.Loaded);

            return;
        }

        if (activeIndex == _activeLyricIndex)
        {
            return;
        }

        SetActiveLyricIndex(activeIndex);
    }

    private TimeSpan StabilizeLyricPosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (!Snapshot.IsPlaying)
        {
            _lastLyricsPosition = position;
            return position;
        }

        if (position < _lastLyricsPosition)
        {
            var regression = _lastLyricsPosition - position;
            if (regression < LyricRegressionTolerance)
            {
                return _lastLyricsPosition;
            }
        }

        _lastLyricsPosition = position;
        return position;
    }

    private static int FindActiveLyricIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        var low = 0;
        var high = lines.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (lines[mid].Time <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private void SetActiveLyricIndex(int activeIndex)
    {
        _activeLyricIndex = activeIndex;
        _isUserBrowsingLyrics = false;
        ApplyLyricBlockVisualState(activeIndex);

        Dispatcher.BeginInvoke(() => CenterActiveLyric(activeIndex), DispatcherPriority.Loaded);
    }

    private void ApplyLyricBlockVisualState(int activeIndex, bool isFullscreen = false)
    {
        var blocks = isFullscreen ? _fullscreenLyricBlocks : _lyricBlocks;
        var isUserBrowsing = isFullscreen ? _isUserBrowsingFullscreenLyrics : _isUserBrowsingLyrics;
        var shouldHidePreviousFinalLine = !isFullscreen && !isUserBrowsing && activeIndex == blocks.Count - 1;

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var distance = activeIndex < 0 ? 4 : Math.Abs(i - activeIndex);
            var isActive = i == activeIndex;
            var targetOpacity = isFullscreen
                ? isActive ? 1.0 : isUserBrowsing || i > activeIndex ? 0.35 : 0.0
                : shouldHidePreviousFinalLine && i == activeIndex - 1
                    ? 0.0
                    : isActive ? 1.0 : distance == 1 ? 0.48 : distance == 2 ? 0.28 : 0.18;
            var targetColor = isFullscreen
                ? isActive ? Color.FromRgb(255, 255, 255) : Color.FromRgb(180, 185, 185)
                : isActive
                    ? Color.FromRgb(246, 249, 244)
                    : distance == 1
                        ? Color.FromRgb(150, 158, 158)
                        : Color.FromRgb(94, 101, 101);

            if (block.Foreground is SolidColorBrush existingBrush && !existingBrush.IsFrozen)
            {
                AnimateBrushColor(existingBrush, targetColor);
            }
            else
            {
                block.Foreground = new SolidColorBrush(targetColor);
            }

            block.FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold;
            AnimateDouble(block, OpacityProperty, targetOpacity, isFullscreen && isUserBrowsing ? 150 : 380);
        }
    }

    private void CenterActiveLyric(int activeIndex, bool isFullscreen = false)
    {
        var blocks = isFullscreen ? _fullscreenLyricBlocks : _lyricBlocks;
        var scrollViewer = isFullscreen ? FullscreenLyricsScrollViewer : LyricsScrollViewer;
        if (scrollViewer.ViewportHeight <= 1)
        {
            return;
        }

        if (activeIndex < 0)
        {
            AnimateLyricsScrollTo(0, isFullscreen);
            return;
        }

        if (activeIndex >= blocks.Count)
        {
            return;
        }

        CenterLyricsElement(blocks[activeIndex], isFullscreen);
    }

    private void CenterLyricsElement(FrameworkElement element, bool isFullscreen = false)
    {
        var scrollViewer = isFullscreen ? FullscreenLyricsScrollViewer : LyricsScrollViewer;
        var stackPanel = isFullscreen ? FullscreenLyricsStackPanel : LyricsStackPanel;
        if (scrollViewer.ViewportHeight <= 1)
        {
            return;
        }

        try
        {
            var artCenterY = isFullscreen ? GetCurrentArtCenterY() : 0;
            stackPanel.UpdateLayout();
            var elementTop = element.TransformToVisual(stackPanel).Transform(new Point(0, 0)).Y;
            var elementExtent = element.ActualHeight;
            var max = GetLyricsScrollMaxOffset(isFullscreen);
            var target = isFullscreen
                ? elementTop + (elementExtent * 0.5) - artCenterY
                : LyricsStackVerticalPadding + elementTop + (elementExtent * 0.5) - (scrollViewer.ViewportHeight * 0.43);
            AnimateLyricsScrollTo(Math.Clamp(target, 0, max), isFullscreen);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void LyricsViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        BrowseLyrics(e, isFullscreen: false);
    }

    private void BrowseLyrics(MouseWheelEventArgs e, bool isFullscreen)
    {
        var blocks = isFullscreen ? _fullscreenLyricBlocks : _lyricBlocks;
        if (Lyrics.Status is not (LyricsStatus.Synced or LyricsStatus.Plain) || blocks.Count == 0)
        {
            return;
        }

        e.Handled = true;
        StopLyricsScrollAnimation();

        if (isFullscreen)
        {
            _isUserBrowsingFullscreenLyrics = true;
            _fullscreenLyricsInactivityTimer.Stop();
            _fullscreenLyricsInactivityTimer.Start();
            ApplyLyricBlockVisualState(_activeFullscreenLyricIndex, isFullscreen: true);
        }
        else
        {
            _isUserBrowsingLyrics = true;
            if (Lyrics.Status == LyricsStatus.Synced)
            {
                ApplyLyricBlockVisualState(_activeLyricIndex);
            }
        }

        var scrollViewer = isFullscreen ? FullscreenLyricsScrollViewer : LyricsScrollViewer;
        var step = isFullscreen ? 60 : 42;
        var target = scrollViewer.VerticalOffset + (e.Delta > 0 ? -step : step);
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, GetLyricsScrollMaxOffset(isFullscreen)));
    }

    private void AnimateLyricsScrollTo(double target, bool isFullscreen = false)
    {
        if (isFullscreen)
        {
            _fullscreenLyricsScrollTarget = Math.Clamp(target, 0, GetLyricsScrollMaxOffset(isFullscreen: true));
        }
        else
        {
            _lyricsScrollTarget = Math.Clamp(target, 0, GetLyricsScrollMaxOffset());
        }

        if (!_isLyricsScrollAnimating)
        {
            StartLyricsScrollAnimation();
        }
    }

    private void StartLyricsScrollAnimation()
    {
        if (_isLyricsScrollAnimating)
        {
            return;
        }

        _isLyricsScrollAnimating = true;
        CompositionTarget.Rendering += LyricsScrollCompositionTarget_Rendering;
    }

    private void StopLyricsScrollAnimation()
    {
        if (!_isLyricsScrollAnimating)
        {
            return;
        }

        _isLyricsScrollAnimating = false;
        CompositionTarget.Rendering -= LyricsScrollCompositionTarget_Rendering;
    }

    private void LyricsScrollCompositionTarget_Rendering(object? sender, EventArgs e)
    {
        UpdateLyricsScrollAnimation();
    }

    private void UpdateLyricsScrollAnimation()
    {
        if (!_isFullscreen)
        {
            var current = LyricsScrollViewer.VerticalOffset;
            _lyricsScrollTarget = Math.Clamp(_lyricsScrollTarget, 0, GetLyricsScrollMaxOffset());
            var remaining = _lyricsScrollTarget - current;

            if (Math.Abs(remaining) < 0.3)
            {
                LyricsScrollViewer.ScrollToVerticalOffset(_lyricsScrollTarget);
                StopLyricsScrollAnimation();
                return;
            }

            var newOffset = current + remaining * 0.12;
            LyricsScrollViewer.ScrollToVerticalOffset(newOffset);
        }
        else
        {
            var current = FullscreenLyricsScrollViewer.VerticalOffset;
            _fullscreenLyricsScrollTarget = Math.Clamp(_fullscreenLyricsScrollTarget, 0, GetLyricsScrollMaxOffset(isFullscreen: true));
            var remaining = _fullscreenLyricsScrollTarget - current;

            if (Math.Abs(remaining) < 0.3)
            {
                FullscreenLyricsScrollViewer.ScrollToVerticalOffset(_fullscreenLyricsScrollTarget);
                StopLyricsScrollAnimation();
                return;
            }

            var newOffset = current + remaining * 0.12;
            FullscreenLyricsScrollViewer.ScrollToVerticalOffset(newOffset);
        }
    }

    private double GetLyricsScrollMaxOffset(bool isFullscreen = false)
    {
        var scrollViewer = isFullscreen ? FullscreenLyricsScrollViewer : LyricsScrollViewer;
        var stackPanel = isFullscreen ? FullscreenLyricsStackPanel : LyricsStackPanel;
        var footerPanel = isFullscreen ? _fullscreenLyricsFooterPanel : _lyricsFooterPanel;
        var max = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
        if (footerPanel is null || scrollViewer.ViewportHeight <= 1)
        {
            return max;
        }

        try
        {
            var topOffset = isFullscreen ? GetCurrentArtCenterY() : 12;
            stackPanel.UpdateLayout();
            var footerTop = footerPanel.TransformToAncestor(stackPanel).Transform(new Point(0, 0)).Y;
            return Math.Clamp(footerTop - topOffset, 0, max);
        }
        catch (InvalidOperationException)
        {
            return max;
        }
    }

    private double GetCurrentArtCenterY()
    {
        try
        {
            if (FullscreenLyricsScrollViewer.IsLoaded && FullscreenLyricsScrollViewer.ViewportHeight > 1)
            {
                var artCenterPoint = FullscreenArtBorder.TransformToVisual(FullscreenLyricsScrollViewer)
                    .Transform(new Point(FullscreenArtBorder.ActualWidth * 0.5, FullscreenArtBorder.ActualHeight * 0.5));
                var artCenterY = artCenterPoint.Y;

                if (_fullscreenLyricsTopSpacer != null && Math.Abs(_fullscreenLyricsTopSpacer.Height - artCenterY) > 0.1)
                {
                    _fullscreenLyricsTopSpacer.Height = artCenterY;
                }
                if (_fullscreenLyricsBottomSpacer != null && Math.Abs(_fullscreenLyricsBottomSpacer.Height - artCenterY) > 0.1)
                {
                    _fullscreenLyricsBottomSpacer.Height = artCenterY;
                }

                return artCenterY;
            }
        }
        catch (InvalidOperationException)
        {
        }
        return 430.0;
    }

    private static void AnimateDouble(DependencyObject target, DependencyProperty property, double value, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = value,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (target is UIElement element)
        {
            element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        else if (target is Animatable animatable)
        {
            animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void AlbumArt_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_isMusicInfoMode || Snapshot.CoverArt is null)
        {
            return;
        }

        AnimateElementOpacity(AlbumExpandHint, 1, 130);
    }

    private void AlbumArt_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateElementOpacity(AlbumExpandHint, 0, 130);
    }

    private void AlbumArt_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void AlbumArt_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isMusicInfoMode)
        {
            CloseMusicInfoMode();
            return;
        }

        if (Snapshot.CoverArt is not null)
        {
            SetExpandedMode(true);
        }
    }

    private void CollapseExpandedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMusicInfoMode)
        {
            CloseMusicInfoMode();
            return;
        }

        SetExpandedMode(false);
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        if (_isExpanded)
        {
            SetExpandedInfoVisible(true);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_isBurnDiscPointerDown || _isBurnDiscDragging || _isBurnDiscInserting)
        {
            ResetCompactBurnDisc();
            RestoreBurnPresentationAfterDiscInsertion();
        }

        if (_isExpanded)
        {
            SetExpandedInfoVisible(false);
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateElementOpacity(ChromeButtons, 1, 150);
        if (_isExpanded)
        {
            SetExpandedInfoVisible(true);
        }
        ShowVolumeSliders(true);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateElementOpacity(ChromeButtons, 0, 250);
        if (_isExpanded)
        {
            SetExpandedInfoVisible(false);
        }
        ShowVolumeSliders(false);
    }

    private void SetExpandedMode(bool isExpanded)
    {
        if (isExpanded && _isMusicInfoMode)
        {
            CloseMusicInfoMode(animate: false, restorePreviousMode: false);
        }

        if (_isLyricsMode && isExpanded)
        {
            return;
        }

        if (isExpanded && Snapshot.CoverArt is null)
        {
            return;
        }

        if (_isExpanded == isExpanded || _isMinimizing)
        {
            return;
        }

        _isExpanded = isExpanded;

        if (isExpanded)
        {
            SetCompactChromeVisible(false);
            AnimateElementOpacity(AlbumExpandHint, 0, 90);
            CollapseExpandedButton.Visibility = Visibility.Visible;
            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedPanel.IsHitTestVisible = true;
            SetExpandedInfoVisible(true, true);
            SetWindowSize(ExpandedSize, ExpandedSize);
            AnimateExpandedPanel(true);
        }
        else
        {
            CollapseExpandedButton.Visibility = Visibility.Collapsed;
            SetExpandedInfoVisible(false);
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(205),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += (_, _) =>
            {
                if (!_isExpanded)
                {
                    ExpandedPanel.Visibility = Visibility.Collapsed;
                    ExpandedPanel.IsHitTestVisible = false;
                    SetWindowSize(CompactWidth, CompactHeight);
                    SetCompactChromeVisible(true);
                }
            };
            ExpandedPanel.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            AnimateExpandedPanel(false);
        }
    }

    private void LyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMusicInfoMode)
        {
            CloseMusicInfoMode(animate: false, restorePreviousMode: false);
        }

        SetLyricsMode(true);
    }

    private void CloseLyricsModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetLyricsMode(false);
    }

    private void SetLyricsMode(bool isLyricsMode)
    {
        if (_isLyricsMode == isLyricsMode || _isMinimizing)
        {
            return;
        }

        if (isLyricsMode)
        {
            _restoreExpandedAfterLyrics = _isExpanded;
            if (_isExpanded)
            {
                CollapseExpandedImmediatelyForLyrics();
            }

            _isLyricsMode = true;
            SetCompactChromeVisible(false);
            UpdateCompactBlurredArtVisibility();
            CollapseExpandedButton.Visibility = Visibility.Collapsed;
            LyricsPanel.Visibility = Visibility.Visible;
            LyricsPanel.IsHitTestVisible = true;
            LyricsPanelScale.ScaleX = 0.97;
            LyricsPanelScale.ScaleY = 0.97;
            SetWindowSize(LyricsWidth, LyricsHeight);
            AnimateLyricsPanel(true);
            RenderLyricsState();
            return;
        }

        _isLyricsMode = false;
        LyricsPanel.IsHitTestVisible = false;
        var restoreExpanded = _restoreExpandedAfterLyrics;
        _restoreExpandedAfterLyrics = false;

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            if (_isLyricsMode)
            {
                return;
            }

            LyricsPanel.Visibility = Visibility.Collapsed;
            if (restoreExpanded)
            {
                SetExpandedMode(true);
            }
            else
            {
                SetWindowSize(CompactWidth, CompactHeight);
                SetCompactChromeVisible(true);
            }

            // Restore the blurred backdrop only after the window is back at its
            // target size, so it never appears mid-resize at the wrong offset.
            UpdateCompactBlurredArtVisibility();
        };

        LyricsPanel.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        LyricsPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, fade.Duration.TimeSpan)
        {
            EasingFunction = fade.EasingFunction
        }, HandoffBehavior.SnapshotAndReplace);
        LyricsPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, fade.Duration.TimeSpan)
        {
            EasingFunction = fade.EasingFunction
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void CollapseExpandedImmediatelyForLyrics()
    {
        _isExpanded = false;
        CollapseExpandedButton.Visibility = Visibility.Collapsed;
        ExpandedPanel.BeginAnimation(OpacityProperty, null);
        ExpandedPanel.Opacity = 0;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.IsHitTestVisible = false;
        ExpandedPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExpandedPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ExpandedPanelScale.ScaleX = 1;
        ExpandedPanelScale.ScaleY = 1;
        SetExpandedInfoVisible(false, true);
    }

    private void AnimateLyricsPanel(bool isEntering)
    {
        var duration = TimeSpan.FromMilliseconds(isEntering ? 230 : 190);
        var easing = new CubicEase { EasingMode = isEntering ? EasingMode.EaseOut : EasingMode.EaseInOut };
        var targetOpacity = isEntering ? 1 : 0;
        var targetScale = isEntering ? 1 : 0.97;

        LyricsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        LyricsPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
        LyricsPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(targetScale, duration)
        {
            EasingFunction = easing
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetExpandedInfoVisible(bool isVisible, bool immediate = false)
    {
        ExpandedInfoLayer.IsHitTestVisible = isVisible;

        if (immediate || !IsLoaded)
        {
            ExpandedInfoLayer.BeginAnimation(OpacityProperty, null);
            ExpandedInfoLayer.Opacity = isVisible ? 1 : 0;
            return;
        }

        AnimateElementOpacity(ExpandedInfoLayer, isVisible ? 1 : 0, isVisible ? 180 : 220);
    }

    private void SetCompactChromeVisible(bool isVisible)
    {
        if (!isVisible)
        {
            HideCompactContentImmediately();
            return;
        }

        MediaPanel.IsHitTestVisible = true;
        ActionBar.IsHitTestVisible = true;
        AnimateElementOpacity(MediaPanel, 1, 170);
        AnimateElementOpacity(ActionBar, 1, 170);
    }

    private void ApplyMusicInfoControlsOnlyPresentation()
    {
        if (_musicInfoControlsOnlyPresentation)
        {
            return;
        }

        _musicInfoControlsOnlyPresentation = true;
        _musicInfoRootBackground = RootCard.Background;
        _musicInfoGlassBackground = GlassSurface.Background;
        _musicInfoActionBackground = ActionBar.Background;
        _musicInfoTitleBarChromeBackground = InfoTitleBarChrome.Background;
        _musicInfoTitleBarChromeBorderBrush = InfoTitleBarChrome.BorderBrush;
        _musicInfoRootBorderThickness = RootCard.BorderThickness;
        _musicInfoActionBorderThickness = ActionBar.BorderThickness;
        _musicInfoTitleBarChromeBorderThickness = InfoTitleBarChrome.BorderThickness;
        _musicInfoTitleBarMargin = TitleBar.Margin;
        _musicInfoBlurredArtVisibility = BlurredArtImage.Visibility;
        _musicInfoGlassGlowVisibility = CompactGlassGlowOverlay.Visibility;
        _musicInfoGlassGlossVisibility = CompactGlassGloss.Visibility;
        _musicInfoInnerBorderVisibility = InnerGlassBorder.Visibility;
        _musicInfoMediaPanelVisibility = MediaPanel.Visibility;

        CompactTitleRow.Height = new GridLength(25);
        CompactMediaRow.Height = new GridLength(MusicInfoTitleBarGapHeight);
        CompactControlsRow.Height = new GridLength(1, GridUnitType.Star);
        RootCard.Background = Brushes.Transparent;
        RootCard.BorderThickness = new Thickness(0);
        GlassSurface.Background = Brushes.Transparent;
        ActionBar.Background = Brushes.Transparent;
        ActionBar.BorderThickness = new Thickness(0);
        TitleBar.Margin = new Thickness(12, 0, 12, 0);
        InfoTitleBarChrome.Background = new SolidColorBrush(Color.FromArgb(0x78, 0x10, 0x18, 0x1D));
        InfoTitleBarChrome.BorderBrush = new SolidColorBrush(Color.FromArgb(0x72, 0xCF, 0xE2, 0xE8));
        InfoTitleBarChrome.BorderThickness = new Thickness(1, 1, 1, 0);
        BlurredArtImage.Visibility = Visibility.Collapsed;
        CompactGlassGlowOverlay.Visibility = Visibility.Collapsed;
        CompactGlassGloss.Visibility = Visibility.Collapsed;
        InnerGlassBorder.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Collapsed;
        TitleBar.Visibility = Visibility.Visible;
        TitleBar.IsHitTestVisible = true;
        ChromeButtons.BeginAnimation(OpacityProperty, null);
        ChromeButtons.Opacity = 1;
        ChromeButtons.IsHitTestVisible = true;
        ActionBar.BeginAnimation(OpacityProperty, null);
        ActionBar.Opacity = 1;
        ActionBar.IsHitTestVisible = true;
    }

    private void RestoreCompactPresentationAfterMusicInfo()
    {
        if (!_musicInfoControlsOnlyPresentation)
        {
            return;
        }

        RootCard.Background = _musicInfoRootBackground;
        RootCard.BorderThickness = _musicInfoRootBorderThickness;
        GlassSurface.Background = _musicInfoGlassBackground;
        ActionBar.Background = _musicInfoActionBackground;
        ActionBar.BorderThickness = _musicInfoActionBorderThickness;
        TitleBar.Margin = _musicInfoTitleBarMargin;
        InfoTitleBarChrome.Background = _musicInfoTitleBarChromeBackground;
        InfoTitleBarChrome.BorderBrush = _musicInfoTitleBarChromeBorderBrush;
        InfoTitleBarChrome.BorderThickness = _musicInfoTitleBarChromeBorderThickness;
        BlurredArtImage.Visibility = _musicInfoBlurredArtVisibility;
        CompactGlassGlowOverlay.Visibility = _musicInfoGlassGlowVisibility;
        CompactGlassGloss.Visibility = _musicInfoGlassGlossVisibility;
        InnerGlassBorder.Visibility = _musicInfoInnerBorderVisibility;
        MediaPanel.Visibility = _musicInfoMediaPanelVisibility;
        CompactTitleRow.Height = new GridLength(25);
        CompactMediaRow.Height = new GridLength(88);
        CompactControlsRow.Height = new GridLength(1, GridUnitType.Star);
        _musicInfoRootBackground = null;
        _musicInfoGlassBackground = null;
        _musicInfoActionBackground = null;
        _musicInfoTitleBarChromeBackground = null;
        _musicInfoTitleBarChromeBorderBrush = null;
        _musicInfoControlsOnlyPresentation = false;
    }

    private void SetWindowSize(double width, double height)
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = width;
        Height = height;
    }

    private void AnimateExpandedPanel(bool isEntering)
    {
        var duration = TimeSpan.FromMilliseconds(isEntering ? 190 : 205);
        var easing = new CubicEase { EasingMode = isEntering ? EasingMode.EaseOut : EasingMode.EaseInOut };
        var fromScale = isEntering ? 0.965 : 1.0;
        var toScale = isEntering ? 1.0 : 0.965;

        ExpandedPanelScale.ScaleX = fromScale;
        ExpandedPanelScale.ScaleY = fromScale;
        ExpandedPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(toScale, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        ExpandedPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(toScale, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);

        if (isEntering)
        {
            ExpandedPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static void AnimateElementOpacity(UIElement element, double opacity, int milliseconds)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void PlayOpenAnimation()
    {
        RootCard.Opacity = 0;
        WindowScale.ScaleX = 0.96;
        WindowScale.ScaleY = 0.96;

        var duration = TimeSpan.FromMilliseconds(190);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyArtworkTint(BitmapSource? coverArt)
    {
        if (_hasAppliedArtworkTint && ReferenceEquals(_lastArtworkTintSource, coverArt))
        {
            return;
        }

        _hasAppliedArtworkTint = true;
        _lastArtworkTintSource = coverArt;
        var tint = ResolvePlayerTint(
            GetEffectivePlayerThemeColor(),
            ExtractDominantTint(coverArt),
            DefaultTint);

        AnimateColor(CardTopStop, WithAlpha(Blend(tint, Colors.White, 0.20), 0x90));
        AnimateColor(CardUpperStop, WithAlpha(Blend(tint, Colors.White, 0.04), 0x82));
        AnimateColor(CardLowerStop, WithAlpha(Blend(tint, Colors.Black, 0.35), 0x76));
        AnimateColor(CardBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.22), 0x84));
        AnimateColor(GlowPrimaryStop, WithAlpha(Blend(tint, Colors.White, 0.56), 0x76));
        AnimateColor(GlowSecondaryStop, WithAlpha(Blend(tint, Colors.White, 0.10), 0x1C));
        AnimateColor(ActionBarTopStop, WithAlpha(Blend(tint, Colors.White, 0.20), 0x2F));
        AnimateColor(ActionBarBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.40), 0x34));
        var playbackTop = WithAlpha(Blend(tint, Colors.Black, 0.42), 0xA8);
        var playbackUpper = WithAlpha(Blend(tint, Colors.Black, 0.56), 0x84);
        var playbackCrease = WithAlpha(Blend(tint, Colors.Black, 0.74), 0x7A);
        var playbackBottom = WithAlpha(Blend(tint, Colors.Black, 0.50), 0x90);
        var playbackBorder = WithAlpha(Blend(tint, Colors.White, 0.38), 0x74);
        AnimateColor(LegacyPlaybackTopStop, playbackTop);
        AnimateColor(LegacyPlaybackUpperStop, playbackUpper);
        AnimateColor(LegacyPlaybackCreaseStop, playbackCrease);
        AnimateColor(LegacyPlaybackBottomStop, playbackBottom);
        AnimateColor(ExpandedTintTopStop, WithAlpha(Blend(tint, Colors.Black, 0.86), 0x18));
        AnimateColor(ExpandedTintMidStop, WithAlpha(Blend(tint, Colors.Black, 0.82), 0x2A));
        AnimateColor(ExpandedTintLowerStop, WithAlpha(Blend(tint, Colors.Black, 0.78), 0x76));
        AnimateColor(ExpandedTintBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.88), 0xE4));
        AnimateColor(ExpandedControlsTintMidStop, WithAlpha(Blend(tint, Colors.Black, 0.78), 0x78));
        AnimateColor(ExpandedControlsTintBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.88), 0xD8));
        AnimateBrushColor(MediaPanel.Background, WithAlpha(Blend(tint, Colors.Black, 0.45), 0x30));
        AnimateBrushColor(RootCard.BorderBrush, WithAlpha(Blend(tint, Colors.White, 0.62), 0xA5));
        AnimateBrushColor(LegacyPlaybackShell.BorderBrush, playbackBorder);
        AnimateColor(LyricsTintTopStop, WithAlpha(Blend(tint, Colors.Black, 0.72), 0xC6));
        AnimateColor(LyricsTintMidStop, WithAlpha(Blend(tint, Colors.Black, 0.68), 0xB8));
        AnimateColor(LyricsTintBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.82), 0xD3));
        AnimateColor(FullscreenTintTopStop, WithAlpha(Blend(tint, Colors.Black, 0.70), 0xFF));
        AnimateColor(FullscreenTintMidStop, WithAlpha(Blend(tint, Colors.Black, 0.85), 0xFF));
        AnimateColor(FullscreenTintBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.95), 0xFF));
        AnimateColor(FullscreenGlowStop, WithAlpha(Blend(tint, Colors.White, 0.40), 0xFF));
    }

    internal static Color ResolvePlayerTint(
        string? playerThemeColor,
        Color? artworkTint,
        Color defaultTint)
    {
        return AppearanceSettings.TryParsePlayerThemeColor(
            playerThemeColor,
            out var red,
            out var green,
            out var blue)
            ? Color.FromRgb(red, green, blue)
            : artworkTint ?? defaultTint;
    }

    internal static bool ShouldShowPlayerArtworkBackdrops(string? playerThemeColor)
    {
        return !AppearanceSettings.TryParsePlayerThemeColor(
            playerThemeColor,
            out _,
            out _,
            out _);
    }

    private string? _playerThemeColorPreview;

    /// <summary>
    /// Settings-driven preview: overrides the saved theme color on the live
    /// player until called with null (picker closed without saving, settings
    /// window closed, or the color saved for real).
    /// </summary>
    internal void PreviewPlayerThemeColor(Color? color)
    {
        _playerThemeColorPreview = color is { } value
            ? AppearanceSettings.FormatPlayerThemeColor(value.R, value.G, value.B)
            : null;
        ApplyPlayerAppearance();
    }

    private string GetEffectivePlayerThemeColor()
    {
        return _playerThemeColorPreview ?? _settingsService.Settings.Appearance.PlayerThemeColor;
    }

    private void ApplyPlayerAppearance()
    {
        var playerThemeColor = GetEffectivePlayerThemeColor();
        var backdropVisibility = ShouldShowPlayerArtworkBackdrops(playerThemeColor)
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateCompactBlurredArtVisibility();
        LyricsArtImage.Visibility = backdropVisibility;
        FullscreenBlurredArtImage.Visibility = backdropVisibility;

        var currentTintSource = _lastArtworkTintSource ?? Snapshot.CoverArt;
        _hasAppliedArtworkTint = false;
        _lastArtworkTintSource = null;
        ApplyArtworkTint(currentTintSource);
    }

    // The lyrics panel is translucent and paints its own blurred-art layer, so the
    // compact blur beneath it must hide while lyrics mode is open or the cover art
    // shows twice. Hidden (not Collapsed) keeps its layout warm so it reappears
    // without an arrange jump after the window resizes back.
    private void UpdateCompactBlurredArtVisibility()
    {
        if (_isMusicInfoMode)
        {
            BlurredArtImage.Visibility = Visibility.Collapsed;
            return;
        }

        var showBackdrops = ShouldShowPlayerArtworkBackdrops(GetEffectivePlayerThemeColor());
        BlurredArtImage.Visibility = !showBackdrops
            ? Visibility.Collapsed
            : _isLyricsMode
                ? Visibility.Hidden
                : Visibility.Visible;
    }

    private void AnimateColor(GradientStop stop, Color color)
    {
        if (!IsLoaded)
        {
            stop.Color = color;
            return;
        }

        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation
        {
            To = color,
            Duration = TimeSpan.FromMilliseconds(520),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateBrushColor(Brush brush, Color color)
    {
        if (brush is not SolidColorBrush solidBrush)
        {
            return;
        }

        if (!IsLoaded)
        {
            solidBrush.Color = color;
            return;
        }

        solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = color,
            Duration = TimeSpan.FromMilliseconds(520),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private TimeSpan GetCurrentPosition()
    {
        if (!Snapshot.HasSession || Snapshot.Duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return _timelineStabilizer.GetPosition(DateTimeOffset.Now);
    }

    private static string CreateTimelineMediaKey(MediaSnapshot snapshot)
    {
        if (!snapshot.HasSession)
        {
            return string.Empty;
        }

        return string.Join(
            '\u001f',
            snapshot.SourceApp.Trim(),
            snapshot.Title.Trim(),
            snapshot.Artist.Trim(),
            snapshot.Album.Trim());
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsControlDisabled(sender)) return;
        await _mediaService.TogglePlayPauseAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsControlDisabled(sender)) return;
        await _mediaService.NextAsync();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsControlDisabled(sender)) return;
        await _mediaService.PreviousAsync();
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.Behavior.AlwaysOnTop =
            !_settingsService.Settings.Behavior.AlwaysOnTop;
        _settingsService.Save(_settingsService.Settings);
    }

    private void SetTopmostUi()
    {
        var alwaysOnTop = _settingsService.Settings.Behavior.AlwaysOnTop;
        TopmostButton.Opacity = alwaysOnTop ? 1 : 0.45;
        TopmostButton.ToolTip = alwaysOnTop ? "Always on top" : "Normal window";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMinimizing)
        {
            return;
        }

        ResetCompactBurnDisc();
        CloseMusicInfoMode(animate: false, restorePreviousMode: false);
        _isMinimizing = true;
        SetExpandedMode(false);

        var duration = TimeSpan.FromMilliseconds(150);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        var fade = new DoubleAnimation(0, duration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            MinimizeNativeWindow();
            ResetAfterMinimizeAnimation();
        };
        RootCard.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void MinimizeNativeWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SwMinimize);
        }
        else
        {
            SystemCommands.MinimizeWindow(this);
        }
    }

    private void ResetAfterMinimizeAnimation()
    {
        RootCard.BeginAnimation(OpacityProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootCard.Opacity = 1;
        WindowScale.ScaleX = 1;
        WindowScale.ScaleY = 1;
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            CloseMusicInfoMode(animate: false, restorePreviousMode: false);
            ResetCompactBurnDisc();
        }
        else
        {
            _isMinimizing = false;
            ResetAfterMinimizeAnimation();
            PlayOpenAnimation();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        PlayCloseAnimation();
    }

    private void PlayCloseAnimation()
    {
        if (_isClosing || _isWaitingForSettingsWindowClose || _isWaitingForBurningWindowClose)
        {
            return;
        }

        if (_settingsWindow?.WarnIfGlobeApprovalPreventsClose() == true)
        {
            _isExitingFromTray = false;
            return;
        }

        if (!ShouldCloseToTray())
        {
            _isExitingFromTray = true;
        }

        if (!ShouldCloseToTray() && _settingsWindow is { } settingsWindow)
        {
            _isWaitingForSettingsWindowClose = true;
            settingsWindow.PrepareForCloseRequest();
            settingsWindow.Close();
            return;
        }

        if (!ShouldCloseToTray() && _burningWindow is { } burningWindow)
        {
            _isWaitingForBurningWindowClose = true;
            if (_isBurningWindowHiddenByDiscRemoval)
            {
                CancelBurnDiscReading();
                _isBurnPresentationDetached = false;
                _isBurningWindowHiddenByDiscRemoval = false;
                RefreshCompactBurnPresentation();
            }
            burningWindow.PrepareForCloseRequest();
            burningWindow.Close();
            return;
        }

        if (_isFullscreen)
        {
            if (ShouldCloseToTray())
            {
                HideToTray();
                return;
            }

            _allowClose = true;
            Close();
            return;
        }

        CloseMusicInfoMode(animate: false, restorePreviousMode: false);
        _isClosing = true;

        var duration = TimeSpan.FromMilliseconds(155);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);

        var fade = new DoubleAnimation(0, duration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (ShouldCloseToTray())
            {
                HideToTray();
                return;
            }

            _allowClose = true;
            Close();
        };
        RootCard.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMoveSafely();
        }
    }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMoveSafely();
        }
    }

    private void DragMoveSafely()
    {
        try
        {
            DragMove();
            if (_isMusicInfoMode)
            {
                _musicInfoCompactBounds = new Rect(
                    Left + MusicInfoPlayerOffsetX * _musicInfoScale,
                    Top + MusicInfoPlayerOffsetY * _musicInfoScale,
                    CompactWidth,
                    CompactHeight);
                ApplyMusicInfoWindowRegion();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_progressSliderUpdating
            || !_seekSliderInteraction.IsPointerDown
            || sender is not Slider slider
            || !ReferenceEquals(_seekSliderInteraction.ActiveSlider, slider))
        {
            return;
        }

        _progressSliderUpdating = true;
        try
        {
            foreach (var progressSlider in ProgressSliders)
            {
                if (!ReferenceEquals(progressSlider, slider))
                {
                    SetSliderValueIfChanged(progressSlider, e.NewValue);
                }
            }
        }
        finally
        {
            _progressSliderUpdating = false;
        }

        ShowSliderToolTip(_seekSliderInteraction, slider, includeSeekPosition: true);
        UpdateTimelineUi();
    }

    private static void SetSliderValueFromPointer(Slider slider, MouseEventArgs e)
    {
        slider.ApplyTemplate();
        double value;
        if (slider.Template.FindName("PART_Track", slider)
            is System.Windows.Controls.Primitives.Track track)
        {
            value = track.ValueFromPoint(e.GetPosition(track));
        }
        else if (slider.ActualWidth > 1)
        {
            var ratio = Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0, 1);
            value = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
        }
        else
        {
            return;
        }

        if (!double.IsNaN(value) && !double.IsInfinity(value))
        {
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        }
    }

    private static bool IsPointerOverSliderThumb(MouseButtonEventArgs e, Slider slider)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is Visual or Visual3D)
        {
            if (current is System.Windows.Controls.Primitives.Thumb)
            {
                return true;
            }

            if (ReferenceEquals(current, slider))
            {
                break;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsVisualDescendantOf(DependencyObject descendant, DependencyObject ancestor)
    {
        var current = descendant;
        while (current is Visual or Visual3D)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async Task CompleteSeekAsync(Slider slider)
    {
        var interaction = _seekSliderInteraction;
        if (!interaction.IsPointerDown || !ReferenceEquals(interaction.ActiveSlider, slider))
        {
            return;
        }

        var target = TimeSpan.FromSeconds(slider.Value);
        EndSliderPointerInteraction(interaction, slider, releaseNativeCapture: false);
        ShowSliderToolTip(interaction, slider, includeSeekPosition: true);
        BeginSliderToolTipLinger(interaction);
        await SeekToPositionAsync(target);
    }

    private async Task SeekToPositionAsync(TimeSpan target)
    {
        if (!Snapshot.HasSession || !Snapshot.CanSeek || Snapshot.Duration <= TimeSpan.Zero)
        {
            return;
        }

        target = TimeSpan.FromSeconds(Math.Clamp(
            target.TotalSeconds,
            0,
            Snapshot.Duration.TotalSeconds));
        var requestGeneration = ++_seekRequestGeneration;
        _timelineStabilizer.BeginSeek(
            CreateTimelineMediaKey(Snapshot),
            target,
            Snapshot.Duration,
            Snapshot.IsPlaying,
            DateTimeOffset.Now);
        PrepareLyricsForExplicitSeek(target);
        UpdateTimelineUi();

        var accepted = false;
        try
        {
            accepted = await _mediaService.SeekAsync(target);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to seek the active media session: {ex}");
        }

        if (requestGeneration != _seekRequestGeneration || accepted)
        {
            return;
        }

        _timelineStabilizer.RejectPendingSeek(DateTimeOffset.Now);
        UpdateTimelineUi();
    }

    private void PrepareLyricsForExplicitSeek(TimeSpan target)
    {
        _lastLyricsPosition = target;
        _isUserBrowsingLyrics = false;
        _isUserBrowsingFullscreenLyrics = false;
        _fullscreenLyricsInactivityTimer.Stop();
        StopLyricsScrollAnimation();
        if (Lyrics.Status == LyricsStatus.Synced)
        {
            _activeLyricIndex = -1;
            _activeFullscreenLyricIndex = -1;
            _activeLyricWaitIndicatorIndex = -1;
            _activeFullscreenLyricWaitIndicatorIndex = -1;
            ApplyLyricBlockVisualState(-1);
            ApplyLyricBlockVisualState(-1, isFullscreen: true);
        }
    }

    private void SetTransportEnabled(bool isEnabled)
    {
        foreach (var control in PlayPauseButtons.Cast<FrameworkElement>()
                     .Concat(NextButtons)
                     .Concat(PreviousButtons)
                     .Concat(ProgressSliders))
        {
            SetControlDisabledState(control, isEnabled);
        }

        if (!isEnabled)
        {
            foreach (var button in PlayPauseButtons)
            {
                button.ToolTip = "Play";
            }
        }
    }

    private void RenderSyncedLyrics(
        Panel panel,
        ICollection<TextBlock> blocks,
        ICollection<LyricWaitIndicator> waitIndicators,
        bool isFullscreen)
    {
        for (var i = 0; i < Lyrics.SyncedLines.Count; i++)
        {
            var line = Lyrics.SyncedLines[i];
            var previousLineTime = i == 0 ? TimeSpan.Zero : Lyrics.SyncedLines[i - 1].Time;
            AddLyricWaitIndicatorIfNeeded(i, previousLineTime, line.Time, panel, waitIndicators, isFullscreen);
            var block = AddLyricBlock(
                panel,
                blocks,
                line.Text,
                isFullscreen ? 38 : 22,
                isFullscreen ? 0.35 : 0.30,
                isFullscreen ? new Thickness(0, 22, 0, 22) : new Thickness(0, 13, 0, 13),
                isFullscreen ? Color.FromRgb(150, 158, 158) : Color.FromRgb(124, 132, 132));
            ConfigureSyncedLyricBlock(block, line.Time);
        }
    }

    private void ConfigureSyncedLyricBlock(TextBlock block, TimeSpan timestamp)
    {
        block.Tag = timestamp;
        block.Background = Brushes.Transparent;
        block.MouseLeftButtonDown += SyncedLyricLine_MouseLeftButtonDown;
        block.MouseLeftButtonUp += SyncedLyricLine_MouseLeftButtonUp;
        UpdateSyncedLyricBlockSeekability(block, Snapshot);
    }

    private void UpdateSyncedLyricLineSeekability(MediaSnapshot snapshot)
    {
        foreach (var block in _lyricBlocks.Concat(_fullscreenLyricBlocks))
        {
            UpdateSyncedLyricBlockSeekability(block, snapshot);
        }
    }

    private static void UpdateSyncedLyricBlockSeekability(TextBlock block, MediaSnapshot snapshot)
    {
        var canSeek = block.Tag is TimeSpan timestamp
            && CanSeekToSyncedLyric(snapshot, timestamp);
        block.Cursor = canSeek ? Cursors.Hand : Cursors.Arrow;
    }

    private static bool CanSeekToSyncedLyric(MediaSnapshot snapshot, TimeSpan timestamp)
    {
        return snapshot.HasSession
            && snapshot.CanSeek
            && snapshot.Duration > TimeSpan.Zero
            && timestamp >= TimeSpan.Zero
            && timestamp <= snapshot.Duration;
    }

    private void SyncedLyricLine_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Tag: TimeSpan timestamp }
            && CanSeekToSyncedLyric(Snapshot, timestamp))
        {
            e.Handled = true;
        }
    }

    private async void SyncedLyricLine_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: TimeSpan timestamp }
            || !CanSeekToSyncedLyric(Snapshot, timestamp))
        {
            return;
        }

        e.Handled = true;
        await SeekToPositionAsync(timestamp);
    }

    private void RenderPlainLyrics(Panel panel, ICollection<TextBlock> blocks, bool isFullscreen)
    {
        foreach (var line in Lyrics.PlainLines)
        {
            AddLyricBlock(
                panel,
                blocks,
                line,
                isFullscreen ? 32 : 19,
                isFullscreen ? 0.70 : 0.76,
                isFullscreen ? new Thickness(0, 22, 0, 22) : new Thickness(0, 13, 0, 13),
                isFullscreen ? Color.FromRgb(150, 158, 158) : Color.FromRgb(124, 132, 132));
        }

        if (!isFullscreen)
        {
            SetPlainLyricStyle(blocks);
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    // ───── Volume Control ─────

    private bool _volumeUpdating;

    private void InitializeVolume()
    {
        if (!_volumeService.IsAvailable) return;
        _volumeUpdating = true;
        var vol = GetDisplayedVolumeValue();
        foreach (var slider in VolumeSliders)
        {
            slider.Value = vol;
        }

        _volumeUpdating = false;
        UpdateVolumeIcon();
        RefreshAllVolumeToolTips();
    }

    private void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_volumeService.IsAvailable) return;
        _volumeService.ToggleMute();
        _volumeUpdating = true;
        var vol = GetDisplayedVolumeValue();
        foreach (var slider in VolumeSliders)
        {
            slider.Value = vol;
        }

        _volumeUpdating = false;
        UpdateVolumeIcon();
        RefreshAllVolumeToolTips();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeUpdating || !_volumeService.IsAvailable) return;
        _volumeUpdating = true;
        var val = e.NewValue;
        _volumeService.Volume = (float)(val / 100.0);
        if (val <= 0)
        {
            _volumeService.IsMuted = true;
        }
        else if (_volumeService.IsMuted)
        {
            _volumeService.IsMuted = false;
        }
        
        foreach (var slider in VolumeSliders)
        {
            if (!ReferenceEquals(sender, slider))
            {
                slider.Value = val;
            }
        }
            
        _volumeUpdating = false;
        UpdateVolumeIcon();
        RefreshAllVolumeToolTips();
        if (_volumeSliderInteraction.IsPointerDown
            && sender is Slider activeSlider
            && ReferenceEquals(_volumeSliderInteraction.ActiveSlider, activeSlider))
        {
            ShowSliderToolTip(_volumeSliderInteraction, activeSlider, includeSeekPosition: false);
        }
    }

    private void RefreshAllVolumeToolTips()
    {
        foreach (var slider in VolumeSliders)
        {
            UpdateVolumeToolTip(slider);
        }
    }

    private double GetDisplayedVolumeValue()
    {
        return _volumeService.IsMuted ? 0 : _volumeService.Volume * 100;
    }

    private void UpdateVolumeIcon()
    {
        if (CompactVolumeSlider == null) return;
        var vol = CompactVolumeSlider.Value;
        var icon = (_volumeService.IsMuted || vol <= 0) ? "vol_muted.png" : vol switch
        {
            <= 33 => "vol_low.png",
            <= 66 => "vol_mid.png",
            _ => "vol_high.png"
        };
        var source = GetSiteImageSource($"Resources/Images/{icon}");
        foreach (var button in VolumeButtons)
        {
            SetVolumeIcon(button, source);
        }
    }

    private static void SetVolumeIcon(Button button, ImageSource source)
    {
        if (button == null) return;
        if (button.Template?.FindName("VolumeIcon", button) is Image img)
        {
            img.Source = source;
        }
    }

    private void ShowVolumeSliders(bool show)
    {
        if (show && (!Snapshot.HasSession || !_volumeService.IsAvailable)) return;
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var slider in VolumeSliders)
        {
            slider.Visibility = vis;
        }
    }

    // ───── Last.fm Info ─────

    private void RefreshLastFmForSnapshot(MediaSnapshot snapshot, bool force = false)
    {
        var query = LastFmMetadataCleaner.CreateQuery(snapshot);
        var isLikelySong = LastFmMetadataCleaner.IsLikelySong(snapshot, query, out _);
        var key = snapshot.HasSession && _lastFmService.IsConfigured && isLikelySong
            ? CreateLastFmLookupKey(query)
            : string.Empty;
        if (!force && key == _lastFmTrackKey) return;
        _lastFmTrackKey = key;
        _lastFmLookupCompletedKey = string.Empty;

        _lastFmCts?.Cancel();
        CurrentLastFmInfo = null;
        ShowInfoButtons();

        if (!_lastFmService.IsConfigured
            || !snapshot.HasSession
            || !isLikelySong)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _lastFmCts = cts;

        _ = Task.Run(async () =>
        {
            var info = await _lastFmService.GetTrackInfoAsync(query, cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            Dispatcher.Invoke(() =>
            {
                if (cts.Token.IsCancellationRequested) return;
                _lastFmLookupCompletedKey = key;
                if (info is not null)
                {
                    CurrentLastFmInfo = info;
                }
                UpdateScrobblingForSnapshot(Snapshot, force: true);
            });
        }, cts.Token);
    }

    private static string CreateLastFmLookupKey(LastFmTrackQuery query)
    {
        return $"{query.ArtistName.Trim().ToLowerInvariant()}|{query.TrackName.Trim().ToLowerInvariant()}";
    }

    private void ShowInfoButtons()
    {
        foreach (var button in InfoButtons)
        {
            button.Visibility = Visibility.Visible;
        }
    }

    private void UpdateScrobblingForSnapshot(MediaSnapshot snapshot, bool force = false)
    {
        if (!_settingsService.Settings.LastFm.ScrobblingEnabled)
        {
            ResetScrobblingState();
            SetScrobblingStatus("Scrobbling: off");
            return;
        }

        if (!_lastFmService.IsScrobblingEnabled)
        {
            ResetScrobblingState();
            SetScrobblingStatus("Scrobbling: disabled");
            return;
        }

        var query = LastFmMetadataCleaner.CreateQuery(snapshot);
        if (!LastFmMetadataCleaner.IsLikelySong(snapshot, query, out var skipReason))
        {
            ResetScrobblingState();
            SetScrobblingStatus($"Scrobbling: skipped ({skipReason})");
            return;
        }

        var lookupKey = CreateLastFmLookupKey(query);
        if (_scrobbleState is not null && _scrobbleState.LookupKey != lookupKey)
        {
            ResetScrobblingState();
        }

        if (!snapshot.IsPlaying)
        {
            PauseScrobblingState();
            SetScrobblingStatus(_scrobbleState is null ? "Scrobbling: waiting" : "Scrobbling: paused");
            return;
        }

        if (CurrentLastFmInfo is null)
        {
            PauseScrobblingState();
            SetScrobblingStatus(_lastFmLookupCompletedKey == lookupKey
                ? "Scrobbling: skipped (not found on Last.fm)"
                : "Scrobbling: checking track");
            return;
        }

        var duration = GetScrobbleDuration(CurrentLastFmInfo, snapshot);
        if (duration < TimeSpan.FromSeconds(30))
        {
            ResetScrobblingState();
            SetScrobblingStatus("Scrobbling: skipped (too short)");
            return;
        }

        var key = CreateScrobbleKey(CurrentLastFmInfo);
        var now = DateTimeOffset.Now;
        if (_scrobbleState is null || _scrobbleState.Key != key)
        {
            _scrobbleState = new ScrobblePlaybackState(lookupKey, key, CurrentLastFmInfo, EstimateTrackStartedAt(snapshot, duration, now), duration);
        }
        _trayNowPlayingTrackName = _scrobbleState.Track.TrackName;

        AccumulateScrobblePlayTime(_scrobbleState, snapshot, now);

        if (!_scrobbleState.NowPlayingSubmitted)
        {
            _scrobbleState.NowPlayingSubmitted = true;
            SendNowPlayingAsync(_scrobbleState);
        }

        if (_scrobbleState.ScrobbleSubmitted)
        {
            SetScrobblingStatus($"Scrobbled: {_scrobbleState.Track.TrackName}");
            return;
        }

        var threshold = GetScrobbleThreshold(_scrobbleState.Duration);
        if (_scrobbleState.ObservedPlayTime >= threshold)
        {
            SubmitScrobbleAsync(_scrobbleState);
            return;
        }

        var remaining = threshold - _scrobbleState.ObservedPlayTime;
        SetScrobblingStatus($"Scrobbling: {FormatTime(remaining)} left");
    }

    private void SendNowPlayingAsync(ScrobblePlaybackState state)
    {
        var cts = ResetLastFmScrobbleCancellation();
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _lastFmService.UpdateNowPlayingAsync(state.Track, cts.Token);
                if (cts.Token.IsCancellationRequested) return;

                Dispatcher.Invoke(() =>
                {
                    if (ReferenceEquals(_scrobbleState, state) && !result.IsSuccess)
                    {
                        SetScrobblingStatus(result.Message);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private void SubmitScrobbleAsync(ScrobblePlaybackState state)
    {
        if (state.ScrobbleSubmitStarted)
        {
            return;
        }

        state.ScrobbleSubmitStarted = true;
        SetScrobblingStatus("Scrobbling: submitting...");
        var cts = ResetLastFmScrobbleCancellation();

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _lastFmService.ScrobbleAsync(state.Track, state.StartedAt, cts.Token);
                if (cts.Token.IsCancellationRequested) return;

                Dispatcher.Invoke(() =>
                {
                    if (!ReferenceEquals(_scrobbleState, state))
                    {
                        return;
                    }

                    state.ScrobbleSubmitted = result.IsSuccess;
                    if (result.IsSuccess)
                    {
                        LocalScrobbleCacheService.Instance.AddRecord(new ScrobbleRecord
                        {
                            Title = state.Track.TrackName,
                            Artist = state.Track.ArtistName,
                            Album = state.Track.AlbumName,
                            Timestamp = state.StartedAt.ToUnixTimeSeconds(),
                            Duration = (int)state.Duration.TotalSeconds
                        });
                    }

                    SetScrobblingStatus(result.IsSuccess
                        ? $"Scrobbled: {state.Track.TrackName}"
                        : result.Message);
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private CancellationTokenSource ResetLastFmScrobbleCancellation()
    {
        _lastFmScrobbleCts?.Cancel();
        _lastFmScrobbleCts?.Dispose();
        _lastFmScrobbleCts = new CancellationTokenSource();
        return _lastFmScrobbleCts;
    }

    private void ResetScrobblingState()
    {
        _scrobbleState = null;
        _trayNowPlayingTrackName = string.Empty;
        _lastFmScrobbleCts?.Cancel();
    }

    private void PauseScrobblingState()
    {
        if (_scrobbleState is not null)
        {
            _scrobbleState.LastObservedAt = null;
        }
    }

    private void SetScrobblingStatus(string status)
    {
        _scrobblingStatusText = status;
    }

    private static void AccumulateScrobblePlayTime(ScrobblePlaybackState state, MediaSnapshot snapshot, DateTimeOffset now)
    {
        var position = snapshot.Position < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Position;
        if (state.LastObservedAt is not null)
        {
            var wallDelta = now - state.LastObservedAt.Value;
            var positionDelta = position - state.LastPosition;
            if (wallDelta > TimeSpan.Zero
                && wallDelta < TimeSpan.FromSeconds(12)
                && positionDelta >= TimeSpan.Zero
                && positionDelta < TimeSpan.FromSeconds(12))
            {
                var increment = positionDelta > TimeSpan.Zero && positionDelta < wallDelta
                    ? positionDelta
                    : wallDelta;
                state.ObservedPlayTime += increment;
            }
        }

        state.LastObservedAt = now;
        state.LastPosition = position;
    }

    private static DateTimeOffset EstimateTrackStartedAt(MediaSnapshot snapshot, TimeSpan duration, DateTimeOffset now)
    {
        var position = snapshot.Position;
        if (position < TimeSpan.Zero || position > duration)
        {
            position = TimeSpan.Zero;
        }

        return now - position;
    }

    private static TimeSpan GetScrobbleDuration(LastFmTrackInfo track, MediaSnapshot snapshot)
    {
        return track.Duration > TimeSpan.Zero ? track.Duration : snapshot.Duration;
    }

    private static TimeSpan GetScrobbleThreshold(TimeSpan duration)
    {
        return duration <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(4)
            : TimeSpan.FromTicks(Math.Min((duration / 2).Ticks, TimeSpan.FromMinutes(4).Ticks));
    }

    private static string CreateScrobbleKey(LastFmTrackInfo track)
    {
        return $"{track.ArtistName.Trim().ToLowerInvariant()}|{track.TrackName.Trim().ToLowerInvariant()}";
    }

    private sealed class ScrobblePlaybackState(string lookupKey, string key, LastFmTrackInfo track, DateTimeOffset startedAt, TimeSpan duration)
    {
        public string LookupKey { get; } = lookupKey;
        public string Key { get; } = key;
        public LastFmTrackInfo Track { get; } = track;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public TimeSpan Duration { get; } = duration;
        public TimeSpan ObservedPlayTime { get; set; }
        public TimeSpan LastPosition { get; set; }
        public DateTimeOffset? LastObservedAt { get; set; }
        public bool NowPlayingSubmitted { get; set; }
        public bool ScrobbleSubmitStarted { get; set; }
        public bool ScrobbleSubmitted { get; set; }
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var hasTrack = Snapshot.HasSession && !string.IsNullOrWhiteSpace(Snapshot.Title);
        var trackInfoItem = new MenuItem
        {
            Header = "Track information",
            Icon = CreateMenuIcon("Resources/Images/info.ico"),
            IsEnabled = hasTrack
        };
        trackInfoItem.Click += (_, _) => ShowMusicInfoMode(MusicInfoPage.Track);
        menu.Items.Add(trackInfoItem);

        var artistInfoItem = new MenuItem
        {
            Header = "Artist information",
            Icon = CreateMenuIcon("Resources/user.ico"),
            IsEnabled = hasTrack
        };
        artistInfoItem.Click += (_, _) => ShowMusicInfoMode(MusicInfoPage.Artist);
        menu.Items.Add(artistInfoItem);

        var albumInfoItem = new MenuItem
        {
            Header = "Album information",
            Icon = CreateMenuIcon("Resources/artwork.ico"),
            IsEnabled = hasTrack
        };
        albumInfoItem.Click += (_, _) => ShowMusicInfoMode(MusicInfoPage.Album);
        menu.Items.Add(albumInfoItem);

        var lastFmInfo = CurrentLastFmInfo;
        if (lastFmInfo is not null)
        {
            var lastFmItem = new MenuItem
            {
                Header = $"View \"{lastFmInfo.TrackName}\" on Last.fm",
                Icon = new Image
                {
                    Source = GetSiteImageSource("Resources/Images/lastfm.png"),
                    Width = 16,
                    Height = 16
                }
            };
            lastFmItem.Click += (_, _) =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = lastFmInfo.Url,
                    UseShellExecute = true
                });
            };
            menu.Items.Add(lastFmItem);
        }

        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem
        {
            Header = "Settings",
            Icon = CreateMenuIcon("Resources/settings.ico")
        };
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(settingsItem);

        var burnItem = new MenuItem
        {
            Header = "Burn",
            Icon = CreateMenuIcon("Resources/burn.ico")
        };
        burnItem.Click += (_, _) => _ = OpenBurningWindowAsync(allowDuringPlayback: true);
        menu.Items.Add(burnItem);
        menu.Items.Add(new Separator());

        var aboutItem = new MenuItem
        {
            Header = "About",
            Icon = CreateMenuIcon("Resources/Images/info.ico")
        };
        aboutItem.Click += (_, _) => ShowAboutWindow();
        menu.Items.Add(aboutItem);

        if (sender is Button btn)
        {
            btn.ContextMenu = menu;
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        }
        menu.IsOpen = true;
    }

    private static Image CreateMenuIcon(string relativePath)
    {
        return new Image
        {
            Source = IconImageSource.LoadBestFitFrame(relativePath, 16),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true
        };
    }

    private bool _savedTopmostState;
    private int _openDialogCount;

    private void PushDisableTopmost()
    {
        if (_openDialogCount == 0)
        {
            _savedTopmostState = Topmost;
            Topmost = false;
        }
        _openDialogCount++;
    }

    private void PopRestoreTopmost()
    {
        _openDialogCount--;
        if (_openDialogCount == 0)
        {
            Topmost = _savedTopmostState;
        }
    }

    private void ShowAboutWindow()
    {
        AppDialogWindow.ShowAbout(this, (owner, button) => SettingsWindow.CheckForUpdatesAsync(owner, showNoUpdateMessage: true, showErrors: true, button));
    }

    // ───── Lifecycle ─────

    private void ShowMusicInfoMode(MusicInfoPage page, bool restoreFullscreen = false)
    {
        if (!Snapshot.HasSession || string.IsNullOrWhiteSpace(Snapshot.Title))
        {
            return;
        }

        if (_pendingMusicInfoPageAfterFullscreen is not null)
        {
            _pendingMusicInfoPageAfterFullscreen = page;
            return;
        }

        if (_isMusicInfoTransitioning || _isMinimizing)
        {
            return;
        }

        if (_isMusicInfoMode)
        {
            MusicInfoPanel.ShowTrack(Snapshot, page);
            return;
        }

        if (_isFullscreen)
        {
            _musicInfoTransitionGeneration++;
            _isMusicInfoTransitioning = true;
            _pendingMusicInfoPageAfterFullscreen = page;
            SetFullscreenMode(false);
            return;
        }

        _musicInfoTransitionGeneration++;
        _isMusicInfoTransitioning = true;
        _restoreFullscreenAfterMusicInfo = restoreFullscreen;
        _restoreExpandedAfterMusicInfo = _isExpanded;
        _restoreLyricsAfterMusicInfo = _isLyricsMode;
        _restoreExpandedBehindLyricsAfterMusicInfo = _restoreExpandedAfterLyrics;

        if (_isLyricsMode)
        {
            CollapseLyricsImmediatelyForMusicInfo();
        }
        else if (_isExpanded)
        {
            CollapseExpandedImmediatelyForLyrics();
        }

        if (_isBurnDiscOverflowActive)
        {
            DisableCompactBurnDiscOverflow();
        }

        ResetCompactBurnDisc();
        var compactLeft = Left;
        var compactTop = Top;
        _musicInfoCompactBounds = new Rect(compactLeft, compactTop, CompactWidth, CompactHeight);
        var workArea = GetCurrentMonitorWorkArea();
        var scaleForWidth = Math.Max(0.01, workArea.Width - 8) / MusicInfoWidth;
        var scaleForHeight = Math.Max(0.01, workArea.Height - 8) / MusicInfoHeight;
        _musicInfoScale = Math.Clamp(Math.Min(1, Math.Min(scaleForWidth, scaleForHeight)), 0.36, 1);
        var surfaceWidth = MusicInfoWidth * _musicInfoScale;
        var surfaceHeight = MusicInfoHeight * _musicInfoScale;
        var maxLeft = Math.Max(workArea.Left, workArea.Right - surfaceWidth);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - surfaceHeight);

        _isMusicInfoMode = true;
        RootCard.BeginAnimation(OpacityProperty, null);
        RootCard.Opacity = 1;
        MusicInfoSurfaceScale.ScaleX = _musicInfoScale;
        MusicInfoSurfaceScale.ScaleY = _musicInfoScale;
        RootCard.Width = CompactWidth;
        RootCard.Height = CompactHeight;
        RootCard.HorizontalAlignment = HorizontalAlignment.Left;
        RootCard.VerticalAlignment = VerticalAlignment.Top;
        RootCard.Margin = new Thickness(MusicInfoPlayerOffsetX, MusicInfoPlayerOffsetY, 0, 0);
        SetWindowSize(surfaceWidth, surfaceHeight);
        Left = Math.Clamp(compactLeft - MusicInfoPlayerOffsetX * _musicInfoScale, workArea.Left, maxLeft);
        Top = Math.Clamp(compactTop - MusicInfoPlayerOffsetY * _musicInfoScale, workArea.Top, maxTop);

        SetCompactChromeVisible(true);
        CollapseExpandedButton.Visibility = Visibility.Visible;
        CollapseExpandedButton.ToolTip = "Close information";
        AlbumArtSurface.ToolTip = null;
        AlbumArtSurface.Cursor = Cursors.Arrow;
        AlbumArtSurface.IsHitTestVisible = false;
        AlbumExpandHint.BeginAnimation(OpacityProperty, null);
        AlbumExpandHint.Opacity = 0;
        CompactBurnSlot.IsHitTestVisible = false;
        AnimateElementOpacity(CompactBurnSlot, 0, 100);
        MusicInfoPanel.Visibility = Visibility.Visible;
        UpdateLayout();
        _musicInfoArtworkTarget = GetBoundsRelativeTo(AlbumArtSurface, MusicInfoPanel);

        ApplyMusicInfoControlsOnlyPresentation();
        RootCard.Height = MusicInfoControlsHeight;
        RootCard.Margin = new Thickness(MusicInfoPlayerOffsetX, MusicInfoControlsOffsetY, 0, 0);
        UpdateLayout();
        ApplyMusicInfoWindowRegion();

        MusicInfoPanel.Activate(Snapshot, page, _musicInfoArtworkTarget);
        AnimateElementOpacity(AlbumArtSurface, 0, 180);
        _isMusicInfoTransitioning = false;
    }

    private void CloseMusicInfoMode(bool animate = true, bool restorePreviousMode = true)
    {
        if (!_isMusicInfoMode)
        {
            if (_pendingMusicInfoPageAfterFullscreen is not null)
            {
                _pendingMusicInfoPageAfterFullscreen = null;
                _isMusicInfoTransitioning = false;
                _musicInfoTransitionGeneration++;
            }
            return;
        }

        var generation = ++_musicInfoTransitionGeneration;
        _isMusicInfoTransitioning = true;
        UpdateLayout();
        var artworkTarget = !_musicInfoArtworkTarget.IsEmpty
            ? _musicInfoArtworkTarget
            : GetBoundsRelativeTo(AlbumArtSurface, MusicInfoPanel);
        AnimateElementOpacity(AlbumArtSurface, 1, animate ? 190 : 0);

        if (!animate || !IsLoaded)
        {
            CompleteMusicInfoExit(generation, restorePreviousMode);
            return;
        }

        MusicInfoPanel.PlayExitAnimation(
            artworkTarget,
            Snapshot.CoverArt,
            () => CompleteMusicInfoExit(generation, restorePreviousMode));
    }

    private void CompleteMusicInfoExit(int generation, bool restorePreviousMode)
    {
        if (generation != _musicInfoTransitionGeneration || !_isMusicInfoMode)
        {
            return;
        }

        var compactLeft = !_musicInfoCompactBounds.IsEmpty
            ? _musicInfoCompactBounds.Left
            : Left + MusicInfoPlayerOffsetX * _musicInfoScale;
        var compactTop = !_musicInfoCompactBounds.IsEmpty
            ? _musicInfoCompactBounds.Top
            : Top + MusicInfoPlayerOffsetY * _musicInfoScale;
        var restoreLyrics = restorePreviousMode && _restoreLyricsAfterMusicInfo;
        var restoreExpanded = restorePreviousMode && _restoreExpandedAfterMusicInfo;
        var restoreExpandedBehindLyrics = _restoreExpandedBehindLyricsAfterMusicInfo;
        var restoreFullscreen = restorePreviousMode && _restoreFullscreenAfterMusicInfo;

        MusicInfoPanel.Deactivate();
        MusicInfoPanel.Visibility = Visibility.Collapsed;
        MusicInfoSurfaceScale.ScaleX = 1;
        MusicInfoSurfaceScale.ScaleY = 1;
        _musicInfoScale = 1;
        RestoreCompactPresentationAfterMusicInfo();
        RootCard.ClearValue(WidthProperty);
        RootCard.ClearValue(HeightProperty);
        RootCard.ClearValue(HorizontalAlignmentProperty);
        RootCard.ClearValue(VerticalAlignmentProperty);
        RootCard.ClearValue(MarginProperty);
        SetWindowSize(CompactWidth, CompactHeight);
        var constrained = ConstrainToVirtualScreen(compactLeft, compactTop, CompactWidth, CompactHeight);
        Left = constrained.X;
        Top = constrained.Y;

        _isMusicInfoMode = false;
        _isMusicInfoTransitioning = false;
        _restoreExpandedAfterMusicInfo = false;
        _restoreLyricsAfterMusicInfo = false;
        _restoreExpandedBehindLyricsAfterMusicInfo = false;
        _restoreFullscreenAfterMusicInfo = false;
        _musicInfoCompactBounds = Rect.Empty;
        _musicInfoArtworkTarget = Rect.Empty;
        CollapseExpandedButton.Visibility = Visibility.Collapsed;
        CollapseExpandedButton.ToolTip = "Collapse";
        AlbumArtSurface.BeginAnimation(OpacityProperty, null);
        AlbumArtSurface.Opacity = 1;
        AlbumArtSurface.IsHitTestVisible = true;
        AlbumArtSurface.ToolTip = Snapshot.CoverArt is not null ? "Expand" : null;
        AlbumArtSurface.Cursor = Snapshot.CoverArt is not null ? Cursors.Hand : Cursors.Arrow;
        CompactBurnSlot.IsHitTestVisible = true;
        CompactBurnSlot.BeginAnimation(OpacityProperty, null);
        CompactBurnSlot.Opacity = 1;
        UpdateCompactBlurredArtVisibility();
        UpdateLayout();
        ApplyRoundedWindowRegion();
        RefreshCompactBurnPresentation();

        if (restoreLyrics)
        {
            SetLyricsMode(true);
            _restoreExpandedAfterLyrics = restoreExpandedBehindLyrics;
        }
        else if (restoreExpanded && Snapshot.CoverArt is not null)
        {
            SetExpandedMode(true);
        }
        else
        {
            SetCompactChromeVisible(true);
        }

        if (restoreFullscreen)
        {
            SetFullscreenMode(true);
        }
    }

    private void CollapseLyricsImmediatelyForMusicInfo()
    {
        _isLyricsMode = false;
        LyricsPanel.BeginAnimation(OpacityProperty, null);
        LyricsPanel.Opacity = 0;
        LyricsPanel.Visibility = Visibility.Collapsed;
        LyricsPanel.IsHitTestVisible = false;
        LyricsPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LyricsPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LyricsPanelScale.ScaleX = 1;
        LyricsPanelScale.ScaleY = 1;
        UpdateCompactBlurredArtVisibility();
    }

    private Rect GetCurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var compositionTarget = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (handle == IntPtr.Zero || compositionTarget is null)
        {
            return SystemParameters.WorkArea;
        }

        var bounds = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var fromDevice = compositionTarget.TransformFromDevice;
        return new Rect(
            fromDevice.Transform(new Point(bounds.Left, bounds.Top)),
            fromDevice.Transform(new Point(bounds.Right, bounds.Bottom)));
    }

    private static Rect GetBoundsRelativeTo(FrameworkElement element, UIElement relativeTo)
    {
        var topLeft = element.TranslatePoint(new Point(), relativeTo);
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var settingsWindow = new SettingsWindow(
            _settingsService,
            _lastFmService,
            _globeConnectionService)
        {
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        _settingsWindow = settingsWindow;
        if (IsVisible)
        {
            settingsWindow.Owner = this;
        }

        settingsWindow.Closed += SettingsWindow_Closed;
        settingsWindow.CloseRequestCanceled += SettingsWindow_CloseRequestCanceled;
        settingsWindow.Show();
    }

    internal void ActivateFromExternalRequest(
        bool openSocialSettings,
        bool startSocialLinking = false)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ShowInTaskbar = true;
        Activate();

        if (!openSocialSettings)
        {
            return;
        }

        ShowSettingsWindow();
        _settingsWindow?.ShowSocialSection(startSocialLinking);
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow settingsWindow)
        {
            settingsWindow.Closed -= SettingsWindow_Closed;
            settingsWindow.CloseRequestCanceled -= SettingsWindow_CloseRequestCanceled;
        }

        if (ReferenceEquals(_settingsWindow, sender))
        {
            _settingsWindow = null;
        }

        if (_isWaitingForSettingsWindowClose)
        {
            _isWaitingForSettingsWindowClose = false;
            Dispatcher.BeginInvoke(PlayCloseAnimation, DispatcherPriority.Normal);
        }
    }

    private void SettingsWindow_CloseRequestCanceled(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_settingsWindow, sender) || !_isWaitingForSettingsWindowClose)
        {
            return;
        }

        _isWaitingForSettingsWindowClose = false;
        _isExitingFromTray = false;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => OnSettingsChanged(sender, e),
                DispatcherPriority.Normal);
            return;
        }

        ResetScrobblingState();
        RefreshLastFmForSnapshot(Snapshot, force: true);
        UpdateScrobblingForSnapshot(Snapshot, force: true);
        ApplyPlayerAppearance();
        var alwaysOnTop = _settingsService.Settings.Behavior.AlwaysOnTop;
        if (_openDialogCount > 0)
        {
            _savedTopmostState = alwaysOnTop;
            Topmost = false;
        }
        else
        {
            Topmost = alwaysOnTop;
        }
        SetTopmostUi();
    }

    private void GlobeConnectionService_LinkRevoked(object? sender, GlobeLinkRevokedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => GlobeConnectionService_LinkRevoked(sender, e),
                DispatcherPriority.Normal);
            return;
        }

        if (_globeRevocationWarningPending)
        {
            return;
        }

        _globeRevocationWarningPending = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _globeRevocationWarningPending = false;
            var owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsVisible && window.IsActive)
                ?? this;
            AppDialogWindow.ShowWarning(
                owner,
                "globe account unlinked",
                "Your globe account is no longer linked.");
        }, DispatcherPriority.Normal);
    }

    private void GlobeConnectionService_ServerUnavailable(
        object? sender,
        GlobeServerUnavailableEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => GlobeConnectionService_ServerUnavailable(sender, e),
                DispatcherPriority.Normal);
            return;
        }

        if (_globeServerWarningPending)
        {
            return;
        }

        _globeServerWarningPending = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _globeServerWarningPending = false;
            if (_isClosing)
            {
                return;
            }

            var owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsVisible && window.IsActive)
                ?? this;
            AppDialogWindow.ShowWarning(owner, "globe connection unavailable", e.Message);
        }, DispatcherPriority.Normal);
    }

    private void InitializeTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "ico.ico");
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = AppMetadata.Name,
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
            Visible = true
        };
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
    }

    private void ShowTrayContextMenu()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }

        _trayContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);

        var menu = new ContextMenu();
        menu.Items.Add(CreateTrayHeaderItem());
        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem
        {
            Header = "Settings",
            Icon = CreateMenuIcon("Resources/settings.ico")
        };
        settingsItem.Click += (_, _) =>
        {
            RestoreFromTray();
            ShowSettingsWindow();
        };

        var burnItem = new MenuItem
        {
            Header = "Burn",
            Icon = CreateMenuIcon("Resources/burn.ico")
        };
        burnItem.Click += (_, _) =>
        {
            _ = OpenBurningWindowAsync(allowDuringPlayback: true);
        };

        var exitItem = new MenuItem
        {
            Header = "Exit",
            Icon = CreateMenuIcon("Resources/exit.ico")
        };
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(settingsItem);
        menu.Items.Add(burnItem);
        menu.Items.Add(new Separator());

        if (_settingsService.Settings.LastFm.Enabled)
        {
            var enableScrobblingItem = CreateTrayScrobblingItem();
            var profileItem = CreateLastFmProfileItem();
            menu.Items.Add(enableScrobblingItem);
            if (!string.IsNullOrWhiteSpace(_trayNowPlayingTrackName))
            {
                menu.Items.Add(CreateNowPlayingTrackItem(_trayNowPlayingTrackName));
            }
            menu.Items.Add(profileItem);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(exitItem);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        var cursor = System.Windows.Forms.Cursor.Position;
        var transformFromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var menuPoint = transformFromDevice.Transform(new Point(cursor.X, cursor.Y));
        menu.HorizontalOffset = menuPoint.X;
        menu.VerticalOffset = menuPoint.Y;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayContextMenu, menu))
            {
                _trayContextMenu = null;
            }
        };

        _trayContextMenu = menu;
        menu.IsOpen = true;
    }

    private MenuItem CreateTrayHeaderItem()
    {
        var item = new MenuItem
        {
            Header = $"{AppMetadata.Name} {AppMetadata.Version}",
            Icon = CreateMenuIcon("Resources/ico.ico"),
            FontWeight = FontWeights.Bold,
            Focusable = false,
            IsTabStop = false,
            Cursor = Cursors.Hand
        };
        item.Click += (_, _) =>
        {
            RestoreFromTray();
            ShowAboutWindow();
        };
        return item;
    }

    private MenuItem CreateTrayScrobblingItem()
    {
        var item = new MenuItem
        {
            Header = "Enable Scrobbling",
            IsCheckable = true,
            IsChecked = _settingsService.Settings.LastFm.ScrobblingEnabled
        };
        item.Click += (_, _) => ToggleScrobblingFromTray();
        return item;
    }

    private static MenuItem CreateNowPlayingTrackItem(string trackName)
    {
        return new MenuItem
        {
            Header = $"Now Playing: {trackName}",
            Focusable = false,
            IsTabStop = false,
            StaysOpenOnClick = true,
            Cursor = Cursors.Arrow
        };
    }

    private MenuItem CreateLastFmProfileItem()
    {
        var username = _settingsService.Settings.LastFm.Username.Trim();
        var item = new MenuItem
        {
            Header = "View your Last.fm Profile",
            IsEnabled = !string.IsNullOrWhiteSpace(username)
        };
        item.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://www.last.fm/user/{Uri.EscapeDataString(username)}",
                UseShellExecute = true
            });
        };
        return item;
    }

    private bool _isTrayScrobbleValidationRunning;

    private async void ToggleScrobblingFromTray()
    {
        if (_isTrayScrobbleValidationRunning)
        {
            return;
        }

        var current = _settingsService.Settings;
        if (current.LastFm.ScrobblingEnabled)
        {
            _settingsService.Save(current with
            {
                LastFm = current.LastFm with { ScrobblingEnabled = false }
            });
            return;
        }

        if (!current.LastFm.HasScrobblingCredentials)
        {
            // Never let the tray flip scrobbling on without the credentials it
            // needs — send the user to the Last.fm settings instead.
            RestoreFromTray();
            ShowSettingsWindow();
            AppDialogWindow.ShowWarning(
                _settingsWindow is { IsVisible: true } settingsWindow ? settingsWindow : this,
                "Scrobbling needs more details",
                "Scrobbling requires your Last.fm API secret and password. Add them in the Last.fm settings and save.");
            return;
        }

        // Fields being filled in is not enough: prove them against Last.fm the
        // same way Settings does before the toggle may claim scrobbling is on.
        _isTrayScrobbleValidationRunning = true;
        try
        {
            var validation = await _lastFmService.ValidateCredentialsAsync(
                current.LastFm with { ScrobblingEnabled = true });
            if (!validation.IsSuccess)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "Couldn't enable scrobbling",
                    validation.Message);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _isTrayScrobbleValidationRunning = false;
        }

        var latest = _settingsService.Settings;
        _settingsService.Save(latest with
        {
            LastFm = latest.LastFm with { ScrobblingEnabled = true }
        });
    }

    private void NotifyIcon_MouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Right)
        {
            Dispatcher.Invoke(ShowTrayContextMenu);
            return;
        }

        if (e.Button == System.Windows.Forms.MouseButtons.Left)
        {
            RestoreFromTray();
        }
    }

    private void HideToTray()
    {
        ResetCompactBurnDisc();
        SaveWindowPlacement();
        ResetAfterMinimizeAnimation();
        _isClosing = false;
        _allowClose = false;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (IsVisible)
        {
            Activate();
            return;
        }

        _isClosing = false;
        _allowClose = false;
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Show();
        Activate();
        ResetAfterMinimizeAnimation();
        PlayOpenAnimation();
    }

    private void ExitApplication()
    {
        _isExitingFromTray = true;
        _allowClose = false;
        _trayContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _trayContextMenu = null;
        PlayCloseAnimation();
    }

    private bool ShouldCloseToTray()
    {
        return !_isExitingFromTray && _settingsService.Settings.Behavior.CloseToTray;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableShellMinimizeStyles();
        InitializeVolume();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPlacement();

        if (!_allowClose)
        {
            e.Cancel = true;
            PlayCloseAnimation();
            return;
        }

        base.OnClosing(e);
    }

    private void RestoreWindowPlacement()
    {
        try
        {
            if (!File.Exists(WindowPlacementPath))
            {
                return;
            }

            var json = File.ReadAllText(WindowPlacementPath);
            var placement = JsonSerializer.Deserialize<WindowPlacement>(json);
            if (placement is null || !IsFinite(placement.Left) || !IsFinite(placement.Top))
            {
                return;
            }

            var constrained = ConstrainToVirtualScreen(placement.Left, placement.Top, CompactWidth, CompactHeight);
            Left = constrained.X;
            Top = constrained.Y;
        }
        catch
        {
        }
    }

    private void SaveWindowPlacement()
    {
        var placementLeft = _isMusicInfoMode && !_musicInfoCompactBounds.IsEmpty
            ? _musicInfoCompactBounds.Left
            : Left;
        var placementTop = _isMusicInfoMode && !_musicInfoCompactBounds.IsEmpty
            ? _musicInfoCompactBounds.Top
            : Top;
        if (!IsFinite(placementLeft) || !IsFinite(placementTop) || WindowState == WindowState.Minimized)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WindowPlacementPath)!);
            var placement = new WindowPlacement(placementLeft, placementTop);
            var json = JsonSerializer.Serialize(placement);
            File.WriteAllText(WindowPlacementPath, json);
        }
        catch
        {
        }
    }

    private static Point ConstrainToVirtualScreen(double left, double top, double width, double height)
    {
        var visibleWidth = Math.Min(width, 96);
        var visibleHeight = Math.Min(height, 72);
        var minLeft = SystemParameters.VirtualScreenLeft - width + visibleWidth;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - visibleWidth;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - visibleHeight;

        return new Point(
            Math.Clamp(left, minLeft, maxLeft),
            Math.Clamp(top, minTop, maxTop));
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= TimelineCompositionTarget_Rendering;
        _burnDiscArtworkCts.Cancel();
        _burnDiscArtworkCts.Dispose();
        CancelBurnDiscReading();
        var burnPresentationArtworkCts = Interlocked.Exchange(ref _burnPresentationArtworkCts, null);
        burnPresentationArtworkCts?.Cancel();
        burnPresentationArtworkCts?.Dispose();
        _mediaPollTimer.Stop();
        StopLyricsScrollAnimation();
        _loadingIconTimer.Stop();
        _seekToolTipHideTimer.Stop();
        _volumeToolTipHideTimer.Stop();
        _lyricsRefreshCts?.Cancel();
        _lyricsRefreshCts?.Dispose();
        _lastFmCts?.Cancel();
        _lastFmCts?.Dispose();
        _lastFmScrobbleCts?.Cancel();
        _lastFmScrobbleCts?.Dispose();
        _animatedArtworkCts?.Cancel();
        _animatedArtworkCts?.Dispose();
        StopAnimatedArtwork();
        MusicInfoPanel.Dispose();
        _settingsWindow?.Close();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _globeConnectionService.LinkRevoked -= GlobeConnectionService_LinkRevoked;
        _globeConnectionService.ServerUnavailable -= GlobeConnectionService_ServerUnavailable;
        if (_notifyIcon is not null)
        {
            _notifyIcon.MouseUp -= NotifyIcon_MouseUp;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _trayContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _trayContextMenu = null;
        _volumeService.Dispose();
        _lastFmService.Dispose();
        _musicBrainzService.Dispose();
        _artistArtworkService.Dispose();
        _globeConnectionService.Dispose();
        _lyricsService.Dispose();
        _animatedArtworkService.Dispose();
        _mediaService.Dispose();
        base.OnClosed(e);
    }

    private void EnableShellMinimizeStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlStyle);
        SetWindowLong(handle, GwlStyle, style | WsSysMenu | WsMinimizeBox);
    }

    private void ApplyRoundedWindowRegion()
    {
        if (_isFullscreen || _isMusicInfoMode)
        {
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var pixelWidth = Math.Max(1, (int)Math.Round(ActualWidth * transform.M11));
        var pixelHeight = Math.Max(1, (int)Math.Round(ActualHeight * transform.M22));
        var radius = Math.Max(1, (int)Math.Round(18 * Math.Max(transform.M11, transform.M22)));
        var region = CreateRoundRectRgn(0, 0, pixelWidth + 1, pixelHeight + 1, radius, radius);

        if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    private void ApplyMusicInfoWindowRegion()
    {
        if (!_isMusicInfoMode)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var source = PresentationSource.FromVisual(this);
        if (handle == IntPtr.Zero || source?.CompositionTarget is not { } compositionTarget)
        {
            return;
        }

        var transform = compositionTarget.TransformToDevice;
        var scaleX = _musicInfoScale * transform.M11;
        var scaleY = _musicInfoScale * transform.M22;
        var combinedRegion = CreateRectRgn(0, 0, 0, 0);
        if (combinedRegion == IntPtr.Zero)
        {
            return;
        }

        var succeeded = false;
        try
        {
            UnionRoundedRegion(combinedRegion, new Rect(28, 106, 692, 350), 19, scaleX, scaleY);
            UnionRoundedRegion(combinedRegion, new Rect(10, 20, 176, 176), 17, scaleX, scaleY);
            UnionRoundedRegion(combinedRegion, new Rect(346, 88, 372, 104), 19, scaleX, scaleY);
            succeeded = SetWindowRgn(handle, combinedRegion, true) != 0;
        }
        finally
        {
            if (!succeeded)
            {
                DeleteObject(combinedRegion);
            }
        }
    }

    private static void UnionRoundedRegion(
        IntPtr destination,
        Rect bounds,
        double radius,
        double scaleX,
        double scaleY)
    {
        var left = (int)Math.Floor(bounds.Left * scaleX);
        var top = (int)Math.Floor(bounds.Top * scaleY);
        var right = (int)Math.Ceiling(bounds.Right * scaleX) + 1;
        var bottom = (int)Math.Ceiling(bounds.Bottom * scaleY) + 1;
        var ellipseWidth = Math.Max(1, (int)Math.Round(radius * 2 * scaleX));
        var ellipseHeight = Math.Max(1, (int)Math.Round(radius * 2 * scaleY));
        var source = CreateRoundRectRgn(left, top, right, bottom, ellipseWidth, ellipseHeight);
        if (source == IntPtr.Zero)
        {
            return;
        }

        CombineRgn(destination, destination, source, RgnOr);
        DeleteObject(source);
    }

    private void ClearRoundedWindowRegion()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
        }
    }

    private const int GwlStyle = -16;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int SwMinimize = 6;
    private const int RgnOr = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int combineMode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isMusicInfoMode)
        {
            CloseMusicInfoMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullscreen)
        {
            SetFullscreenMode(false);
            e.Handled = true;
        }
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingMusicInfoPageAfterFullscreen is not null || _isMusicInfoTransitioning)
        {
            return;
        }

        if (_isMusicInfoMode)
        {
            CloseMusicInfoMode(animate: false, restorePreviousMode: false);
        }

        SetFullscreenMode(!_isFullscreen);
    }

    private void FullscreenLyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        SetFullscreenLyricsVisible(!_isFullscreenLyrics);
    }

    private void SetFullscreenMode(bool isFullscreen)
    {
        if (isFullscreen && _pendingMusicInfoPageAfterFullscreen is not null)
        {
            return;
        }

        if (isFullscreen && _isMusicInfoMode)
        {
            CloseMusicInfoMode(animate: false, restorePreviousMode: false);
        }

        if (_isFullscreen == isFullscreen)
        {
            return;
        }

        _isFullscreen = isFullscreen;

        if (isFullscreen)
        {
            _preFullscreenLeft = Left;
            _preFullscreenTop = Top;
            _preFullscreenWidth = Width;
            _preFullscreenHeight = Height;
            _preFullscreenState = WindowState;
            _preFullscreenExpanded = _isExpanded;
            _preFullscreenLyrics = _isLyricsMode;

            // Recycle ChromeButtons: fullscreen keeps only its exit control visible.
            CloseButton.Visibility = Visibility.Collapsed;
            CollapseExpandedButton.Visibility = Visibility.Collapsed;
            TopmostButton.Visibility = Visibility.Collapsed;
            MinimizeButton.Visibility = Visibility.Collapsed;

            // Update fullscreen toggle path data & tooltip to exit fullscreen arrows
            FullscreenButtonPath.Data = Geometry.Parse("M 1,5 L 5,5 L 5,1 M 5,5 L 1,1 M 11,7 L 7,7 L 7,11 M 7,7 L 11,11");
            FullscreenButton.ToolTip = "Exit Fullscreen";

            // Remove immersion borders
            RootCard.BorderThickness = new Thickness(0);
            InnerGlassBorder.BorderThickness = new Thickness(0);

            HideCompactContentImmediately();
            ExpandedPanel.Visibility = Visibility.Collapsed;
            LyricsPanel.Visibility = Visibility.Collapsed;

            MinWidth = 0;
            MinHeight = 0;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;

            WindowState = WindowState.Maximized;

            FullscreenPanel.Visibility = Visibility.Visible;
            AnimateElementOpacity(FullscreenPanel, 1, 300);

            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowRgn(handle, IntPtr.Zero, true);
            }

            SyncFullscreenPlaybackState();
            RenderFullscreenLyricsState();

            var hasLyrics = Lyrics.Status is LyricsStatus.Synced or LyricsStatus.Plain;
            SetFullscreenLyricsVisible(_preFullscreenLyrics && hasLyrics);
        }
        else
        {
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += (_, _) =>
            {
                if (!_isFullscreen)
                {
                    FullscreenPanel.Visibility = Visibility.Collapsed;

                    // Restore other chrome buttons visibility and exit state icon
                    CloseButton.Visibility = Visibility.Visible;
                    MinimizeButton.Visibility = Visibility.Visible;
                    TopmostButton.Visibility = Visibility.Visible;
                    FullscreenButtonPath.Data = Geometry.Parse("M 1,6 L 1,1 L 6,1 M 1,1 L 7,7 M 14,9 L 14,14 L 9,14 M 14,14 L 8,8");
                    FullscreenButton.ToolTip = "Fullscreen";

                    // Restore immersion borders
                    RootCard.BorderThickness = new Thickness(1);
                    InnerGlassBorder.BorderThickness = new Thickness(1);
                    
                    MinWidth = 218;
                    MinHeight = 144;
                    MaxWidth = MusicInfoWidth;
                    MaxHeight = 660;

                    WindowState = _preFullscreenState;
                    Left = _preFullscreenLeft;
                    Top = _preFullscreenTop;
                    Width = _preFullscreenWidth;
                    Height = _preFullscreenHeight;

                    ApplyRoundedWindowRegion();

                    if (_preFullscreenLyrics)
                    {
                        _isLyricsMode = false;
                        SetLyricsMode(true);
                    }
                    else if (_preFullscreenExpanded)
                    {
                        _isExpanded = false;
                        SetExpandedMode(true);
                    }
                    else
                    {
                        SetCompactChromeVisible(true);
                    }

                    var pendingMusicInfoPage = _pendingMusicInfoPageAfterFullscreen;
                    _pendingMusicInfoPageAfterFullscreen = null;
                    if (pendingMusicInfoPage is { } page)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            _isMusicInfoTransitioning = false;
                            if (!_isClosing
                                && !_isFullscreen
                                && IsVisible
                                && Snapshot.HasSession
                                && !string.IsNullOrWhiteSpace(Snapshot.Title))
                            {
                                ShowMusicInfoMode(page, restoreFullscreen: true);
                            }
                            else if (!_isClosing)
                            {
                                RootCard.BeginAnimation(OpacityProperty, null);
                                RootCard.Opacity = 1;
                                if (IsVisible && WindowState != WindowState.Minimized)
                                {
                                    PlayOpenAnimation();
                                }
                            }
                        }, DispatcherPriority.Loaded);
                    }
                    else
                    {
                        if (!_isClosing && WindowState != WindowState.Minimized)
                        {
                            PlayOpenAnimation();
                        }
                    }
                }
            };
            RootCard.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void SetFullscreenLyricsVisible(bool visible)
    {
        var hasLyrics = Lyrics.Status is LyricsStatus.Synced or LyricsStatus.Plain;
        _isFullscreenLyrics = visible;

        var duration = TimeSpan.FromMilliseconds(300);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        FullscreenLyricsToggle.Opacity = 1.0;

        SetFullscreenArtSize(animate: true);

        if (_isFullscreenLyrics)
        {
            FullscreenSpacerColumn.Width = new GridLength(80);
            FullscreenLyricsColumn.Width = new GridLength(1.5, GridUnitType.Star);
            FullscreenLyricsPanel.Visibility = Visibility.Visible;
            
            AnimateElementOpacity(FullscreenLyricsPanel, 1, 300);
            
            RenderFullscreenLyricsState();
            UpdateSyncedLyricsUi(GetCurrentPosition(), isFullscreen: true);
        }
        else
        {
            var fade = new DoubleAnimation(0, duration) { EasingFunction = easing };
            fade.Completed += (_, _) =>
            {
                if (!_isFullscreenLyrics)
                {
                    FullscreenLyricsPanel.Visibility = Visibility.Collapsed;
                    FullscreenSpacerColumn.Width = new GridLength(0);
                    FullscreenLyricsColumn.Width = new GridLength(0);
                }
            };
            FullscreenLyricsPanel.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void SetFullscreenArtSize(bool animate)
    {
        var desired = _isFullscreenLyrics ? 460.0 : 560.0;
        var height = FullscreenSurface.ActualHeight > 0 ? FullscreenSurface.ActualHeight : SystemParameters.WorkArea.Height;
        var margin = FullscreenContentGrid.Margin;
        // ponytail: 205 is the fixed title/progress/control stack under the art.
        var size = Math.Min(desired, Math.Max(300.0, height - margin.Top - margin.Bottom - 205.0));

        if (Math.Abs(FullscreenArtBorder.Width - size) < 0.5 && Math.Abs(FullscreenArtBorder.Height - size) < 0.5)
        {
            return;
        }

        if (animate)
        {
            AnimateDouble(FullscreenArtBorder, WidthProperty, size, 300);
            AnimateDouble(FullscreenArtBorder, HeightProperty, size, 300);
            return;
        }

        FullscreenArtBorder.BeginAnimation(WidthProperty, null);
        FullscreenArtBorder.BeginAnimation(HeightProperty, null);
        FullscreenArtBorder.Width = size;
        FullscreenArtBorder.Height = size;
    }

    private void SyncFullscreenPlaybackState()
    {
        FullscreenTitleText.Text = Snapshot.Title;
        FullscreenArtistText.Text = Snapshot.Description;
        SetImageSourceIfChanged(FullscreenArtImage, Snapshot.CoverArt);
        SetImageSourceIfChanged(FullscreenBlurredArtImage, Snapshot.CoverArt);
        FullscreenArtPlaceholder.Visibility = Snapshot.CoverArt is null ? Visibility.Visible : Visibility.Collapsed;
        
        ApplyArtworkTint(Snapshot.CoverArt);
        
        foreach (var button in PreviousButtons)
        {
            SetControlDisabledState(button, Snapshot.CanPrevious);
        }

        foreach (var button in NextButtons)
        {
            SetControlDisabledState(button, Snapshot.CanNext);
        }

        var canPlayPause = Snapshot.CanPlay || Snapshot.CanPause;
        foreach (var button in PlayPauseButtons)
        {
            SetControlDisabledState(button, canPlayPause);
        }

        SetPlayPauseButtonState(Snapshot.IsPlaying);
        
        var canSeek = Snapshot.CanSeek && Snapshot.Duration > TimeSpan.Zero;
        foreach (var slider in ProgressSliders)
        {
            SetControlDisabledState(slider, canSeek);
            slider.Maximum = Math.Max(1, Snapshot.Duration.TotalSeconds);
        }
        
        if (_volumeService.IsAvailable)
        {
            _volumeUpdating = true;
            foreach (var slider in VolumeSliders)
            {
                slider.Value = GetDisplayedVolumeValue();
            }
            _volumeUpdating = false;
            UpdateVolumeIcon();
        }
    }

    private void RenderFullscreenLyricsState()
    {
        if (!_isFullscreen)
        {
            return;
        }

        FullscreenLyricsStackPanel.Children.Clear();
        _fullscreenLyricBlocks.Clear();
        _fullscreenLyricWaitIndicators.Clear();
        _activeFullscreenLyricWaitIndicatorIndex = -1;
        _fullscreenLyricsFooterPanel = null;
        _fullscreenLyricsTopSpacer = null;
        _fullscreenLyricsBottomSpacer = null;
        
        var status = Lyrics.Status;
        if (status is LyricsStatus.Synced or LyricsStatus.Plain)
        {
            FullscreenLyricsMessagePanel.Visibility = Visibility.Collapsed;
            _fullscreenLyricsTopSpacer = AddFullscreenLyricsSpacer();

            if (status == LyricsStatus.Synced)
            {
                RenderSyncedLyrics(FullscreenLyricsStackPanel, _fullscreenLyricBlocks, _fullscreenLyricWaitIndicators, isFullscreen: true);
            }
            else
            {
                RenderPlainLyrics(FullscreenLyricsStackPanel, _fullscreenLyricBlocks, isFullscreen: true);
            }
            
            AddFullscreenLyricsInfoFooter();
            _fullscreenLyricsBottomSpacer = AddFullscreenLyricsSpacer();
            return;
        }

        FullscreenLyricsMessagePanel.Visibility = Visibility.Visible;
        FullscreenLyricsMessageText.Text = Lyrics.Message;
    }

    private Border AddFullscreenLyricsSpacer()
    {
        var spacer = new Border { Height = GetCurrentArtCenterY(), Opacity = 0, IsHitTestVisible = false };
        FullscreenLyricsStackPanel.Children.Add(spacer);
        return spacer;
    }

    private void UpdateFullscreenLyricWaitIndicators(TimeSpan position)
    {
        UpdateLyricWaitIndicators(
            _fullscreenLyricWaitIndicators,
            position,
            ref _activeFullscreenLyricWaitIndicatorIndex,
            _activeFullscreenLyricIndex,
            _isUserBrowsingFullscreenLyrics,
            isFullscreen: true);
    }

    private void FullscreenLyricsViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        BrowseLyrics(e, isFullscreen: true);
    }

    private void FullscreenLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreen && ReferenceEquals(sender, FullscreenSurface))
        {
            SetFullscreenArtSize(animate: false);
        }

        if (_isFullscreen && _isFullscreenLyrics)
        {
            GetCurrentArtCenterY();

            if (!_isUserBrowsingFullscreenLyrics)
            {
                if (_activeFullscreenLyricIndex >= 0 && _fullscreenLyricBlocks.Count > _activeFullscreenLyricIndex)
                {
                    CenterActiveLyric(_activeFullscreenLyricIndex, isFullscreen: true);
                }
            }
        }
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key != null)
            {
                var appPath = Environment.ProcessPath ?? System.IO.Path.Combine(System.AppContext.BaseDirectory, "Mystral.exe");

                if (enable)
                {
                    key.SetValue("Mystral", $"\"{appPath}\"");
                }
                else
                {
                    key.DeleteValue("Mystral", throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Suppress registry errors
        }
    }

}
