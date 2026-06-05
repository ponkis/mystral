using System.ComponentModel;
using System.Windows;
using Mystral.Models;
using Mystral.Services;

namespace Mystral.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly LastFmService _lastFmService;
    private bool _isLoadingSettings;
    private bool _hasUnsavedChanges;
    private bool _isClosingConfirmed;

    public SettingsWindow(AppSettingsService settingsService, LastFmService lastFmService)
    {
        _settingsService = settingsService;
        _lastFmService = lastFmService;

        InitializeComponent();
        SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("res/settings.ico", 16);
        StatusIcon.Source = IconImageSource.LoadBestFitFrame("res/img/info.ico", 16);
        LoadSettings();
        CategoriesListBox.SelectedItem = LastFmCategoryItem;
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
        _isLoadingSettings = false;

        _hasUnsavedChanges = false;
        UpdateLastFmStatus();
        UpdateDirtyStatus();
    }

    private void CategoriesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var showLastFm = CategoriesListBox.SelectedItem == LastFmCategoryItem;
        LastFmPanel.Visibility = showLastFm ? Visibility.Visible : Visibility.Collapsed;
        BehaviorPanel.Visibility = showLastFm ? Visibility.Collapsed : Visibility.Visible;
        SettingsTitleText.Text = showLastFm ? "Last.fm" : "Behavior";
        SettingsHeaderIcon.Source = showLastFm
            ? LoadSiteImage("res/img/lastfm.png")
            : IconImageSource.LoadBestFitFrame("res/settings.ico", 16);
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _hasUnsavedChanges = true;
        UpdateLastFmStatus();
        UpdateDirtyStatus();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync();
    }

    private async Task<bool> SaveSettingsAsync()
    {
        var settings = CreateSettingsFromFields();
        if (settings.LastFm.Enabled && !settings.LastFm.IsConfigured)
        {
            AppDialogWindow.ShowWarning(
                this,
                "Last.fm incomplete",
                "Fill in all Last.fm fields to enable Last.fm features.");
            return false;
        }

        SaveButton.IsEnabled = false;
        try
        {
            if (settings.LastFm.IsConfigured)
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
            AppDialogWindow.ShowInformation(
                this,
                "Settings saved",
                "Your settings were saved successfully.");
            return true;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private AppSettings CreateSettingsFromFields()
    {
        return new AppSettings
        {
            LastFm = new LastFmCredentials
            {
                Enabled = EnableLastFmCheckBox.IsChecked == true,
                ApiKey = ApiKeyBox.Text,
                ApiSecret = ApiSecretBox.Text,
                Username = UsernameBox.Text,
                Password = PasswordBox.Password,
                ScrobblingEnabled = ScrobbleCheckBox.IsChecked == true
            },
            Behavior = new BehaviorSettings
            {
                CloseToTray = CloseToTrayCheckBox.IsChecked == true
            }
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingConfirmed || !_hasUnsavedChanges)
        {
            return;
        }

        var result = AppDialogWindow.ShowQuestion(
            this,
            "Unsaved changes",
            "Save your settings before closing?");

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.No)
        {
            _isClosingConfirmed = true;
            return;
        }

        e.Cancel = true;
        if (await SaveSettingsAsync())
        {
            _isClosingConfirmed = true;
            Close();
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
            ? LoadSiteImage("res/img/WarningIcon.png")
            : IconImageSource.LoadBestFitFrame("res/img/info.ico", 16);
    }

    private void UpdateDirtyStatus()
    {
        DirtyStatusText.Text = _hasUnsavedChanges ? "Unsaved changes" : string.Empty;
    }

    private static System.Windows.Media.ImageSource LoadSiteImage(string relativePath)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.UriSource = new Uri($"pack://siteoforigin:,,,/{relativePath.TrimStart('/', '\\').Replace('\\', '/')}", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
