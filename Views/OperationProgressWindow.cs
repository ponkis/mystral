using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mystral.Services;

namespace Mystral.Views;

internal sealed class OperationProgressWindow : Window
{
    private readonly Action _cancelOperation;
    private readonly ProgressBar _progressBar = new()
    {
        Height = 22,
        Minimum = 0,
        Maximum = 100,
        Foreground = Brushes.DodgerBlue
    };
    private readonly TextBlock _progressText = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brushes.Black,
        FontSize = 11,
        IsHitTestVisible = false,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Button _cancelButton = new()
    {
        Content = "Cancel",
        Height = 23,
        MinWidth = 72
    };
    private bool _canClose;
    private bool _isClosed;
    private bool _isCanceling;

    public Exception? CancellationError { get; private set; }

    public OperationProgressWindow(
        Window owner,
        string title,
        string heading,
        string detail,
        bool isIndeterminate,
        Action cancelOperation,
        string iconPath = "Resources/burn.ico")
    {
        _cancelOperation = cancelOperation;
        Title = title;
        Icon = IconImageSource.LoadBestFitFrame(iconPath, 16);
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Owner = owner;
        WindowStartupLocation = owner.IsVisible
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PresentationFramework.Aero;component/themes/Aero.NormalColor.xaml", UriKind.Relative)
        });

        _progressBar.IsIndeterminate = isIndeterminate;
        _progressText.Text = isIndeterminate ? detail : "0%";
        _cancelButton.Click += (_, _) => RequestCancel();
        Closing += Window_Closing;
        Closed += (_, _) => _isClosed = true;

        var progressGrid = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                _progressBar,
                _progressText
            }
        };
        var body = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Margin = new Thickness(0, 0, 0, 2),
                    FontSize = 19,
                    Foreground = new SolidColorBrush(Color.FromRgb(53, 90, 136)),
                    Text = heading,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Foreground = Brushes.DimGray,
                    Text = detail,
                    TextWrapping = TextWrapping.Wrap
                },
                progressGrid
            }
        };

        var root = new Grid
        {
            Background = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri("pack://siteoforigin:,,,/Resources/Images/dialog_background.png")),
                Stretch = Stretch.Fill
            }
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var contentGrid = new Grid { Margin = new Thickness(16) };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconImage = new Image
        {
            Source = IconImageSource.LoadBestFitFrame(iconPath, 32),
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
        contentGrid.Children.Add(iconImage);
        Grid.SetColumn(body, 1);
        body.Margin = new Thickness(8, 0, 0, 0);
        contentGrid.Children.Add(body);

        var buttonPanel = new StackPanel
        {
            Margin = new Thickness(15, 11, 15, 11),
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Children = { _cancelButton }
        };
        root.Children.Add(contentGrid);
        Grid.SetRow(buttonPanel, 1);
        root.Children.Add(buttonPanel);
        Content = root;
    }

    public void SetProgress(double progress, string? text = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetProgress(progress, text));
            return;
        }

        var normalized = Math.Clamp(progress, 0, 1);
        if (_isCanceling)
        {
            return;
        }

        _progressBar.IsIndeterminate = false;
        _progressBar.Value = normalized * 100;
        _progressText.Text = text ?? $"{normalized:P0}";
    }

    public void SetIndeterminate(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetIndeterminate(text));
            return;
        }

        if (_isCanceling)
        {
            return;
        }

        _progressBar.IsIndeterminate = true;
        _progressText.Text = text;
    }

    public void SetByteProgress(long completedBytes, long? totalBytes)
    {
        if (totalBytes is > 0)
        {
            var progress = Math.Clamp(completedBytes / (double)totalBytes.Value, 0, 1);
            SetProgress(
                progress,
                $"{progress:P0} ({FormatBytes(completedBytes)} of {FormatBytes(totalBytes.Value)})");
            return;
        }

        SetIndeterminate($"Downloaded {FormatBytes(completedBytes)}");
    }

    public void CloseOperationWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(CloseOperationWindow);
            return;
        }

        _canClose = true;
        if (!_isClosed)
        {
            Close();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_canClose)
        {
            return;
        }

        RequestCancel();
        e.Cancel = true;
    }

    private void RequestCancel()
    {
        _cancelButton.IsEnabled = false;
        _progressText.Text = "Canceling…";
        if (_isCanceling)
        {
            return;
        }

        _isCanceling = true;
        try
        {
            _cancelOperation();
        }
        catch (Exception ex)
        {
            CancellationError = ex;
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";
    }
}
