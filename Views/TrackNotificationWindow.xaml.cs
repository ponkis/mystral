using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Mystral.Views
{
    public enum NotificationState
    {
        Opening,
        Open,
        Closing
    }

    public partial class TrackNotificationWindow : Window
    {
        public NotificationState State = NotificationState.Opening;
        public int ScreenWidth => (int)SystemParameters.WorkArea.Width;
        public int ScreenHeight => (int)SystemParameters.WorkArea.Height;

        public TrackNotificationWindow(string title, string artist, ImageSource? coverArt)
        {
            InitializeComponent();

            TrackTitleText.Text = title;
            TrackArtistText.Text = artist;

            if (coverArt != null)
            {
                ArtImage.Source = coverArt;
                ArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                ArtPlaceholder.Visibility = Visibility.Visible;
            }

            Left = ScreenWidth - Width - 10;
            Opacity = 0;
            RunOpenAnimation();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public async void RunOpenAnimation()
        {
            var windows = Application.Current.Windows;
            int offset = 0;
            foreach (var window in windows)
            {
                if (window is TrackNotificationWindow notification)
                {
                    if (notification == this) continue;
                    offset += (int)notification.ActualHeight + 18;
                    notification.Closing += Notification_Closing;
                }
            }
            double startTop = ScreenHeight;
            double endTop = ScreenHeight - Height - 10 - offset;

            // Animate via an interval
            int animationTime = 500;
            int steps = 10;
            double stepSize = (startTop - endTop) / steps;

            // Fade in setup
            double startOpacity = 0;
            double endOpacity = 1;
            double opacityStepSize = (endOpacity - startOpacity) / steps;

            for (int i = 0; i < steps; i++)
            {
                double top = startTop - stepSize * i;
                double opacity = startOpacity + opacityStepSize * i;

                await Dispatcher.InvokeAsync(() =>
                {
                    Top = top;
                    Opacity = opacity;
                });
                await Task.Delay(animationTime / steps);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                Top = endTop;
                Opacity = endOpacity;
                State = NotificationState.Open;
            });

            // Close after 5 seconds
            await Task.Delay(5000);
            RunCloseAnimation();
        }

        private void Notification_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (State != NotificationState.Open) return;
            if (sender is TrackNotificationWindow notification)
            {
                Top += notification.ActualHeight + 18;
            }
        }

        public async void RunCloseAnimation()
        {
            State = NotificationState.Closing;
            double startTop = Top;
            double endTop = ScreenHeight;

            int steps = 10;
            double stepSize = (startTop - endTop) / steps;

            double startOpacity = 1;
            double endOpacity = 0;
            double opacityStepSize = (startOpacity - endOpacity) / steps;

            for (int i = 0; i < steps; i++)
            {
                double top = startTop - stepSize * i;
                double opacity = startOpacity - opacityStepSize * i;

                await Dispatcher.InvokeAsync(() =>
                {
                    Top = top;
                    Opacity = opacity;
                });
                await Task.Delay(500 / steps);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                Top = endTop;
                Opacity = endOpacity;
            });

            Close();
        }
    }
}
