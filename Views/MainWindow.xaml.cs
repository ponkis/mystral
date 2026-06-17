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
using System.Windows.Threading;
using Mystral.Configuration;
using Mystral.Models;
using Mystral.Services;
using static Mystral.Services.ArtworkTint;

namespace Mystral.Views;

public partial class MainWindow : Window
{
    private readonly MediaSessionService _mediaService;
    private readonly LyricsService _lyricsService;
    private readonly VolumeService _volumeService;
    private readonly LastFmService _lastFmService;
    private readonly AppSettingsService _settingsService;
    private readonly DispatcherTimer _mediaPollTimer;
    private readonly DispatcherTimer _loadingIconTimer;
    private readonly DispatcherTimer _volumeToolTipHideTimer;
    private readonly List<TextBlock> _lyricBlocks = [];
    private readonly List<LyricWaitIndicator> _lyricWaitIndicators = [];
    private readonly List<BitmapImage> _loadingIconFrames = [];
    private CancellationTokenSource? _lyricsRefreshCts;
    private CancellationTokenSource? _lastFmCts;
    private CancellationTokenSource? _lastFmScrobbleCts;
    private string _lyricsTrackKey = string.Empty;
    private string _lastFmTrackKey = string.Empty;
    private string _lastFmLookupCompletedKey = string.Empty;
    private string _scrobblingStatusText = "Scrobbling: disabled";
    private string _trayNowPlayingTrackName = string.Empty;
    private ScrobblePlaybackState? _scrobbleState;
    private bool _isSeeking;
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
    private bool _isVolumeDragging;
    private bool _isUserBrowsingLyrics;
    private bool _isUserBrowsingFullscreenLyrics;
    private bool _isLyricsScrollAnimating;
    private readonly DispatcherTimer _fullscreenLyricsInactivityTimer;
    private Slider? _activeVolumeSlider;
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
    private DateTimeOffset _lastSnapshotAt = DateTimeOffset.Now;
    private TimeSpan _lastLyricsPosition = TimeSpan.Zero;
    private int _loadingIconFrameIndex;
    private const double CompactWidth = 352;
    private const double CompactHeight = 172;
    private const double ExpandedSize = 352;
    private const double LyricsWidth = ExpandedSize;
    private const double LyricsHeight = 620;
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

