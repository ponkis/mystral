using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mystral.Configuration;
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
    private bool _isSaving;

    public SettingsWindow(AppSettingsService settingsService, LastFmService lastFmService)
    {
        _settingsService = settingsService;
        _lastFmService = lastFmService;

        InitializeComponent();
#if DEBUG
        Debug.Assert(IsNewerRelease("v1.1.5", "1.1.4"));
        Debug.Assert(IsNewerRelease("v1.1.4", "1.1.4-dev"));
        Debug.Assert(!IsNewerRelease("v1.1.4", "1.1.4"));
#endif
        SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("Resources/settings.ico", 16);
        StatusIcon.Source = IconImageSource.LoadBestFitFrame("Resources/Images/info.ico", 16);
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
        CheckForUpdatesOnStartupCheckBox.IsChecked = settings.Behavior.CheckForUpdatesOnStartup;
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
            SettingsHeaderIcon.Source = IconImageSource.LoadSiteImage("Resources/Images/lastfm.png");
        }
        else if (selectedItem == BehaviorCategoryItem)
        {
            SettingsTitleText.Text = "Behavior";
            SettingsHeaderIcon.Source = IconImageSource.LoadBestFitFrame("Resources/settings.ico", 16);
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
            if (showSuccess)
            {
                AppDialogWindow.ShowConfirmation(
                    this,
                    "Settings saved",
                    "Your settings were saved successfully.");
            }
            return true;
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
                CheckForUpdatesOnStartup = CheckForUpdatesOnStartupCheckBox.IsChecked == true
            }
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isSaving)
        {
            e.Cancel = true;
            return;
        }

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
        if (await SaveSettingsAsync(showSuccess: false))
        {
            _isClosingConfirmed = true;
            _ = Dispatcher.BeginInvoke(Close);
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
        UpdateProgressWindow? progressWindow = null;
        if (sourceButton is not null)
        {
            sourceButton.IsEnabled = false;
            sourceButton.Content = "Checking...";
        }

        try
        {
            using var downloadCancellation = new CancellationTokenSource();
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
            Exception? downloadError = null;
            progressWindow = new UpdateProgressWindow(owner, $"Mystral {latestVersion}", downloadCancellation.Cancel);
            progressWindow.ContentRendered += async (_, _) =>
            {
                try
                {
                    installerPath = await DownloadInstallerAsync(client, installerUrl, installerName, progressWindow.SetProgress, downloadCancellation.Token);
                }
                catch (Exception ex)
                {
                    downloadError = ex;
                }
                finally
                {
                    progressWindow.CloseDownloadWindow();
                }
            };
            progressWindow.ShowDialog();
            progressWindow = null;

            if (downloadError is not null)
            {
                ExceptionDispatchInfo.Capture(downloadError).Throw();
            }

            if (installerPath is null)
            {
                return;
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
            progressWindow?.CloseDownloadWindow();
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

    private static async Task<string> DownloadInstallerAsync(HttpClient client, string url, string assetName, Action<long, long?> progress, CancellationToken cancellationToken)
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

    private sealed class UpdateProgressWindow : Window
    {
        private readonly Action _cancelDownload;
        private readonly ProgressBar _progressBar = new()
        {
            Height = 22,
            Minimum = 0,
            Maximum = 100,
            Foreground = System.Windows.Media.Brushes.DodgerBlue
        };

        private readonly TextBlock _progressText = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Black,
            FontSize = 11,
            IsHitTestVisible = false,
            Text = "0%",
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        private readonly Button _cancelButton = new()
        {
            Content = "Cancel",
            Height = 23,
            MinWidth = 72
        };

        private bool _canClose;
        private bool _isClosed;
        private bool _isCanceling;

        public UpdateProgressWindow(Window owner, string versionInfo, Action cancelDownload)
        {
            _cancelDownload = cancelDownload;
            Title = "Downloading update";
            Icon = owner.Icon;
            Width = 430;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            if (owner.IsVisible)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            ShowInTaskbar = false;
            Background = System.Windows.Media.Brushes.White;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/PresentationFramework.Aero;component/themes/Aero.NormalColor.xaml", UriKind.Relative)
            });
            Closing += (_, e) =>
            {
                if (!_canClose)
                {
                    RequestCancel();
                    e.Cancel = true;
                }
            };
            Closed += (_, _) => _isClosed = true;
            _cancelButton.Click += (_, _) => RequestCancel();

            var progressGrid = new Grid
            {
                Margin = new Thickness(0, 12, 0, 0),
                Children =
                {
                    _progressBar,
                    _progressText
                }
            };

            var body = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Margin = new Thickness(0, 0, 0, 2),
                        FontSize = 19,
                        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(53, 90, 136)),
                        Text = "Downloading update",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Foreground = System.Windows.Media.Brushes.DimGray,
                        Text = versionInfo,
                        TextWrapping = TextWrapping.Wrap
                    },
                    progressGrid
                }
            };

            var root = new Grid
            {
                Background = new System.Windows.Media.ImageBrush
                {
                    ImageSource = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://siteoforigin:,,,/Resources/Images/dialog_background.png")),
                    Stretch = System.Windows.Media.Stretch.Fill
                }
            };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var contentGrid = new Grid { Margin = new Thickness(16) };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var iconImage = new Image
            {
                Source = IconImageSource.LoadBestFitFrame("Resources/ico.ico", 32),
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Top
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(iconImage, System.Windows.Media.BitmapScalingMode.HighQuality);
            contentGrid.Children.Add(iconImage);
            Grid.SetColumn(body, 1);
            body.Margin = new Thickness(8, 0, 0, 0);
            contentGrid.Children.Add(body);

            var buttonPanel = new StackPanel
            {
                Margin = new Thickness(15, 11, 15, 11),
                HorizontalAlignment = HorizontalAlignment.Right,
                Orientation = Orientation.Horizontal,
                Children = { _cancelButton }
            };

            root.Children.Add(contentGrid);
            Grid.SetRow(buttonPanel, 1);
            root.Children.Add(buttonPanel);
            Content = root;
        }

        public void SetProgress(long downloadedBytes, long? totalBytes)
        {
            if (totalBytes is > 0)
            {
                var percent = Math.Clamp(downloadedBytes * 100d / totalBytes.Value, 0, 100);
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = percent;
                _progressText.Text = $"{percent:0}% ({FormatBytes(downloadedBytes)} of {FormatBytes(totalBytes.Value)})";
                return;
            }

            _progressBar.IsIndeterminate = true;
            _progressText.Text = $"Downloaded {FormatBytes(downloadedBytes)}";
        }

        public void CloseDownloadWindow()
        {
            _canClose = true;
            if (!_isClosed)
            {
                Close();
            }
        }

        private void RequestCancel()
        {
            _cancelButton.IsEnabled = false;
            _progressText.Text = "Canceling...";
            if (_isCanceling)
            {
                return;
            }

            _isCanceling = true;
            _cancelDownload();
        }

        private static string FormatBytes(long bytes)
        {
            return bytes >= 1024 * 1024
                ? $"{bytes / 1024d / 1024d:0.0} MB"
                : $"{bytes / 1024d:0.0} KB";
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
