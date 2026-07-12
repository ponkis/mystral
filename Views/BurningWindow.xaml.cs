using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Mystral.Models;
using Mystral.Services;
using static Mystral.Services.ArtworkTint;

namespace Mystral.Views;

public partial class BurningWindow : Window
{
    private static readonly Color DefaultTint = Color.FromRgb(74, 82, 88);
    private readonly BurnTrackDraft _draft;
    private readonly AudioTagService _audioTagService;
    private readonly ImageArtworkLoader _artworkLoader;
    private readonly MusicBrainzService _musicBrainzService = new();
    private readonly BitmapSource _placeholderArtwork;
    private CancellationTokenSource? _operationCts;
    private BurnEditorState _baselineState;
    private string? _coverArtworkHash;
    private string? _discArtworkHash;
    private bool _isInitialized;
    private bool _hasUnsavedChanges;
    private bool _isBusy;
    private bool _isSaving;
    private bool _isClosingConfirmed;
    private bool _isClosed;

    public BurningWindow(
        BurnTrackDraft draft,
        AudioTagService audioTagService,
        ImageArtworkLoader artworkLoader)
    {
        _draft = draft;
        _audioTagService = audioTagService;
        _artworkLoader = artworkLoader;
        _placeholderArtwork = artworkLoader.LoadApplicationImage("unknown.png");

        InitializeComponent();
        Closed += Window_Closed;
        TitleBox.Text = draft.Title;
        ArtistBox.Text = draft.Artist;
        AlbumBox.Text = draft.Album;
        GenreBox.Text = draft.Genre;
        YearBox.Text = draft.Year;
        TrackNumberBox.Text = draft.TrackNumber;
        TrackTotalBox.Text = draft.TrackTotal;
        _coverArtworkHash = HashArtwork(draft.CoverArtwork);
        _discArtworkHash = HashArtwork(draft.DiscArtwork);
        _isInitialized = true;
        UpdatePreviewText();
        ApplyArtworkTint(draft.CoverArtwork?.Preview);
        _baselineState = CaptureEditorState();
        UpdateDirtyState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshArtworkUiAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isBusy)
        {
            e.Cancel = true;
            return;
        }

        if (_isClosingConfirmed || !_hasUnsavedChanges)
        {
            return;
        }

        var result = AppDialogWindow.ShowQuestion(
            this,
            "Unsaved changes",
            "Save your CD metadata before closing?");
        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.No)
        {
            _isClosingConfirmed = true;
            return;
        }

        e.Cancel = true;
        if (await SaveBurnedFileAsync(showSuccess: false))
        {
            _isClosingConfirmed = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        _musicBrainzService.Dispose();
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
    }

    private async void JewelPreview_UploadCoverRequested(object? sender, EventArgs e)
    {
        await UploadArtworkAsync(isDiscArtwork: false);
    }

    private async void CoverUploadButton_Click(object sender, RoutedEventArgs e)
    {
        await UploadArtworkAsync(isDiscArtwork: false);
    }

    private async void DiscUploadButton_Click(object sender, RoutedEventArgs e)
    {
        await UploadArtworkAsync(isDiscArtwork: true);
    }

    private void ArtworkButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { ContextMenu.Items.Count: > 0 } button
            || button.ContextMenu.Items[0] is not MenuItem deleteItem)
        {
            return;
        }

        deleteItem.IsEnabled = string.Equals(button.Tag as string, "Disc", StringComparison.Ordinal)
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
                "Enter a song title before fetching data from MusicBrainz.");
            TitleBox.Focus();
            return;
        }

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        SetBusy(true);
        MusicBrainzTrackData? result = null;
        ArtworkAsset? fetchedCover = null;
        ArtworkAsset? fetchedDisc = null;
        Exception? operationError = null;
        var wasCanceled = false;
        var progressWindow = new OperationProgressWindow(
            this,
            "Fetching song data",
            "Fetching song data",
            "Searching MusicBrainz and Cover Art Archive…",
            isIndeterminate: true,
            _operationCts.Cancel);
        progressWindow.ContentRendered += async (_, _) =>
        {
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
                return;
            }

            if (operationError is not null)
            {
                AppDialogWindow.ShowWarning(this, "Could not fetch song data", operationError.Message);
                return;
            }

            if (result is null)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "Song not found",
                    "MusicBrainz could not find a confident match. Try adding or correcting the title, artist, or album.");
                return;
            }

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

            UpdateDirtyState();
            await RefreshArtworkUiAsync();
            AppDialogWindow.ShowConfirmation(
                this,
                "Song data fetched",
                "Song data was fetched from MusicBrainz.");
            if (fetchedDisc is null && _draft.DiscArtwork is null)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "CD artwork not found",
                    "MusicBrainz has no disc or medium artwork for this release. You can upload CD artwork yourself from the Artwork tab; it is optional.");
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
        var baseName = Path.GetFileNameWithoutExtension(_draft.SourcePath);
        var dialog = new SaveFileDialog
        {
            Title = "Save burned audio file",
            AddExtension = true,
            OverwritePrompt = true,
            DefaultExt = sourceExtension,
            FileName = $"{baseName}{sourceExtension}",
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
            UpdateDirtyStatus();
            if (showSuccess)
            {
                AppDialogWindow.ShowConfirmation(this, "Burn complete", "Your CD has been burned!");
            }

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

    private static void SetIfPresent(TextBox textBox, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            textBox.Text = value;
        }
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
        DiscUploadImage.Source = _draft.DiscArtwork?.Preview;
        BlurredCoverImage.Source = cover;
        ApplyArtworkTint(cover);
        await JewelPreview.SetArtworkAsync(coverPreview, _draft.DiscArtwork?.Preview);
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
            _coverArtworkHash,
            _discArtworkHash);
    }

    private void UpdateDirtyStatus()
    {
        DirtyStatusText.Text = _hasUnsavedChanges ? "Unsaved changes" : string.Empty;
    }

    private static string? HashArtwork(ArtworkAsset? artwork)
    {
        return artwork is null
            ? null
            : Convert.ToHexString(SHA256.HashData(artwork.Data));
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
        SaveButton.IsEnabled = !isBusy;
        EditorTabs.IsEnabled = !isBusy;
        JewelPreview.IsEnabled = !isBusy;
        CoverUploadButton.IsEnabled = !isBusy;
        DiscUploadButton.IsEnabled = !isBusy;
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
        string? CoverArtworkHash,
        string? DiscArtworkHash);
}
