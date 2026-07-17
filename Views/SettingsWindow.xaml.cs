using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mystral.Configuration;
using Mystral.Models;
using Mystral.Services;

namespace Mystral.Views;

public partial class SettingsWindow : Window
{
    private const int MaximumSocialAvatarDownloadBytes = 5 * 1024 * 1024;
    private const int MaximumSocialAvatarSourceDimension = 8192;
    private const long MaximumSocialAvatarSourcePixels = 32L * 1024 * 1024;
    private const int MaximumSocialAvatarAspectRatio = 20;
    private const int SocialAvatarDecodeDimension = 256;
    private readonly AppSettingsService _settingsService;
    private readonly LastFmService _lastFmService;
    private readonly GlobeConnectionService _globeConnectionService;
    private bool _isLoadingSettings;
    private bool _hasUnsavedChanges;
    private bool _isClosingConfirmed;
    private bool _isSaving;
    private bool _isCloseRequestPending;
    private bool _isSocialAccountLinked;
    private bool _isSocialSharingAvailable;
    private bool _isSocialSigningIn;
    private bool _isGlobeApprovalPending;
    private bool _isGlobeApprovalCloseWarningOpen;
    private bool _startSocialLinkRequested;
    private long _socialProfileTransitionId;
    private long _socialSignInTransitionId;
    private CancellationTokenSource? _socialSignInCancellation;
    private GlobeProfile? _socialProfile;
    private ImageSource? _socialProfileImage;
    private int _socialAvatarGeneration;

    internal event EventHandler? CloseRequestCanceled;

    public SettingsWindow(
        AppSettingsService settingsService,
        LastFmService lastFmService,
        GlobeConnectionService globeConnectionService)
    {
        _settingsService = settingsService;
        _lastFmService = lastFmService;
        _globeConnectionService = globeConnectionService;

        InitializeComponent();
#if DEBUG
        Debug.Assert(IsNewerRelease("v1.1.5", "1.1.4"));
        Debug.Assert(IsNewerRelease("v1.1.4", "1.1.4-dev"));
        Debug.Assert(!IsNewerRelease("v1.1.4", "1.1.4"));
#endif
        SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("Resources/settings.ico", 16);
        StatusIcon.Source = IconImageSource.LoadBestFitFrame("Resources/Images/info.ico", 16);
        _globeConnectionService.StateChanged += GlobeConnectionService_StateChanged;
        LoadSettings();
        CategoriesListBox.SelectedItem = LastFmCategoryItem;

        LocalScrobbleCacheService.Instance.ScrobbleAdded += LocalScrobbleCache_ScrobbleAdded;
        Closed += (s, e) =>
        {
            CancelSocialSignIn(restoreProfile: false);
            _globeConnectionService.StateChanged -= GlobeConnectionService_StateChanged;
            LocalScrobbleCacheService.Instance.ScrobbleAdded -= LocalScrobbleCache_ScrobbleAdded;
        };
    }

    internal void ShowSocialSection(bool startLinking = false)
    {
        CategoriesListBox.SelectedItem = SocialCategoryItem;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _ = Dispatcher.BeginInvoke(() => _ = RefreshSocialProfileAsync());
        if (startLinking)
        {
            _startSocialLinkRequested = true;
            _ = Dispatcher.BeginInvoke(TryStartRequestedSocialLink);
        }
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        var settings = _settingsService.Settings;
        EnableLastFmCheckBox.IsChecked = settings.LastFm.Enabled;
        ApiKeyBox.Text = settings.LastFm.ApiKey;
        ApiSecretBox.Text = settings.LastFm.ApiSecret;
        UsernameBox.Text = settings.LastFm.Username;
        PasswordBox.Password = settings.LastFm.Password;
        ScrobbleCheckBox.IsChecked = settings.LastFm.ScrobblingEnabled;
        CloseToTrayCheckBox.IsChecked = settings.Behavior.CloseToTray;
        EnableNotificationsCheckBox.IsChecked = settings.Behavior.EnableNotifications;
        StartWithWindowsCheckBox.IsChecked = settings.Behavior.StartWithWindows;
        CheckForUpdatesOnStartupCheckBox.IsChecked = settings.Behavior.CheckForUpdatesOnStartup;
        BurnLyricsProviderComboBox.SelectedIndex = settings.Behavior.BurnLyricsProvider == BurnLyricsProvider.Lrclib
            ? 1
            : 0;
        var globeState = _globeConnectionService.State;
        _isSocialAccountLinked = globeState.IsLinked;
        _isSocialSharingAvailable = globeState.CanShare;
        _socialProfile = globeState.Profile;
        _socialProfileImage = TryLoadCachedSocialAvatar(_socialProfile);
        AutomaticallyShareBurnsCheckBox.IsChecked = settings.Social.AutomaticallyShareBurns;
        UpdateSocialPanel(animate: false);
        if (_socialProfile is not null)
        {
            _ = RefreshSocialAvatarAsync(_socialProfile, animate: false);
        }
        _isLoadingSettings = false;

        _hasUnsavedChanges = false;
        UpdateLastFmStatus();
        UpdateDirtyStatus();
    }

