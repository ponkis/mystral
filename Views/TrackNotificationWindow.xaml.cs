using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;

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

        private static Color? ExtractDominantTint(BitmapSource? source)
        {
            if (source is null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return null;
            }

            try
            {
                BitmapSource sampled = source;
                var maxSide = Math.Max(source.PixelWidth, source.PixelHeight);
                if (maxSide > 64)
                {
                    var scale = 64.0 / maxSide;
                    sampled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                }

                if (sampled.Format != PixelFormats.Bgra32)
                {
                    sampled = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);
                }

                var width = sampled.PixelWidth;
                var height = sampled.PixelHeight;
                var stride = width * 4;
                var pixels = new byte[stride * height];
                sampled.CopyPixels(pixels, stride, 0);

                double total = 0;
                double rSum = 0, gSum = 0, bSum = 0;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];

                    if (a < 200) continue;

                    // Skip extremely dark or extremely light pixels
                    double l = (r * 0.2126 + g * 0.7152 + b * 0.0722) / 255.0;
                    if (l < 0.15 || l > 0.85) continue;

                    rSum += r;
                    gSum += g;
                    bSum += b;
                    total++;
                }

                if (total == 0)
                {
                    // Fallback to simple average of non-transparent pixels
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        byte b = pixels[i];
                        byte g = pixels[i + 1];
                        byte r = pixels[i + 2];
                        byte a = pixels[i + 3];

                        if (a < 128) continue;

                        rSum += r;
                        gSum += g;
                        bSum += b;
                        total++;
                    }
                }

                if (total > 0)
                {
                    var avgColor = Color.FromRgb(
                        (byte)Math.Clamp(Math.Round(rSum / total), 0, 255),
                        (byte)Math.Clamp(Math.Round(gSum / total), 0, 255),
                        (byte)Math.Clamp(Math.Round(bSum / total), 0, 255));
                    return PolishTint(avgColor);
                }
            }
            catch
            {
                // Ignore and fallback
            }

            return null;
        }

        private static Color PolishTint(Color color)
        {
            var gray = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
            double factor = 1.35; // boost saturation a bit
            return Color.FromRgb(
                ClampByte(gray + (color.R - gray) * factor),
                ClampByte(gray + (color.G - gray) * factor),
                ClampByte(gray + (color.B - gray) * factor));
        }

        private static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromRgb(
                ClampByte(from.R + (to.R - from.R) * amount),
                ClampByte(from.G + (to.G - from.G) * amount),
                ClampByte(from.B + (to.B - from.B) * amount));
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static byte ClampByte(double value)
        {
            return (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
    }
}
