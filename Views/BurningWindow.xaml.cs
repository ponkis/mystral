using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Mystral.Models;
using Mystral.Services;
using static Mystral.Services.ArtworkTint;

namespace Mystral.Views;

public partial class BurningWindow : Window
{
    private enum DiscVisibilityState
    {
        Visible,
        Hiding,
        Hidden,
        Showing
    }

    private static readonly Color DefaultTint = Color.FromRgb(74, 82, 88);
    private BurnTrackDraft _draft;
    private readonly AudioTagService _audioTagService;
    private readonly ImageArtworkLoader _artworkLoader;
    private readonly AppSettingsService _settingsService;
    private readonly GlobeConnectionService _globeConnectionService;
    private readonly MusicBrainzService _musicBrainzService = new();
    private readonly LyricsService _lyricsService = new();
    private readonly BitmapSource _placeholderArtwork;
    private readonly BitmapSource _defaultDiscArtwork;
    private CancellationTokenSource? _operationCts;
    private BurnEditorState _baselineState;
    private string? _coverArtworkHash;
    private string? _discArtworkHash;
    private bool _isInitialized;
    private bool _hasUnsavedChanges;
    private bool _isBusy;
    private bool _isSaving;
    private bool _isClosingConfirmed;
    private bool _isCloseRequestPending;
    private bool _isCloseAnimationRunning;
    private bool _isCloseCommitted;
    private bool _isClosed;
    private bool _isDiscUploadTilePointerDown;
    private bool _hasPlayedInitialOpenAnimation;
    private int _discVisibilityGeneration;
    private DiscVisibilityState _discVisibilityState = DiscVisibilityState.Visible;

    public BurningWindow(
        BurnTrackDraft draft,
        AudioTagService audioTagService,
        ImageArtworkLoader artworkLoader,
        AppSettingsService settingsService,
        GlobeConnectionService globeConnectionService)
    {
        _draft = draft;
        _audioTagService = audioTagService;
        _artworkLoader = artworkLoader;
        _settingsService = settingsService;
        _globeConnectionService = globeConnectionService;
        _placeholderArtwork = artworkLoader.LoadApplicationImage("unknown.png");
        _defaultDiscArtwork = artworkLoader.LoadApplicationImage("cd_default_cover.png");

        InitializeComponent();
        Closed += Window_Closed;
        RootCard.Opacity = 0;
        WindowScale.ScaleX = 0.96;
        WindowScale.ScaleY = 0.96;
        LoadDraftIntoEditor(draft);
    }

    internal event EventHandler? PresentationChanged;

    internal event EventHandler? TrackReplaced;

    internal event EventHandler? BurnCompleted;

    internal event EventHandler? CloseRequestCanceled;

    internal string PresentationTitle => TitleBox.Text.Trim();

    internal string PresentationArtist => ArtistBox.Text.Trim();

    internal BitmapSource? PresentationCover => _draft.CoverArtwork?.Preview;

    internal BitmapSource PresentationDisc => _draft.DiscArtwork?.Preview ?? _defaultDiscArtwork;

    internal void HideForDiscRemoval()
    {
        if (_isClosed
            || _isCloseCommitted
            || _isCloseRequestPending
            || _isCloseAnimationRunning
            || _discVisibilityState is DiscVisibilityState.Hidden or DiscVisibilityState.Hiding)
        {
            return;
        }

        if (!IsVisible)
        {
            _discVisibilityState = DiscVisibilityState.Hidden;
            return;
        }

        var fromOpacity = RootCard.Opacity;
        var fromScaleX = WindowScale.ScaleX;
        var fromScaleY = WindowScale.ScaleY;
        StopPresentationAnimations(fromOpacity, fromScaleX, fromScaleY);

        var generation = ++_discVisibilityGeneration;
        _discVisibilityState = DiscVisibilityState.Hiding;
        var duration = TimeSpan.FromMilliseconds(155);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        WindowScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromScaleX, 0.94, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        WindowScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScaleY, 0.94, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);

