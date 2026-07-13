using System.IO;
using System.Text.Json;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettingsService()
        : this(Path.Combine(AppMetadata.LocalApplicationDataDirectory, "settings.json"))
    {
    }

    internal AppSettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
        Settings = LoadSettings();
    }

    public event EventHandler? SettingsChanged;

    public AppSettings Settings { get; private set; }

    public void Save(AppSettings settings)
    {
        Settings = Normalize(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return Normalize(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.LastFm ??= new LastFmCredentials();
        settings.Behavior ??= new BehaviorSettings();
        settings.Social ??= new SocialSettings();
        settings.LastFm.ApiKey = settings.LastFm.ApiKey.Trim();
        settings.LastFm.ApiSecret = settings.LastFm.ApiSecret.Trim();
        settings.LastFm.Username = settings.LastFm.Username.Trim();
        if (!settings.Social.IsAccountLinked)
        {
            settings.Social.AutomaticallyShareBurns = false;
        }
        return settings;
    }
}
