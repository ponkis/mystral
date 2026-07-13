using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mystral.Services;

namespace Mystral.Controls;

public partial class SocialProfileFrame : UserControl
{
    private const int FrameSize = 139;
    private const int OnlineFrameCount = 40;
    private const int OnlineFrameDurationMilliseconds = 42;

    private readonly ImageSource _offlineFrame;
    private readonly ImageSource _onlineFrame;
    private readonly ImageSource _onlineFrameAnimation;
    private bool _isOnline;
    private long _transitionId;

    public SocialProfileFrame()
    {
        InitializeComponent();

        _offlineFrame = IconImageSource.LoadSiteImage("Resources/Images/XLFrameOffline.png");
        _onlineFrame = IconImageSource.LoadSiteImage("Resources/Images/XLFrameActive.png");
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
        StopAnimations();

        if (!animate)
        {
            _isOnline = isOnline;
            ProfilePictureImage.Source = profilePicture;
            ProfilePictureImage.Opacity = 1;
            ApplySettledPresence(isOnline);
            return;
        }

        AnimateProfilePicture(profilePicture, transitionId);
        if (_isOnline != isOnline)
        {
            AnimatePresenceChange(isOnline, transitionId);
        }
        else
        {
            ApplySettledPresence(isOnline);
        }

        _isOnline = isOnline;
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

    private void AnimatePresenceChange(bool isOnline, long transitionId)
    {
        FrontFrameImage.Source = _isOnline ? _onlineFrame : _offlineFrame;
        FrontFrameTranslate.X = 0;
        FrontFrameImage.Opacity = 1;

        BackFrameImage.Source = isOnline ? _onlineFrameAnimation : _offlineFrame;
        BackFrameTranslate.X = 0;
        BackFrameImage.Opacity = 0;

        var crossFadeDelay = isOnline
            ? TimeSpan.FromMilliseconds(400)
            : TimeSpan.Zero;
        var crossFadeDuration = TimeSpan.FromMilliseconds(isOnline ? 280 : 320);

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

        Opacity = isOnline ? 1 : 0.5;
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                isOnline ? 0.5 : 1,
                isOnline ? 1 : 0.5,
                TimeSpan.FromSeconds(1))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });

        if (!isOnline)
        {
            return;
        }

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

        spriteAnimation.Completed += (_, _) =>
        {
            if (transitionId != _transitionId || !_isOnline)
            {
                return;
            }

            BackFrameTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            BackFrameTranslate.X = 0;
            BackFrameImage.Source = _onlineFrame;
        };
        BackFrameTranslate.BeginAnimation(TranslateTransform.XProperty, spriteAnimation);
    }

    private void ApplySettledPresence(bool isOnline)
    {
        StopFrameAnimations();
        Opacity = isOnline ? 1 : 0.5;
        BackFrameImage.Source = isOnline ? _onlineFrame : _offlineFrame;
        BackFrameImage.Opacity = 1;
        BackFrameTranslate.X = 0;
        FrontFrameImage.Source = null;
        FrontFrameImage.Opacity = 0;
        FrontFrameTranslate.X = 0;
    }

    private void StopAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        ProfilePictureImage.BeginAnimation(OpacityProperty, null);
        StopFrameAnimations();
    }

    private void StopFrameAnimations()
    {
        BackFrameImage.BeginAnimation(OpacityProperty, null);
        FrontFrameImage.BeginAnimation(OpacityProperty, null);
        BackFrameTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        FrontFrameTranslate.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
