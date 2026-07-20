using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Mystral.Services;

namespace Mystral.Controls;

public partial class SocialProfileFrame : UserControl
{
    private const int FrameSize = 139;
    private const int OnlineFrameCount = 40;
    private const int OnlineFrameDurationMilliseconds = 42;

    private readonly ImageSource _offlineFrame;
    private readonly ImageSource _onlineFrameAnimation;
    private bool? _isOnline;
    private long _transitionId;

    public SocialProfileFrame()
    {
        InitializeComponent();

        _offlineFrame = IconImageSource.LoadSiteImage("Resources/Images/XLFrameOffline.png");
        _onlineFrameAnimation = IconImageSource.LoadSiteImage("Resources/Images/XLFrameActiveAnimation.png");

        SetProfile(
            isOnline: false,
            IconImageSource.LoadSiteImage("Resources/Images/placeholder_pfp.png"),
            animate: false);
    }

    public void SetProfile(bool isOnline, ImageSource profilePicture, bool animate)
    {
        ArgumentNullException.ThrowIfNull(profilePicture);

        var transitionId = ++_transitionId;
        SetProfilePicture(profilePicture, animate, transitionId);

        // Aerochat's frame only reacts to actual status changes; refreshes that
        // keep the same presence must not restart or cut short a transition.
        if (_isOnline == isOnline)
        {
            return;
        }

        if (animate && _isOnline is not null)
        {
            AnimatePresenceChange(isOnline);
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            ApplySettledPresence(isOnline);
        }

        _isOnline = isOnline;
    }

    private void SetProfilePicture(ImageSource profilePicture, bool animate, long transitionId)
    {
        if (ReferenceEquals(ProfilePictureImage.Source, profilePicture))
        {
            return;
        }

        if (animate)
        {
            AnimateProfilePicture(profilePicture, transitionId);
            return;
        }

        ProfilePictureImage.BeginAnimation(OpacityProperty, null);
        ProfilePictureImage.Source = profilePicture;
        ProfilePictureImage.Opacity = 1;
    }

    private void AnimateProfilePicture(ImageSource profilePicture, long transitionId)
    {
        if (ReferenceEquals(ProfilePictureImage.Source, profilePicture))
        {
            ProfilePictureImage.Opacity = 1;
            return;
        }

        ProfilePictureImage.Opacity = 0;
        var fadeOut = new DoubleAnimation(
            fromValue: 1,
            toValue: 0,
            duration: TimeSpan.FromMilliseconds(150));
        fadeOut.Completed += (_, _) =>
        {
            if (transitionId != _transitionId)
            {
                return;
            }

            ProfilePictureImage.Source = profilePicture;
            ProfilePictureImage.Opacity = 1;
            ProfilePictureImage.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        };
        ProfilePictureImage.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void AnimatePresenceChange(bool isOnline)
    {
        // Match Aerochat's ProfilePictureFrame exactly: online always uses the
        // non-looping animation sheet, including as the outgoing/settled source,
        // and both tiles restart from frame zero when the status flips.
        var wasOnline = _isOnline == true;
        SetFrameSource(
            FrontFrameImage,
            wasOnline ? _onlineFrameAnimation : _offlineFrame);
        FrontFrameTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        FrontFrameTranslate.X = 0;
        FrontFrameImage.Opacity = 1;

        SetFrameSource(
            BackFrameImage,
            isOnline ? _onlineFrameAnimation : _offlineFrame);
        BackFrameTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        BackFrameTranslate.X = 0;
        BackFrameImage.Opacity = 0;

        var newFrameCount = isOnline ? OnlineFrameCount : 1;
        var halfTimeMilliseconds = newFrameCount * OnlineFrameDurationMilliseconds / 2.0;
        var crossFadeDelayMilliseconds = wasOnline
            ? halfTimeMilliseconds
            : halfTimeMilliseconds / 2.0;
        var crossFadeDelay = TimeSpan.FromMilliseconds(crossFadeDelayMilliseconds);
        var crossFadeDuration = TimeSpan.FromMilliseconds(halfTimeMilliseconds / 2.0);

        FrontFrameImage.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, crossFadeDuration)
            {
                BeginTime = crossFadeDelay,
                FillBehavior = FillBehavior.HoldEnd
            });
        BackFrameImage.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, crossFadeDuration)
            {
                BeginTime = crossFadeDelay,
                FillBehavior = FillBehavior.HoldEnd
            });

        var currentOpacity = Opacity;
        var targetOpacity = isOnline ? 1 : 0.5;
        Opacity = targetOpacity;
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                currentOpacity,
                targetOpacity,
                TimeSpan.FromSeconds(1))
            {
                FillBehavior = FillBehavior.HoldEnd
            });

        // Aerochat resets the outgoing tile too, so a formerly-online frame
        // replays its sheet while it fades instead of freezing on frame zero.
        if (wasOnline)
        {
            FrontFrameTranslate.BeginAnimation(
                TranslateTransform.XProperty,
                CreateOnlineSpriteAnimation());
        }

        if (isOnline)
        {
            BackFrameTranslate.BeginAnimation(
                TranslateTransform.XProperty,
                CreateOnlineSpriteAnimation());
        }
    }

    private static DoubleAnimationUsingKeyFrames CreateOnlineSpriteAnimation()
    {
        var spriteAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(OnlineFrameCount * OnlineFrameDurationMilliseconds),
            FillBehavior = FillBehavior.HoldEnd
        };
        for (var frame = 0; frame < OnlineFrameCount; frame++)
        {
            spriteAnimation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                -frame * FrameSize,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame * OnlineFrameDurationMilliseconds))));
        }

        return spriteAnimation;
    }

    private void ApplySettledPresence(bool isOnline)
    {
        StopFrameAnimations();
        Opacity = isOnline ? 1 : 0.5;
        SetFrameSource(
            BackFrameImage,
            isOnline ? _onlineFrameAnimation : _offlineFrame);
        BackFrameImage.Opacity = 1;
        BackFrameTranslate.X = 0;
        SetFrameSource(FrontFrameImage, null);
        FrontFrameImage.Opacity = 0;
        FrontFrameTranslate.X = 0;
    }

    private static void SetFrameSource(Image image, ImageSource? source)
    {
        image.Source = source;
        if (source is BitmapSource bitmap)
        {
            // Aerochat's AnimatedTileImage deliberately uses pixel dimensions
            // instead of DPI-derived WPF dimensions so every 139px sprite step
            // lands on an exact frame boundary.
            image.Width = bitmap.PixelWidth;
            image.Height = bitmap.PixelHeight;
            return;
        }

        image.ClearValue(WidthProperty);
        image.ClearValue(HeightProperty);
    }

    private void StopFrameAnimations()
    {
        BackFrameImage.BeginAnimation(OpacityProperty, null);
        FrontFrameImage.BeginAnimation(OpacityProperty, null);
        BackFrameTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        FrontFrameTranslate.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
