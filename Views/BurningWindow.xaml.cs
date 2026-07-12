using System.ComponentModel;
using System.IO;
using System.Net.Http;
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
    private bool _isInitialized;
    private bool _isBusy;
    private bool _isSaving;
    private bool _allowClose;
    private bool _isClosing;

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
        TitleBox.Text = draft.Title;
        ArtistBox.Text = draft.Artist;
        GenreBox.Text = draft.Genre;
        DateBox.Text = draft.Date;
        AlbumBox.Text = draft.Album;
        TrackNumberBox.Text = draft.TrackNumber;
        SourceFileText.Text = BuildSourceDescription(draft);
        _isInitialized = true;
        UpdatePreviewText();
        ApplyArtworkTint(draft.CoverArtwork?.Preview);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshArtworkUiAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isSaving && !_allowClose)
        {
            e.Cancel = true;
            return;
        }

        _isClosing = true;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        _musicBrainzService.Dispose();
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
        if (_isSaving)
        {
            return;
        }

        _operationCts?.Cancel();
        Close();
    }

    private void MetadataBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
        {
            UpdatePreviewText();
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

    private async void DiscUploadButton_Click(object sender, RoutedEventArgs e)
    {
        await UploadArtworkAsync(isDiscArtwork: true);
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
        SetBusy(true, isDiscArtwork ? "Loading CD artwork…" : "Loading cover artwork…");
        try
        {
            var artwork = await _artworkLoader.LoadFileAsync(dialog.FileName, cancellationToken);
            if (isDiscArtwork)
            {
                _draft.DiscArtwork = artwork;
                _draft.DiscArtworkChanged = true;
            }
            else
            {
                _draft.CoverArtwork = artwork;
                _draft.CoverArtworkChanged = true;
            }

            await RefreshArtworkUiAsync();
            StatusText.Text = isDiscArtwork ? "CD artwork updated." : "Cover artwork updated.";
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
            EndOperation();
            if (!_isClosing)
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
        SetBusy(true, "Searching MusicBrainz…", indeterminateProgress: true);
        try
        {
            var result = await _musicBrainzService.FetchTrackDataAsync(
                _draft.Title,
                _draft.Artist,
                _draft.Album,
                _draft.Isrc,
                _draft.Duration,
                cancellationToken);
            if (result is null)
            {
                StatusText.Text = "No MusicBrainz match found.";
                AppDialogWindow.ShowWarning(
                    this,
                    "Song not found",
                    "MusicBrainz could not find a confident match. Try adding or correcting the title, artist, or album.");
                return;
            }

            ApplyFetchedText(result);
            if (result.CoverArtwork is { Length: > 0 })
            {
                _draft.CoverArtwork = await _artworkLoader.LoadAsync(result.CoverArtwork, cancellationToken);
                _draft.CoverArtworkChanged = true;
            }

            if (result.DiscArtwork is { Length: > 0 })
            {
                _draft.DiscArtwork = await _artworkLoader.LoadAsync(result.DiscArtwork, cancellationToken);
                _draft.DiscArtworkChanged = true;
            }

            await RefreshArtworkUiAsync();
            StatusText.Text = "Song data fetched from MusicBrainz.";
            if (result.DiscArtwork is not { Length: > 0 } && _draft.DiscArtwork is null)
            {
                AppDialogWindow.ShowWarning(
                    this,
                    "CD artwork not found",
                    "MusicBrainz has no disc or medium artwork for this release. You can upload CD artwork yourself from the Artwork tab; it is optional.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_isClosing)
            {
                StatusText.Text = "MusicBrainz lookup canceled.";
            }
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or InvalidDataException
                                   or NotSupportedException
                                   or IOException)
        {
            StatusText.Text = "Could not fetch song data.";
            AppDialogWindow.ShowWarning(this, "Could not fetch song data", ex.Message);
        }
        finally
        {
            EndOperation();
            if (!_isClosing)
            {
                SetBusy(false);
            }
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        SyncDraftFromForm();
        var sourceExtension = Path.GetExtension(_draft.SourcePath);
        var baseName = Path.GetFileNameWithoutExtension(_draft.SourcePath);
        var dialog = new SaveFileDialog
        {
            Title = "Save burned audio file",
            AddExtension = true,
            OverwritePrompt = true,
            DefaultExt = sourceExtension,
            FileName = $"{baseName} (burned){sourceExtension}",
            Filter = sourceExtension.Length == 0
                ? "All files|*.*"
                : $"Original audio format (*{sourceExtension})|*{sourceExtension}"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!string.Equals(Path.GetExtension(dialog.FileName), sourceExtension, StringComparison.OrdinalIgnoreCase))
        {
            AppDialogWindow.ShowWarning(
                this,
                "Keep the original format",
                $"Metadata burning does not transcode audio. Save the copy as a {sourceExtension} file.");
            return;
        }

        BeginOperation();
        var cancellationToken = _operationCts!.Token;
        _isSaving = true;
        SetBusy(true, "Burning metadata into a new audio file…");
        OperationProgress.IsIndeterminate = false;
        OperationProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(value => OperationProgress.Value = Math.Clamp(value, 0, 1));
        try
        {
            await _audioTagService.SaveCopyAsync(
                _draft,
                dialog.FileName,
                progress,
                cancellationToken);
            AppDialogWindow.ShowConfirmation(this, "Burn complete", "Your CD has been burned!");
            _allowClose = true;
            Close();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText.Text = "Burn canceled.";
        }
        catch (Exception ex) when (ex is InvalidDataException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            AppDialogWindow.ShowError(this, "Could not burn the CD", ex.Message);
        }
        finally
        {
            _isSaving = false;
            EndOperation();
            if (!_allowClose)
            {
                SetBusy(false);
            }
        }
    }

    private void ApplyFetchedText(MusicBrainzTrackData result)
    {
        SetIfPresent(TitleBox, result.Title);
        SetIfPresent(ArtistBox, result.Artist);
        SetIfPresent(GenreBox, result.Genre);
        SetIfPresent(DateBox, result.Date);
        SetIfPresent(AlbumBox, result.Album);
        SetIfPresent(TrackNumberBox, result.TrackNumber);
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
        _draft.Genre = GenreBox.Text.Trim();
        _draft.Date = DateBox.Text.Trim();
        _draft.Album = AlbumBox.Text.Trim();
        _draft.TrackNumber = TrackNumberBox.Text.Trim();
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

    private void SetBusy(bool isBusy, string? status = null, bool indeterminateProgress = false)
    {
        _isBusy = isBusy;
        FetchButton.IsEnabled = !isBusy;
        SaveButton.IsEnabled = !isBusy;
        EditorTabs.IsEnabled = !isBusy;
        JewelPreview.IsEnabled = !isBusy;
        CoverUploadButton.IsEnabled = !isBusy;
        DiscUploadButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !_isSaving;
        if (status is not null)
        {
            StatusText.Text = status;
        }
        OperationProgress.IsIndeterminate = indeterminateProgress;
        OperationProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (!isBusy)
        {
            OperationProgress.Value = 0;
        }
    }

    private static string BuildSourceDescription(BurnTrackDraft draft)
    {
        var duration = draft.Duration.TotalHours >= 1
            ? draft.Duration.ToString(@"h\:mm\:ss")
            : draft.Duration.ToString(@"m\:ss");
        return $"{Path.GetFileName(draft.SourcePath)}  •  {duration}";
    }
}
