namespace Mystral.Models;

public sealed record class AppSettings
{
    public LastFmCredentials LastFm { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
}

public sealed record class LastFmCredentials
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool ScrobblingEnabled { get; set; }

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
}
