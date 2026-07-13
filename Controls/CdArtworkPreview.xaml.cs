using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mystral.Services;

namespace Mystral.Controls;

/// <summary>
/// Displays the application's composed CD and preserves its rotation between
/// hover sessions. A null artwork value uses cd_default_cover.png.
/// </summary>
public partial class CdArtworkPreview : UserControl, INotifyPropertyChanged
{
    private const double DegreesPerSecond = 12;
    private CancellationTokenSource? _compositionCts;
    private BitmapSource? _composedArtwork;
    private BitmapSource? _requestedArtwork;
    private bool _isHovered;
    private long _lastAnimationTick;
    private double _discAngle;
    private int _artworkGeneration;
    private int _completedArtworkGeneration;
    private bool _isPointerDown;

    public CdArtworkPreview()
    {
        InitializeComponent();
        Loaded += CdArtworkPreview_Loaded;
        Unloaded += CdArtworkPreview_Unloaded;
    }

    public event EventHandler? UploadArtworkRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BitmapSource? ComposedArtwork
    {
        get => _composedArtwork;
        private set
        {
            if (ReferenceEquals(_composedArtwork, value))
            {
                return;
            }

            _composedArtwork = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComposedArtwork)));
        }
    }

    public bool HasCustomArtwork { get; private set; }

    /// <summary>
    /// Composes the supplied image into the CD layer stack. Passing null shows
    /// the built-in default CD artwork.
    /// </summary>
    public async Task SetArtworkAsync(
        BitmapSource? artwork,
        CancellationToken cancellationToken = default)
    {
        _requestedArtwork = artwork;
        var generation = ++_artworkGeneration;
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCts = Interlocked.Exchange(
            ref _compositionCts,
            requestCts);
        previousCts?.Cancel();
        previousCts?.Dispose();

        try
        {
            var composed = artwork is null
                ? await CdArtworkComposer.ComposeDefaultAsync(requestCts.Token)
                : await CdArtworkComposer.ComposeAsync(artwork, requestCts.Token);

            if (generation != _artworkGeneration || requestCts.IsCancellationRequested)
            {
                return;
            }

            HasCustomArtwork = artwork is not null;
            ComposedArtwork = composed;
            _completedArtworkGeneration = generation;
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to compose the CD artwork preview: {ex}");
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _compositionCts, null, requestCts), requestCts))
            {
                requestCts.Dispose();
            }
        }
    }

    private async void CdArtworkPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (ComposedArtwork is not null && _completedArtworkGeneration == _artworkGeneration)
        {
            return;
        }

        await SetArtworkAsync(_requestedArtwork);
    }

    private void CdArtworkPreview_Unloaded(object sender, RoutedEventArgs e)
    {
        StopSpinning();
        _isPointerDown = false;
        if (IsMouseCaptured)
        {
            Mouse.Capture(null);
        }
        var compositionCts = Interlocked.Exchange(ref _compositionCts, null);
        compositionCts?.Cancel();
        compositionCts?.Dispose();
    }

    private void Preview_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !Mouse.Capture(this, CaptureMode.Element))
        {
            return;
        }

        _isPointerDown = true;
        e.Handled = true;
    }

    private void Preview_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        _isPointerDown = false;
        var shouldUpload = IsMouseOver;
        if (IsMouseCaptured)
        {
            Mouse.Capture(null);
        }

        e.Handled = true;
        if (shouldUpload)
        {
            UploadArtworkRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Preview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isPointerDown = false;
    }

    private void Preview_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovered = true;
        _lastAnimationTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private void Preview_MouseLeave(object sender, MouseEventArgs e)
    {
        StopSpinning();
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (!_isHovered)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Min(
            0.12,
            (now - _lastAnimationTick) / (double)Stopwatch.Frequency);
        _lastAnimationTick = now;
        _discAngle = (_discAngle + (elapsed * DegreesPerSecond)) % 360;

        DiscRotation.Angle = _discAngle;
    }

    private void StopSpinning()
    {
        _isHovered = false;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _lastAnimationTick = 0;
    }
}
