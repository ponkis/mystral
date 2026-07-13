using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mystral.Services;

namespace Mystral.Views;

internal sealed class ShareStatusWindow : Window
{
    private readonly Func<CancellationToken, Task> _shareAsync;
    private readonly TextBlock _headingText;
    private readonly TextBlock _detailText;
    private readonly ProgressBar _progressBar;
    private readonly Button _retryButton;
    private readonly Button _closeButton;
    private bool _isRunning;

    internal ShareStatusWindow(Window owner, Func<CancellationToken, Task> shareAsync)
    {
        _shareAsync = shareAsync;
        Title = "Share to Globe";
        Icon = IconImageSource.LoadBestFitFrame("Resources/globe.ico", 16);
        Width = 450;
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

        _headingText = new TextBlock
        {
            FontSize = 19,
            Foreground = new SolidColorBrush(Color.FromRgb(53, 90, 136)),
            Text = "Sharing your burned CD",
            TextWrapping = TextWrapping.Wrap
        };
        _detailText = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brushes.DimGray,
            Text = "Sending the album details and cover art to Globe…",
            TextWrapping = TextWrapping.Wrap
        };
        _progressBar = new ProgressBar
        {
            Height = 22,
            Margin = new Thickness(0, 12, 0, 0),
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(Color.FromRgb(66, 145, 202))
        };
        _retryButton = new Button
        {
            Content = "Retry",
            Height = 23,
            MinWidth = 72,
            Visibility = Visibility.Collapsed
        };
        _closeButton = new Button
        {
            Content = "Close",
            Height = 23,
            MinWidth = 72,
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = true,
            IsCancel = true,
            IsEnabled = false
        };

        _retryButton.Click += async (_, _) => await RunShareAsync();
        _closeButton.Click += (_, _) => Close();
        Closing += Window_Closing;
        ContentRendered += async (_, _) => await RunShareAsync();

        var body = new StackPanel
        {
            Children = { _headingText, _detailText, _progressBar }
        };
        var contentGrid = new Grid { Margin = new Thickness(16) };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var globeIcon = new Image
        {
            Source = IconImageSource.LoadBestFitFrame("Resources/globe.ico", 32),
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(globeIcon, BitmapScalingMode.HighQuality);
        contentGrid.Children.Add(globeIcon);
        Grid.SetColumn(body, 1);
        body.Margin = new Thickness(8, 0, 0, 0);
        contentGrid.Children.Add(body);

        var buttonPanel = new StackPanel
        {
            Margin = new Thickness(15, 11, 15, 11),
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Children = { _retryButton, _closeButton }
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
        root.Children.Add(contentGrid);
        Grid.SetRow(buttonPanel, 1);
        root.Children.Add(buttonPanel);
        Content = root;
    }

    internal bool WasSuccessful { get; private set; }

    private async Task RunShareAsync()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        WasSuccessful = false;
        _headingText.Text = "Sharing your burned CD";
        _detailText.Text = "Sending the album details and cover art to Globe…";
        _progressBar.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = true;
        _retryButton.Visibility = Visibility.Collapsed;
        _closeButton.IsEnabled = false;

        try
        {
            await _shareAsync(CancellationToken.None);
            WasSuccessful = true;
            _headingText.Text = "Shared to Globe";
            _detailText.Text = "Your burned CD was shared to your Globe profile.";
            _progressBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _headingText.Text = "Couldn’t share to Globe";
            _detailText.Text = string.IsNullOrWhiteSpace(ex.Message)
                ? "Globe did not accept the share. Please try again."
                : ex.Message;
            _progressBar.Visibility = Visibility.Collapsed;
            _retryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _isRunning = false;
            _closeButton.IsEnabled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
        }
    }
}