    private MediaSnapshot Snapshot { get; set; } = MediaSnapshot.Empty;
    private LyricsResult Lyrics { get; set; } = LyricsResult.Empty;
    private LastFmTrackInfo? CurrentLastFmInfo { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new AppSettingsService();
        _mediaService = new MediaSessionService();
        _lyricsService = new LyricsService();
        _volumeService = new VolumeService();
        _lastFmService = new LastFmService(_settingsService);

        _mediaService.SnapshotChanged += OnSnapshotChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        _mediaPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mediaPollTimer.Tick += async (_, _) => await _mediaService.RefreshAsync();

        _fullscreenLyricsInactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.0) };
        _fullscreenLyricsInactivityTimer.Tick += (_, _) =>
        {
            _fullscreenLyricsInactivityTimer.Stop();
            if (_isFullscreen && _isFullscreenLyrics)
            {
                _isUserBrowsingFullscreenLyrics = false;
                ApplyFullscreenLyricBlockVisualState(_activeFullscreenLyricIndex);
                CenterFullscreenActiveLyric(_activeFullscreenLyricIndex);
            }
        };

        _loadingIconTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        _loadingIconTimer.Tick += (_, _) => AdvanceLoadingIconFrame();
        _volumeToolTipHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _volumeToolTipHideTimer.Tick += (_, _) => HideVolumeToolTip();
        LoadLoadingIconFrames();

        RootCard.Opacity = 0;
        WindowScale.ScaleX = 0.96;
        WindowScale.ScaleY = 0.96;
        InitializeInteractiveToolTips();
        ApplyArtworkTint(null);
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
                AppDialogWindow.ShowConfirmation(this, "Update installed", $"Mystral was updated to version {AppMetadata.Version}.");
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

        foreach (var slider in new[] { ProgressSlider, ExpandedProgressSlider, LyricsProgressSlider, FullscreenProgressSlider })
        {
            slider.ToolTip = "Seek";
            ToolTipService.SetInitialShowDelay(slider, 250);
            slider.MouseMove += ProgressSlider_MouseMove;
        }

        foreach (var slider in new[] { CompactVolumeSlider, ExpandedVolumeSlider, LyricsVolumeSlider, FullscreenVolumeSlider })
        {
            slider.ToolTip = CreateVolumeToolTip(slider);
            ToolTipService.SetInitialShowDelay(slider, 0);
            ToolTipService.SetShowDuration(slider, 60000);
            slider.PreviewMouseLeftButtonDown += VolumeSlider_PreviewMouseLeftButtonDown;
            slider.PreviewMouseLeftButtonUp += VolumeSlider_PreviewMouseLeftButtonUp;
            slider.MouseMove += VolumeSlider_MouseMove;
            slider.MouseLeave += VolumeSlider_MouseLeave;
            slider.LostMouseCapture += VolumeSlider_LostMouseCapture;
        }
    }

    private void ProgressSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider || IsControlDisabled(slider) || slider.ActualWidth <= 1)
        {
            return;
        }

        var ratio = Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0, 1);
        var seconds = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
        slider.ToolTip = $"Seek to {FormatTime(TimeSpan.FromSeconds(seconds))}";
    }

    private ToolTip CreateVolumeToolTip(Slider slider)
    {
        return new ToolTip
        {
            Content = FormatVolume(GetVolumeTitleValue(slider)),
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            PlacementTarget = slider,
            StaysOpen = true
        };
    }

    private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || IsControlDisabled(slider))
        {
            return;
        }

        _isVolumeDragging = true;
        _activeVolumeSlider = slider;
        ShowVolumeToolTip(slider);
    }

    private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            ShowVolumeToolTip(slider);
            BeginVolumeToolTipLinger();
        }
    }

    private void VolumeSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isVolumeDragging && sender is Slider slider)
        {
            ShowVolumeToolTip(slider);
        }
    }

    private void VolumeSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isVolumeDragging)
        {
            BeginVolumeToolTipLinger();
        }
    }

    private void VolumeSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_activeVolumeSlider == sender)
        {
            BeginVolumeToolTipLinger();
        }
    }

    private void ShowVolumeToolTip(Slider slider)
    {
        _volumeToolTipHideTimer.Stop();
        UpdateVolumeToolTip(slider);

        if (slider.ToolTip is ToolTip toolTip)
        {
            toolTip.PlacementTarget = slider;
            toolTip.IsOpen = true;
        }
    }

    private void UpdateVolumeToolTip(Slider slider)
    {
        if (slider.ToolTip is not ToolTip toolTip)
        {
            slider.ToolTip = toolTip = CreateVolumeToolTip(slider);
        }

        toolTip.Content = FormatVolume(GetVolumeTitleValue(slider));
    }

    private void BeginVolumeToolTipLinger()
    {
        _isVolumeDragging = false;
        _volumeToolTipHideTimer.Stop();
        _volumeToolTipHideTimer.Start();
    }

    private void HideVolumeToolTip()
    {
        _volumeToolTipHideTimer.Stop();

        if (_activeVolumeSlider?.ToolTip is ToolTip toolTip)
        {
            toolTip.IsOpen = false;
        }

        _activeVolumeSlider = null;
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
            Snapshot = snapshot;
            _lastSnapshotAt = DateTimeOffset.Now;
            ApplySnapshot(snapshot);
        });
    }

    private void ShowTrackNotification(string title, string artist, ImageSource? coverArt)
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
                ShowTrackNotification(snapshot.Title, snapshot.Description, snapshot.CoverArt);
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

        SetImageSourceIfChanged(ArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(BlurredArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(ExpandedArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(LyricsArtImage, snapshot.CoverArt);
        SetImageSourceIfChanged(LyricsHeaderArtImage, snapshot.CoverArt);
        ArtImage.BeginAnimation(OpacityProperty, null);
        ArtImage.Opacity = 1;
        ArtPlaceholderText.Visibility = snapshot.CoverArt is null ? Visibility.Visible : Visibility.Collapsed;
        LyricsHeaderArtPlaceholderText.Visibility = snapshot.CoverArt is null ? Visibility.Visible : Visibility.Collapsed;
        AlbumArtSurface.ToolTip = snapshot.CoverArt is not null ? "Expand" : null;
        AlbumArtSurface.Cursor = snapshot.CoverArt is not null
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        ApplyArtworkTint(snapshot.CoverArt);

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

        SetControlDisabledState(PreviousButton, snapshot.CanPrevious);
        SetControlDisabledState(ExpandedPreviousButton, snapshot.CanPrevious);
        SetControlDisabledState(LyricsPreviousButton, snapshot.CanPrevious);
        SetControlDisabledState(FullscreenPreviousButton, snapshot.CanPrevious);
        SetControlDisabledState(NextButton, snapshot.CanNext);
        SetControlDisabledState(ExpandedNextButton, snapshot.CanNext);
        SetControlDisabledState(LyricsNextButton, snapshot.CanNext);
        SetControlDisabledState(FullscreenNextButton, snapshot.CanNext);
        var canPlayPause = snapshot.CanPlay || snapshot.CanPause;
        SetControlDisabledState(PlayPauseButton, canPlayPause);
        SetControlDisabledState(ExpandedPlayPauseButton, canPlayPause);
        SetControlDisabledState(LyricsPlayPauseButton, canPlayPause);
        SetControlDisabledState(FullscreenPlayPauseButton, canPlayPause);
        SetPlayPauseButtonState(snapshot.IsPlaying);
        var canSeek = snapshot.CanSeek && snapshot.Duration > TimeSpan.Zero;
        SetControlDisabledState(ProgressSlider, canSeek);
        SetControlDisabledState(ExpandedProgressSlider, canSeek);
        SetControlDisabledState(LyricsProgressSlider, canSeek);
        SetControlDisabledState(FullscreenProgressSlider, canSeek);
        ProgressSlider.Maximum = Math.Max(1, snapshot.Duration.TotalSeconds);
        ExpandedProgressSlider.Maximum = Math.Max(1, snapshot.Duration.TotalSeconds);
        LyricsProgressSlider.Maximum = Math.Max(1, snapshot.Duration.TotalSeconds);
        FullscreenProgressSlider.Maximum = Math.Max(1, snapshot.Duration.TotalSeconds);

        CompactProgressRow.Visibility = snapshot.HasSession ? Visibility.Visible : Visibility.Collapsed;
        if (!snapshot.HasSession)
        {
            ShowVolumeSliders(false);
        }

        if (_isFullscreen)
        {
            SyncFullscreenPlaybackState();
        }

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
        CompactVolumeButton.Visibility = volumeVisibility;
        ExpandedVolumeButton.Visibility = volumeVisibility;
        LyricsVolumeButton.Visibility = volumeVisibility;
        FullscreenVolumeButton.Visibility = volumeVisibility;

        if (!showVolume)
        {
            ShowVolumeSliders(false);
        }

        var lyricsVisibility = ShouldShowLyricsButton(snapshot) ? Visibility.Visible : Visibility.Collapsed;
        CompactLyricsButton.Visibility = lyricsVisibility;
        ExpandedLyricsButton.Visibility = lyricsVisibility;
        FullscreenLyricsToggle.Visibility = lyricsVisibility;
    }

    private bool ShouldShowLyricsButton(MediaSnapshot snapshot)
    {
        return snapshot.HasSession && Lyrics.Status is LyricsStatus.Loading or LyricsStatus.Synced or LyricsStatus.Plain;
    }

    private void SetPlayPauseButtonState(bool isPlaying)
    {
        var state = isPlaying ? "Pause" : "Play";
        var tooltip = isPlaying ? "Pause" : "Play";

        PlayPauseButton.CommandParameter = state;
        ExpandedPlayPauseButton.CommandParameter = state;
        LyricsPlayPauseButton.CommandParameter = state;
        FullscreenPlayPauseButton.CommandParameter = state;
        PlayPauseButton.ToolTip = tooltip;
        ExpandedPlayPauseButton.ToolTip = tooltip;
        LyricsPlayPauseButton.ToolTip = tooltip;
        FullscreenPlayPauseButton.ToolTip = tooltip;
        RefreshPlayPauseButtonImage(PlayPauseButton);
        RefreshPlayPauseButtonImage(ExpandedPlayPauseButton);
        RefreshPlayPauseButtonImage(LyricsPlayPauseButton);
        RefreshPlayPauseButtonImage(FullscreenPlayPauseButton);
    }

    private static void SetControlDisabledState(FrameworkElement element, bool isEnabled)
    {
        element.IsEnabled = true;
        element.Tag = isEnabled ? null : "disabled";
        element.Opacity = isEnabled || IsPlayPauseButton(element) ? 1.0 : (element is Slider ? 0.42 : 0.45);
        element.Cursor = isEnabled
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.No;
        element.ForceCursor = !isEnabled;
    }

    private static bool IsPlayPauseButton(FrameworkElement element)
    {
        return element is Button { Name: "PlayPauseButton" or "ExpandedPlayPauseButton" or "LyricsPlayPauseButton" or "FullscreenPlayPauseButton" };
    }

    private static bool IsControlDisabled(object sender)
    {
        return sender is FrameworkElement fe && (!fe.IsEnabled || fe.Tag is "disabled");
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

        image.Source = GetSiteImageSource($"res/img/{action}{suffix}.png");
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
        var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
        if (clip.CanFreeze)
        {
            clip.Freeze();
        }

        element.Clip = clip;

        if (ReferenceEquals(element, LyricsSurface))
        {
            CenterLyricsBackgroundArtwork(e.NewSize);
        }

        if (ReferenceEquals(element, RootCard))
        {
            ApplyRoundedWindowRegion();
        }
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
        var position = GetCurrentPosition();
        var duration = Snapshot.Duration;

        if (!_isSeeking)
        {
            var progressValue = duration > TimeSpan.Zero
                ? Math.Clamp(position.TotalSeconds, 0, Math.Max(1, duration.TotalSeconds))
                : 0;
            SetSliderValueIfChanged(ProgressSlider, progressValue);
            SetSliderValueIfChanged(ExpandedProgressSlider, progressValue);
            SetSliderValueIfChanged(LyricsProgressSlider, progressValue);
            SetSliderValueIfChanged(FullscreenProgressSlider, progressValue);
        }

        var elapsedText = FormatTime(position);
        SetTextIfChanged(ElapsedText, elapsedText);
        SetTextIfChanged(ExpandedElapsedText, elapsedText);
        SetTextIfChanged(LyricsElapsedText, elapsedText);
        SetTextIfChanged(FullscreenElapsedText, elapsedText);

        var remaining = duration > TimeSpan.Zero ? duration - position : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var remainingText = "-" + FormatTime(remaining);
        SetTextIfChanged(DurationText, remainingText);
        SetTextIfChanged(ExpandedDurationText, remainingText);
        SetTextIfChanged(LyricsDurationText, remainingText);
        SetTextIfChanged(FullscreenDurationText, remainingText);
        UpdateSyncedLyricsUi(position);

        if (_isFullscreen && _isFullscreenLyrics)
        {
            UpdateFullscreenSyncedLyricsUi(position);
        }
    }

    private void TimelineCompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            UpdateTimelineUi();
        }
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

    private void RenderLyricsState()
    {
        LyricsStatusText.Text = Lyrics.Message;
        LyricsStackPanel.Children.Clear();
        _lyricBlocks.Clear();
        _lyricWaitIndicators.Clear();
        _fullscreenLyricWaitIndicators.Clear();
        _lyricsFooterPanel = null;
        _activeLyricIndex = -1;
        _activeLyricWaitIndicatorIndex = -1;
        _activeFullscreenLyricIndex = -1;
        _activeFullscreenLyricWaitIndicatorIndex = -1;
        _isUserBrowsingLyrics = false;
        _lastLyricsPosition = TimeSpan.Zero;
        StopLyricsScrollAnimation();
        LyricsScrollViewer.ScrollToVerticalOffset(0);
        SetLyricsMessageIcon(Lyrics.Status);

        RenderFullscreenLyricsState();

        var hasLyrics = Lyrics.Status is LyricsStatus.Synced or LyricsStatus.Plain;
        var isLoading = Lyrics.Status == LyricsStatus.Loading;
        var lyricsButtonVisibility = ShouldShowLyricsButton(Snapshot) ? Visibility.Visible : Visibility.Collapsed;
        CompactLyricsButton.Visibility = lyricsButtonVisibility;
        ExpandedLyricsButton.Visibility = lyricsButtonVisibility;
        FullscreenLyricsToggle.Visibility = lyricsButtonVisibility;

        if (Lyrics.Status == LyricsStatus.Synced)
        {
            LyricsMessagePanel.Visibility = Visibility.Collapsed;
            for (var i = 0; i < Lyrics.SyncedLines.Count; i++)
            {
                var line = Lyrics.SyncedLines[i];
                var previousLineTime = i == 0 ? TimeSpan.Zero : Lyrics.SyncedLines[i - 1].Time;
                AddLyricWaitIndicatorIfNeeded(i, previousLineTime, line.Time);
                AddLyricBlock(line.Text, 22, 0.30);
            }

            AddLyricsInfoFooter();
            SetCompactLyricText("Lyrics ready");
            Dispatcher.BeginInvoke(() => UpdateSyncedLyricsUi(GetCurrentPosition()), DispatcherPriority.Loaded);
            if (_isFullscreen && _isFullscreenLyrics)
            {
                Dispatcher.BeginInvoke(() => UpdateFullscreenSyncedLyricsUi(GetCurrentPosition()), DispatcherPriority.Loaded);
            }
            return;
        }

        if (Lyrics.Status == LyricsStatus.Plain)
        {
            LyricsMessagePanel.Visibility = Visibility.Collapsed;
            foreach (var line in Lyrics.PlainLines)
            {
                AddLyricBlock(line, 19, 0.76);
            }

            AddLyricsInfoFooter();
            SetPlainLyricStyle();
            SetCompactLyricText(Lyrics.PlainLines.FirstOrDefault() ?? "Unsynced lyrics");
            return;
        }

        LyricsMessagePanel.Visibility = Visibility.Visible;
        LyricsMessageText.Text = Lyrics.Message;
        SetCompactLyricText(Lyrics.Message);

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
        var directory = Path.Combine(AppContext.BaseDirectory, "res", "img");
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

    private void AddLyricBlock(string text, double fontSize, double opacity)
    {
        AddLyricBlock(
            LyricsStackPanel,
            _lyricBlocks,
            text,
            fontSize,
            opacity,
            new Thickness(0, 13, 0, 13),
            Color.FromRgb(124, 132, 132));
    }

    private static void AddLyricBlock(
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
    }

    private void AddLyricWaitIndicatorIfNeeded(int lineIndex, TimeSpan previousLineTime, TimeSpan nextLineTime)
    {
        AddLyricWaitIndicatorIfNeeded(
            lineIndex,
            previousLineTime,
            nextLineTime,
            LyricsStackPanel,
            _lyricWaitIndicators,
            isFullscreen: false);
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

    private bool UpdateLyricWaitIndicators(TimeSpan position)
    {
        return UpdateLyricWaitIndicators(
            _lyricWaitIndicators,
            position,
            ref _activeLyricWaitIndicatorIndex,
            _activeLyricIndex,
            _isUserBrowsingLyrics,
            isFullscreen: false);
    }

    private bool UpdateLyricWaitIndicators(
        IReadOnlyList<LyricWaitIndicator> indicators,
        TimeSpan position,
        ref int activeWaitIndicatorIndex,
        int activeLyricIndex,
        bool isUserBrowsing,
        bool isFullscreen)
    {
        if (indicators.Count == 0)
        {
            return false;
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

        return changed;
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

    private void SetPlainLyricStyle()
    {
        foreach (var block in _lyricBlocks)
        {
            block.Foreground = new SolidColorBrush(Color.FromRgb(221, 226, 226));
            block.FontWeight = FontWeights.SemiBold;
            block.Margin = new Thickness(0, 10, 0, 10);
            block.Opacity = 0.72;
        }
    }

    private void UpdateSyncedLyricsUi(TimeSpan position)
    {
        if (Lyrics.Status != LyricsStatus.Synced || Lyrics.SyncedLines.Count == 0)
        {
            return;
        }

        var stabilizedPosition = StabilizeLyricPosition(position);
        var activeWaitChanged = UpdateLyricWaitIndicators(stabilizedPosition);
        var activeIndex = FindActiveLyricIndex(Lyrics.SyncedLines, stabilizedPosition + LyricActivationLead);
        if (activeIndex == _activeLyricIndex)
        {
            if (activeWaitChanged)
            {
                ApplyLyricBlockVisualState(activeIndex);
                CenterActiveLyricWaitIndicator();
            }

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

        if (activeIndex >= 0 && activeIndex < Lyrics.SyncedLines.Count)
        {
            SetCompactLyricText(Lyrics.SyncedLines[activeIndex].Text);
        }
        else
        {
            SetCompactLyricText("Lyrics ready");
        }

        Dispatcher.BeginInvoke(() => CenterActiveLyric(activeIndex), DispatcherPriority.Loaded);
    }

    private void ApplyLyricBlockVisualState(int activeIndex)
    {
        var shouldHidePreviousFinalLine = !_isUserBrowsingLyrics && activeIndex == _lyricBlocks.Count - 1;
        var hasActiveWaitIndicator = !_isUserBrowsingLyrics && _activeLyricWaitIndicatorIndex >= 0;
        for (var i = 0; i < _lyricBlocks.Count; i++)
        {
            var block = _lyricBlocks[i];
            var distance = activeIndex < 0 ? 4 : Math.Abs(i - activeIndex);
            var isActive = !hasActiveWaitIndicator && i == activeIndex;
            var targetOpacity = shouldHidePreviousFinalLine && i == activeIndex - 1
                ? 0.0
                : isActive ? 1.0 : hasActiveWaitIndicator && i == activeIndex ? 0.44 : distance == 1 ? 0.48 : distance == 2 ? 0.28 : 0.18;
            var targetColor = isActive
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
            AnimateDouble(block, OpacityProperty, targetOpacity, 380);
        }
    }

    private void CenterActiveLyric(int activeIndex)
    {
        if (activeIndex < 0 || activeIndex >= _lyricBlocks.Count || LyricsScrollViewer.ViewportHeight <= 1)
        {
            return;
        }

        CenterLyricsElement(_lyricBlocks[activeIndex]);
    }

    private void CenterActiveLyricWaitIndicator()
    {
        if (_isUserBrowsingLyrics ||
            _activeLyricWaitIndicatorIndex < 0 ||
            _activeLyricWaitIndicatorIndex >= _lyricWaitIndicators.Count)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => CenterLyricsElement(_lyricWaitIndicators[_activeLyricWaitIndicatorIndex].Element),
            DispatcherPriority.Loaded);
    }

    private void CenterLyricsElement(FrameworkElement element)
    {
        if (LyricsScrollViewer.ViewportHeight <= 1)
        {
            return;
        }

        try
        {
            LyricsStackPanel.UpdateLayout();
            var elementTop = element.TransformToVisual(LyricsStackPanel).Transform(new Point(0, 0)).Y;
            var elementExtent = element.ActualHeight;
            var viewport = LyricsScrollViewer.ViewportHeight;
            var max = GetLyricsScrollMaxOffset();
            var target = LyricsStackVerticalPadding + elementTop + (elementExtent * 0.5) - (viewport * 0.43);
            AnimateLyricsScrollTo(Math.Clamp(target, 0, max));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SetCompactLyricText(string text)
    {
    }

    private void LyricsViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Lyrics.Status is not (LyricsStatus.Synced or LyricsStatus.Plain) || _lyricBlocks.Count == 0)
        {
            return;
        }

        e.Handled = true;
        _isUserBrowsingLyrics = true;
        StopLyricsScrollAnimation();

        if (Lyrics.Status == LyricsStatus.Synced)
        {
            ApplyLyricBlockVisualState(_activeLyricIndex);
        }

        var target = LyricsScrollViewer.VerticalOffset + (e.Delta > 0 ? -42 : 42);
        var max = GetLyricsScrollMaxOffset();
        LyricsScrollViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, max));
    }

    private void AnimateLyricsScrollTo(double target)
    {
        _lyricsScrollTarget = Math.Clamp(target, 0, GetLyricsScrollMaxOffset());

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
            _fullscreenLyricsScrollTarget = Math.Clamp(_fullscreenLyricsScrollTarget, 0, GetFullscreenLyricsScrollMaxOffset());
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

    private double GetLyricsScrollMaxOffset()
    {
        var max = Math.Max(0, LyricsScrollViewer.ExtentHeight - LyricsScrollViewer.ViewportHeight);
        if (_lyricsFooterPanel is null || LyricsScrollViewer.ViewportHeight <= 1)
        {
            return max;
        }

        try
        {
            LyricsStackPanel.UpdateLayout();
            var footerTop = _lyricsFooterPanel.TransformToAncestor(LyricsStackPanel).Transform(new Point(0, 0)).Y;
            return Math.Clamp(footerTop - 12, 0, max);
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

    private double GetFullscreenLyricsScrollMaxOffset()
    {
        var max = Math.Max(0, FullscreenLyricsScrollViewer.ExtentHeight - FullscreenLyricsScrollViewer.ViewportHeight);
        if (_fullscreenLyricsFooterPanel is null || FullscreenLyricsScrollViewer.ViewportHeight <= 1)
        {
            return max;
        }

        try
        {
            var artCenterY = GetCurrentArtCenterY();
            FullscreenLyricsStackPanel.UpdateLayout();
            var footerTop = _fullscreenLyricsFooterPanel.TransformToAncestor(FullscreenLyricsStackPanel).Transform(new Point(0, 0)).Y;
            return Math.Clamp(footerTop - artCenterY, 0, max);
        }
        catch (InvalidOperationException)
        {
            return max;
        }
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
        if (Snapshot.CoverArt is null)
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
        if (Snapshot.CoverArt is not null)
        {
            SetExpandedMode(true);
        }
    }

    private void CollapseExpandedButton_Click(object sender, RoutedEventArgs e)
    {
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
        var tint = ExtractDominantTint(coverArt) ?? DefaultTint;

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

        var position = Snapshot.Position;
        if (Snapshot.IsPlaying)
        {
            position += DateTimeOffset.Now - _lastSnapshotAt;
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return position > Snapshot.Duration ? Snapshot.Duration : position;
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
        Topmost = !Topmost;
        SetTopmostUi();
        _settingsService.Settings.Behavior.AlwaysOnTop = Topmost;
        _settingsService.Save(_settingsService.Settings);
    }

    private void SetTopmostUi()
    {
        TopmostButton.Opacity = Topmost ? 1 : 0.45;
        TopmostButton.ToolTip = Topmost ? "Always on top" : "Normal window";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMinimizing)
        {
            return;
        }

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
        if (WindowState != WindowState.Minimized)
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
        if (_isClosing)
        {
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
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsControlDisabled(sender))
        {
            e.Handled = true;
            return;
        }

        _isSeeking = true;
    }

    private async void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsControlDisabled(sender))
        {
            _isSeeking = false;
            e.Handled = true;
            return;
        }

        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        var slider = sender as Slider ?? ProgressSlider;
        await _mediaService.SeekAsync(TimeSpan.FromSeconds(slider.Value));
    }

    private void SetTransportEnabled(bool isEnabled)
    {
        SetControlDisabledState(PlayPauseButton, isEnabled);
        SetControlDisabledState(ExpandedPlayPauseButton, isEnabled);
        SetControlDisabledState(LyricsPlayPauseButton, isEnabled);
        SetControlDisabledState(NextButton, isEnabled);
        SetControlDisabledState(ExpandedNextButton, isEnabled);
        SetControlDisabledState(LyricsNextButton, isEnabled);
        SetControlDisabledState(PreviousButton, isEnabled);
        SetControlDisabledState(ExpandedPreviousButton, isEnabled);
        SetControlDisabledState(LyricsPreviousButton, isEnabled);
        SetControlDisabledState(ProgressSlider, isEnabled);
        SetControlDisabledState(ExpandedProgressSlider, isEnabled);
        SetControlDisabledState(LyricsProgressSlider, isEnabled);
        if (!isEnabled)
        {
            PlayPauseButton.ToolTip = "Play";
            ExpandedPlayPauseButton.ToolTip = "Play";
            LyricsPlayPauseButton.ToolTip = "Play";
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
        CompactVolumeSlider.Value = vol;
        ExpandedVolumeSlider.Value = vol;
        LyricsVolumeSlider.Value = vol;
        FullscreenVolumeSlider.Value = vol;
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
        CompactVolumeSlider.Value = vol;
        ExpandedVolumeSlider.Value = vol;
        LyricsVolumeSlider.Value = vol;
        FullscreenVolumeSlider.Value = vol;
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
        
        if (sender != CompactVolumeSlider && CompactVolumeSlider != null) 
            CompactVolumeSlider.Value = val;
        if (sender != ExpandedVolumeSlider && ExpandedVolumeSlider != null) 
            ExpandedVolumeSlider.Value = val;
        if (sender != LyricsVolumeSlider && LyricsVolumeSlider != null) 
            LyricsVolumeSlider.Value = val;
        if (sender != FullscreenVolumeSlider && FullscreenVolumeSlider != null)
            FullscreenVolumeSlider.Value = val;
            
        _volumeUpdating = false;
        UpdateVolumeIcon();
        RefreshAllVolumeToolTips();
        if (_isVolumeDragging && sender is Slider activeSlider)
        {
            ShowVolumeToolTip(activeSlider);
        }
    }

    private void RefreshAllVolumeToolTips()
    {
        if (CompactVolumeSlider is not null)
        {
            UpdateVolumeToolTip(CompactVolumeSlider);
        }

        if (ExpandedVolumeSlider is not null)
        {
            UpdateVolumeToolTip(ExpandedVolumeSlider);
        }

        if (LyricsVolumeSlider is not null)
        {
            UpdateVolumeToolTip(LyricsVolumeSlider);
        }

        if (FullscreenVolumeSlider is not null)
        {
            UpdateVolumeToolTip(FullscreenVolumeSlider);
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
        var source = GetSiteImageSource($"res/img/{icon}");
        SetVolumeIcon(CompactVolumeButton, source);
        SetVolumeIcon(ExpandedVolumeButton, source);
        SetVolumeIcon(LyricsVolumeButton, source);
        SetVolumeIcon(FullscreenVolumeButton, source);
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
        CompactVolumeSlider.Visibility = vis;
        ExpandedVolumeSlider.Visibility = vis;
        LyricsVolumeSlider.Visibility = vis;
        FullscreenVolumeSlider.Visibility = vis;
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
        CompactInfoButton.Visibility = Visibility.Visible;
        ExpandedInfoButton.Visibility = Visibility.Visible;
        LyricsInfoButton.Visibility = Visibility.Visible;
        FullscreenInfoButton.Visibility = Visibility.Visible;
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
        var lastFmInfo = CurrentLastFmInfo;
        if (lastFmInfo is not null)
        {
            var lastFmItem = new MenuItem
            {
                Header = $"View \"{lastFmInfo.TrackName}\" on Last.fm",
                Icon = new Image
                {
                    Source = GetSiteImageSource("res/img/lastfm.png"),
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
            menu.Items.Add(new Separator());
        }

        var settingsItem = new MenuItem
        {
            Header = "Settings",
            Icon = CreateMenuIcon("res/settings.ico")
        };
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(settingsItem);

        var aboutItem = new MenuItem
        {
            Header = "About",
            Icon = CreateMenuIcon("res/img/info.ico")
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

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        PushDisableTopmost();

        _settingsWindow = new SettingsWindow(_settingsService, _lastFmService)
        {
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        if (IsVisible)
        {
            _settingsWindow.Owner = this;
        }

        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            PopRestoreTopmost();
        };
        _settingsWindow.Show();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ResetScrobblingState();
        RefreshLastFmForSnapshot(Snapshot, force: true);
        UpdateScrobblingForSnapshot(Snapshot, force: true);
        Topmost = _settingsService.Settings.Behavior.AlwaysOnTop;
        SetTopmostUi();
    }

    private void InitializeTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "res", "ico.ico");
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
            Icon = CreateMenuIcon("res/settings.ico")
        };
        settingsItem.Click += (_, _) =>
        {
            RestoreFromTray();
            ShowSettingsWindow();
        };

        var exitItem = new MenuItem
        {
            Header = "Exit",
            Icon = CreateMenuIcon("res/exit.ico")
        };
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(settingsItem);
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
            Icon = CreateMenuIcon("res/ico.ico"),
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

    private void ToggleScrobblingFromTray()
    {
        var current = _settingsService.Settings;
        if (!current.LastFm.IsConfigured)
        {
            ShowSettingsWindow();
            return;
        }

        _settingsService.Save(new AppSettings
        {
            LastFm = new LastFmCredentials
            {
                Enabled = current.LastFm.Enabled,
                ApiKey = current.LastFm.ApiKey,
                ApiSecret = current.LastFm.ApiSecret,
                Username = current.LastFm.Username,
                Password = current.LastFm.Password,
                ScrobblingEnabled = !current.LastFm.ScrobblingEnabled
            },
            Behavior = new BehaviorSettings
            {
                CloseToTray = current.Behavior.CloseToTray,
                EnableNotifications = current.Behavior.EnableNotifications,
                AlwaysOnTop = current.Behavior.AlwaysOnTop,
                StartWithWindows = current.Behavior.StartWithWindows,
                CheckForUpdatesOnStartup = current.Behavior.CheckForUpdatesOnStartup
            }
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
        _allowClose = true;
        _trayContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _trayContextMenu = null;
        _settingsWindow?.Close();
        ShowInTaskbar = false;
        Close();
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
        if (!IsFinite(Left) || !IsFinite(Top) || WindowState == WindowState.Minimized)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WindowPlacementPath)!);
            var placement = new WindowPlacement(Left, Top);
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
        _mediaPollTimer.Stop();
        StopLyricsScrollAnimation();
        _loadingIconTimer.Stop();
        _volumeToolTipHideTimer.Stop();
        _lyricsRefreshCts?.Cancel();
        _lyricsRefreshCts?.Dispose();
        _lastFmCts?.Cancel();
        _lastFmCts?.Dispose();
        _lastFmScrobbleCts?.Cancel();
        _lastFmScrobbleCts?.Dispose();
        _settingsWindow?.Close();
        _settingsService.SettingsChanged -= OnSettingsChanged;
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
        _lyricsService.Dispose();
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
        if (_isFullscreen)
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

    private const int GwlStyle = -16;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int SwMinimize = 6;

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

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullscreen)
        {
            SetFullscreenMode(false);
            e.Handled = true;
        }
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        SetFullscreenMode(!_isFullscreen);
    }

    private void FullscreenLyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        SetFullscreenLyricsVisible(!_isFullscreenLyrics);
    }

    private void SetFullscreenMode(bool isFullscreen)
    {
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

            // Recycle ChromeButtons: hide collapse, topmost, and minimize
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
                    MinimizeButton.Visibility = Visibility.Visible;
                    TopmostButton.Visibility = Visibility.Visible;
                    FullscreenButtonPath.Data = Geometry.Parse("M 1,6 L 1,1 L 6,1 M 1,1 L 7,7 M 14,9 L 14,14 L 9,14 M 14,14 L 8,8");
                    FullscreenButton.ToolTip = "Fullscreen";

                    // Restore immersion borders
                    RootCard.BorderThickness = new Thickness(1);
                    InnerGlassBorder.BorderThickness = new Thickness(1);
                    
                    MinWidth = 218;
                    MinHeight = 144;
                    MaxWidth = 430;
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

                    PlayOpenAnimation();
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

        var targetInfoSize = _isFullscreenLyrics ? 460.0 : 560.0;
        AnimateDouble(FullscreenArtBorder, WidthProperty, targetInfoSize, 300);
        AnimateDouble(FullscreenArtBorder, HeightProperty, targetInfoSize, 300);

        if (_isFullscreenLyrics)
        {
            FullscreenSpacerColumn.Width = new GridLength(80);
            FullscreenLyricsColumn.Width = new GridLength(1.5, GridUnitType.Star);
            FullscreenLyricsPanel.Visibility = Visibility.Visible;
            
            AnimateElementOpacity(FullscreenLyricsPanel, 1, 300);
            
            RenderFullscreenLyricsState();
            UpdateFullscreenSyncedLyricsUi(GetCurrentPosition());
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

    private void SyncFullscreenPlaybackState()
    {
        FullscreenTitleText.Text = Snapshot.Title;
        FullscreenArtistText.Text = Snapshot.Description;
        SetImageSourceIfChanged(FullscreenArtImage, Snapshot.CoverArt);
        SetImageSourceIfChanged(FullscreenBlurredArtImage, Snapshot.CoverArt);
        FullscreenArtPlaceholder.Visibility = Snapshot.CoverArt is null ? Visibility.Visible : Visibility.Collapsed;
        
        ApplyArtworkTint(Snapshot.CoverArt);
        
        SetControlDisabledState(FullscreenPreviousButton, Snapshot.CanPrevious);
        SetControlDisabledState(FullscreenNextButton, Snapshot.CanNext);
        var canPlayPause = Snapshot.CanPlay || Snapshot.CanPause;
        SetControlDisabledState(FullscreenPlayPauseButton, canPlayPause);
        SetPlayPauseButtonState(Snapshot.IsPlaying);
        
        var canSeek = Snapshot.CanSeek && Snapshot.Duration > TimeSpan.Zero;
        SetControlDisabledState(FullscreenProgressSlider, canSeek);
        FullscreenProgressSlider.Maximum = Math.Max(1, Snapshot.Duration.TotalSeconds);
        
        if (_volumeService.IsAvailable)
        {
            _volumeUpdating = true;
            FullscreenVolumeSlider.Value = GetDisplayedVolumeValue();
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
        var artCenterY = GetCurrentArtCenterY();
        
        if (status == LyricsStatus.Synced)
        {
            FullscreenLyricsMessagePanel.Visibility = Visibility.Collapsed;
            
            // Add top spacer
            _fullscreenLyricsTopSpacer = new Border { Height = artCenterY, Opacity = 0, IsHitTestVisible = false };
            FullscreenLyricsStackPanel.Children.Add(_fullscreenLyricsTopSpacer);

            for (var i = 0; i < Lyrics.SyncedLines.Count; i++)
            {
                var line = Lyrics.SyncedLines[i];
                var previousLineTime = i == 0 ? TimeSpan.Zero : Lyrics.SyncedLines[i - 1].Time;
                AddLyricWaitIndicatorIfNeeded(
                    i,
                    previousLineTime,
                    line.Time,
                    FullscreenLyricsStackPanel,
                    _fullscreenLyricWaitIndicators,
                    isFullscreen: true);
                AddLyricBlock(
                    FullscreenLyricsStackPanel,
                    _fullscreenLyricBlocks,
                    line.Text,
                    38,
                    0.35,
                    new Thickness(0, 22, 0, 22),
                    Color.FromRgb(150, 158, 158));
            }
            
            AddFullscreenLyricsInfoFooter();
            
            // Add bottom spacer
            _fullscreenLyricsBottomSpacer = new Border { Height = artCenterY, Opacity = 0, IsHitTestVisible = false };
            FullscreenLyricsStackPanel.Children.Add(_fullscreenLyricsBottomSpacer);
            return;
        }
        
        if (status == LyricsStatus.Plain)
        {
            FullscreenLyricsMessagePanel.Visibility = Visibility.Collapsed;
            
            // Add top spacer
            _fullscreenLyricsTopSpacer = new Border { Height = artCenterY, Opacity = 0, IsHitTestVisible = false };
            FullscreenLyricsStackPanel.Children.Add(_fullscreenLyricsTopSpacer);

            foreach (var line in Lyrics.PlainLines)
            {
                AddLyricBlock(
                    FullscreenLyricsStackPanel,
                    _fullscreenLyricBlocks,
                    line,
                    32,
                    0.70,
                    new Thickness(0, 22, 0, 22),
                    Color.FromRgb(150, 158, 158));
            }
            
            AddFullscreenLyricsInfoFooter();
            
            // Add bottom spacer
            _fullscreenLyricsBottomSpacer = new Border { Height = artCenterY, Opacity = 0, IsHitTestVisible = false };
            FullscreenLyricsStackPanel.Children.Add(_fullscreenLyricsBottomSpacer);
            return;
        }

        FullscreenLyricsMessagePanel.Visibility = Visibility.Visible;
        FullscreenLyricsMessageText.Text = Lyrics.Message;
    }

    private bool UpdateFullscreenLyricWaitIndicators(TimeSpan position)
    {
        return UpdateLyricWaitIndicators(
            _fullscreenLyricWaitIndicators,
            position,
            ref _activeFullscreenLyricWaitIndicatorIndex,
            _activeFullscreenLyricIndex,
            _isUserBrowsingFullscreenLyrics,
            isFullscreen: true);
    }

    private void CenterFullscreenElement(FrameworkElement element)
    {
        if (FullscreenLyricsScrollViewer.ViewportHeight <= 1)
        {
            return;
        }

        try
        {
            var artCenterY = GetCurrentArtCenterY();
            FullscreenLyricsStackPanel.UpdateLayout();
            var elementTop = element.TransformToVisual(FullscreenLyricsStackPanel).Transform(new Point(0, 0)).Y;
            var elementExtent = element.ActualHeight;
            var max = GetFullscreenLyricsScrollMaxOffset();
            
            var target = elementTop + (elementExtent * 0.5) - artCenterY;
            
            _fullscreenLyricsScrollTarget = Math.Clamp(target, 0, max);
            if (!_isLyricsScrollAnimating)
            {
                StartLyricsScrollAnimation();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void UpdateFullscreenSyncedLyricsUi(TimeSpan position)
    {
        if (Lyrics.Status != LyricsStatus.Synced || Lyrics.SyncedLines.Count == 0)
        {
            return;
        }

        var stabilizedPosition = StabilizeLyricPosition(position);
        var activeWaitChanged = UpdateFullscreenLyricWaitIndicators(stabilizedPosition);

        if (activeWaitChanged && _activeFullscreenLyricWaitIndicatorIndex >= 0)
        {
            Dispatcher.BeginInvoke(() => CenterFullscreenElement(_fullscreenLyricWaitIndicators[_activeFullscreenLyricWaitIndicatorIndex].Element), DispatcherPriority.Loaded);
        }

        var activeIndex = FindActiveLyricIndex(Lyrics.SyncedLines, stabilizedPosition + LyricActivationLead);
        if (activeIndex == _activeFullscreenLyricIndex)
        {
            return;
        }

        _isUserBrowsingFullscreenLyrics = false;
        _fullscreenLyricsInactivityTimer.Stop();
        _activeFullscreenLyricIndex = activeIndex;
        ApplyFullscreenLyricBlockVisualState(activeIndex);

        if (_activeFullscreenLyricWaitIndicatorIndex < 0)
        {
            Dispatcher.BeginInvoke(() => CenterFullscreenActiveLyric(activeIndex), DispatcherPriority.Loaded);
        }
    }

    private void ApplyFullscreenLyricBlockVisualState(int activeIndex)
    {
        for (var i = 0; i < _fullscreenLyricBlocks.Count; i++)
        {
            var block = _fullscreenLyricBlocks[i];
            var isActive = i == activeIndex;
            
            double targetOpacity = 0.0;
            if (isActive)
            {
                targetOpacity = 1.0;
            }
            else
            {
                if (_isUserBrowsingFullscreenLyrics)
                {
                    targetOpacity = 0.35;
                }
                else
                {
                    if (i > activeIndex)
                    {
                        targetOpacity = 0.35;
                    }
                    else
                    {
                        targetOpacity = 0.0;
                    }
                }
            }

            var targetColor = isActive
                ? Color.FromRgb(255, 255, 255)
                : Color.FromRgb(180, 185, 185);

            if (block.Foreground is SolidColorBrush existingBrush && !existingBrush.IsFrozen)
            {
                AnimateBrushColor(existingBrush, targetColor);
            }
            else
            {
                block.Foreground = new SolidColorBrush(targetColor);
            }

            block.FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold;
            AnimateDouble(block, OpacityProperty, targetOpacity, _isUserBrowsingFullscreenLyrics ? 150 : 380);
        }
    }

    private void CenterFullscreenActiveLyric(int activeIndex)
    {
        if (activeIndex < 0 || activeIndex >= _fullscreenLyricBlocks.Count || FullscreenLyricsScrollViewer.ViewportHeight <= 1)
        {
            return;
        }

        CenterFullscreenElement(_fullscreenLyricBlocks[activeIndex]);
    }

    private void FullscreenLyricsViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Lyrics.Status is not (LyricsStatus.Synced or LyricsStatus.Plain) || _fullscreenLyricBlocks.Count == 0)
        {
            return;
        }

        e.Handled = true;
        _isUserBrowsingFullscreenLyrics = true;
        _fullscreenLyricsInactivityTimer.Stop();
        _fullscreenLyricsInactivityTimer.Start();
        
        StopLyricsScrollAnimation();

        ApplyFullscreenLyricBlockVisualState(_activeFullscreenLyricIndex);

        var target = FullscreenLyricsScrollViewer.VerticalOffset + (e.Delta > 0 ? -60 : 60);
        var max = GetFullscreenLyricsScrollMaxOffset();
        FullscreenLyricsScrollViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, max));
    }

    private void FullscreenLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreen && _isFullscreenLyrics)
        {
            // GetCurrentArtCenterY will dynamically update the spacer heights if they changed
            var artCenterY = GetCurrentArtCenterY();
            
            if (!_isUserBrowsingFullscreenLyrics)
            {
                if (_activeFullscreenLyricWaitIndicatorIndex >= 0 && _fullscreenLyricWaitIndicators.Count > _activeFullscreenLyricWaitIndicatorIndex)
                {
                    CenterFullscreenElement(_fullscreenLyricWaitIndicators[_activeFullscreenLyricWaitIndicatorIndex].Element);
                }
                else if (_activeFullscreenLyricIndex >= 0 && _fullscreenLyricBlocks.Count > _activeFullscreenLyricIndex)
                {
                    CenterFullscreenActiveLyric(_activeFullscreenLyricIndex);
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
