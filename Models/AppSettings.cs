using System.Globalization;
using System.Text.Json.Serialization;

namespace Mystral.Models;

public sealed record class AppSettings
{
    public LastFmCredentials LastFm { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
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

    /// <summary>
    /// Enough to look tracks up and open the Last.fm profile: API key + username.
    /// </summary>
    [JsonIgnore]
    public bool HasViewerCredentials =>
        Enabled
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Username);

    /// <summary>
    /// Scrobbling additionally signs requests and opens a session, so it needs
    /// the API secret and account password on top of the viewer credentials.
    /// </summary>
    [JsonIgnore]
    public bool HasScrobblingCredentials =>
        HasViewerCredentials
        && !string.IsNullOrWhiteSpace(ApiSecret)
        && !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Every credential the currently enabled feature set requires is present.
    /// </summary>
    [JsonIgnore]
    public bool IsConfigured =>
        ScrobblingEnabled ? HasScrobblingCredentials : HasViewerCredentials;
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

public sealed record class AppearanceSettings
{
    public const string DefaultPlayerThemeColor = "#4A5258";

    /// <summary>
    /// An opaque RGB color in canonical #RRGGBB form. An empty value keeps the
    /// player's automatic artwork-derived tint.
    /// </summary>
    public string PlayerThemeColor { get; set; } = string.Empty;

    public static string NormalizePlayerThemeColor(string? value)
    {
        return TryParsePlayerThemeColor(value, out var red, out var green, out var blue)
            ? FormatPlayerThemeColor(red, green, blue)
            : string.Empty;
    }

    public static bool TryParsePlayerThemeColor(
        string? value,
        out byte red,
        out byte green,
        out byte blue)
    {
        red = 0;
        green = 0;
        blue = 0;

        var candidate = value?.Trim();
        if (candidate is null || candidate.Length != 7 || candidate[0] != '#')
        {
            return false;
        }

        if (!byte.TryParse(
                candidate.AsSpan(1, 2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var parsedRed)
            || !byte.TryParse(
                candidate.AsSpan(3, 2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var parsedGreen)
            || !byte.TryParse(
                candidate.AsSpan(5, 2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var parsedBlue))
        {
            return false;
        }

        red = parsedRed;
        green = parsedGreen;
        blue = parsedBlue;
        return true;
    }

    public static string FormatPlayerThemeColor(byte red, byte green, byte blue)
    {
        return $"#{red:X2}{green:X2}{blue:X2}";
    }
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
