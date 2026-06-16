using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using static Mystral.Services.ArtworkTint;

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

            UpdateArtwork(coverArt);

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
            var offset = 0;
            foreach (var window in windows)
            {
                if (window is TrackNotificationWindow notification)
                {
                    if (notification == this) continue;
                    offset += (int)notification.ActualHeight + 18;
                    notification.Closing += Notification_Closing;
                }
            }
            Top = ScreenHeight;
            AnimateDouble(Window.TopProperty, ScreenHeight - Height - 10 - offset, 500);
            AnimateDouble(OpacityProperty, 1, 500);

            await Task.Delay(500);
            State = NotificationState.Open;

            await Task.Delay(5000);
            if (State == NotificationState.Open)
            {
                RunCloseAnimation();
            }
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
            if (State == NotificationState.Closing)
            {
                return;
            }

            State = NotificationState.Closing;
            AnimateDouble(Window.TopProperty, ScreenHeight, 500);
            AnimateDouble(OpacityProperty, 0, 500);
            await Task.Delay(500);
            Close();
        }

        private void RoundedSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not FrameworkElement element || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }

            double radius = ReferenceEquals(element, GlassSurface) ? 9.0 : 5.0;
            var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
            if (clip.CanFreeze)
            {
                clip.Freeze();
            }

            element.Clip = clip;
        }

        public void UpdateArtwork(ImageSource? coverArt)
        {
            if (coverArt != null)
            {
                ArtImage.Source = coverArt;
                ArtPlaceholder.Visibility = Visibility.Collapsed;

                if (coverArt is BitmapSource bitmapSource)
                {
                    var tint = ExtractDominantTint(bitmapSource) ?? Color.FromRgb(74, 82, 88);
                    ApplyArtworkTint(tint);
                }
            }
            else
            {
                ArtImage.Source = null;
                ArtPlaceholder.Visibility = Visibility.Visible;
                ApplyArtworkTint(Color.FromRgb(74, 82, 88));
            }
        }

        public void ApplyArtworkTint(Color tint)
        {
            AnimateColor(CardTopStop, WithAlpha(Blend(tint, Colors.White, 0.20), 0x90));
            AnimateColor(CardUpperStop, WithAlpha(Blend(tint, Colors.White, 0.04), 0x82));
            AnimateColor(CardLowerStop, WithAlpha(Blend(tint, Colors.Black, 0.35), 0x76));
            AnimateColor(CardBottomStop, WithAlpha(Blend(tint, Colors.Black, 0.22), 0x84));
            AnimateColor(GlowPrimaryStop, WithAlpha(Blend(tint, Colors.White, 0.56), 0x76));
            AnimateColor(GlowSecondaryStop, WithAlpha(Blend(tint, Colors.White, 0.10), 0x1C));

            var borderBrushColor = WithAlpha(Blend(tint, Colors.White, 0.62), 0xA5);
            if (!IsLoaded)
            {
                RootCard.BorderBrush = new SolidColorBrush(borderBrushColor);
            }
            else
            {
                if (RootCard.BorderBrush is SolidColorBrush solidBrush)
                {
                    solidBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
                    {
                        To = borderBrushColor,
                        Duration = TimeSpan.FromMilliseconds(520),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    }, HandoffBehavior.SnapshotAndReplace);
                }
                else
                {
                    RootCard.BorderBrush = new SolidColorBrush(borderBrushColor);
                }
            }
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

        private void AnimateDouble(DependencyProperty property, double value, int milliseconds)
        {
            BeginAnimation(property, new DoubleAnimation
            {
                To = value,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            }, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