        var fade = new DoubleAnimation(fromOpacity, 0, duration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (generation != _discVisibilityGeneration
                || _discVisibilityState != DiscVisibilityState.Hiding
                || _isClosed
                || _isCloseAnimationRunning)
            {
                return;
            }

            Hide();
            StopPresentationAnimations(0, 0.96, 0.96);
            _discVisibilityState = DiscVisibilityState.Hidden;
        };
        RootCard.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    internal void ShowAfterDiscInsertion()
    {
        if (_isClosed || _isCloseCommitted || _isCloseAnimationRunning)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (_discVisibilityState == DiscVisibilityState.Visible && IsVisible)
        {
            Activate();
            return;
        }

        var wasVisible = IsVisible;
        var fromOpacity = wasVisible ? RootCard.Opacity : 0;
        var fromScaleX = wasVisible ? WindowScale.ScaleX : 0.96;
        var fromScaleY = wasVisible ? WindowScale.ScaleY : 0.96;
        var generation = ++_discVisibilityGeneration;
        _discVisibilityState = DiscVisibilityState.Showing;
        StopPresentationAnimations(fromOpacity, fromScaleX, fromScaleY);
        if (!wasVisible)
        {
            Show();
        }

        void BeginAnimation()
        {
            if (generation != _discVisibilityGeneration
                || _discVisibilityState != DiscVisibilityState.Showing
                || _isClosed
                || _isCloseAnimationRunning)
            {
                return;
            }

            StartDiscShowAnimation(generation, fromOpacity, fromScaleX, fromScaleY);
        }

        if (wasVisible)
        {
            BeginAnimation();
        }
        else
        {
            Dispatcher.BeginInvoke(BeginAnimation, DispatcherPriority.Loaded);
        }

        Activate();
    }

