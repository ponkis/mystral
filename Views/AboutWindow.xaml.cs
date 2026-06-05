using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Mystral.Configuration;
using Mystral.Services;

namespace Mystral.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        TitleText.Text = $"{AppMetadata.Name} - Music Player {AppMetadata.Version}";
        AppLogoImage.Source = IconImageSource.LoadBestFrame("res/ico.ico");
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RootCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RootCard.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 9, 9);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenExternalUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void SocialButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            OpenExternalUrl(url);
        }
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
