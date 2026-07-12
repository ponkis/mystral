using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mystral.Services;

namespace Mystral.Controls;

public partial class JewelCasePreview : UserControl
{
    private readonly Task<JewelCaseRenderer> _rendererTask;
    private readonly DispatcherTimer _animationTimer;
    private readonly SemaphoreSlim _artworkUpdateGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isHovered;
    private bool _hasDiscArtwork;
    private bool _isRendering;
    private bool _renderRequested;
    private double _coverOpacity = 1;
    private double _targetCoverOpacity = 1;
    private double _discAngle;
    private long _lastAnimationTick;
    private int _artworkGeneration;
    private bool _isUploadPointerDown;

    public JewelCasePreview()
    {
        InitializeComponent();
        _rendererTask = JewelCaseRenderer.CreateAsync(_lifetimeCts.Token);
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += AnimationTimer_Tick;
        Loaded += JewelCasePreview_Loaded;
        Unloaded += JewelCasePreview_Unloaded;
    }

    public event EventHandler? UploadCoverRequested;

    public async Task SetArtworkAsync(BitmapSource? coverArtwork, BitmapSource? discArtwork)
    {
        var generation = ++_artworkGeneration;
        try
        {
            await _artworkUpdateGate.WaitAsync(_lifetimeCts.Token);
            try
            {
                if (generation != _artworkGeneration)
                {
                    return;
                }

                var renderer = await _rendererTask;
                await renderer.SetArtworkAsync(coverArtwork, discArtwork, _lifetimeCts.Token);
                if (generation != _artworkGeneration)
                {
                    return;
                }

                _hasDiscArtwork = renderer.HasDiscArtwork;
                _targetCoverOpacity = _isHovered && _hasDiscArtwork ? 0 : 1;
                await RenderFrameAsync();
                UpdateAnimationTimer();
            }
            finally
            {
                _artworkUpdateGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to render jewel case artwork: {ex}");
        }
    }

    private async void JewelCasePreview_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RenderFrameAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
    }

    private void JewelCasePreview_Unloaded(object sender, RoutedEventArgs e)
    {
        _animationTimer.Stop();
        _lifetimeCts.Cancel();
    }

    private void Preview_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovered = true;
        _targetCoverOpacity = _hasDiscArtwork ? 0 : 1;
        AnimateHint(1);
        UpdateAnimationTimer();
    }

    private void Preview_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovered = false;
        _targetCoverOpacity = 1;
        AnimateHint(0);
        UpdateAnimationTimer();
    }

    private void Preview_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _isUploadPointerDown = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void Preview_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var shouldUpload = _isUploadPointerDown && IsMouseOver;
        _isUploadPointerDown = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        e.Handled = true;
        if (shouldUpload)
        {
            UploadCoverRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Preview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isUploadPointerDown = false;
    }

    private async void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_isRendering)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastAnimationTick == 0
            ? 0.05
            : Math.Min(0.12, (now - _lastAnimationTick) / (double)Stopwatch.Frequency);
        _lastAnimationTick = now;

        var opacityStep = elapsed / 0.22;
        if (_coverOpacity < _targetCoverOpacity)
        {
            _coverOpacity = Math.Min(_targetCoverOpacity, _coverOpacity + opacityStep);
        }
        else if (_coverOpacity > _targetCoverOpacity)
        {
            _coverOpacity = Math.Max(_targetCoverOpacity, _coverOpacity - opacityStep);
        }

        if (_isHovered && _hasDiscArtwork)
        {
            _discAngle = (_discAngle + (elapsed * 12)) % 360;
        }

        await RenderFrameAsync();
        UpdateAnimationTimer();
    }

    private async Task RenderFrameAsync()
    {
        if (_isRendering || _lifetimeCts.IsCancellationRequested)
        {
            _renderRequested = !_lifetimeCts.IsCancellationRequested;
            return;
        }

        _isRendering = true;
        try
        {
            var renderer = await _rendererTask;
            var frame = await renderer.RenderAsync(_coverOpacity, _discAngle, _lifetimeCts.Token);
            if (!_lifetimeCts.IsCancellationRequested)
            {
                CaseImage.Source = frame;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to update jewel case preview: {ex}");
        }
        finally
        {
            _isRendering = false;
            if (_renderRequested && !_lifetimeCts.IsCancellationRequested)
            {
                _renderRequested = false;
                _ = RenderFrameAsync();
            }
        }
    }

    private void UpdateAnimationTimer()
    {
        var isTransitioning = Math.Abs(_coverOpacity - _targetCoverOpacity) > 0.002;
        var shouldRun = isTransitioning || (_isHovered && _hasDiscArtwork);
        if (shouldRun && !_animationTimer.IsEnabled)
        {
            _lastAnimationTick = Stopwatch.GetTimestamp();
            _animationTimer.Start();
        }
        else if (!shouldRun && _animationTimer.IsEnabled)
        {
            _animationTimer.Stop();
            _lastAnimationTick = 0;
        }
    }

    private void AnimateHint(double opacity)
    {
        UploadHint.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