    internal void PrepareForCloseRequest()
    {
        if (_isClosed || _isCloseCommitted || _isCloseAnimationRunning)
        {
            return;
        }

        _discVisibilityGeneration++;
        _discVisibilityState = DiscVisibilityState.Visible;
        StopPresentationAnimations(1, 1, 1);
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_hasPlayedInitialOpenAnimation)
        {
            _hasPlayedInitialOpenAnimation = true;
            PlayOpenAnimation();
        }
        await RefreshArtworkUiAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isCloseCommitted)
        {
            return;
        }

        e.Cancel = true;
        if (_isCloseRequestPending || _isCloseAnimationRunning)
        {
            return;
        }

        _discVisibilityGeneration++;
        _discVisibilityState = DiscVisibilityState.Visible;
        StopPresentationAnimations(1, 1, 1);

        if (_isBusy)
        {
            CloseRequestCanceled?.Invoke(this, EventArgs.Empty);
            return;
        }

        _isCloseRequestPending = true;
        try
        {
            if (!_isClosingConfirmed && _hasUnsavedChanges)
            {
                var result = AppDialogWindow.ShowQuestion(
                    this,
                    "Unsaved changes",
                    "Save your CD metadata before closing?");
                if (result == MessageBoxResult.Cancel)
                {
                    CloseRequestCanceled?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (result == MessageBoxResult.No)
                {
                    _isClosingConfirmed = true;
                }
                else
                {
                    if (!await SaveBurnedFileAsync())
                    {
                        CloseRequestCanceled?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    _isClosingConfirmed = true;
                }
            }

            PlayCloseAnimation();
        }
        finally
        {
            _isCloseRequestPending = false;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _discVisibilityGeneration++;
        _discVisibilityState = DiscVisibilityState.Hidden;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        _musicBrainzService.Dispose();
        _lyricsService.Dispose();
        Mouse.OverrideCursor = null;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSaving)
        {
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSaving)
        {
            Close();
        }
    }

    private void MetadataBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        UpdatePreviewText();
        UpdateDirtyState();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LyricsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
        {
            UpdateDirtyState();
        }
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => character is < '0' or > '9');
    }

    private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox
            || !e.DataObject.GetDataPresent(DataFormats.UnicodeText)
            || e.DataObject.GetData(DataFormats.UnicodeText) is not string pastedText
            || pastedText.Any(character => character is < '0' or > '9'))
        {
            e.CancelCommand();
            return;
        }

        var proposedLength = textBox.Text.Length - textBox.SelectionLength + pastedText.Length;
        if (proposedLength > textBox.MaxLength)
        {
            e.CancelCommand();
        }
    }

    private async void JewelPreview_UploadCoverRequested(object? sender, EventArgs e)
    {
        await UploadArtworkAsync(isDiscArtwork: false);
    }

    private async void CoverUploadButton_Click(object sender, RoutedEventArgs e)
    {
        await UploadArtworkAsync(isDiscArtwork: false);
    }

    private async void DiscArtworkPreview_UploadArtworkRequested(object? sender, EventArgs e)
    {
        await UploadArtworkAsync(isDiscArtwork: true);
    }

    private void DiscUploadTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled
            || e.LeftButton != MouseButtonState.Pressed
            || !Mouse.Capture(DiscUploadTile, CaptureMode.Element))
        {
            return;
        }

        _isDiscUploadTilePointerDown = true;
        e.Handled = true;
    }

    private async void DiscUploadTile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || !_isDiscUploadTilePointerDown)
        {
            return;
        }

        _isDiscUploadTilePointerDown = false;
        var shouldUpload = DiscUploadTile.IsMouseOver;
        if (DiscUploadTile.IsMouseCaptured)
        {
            Mouse.Capture(null);
        }

        e.Handled = true;
        if (shouldUpload)
        {
            await UploadArtworkAsync(isDiscArtwork: true);
        }
    }

    private void DiscUploadTile_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isDiscUploadTilePointerDown = false;
    }

    private void ArtworkButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu.Items.Count: > 0 } element
            || element.ContextMenu.Items[0] is not MenuItem deleteItem)
        {
            return;
        }

        deleteItem.IsEnabled = string.Equals(element.Tag as string, "Disc", StringComparison.Ordinal)
            ? _draft.DiscArtwork is not null
            : _draft.CoverArtwork is not null;
    }

    private async void DeleteCoverArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _draft.CoverArtwork is null)
        {
            return;
        }

        _draft.CoverArtwork = null;
        _coverArtworkHash = null;
        UpdateDirtyState();
        await RefreshArtworkUiAsync();
    }

    private async void DeleteDiscArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _draft.DiscArtwork is null)
        {
            return;
        }

        _draft.DiscArtwork = null;
        _discArtworkHash = null;
        UpdateDirtyState();
        await RefreshArtworkUiAsync();
    }

    private async Task UploadArtworkAsync(bool isDiscArtwork)
    {
        if (_isBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = isDiscArtwork ? "Choose CD artwork" : "Choose cover artwork",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        SetBusy(true);
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var artwork = await _artworkLoader.LoadFileAsync(dialog.FileName, cancellationToken);
            if (isDiscArtwork)
            {
                _draft.DiscArtwork = artwork;
                _discArtworkHash = HashArtwork(artwork);
            }
            else
            {
                _draft.CoverArtwork = artwork;
                _coverArtworkHash = HashArtwork(artwork);
            }

            UpdateDirtyState();
            await RefreshArtworkUiAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AppDialogWindow.ShowWarning(this, "Unsupported image", ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Mouse.OverrideCursor = null;
            EndOperation();
            if (!_isClosed)
            {
                SetBusy(false);
            }
        }
    }

    private async void ReplaceSongButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_hasUnsavedChanges)
        {
            var result = AppDialogWindow.ShowQuestion(
                this,
                "Unsaved changes",
                "Save your CD metadata before replacing the song?");
            if (result == MessageBoxResult.Cancel)
            {
                return;
            }

            if (result == MessageBoxResult.Yes && !await SaveBurnedFileAsync())
            {
                return;
            }
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose an audio file to burn to a CD",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Audio files|*.mp3;*.mp2;*.mp1;*.flac;*.m4a;*.m4b;*.aac;*.ogg;*.oga;*.opus;*.wav;*.aif;*.aiff;*.wma;*.asf;*.ape;*.wv;*.mpc;*.mpp;*.webm;*.dsf;*.aa;*.aax|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        SetBusy(true);
        Mouse.OverrideCursor = Cursors.Wait;
        BurnTrackDraft? replacement = null;
        try
        {
            replacement = await _audioTagService.ReadAsync(dialog.FileName, cancellationToken);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Mouse.OverrideCursor = null;
            EndOperation();
            if (!_isClosed)
            {
                SetBusy(false);
            }
        }

        if (replacement is null || _isClosed)
        {
            return;
        }

        LoadDraftIntoEditor(replacement);
        await RefreshArtworkUiAsync();
        TrackReplaced?.Invoke(this, EventArgs.Empty);
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        SyncDraftFromForm();
        if (string.IsNullOrWhiteSpace(_draft.Title) && string.IsNullOrWhiteSpace(_draft.Isrc))
        {
            AppDialogWindow.ShowWarning(
                this,
                "Title needed",
                "Enter a song title before fetching song data and lyrics.");
            TitleBox.Focus();
            return;
        }

        var lookupTitle = _draft.Title;
        var lookupArtist = _draft.Artist;
        var lookupAlbum = _draft.Album;
        var lyricsProvider = _settingsService.Settings.Behavior.BurnLyricsProvider;

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        SetBusy(true);
        MusicBrainzTrackData? result = null;
        LyricsResult? lyricsResult = null;
        ArtworkAsset? fetchedCover = null;
        ArtworkAsset? fetchedDisc = null;
        Exception? metadataError = null;
        Exception? lyricsError = null;
        var wasCanceled = false;
        var progressWindow = new OperationProgressWindow(
            this,
            "Fetch song data",
            "Fetching song data",
            "Searching for a matching song…",
            isIndeterminate: true,
            _operationCts.Cancel,
            iconPath: "Resources/search.ico",
            progressBrush: Brushes.LightSeaGreen);
        progressWindow.ContentRendered += async (_, _) =>
        {
            Task<LyricsResult>? directLyricsTask = lyricsProvider == BurnLyricsProvider.Lrclib
                ? _lyricsService.GetLyricsAsync(
                    lookupTitle,
                    lookupArtist,
                    lookupAlbum,
                    _draft.Duration,
                    cancellationToken)
                : null;
            try
            {
                result = await _musicBrainzService.FetchTrackDataAsync(
                    _draft.Title,
                    _draft.Artist,
                    _draft.Album,
                    _draft.Isrc,
                    _draft.Duration,
                    cancellationToken);
                if (result?.CoverArtwork is { Length: > 0 } coverBytes)
                {
                    fetchedCover = await TryLoadFetchedArtworkAsync(coverBytes, cancellationToken);
                }

                if (result?.DiscArtwork is { Length: > 0 } discBytes)
                {
                    fetchedDisc = await TryLoadFetchedArtworkAsync(discBytes, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
            }
            catch (Exception ex)
            {
                metadataError = ex;
            }

            try
            {
                if (directLyricsTask is not null)
                {
                    lyricsResult = await directLyricsTask;
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    lyricsResult = await _lyricsService.GetLyricsAsync(
                        PreferFetched(result?.Title, lookupTitle),
                        PreferFetched(result?.Artist, lookupArtist),
                        PreferFetched(result?.Album, lookupAlbum),
                        _draft.Duration,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
            }
            catch (Exception ex)
            {
                lyricsError = ex;
            }
            finally
            {
                progressWindow.CloseOperationWindow();
            }
        };

        try
        {
            progressWindow.ShowDialog();
            metadataError ??= progressWindow.CancellationError;
            if (wasCanceled)
            {
                return;
            }

            var metadataApplied = result is not null;
            if (result is not null)
            {
                ApplyFetchedText(result);
                if (fetchedCover is not null)
                {
                    _draft.CoverArtwork = fetchedCover;
                    _coverArtworkHash = HashArtwork(fetchedCover);
                }

                if (fetchedDisc is not null)
                {
                    _draft.DiscArtwork = fetchedDisc;
                    _discArtworkHash = HashArtwork(fetchedDisc);
                }
            }

            var lyricsApplied = ApplyFetchedLyrics(lyricsResult);
            if (!metadataApplied && !lyricsApplied)
            {
                ShowFetchNotFound(metadataError, lyricsError, lyricsResult);
                return;
            }

            UpdateDirtyState();
            await RefreshArtworkUiAsync();
            var confirmationMessage = (metadataApplied, lyricsApplied) switch
            {
                (true, true) => "Song data was fetched from MusicBrainz and lyrics were fetched from LRCLIB.",
                (true, false) when lyricsResult?.Status == LyricsStatus.Instrumental =>
                    "Song data was fetched from MusicBrainz. LRCLIB marks this track as instrumental.",
                (true, false) => "Song data was fetched from MusicBrainz. LRCLIB did not return lyrics.",
                _ => "Lyrics were fetched from LRCLIB. MusicBrainz did not return a confident metadata match."
            };
            AppDialogWindow.ShowConfirmation(this, "Fetch complete", confirmationMessage);

            if (metadataError is not null)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "MusicBrainz fetch incomplete",
                    metadataError.Message);
            }

            if (lyricsError is not null)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "Could not fetch lyrics",
                    lyricsError.Message);
            }

            // Warn when no cover ended up on the draft, distinguishing a temporary
            // download failure from artwork that genuinely does not exist — mirroring
            // the CD-artwork messaging below.
            if (result is not null && _draft.CoverArtwork is null)
            {
                if (result.CoverOutcome == ArtworkFetchOutcome.Failed)
                {
                    AppDialogWindow.ShowWarning(
                        this,
                        "Cover art not downloaded",
                        "The cover art couldn't be downloaded right now (a temporary Cover Art Archive issue). Try fetching again, or add cover art yourself from the Artwork tab.");
                }
                else
                {
                    AppDialogWindow.ShowWarningWithIcon(
                        this,
                        "Cover art not found",
                        "Cover Art Archive has no cover art for this release. You can add cover art yourself from the Artwork tab.",
                        "Resources/artwork.ico");
                }
            }

            if (result is not null && fetchedDisc is null && _draft.DiscArtwork is null)
            {
                if (result.DiscOutcome == ArtworkFetchOutcome.Failed)
                {
                    AppDialogWindow.ShowWarning(
                        this,
                        "CD artwork not downloaded",
                        "The disc artwork couldn't be downloaded right now (a temporary Cover Art Archive issue). Try fetching again; it is optional.");
                }
                else
                {
                    AppDialogWindow.ShowWarningWithIcon(
                        this,
                        "CD artwork not found",
                        "MusicBrainz has no disc or medium artwork for this release. You can upload CD artwork yourself from the Artwork tab; it is optional.",
                        "Resources/artwork.ico");
                }
            }
        }
        finally
        {
            EndOperation();
            if (!_isClosed)
            {
                SetBusy(false);
            }
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveBurnedFileAsync())
        {
            _isClosingConfirmed = true;
            Close();
        }
    }

    private async Task<bool> SaveBurnedFileAsync(bool showSuccess = true)
    {
        if (_isBusy)
        {
            return false;
        }

        SyncDraftFromForm();
        try
        {
            AudioTagService.ValidateDraft(_draft);
        }
        catch (InvalidDataException ex)
        {
            AppDialogWindow.ShowWarning(this, "Check the metadata", ex.Message);
            return false;
        }

        var sourceExtension = Path.GetExtension(_draft.SourcePath);
        var dialog = new SaveFileDialog
        {
            Title = "Save burned audio file",
            AddExtension = true,
            OverwritePrompt = true,
            DefaultExt = sourceExtension,
            FileName = AudioTagService.CreateSuggestedOutputFileName(_draft),
            Filter = sourceExtension.Length == 0
                ? "All files|*.*"
                : $"Original audio format (*{sourceExtension})|*{sourceExtension}"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(dialog.FileName), sourceExtension, StringComparison.OrdinalIgnoreCase))
        {
            AppDialogWindow.ShowWarning(
                this,
                "Keep the original format",
                sourceExtension.Length == 0
                    ? "Metadata burning does not transcode audio. Keep the original file name format."
                    : $"Metadata burning does not transcode audio. Save the copy as a {sourceExtension} file.");
            return false;
        }

        if (string.Equals(
                Path.GetFullPath(dialog.FileName),
                Path.GetFullPath(_draft.SourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            AppDialogWindow.ShowWarning(
                this,
                "Choose another location",
                "Choose a different folder or file name so the original audio is preserved.");
            return false;
        }

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        _isSaving = true;
        SetBusy(true);
        Exception? operationError = null;
        var wasCanceled = false;
        var progressWindow = new OperationProgressWindow(
            this,
            "Burning CD",
            "Burning your CD",
            "Copying the audio and writing its metadata…",
            isIndeterminate: false,
            _operationCts.Cancel);
        progressWindow.ContentRendered += async (_, _) =>
        {
            var progress = new Progress<double>(value => progressWindow.SetProgress(value));
            try
            {
                await _audioTagService.SaveCopyAsync(
                    _draft,
                    dialog.FileName,
                    progress,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
            }
            catch (Exception ex)
            {
                operationError = ex;
            }
            finally
            {
                progressWindow.CloseOperationWindow();
            }
        };

        try
        {
            progressWindow.ShowDialog();
            operationError ??= progressWindow.CancellationError;
            if (wasCanceled && operationError is null)
            {
                return false;
            }

            if (operationError is not null)
            {
                AppDialogWindow.ShowError(this, "Could not burn the CD", operationError.Message);
                return false;
            }

            _baselineState = CaptureEditorState();
            _hasUnsavedChanges = false;
            _draft.CoverArtworkChanged = false;
            _draft.DiscArtworkChanged = false;
            _draft.LyricsChanged = false;
            UpdateDirtyStatus();
            var shareRequest = GlobeBurnShareRequest.FromDraft(_draft);
            await HandleBurnCompletionAsync(shareRequest, showSuccess);
            BurnCompleted?.Invoke(this, EventArgs.Empty);

            return true;
        }
        finally
        {
            _isSaving = false;
            EndOperation();
            if (!_isClosed)
            {
                SetBusy(false);
            }
        }
    }

    private async Task HandleBurnCompletionAsync(
        GlobeBurnShareRequest shareRequest,
        bool showSuccess)
    {
        var globeState = _globeConnectionService.State;
        var isLinked = globeState.IsLinked;
        var canShare = globeState.CanShare;
        var automaticallyShare = canShare
            && _settingsService.Settings.Social.AutomaticallyShareBurns;

        if (automaticallyShare)
        {
            try
            {
                await _globeConnectionService.ShareBurnAsync(shareRequest);
                if (showSuccess)
                {
                    AppDialogWindow.ShowConfirmation(
                        this,
                        "Burn complete",
                        "Successfully burned and shared to your globe profile!");
                }
            }
            catch (Exception)
            {
                if (!showSuccess)
                {
                    return;
                }

                var retry = AppDialogWindow.ShowAction(
                    this,
                    "Burn complete — sharing failed",
                    "Your CD was burned, but it couldn't be shared to globe. Check your connection and try again.",
                    "Retry",
                    isWarning: true);
                if (retry)
                {
                    ShowShareStatus(shareRequest);
                }
            }
            return;
        }

        if (!showSuccess)
        {
            return;
        }

        if (isLinked && !canShare)
        {
            AppDialogWindow.ShowWarning(
                this,
                "Burn complete — sharing unavailable",
                "Your CD has been burned! globe is unavailable right now, so sharing is temporarily disabled.");
            return;
        }

        if (isLinked)
        {
            var share = AppDialogWindow.ShowAction(
                this,
                "Burn complete",
                "Your CD has been burned!",
                "Share to globe");
            if (share)
            {
                ShowShareStatus(shareRequest);
            }
            return;
        }

        var link = AppDialogWindow.ShowAction(
            this,
            "Burn complete",
            "Your CD has been burned!",
            "Link your account to share to the internet",
            placeActionOnNewLine: true);
        if (link && Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ActivateFromExternalRequest(openSocialSettings: true);
        }
    }

    private void ShowShareStatus(GlobeBurnShareRequest shareRequest)
    {
        var statusWindow = new ShareStatusWindow(
            this,
            cancellationToken => _globeConnectionService.ShareBurnAsync(
                shareRequest,
                cancellationToken));
        statusWindow.ShowDialog();
        if (statusWindow.WasSuccessful)
        {
            AppDialogWindow.ShowConfirmation(
                this,
                "Shared to globe",
                "Your burned CD was shared to your globe profile.");
        }
    }

    private async Task<ArtworkAsset?> TryLoadFetchedArtworkAsync(byte[] data, CancellationToken cancellationToken)
    {
        try
        {
            return await _artworkLoader.LoadAsync(data, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private void ApplyFetchedText(MusicBrainzTrackData result)
    {
        SetIfPresent(TitleBox, result.Title);
        SetIfPresent(ArtistBox, result.Artist);
        SetIfPresent(AlbumBox, result.Album);
        SetIfPresent(GenreBox, result.Genre);
        SetIfPresent(YearBox, result.Year);
        SetIfPresent(TrackNumberBox, result.TrackNumber);
        SetIfPresent(TrackTotalBox, result.TrackTotal);
        SyncDraftFromForm();
    }

    private bool ApplyFetchedLyrics(LyricsResult? result)
    {
        if (result?.Status is not (LyricsStatus.Synced or LyricsStatus.Plain))
        {
            return false;
        }

        var applied = false;
        var unsynchronized = NormalizeLyricsText(result.PlainText);
        if (unsynchronized.Length == 0 && result.Status == LyricsStatus.Plain)
        {
            unsynchronized = string.Join("\n", result.PlainLines);
        }

        if (!string.IsNullOrWhiteSpace(unsynchronized))
        {
            UnsynchronizedLyricsBox.Text = LimitForTextBox(
                UnsynchronizedLyricsBox,
                unsynchronized);
            applied = true;
        }

        var synchronized = NormalizeLyricsText(result.SyncedText);
        if (synchronized.Length == 0 && result.Status == LyricsStatus.Synced)
        {
            synchronized = FormatLrc(result.SyncedLines);
        }

        if (!string.IsNullOrWhiteSpace(synchronized))
        {
            SynchronizedLyricsBox.Text = LimitForTextBox(
                SynchronizedLyricsBox,
                synchronized);
            applied = true;
        }

        if (applied)
        {
            SyncDraftFromForm();
        }

        return applied;
    }

    private static string FormatLrc(IEnumerable<LyricLine> lines)
    {
        return string.Join("\n", lines.Select(line =>
        {
            var totalMinutes = Math.Max(0, (int)Math.Floor(line.Time.TotalMinutes));
            var seconds = Math.Max(0, line.Time.TotalSeconds - (totalMinutes * 60));
            return $"[{totalMinutes:00}:{seconds.ToString("00.00", CultureInfo.InvariantCulture)}]{line.Text}";
        }));
    }

    private void ShowFetchNotFound(
        Exception? metadataError,
        Exception? lyricsError,
        LyricsResult? lyricsResult)
    {
        var messages = new List<string>
        {
            metadataError is null
                ? "MusicBrainz could not find a confident metadata match."
                : $"MusicBrainz: {metadataError.Message}"
        };

        if (lyricsError is not null)
        {
            messages.Add($"LRCLIB: {lyricsError.Message}");
        }
        else if (lyricsResult?.Status == LyricsStatus.Instrumental)
        {
            messages.Add("LRCLIB marks this track as instrumental.");
        }
        else
        {
            messages.Add("LRCLIB did not find lyrics for this track.");
        }

        AppDialogWindow.ShowWarning(
            this,
            "Song data and lyrics not found",
            string.Join(Environment.NewLine, messages));
    }

    private static string PreferFetched(string? fetched, string fallback)
    {
        return string.IsNullOrWhiteSpace(fetched) ? fallback : fetched;
    }

    private static void SetIfPresent(TextBox textBox, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            textBox.Text = LimitForTextBox(textBox, value);
        }
    }

    private void LoadDraftIntoEditor(BurnTrackDraft draft)
    {
        _draft = draft;
        _isInitialized = false;
        TitleBox.Text = LimitForTextBox(TitleBox, draft.Title);
        ArtistBox.Text = LimitForTextBox(ArtistBox, draft.Artist);
        AlbumBox.Text = LimitForTextBox(AlbumBox, draft.Album);
        GenreBox.Text = LimitForTextBox(GenreBox, draft.Genre);
        YearBox.Text = LimitForTextBox(YearBox, draft.Year);
        TrackNumberBox.Text = LimitForTextBox(TrackNumberBox, draft.TrackNumber);
        TrackTotalBox.Text = LimitForTextBox(TrackTotalBox, draft.TrackTotal);
        UnsynchronizedLyricsBox.Text = LimitForTextBox(
            UnsynchronizedLyricsBox,
            NormalizeLyricsText(draft.UnsynchronizedLyrics));
        SynchronizedLyricsBox.Text = LimitForTextBox(
            SynchronizedLyricsBox,
            NormalizeLyricsText(draft.SynchronizedLyrics));
        _coverArtworkHash = HashArtwork(draft.CoverArtwork);
        _discArtworkHash = HashArtwork(draft.DiscArtwork);
        _isInitialized = true;
        SyncDraftFromForm();
        UpdatePreviewText();
        ApplyArtworkTint(draft.CoverArtwork?.Preview);
        _baselineState = CaptureEditorState();
        UpdateDirtyState();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string LimitForTextBox(TextBox textBox, string value)
    {
        return textBox.MaxLength > 0 && value.Length > textBox.MaxLength
            ? value[..textBox.MaxLength]
            : value;
    }

    private void SyncDraftFromForm()
    {
        _draft.Title = TitleBox.Text.Trim();
        _draft.Artist = ArtistBox.Text.Trim();
        _draft.Album = AlbumBox.Text.Trim();
        _draft.Genre = GenreBox.Text.Trim();
        _draft.Year = YearBox.Text.Trim();
        _draft.TrackNumber = TrackNumberBox.Text.Trim();
        _draft.TrackTotal = TrackTotalBox.Text.Trim();
        _draft.UnsynchronizedLyrics = NormalizeLyricsText(UnsynchronizedLyricsBox.Text);
        _draft.SynchronizedLyrics = NormalizeLyricsText(SynchronizedLyricsBox.Text);
    }

    private void UpdatePreviewText()
    {
        SetPreviewField(PreviewTitleText, TitleBox.Text);
        SetPreviewField(PreviewArtistText, ArtistBox.Text);
        SetPreviewField(PreviewAlbumText, AlbumBox.Text);
    }

    private static void SetPreviewField(TextBlock textBlock, string value)
    {
        var normalized = value.Trim();
        textBlock.Text = normalized;
        textBlock.Visibility = normalized.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshArtworkUiAsync()
    {
        var cover = _draft.CoverArtwork?.Preview;
        var coverPreview = cover ?? _placeholderArtwork;
        CoverUploadImage.Source = coverPreview;
        BlurredCoverImage.Source = cover;
        ApplyArtworkTint(cover);
        await DiscArtworkPreview.SetArtworkAsync(_draft.DiscArtwork?.Preview);
        await JewelPreview.SetArtworkAsync(
            coverPreview,
            _draft.DiscArtwork?.Preview ?? _defaultDiscArtwork);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirtyState()
    {
        if (!_isInitialized)
        {
            return;
        }

        var current = CaptureEditorState();
        _hasUnsavedChanges = current != _baselineState;
        _draft.CoverArtworkChanged = !string.Equals(
            current.CoverArtworkHash,
            _baselineState.CoverArtworkHash,
            StringComparison.Ordinal);
        _draft.DiscArtworkChanged = !string.Equals(
            current.DiscArtworkHash,
            _baselineState.DiscArtworkHash,
            StringComparison.Ordinal);
        _draft.LyricsChanged = !string.Equals(
                current.UnsynchronizedLyrics,
                _baselineState.UnsynchronizedLyrics,
                StringComparison.Ordinal)
            || !string.Equals(
                current.SynchronizedLyrics,
                _baselineState.SynchronizedLyrics,
                StringComparison.Ordinal);
        UpdateDirtyStatus();
    }

    private BurnEditorState CaptureEditorState()
    {
        return new BurnEditorState(
            TitleBox.Text.Trim(),
            ArtistBox.Text.Trim(),
            AlbumBox.Text.Trim(),
            GenreBox.Text.Trim(),
            YearBox.Text.Trim(),
            TrackNumberBox.Text.Trim(),
            TrackTotalBox.Text.Trim(),
            NormalizeLyricsText(UnsynchronizedLyricsBox.Text),
            NormalizeLyricsText(SynchronizedLyricsBox.Text),
            _coverArtworkHash,
            _discArtworkHash);
    }

    private static string NormalizeLyricsText(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
    }

    private void UpdateDirtyStatus()
    {
        DirtyStatusText.Text = _hasUnsavedChanges ? "Unsaved changes" : string.Empty;
        SaveButton.IsEnabled = _hasUnsavedChanges && !_isBusy;
    }

    private static string? HashArtwork(ArtworkAsset? artwork)
    {
        return artwork is null
            ? null
            : Convert.ToHexString(SHA256.HashData(artwork.Data));
    }

    private void PlayOpenAnimation()
    {
        var generation = ++_discVisibilityGeneration;
        _discVisibilityState = DiscVisibilityState.Showing;
        StopPresentationAnimations(0, 0.96, 0.96);
        StartDiscShowAnimation(generation, 0, 0.96, 0.96);
    }

    private void StartDiscShowAnimation(
        int generation,
        double fromOpacity,
        double fromScaleX,
        double fromScaleY)
    {
        var duration = TimeSpan.FromMilliseconds(190);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        WindowScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromScaleX, 1, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        WindowScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScaleY, 1, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);

        var fade = new DoubleAnimation(fromOpacity, 1, duration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (generation != _discVisibilityGeneration
                || _discVisibilityState != DiscVisibilityState.Showing
                || _isClosed
                || _isCloseAnimationRunning)
            {
                return;
            }

            StopPresentationAnimations(1, 1, 1);
            _discVisibilityState = DiscVisibilityState.Visible;
        };
        RootCard.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopPresentationAnimations(double opacity, double scaleX, double scaleY)
    {
        RootCard.BeginAnimation(OpacityProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootCard.Opacity = opacity;
        WindowScale.ScaleX = scaleX;
        WindowScale.ScaleY = scaleY;
    }

    private void PlayCloseAnimation()
    {
        if (_isCloseAnimationRunning || _isClosed)
        {
            return;
        }

        _discVisibilityGeneration++;
        _discVisibilityState = DiscVisibilityState.Hiding;
        _isCloseAnimationRunning = true;
        var duration = TimeSpan.FromMilliseconds(155);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        WindowScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        WindowScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);

        var fade = new DoubleAnimation(0, duration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (_isClosed)
            {
                return;
            }

            _isCloseCommitted = true;
            Close();
        };
        RootCard.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyArtworkTint(BitmapSource? cover)
    {
        var tint = ExtractDominantTint(cover) ?? DefaultTint;
        AnimateColor(CardTopStop, WithAlpha(Blend(tint, Colors.White, 0.20), 0x90));
        AnimateColor(CardUpperStop, WithAlpha(Blend(tint, Colors.White, 0.04), 0x82));
        AnimateColor(CardLowerStop, WithAlpha(Blend(tint, Colors.Black, 0.35), 0x76));
        AnimateColor(CardBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.22), 0x84));
        AnimateColor(GlowPrimaryStop, WithAlpha(Blend(tint, Colors.White, 0.56), 0x76));
        AnimateColor(GlowSecondaryStop, WithAlpha(Blend(tint, Colors.White, 0.10), 0x1C));
        AnimateBrushColor(RootCard.BorderBrush, WithAlpha(Blend(tint, Colors.White, 0.62), 0xA5));
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

        if (!IsLoaded)
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

    private void RoundedSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        var geometry = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 9, 9);
        geometry.Freeze();
        element.Clip = geometry;
    }

    private void BeginOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        FetchButton.IsEnabled = !isBusy;
        ReplaceSongButton.IsEnabled = !isBusy;
        EditorTabs.IsEnabled = !isBusy;
        JewelPreview.IsEnabled = !isBusy;
        CoverUploadButton.IsEnabled = !isBusy;
        DiscUploadTile.IsEnabled = !isBusy;
        CloseButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !isBusy;
        UpdateDirtyStatus();
    }

    private readonly record struct BurnEditorState(
        string Title,
        string Artist,
        string Album,
        string Genre,
        string Year,
        string TrackNumber,
        string TrackTotal,
        string UnsynchronizedLyrics,
        string SynchronizedLyrics,
        string? CoverArtworkHash,
        string? DiscArtworkHash);
}
