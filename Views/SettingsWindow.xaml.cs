using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

        LocalScrobbleCacheService.Instance.ScrobbleAdded += LocalScrobbleCache_ScrobbleAdded;
        Closed += (s, e) =>
        {
            LocalScrobbleCacheService.Instance.ScrobbleAdded -= LocalScrobbleCache_ScrobbleAdded;
        };
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
        HistoryPanel.Visibility = selectedItem == HistoryCategoryItem ? Visibility.Visible : Visibility.Collapsed;

        if (selectedItem == LastFmCategoryItem)
        {
            SettingsTitleText.Text = "Last.fm";
            SettingsHeaderIcon.Source = LoadSiteImage("res/img/lastfm.png");
        }
        else if (selectedItem == BehaviorCategoryItem)
        {
            SettingsTitleText.Text = "Behavior";
            SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("res/settings.ico", 16);
        }
        else if (selectedItem == HistoryCategoryItem)
        {
            SettingsTitleText.Text = "Playback History";
            SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("res/settings.ico", 16);
            LoadHistory();
        }
    }

    private bool AreSettingsEqual(AppSettings current, AppSettings saved)
    {
        if (current.LastFm.Enabled != saved.LastFm.Enabled) return false;
        if ((current.LastFm.ApiKey ?? "") != (saved.LastFm.ApiKey ?? "")) return false;
        if ((current.LastFm.ApiSecret ?? "") != (saved.LastFm.ApiSecret ?? "")) return false;
        if ((current.LastFm.Username ?? "") != (saved.LastFm.Username ?? "")) return false;
        if ((current.LastFm.Password ?? "") != (saved.LastFm.Password ?? "")) return false;
        if (current.LastFm.ScrobblingEnabled != saved.LastFm.ScrobblingEnabled) return false;

        if (current.Behavior.CloseToTray != saved.Behavior.CloseToTray) return false;
        if (current.Behavior.EnableNotifications != saved.Behavior.EnableNotifications) return false;
        if (current.Behavior.StartWithWindows != saved.Behavior.StartWithWindows) return false;

        return true;
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var current = CreateSettingsFromFields();
        _hasUnsavedChanges = !AreSettingsEqual(current, _settingsService.Settings);
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
                CloseToTray = CloseToTrayCheckBox.IsChecked == true,
                EnableNotifications = EnableNotificationsCheckBox.IsChecked == true,
                AlwaysOnTop = _settingsService.Settings.Behavior.AlwaysOnTop,
                StartWithWindows = StartWithWindowsCheckBox.IsChecked == true
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
}
