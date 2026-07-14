using System.IO;
using System.Text.Json;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

public sealed class AppSettingsService
{
    private const string LastFmCredentialKey = "lastfm.credentials.v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly ISecureCredentialStore _credentialStore;

    public AppSettingsService()
        : this(
            Path.Combine(AppMetadata.LocalApplicationDataDirectory, "settings.json"),
            new DpapiCredentialStore())
    {
    }

    public AppSettingsService(ISecureCredentialStore credentialStore)
        : this(
            Path.Combine(AppMetadata.LocalApplicationDataDirectory, "settings.json"),
            credentialStore)
    {
    }

    internal AppSettingsService(string settingsPath)
        : this(
            settingsPath,
            new DpapiCredentialStore(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(settingsPath))!,
                "credentials")))
    {
    }

    internal AppSettingsService(string settingsPath, ISecureCredentialStore credentialStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(credentialStore);
        _settingsPath = Path.GetFullPath(settingsPath);
        _credentialStore = credentialStore;
        Settings = LoadSettings();
    }

    public event EventHandler? SettingsChanged;

    public AppSettings Settings { get; private set; }

    /// <summary>
    /// The shared protected store used by settings. A Globe connection service
    /// can reuse it so all credentials live under the same environment-specific
    /// DPAPI store.
    /// </summary>
    public ISecureCredentialStore CredentialStore => _credentialStore;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);

        // Protect credentials before replacing settings.json. If DPAPI or the
        // credential store fails, the settings file is left untouched rather
        // than silently discarding the only usable copy of the credentials.
        PersistLastFmCredentials(normalized.LastFm);
        WriteSettingsFile(normalized);

        Settings = normalized;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the legacy runtime link flag for existing views. The protected
    /// token remains authoritative. Moving to an unlinked state always forces
    /// auto-share off and persists that change.
    /// </summary>
    public void SetGlobeConnectionState(bool isLinked)
    {
        var social = Settings.Social with
        {
            IsAccountLinked = isLinked,
            AutomaticallyShareBurns = isLinked && Settings.Social.AutomaticallyShareBurns
        };
        if (social == Settings.Social)
        {
            return;
        }

        if (isLinked)
        {
            // The linked flag is intentionally runtime-only. Avoid rewriting
            // settings.json (and the protected Last.fm bundle) for a status
            // refresh that has no persistent setting changes.
            Settings = Settings with { Social = social };
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Save(Settings with { Social = social });
    }

    private AppSettings LoadSettings()
    {
        var settings = new AppSettings();
        string? json = null;
        try
        {
            if (File.Exists(_settingsPath))
            {
                json = File.ReadAllText(_settingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            settings = new AppSettings();
            json = null;
        }

        settings = Normalize(settings);
        if (TryReadProtectedLastFmCredentials(out var protectedCredentials))
        {
            ApplyLastFmCredentials(settings.LastFm, protectedCredentials);

            // A previous migration may have committed the DPAPI copy but
            // failed while atomically replacing settings.json. Keep the
            // protected copy authoritative and retry removing any legacy
            // plaintext on every launch until that sanitization succeeds.
            if (json is not null && TryReadLegacyLastFmCredentials(json, out _))
            {
                try
                {
                    WriteSettingsFile(settings);
                }
                catch
                {
                    // Leave the legacy file intact so the next launch can retry.
                }
            }
            return settings;
        }

        if (json is null || !TryReadLegacyLastFmCredentials(json, out var legacyCredentials))
        {
            return settings;
        }

        ApplyLastFmCredentials(settings.LastFm, legacyCredentials);
        try
        {
            // One-time migration: only sanitize settings.json after the
            // protected copy has been committed successfully.
            PersistLastFmCredentials(settings.LastFm);
            WriteSettingsFile(settings);
        }
        catch
        {
            // Keep the legacy file intact. A later explicit Save will retry the
            // protected write before it is allowed to replace settings.json.
        }

        return settings;
    }

    private bool TryReadProtectedLastFmCredentials(out LastFmSecretData credentials)
    {
        credentials = new LastFmSecretData();
        try
        {
            var protectedJson = _credentialStore.Read(LastFmCredentialKey);
            if (string.IsNullOrWhiteSpace(protectedJson))
            {
                return false;
            }

            credentials = JsonSerializer.Deserialize<LastFmSecretData>(protectedJson)
                ?? new LastFmSecretData();
            return credentials.HasAnyValue;
        }
        catch
        {
            return false;
        }
    }

    private void PersistLastFmCredentials(LastFmCredentials credentials)
    {
        var protectedCredentials = new LastFmSecretData
        {
            ApiKey = credentials.ApiKey,
            ApiSecret = credentials.ApiSecret,
            Username = credentials.Username,
            Password = credentials.Password
        };
        if (!protectedCredentials.HasAnyValue)
        {
            _credentialStore.Delete(LastFmCredentialKey);
            return;
        }

        _credentialStore.Write(
            LastFmCredentialKey,
            JsonSerializer.Serialize(protectedCredentials));
    }

    private static bool TryReadLegacyLastFmCredentials(string json, out LastFmSecretData credentials)
    {
        credentials = new LastFmSecretData();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("LastFm", out var lastFm)
                || lastFm.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            credentials = new LastFmSecretData
            {
                ApiKey = ReadString(lastFm, "ApiKey"),
                ApiSecret = ReadString(lastFm, "ApiSecret"),
                Username = ReadString(lastFm, "Username"),
                Password = ReadString(lastFm, "Password")
            };
            return credentials.HasAnyValue;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadString(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private void WriteSettingsFile(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void ApplyLastFmCredentials(
        LastFmCredentials target,
        LastFmSecretData source)
    {
        target.ApiKey = source.ApiKey.Trim();
        target.ApiSecret = source.ApiSecret.Trim();
        target.Username = source.Username.Trim();
        target.Password = source.Password;
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.LastFm ??= new LastFmCredentials();
        settings.Behavior ??= new BehaviorSettings();
        settings.Social ??= new SocialSettings();
        settings.LastFm.ApiKey = settings.LastFm.ApiKey.Trim();
        settings.LastFm.ApiSecret = settings.LastFm.ApiSecret.Trim();
        settings.LastFm.Username = settings.LastFm.Username.Trim();
        return settings;
    }

    private sealed record class LastFmSecretData
    {
        public string ApiKey { get; init; } = string.Empty;
        public string ApiSecret { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;

        public bool HasAnyValue =>
            !string.IsNullOrEmpty(ApiKey)
            || !string.IsNullOrEmpty(ApiSecret)
            || !string.IsNullOrEmpty(Username)
            || !string.IsNullOrEmpty(Password);
    }
}