    private void CategoriesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selectedItem = CategoriesListBox.SelectedItem;
        
        LastFmPanel.Visibility = selectedItem == LastFmCategoryItem ? Visibility.Visible : Visibility.Collapsed;
        BehaviorPanel.Visibility = selectedItem == BehaviorCategoryItem ? Visibility.Visible : Visibility.Collapsed;
        SocialPanel.Visibility = selectedItem == SocialCategoryItem ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = selectedItem == HistoryCategoryItem ? Visibility.Visible : Visibility.Collapsed;

        if (selectedItem == LastFmCategoryItem)
        {
            SettingsTitleText.Text = "Last.fm";
            SettingsHeaderIcon.Source = IconImageSource.LoadSiteImage("Resources/Images/lastfm.png");
        }
        else if (selectedItem == BehaviorCategoryItem)
        {
            SettingsTitleText.Text = "Behavior";
            SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("Resources/settings.ico", 16);
        }
        else if (selectedItem == SocialCategoryItem)
        {
            SettingsTitleText.Text = "Social";
            SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("Resources/globe.ico", 16);
            if (IsLoaded)
            {
                _ = RefreshSocialProfileAsync();
            }
        }
        else if (selectedItem == HistoryCategoryItem)
        {
            SettingsTitleText.Text = "Playback History";
            SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("Resources/settings.ico", 16);
            LoadHistory();
        }
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        RefreshDirtyState();
    }

    private void RefreshDirtyState()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var current = CreateSettingsFromFields();
        _hasUnsavedChanges = current != _settingsService.Settings;
        UpdateLastFmStatus();
        UpdateDirtyStatus();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync();
    }

    private async Task<bool> SaveSettingsAsync(bool showSuccess = true)
    {
        var settings = CreateSettingsFromFields();
        var lastFmChanged = settings.LastFm != _settingsService.Settings.LastFm;
        if (!CanSave(settings))
        {
            UpdateLastFmStatus();
            UpdateDirtyStatus();
            return false;
        }

        _isSaving = true;
        UpdateDirtyStatus();
        try
        {
            if (lastFmChanged && settings.LastFm.IsConfigured)
            {
                SetLastFmStatus(
                    settings.LastFm.ScrobblingEnabled
                        ? "Checking Last.fm account and scrobbling access..."
                        : "Checking Last.fm API key and username...",
                    isWarning: false);
                var validation = await _lastFmService.ValidateCredentialsAsync(settings.LastFm);
                if (!validation.IsSuccess)
                {
                    SetLastFmStatus(validation.Message, isWarning: true);
                    AppDialogWindow.ShowWarning(
                        this,
                        "Last.fm check failed",
                        validation.Message);
                    return false;
                }
            }

            _settingsService.Save(settings);
            _hasUnsavedChanges = false;
            UpdateLastFmStatus();
            UpdateDirtyStatus();
            if (showSuccess)
            {
                AppDialogWindow.ShowConfirmation(
                    this,
                    "Settings saved",
                    "Your settings were saved successfully.");
            }
            return true;
        }
        catch (Exception ex)
        {
            AppDialogWindow.ShowError(
                this,
                "Could not save settings",
                ex.Message);
            return false;
        }
        finally
        {
            _isSaving = false;
            UpdateDirtyStatus();
        }
    }

    private AppSettings CreateSettingsFromFields()
    {
        return new AppSettings
        {
            LastFm = new LastFmCredentials
            {
                Enabled = EnableLastFmCheckBox.IsChecked == true,
                ApiKey = ApiKeyBox.Text.Trim(),
                ApiSecret = ApiSecretBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password,
                ScrobblingEnabled = ScrobbleCheckBox.IsChecked == true
            },
            Behavior = new BehaviorSettings
            {
                CloseToTray = CloseToTrayCheckBox.IsChecked == true,
                EnableNotifications = EnableNotificationsCheckBox.IsChecked == true,
                AlwaysOnTop = _settingsService.Settings.Behavior.AlwaysOnTop,
                StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
                CheckForUpdatesOnStartup = CheckForUpdatesOnStartupCheckBox.IsChecked == true,
                BurnLyricsProvider = BurnLyricsProviderComboBox.SelectedIndex == 1
                    ? BurnLyricsProvider.Lrclib
                    : BurnLyricsProvider.MusicBrainzAssisted
            },
            Social = new SocialSettings
            {
                IsAccountLinked = _isSocialAccountLinked,
                AutomaticallyShareBurns = _isSocialAccountLinked
                    && AutomaticallyShareBurnsCheckBox.IsChecked == true
            }
        };
    }

    private void SocialAd_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppMetadata.GlobeBaseUri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDialogWindow.ShowWarning(this, "Could not open globe", ex.Message);
        }
    }

    private async void LinkSocialAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_isSocialSigningIn)
        {
            return;
        }

        if (_isSocialAccountLinked)
        {
            ShowGlobeAccountAlreadyLinkedWarning();
            return;
        }

        if (_globeConnectionService.HasStoredToken)
        {
            AppDialogWindow.ShowWarning(
                this,
                "Checking globe link",
                "Mystral is still checking the saved globe link. Wait a moment and try again.");
            return;
        }

        _startSocialLinkRequested = false;
        var transitionId = ++_socialSignInTransitionId;
        using var cancellation = new CancellationTokenSource();
        _socialSignInCancellation = cancellation;
        SocialSigningInText.Text = "Waiting for globe approval...";
        SetSocialSigningInState(true);
        SetGlobeApprovalPending(true);

        try
        {
            var profile = await _globeConnectionService.LinkAsync(
                approvalUri => Process.Start(new ProcessStartInfo
                {
                    FileName = approvalUri.AbsoluteUri,
                    UseShellExecute = true
                }),
                cancellation.Token);
            SetGlobeApprovalPending(false);
            if (cancellation.IsCancellationRequested ||
                transitionId != _socialSignInTransitionId ||
                !IsLoaded)
            {
                return;
            }

            _isSocialAccountLinked = true;
            _isSocialSharingAvailable = true;
            _socialProfile = profile;
            AutomaticallyShareBurnsCheckBox.IsChecked = false;
            UpdateSocialPanel(animate: true);
            _ = RefreshSocialAvatarAsync(profile, animate: true);
            RefreshDirtyState();
            SetSocialSigningInState(false);
            AppDialogWindow.ShowConfirmationWithBadge(
                this,
                "globe account linked",
                "Your globe account is now linked to Mystral.",
                "Resources/user.ico",
                "Resources/checkmark.ico");
        }
        catch (OperationCanceledException)
        {
            SetGlobeApprovalPending(false);
        }
        catch (GlobeLinkCancelledException)
        {
            SetGlobeApprovalPending(false);
            if (IsLoaded && transitionId == _socialSignInTransitionId)
            {
                SetSocialSigningInState(false);
                AppDialogWindow.ShowError(
                    this,
                    "Link canceled",
                    "The globe account link was canceled.");
            }
        }
        catch (Exception ex)
        {
            SetGlobeApprovalPending(false);
            if (IsLoaded)
            {
                SetSocialSigningInState(false);
                AppDialogWindow.ShowError(this, "Could not link globe", ex.Message);
            }
        }
        finally
        {
            if (transitionId == _socialSignInTransitionId)
            {
                _socialSignInCancellation = null;
                SetGlobeApprovalPending(false);
                SetSocialSigningInState(false);
            }
        }
    }

    private void TryStartRequestedSocialLink()
    {
        if (!_startSocialLinkRequested || !IsLoaded)
        {
            return;
        }

        if (_isSocialSigningIn)
        {
            _startSocialLinkRequested = false;
            if (_isSocialAccountLinked && !_isGlobeApprovalPending)
            {
                ShowGlobeAccountAlreadyLinkedWarning();
            }
            return;
        }

        if (_isSocialAccountLinked)
        {
            if (_globeConnectionService.State.Status == GlobeConnectionStatus.Validating
                && _globeConnectionService.HasStoredToken)
            {
                return;
            }

            _startSocialLinkRequested = false;
            ShowGlobeAccountAlreadyLinkedWarning();
            return;
        }

        // A cached token may still be completing its initial validation. Wait
        // for StateChanged instead of opening a second, conflicting flow.
        if (_globeConnectionService.HasStoredToken)
        {
            return;
        }

        _startSocialLinkRequested = false;
        LinkSocialAccount_Click(this, new RoutedEventArgs());
    }

    private async void UnlinkSocialAccount_Click(object sender, RoutedEventArgs e)
    {
        CancelSocialSignIn(restoreProfile: true);
        if (!_isSocialAccountLinked || _globeConnectionService.State.IsOffline)
        {
            return;
        }

        if (AppDialogWindow.ShowQuestion(
                this,
                "Unlink globe account",
                "Are you sure you want to unlink your account?") != MessageBoxResult.Yes)
        {
            return;
        }

        SocialSigningInText.Text = "Unlinking from globe...";
        SetSocialSigningInState(true);
        try
        {
            await _globeConnectionService.UnlinkAsync();
            _isSocialAccountLinked = false;
            _isSocialSharingAvailable = false;
            _socialProfile = null;
            _socialProfileImage = null;
            AutomaticallyShareBurnsCheckBox.IsChecked = false;
            UpdateSocialPanel(animate: true);
            RefreshDirtyState();
            SetSocialSigningInState(false);
            AppDialogWindow.ShowConfirmationWithBadge(
                this,
                "Account unlinked",
                "Your account was unlinked successfully.",
                "Resources/user.ico",
                "Resources/cross.ico");
        }
        catch (Exception ex)
        {
            SetSocialSigningInState(false);
            AppDialogWindow.ShowError(this, "Could not unlink globe", ex.Message);
        }
        finally
        {
            if (IsLoaded)
            {
                SetSocialSigningInState(false);
            }
        }
    }

    private void SetSocialSigningInState(bool isSigningIn)
    {
        _isSocialSigningIn = isSigningIn;
        SocialProfileContent.Visibility = isSigningIn
            ? Visibility.Collapsed
            : Visibility.Visible;
        SocialSigningInPanel.Visibility = isSigningIn
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomaticallyShareBurnsCheckBox.IsEnabled = !isSigningIn
            && _isSocialAccountLinked
            && _isSocialSharingAvailable;

        if (isSigningIn)
        {
            StartSocialSignInAnimation();
        }
        else
        {
            StopSocialSignInAnimation();
        }
    }

    private void SetGlobeApprovalPending(bool isPending)
    {
        _isGlobeApprovalPending = isPending;
        CloseButton.IsEnabled = !isPending;
    }

    private void ShowGlobeAccountAlreadyLinkedWarning()
    {
        AppDialogWindow.ShowWarning(
            this,
            "globe account already linked",
            "Unlink your current globe account before linking another one.");
    }

    private void StartSocialSignInAnimation()
    {
        const int frameCount = 32;
        const int frameWidth = 48;
        const int frameDurationMilliseconds = 58;

        SocialSigningInSpriteTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        SocialSigningInSpriteTranslate.X = 0;

        var spriteAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(frameCount * frameDurationMilliseconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        for (var frame = 0; frame < frameCount; frame++)
        {
            spriteAnimation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                -frame * frameWidth,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame * frameDurationMilliseconds))));
        }

        SocialSigningInSpriteTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            spriteAnimation);
    }

    private void StopSocialSignInAnimation()
    {
        SocialSigningInSpriteTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        SocialSigningInSpriteTranslate.X = 0;
    }

    private void CancelSocialSignIn(bool restoreProfile)
    {
        if (!_isSocialSigningIn && _socialSignInCancellation is null)
        {
            return;
        }

        _socialSignInTransitionId++;
        _socialSignInCancellation?.Cancel();
        _socialSignInCancellation = null;
        SetGlobeApprovalPending(false);

        if (restoreProfile)
        {
            SetSocialSigningInState(false);
        }
        else
        {
            _isSocialSigningIn = false;
        }
    }

    private void GlobeConnectionService_StateChanged(
        object? sender,
        GlobeConnectionStateChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => GlobeConnectionService_StateChanged(sender, e));
            return;
        }

        var wasLinked = _isSocialAccountLinked;
        var previousAvatarUrl = _socialProfile?.AvatarUrl;
        _isSocialAccountLinked = e.State.IsLinked;
        _isSocialSharingAvailable = e.State.CanShare;
        _socialProfile = e.State.Profile;
        if (e.State.Status == GlobeConnectionStatus.Unlinked)
        {
            _socialProfileImage = null;
            AutomaticallyShareBurnsCheckBox.IsChecked = false;
        }
        else if (_socialProfile is not null
                 && (!string.Equals(previousAvatarUrl, _socialProfile.AvatarUrl, StringComparison.Ordinal)
                     || _socialProfileImage is null))
        {
            _socialProfileImage = TryLoadCachedSocialAvatar(_socialProfile);
        }

        UpdateSocialPanel(animate: wasLinked != _isSocialAccountLinked);
        if (_socialProfile is not null
            && !e.State.IsChecking
            && string.IsNullOrWhiteSpace(e.State.ErrorMessage))
        {
            _ = RefreshSocialAvatarAsync(_socialProfile, animate: false);
        }
        RefreshDirtyState();
        TryStartRequestedSocialLink();
    }

    private async Task RefreshSocialProfileAsync()
    {
        if (!_globeConnectionService.HasStoredToken
            || _globeConnectionService.State.IsChecking)
        {
            return;
        }

        try
        {
            await _globeConnectionService.ValidateAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Validation publishes its linked/offline state and the main
            // window owns the once-per-outage warning. Opening Settings must
            // not add a second error dialog for the same background check.
        }
    }

    private async Task RefreshSocialAvatarAsync(GlobeProfile profile, bool animate)
    {
        var generation = ++_socialAvatarGeneration;
        var placeholder = IconImageSource.LoadSiteImage("Resources/Images/placeholder_pfp.png");
        if (!Uri.TryCreate(profile.AvatarUrl, UriKind.Absolute, out var avatarUri)
            || !AppMetadata.IsTrustedGlobeAvatarUri(avatarUri))
        {
            _socialProfileImage = placeholder;
            UpdateSocialPanel(animate);
            return;
        }

        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                CreateFreshAvatarRequestUri(avatarUri));
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true
            };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumSocialAvatarDownloadBytes)
            {
                throw new InvalidDataException("The globe profile image is too large.");
            }

            var bytes = await ReadSocialAvatarBytesAsync(response.Content, timeout.Token);

            var image = DecodeSocialAvatar(bytes);
            if (generation != _socialAvatarGeneration
                || _socialProfile?.AvatarUrl != profile.AvatarUrl
                || !IsLoaded)
            {
                return;
            }

            _socialProfileImage = image;
            _globeConnectionService.CacheAvatar(profile, EncodeSocialAvatar(image));
            UpdateSocialPanel(animate);
        }
        catch
        {
            if (generation == _socialAvatarGeneration
                && _socialProfile?.AvatarUrl == profile.AvatarUrl
                && IsLoaded)
            {
                _socialProfileImage ??= placeholder;
                UpdateSocialPanel(animate);
            }
        }
    }

    internal static Uri CreateFreshAvatarRequestUri(Uri avatarUri)
    {
        ArgumentNullException.ThrowIfNull(avatarUri);
        var cdnBase = AppMetadata.GlobeAvatarCdnBaseUri;
        if (!avatarUri.IsAbsoluteUri
            || cdnBase is null
            || !string.IsNullOrEmpty(avatarUri.Query)
            || !string.Equals(avatarUri.Scheme, cdnBase.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(avatarUri.IdnHost, cdnBase.IdnHost, StringComparison.OrdinalIgnoreCase)
            || avatarUri.Port != cdnBase.Port)
        {
            return avatarUri;
        }

        var builder = new UriBuilder(avatarUri)
        {
            Query = "mystral_refresh=" + Guid.NewGuid().ToString("N")
        };
        return builder.Uri;
    }

    private ImageSource? TryLoadCachedSocialAvatar(GlobeProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        var bytes = _globeConnectionService.GetCachedAvatar(profile.AvatarUrl);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return DecodeSocialAvatar(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource DecodeSocialAvatar(byte[] bytes)
    {
        using var metadataStream = new MemoryStream(bytes, writable: false);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            metadataStream,
            System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
            System.Windows.Media.Imaging.BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The globe profile image is invalid.");
        }

        var sourceWidth = decoder.Frames[0].PixelWidth;
        var sourceHeight = decoder.Frames[0].PixelHeight;
        if (!AreSocialAvatarDimensionsSafe(sourceWidth, sourceHeight))
        {
            throw new InvalidDataException("The globe profile image dimensions are not supported.");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        if (sourceWidth >= sourceHeight)
        {
            image.DecodePixelWidth = SocialAvatarDecodeDimension;
        }
        else
        {
            image.DecodePixelHeight = SocialAvatarDecodeDimension;
        }
        image.StreamSource = stream;
        image.EndInit();
        if (image.PixelWidth <= 0
            || image.PixelHeight <= 0
            || image.PixelWidth > SocialAvatarDecodeDimension
            || image.PixelHeight > SocialAvatarDecodeDimension)
        {
            throw new InvalidDataException("The globe profile image is invalid.");
        }

        image.Freeze();
        return image;
    }

    internal static bool AreSocialAvatarDimensionsSafe(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var smallerDimension = Math.Min(width, height);
        var largerDimension = Math.Max(width, height);
        return largerDimension <= MaximumSocialAvatarSourceDimension
               && (long)width * height <= MaximumSocialAvatarSourcePixels
               && (long)largerDimension <= (long)smallerDimension * MaximumSocialAvatarAspectRatio;
    }

    private static async Task<byte[]> ReadSocialAvatarBytesAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumSocialAvatarDownloadBytes)
            {
                throw new InvalidDataException("The globe profile image is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static byte[] EncodeSocialAvatar(System.Windows.Media.Imaging.BitmapSource image)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private void UpdateSocialPanel(bool animate)
    {
        AutomaticallyShareBurnsCheckBox.IsEnabled = _isSocialAccountLinked
            && _isSocialSharingAvailable
            && !_isSocialSigningIn;
        AutomaticallyShareBurnsCheckBox.ToolTip = _isSocialAccountLinked && !_isSocialSharingAvailable
            ? "Sharing will be available when globe reconnects."
            : null;
        var isGlobeOffline = _globeConnectionService.State.IsOffline;
        UnlinkSocialAccountLink.IsEnabled = _isSocialAccountLinked && !isGlobeOffline;
        UnlinkSocialAccountLink.ToolTip = isGlobeOffline
            ? "Unlinking will be available when globe reconnects."
            : null;
        var profile = _socialProfile;
        SocialUsernameText.Text = profile?.DisplayUsername
            ?? (_isSocialAccountLinked ? "globe unavailable" : string.Empty);
        SocialDisplayNameText.Text = profile?.DisplayName
            ?? (_isSocialAccountLinked ? "Account linked" : string.Empty);
        var cdCount = profile?.CdCount ?? 0;
        SocialCdCountText.Text = profile is null && _isSocialAccountLinked
            ? "Account details will refresh when globe reconnects."
            : $"{cdCount} {(cdCount == 1 ? "CD" : "CDs")} burned total";
        SocialProfileFrame.SetProfile(
            _isSocialAccountLinked,
            _socialProfileImage
            ?? IconImageSource.LoadSiteImage("Resources/Images/placeholder_pfp.png"),
            animate);

        var transitionId = ++_socialProfileTransitionId;
        SocialProfileDetailsHost.BeginAnimation(OpacityProperty, null);
        if (!animate)
        {
            ApplySocialProfileDetailsVisibility();
            SocialProfileDetailsHost.Opacity = 1;
            return;
        }

        SocialProfileDetailsHost.Opacity = 0;
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
        fadeOut.Completed += (_, _) =>
        {
            if (transitionId != _socialProfileTransitionId)
            {
                return;
            }

            ApplySocialProfileDetailsVisibility();
            SocialProfileDetailsHost.Opacity = 1;
            SocialProfileDetailsHost.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        };
        SocialProfileDetailsHost.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void ApplySocialProfileDetailsVisibility()
    {
        UnlinkedSocialProfilePanel.Visibility = _isSocialAccountLinked
            ? Visibility.Collapsed
            : Visibility.Visible;
        LinkedSocialProfilePanel.Visibility = _isSocialAccountLinked
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    internal void PrepareForCloseRequest()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    internal bool WarnIfGlobeApprovalPreventsClose()
    {
        if (!_isGlobeApprovalPending)
        {
            return false;
        }

        if (_isGlobeApprovalCloseWarningOpen)
        {
            return true;
        }

        _isGlobeApprovalCloseWarningOpen = true;
        try
        {
            AppDialogWindow.ShowWarning(
                this,
                "globe approval pending",
                "Finish or cancel the globe approval before closing Mystral.");
        }
        finally
        {
            _isGlobeApprovalCloseWarningOpen = false;
        }

        return true;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (WarnIfGlobeApprovalPreventsClose())
        {
            e.Cancel = true;
            CloseRequestCanceled?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isCloseRequestPending)
        {
            e.Cancel = true;
            return;
        }

        if (_isSaving)
        {
            e.Cancel = true;
            CloseRequestCanceled?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isClosingConfirmed || !_hasUnsavedChanges)
        {
            return;
        }

        _isCloseRequestPending = true;
        try
        {
            var result = AppDialogWindow.ShowQuestion(
                this,
                "Unsaved changes",
                "Save your settings before closing?");

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                CloseRequestCanceled?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (result == MessageBoxResult.No)
            {
                _isClosingConfirmed = true;
                return;
            }

            e.Cancel = true;
            if (await SaveSettingsAsync(showSuccess: false))
            {
                _isClosingConfirmed = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
            else
            {
                CloseRequestCanceled?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _isCloseRequestPending = false;
        }
    }

    private void UpdateLastFmStatus()
    {
        var credentials = CreateSettingsFromFields().LastFm;
        if (!credentials.Enabled)
        {
            SetLastFmStatus("Last.fm disabled.", isWarning: false);
            return;
        }

        if (credentials.IsConfigured)
        {
            SetLastFmStatus(
                credentials.ScrobblingEnabled
                    ? "Last.fm account and scrobbling access will be checked when saved."
                    : "Last.fm API key and username will be checked when saved.",
                isWarning: false);
            return;
        }

        SetLastFmStatus(
            "Last.fm needs every field before it can be enabled.",
            isWarning: true);
    }

    private void SetLastFmStatus(string message, bool isWarning)
    {
        LastFmStatusText.Text = message;
        LastFmStatusText.Foreground = isWarning
            ? System.Windows.Media.Brushes.DarkRed
            : System.Windows.Media.Brushes.DimGray;
        StatusIcon.Source = isWarning
            ? IconImageSource.LoadSiteImage("Resources/Images/WarningIcon.png")
            : IconImageSource.LoadBestFitFrame("Resources/Images/info.ico", 16);
    }

    private void UpdateDirtyStatus()
    {
        var settings = CreateSettingsFromFields();
        SaveButton.IsEnabled = _hasUnsavedChanges && !_isSaving && CanSave(settings);
        DirtyStatusText.Text = _isSaving
            ? "Saving..."
            : _hasUnsavedChanges
                ? CanSave(settings) ? "Unsaved changes" : "Complete required fields"
                : string.Empty;
    }

    private static bool CanSave(AppSettings settings)
    {
        return !settings.LastFm.Enabled || settings.LastFm.IsConfigured;
    }

    private void LoadHistory()
    {
        try
        {
            var records = LocalScrobbleCacheService.Instance.LoadAllRecords();
            HistoryListView.ItemsSource = records;

            bool hasHistory = records != null && records.Count > 0;
            ClearHistoryButton.IsEnabled = hasHistory;
            ExportHistoryButton.IsEnabled = hasHistory;
        }
        catch
        {
            ClearHistoryButton.IsEnabled = false;
            ExportHistoryButton.IsEnabled = false;
        }
    }

    private void LocalScrobbleCache_ScrobbleAdded(object? sender, ScrobbleRecord record)
    {
        Dispatcher.Invoke(() =>
        {
            if (CategoriesListBox.SelectedItem == HistoryCategoryItem)
            {
                LoadHistory();
            }
        });
    }

    private void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        LoadHistory();
    }

    internal static async Task CheckForUpdatesAsync(Window owner, bool showNoUpdateMessage, bool showErrors, Button? sourceButton = null)
    {
        var originalContent = sourceButton?.Content;
        OperationProgressWindow? progressWindow = null;
        if (sourceButton is not null)
        {
            sourceButton.IsEnabled = false;
            sourceButton.Content = "Checking...";
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);

            using var response = await client.GetAsync("https://api.github.com/repos/ponkis/mystral/releases/latest");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var release = await JsonDocument.ParseAsync(stream);
            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new InvalidOperationException("GitHub release did not include a tag name.");
            }

            if (!IsNewerRelease(tag, AppMetadata.Version))
            {
                if (showNoUpdateMessage)
                {
                    AppDialogWindow.ShowInformation(owner, "No updates found", $"Mystral {AppMetadata.Version} is up to date.");
                }
                return;
            }

            var latestVersion = tag.Trim().TrimStart('v', 'V');
            var (installerName, installerUrl) = FindInstallerAsset(root);
            if (AppDialogWindow.ShowQuestion(owner, "Update available", $"Mystral {latestVersion} is available. Download and run the installer? Mystral will close.") != MessageBoxResult.Yes)
            {
                return;
            }

            if (sourceButton is not null)
            {
                sourceButton.Content = "Downloading...";
            }

            string? installerPath = null;
            while (installerPath is null)
            {
                using var downloadCancellation = new CancellationTokenSource();
                Exception? downloadError = null;
                var activeProgressWindow = new OperationProgressWindow(
                    owner,
                    "Downloading update",
                    "Downloading update",
                    $"Mystral {latestVersion}",
                    isIndeterminate: false,
                    downloadCancellation.Cancel,
                    iconPath: "Resources/ico.ico");
                progressWindow = activeProgressWindow;
                activeProgressWindow.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        installerPath = await DownloadInstallerAsync(
                            client,
                            installerUrl,
                            installerName,
                            activeProgressWindow.SetByteProgress,
                            downloadCancellation.Token);
                    }
                    catch (Exception ex)
                    {
                        downloadError = ex;
                    }
                    finally
                    {
                        activeProgressWindow.CloseOperationWindow();
                    }
                };
                activeProgressWindow.ShowDialog();
                progressWindow = null;

                if (downloadCancellation.IsCancellationRequested)
                {
                    DeleteDownloadedInstaller(installerPath);
                    AppDialogWindow.ShowInformation(
                        owner,
                        "Update canceled",
                        "The update download was canceled successfully. The installer was not launched, and Mystral was not changed.");
                    return;
                }

                downloadError ??= activeProgressWindow.CancellationError;
                if (downloadError is null && installerPath is not null)
                {
                    break;
                }

                downloadError ??= new IOException("The update download ended without producing an installer.");
                var retry = AppDialogWindow.ShowRetryCancel(
                    owner,
                    "Update download failed",
                    $"Mystral couldn't download version {latestVersion}.\n\nCause: {DescribeUpdateDownloadFailure(downloadError)}\n\nCheck your connection, then choose Retry to try again.");
                if (retry != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            if (sourceButton is not null)
            {
                sourceButton.Content = "Launching...";
            }

            if (Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true }) is null)
            {
                throw new InvalidOperationException("Windows did not start the installer.");
            }

            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                AppDialogWindow.ShowWarning(owner, "Update check failed", $"Could not check GitHub releases: {ex.Message}");
            }
        }
        finally
        {
            progressWindow?.CloseOperationWindow();
            if (sourceButton is not null)
            {
                sourceButton.Content = originalContent;
                sourceButton.IsEnabled = true;
            }
        }
    }

    private static (string Name, string Url) FindInstallerAsset(JsonElement release)
    {
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException("GitHub release installer did not include a download URL.");
            }

            return (name, url);
        }

        throw new InvalidOperationException("No win-x64 installer was attached to the latest GitHub release.");
    }

    internal static async Task<string> DownloadInstallerAsync(HttpClient client, string url, string assetName, Action<long, long?> progress, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(assetName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("GitHub release installer had an invalid file name.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("GitHub release installer URL was not HTTPS.");
        }

        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var path = Path.Combine(Path.GetTempPath(), fileName);
        var totalBytes = response.Content.Headers.ContentLength;
        var downloadedBytes = 0L;
        var buffer = new byte[81920];
        progress(0, totalBytes);

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(path);
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;
                progress(downloadedBytes, totalBytes);
            }

            if (downloadedBytes == 0)
            {
                throw new IOException("The update server returned an empty installer.");
            }

            if (totalBytes is > 0 && downloadedBytes != totalBytes.Value)
            {
                throw new IOException("The connection closed before the installer finished downloading.");
            }

            progress(downloadedBytes, totalBytes ?? downloadedBytes);
            return path;
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }

            throw;
        }
    }

    internal static string DescribeUpdateDownloadFailure(Exception exception)
    {
        if (exception is TaskCanceledException)
        {
            return "The download timed out.";
        }

        var cause = exception;
        while (cause.InnerException is not null)
        {
            cause = cause.InnerException;
        }

        if (cause is TaskCanceledException)
        {
            return "The download timed out.";
        }

        return string.IsNullOrWhiteSpace(cause.Message)
            ? "The connection was interrupted before the download completed."
            : cause.Message.Trim();
    }

    private static void DeleteDownloadedInstaller(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var result = AppDialogWindow.ShowQuestion(
            this,
            "Clear history",
            "Are you sure you want to delete all scrobble history? This action cannot be undone.");

        if (result == MessageBoxResult.Yes)
        {
            if (LocalScrobbleCacheService.Instance.ClearHistory())
            {
                LoadHistory();
                AppDialogWindow.ShowInformation(
                    this,
                    "History cleared",
                    "Playback history has been successfully cleared.");
            }
            else
            {
                AppDialogWindow.ShowError(
                    this,
                    "Error clearing history",
                    "Failed to clear playback history.");
            }
        }
    }

    private void RemoveHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is ScrobbleRecord record)
        {
            if (HistoryListView.ItemsSource is List<ScrobbleRecord> records)
            {
                var selectedRecords = records.Where(r => r.IsSelected).ToList();
                if (selectedRecords.Count > 0)
                {
                    if (!selectedRecords.Contains(record))
                    {
                        selectedRecords.Add(record);
                    }

                    var result = AppDialogWindow.ShowQuestion(
                        this,
                        "Delete items",
                        $"Are you sure you want to delete the {selectedRecords.Count} selected items?");

                    if (result == MessageBoxResult.Yes)
                    {
                        LocalScrobbleCacheService.Instance.RemoveRecords(selectedRecords);
                        LoadHistory();
                    }
                }
                else
                {
                    var result = AppDialogWindow.ShowQuestion(
                        this,
                        "Delete item",
                        $"Are you sure you want to delete this scrobble: \"{record.Title}\"?");

                    if (result == MessageBoxResult.Yes)
                    {
                        LocalScrobbleCacheService.Instance.RemoveRecord(record);
                        LoadHistory();
                    }
                }
            }
        }
    }

    private void ExportHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = "mystral_scrobbles.csv"
        };

        if (sfd.ShowDialog() == true)
        {
            try
            {
                var records = LocalScrobbleCacheService.Instance.LoadAllRecords();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Artist,Album,Track,Timestamp");
                foreach (var record in records)
                {
                    var artist = EscapeCsv(record.Artist);
                    var album = EscapeCsv(record.Album);
                    var track = EscapeCsv(record.Title);
                    sb.AppendLine($"{artist},{album},{track},{record.Timestamp}");
                }

                System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                AppDialogWindow.ShowInformation(this, "Export complete", "Playback history exported successfully!");
            }
            catch (Exception ex)
            {
                AppDialogWindow.ShowWarning(this, "Export failed", $"Failed to export history: {ex.Message}");
            }
        }
    }

    private static string EscapeCsv(string val)
    {
        if (string.IsNullOrEmpty(val)) return string.Empty;
        if (val.Contains(",") || val.Contains("\"") || val.Contains("\n") || val.Contains("\r"))
        {
            return $"\"{val.Replace("\"", "\"\"")}\"";
        }
        return val;
    }

    internal static bool IsNewerRelease(string latestTag, string currentVersion)
    {
        if (!TryReleaseVersion(latestTag, out var latest, out var latestIsPrerelease)
            || !TryReleaseVersion(currentVersion, out var current, out var currentIsPrerelease))
        {
            return false;
        }

        var versionCompare = latest.CompareTo(current);
        return versionCompare > 0 || (versionCompare == 0 && currentIsPrerelease && !latestIsPrerelease);
    }

    private static bool TryReleaseVersion(string value, out Version version, out bool isPrerelease)
    {
        version = new Version(0, 0, 0, 0);
        isPrerelease = false;
        value = value.Trim().TrimStart('v', 'V');

        var suffixIndex = value.IndexOf('-');
        if (suffixIndex >= 0)
        {
            isPrerelease = true;
            value = value[..suffixIndex];
        }

        if (!Version.TryParse(value, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }
}
