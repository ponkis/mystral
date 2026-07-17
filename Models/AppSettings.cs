using System.Text.Json.Serialization;

namespace Mystral.Models;

public sealed record class AppSettings
{
    public LastFmCredentials LastFm { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public SocialSettings Social { get; set; } = new();
}

public sealed record class LastFmCredentials
{
    public bool Enabled { get; set; }

    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    [JsonIgnore]
    public string ApiSecret { get; set; } = string.Empty;

    [JsonIgnore]
    public string Username { get; set; } = string.Empty;

    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public bool ScrobblingEnabled { get; set; }

    [JsonIgnore]
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ApiSecret)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);
}

public sealed record class BehaviorSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool EnableNotifications { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public BurnLyricsProvider BurnLyricsProvider { get; set; } = BurnLyricsProvider.MusicBrainzAssisted;
}

public enum BurnLyricsProvider
{
    MusicBrainzAssisted,
    Lrclib
}

public sealed record class SocialSettings
{
    // Link state is derived from the protected Globe token and its server-side
    // validation. Keep this runtime-only property while the WPF views migrate
    // away from treating settings.json as the source of truth.
    [JsonIgnore]
    public bool IsAccountLinked { get; set; }

    public bool AutomaticallyShareBurns { get; set; }
}
