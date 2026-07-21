using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mystral.Controls;
using Mystral.Models;
using Mystral.Parsing;
using Mystral.Services;
using Mystral.Views;
using static Mystral.Tests.Samples;

namespace Mystral.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("LRC parser handles empty, invalid, repeated, and ordered lines", LrcParserTests.ParseCoversImportantCases),
            ("metadata cleaner normalizes media snapshots and rejects non-songs", LastFmMetadataCleanerTests.CleansAndClassifiesTracks),
            ("settings service protects and migrates Last.fm credentials", AppSettingsServiceTests.ProtectsMigratesNormalizesAndFallsBack),
            ("DPAPI store protects current-user credentials at rest", DpapiCredentialStoreTests.ProtectsRoundTripsAndDeletes),
            ("desktop activation accepts only the environment-specific Social route", DesktopActivationServiceTests.ValidatesProtocolRoute),
            ("Globe service links, refreshes profiles, shares idempotently, and unlinks", GlobeConnectionServiceTests.LinksSharesAndUnlinks),
            ("Globe service detects revoked tokens once and disables sharing", GlobeConnectionServiceTests.DetectsRevocationOnce),
            ("Globe service caches profiles, survives outages, and recovers", GlobeConnectionServiceTests.CachesProfileAndSurvivesOutages),
            ("Globe duplicate burn retries reconcile the live CD total", GlobeConnectionServiceTests.ReconcilesDuplicateBurnCount),
            ("Globe linking distinguishes browser cancellation", GlobeConnectionServiceTests.DetectsBrowserCancellation),
            ("Globe linking retains protected tokens until acknowledgement recovers", GlobeConnectionServiceTests.RecoversPendingAcknowledgement),
            ("Globe missing claim and revoke endpoints fail safely", GlobeConnectionServiceTests.TreatsMissingEndpointsAsFailures),
            ("Globe avatar dimensions reject oversized and extreme inputs", GlobeConnectionServiceTests.ValidatesAvatarDimensions),
            ("local scrobble cache adds, removes, caps, and tolerates corrupt files", LocalScrobbleCacheServiceTests.ManagesHistory),
            ("media timeline projects source anchors and reconciles stale seeks", PlaybackTimelineStabilizerTests.StabilizesSourceUpdatesAndSeeks),
            ("lyrics service exact-matches, searches, ranks, parses, and caches results", LyricsServiceTests.FetchesExactOrBestLyricsAndCaches),
            ("lyrics service normalizes Apple Music media-session metadata", LyricsServiceTests.NormalizesAppleMusicMetadata),
            ("animated artwork parses playlists, prefers H.264, and keys by album", AnimatedArtworkServiceTests.ParsesPlaylistsAndKeys),
            ("Last.fm service validates, fetches, scrobbles, signs, and caches", LastFmServiceTests.UsesLastFmApiSafely),
            ("models expose expected defaults and computed properties", ModelTests.DefaultsAndComputedPropertiesAreStable),
            ("artwork tint clamps colors and extracts usable dominant tints", ArtworkTintTests.ColorHelpersAreStable),
            ("CD artwork compositor masks and blends the Photoshop layer stack", CdArtworkComposerTests.ComposesMaskedLayerStack),
            ("artwork loader validates decoded image content instead of extensions", ImageArtworkLoaderTests.ValidatesDecodedImageContent),
            ("MusicBrainz maps the best recording, release, cover, and medium artwork", MusicBrainzServiceTests.MapsRecordingAndArtworkResponses),
            ("MusicBrainz maps fast track information with stable entity IDs", MusicBrainzServiceTests.MapsTrackInformation),
            ("MusicBrainz rejects mismatched titles and checks lower-ranked candidates", MusicBrainzServiceTests.ChoosesConfidentRecordingCandidate),
            ("MusicBrainz maps artist details by MBID", MusicBrainzServiceTests.MapsArtistInformation),
            ("artist artwork resolves trusted Commons photos and caches them", ArtistArtworkServiceTests.ResolvesTrustedCommonsArtwork),
            ("artist artwork rejects unsafe URLs and invalid responses", ArtistArtworkServiceTests.RejectsUnsafeAndInvalidResponses),
            ("MusicBrainz maps release details, tracks, labels, genres, and cover art", MusicBrainzServiceTests.MapsAlbumInformation),
            ("MusicBrainz retries transient API and transport failures", MusicBrainzServiceTests.RetriesTransientApiFailures),
            ("music information failures keep accurate retry states", MusicBrainzServiceTests.ClassifiesLookupFailures),
            ("MusicBrainz retries transient artwork responses and gives up gracefully", MusicBrainzServiceTests.RetriesTransientArtworkResponses),
            ("MusicBrainz enforces artwork hosts, redirect limits, and size bounds", MusicBrainzServiceTests.EnforcesArtworkBoundaries),
            ("MusicBrainz reports artwork outcomes and records failure diagnostics", MusicBrainzServiceTests.ReportsArtworkOutcomesAndDiagnostics),
            ("artwork diagnostics write a bounded, rotating, failure-tolerant log", ArtworkDiagnosticsTests.WritesAndRotatesBoundedLog),
            ("GitHub update links compare valid release tags safely", GitHubReleaseLinksTests.BuildsSafeCompareUris),
            ("update downloads reject interrupted files and preserve useful failure causes", UpdateDownloadTests.ValidatesInterruptedDownloadsAndFailureMessages),
            ("burn lyric fetches replace definitive results and preserve service failures", BurnLyricsFetchTests.ReplacesDefinitiveResultsAndPreservesFailures),
            ("burn metadata validates bounded fields and creates safe title filenames", BurnMetadataValidationTests.ValidatesFieldsAndSuggestedFileNames),
            ("audio burning reads headers and saves metadata to a separate WAV copy", AudioTagServiceTests.ReadsAndSavesMetadataWithoutTouchingSource)
        };

        var failures = new List<string>();
        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}{Environment.NewLine}{ex}");
                Console.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"{tests.Length} tests passed.");
            return 0;
        }

        Console.Error.WriteLine(string.Join(Environment.NewLine + Environment.NewLine, failures));
        return 1;
    }
}

static class AnimatedArtworkServiceTests
{
    public static void ParsesPlaylistsAndKeys()
    {
        var snapshot = MediaSnapshot.Empty with
        {
            HasSession = true,
            Title = "Sacrifice",
            Artist = " The Weeknd ",
            Album = "Dawn FM"
        };
        Check.Equal("the weeknd|dawn fm", AnimatedArtworkService.CreateArtworkKey(snapshot));
        Check.Equal(string.Empty, AnimatedArtworkService.CreateArtworkKey(snapshot with { Album = " " }));
        Check.Equal(string.Empty, AnimatedArtworkService.CreateArtworkKey(MediaSnapshot.Empty));

        var masterUri = new Uri("https://example.com/artwork/master.m3u8");
        var master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1,CODECS="hvc1.2.20000000.L123.B0",RESOLUTION=768x768
            hevc_768.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2,CODECS="avc1.640020",RESOLUTION=1080x1080
            avc_1080.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3,CODECS="avc1.64001f",RESOLUTION=768x768
            avc_768.m3u8
            """;
        var variant = AnimatedArtworkService.SelectStreamVariantUri(master, masterUri);
        Check.Equal("https://example.com/artwork/avc_768.m3u8", variant!.ToString());

        var hevcOnly = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1,CODECS="hvc1.2.20000000.L123.B0",RESOLUTION=486x486
            hevc_486.m3u8
            """;
        Check.Equal(
            "https://example.com/artwork/hevc_486.m3u8",
            AnimatedArtworkService.SelectStreamVariantUri(hevcOnly, masterUri)!.ToString());
        Check.True(AnimatedArtworkService.SelectStreamVariantUri("#EXTM3U", masterUri) is null);

        var mediaUri = new Uri("https://example.com/artwork/avc_768.m3u8");
        var media = """
            #EXTM3U
            #EXT-X-TARGETDURATION:3
            #EXT-X-MAP:URI="segment-.mp4",BYTERANGE="897@0"
            #EXTINF:3.00000,
            #EXT-X-BYTERANGE:100@897
            segment-.mp4
            #EXTINF:3.00000,
            #EXT-X-BYTERANGE:100@997
            segment-.mp4
            """;
        var file = AnimatedArtworkService.ResolveSegmentFileUri(media, mediaUri);
        Check.Equal("https://example.com/artwork/segment-.mp4", file!.ToString());

        var multiFile = """
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:3.00000,
            other-segment.mp4
            """;
        Check.True(AnimatedArtworkService.ResolveSegmentFileUri(multiFile, mediaUri) is null);

        var noMap = """
            #EXTM3U
            #EXTINF:3.00000,
            segment0.ts
            """;
        Check.True(AnimatedArtworkService.ResolveSegmentFileUri(noMap, mediaUri) is null);
    }
}

static class DesktopActivationServiceTests
{
    public static void ValidatesProtocolRoute()
    {
        var scheme = DesktopActivationService.ProtocolScheme;
#if APP_ENVIRONMENT_DEVELOPMENT
        Check.Equal("Development", Mystral.Configuration.AppMetadata.EnvironmentName);
        Check.Equal("mystral-dev", scheme);
        Check.True(DesktopActivationService.CanSelfRegisterProtocol);
#else
        Check.Equal("Production", Mystral.Configuration.AppMetadata.EnvironmentName);
        Check.Equal("mystral", scheme);
        Check.False(DesktopActivationService.CanSelfRegisterProtocol);
#endif
        Check.Equal(
            "\"C:\\Program Files\\Mystral\\Mystral.exe\" \"%1\"",
            DesktopActivationService.BuildProtocolOpenCommand(
                "C:\\Program Files\\Mystral\\Mystral.exe",
                null));
        Check.Equal(
            "\"C:\\Program Files\\dotnet\\dotnet.exe\" exec \"C:\\Dev Builds\\Mystral.dll\" \"%1\"",
            DesktopActivationService.BuildProtocolOpenCommand(
                "C:\\Program Files\\dotnet\\dotnet.exe",
                "C:\\Dev Builds\\Mystral.dll"));
        Check.Null(DesktopActivationService.BuildProtocolOpenCommand(
            "C:\\Program Files\\dotnet\\dotnet.exe",
            null));
        Check.True(DesktopActivationService.IsProtocolRegistrationRequest(
            new[] { DesktopActivationService.RegisterProtocolArgument }));
        Check.True(DesktopActivationService.IsProtocolRegistrationRequest(
            new[] { "--REGISTER-PROTOCOL" }));
        Check.False(DesktopActivationService.IsProtocolRegistrationRequest(Array.Empty<string>()));
        Check.False(DesktopActivationService.IsProtocolRegistrationRequest(
            new[] { DesktopActivationService.RegisterProtocolArgument, "unexpected" }));
        Check.True(DesktopActivationService.IsSocialSettingsActivation($"{scheme}://settings/social"));
        Check.True(DesktopActivationService.IsSocialSettingsActivation($"{scheme}:///settings/social"));
        Check.False(DesktopActivationService.IsSocialSettingsActivation("activate"));
        Check.False(DesktopActivationService.IsSocialSettingsActivation("https://chat.ponkis.xyz/settings/social"));
        Check.False(DesktopActivationService.IsSocialSettingsActivation($"{scheme}://settings/social?token=never-accept-secrets"));
        Check.False(DesktopActivationService.IsSocialSettingsActivation($"{scheme}://settings/social/extra"));
        Check.False(DesktopActivationService.IsSocialSettingsActivation(
            scheme == "mystral" ? "mystral-dev://settings/social" : "mystral://settings/social"));
        var localGlobe = new Uri("http://localhost:3000/");
        Check.True(Mystral.Configuration.AppMetadata.IsTrustedGlobeAvatarUri(
            new Uri("http://localhost:3000/avatars/user.png"),
            localGlobe));
        Check.False(Mystral.Configuration.AppMetadata.IsTrustedGlobeAvatarUri(
            new Uri("https://untrusted.example/avatar.png"),
            localGlobe));
        Check.False(Mystral.Configuration.AppMetadata.IsTrustedGlobeAvatarUri(
            new Uri("http://user:password@localhost:3000/avatar.png"),
            localGlobe));

        var avatarCdn = Mystral.Configuration.AppMetadata.GlobeAvatarCdnBaseUri;
        Check.NotNull(avatarCdn);
        var cdnAvatar = new Uri(avatarCdn!, "profiles/342_avatar.jpg");
        Check.True(Mystral.Configuration.AppMetadata.IsTrustedGlobeAvatarUri(
            cdnAvatar,
            localGlobe));
        var freshAvatar = Mystral.Views.SettingsWindow.CreateFreshAvatarRequestUri(cdnAvatar);
        Check.Equal(cdnAvatar.GetLeftPart(UriPartial.Path), freshAvatar.GetLeftPart(UriPartial.Path));
        Check.Contains("mystral_refresh=", freshAvatar.Query);
        var signedAvatar = new Uri(cdnAvatar.AbsoluteUri + "?signature=keep-me");
        Check.Equal(
            signedAvatar,
            Mystral.Views.SettingsWindow.CreateFreshAvatarRequestUri(signedAvatar));

        var socialActivation = $"{scheme}://settings/social";
        Check.Equal(
            socialActivation,
            DesktopActivationService.PreferActivation(null, socialActivation));
        Check.Equal(
            socialActivation,
            DesktopActivationService.PreferActivation(socialActivation, DesktopActivationService.ActivateMessage));
        Check.Equal(
            socialActivation,
            DesktopActivationService.PreferActivation(DesktopActivationService.ActivateMessage, socialActivation));
    }
}

static class LrcParserTests
{
    public static void ParseCoversImportantCases()
    {
        Check.Empty(LrcParser.Parse(null));
        Check.Empty(LrcParser.Parse(" \r\n "));

        var lines = LrcParser.Parse("""
            [00:10.50]Second
            [ar:metadata]
            [00:01.00][00:02.25] First 
            [bad]ignored
            [00:02.25]
            [00:02.25]Alpha
            [01:00]Minute
            """);

        Check.Equal(5, lines.Count);
        Check.Equal(TimeSpan.FromSeconds(1), lines[0].Time);
        Check.Equal("First", lines[0].Text);
        Check.Equal(TimeSpan.FromSeconds(2.25), lines[1].Time);
        Check.Equal("Alpha", lines[1].Text);
        Check.Equal(TimeSpan.FromSeconds(2.25), lines[2].Time);
        Check.Equal("First", lines[2].Text);
        Check.Equal(TimeSpan.FromSeconds(10.5), lines[3].Time);
        Check.Equal(TimeSpan.FromMinutes(1), lines[4].Time);
    }
}

static class LastFmMetadataCleanerTests
{
    public static void CleansAndClassifiesTracks()
    {
        Check.Equal("Song", LastFmMetadataCleaner.CleanTrackName("  Song   (Official Music Video) - YouTube "));
        Check.Equal("Album", LastFmMetadataCleaner.CleanAlbumName(" Album [HD] "));
        Check.Equal("Artist", LastFmMetadataCleaner.CleanArtistName("Artist - Album", "Album"));
        Check.Equal("Artist", LastFmMetadataCleaner.CleanArtistName("Artist -- Album"));

        var split = LastFmMetadataCleaner.CreateQuery(Snapshot(title: "Artist - Song (Official Audio)", artist: "", album: " Album "));
        Check.Equal("Artist", split.ArtistName);
        Check.Equal("Song", split.TrackName);
        Check.Equal("Album", split.AlbumName);

        Check.False(LastFmMetadataCleaner.IsLikelySong(MediaSnapshot.Empty, split, out var reason));
        Check.Equal("idle", reason);

        Check.False(LastFmMetadataCleaner.IsLikelySong(Snapshot(title: "Song", artist: "", duration: TimeSpan.FromMinutes(3)), new("", "", ""), out reason));
        Check.Equal("missing artist or title", reason);

        Check.False(LastFmMetadataCleaner.IsLikelySong(Snapshot(duration: TimeSpan.FromSeconds(29)), new("Song", "Artist", ""), out reason));
        Check.Equal("too short", reason);

        Check.False(LastFmMetadataCleaner.IsLikelySong(Snapshot(title: "Advertisement"), new("Advertisement", "Station", ""), out reason));
        Check.Equal("not a song", reason);

        Check.True(LastFmMetadataCleaner.IsLikelySong(Snapshot(), new("Song", "Artist", "Album"), out reason));
        Check.Equal("", reason);
    }
}

static class AppSettingsServiceTests
{
    public static void ProtectsMigratesNormalizesAndFallsBack()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Path, "settings.json");
        var credentialStore = new MemorySecureCredentialStore();
        var service = new AppSettingsService(path, credentialStore);
        var changed = 0;
        service.SettingsChanged += (_, _) => changed++;

        service.Save(new AppSettings
        {
            LastFm = new LastFmCredentials
            {
                Enabled = true,
                ApiKey = " key ",
                ApiSecret = " secret ",
                Username = " user ",
                Password = " pass ",
                ScrobblingEnabled = true
            },
            Behavior = new BehaviorSettings
            {
                AlwaysOnTop = false,
                BurnLyricsProvider = BurnLyricsProvider.Lrclib
            },
            Appearance = new AppearanceSettings
            {
                PlayerThemeColor = "  #a1b2c3  "
            },
            Social = new SocialSettings
            {
                IsAccountLinked = true,
                AutomaticallyShareBurns = true
            }
        });

        Check.Equal(1, changed);
        Check.Equal("key", service.Settings.LastFm.ApiKey);
        Check.Equal("secret", service.Settings.LastFm.ApiSecret);
        Check.Equal("user", service.Settings.LastFm.Username);
        Check.Equal(" pass ", service.Settings.LastFm.Password);
        Check.False(service.Settings.Behavior.AlwaysOnTop);
        Check.Equal(BurnLyricsProvider.Lrclib, service.Settings.Behavior.BurnLyricsProvider);
        Check.Equal("#A1B2C3", service.Settings.Appearance.PlayerThemeColor);
        Check.True(service.Settings.Social.IsAccountLinked);
        Check.True(service.Settings.Social.AutomaticallyShareBurns);

        var persistedJson = File.ReadAllText(path);
        Check.False(persistedJson.Contains("ApiKey", StringComparison.Ordinal));
        Check.False(persistedJson.Contains("ApiSecret", StringComparison.Ordinal));
        Check.False(persistedJson.Contains("Username", StringComparison.Ordinal));
        Check.False(persistedJson.Contains("Password", StringComparison.Ordinal));
        Check.False(persistedJson.Contains("secret", StringComparison.Ordinal));
        Check.False(persistedJson.Contains(" pass ", StringComparison.Ordinal));

        var reloaded = new AppSettingsService(path, credentialStore);
        Check.True(reloaded.Settings.LastFm.IsConfigured);
        Check.False(reloaded.Settings.Behavior.AlwaysOnTop);
        Check.Equal(BurnLyricsProvider.Lrclib, reloaded.Settings.Behavior.BurnLyricsProvider);
        Check.Equal("#A1B2C3", reloaded.Settings.Appearance.PlayerThemeColor);
        Check.False(reloaded.Settings.Social.IsAccountLinked);
        Check.True(reloaded.Settings.Social.AutomaticallyShareBurns);

        reloaded.SetGlobeConnectionState(isLinked: false);
        Check.False(reloaded.Settings.Social.AutomaticallyShareBurns);

        var nullPath = Path.Combine(temp.Path, "null-settings.json");
        File.WriteAllText(
            nullPath,
            """{"LastFm":null,"Behavior":null,"Appearance":null,"Social":null}""");
        var nullNested = new AppSettingsService(nullPath, new MemorySecureCredentialStore());
        Check.NotNull(nullNested.Settings.LastFm);
        Check.NotNull(nullNested.Settings.Behavior);
        Check.NotNull(nullNested.Settings.Appearance);
        Check.Equal(string.Empty, nullNested.Settings.Appearance.PlayerThemeColor);
        Check.NotNull(nullNested.Settings.Social);
        Check.Equal(
            BurnLyricsProvider.MusicBrainzAssisted,
            nullNested.Settings.Behavior.BurnLyricsProvider);

        var invalidProviderPath = Path.Combine(temp.Path, "invalid-provider-settings.json");
        File.WriteAllText(
            invalidProviderPath,
            """{"Behavior":{"BurnLyricsProvider":999},"Appearance":{"PlayerThemeColor":"#12345678"}}""");
        var invalidProvider = new AppSettingsService(
            invalidProviderPath,
            new MemorySecureCredentialStore());
        Check.Equal(
            BurnLyricsProvider.MusicBrainzAssisted,
            invalidProvider.Settings.Behavior.BurnLyricsProvider);
        Check.Equal(string.Empty, invalidProvider.Settings.Appearance.PlayerThemeColor);

        var legacyPath = Path.Combine(temp.Path, "legacy-settings.json");
        var legacyStore = new MemorySecureCredentialStore();
        File.WriteAllText(
            legacyPath,
            """
            {
              "LastFm": {
                "Enabled": true,
                "ApiKey": "legacy-key",
                "ApiSecret": "legacy-secret",
                "Username": "legacy-user",
                "Password": "legacy-password",
                "ScrobblingEnabled": true
              },
              "Social": { "AutomaticallyShareBurns": false }
            }
            """);
        var migrated = new AppSettingsService(legacyPath, legacyStore);
        Check.Equal("legacy-key", migrated.Settings.LastFm.ApiKey);
        Check.Equal("legacy-secret", migrated.Settings.LastFm.ApiSecret);
        Check.Equal("legacy-user", migrated.Settings.LastFm.Username);
        Check.Equal("legacy-password", migrated.Settings.LastFm.Password);
        var migratedJson = File.ReadAllText(legacyPath);
        Check.False(migratedJson.Contains("legacy-key", StringComparison.Ordinal));
        Check.False(migratedJson.Contains("legacy-secret", StringComparison.Ordinal));
        Check.False(migratedJson.Contains("legacy-user", StringComparison.Ordinal));
        Check.False(migratedJson.Contains("legacy-password", StringComparison.Ordinal));
        Check.False(migratedJson.Contains("ApiSecret", StringComparison.Ordinal));
        var migratedReload = new AppSettingsService(legacyPath, legacyStore);
        Check.True(migratedReload.Settings.LastFm.IsConfigured);

        var partialMigrationPath = Path.Combine(temp.Path, "partial-migration-settings.json");
        var partialMigrationStore = new MemorySecureCredentialStore();
        File.WriteAllText(
            partialMigrationPath,
            """
            {
              "LastFm": {
                "Enabled": true,
                "ApiKey": "stale-plaintext-key",
                "ApiSecret": "stale-plaintext-secret",
                "Username": "stale-plaintext-user",
                "Password": "stale-plaintext-password",
                "ScrobblingEnabled": true
              }
            }
            """);
        partialMigrationStore.Write(
            "lastfm.credentials.v1",
            """{"ApiKey":"protected-key","ApiSecret":"protected-secret","Username":"protected-user","Password":"protected-password"}""");
        var partialMigration = new AppSettingsService(partialMigrationPath, partialMigrationStore);
        Check.Equal("protected-key", partialMigration.Settings.LastFm.ApiKey);
        Check.Equal("protected-secret", partialMigration.Settings.LastFm.ApiSecret);
        Check.Equal("protected-user", partialMigration.Settings.LastFm.Username);
        Check.Equal("protected-password", partialMigration.Settings.LastFm.Password);
        var sanitizedPartialMigrationJson = File.ReadAllText(partialMigrationPath);
        Check.False(sanitizedPartialMigrationJson.Contains("stale-plaintext", StringComparison.Ordinal));
        Check.False(sanitizedPartialMigrationJson.Contains("ApiSecret", StringComparison.Ordinal));

        var corruptPath = Path.Combine(temp.Path, "corrupt-settings.json");
        File.WriteAllText(corruptPath, "{ nope");
        var corrupt = new AppSettingsService(corruptPath, new MemorySecureCredentialStore());
        Check.False(corrupt.Settings.LastFm.IsConfigured);
        Check.True(corrupt.Settings.Behavior.AlwaysOnTop);
        Check.False(corrupt.Settings.Social.IsAccountLinked);
    }
}

static class DpapiCredentialStoreTests
{
    public static void ProtectsRoundTripsAndDeletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDir.Create();
        var directory = Path.Combine(temp.Path, "credentials");
        const string key = "test-token";
        const string secret = "never-write-this-token-in-plaintext";
        var store = new DpapiCredentialStore(directory);
        store.Write(key, secret);

        var files = Directory.GetFiles(directory, "*.credential");
        Check.Equal(1, files.Length);
        var bytesAtRest = File.ReadAllBytes(files[0]);
        Check.False(Encoding.UTF8.GetString(bytesAtRest).Contains(secret, StringComparison.Ordinal));
        Check.Equal(secret, store.Read(key));
        Check.Equal(secret, new DpapiCredentialStore(directory).Read(key));

        store.Delete(key);
        Check.Null(store.Read(key));
    }
}

static class GlobeConnectionServiceTests
{
    public static void LinksSharesAndUnlinks()
    {
        using var temp = TempDir.Create();
        var secureStore = new MemorySecureCredentialStore();
        var settings = new AppSettingsService(Path.Combine(temp.Path, "settings.json"), secureStore);
        settings.Save(new AppSettings
        {
            Social = new SocialSettings
            {
                IsAccountLinked = true,
                AutomaticallyShareBurns = true
            }
        });

        var claimCalls = 0;
        var acknowledgeCalls = 0;
        var revokeCalls = 0;
        var burnCalls = 0;
        var serverCdCount = 0;
        var burnIds = new List<string>();
        string? codeChallenge = null;
        string? codeVerifier = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/mystral/link/claim")
            {
                Check.Equal(HttpMethod.Post, request.Method);
                claimCalls++;
                using var claimBody = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var claimRoot = claimBody.RootElement;
                var linkCode = claimRoot.GetProperty("link_code").GetString();
                Check.True(!string.IsNullOrWhiteSpace(linkCode));
                if (claimCalls == 1)
                {
                    codeChallenge = claimRoot.GetProperty("code_challenge").GetString();
                    Check.True(!string.IsNullOrWhiteSpace(codeChallenge));
                    Check.Equal(43, codeChallenge!.Length);
                    Check.False(codeChallenge.Contains('='));
                    Check.False(claimRoot.TryGetProperty("code_verifier", out _));
                    return new HttpResponseMessage(HttpStatusCode.Accepted)
                    {
                        Content = new StringContent("{\"pending\":true}", Encoding.UTF8, "application/json")
                    };
                }

                codeVerifier = claimRoot.GetProperty("code_verifier").GetString();
                Check.True(!string.IsNullOrWhiteSpace(codeVerifier));
                Check.Equal(43, codeVerifier!.Length);
                Check.False(codeVerifier.Contains('='));
                Check.False(claimRoot.TryGetProperty("code_challenge", out _));
                var expectedChallenge = Convert.ToBase64String(
                        System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                Check.Equal(expectedChallenge, codeChallenge);

                return Json(new
                {
                    linked = true,
                    token = "globe-secret-token",
                    username = "listener",
                    name = "",
                    avatar_url = "/avatars/listener.png"
                });
            }

            Check.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Check.Equal("globe-secret-token", request.Headers.Authorization?.Parameter);
            if (path == "/api/mystral/link/ack")
            {
                Check.Equal(HttpMethod.Post, request.Method);
                acknowledgeCalls++;
                Check.Equal(
                    "globe-secret-token",
                    secureStore.Read(GlobeConnectionService.TokenCredentialKey));
                return Json(new { linked = true, acknowledged = true });
            }

            if (path == "/api/mystral/link/status")
            {
                Check.Equal(HttpMethod.Get, request.Method);
                Check.True(request.Headers.CacheControl?.NoCache == true);
                Check.True(request.Headers.CacheControl?.NoStore == true);
                Check.True(request.Headers.Pragma.Any(value =>
                    string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase)));
                return Json(new
                {
                    linked = true,
                    username = "listener",
                    name = "Changed Name",
                    avatar_url = "/avatars/updated.png",
                    cd_count = serverCdCount
                });
            }

            if (path == "/api/mystral/burns")
            {
                Check.Equal(HttpMethod.Post, request.Method);
                burnCalls++;
                var idempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
                burnIds.Add(idempotencyKey);
                using var burnBody = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var root = burnBody.RootElement;
                Check.Equal(idempotencyKey, root.GetProperty("client_burn_id").GetString());
                Check.Equal("Album", root.GetProperty("album").GetString());
                Check.Equal("Artist", root.GetProperty("artist").GetString());
                Check.Equal(12, root.GetProperty("track_count").GetInt32());
                if (burnCalls <= 3)
                {
                    Check.Contains("data:image/png;base64,", root.GetProperty("cover").GetString()!);
                }
                else
                {
                    Check.False(root.TryGetProperty("cover", out _));
                }

                if (burnCalls == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new StringContent(
                            "{\"error\":\"too_many_requests\",\"message\":\"internal limiter bucket 7 overflowed\"}",
                            Encoding.UTF8,
                            "application/json")
                    };
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                    return limited;
                }

                var created = burnCalls is not 3 and not 7;
                if (created)
                {
                    serverCdCount++;
                }

                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        burnCalls == 3
                            ? "{\"shared\":true,\"created\":false,\"burn\":{\"id\":41,\"postId\":82},\"post\":{\"id\":82}}"
                            : burnCalls == 7
                                ? "{\"shared\":true,\"burn\":{\"id\":41,\"postId\":82},\"post\":{\"id\":82}}"
                                : "{\"shared\":true,\"created\":true,\"message\":\"inserted database row 42\",\"burn\":{\"id\":41,\"postId\":82},\"post\":{\"id\":82}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (path == "/api/mystral/link/revoke")
            {
                Check.Equal(HttpMethod.Post, request.Method);
                revokeCalls++;
                if (revokeCalls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            "{\"error\":\"route_not_found\"}",
                            Encoding.UTF8,
                            "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            throw new InvalidOperationException("Unexpected Globe request: " + request.RequestUri);
        });

        using var httpClient = new HttpClient(handler);
        using var apiClient = new GlobeApiClient(httpClient, new Uri("http://localhost:3000/"));
        using var connection = new GlobeConnectionService(
            apiClient,
            secureStore,
            settings,
            new GlobeConnectionOptions
            {
                LinkPollInterval = TimeSpan.FromMilliseconds(1),
                LinkTimeout = TimeSpan.FromSeconds(1),
                StatusPollInterval = TimeSpan.FromHours(1)
            });

        Uri? approvalUri = null;
        var linkedProfile = connection.LinkAsync(uri =>
        {
            Check.Equal(1, claimCalls);
            approvalUri = uri;
        }).GetAwaiter().GetResult();
        Check.NotNull(approvalUri);
        Check.Equal("/settings/connections/mystral/approve", approvalUri!.AbsolutePath);
        Check.Contains("code=", approvalUri.Query);
        Check.Equal(2, claimCalls);
        Check.Equal(1, acknowledgeCalls);
        Check.NotNull(codeVerifier);
        Check.Equal("listener", linkedProfile.DisplayName);
        Check.Equal("@listener", linkedProfile.DisplayUsername);
        Check.True(connection.State.IsLinked);
        Check.Equal("globe-secret-token", secureStore.Read(GlobeConnectionService.TokenCredentialKey));
        Check.Null(secureStore.Read(GlobeConnectionService.LinkAckPendingCredentialKey));

        var secondApprovalOpened = false;
        InvalidOperationException? alreadyLinked = null;
        try
        {
            connection.LinkAsync(_ => secondApprovalOpened = true).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            alreadyLinked = ex;
        }
        Check.NotNull(alreadyLinked);
        Check.Equal(
            "Unlink your current globe account before linking another one.",
            alreadyLinked!.Message);
        Check.False(secondApprovalOpened);
        Check.Equal(2, claimCalls);
        Check.True(connection.State.IsLinked);

        Check.True(connection.ValidateAsync().GetAwaiter().GetResult());
        Check.Equal("Changed Name", connection.State.Profile!.DisplayName);
        Check.Equal("http://localhost:3000/avatars/updated.png", connection.State.Profile.AvatarUrl);

        var request = new GlobeBurnShareRequest(
            " Album ",
            " Artist ",
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.FromHours(-6)),
            12,
            [0x89, 0x50, 0x4E, 0x47, 1, 2, 3],
            "stable-burn-id");
        GlobeApiException? rateLimit = null;
        try
        {
            _ = connection.ShareBurnAsync(request).GetAwaiter().GetResult();
        }
        catch (GlobeApiException ex)
        {
            rateLimit = ex;
        }

        Check.NotNull(rateLimit);
        Check.Equal(HttpStatusCode.TooManyRequests, rateLimit!.StatusCode);
        Check.Equal("too_many_requests", rateLimit.ErrorCode);
        Check.Equal(
            "globe is receiving too many requests. Please wait a moment and try again.",
            rateLimit.Message);
        Check.Equal(TimeSpan.FromSeconds(7), rateLimit.RetryAfter!.Value);
        Check.True(connection.State.IsLinked);

        var shared = connection.ShareBurnAsync(request).GetAwaiter().GetResult();
        Check.Equal("82", shared.PostId);
        Check.Equal("41", shared.CollectionEntryId);
        Check.Equal("The burned CD was shared to globe.", shared.Message);
        Check.Equal(2, burnIds.Count);
        Check.Equal(burnIds[0], burnIds[1]);
        Check.Equal(1, connection.State.Profile!.CdCount);

        var duplicate = connection.ShareBurnAsync(request).GetAwaiter().GetResult();
        Check.False(duplicate.Created);
        Check.Equal(1, connection.State.Profile!.CdCount);
        Check.Equal(3, burnIds.Count);
        Check.Equal(burnIds[0], burnIds[2]);

        var oversizedCover = new byte[(5 * 1024 * 1024) + 1];
        oversizedCover[0] = 0x89;
        oversizedCover[1] = 0x50;
        oversizedCover[2] = 0x4E;
        oversizedCover[3] = 0x47;
        _ = connection.ShareBurnAsync(new GlobeBurnShareRequest(
            "Album",
            "Artist",
            DateTimeOffset.UtcNow,
            12,
            oversizedCover,
            "oversized-cover-burn")).GetAwaiter().GetResult();

        Task.WhenAll(
            connection.ShareBurnAsync(new GlobeBurnShareRequest(
                "Album",
                "Artist",
                DateTimeOffset.UtcNow,
                12,
                burnId: "concurrent-burn-a")),
            connection.ShareBurnAsync(new GlobeBurnShareRequest(
                "Album",
                "Artist",
                DateTimeOffset.UtcNow,
                12,
                burnId: "concurrent-burn-b"))).GetAwaiter().GetResult();
        Check.Equal(4, connection.State.Profile!.CdCount);

        var unspecifiedCreation = connection.ShareBurnAsync(new GlobeBurnShareRequest(
            "Album",
            "Artist",
            DateTimeOffset.UtcNow,
            12,
            burnId: "unspecified-created-burn")).GetAwaiter().GetResult();
        Check.False(unspecifiedCreation.Created);
        Check.Equal(4, connection.State.Profile!.CdCount);

        Check.Throws<GlobeApiException>(() => connection.UnlinkAsync().GetAwaiter().GetResult());
        Check.True(connection.State.IsLinked);
        Check.True(connection.HasStoredToken);
        Check.Equal("globe-secret-token", secureStore.Read(GlobeConnectionService.TokenCredentialKey));

        connection.UnlinkAsync().GetAwaiter().GetResult();
        Check.Equal(2, revokeCalls);
        Check.False(connection.State.IsLinked);
        Check.False(connection.HasStoredToken);
        Check.Null(secureStore.Read(GlobeConnectionService.TokenCredentialKey));
        Check.False(settings.Settings.Social.AutomaticallyShareBurns);
    }

    public static void DetectsRevocationOnce()
    {
        using var temp = TempDir.Create();
        var secureStore = new MemorySecureCredentialStore();
        var settings = new AppSettingsService(Path.Combine(temp.Path, "settings.json"), secureStore);
        settings.Save(new AppSettings
        {
            Social = new SocialSettings
            {
                IsAccountLinked = true,
                AutomaticallyShareBurns = true
            }
        });
        secureStore.Write(GlobeConnectionService.TokenCredentialKey, "revoked-token");
        var statusCalls = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal("/api/mystral/link/status", request.RequestUri!.AbsolutePath);
            statusCalls++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"linked\":false,\"error\":\"mystral_link_invalid\",\"message\":\"token hash 123 was deleted from mystral_connections\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var apiClient = new GlobeApiClient(httpClient, new Uri("http://localhost:3000/"));
        using var connection = new GlobeConnectionService(apiClient, secureStore, settings);
        var revocations = 0;
        GlobeLinkRevokedEventArgs? revocation = null;
        connection.LinkRevoked += (_, args) =>
        {
            revocations++;
            revocation = args;
        };

        Check.False(connection.ValidateAsync().GetAwaiter().GetResult());
        Check.False(connection.ValidateAsync().GetAwaiter().GetResult());
        Check.Equal(1, statusCalls);
        Check.Equal(1, revocations);
        Check.Equal(GlobeLinkRevocationSource.StatusCheck, revocation!.Source);
        Check.Equal("Your globe account is no longer linked.", revocation.Message);
        Check.Null(secureStore.Read(GlobeConnectionService.TokenCredentialKey));
        Check.False(settings.Settings.Social.AutomaticallyShareBurns);
        Check.Equal(GlobeConnectionStatus.Unlinked, connection.State.Status);
    }

    public static void CachesProfileAndSurvivesOutages()
    {
        using var temp = TempDir.Create();
        var secureStore = new MemorySecureCredentialStore();
        var settings = new AppSettingsService(Path.Combine(temp.Path, "settings.json"), secureStore);
        settings.Save(new AppSettings
        {
            Social = new SocialSettings { AutomaticallyShareBurns = true }
        });
        secureStore.Write(GlobeConnectionService.TokenCredentialKey, "cached-token");

        var mode = 0;
        var statusCalls = 0;
        var burnCalls = 0;
        var retryBurnIds = new List<string>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            Check.Equal("cached-token", request.Headers.Authorization?.Parameter);
            if (path == "/api/mystral/burns")
            {
                Check.Equal(HttpMethod.Post, request.Method);
                Check.Equal(2, mode);
                burnCalls++;
                retryBurnIds.Add(request.Headers.GetValues("Idempotency-Key").Single());
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        "{\"shared\":true,\"created\":true,\"burn\":{\"id\":91},\"post\":{\"id\":92}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            Check.Equal("/api/mystral/link/status", path);
            statusCalls++;
            if (mode == 1)
            {
                throw new HttpRequestException("server offline");
            }

            return Json(new
            {
                linked = true,
                username = "listener",
                name = mode == 2 ? "Updated Listener" : "Cached Listener",
                avatar_url = mode == 2 ? "/avatars/new.png" : "/avatars/cached.png",
                cd_count = mode == 2 ? 8 : 7
            });
        });

        using var httpClient = new HttpClient(handler);
        using var apiClient = new GlobeApiClient(httpClient, new Uri("http://localhost:3000/"));
        using (var first = new GlobeConnectionService(apiClient, secureStore, settings))
        {
            Check.True(first.ValidateAsync().GetAwaiter().GetResult());
            first.CacheAvatar(first.State.Profile!, [1, 2, 3, 4]);
        }

        using var connection = new GlobeConnectionService(apiClient, secureStore, settings);
        Check.True(connection.State.IsLinked);
        Check.False(connection.State.CanShare);
        Check.Equal("Cached Listener", connection.State.Profile!.DisplayName);
        Check.Sequence(new byte[] { 1, 2, 3, 4 }, connection.GetCachedAvatar(connection.State.Profile.AvatarUrl)!);

        var outageWarnings = 0;
        connection.ServerUnavailable += (_, _) => outageWarnings++;
        mode = 1;
        Check.False(connection.ValidateAsync().GetAwaiter().GetResult());
        Check.Equal(GlobeConnectionStatus.Offline, connection.State.Status);
        Check.True(connection.State.IsLinked);
        Check.False(connection.State.CanShare);
        Check.True(connection.HasStoredToken);
        Check.True(settings.Settings.Social.AutomaticallyShareBurns);
        Check.Equal(1, outageWarnings);
        Check.False(connection.ValidateAsync().GetAwaiter().GetResult());
        Check.Equal(1, outageWarnings);

        mode = 2;
        var statusCallsBeforeRetry = statusCalls;
        var offlineRetry = new GlobeBurnShareRequest(
            "Recovered Album",
            "Recovered Artist",
            DateTimeOffset.UtcNow,
            9,
            burnId: "offline-retry-burn-id");
        var retryResult = connection.ShareBurnAsync(offlineRetry).GetAwaiter().GetResult();
        Check.True(retryResult.Created);
        Check.Equal(statusCallsBeforeRetry + 1, statusCalls);
        Check.Equal(1, burnCalls);
        Check.Sequence(new[] { offlineRetry.BurnId }, retryBurnIds);
        Check.Equal(GlobeConnectionStatus.Linked, connection.State.Status);
        Check.True(connection.State.CanShare);
        Check.Equal("Updated Listener", connection.State.Profile!.DisplayName);
        Check.Equal(9, connection.State.Profile.CdCount);
        Check.Null(connection.GetCachedAvatar(connection.State.Profile.AvatarUrl));

        mode = 1;
        Check.False(connection.ValidateAsync().GetAwaiter().GetResult());
        Check.Equal(2, outageWarnings);

        var noCacheStore = new MemorySecureCredentialStore();
        noCacheStore.Write(GlobeConnectionService.TokenCredentialKey, "cached-token");
        var noCacheSettings = new AppSettingsService(
            Path.Combine(temp.Path, "no-cache-settings.json"),
            noCacheStore);
        noCacheSettings.Save(new AppSettings
        {
            Social = new SocialSettings { AutomaticallyShareBurns = true }
        });
        using var noCacheConnection = new GlobeConnectionService(
            apiClient,
            noCacheStore,
            noCacheSettings);
        Check.False(noCacheConnection.State.IsLinked);
        Check.False(noCacheConnection.ValidateAsync().GetAwaiter().GetResult());
        Check.Equal(GlobeConnectionStatus.Offline, noCacheConnection.State.Status);
        Check.True(noCacheConnection.State.IsLinked);
        Check.True(noCacheConnection.HasStoredToken);
        Check.True(noCacheSettings.Settings.Social.AutomaticallyShareBurns);
    }

    public static void DetectsBrowserCancellation()
    {
        using var temp = TempDir.Create();
        var secureStore = new MemorySecureCredentialStore();
        var settings = new AppSettingsService(Path.Combine(temp.Path, "settings.json"), secureStore);
        var claimCalls = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal("/api/mystral/link/claim", request.RequestUri!.AbsolutePath);
            claimCalls++;
            if (claimCalls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            return new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent(
                    "{\"error\":\"link_code_cancelled\",\"message\":\"this Mystral link request was cancelled\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var apiClient = new GlobeApiClient(httpClient, new Uri("http://localhost:3000/"));
        using var connection = new GlobeConnectionService(
            apiClient,
            secureStore,
            settings,
            new GlobeConnectionOptions
            {
                LinkPollInterval = TimeSpan.FromMilliseconds(1),
                LinkTimeout = TimeSpan.FromSeconds(1)
            });
        Uri? opened = null;
        GlobeLinkCancelledException? cancellation = null;
        try
        {
            connection.LinkAsync(uri => opened = uri).GetAwaiter().GetResult();
        }
        catch (GlobeLinkCancelledException ex)
        {
            cancellation = ex;
        }
        Check.NotNull(cancellation);
        Check.Equal("The globe account link was canceled.", cancellation!.Message);
        Check.NotNull(opened);
        Check.Equal(2, claimCalls);
        Check.Equal(GlobeConnectionStatus.Unlinked, connection.State.Status);
        Check.False(connection.HasStoredToken);
    }

    public static void ReconcilesDuplicateBurnCount()
    {
        using var temp = TempDir.Create();
        var secureStore = new MemorySecureCredentialStore();
        secureStore.Write(GlobeConnectionService.TokenCredentialKey, "duplicate-token");
        var settings = new AppSettingsService(Path.Combine(temp.Path, "settings.json"), secureStore);
        var burnReachedServer = false;
        var statusCalls = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal("duplicate-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath == "/api/mystral/burns")
            {
                burnReachedServer = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"shared\":true,\"created\":false,\"burn\":{\"id\":51,\"postId\":52},\"post\":{\"id\":52}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            Check.Equal("/api/mystral/link/status", request.RequestUri.AbsolutePath);
            statusCalls++;
            return Json(new
            {
                linked = true,
                username = "duplicate-listener",
                name = "Duplicate Listener",
                avatar_url = "/avatars/duplicate.png",
                cd_count = burnReachedServer ? 1 : 0
            });
        });

        using var httpClient = new HttpClient(handler);
        using var apiClient = new GlobeApiClient(httpClient, new Uri("http://localhost:3000/"));
        using var connection = new GlobeConnectionService(apiClient, secureStore, settings);
        Check.True(connection.ValidateAsync().GetAwaiter().GetResult());
        Check.Equal(0, connection.State.Profile!.CdCount);

        var retryRequest = new GlobeBurnShareRequest(
            "Recovered Album",
            "Recovered Artist",
            DateTimeOffset.UtcNow,
            10,
            burnId: "lost-response-burn");
        var duplicate = connection.ShareBurnAsync(retryRequest).GetAwaiter().GetResult();
        Check.False(duplicate.Created);
        Check.Equal(2, statusCalls);
        Check.Equal(1, connection.State.Profile!.CdCount);

        using var falseSuccessHttpClient = new HttpClient(new FakeHttpMessageHandler(_ => Json(new
        {
            shared = false,
            created = false
        })));
        using var falseSuccessApiClient = new GlobeApiClient(
            falseSuccessHttpClient,
            new Uri("http://localhost:3000/"));
        Check.Throws<GlobeApiException>(() =>
            falseSuccessApiClient.ShareBurnAsync("token", retryRequest).GetAwaiter().GetResult());
    }

    public static void RecoversPendingAcknowledgement()
    {
        RunScenario(cancelDuringAcknowledgement: false, statusCanRecoverLostAcknowledgement: false);
        RunScenario(cancelDuringAcknowledgement: true, statusCanRecoverLostAcknowledgement: false);
        RunScenario(cancelDuringAcknowledgement: false, statusCanRecoverLostAcknowledgement: true);

        static void RunScenario(
            bool cancelDuringAcknowledgement,
            bool statusCanRecoverLostAcknowledgement)
        {
            using var temp = TempDir.Create();
            var secureStore = new MemorySecureCredentialStore();
            var settings = new AppSettingsService(Path.Combine(temp.Path, "settings.json"), secureStore);
            using var cancellation = new CancellationTokenSource();
            var claimCalls = 0;
            var acknowledgeCalls = 0;
            var statusCalls = 0;
            var acknowledgementAvailable = false;
            var handler = new FakeHttpMessageHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/mystral/link/claim")
                {
                    claimCalls++;
                    return claimCalls == 1
                        ? new HttpResponseMessage(HttpStatusCode.Accepted)
                        : Json(new
                        {
                            linked = true,
                            token = "pending-ack-token",
                            username = "pending-listener",
                            name = "Pending Listener",
                            avatar_url = "/avatars/pending.png",
                            cd_count = 3
                        });
                }

                Check.Equal("pending-ack-token", request.Headers.Authorization?.Parameter);
                if (path == "/api/mystral/link/ack")
                {
                    acknowledgeCalls++;
                    Check.Equal(
                        "pending-ack-token",
                        secureStore.Read(GlobeConnectionService.TokenCredentialKey));
                    if (!acknowledgementAvailable)
                    {
                        if (cancelDuringAcknowledgement)
                        {
                            cancellation.Cancel();
                            throw new OperationCanceledException(cancellation.Token);
                        }

                        throw new HttpRequestException("ack response was lost");
                    }

                    return Json(new { linked = true, acknowledged = true });
                }

                Check.Equal("/api/mystral/link/status", path);
                statusCalls++;
                if (!acknowledgementAvailable && !statusCanRecoverLostAcknowledgement)
                {
                    throw new HttpRequestException("status response was also lost");
                }

                return Json(new
                {
                    linked = true,
                    username = "pending-listener",
                    name = "Recovered Listener",
                    avatar_url = "/avatars/recovered.png",
                    cd_count = 4
                });
            });

            using var httpClient = new HttpClient(handler);
            using var apiClient = new GlobeApiClient(httpClient, new Uri("http://localhost:3000/"));
            using var connection = new GlobeConnectionService(
                apiClient,
                secureStore,
                settings,
                new GlobeConnectionOptions
                {
                    LinkPollInterval = TimeSpan.FromMilliseconds(1),
                    LinkTimeout = TimeSpan.FromSeconds(1),
                    StatusPollInterval = TimeSpan.FromHours(1)
                });

            if (statusCanRecoverLostAcknowledgement)
            {
                var recoveredDuringLink = connection.LinkAsync(_ => { }).GetAwaiter().GetResult();
                Check.Equal("Recovered Listener", recoveredDuringLink.DisplayName);
                Check.Equal(3, acknowledgeCalls);
                Check.Equal(1, statusCalls);
                Check.True(connection.HasStoredToken);
                Check.Equal(GlobeConnectionStatus.Linked, connection.State.Status);
                Check.True(connection.State.CanShare);
                Check.Null(secureStore.Read(GlobeConnectionService.LinkAckPendingCredentialKey));
                return;
            }

            if (cancelDuringAcknowledgement)
            {
                Check.Throws<OperationCanceledException>(() =>
                    connection.LinkAsync(_ => { }, cancellation.Token).GetAwaiter().GetResult());
                Check.Equal(1, acknowledgeCalls);
                Check.Equal(0, statusCalls);
            }
            else
            {
                Check.Throws<GlobeUnavailableException>(() =>
                    connection.LinkAsync(_ => { }).GetAwaiter().GetResult());
                Check.Equal(3, acknowledgeCalls);
                Check.Equal(1, statusCalls);
            }

            Check.True(connection.HasStoredToken);
            Check.Equal("pending-ack-token", secureStore.Read(GlobeConnectionService.TokenCredentialKey));
            Check.Equal("pending", secureStore.Read(GlobeConnectionService.LinkAckPendingCredentialKey));
            Check.Equal(GlobeConnectionStatus.Offline, connection.State.Status);
            Check.True(connection.State.IsLinked);
            Check.False(connection.State.CanShare);

            acknowledgementAvailable = true;
            Check.True(connection.ValidateAsync().GetAwaiter().GetResult());
            Check.Equal(GlobeConnectionStatus.Linked, connection.State.Status);
            Check.Equal("Recovered Listener", connection.State.Profile!.DisplayName);
            Check.Null(secureStore.Read(GlobeConnectionService.LinkAckPendingCredentialKey));
        }
    }

    public static void TreatsMissingEndpointsAsFailures()
    {
        using var temp = TempDir.Create();

        var claimStore = new MemorySecureCredentialStore();
        var claimSettings = new AppSettingsService(
            Path.Combine(temp.Path, "claim-settings.json"),
            claimStore);
        using (var claimHttpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
                   new HttpResponseMessage(HttpStatusCode.NotFound)
                   {
                       Content = new StringContent(
                           "{\"error\":\"route_not_found\"}",
                           Encoding.UTF8,
                           "application/json")
                   })))
        using (var claimApiClient = new GlobeApiClient(
                   claimHttpClient,
                   new Uri("http://localhost:3000/")))
        using (var claimConnection = new GlobeConnectionService(
                   claimApiClient,
                   claimStore,
                   claimSettings))
        {
            var browserOpened = false;
            Check.Throws<GlobeApiException>(() =>
                claimConnection.LinkAsync(_ => browserOpened = true).GetAwaiter().GetResult());
            Check.False(browserOpened);
            Check.False(claimConnection.HasStoredToken);
            Check.Equal(GlobeConnectionStatus.Unlinked, claimConnection.State.Status);
        }

        var revokeStore = new MemorySecureCredentialStore();
        revokeStore.Write(GlobeConnectionService.TokenCredentialKey, "live-revoke-token");
        var revokeSettings = new AppSettingsService(
            Path.Combine(temp.Path, "revoke-settings.json"),
            revokeStore);
        using var revokeHttpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Check.Equal("/api/mystral/link/revoke", request.RequestUri!.AbsolutePath);
            Check.Equal("live-revoke-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"error\":\"route_not_found\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        using var revokeApiClient = new GlobeApiClient(
            revokeHttpClient,
            new Uri("http://localhost:3000/"));
        using var revokeConnection = new GlobeConnectionService(
            revokeApiClient,
            revokeStore,
            revokeSettings);
        Check.Throws<GlobeApiException>(() =>
            revokeConnection.UnlinkAsync().GetAwaiter().GetResult());
        Check.True(revokeConnection.HasStoredToken);
        Check.Equal("live-revoke-token", revokeStore.Read(GlobeConnectionService.TokenCredentialKey));
    }

    public static void ValidatesAvatarDimensions()
    {
        Check.True(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(256, 256));
        Check.True(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(8192, 4096));
        Check.True(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(20, 1));
        Check.False(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(0, 256));
        Check.False(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(8193, 1));
        Check.False(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(8192, 4097));
        Check.False(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(21, 1));
        Check.False(Mystral.Views.SettingsWindow.AreSocialAvatarDimensionsSafe(int.MaxValue, int.MaxValue));

        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => Json(new
        {
            linked = true,
            username = "safe-user",
            name = "Safe User",
            avatar_url = "https://untrusted.example/private-resource",
            cd_count = 0
        })));
        using var apiClient = new GlobeApiClient(httpClient, new Uri("http://localhost:3000/"));
        var profile = apiClient.GetStatusAsync("safe-token").GetAwaiter().GetResult();
        Check.Equal(string.Empty, profile.AvatarUrl);
    }
}

static class LocalScrobbleCacheServiceTests
{
    public static void ManagesHistory()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Path, "scrobbles.json");
        var service = new LocalScrobbleCacheService(path);
        ScrobbleRecord? raised = null;
        service.ScrobbleAdded += (_, record) => raised = record;

        var older = Record("Old", timestamp: 1);
        var newer = Record("New", timestamp: 2);
        service.AddRecord(older);
        service.AddRecord(newer);

        Check.Same(newer, raised);
        var records = service.LoadAllRecords();
        Check.Sequence(["New", "Old"], records.Select(r => r.Title));

        service.RemoveRecord(older);
        Check.Sequence(["New"], service.LoadAllRecords().Select(r => r.Title));

        service.RemoveRecords([newer, Record("Missing", timestamp: 99)]);
        Check.Empty(service.LoadAllRecords());

        var many = Enumerable.Range(0, 10000).Select(i => Record($"Track {i}", i)).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(many));
        service.AddRecord(Record("Newest", 10001));
        var capped = service.LoadAllRecords();
        Check.Equal(10000, capped.Count);
        Check.Equal("Newest", capped[0].Title);
        Check.Equal("Track 9998", capped[^1].Title);

        File.WriteAllText(path, "not json");
        Check.Empty(service.LoadAllRecords());
        Check.True(service.ClearHistory());
        Check.False(File.Exists(path));
    }
}

static class PlaybackTimelineStabilizerTests
{
    public static void StabilizesSourceUpdatesAndSeeks()
    {
        var start = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var duration = TimeSpan.FromMinutes(3);

        var validAnchor = PlaybackTimelineStabilizer.ResolveSourceAnchorTimestamp(
            start,
            start + TimeSpan.FromSeconds(5),
            duration,
            out var validAnchorIsReliable);
        Check.Equal(start, validAnchor);
        Check.True(validAnchorIsReliable);
        Check.Equal(
            TimeSpan.FromSeconds(45),
            PlaybackTimelineStabilizer.ProjectSourcePosition(
                TimeSpan.FromSeconds(40),
                duration,
                isPlaying: true,
                sourceUpdatedAt: validAnchor,
                observedAt: start + TimeSpan.FromSeconds(5)));

        var fallbackAnchor = PlaybackTimelineStabilizer.ResolveSourceAnchorTimestamp(
            default,
            start,
            duration,
            out var fallbackAnchorIsReliable);
        Check.Equal(start, fallbackAnchor);
        Check.False(fallbackAnchorIsReliable);
        Check.Equal(
            TimeSpan.FromSeconds(40),
            PlaybackTimelineStabilizer.ProjectSourcePosition(
                TimeSpan.FromSeconds(40),
                duration,
                isPlaying: false,
                sourceUpdatedAt: validAnchor,
                observedAt: start + TimeSpan.FromSeconds(5)));

        var stabilizer = new PlaybackTimelineStabilizer();
        Check.Equal(
            TimeSpan.FromSeconds(40),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.FromSeconds(40),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start,
                hasReliableTimelineUpdatedAt: true,
                observedAt: start));
        Check.Equal(TimeSpan.FromSeconds(42), stabilizer.GetPosition(start + TimeSpan.FromSeconds(2)));

        // Re-reading the same source anchor projects forward instead of resetting
        // the display to the old raw position on every GSMTC poll.
        Check.Equal(
            TimeSpan.FromSeconds(42),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.FromSeconds(40),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start,
                hasReliableTimelineUpdatedAt: true,
                observedAt: start + TimeSpan.FromSeconds(2)));

        // A malformed same-track snapshot with no duration must preserve the
        // established timeline instead of resetting the player to 0:00.
        Check.Equal(
            TimeSpan.FromMilliseconds(42500),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.Zero,
                TimeSpan.Zero,
                isPlaying: true,
                timelineUpdatedAt: default,
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromMilliseconds(2500)));
        Check.Equal(duration, stabilizer.Duration);

        // Chrome can publish a default timestamp and a one-off 0 position. Two
        // frozen observations must not rewind a timeline that is still playing.
        Check.Equal(
            TimeSpan.FromSeconds(43),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.Zero,
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(3),
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(3)));
        Check.Equal(
            TimeSpan.FromSeconds(45),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.Zero,
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(5),
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(5)));

        // Even an advancing reset stays filtered when Chrome supplied no reliable
        // anchor timestamp.
        Check.Equal(
            TimeSpan.FromMilliseconds(46500),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.FromMilliseconds(1500),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromMilliseconds(6500),
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromMilliseconds(6500)));

        // A deliberate external backwards seek is accepted only after two
        // coherent observations backed by a credible source anchor.
        Check.Equal(
            TimeSpan.FromSeconds(47),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.FromSeconds(2),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(7),
                hasReliableTimelineUpdatedAt: true,
                observedAt: start + TimeSpan.FromSeconds(7)));
        Check.Equal(
            TimeSpan.FromSeconds(3),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.FromSeconds(2),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(7),
                hasReliableTimelineUpdatedAt: true,
                observedAt: start + TimeSpan.FromSeconds(8)));

        // Mystral's own seek is immediate, while the stale response emitted by
        // the source right after the command remains fenced out.
        Check.Equal(
            TimeSpan.FromSeconds(80),
            stabilizer.BeginSeek(
                "spotify|song",
                TimeSpan.FromSeconds(80),
                duration,
                isPlaying: true,
                startedAt: start + TimeSpan.FromSeconds(9)));
        Check.Equal(
            TimeSpan.FromMilliseconds(80100),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.FromSeconds(2),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromMilliseconds(9100),
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromMilliseconds(9100)));
        Check.Equal(
            TimeSpan.FromSeconds(81),
            stabilizer.Observe(
                "spotify|song",
                hasSession: true,
                TimeSpan.FromSeconds(81),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(10),
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(10)));

        // A track change is an explicit discontinuity and resets immediately.
        Check.Equal(
            TimeSpan.Zero,
            stabilizer.Observe(
                "chrome|next-song",
                hasSession: true,
                TimeSpan.Zero,
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(11),
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(11)));

        Check.True(PlaybackTimelineStabilizer.IsPlaybackRestart(
            TimeSpan.FromSeconds(178),
            TimeSpan.FromSeconds(2),
            duration));
        Check.False(PlaybackTimelineStabilizer.IsPlaybackRestart(
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(2),
            duration));
        Check.False(PlaybackTimelineStabilizer.IsPlaybackRestart(
            TimeSpan.FromSeconds(178),
            TimeSpan.FromSeconds(172),
            duration));
        Check.False(PlaybackTimelineStabilizer.IsPlaybackRestart(
            TimeSpan.FromSeconds(13),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(14)));

        // A same-track loop is accepted after a second coherent near-start
        // observation. The first zero remains fenced as possible provider jitter.
        var reliableRestart = new PlaybackTimelineStabilizer();
        Check.Equal(
            TimeSpan.FromSeconds(178),
            reliableRestart.Observe(
                "spotify|looping-song",
                hasSession: true,
                TimeSpan.FromSeconds(178),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start,
                hasReliableTimelineUpdatedAt: true,
                observedAt: start));
        Check.Equal(
            TimeSpan.FromSeconds(179),
            reliableRestart.Observe(
                "spotify|looping-song",
                hasSession: true,
                TimeSpan.Zero,
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(1),
                hasReliableTimelineUpdatedAt: true,
                observedAt: start + TimeSpan.FromSeconds(1)));
        Check.False(reliableRestart.LastObservationWasPlaybackRestart);
        Check.Equal(
            TimeSpan.FromSeconds(1),
            reliableRestart.Observe(
                "spotify|looping-song",
                hasSession: true,
                TimeSpan.FromSeconds(1),
                duration,
                isPlaying: true,
                timelineUpdatedAt: start + TimeSpan.FromSeconds(2),
                hasReliableTimelineUpdatedAt: true,
                observedAt: start + TimeSpan.FromSeconds(2)));
        Check.True(reliableRestart.LastObservationWasPlaybackRestart);

        // Providers with no usable timeline timestamp can report a paused zero
        // before playback resumes. Two coherent samples still identify the restart.
        var unreliableRestart = new PlaybackTimelineStabilizer();
        Check.Equal(
            TimeSpan.FromSeconds(178),
            unreliableRestart.Observe(
                "browser|looping-song",
                hasSession: true,
                TimeSpan.FromSeconds(178),
                duration,
                isPlaying: false,
                timelineUpdatedAt: default,
                hasReliableTimelineUpdatedAt: false,
                observedAt: start));
        Check.Equal(
            TimeSpan.FromSeconds(178),
            unreliableRestart.Observe(
                "browser|looping-song",
                hasSession: true,
                TimeSpan.Zero,
                duration,
                isPlaying: false,
                timelineUpdatedAt: default,
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(1)));
        Check.False(unreliableRestart.LastObservationWasPlaybackRestart);
        Check.Equal(
            TimeSpan.Zero,
            unreliableRestart.Observe(
                "browser|looping-song",
                hasSession: true,
                TimeSpan.Zero,
                duration,
                isPlaying: true,
                timelineUpdatedAt: default,
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(2)));
        Check.True(unreliableRestart.LastObservationWasPlaybackRestart);

        // A single bad zero that recovers to the end is never classified as a loop.
        var staleEndReading = new PlaybackTimelineStabilizer();
        staleEndReading.Observe(
            "chrome|stale-end",
            hasSession: true,
            TimeSpan.FromSeconds(178),
            duration,
            isPlaying: false,
            timelineUpdatedAt: default,
            hasReliableTimelineUpdatedAt: false,
            observedAt: start);
        Check.Equal(
            TimeSpan.FromSeconds(178),
            staleEndReading.Observe(
                "chrome|stale-end",
                hasSession: true,
                TimeSpan.Zero,
                duration,
                isPlaying: false,
                timelineUpdatedAt: default,
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(1)));
        Check.False(staleEndReading.LastObservationWasPlaybackRestart);
        Check.Equal(
            TimeSpan.FromSeconds(178),
            staleEndReading.Observe(
                "chrome|stale-end",
                hasSession: true,
                TimeSpan.FromSeconds(178),
                duration,
                isPlaying: false,
                timelineUpdatedAt: default,
                hasReliableTimelineUpdatedAt: false,
                observedAt: start + TimeSpan.FromSeconds(2)));
        Check.False(staleEndReading.LastObservationWasPlaybackRestart);
    }
}

static class LyricsServiceTests
{
    public static void FetchesExactOrBestLyricsAndCaches()
    {
        Check.Equal("", LyricsService.CreateTrackKey(MediaSnapshot.Empty));
        Check.Equal("song|artist|album", LyricsService.CreateTrackKey(Snapshot(" Song ", " Artist ", " Album ")));

        var exactGetCount = 0;
        var exactSearchCount = 0;
        var exactHandler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal(HttpMethod.Get, request.Method);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/get":
                    exactGetCount++;
                    var exactQuery = ParseQuery(request.RequestUri);
                    Check.Equal("Song", exactQuery["track_name"]);
                    Check.Equal("Artist", exactQuery["artist_name"]);
                    Check.Equal("Album", exactQuery["album_name"]);
                    Check.Equal("121", exactQuery["duration"]);
                    return Json(new
                    {
                        id = 1,
                        trackName = "Song",
                        artistName = "Artist",
                        albumName = "Album",
                        duration = 121d,
                        instrumental = false,
                        plainLyrics = "Exact plain line",
                        syncedLyrics = (string?)"[00:01.00]Exact line"
                    });
                case "/api/search":
                    exactSearchCount++;
                    throw new InvalidOperationException("search should not follow an exact hit");
                default:
                    throw new InvalidOperationException($"unexpected LRCLIB endpoint: {request.RequestUri.AbsolutePath}");
            }
        });

        using var service = new LyricsService(new HttpClient(exactHandler));
        var snapshot = Snapshot("Song - Official Video", "Artist", "Album", TimeSpan.FromSeconds(121));
        var result = service.GetLyricsAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();
        var cached = service.GetLyricsAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();

        Check.Same(result, cached);
        Check.Equal(1, exactHandler.Count);
        Check.Equal(1, exactGetCount);
        Check.Equal(0, exactSearchCount);
        Check.Equal(LyricsStatus.Synced, result.Status);
        Check.Equal("Exact line", result.SyncedLines.Single().Text);
        Check.Equal("[00:01.00]Exact line", result.SyncedText);
        Check.Equal("Exact plain line", result.PlainText);
        Check.Equal("LRCLIB", result.TrackInfo!.SourceName);

        var fallbackGetCount = 0;
        var fallbackSearchCount = 0;
        var fallbackHandler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal(HttpMethod.Get, request.Method);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/get":
                    fallbackGetCount++;
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"name\":\"TrackNotFound\"}", Encoding.UTF8, "application/json")
                    };
                case "/api/search":
                    fallbackSearchCount++;
                    var searchQuery = ParseQuery(request.RequestUri);
                    Check.Equal("Song", searchQuery["track_name"]);
                    Check.Equal("Artist", searchQuery["artist_name"]);
                    return Json(new[]
                    {
                        new { id = 2, trackName = "Song", artistName = "Artist", albumName = "Album", duration = 120d, instrumental = false, plainLyrics = "plain", syncedLyrics = (string?)null },
                        new { id = 3, trackName = "Song", artistName = "Artist", albumName = "Album", duration = 121d, instrumental = false, plainLyrics = "", syncedLyrics = (string?)"[00:01.00]Line" },
                        new { id = 4, trackName = "Song", artistName = "Artist", albumName = "Album", duration = 119d, instrumental = false, plainLyrics = "", syncedLyrics = (string?)"" }
                    });
                default:
                    throw new InvalidOperationException($"unexpected LRCLIB endpoint: {request.RequestUri.AbsolutePath}");
            }
        });

        using var fallbackService = new LyricsService(new HttpClient(fallbackHandler));
        var fallbackResult = fallbackService.GetLyricsAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();
        var fallbackCached = fallbackService.GetLyricsAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();

        Check.Same(fallbackResult, fallbackCached);
        Check.Equal(2, fallbackHandler.Count);
        Check.Equal(1, fallbackGetCount);
        Check.Equal(1, fallbackSearchCount);
        Check.Equal(LyricsStatus.Synced, fallbackResult.Status);
        Check.Equal("Line", fallbackResult.SyncedLines.Single().Text);

        using var emptyService = new LyricsService(new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("should not call"))));
        Check.Equal(LyricsStatus.Empty, emptyService.GetLyricsAsync(MediaSnapshot.Empty, CancellationToken.None).GetAwaiter().GetResult().Status);

        var plainSearchCount = 0;
        using var plainService = new LyricsService(new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Check.Equal("/api/search", request.RequestUri!.AbsolutePath);
            plainSearchCount++;
            return Json(new[]
            {
                new { id = 5, trackName = "Song", artistName = "Artist", albumName = "", duration = 0d, instrumental = false, plainLyrics = "\r\n one \n\n two ", syncedLyrics = (string?)null }
            });
        })));
        var plain = plainService.GetLyricsAsync(Snapshot("Song", "Artist", "", TimeSpan.FromSeconds(121)), CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(1, plainSearchCount);
        Check.Equal(LyricsStatus.Plain, plain.Status);
        Check.Sequence(["one", "two"], plain.PlainLines);

        var instrumentalSearchCount = 0;
        using var instrumentalService = new LyricsService(new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Check.Equal("/api/search", request.RequestUri!.AbsolutePath);
            instrumentalSearchCount++;
            return Json(new[]
            {
                new { id = 6, trackName = "Song", artistName = "Artist", albumName = "", duration = 0d, instrumental = true, plainLyrics = "", syncedLyrics = "" }
            });
        })));
        Check.Equal(LyricsStatus.Instrumental, instrumentalService.GetLyricsAsync(Snapshot("Song", "Artist", "Album", TimeSpan.Zero), CancellationToken.None).GetAwaiter().GetResult().Status);
        Check.Equal(1, instrumentalSearchCount);

        var missingArtistSearchCount = 0;
        using var missingArtistService = new LyricsService(new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Check.Equal("/api/search", request.RequestUri!.AbsolutePath);
            missingArtistSearchCount++;
            return Json(Array.Empty<object>());
        })));
        var missingArtist = missingArtistService.GetLyricsAsync(
            Snapshot("Song", "", "Album", TimeSpan.FromSeconds(121)),
            CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(LyricsStatus.NotFound, missingArtist.Status);
        Check.Equal(1, missingArtistSearchCount);
    }

    public static void NormalizesAppleMusicMetadata()
    {
        const string appleMusicSource = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
        var appleSnapshot = Snapshot(
                "Potholderz (feat. Count Bass D)",
                "MF DOOM \u2014 MM..FOOD (Deluxe Edition)",
                "",
                TimeSpan.FromSeconds(200)) with
            {
                SourceApp = appleMusicSource
            };

        var handler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal("/api/get", request.RequestUri!.AbsolutePath);
            var query = ParseQuery(request.RequestUri);
            Check.Equal("Potholderz (feat. Count Bass D)", query["track_name"]);
            Check.Equal("MF DOOM", query["artist_name"]);
            Check.Equal("MM..FOOD (Deluxe Edition)", query["album_name"]);
            Check.Equal("200", query["duration"]);
            return Json(new
            {
                id = 7,
                trackName = "Potholderz (feat. Count Bass D)",
                artistName = "MF DOOM",
                albumName = "MM..FOOD (Deluxe Edition)",
                duration = 200d,
                instrumental = false,
                plainLyrics = "Apple plain line",
                syncedLyrics = (string?)"[00:01.00]Apple line"
            });
        });

        using var service = new LyricsService(new HttpClient(handler));
        var result = service.GetLyricsAsync(appleSnapshot, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(1, handler.Count);
        Check.Equal(LyricsStatus.Synced, result.Status);
        Check.Equal("Apple line", result.SyncedLines.Single().Text);

        var normalized = AppleMusicMediaMetadata.NormalizeLyricsLookup(appleSnapshot);
        Check.Equal("MF DOOM", normalized.Artist);
        Check.Equal("MM..FOOD (Deluxe Edition)", normalized.Album);
        Check.Equal(
            "potholderz (feat. count bass d)|mf doom|mm..food (deluxe edition)",
            LyricsService.CreateTrackKey(appleSnapshot));

        var appleWithAlbum = AppleMusicMediaMetadata.NormalizeLyricsLookup(appleSnapshot with
        {
            Artist = "MF DOOM \u2014 Should remain artist text",
            Album = "Published Album"
        });
        Check.Equal("MF DOOM \u2014 Should remain artist text", appleWithAlbum.Artist);
        Check.Equal("Published Album", appleWithAlbum.Album);

        var enDash = AppleMusicMediaMetadata.NormalizeLyricsLookup(appleSnapshot with
        {
            Artist = "MF DOOM \u2013 MM..FOOD (Deluxe Edition)"
        });
        Check.Equal("MF DOOM", enDash.Artist);
        Check.Equal("MM..FOOD (Deluxe Edition)", enDash.Album);

        var nonAppleSnapshot = appleSnapshot with { SourceApp = "chrome.exe" };
        var nonAppleNormalized = AppleMusicMediaMetadata.NormalizeLyricsLookup(nonAppleSnapshot);
        Check.Equal("MF DOOM \u2014 MM..FOOD (Deluxe Edition)", nonAppleNormalized.Artist);
        Check.Equal("", nonAppleNormalized.Album);
        Check.Equal(
            "potholderz (feat. count bass d)|mf doom \u2014 mm..food (deluxe edition)|",
            LyricsService.CreateTrackKey(nonAppleSnapshot));

        Check.Equal(
            "Album Artist",
            AppleMusicMediaMetadata.ResolveArtist("", " Album Artist ", appleMusicSource));
        Check.Equal(
            "Primary Artist",
            AppleMusicMediaMetadata.ResolveArtist(" Primary Artist ", "Album Artist", appleMusicSource));
        Check.Equal(
            "",
            AppleMusicMediaMetadata.ResolveArtist("", "Album Artist", "chrome.exe"));
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1]),
                StringComparer.Ordinal);
    }
}

static class LastFmServiceTests
{
    public static void UsesLastFmApiSafely()
    {
        using var temp = TempDir.Create();
        var settings = new AppSettingsService(Path.Combine(temp.Path, "settings.json"));
        using var unconfigured = new LastFmService(settings, new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("should not call"))));
        Check.False(unconfigured.IsConfigured);
        Check.Equal("Fill in the Last.fm API key and username.", unconfigured.ValidateCredentialsAsync(new LastFmCredentials()).GetAwaiter().GetResult().Message);
        Check.Equal(
            "Scrobbling also needs the API secret and password.",
            unconfigured.ValidateCredentialsAsync(new LastFmCredentials
            {
                Enabled = true,
                ApiKey = "api",
                Username = "user",
                ScrobblingEnabled = true
            }).GetAwaiter().GetResult().Message);
        Check.Equal("Scrobbling is disabled.", unconfigured.ScrobbleAsync(Track(), DateTimeOffset.UnixEpoch).GetAwaiter().GetResult().Message);

        settings.Save(new AppSettings
        {
            LastFm = new LastFmCredentials
            {
                Enabled = true,
                ApiKey = "api",
                ApiSecret = "secret",
                Username = "user",
                Password = "pass",
                ScrobblingEnabled = true
            }
        });

        var handler = new FakeHttpMessageHandler(request =>
        {
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (request.Method == HttpMethod.Post && body.Contains("method=auth.getMobileSession", StringComparison.Ordinal))
            {
                Check.Contains("api_sig=", body);
                return Json(new { session = new { key = "session-key" } });
            }

            if (request.Method == HttpMethod.Post && body.Contains("method=track.scrobble", StringComparison.Ordinal))
            {
                Check.Contains("timestamp=42", body);
                Check.Contains("duration=123", body);
                Check.Contains("sk=session-key", body);
                Check.Contains("api_sig=", body);
                return Json(new { scrobbles = new { } });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("method=track.getInfo", StringComparison.Ordinal))
            {
                Check.Contains("artist=Artist", request.RequestUri.Query);
                Check.Contains("track=Song", request.RequestUri.Query);
                return Json(new
                {
                    track = new
                    {
                        name = "Song (Official Video)",
                        url = "https://last.fm/track",
                        duration = "123000",
                        artist = new { name = "Artist - Album" },
                        album = new { title = "Album [HD]" }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("method=user.getInfo", StringComparison.Ordinal))
            {
                return Json(new { user = new { name = "user" } });
            }

            throw new InvalidOperationException("Unexpected request: " + request);
        });

        using var service = new LastFmService(settings, new HttpClient(handler));
        Check.True(service.IsConfigured);
        Check.True(service.IsScrobblingEnabled);

        var validation = service.ValidateCredentialsAsync(settings.Settings.LastFm).GetAwaiter().GetResult();
        Check.True(validation.IsSuccess);
        Check.Equal("Last.fm account and scrobbling verified.", validation.Message);

        var scrobble = service.ScrobbleAsync(Track(), DateTimeOffset.FromUnixTimeSeconds(42)).GetAwaiter().GetResult();
        Check.True(scrobble.IsSuccess);
        Check.Equal("Scrobbled Artist - Song.", scrobble.Message);

        var info = service.GetTrackInfoAsync(new LastFmTrackQuery(" Song ", " Artist ", "Album")).GetAwaiter().GetResult();
        var cached = service.GetTrackInfoAsync(new LastFmTrackQuery("song", "artist", "ignored")).GetAwaiter().GetResult();
        Check.Same(info, cached);
        Check.Equal("Song", info!.TrackName);
        Check.Equal("Artist", info.ArtistName);
        Check.Equal("Album", info.AlbumName);
        Check.Equal(TimeSpan.FromSeconds(123), info.Duration);
        Check.Equal(3, handler.Count);

        settings.Save(new AppSettings { LastFm = new LastFmCredentials { Enabled = true, ApiKey = "api2", ApiSecret = "secret", Username = "user", Password = "pass" } });
        Check.False(service.IsScrobblingEnabled);
        Check.Null(service.GetTrackInfoAsync(new LastFmTrackQuery("", "Artist", "")).GetAwaiter().GetResult());
    }
}

static class ModelTests
{
    public static void DefaultsAndComputedPropertiesAreStable()
    {
        // Viewer features need only key + username; scrobbling adds secret + password.
        Check.False(new LastFmCredentials { ApiKey = "a", Username = "u" }.HasViewerCredentials);
        Check.False(new LastFmCredentials { Enabled = true, ApiKey = "a" }.HasViewerCredentials);
        Check.True(new LastFmCredentials { Enabled = true, ApiKey = "a", Username = "u" }.HasViewerCredentials);
        Check.True(new LastFmCredentials { Enabled = true, ApiKey = "a", Username = "u" }.IsConfigured);
        Check.False(new LastFmCredentials { Enabled = true, ApiKey = "a", Username = "u" }.HasScrobblingCredentials);
        Check.False(new LastFmCredentials { Enabled = true, ApiKey = "a", ApiSecret = "s", Username = "u", ScrobblingEnabled = true }.IsConfigured);
        Check.True(new LastFmCredentials { Enabled = true, ApiKey = "a", ApiSecret = "s", Username = "u", Password = "p", ScrobblingEnabled = true }.IsConfigured);
        Check.True(new LastFmCredentials { Enabled = true, ApiKey = "a", ApiSecret = "s", Username = "u", Password = "p" }.HasScrobblingCredentials);
        Check.True(new BehaviorSettings().CloseToTray);
        Check.Equal(
            BurnLyricsProvider.MusicBrainzAssisted,
            new BehaviorSettings().BurnLyricsProvider);
        Check.Equal("#4A5258", AppearanceSettings.DefaultPlayerThemeColor);
        Check.Equal(string.Empty, new AppearanceSettings().PlayerThemeColor);
        Check.Equal(
            "#0A7FFF",
            AppearanceSettings.FormatPlayerThemeColor(0x0A, 0x7F, 0xFF));
        Check.Equal(
            "#A1B2C3",
            AppearanceSettings.NormalizePlayerThemeColor("  #a1b2c3 "));
        Check.Equal(
            string.Empty,
            AppearanceSettings.NormalizePlayerThemeColor("#A1B2C3FF"));
        Check.True(AppearanceSettings.TryParsePlayerThemeColor(
            "#0A7FFF",
            out var themeRed,
            out var themeGreen,
            out var themeBlue));
        Check.Equal((byte)0x0A, themeRed);
        Check.Equal((byte)0x7F, themeGreen);
        Check.Equal((byte)0xFF, themeBlue);
        Check.False(AppearanceSettings.TryParsePlayerThemeColor(
            "not-a-color",
            out _,
            out _,
            out _));
        Check.Equal("No active track", MediaSnapshot.Empty.Title);
        Check.False(MediaSnapshot.Empty.HasSession);
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(0).LocalDateTime.ToString("yyyy-MM-dd"), new ScrobbleRecord { Timestamp = 0 }.FormattedTime[..10]);
        Check.False(new ScrobbleRecord().IsSelected);

        var globeBurn = GlobeBurnShareRequest.FromDraft(new BurnTrackDraft
        {
            SourcePath = "song.wav",
            Album = " ",
            Artist = "",
            TrackTotal = "9999"
        });
        Check.Equal("Unknown album", globeBurn.Album);
        Check.Equal("Unknown artist", globeBurn.Artist);
        Check.Equal(GlobeBurnShareRequest.MaximumTrackCount, globeBurn.TrackCount);

        var lines = new[] { new LyricLine(TimeSpan.Zero, "line") };
        Check.Equal(LyricsStatus.Synced, LyricsResult.Synced(lines).Status);
        Check.Same(lines, LyricsResult.Synced(lines).SyncedLines);
        Check.Equal(LyricsStatus.Error, LyricsResult.Error("bad").Status);
    }
}

static class ArtworkTintTests
{
    public static void ColorHelpersAreStable()
    {
        Check.Equal(Colors.Black, ArtworkTint.Blend(Colors.Black, Colors.White, -1));
        Check.Equal(Colors.White, ArtworkTint.Blend(Colors.Black, Colors.White, 2));
        Check.Equal(Color.FromRgb(128, 128, 128), ArtworkTint.Blend(Colors.Black, Colors.White, 0.5));
        Check.Equal(Color.FromArgb(64, 10, 20, 30), ArtworkTint.WithAlpha(Color.FromRgb(10, 20, 30), 64));
        Check.Null(ArtworkTint.ExtractDominantTint(null));

        var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 2, 2), new byte[]
        {
            30, 90, 210, 255,
            30, 90, 210, 255,
            255, 255, 255, 255,
            0, 0, 0, 0
        }, 8, 0);
        var tint = ArtworkTint.ExtractDominantTint(bitmap);
        Check.NotNull(tint);
        Check.True(tint!.Value.R > tint.Value.G);
        Check.True(tint.Value.G > tint.Value.B);

        var defaultTint = Color.FromRgb(74, 82, 88);
        Check.Equal(
            Color.FromRgb(0x12, 0x34, 0x56),
            MainWindow.ResolvePlayerTint("#123456", Colors.Red, defaultTint));
        Check.Equal(
            Colors.Red,
            MainWindow.ResolvePlayerTint(string.Empty, Colors.Red, defaultTint));
        Check.Equal(
            defaultTint,
            MainWindow.ResolvePlayerTint("not-a-color", null, defaultTint));
        Check.False(MainWindow.ShouldShowPlayerArtworkBackdrops("#123456"));
        Check.True(MainWindow.ShouldShowPlayerArtworkBackdrops(string.Empty));
        Check.True(MainWindow.ShouldShowPlayerArtworkBackdrops("not-a-color"));
    }
}

static class CdArtworkComposerTests
{
    public static void ComposesMaskedLayerStack()
    {
        var transparent = Pixel(0, 0, 0, 0);
        var composed = CdArtworkComposer.Compose(
            Pixel(100, 150, 200, 255),
            Pixel(255, 255, 255, 255),
            Pixel(128, 255, 64, 255),
            Pixel(0, 128, 255, 255),
            transparent);

        Check.True(composed.IsFrozen);
        Check.Equal(1, composed.PixelWidth);
        Check.Equal(1, composed.PixelHeight);
        Check.Sequence(new byte[] { 50, 203, 255, 255 }, ReadPixel(composed));

        var halfMasked = CdArtworkComposer.Compose(
            Pixel(30, 60, 90, 200),
            Pixel(128, 128, 128, 255),
            transparent,
            transparent,
            transparent);
        Check.Sequence(new byte[] { 30, 60, 90, 100 }, ReadPixel(halfMasked));

        var fullyMasked = CdArtworkComposer.Compose(
            Pixel(30, 60, 90, 255),
            Pixel(0, 0, 0, 255),
            transparent,
            transparent,
            transparent);
        Check.Equal((byte)0, ReadPixel(fullyMasked)[3]);

        var partialAlpha = CdArtworkComposer.Compose(
            Pixel(0, 0, 255, 128),
            Pixel(255, 255, 255, 255),
            transparent,
            transparent,
            Pixel(255, 0, 0, 128));
        Check.Sequence(new byte[] { 170, 0, 85, 192 }, ReadPixel(partialAlpha));

        var centerCrop = CdArtworkComposer.Compose(
            Bitmap(2, 1,
            [
                0, 0, 0, 255,
                255, 255, 255, 255
            ]),
            Pixel(255, 255, 255, 255),
            transparent,
            transparent,
            transparent);
        Check.Sequence(new byte[] { 128, 128, 128, 255 }, ReadPixel(centerCrop));
    }

    private static BitmapSource Pixel(byte blue, byte green, byte red, byte alpha)
    {
        return Bitmap(1, 1, [blue, green, red, alpha]);
    }

    private static BitmapSource Bitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] ReadPixel(BitmapSource bitmap)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(pixel, 4, 0);
        return pixel;
    }
}

static class ImageArtworkLoaderTests
{
    public static void ValidatesDecodedImageContent()
    {
        using var temp = TempDir.Create();
        var loader = new ImageArtworkLoader();
        var png = Png(
            width: 2,
            height: 1,
            pixels:
            [
                20, 40, 220, 255,
                180, 80, 10, 255
            ]);
        var validPath = Path.Combine(temp.Path, "cover.not-an-image-extension");
        File.WriteAllBytes(validPath, png);

        var artwork = loader.LoadFileAsync(validPath).GetAwaiter().GetResult();

        Check.Sequence(png, artwork.Data);
        Check.True(artwork.Preview.IsFrozen);
        Check.Equal(2, artwork.Preview.PixelWidth);
        Check.Equal(1, artwork.Preview.PixelHeight);

        var fakeImagePath = Path.Combine(temp.Path, "fake.png");
        File.WriteAllText(fakeImagePath, "this is not an image");
        Check.Throws<InvalidDataException>(() =>
            loader.LoadFileAsync(fakeImagePath).GetAwaiter().GetResult());
        Check.Throws<InvalidDataException>(() =>
            loader.LoadAsync([]).GetAwaiter().GetResult());
    }
}

static class MusicBrainzServiceTests
{
    private const string MatchedRecordingJson = """
        {
          "recordings": [{
            "id": "recording-recovered",
            "score": 100,
            "title": "Recovered title",
            "artist-credit": [{
              "name": "Recovered artist",
              "artist": { "id": "artist-recovered", "name": "Recovered artist" }
            }],
            "releases": []
          }]
        }
        """;

    public static void MapsTrackInformation()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            Check.Equal("musicbrainz.org", uri.Host);
            Check.Contains("Mystral/", request.Headers.UserAgent.ToString());
            var query = Uri.UnescapeDataString(uri.Query);
            if (query.Contains("Uncertain title", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "id": "recording-low",
                        "score": 69,
                        "title": "Uncertain title",
                        "artist-credit": [{ "name": "Artist" }]
                      }]
                    }
                    """);
            }

            Check.Contains("recording:\"Requested title\"", query);
            Check.Contains("artist:\"Lead credit feat. Guest\"", query);
            Check.Contains("release:\"Selected album\"", query);
            return JsonText("""
                {
                  "recordings": [
                    {
                      "id": "recording-other",
                      "score": 100,
                      "title": "Another song",
                      "artist-credit": [{ "artist": { "id": "artist-other", "name": "Other" } }]
                    },
                    {
                      "id": "recording-good",
                      "score": "92",
                      "title": "Requested title",
                      "length": 211234,
                      "first-release-date": "1998-04-01",
                      "disambiguation": "studio master",
                      "isrcs": [
                        { "id": " USAAA0100001 " },
                        "USAAA0100002",
                        { "id": "usaaa0100001" }
                      ],
                      "artist-credit": [
                        {
                          "name": "Lead credit",
                          "joinphrase": " feat. ",
                          "artist": { "id": "artist-lead", "name": "Lead legal" }
                        },
                        {
                          "artist": { "id": "artist-guest", "name": "Guest" }
                        }
                      ],
                      "genres": [{ "count": 4, "name": "art pop" }],
                      "tags": [
                        { "count": 9, "name": "alternative rock" },
                        { "count": 1, "name": "Art Pop" }
                      ],
                      "releases": [
                        {
                          "id": "release-other",
                          "title": "Other album",
                          "media": []
                        },
                        {
                          "id": "release-selected",
                          "title": "Selected album",
                          "status": "Official",
                          "release-group": { "id": "group-selected", "primary-type": "Album" },
                          "media": [{
                            "format": "CD",
                            "track-count": "13",
                            "track": [
                              { "number": "01", "recording": { "id": "some-other-recording" } },
                              { "number": "07", "recording": { "id": "recording-good" } }
                            ]
                          }]
                        }
                      ]
                    }
                  ]
                }
                """);
        });

        using var client = new HttpClient(handler);
        using var service = new MusicBrainzService(client);
        var result = service.FetchTrackInfoAsync(
                "Requested title",
                "Lead credit feat. Guest",
                "Selected album",
                TimeSpan.FromMilliseconds(211234))
            .GetAwaiter()
            .GetResult();

        Check.NotNull(result);
        Check.Equal("recording-good", result!.RecordingId);
        Check.Equal("Requested title", result.Title);
        Check.Equal("Lead credit feat. Guest", result.Artist);
        Check.Equal(2, result.ArtistCredits.Count);
        Check.Equal("artist-lead", result.ArtistCredits[0].ArtistId);
        Check.Equal("Lead credit", result.ArtistCredits[0].Name);
        Check.Equal(" feat. ", result.ArtistCredits[0].JoinPhrase);
        Check.Equal("artist-guest", result.ArtistCredits[1].ArtistId);
        Check.Equal(TimeSpan.FromMilliseconds(211234), result.Duration);
        Check.Equal("1998-04-01", result.FirstReleaseDate);
        Check.Sequence(new[] { "USAAA0100001", "USAAA0100002" }, result.Isrcs);
        Check.Sequence(new[] { "alternative rock", "art pop" }, result.Genres);
        Check.Equal("studio master", result.Disambiguation);
        Check.Equal("release-selected", result.ReleaseId);
        Check.Equal("group-selected", result.ReleaseGroupId);
        Check.Equal("Selected album", result.Album);
        Check.Equal("07", result.TrackNumber);
        Check.Equal("13", result.TrackTotal);

        var uncertain = service.FetchTrackInfoAsync(
                "Uncertain title", "Artist", string.Empty, TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
        Check.Null(uncertain);

        var empty = service.FetchTrackInfoAsync(
                string.Empty, "Artist", "Album", TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
        Check.Null(empty);
        Check.Equal(2, handler.Count);
    }

    public static void RetriesTransientApiFailures()
    {
        var responseAttempt = 0;
        var transientHandler = new FakeHttpMessageHandler(_ =>
        {
            responseAttempt++;
            if (responseAttempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            }

            return responseAttempt == 2
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : JsonText(MatchedRecordingJson);
        });
        using (var client = new HttpClient(transientHandler))
        using (var service = new MusicBrainzService(client))
        {
            var result = service.FetchTrackInfoAsync(
                    "Recovered title",
                    "Recovered artist",
                    string.Empty,
                    TimeSpan.Zero)
                .GetAwaiter()
                .GetResult();

            Check.NotNull(result);
            Check.Equal("recording-recovered", result!.RecordingId);
            Check.Equal(3, transientHandler.Count);
        }

        var transportAttempt = 0;
        var transportHandler = new FakeHttpMessageHandler(_ =>
        {
            transportAttempt++;
            return transportAttempt == 1
                ? throw new HttpRequestException("The connection was reset.")
                : JsonText(MatchedRecordingJson);
        });
        using (var client = new HttpClient(transportHandler))
        using (var service = new MusicBrainzService(client))
        {
            var result = service.FetchTrackInfoAsync(
                    "Recovered title",
                    "Recovered artist",
                    string.Empty,
                    TimeSpan.Zero)
                .GetAwaiter()
                .GetResult();

            Check.NotNull(result);
            Check.Equal(2, transportHandler.Count);
        }

        var exhaustedHandler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using (var client = new HttpClient(exhaustedHandler))
        using (var service = new MusicBrainzService(client))
        {
            Check.Throws<HttpRequestException>(() =>
                service.FetchTrackInfoAsync(
                        "Unavailable title",
                        "Artist",
                        string.Empty,
                        TimeSpan.Zero)
                    .GetAwaiter()
                    .GetResult());
            Check.Equal(3, exhaustedHandler.Count);
        }

        var permanentHandler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var permanentClient = new HttpClient(permanentHandler);
        using var permanentService = new MusicBrainzService(permanentClient);
        Check.Throws<HttpRequestException>(() =>
            permanentService.FetchTrackInfoAsync(
                    "Rejected title",
                    "Artist",
                    string.Empty,
                    TimeSpan.Zero)
                .GetAwaiter()
                .GetResult());
        Check.Equal(1, permanentHandler.Count);

        var oversizedHandler = new FakeHttpMessageHandler(_ =>
        {
            var response = JsonText(MatchedRecordingJson);
            response.Content.Headers.ContentLength = 9L * 1024 * 1024;
            return response;
        });
        using var oversizedClient = new HttpClient(oversizedHandler);
        using var oversizedService = new MusicBrainzService(oversizedClient);
        Check.Throws<InvalidDataException>(() =>
            oversizedService.FetchTrackInfoAsync(
                    "Oversized title",
                    "Artist",
                    string.Empty,
                    TimeSpan.Zero)
                .GetAwaiter()
                .GetResult());
        Check.Equal(1, oversizedHandler.Count);
    }

    public static void ClassifiesLookupFailures()
    {
        foreach (var status in new[]
                 {
                     HttpStatusCode.RequestTimeout,
                     HttpStatusCode.TooManyRequests,
                     HttpStatusCode.InternalServerError,
                     HttpStatusCode.BadGateway,
                     HttpStatusCode.ServiceUnavailable,
                     HttpStatusCode.GatewayTimeout
                 })
        {
            Check.True(MusicBrainzService.IsTransientMusicBrainzStatus(status));
            var failure = MusicInfoPanel.ClassifyLookupFailure(
                new HttpRequestException("Transient failure.", null, status));
            Check.Equal("These details aren't available right now. Try again.", failure.Message);
            Check.True(failure.CanRetry);
            Check.True(failure.IsTransient);
        }

        foreach (var status in new[]
                 {
                     HttpStatusCode.BadRequest,
                     HttpStatusCode.Unauthorized,
                     HttpStatusCode.Forbidden,
                     HttpStatusCode.NotFound
                 })
        {
            Check.False(MusicBrainzService.IsTransientMusicBrainzStatus(status));
        }

        var missing = MusicInfoPanel.ClassifyLookupFailure(
            new HttpRequestException("Missing.", null, HttpStatusCode.NotFound));
        Check.Equal("These details are no longer available.", missing.Message);
        Check.False(missing.CanRetry);
        Check.False(missing.IsTransient);

        var missingSearch = MusicInfoPanel.ClassifyLookupFailure(
            new HttpRequestException("Missing search route.", null, HttpStatusCode.NotFound),
            isEntityLookup: false);
        Check.Equal("These details aren't available right now. Try again.", missingSearch.Message);
        Check.True(missingSearch.CanRetry);
        Check.True(missingSearch.IsTransient);

        var blocked = MusicInfoPanel.ClassifyLookupFailure(
            new HttpRequestException("Blocked.", null, HttpStatusCode.Forbidden));
        Check.Equal("These details aren't available right now. Try again.", blocked.Message);
        Check.True(blocked.CanRetry);
        Check.True(blocked.IsTransient);

        var connection = MusicInfoPanel.ClassifyLookupFailure(
            new HttpRequestException("Connection reset."));
        Check.Equal("These details aren't available right now. Try again.", connection.Message);
        Check.True(connection.CanRetry);
        Check.True(connection.IsTransient);

        var timeout = MusicInfoPanel.ClassifyLookupFailure(new TaskCanceledException());
        Check.Equal("These details took too long to load. Try again.", timeout.Message);
        Check.True(timeout.CanRetry);
        Check.True(timeout.IsTransient);

        var interrupted = MusicInfoPanel.ClassifyLookupFailure(new IOException("The response ended early."));
        Check.Equal("These details aren't available right now. Try again.", interrupted.Message);
        Check.True(interrupted.CanRetry);
        Check.True(interrupted.IsTransient);

        var invalid = MusicInfoPanel.ClassifyLookupFailure(new InvalidDataException());
        Check.Equal("These details couldn't be read. Try again.", invalid.Message);
        Check.True(invalid.CanRetry);
        Check.True(invalid.IsTransient);

        var rejected = MusicInfoPanel.ClassifyLookupFailure(
            new HttpRequestException("Rejected.", null, HttpStatusCode.BadRequest));
        Check.Equal("These details couldn't be loaded.", rejected.Message);
        Check.False(rejected.CanRetry);
        Check.False(rejected.IsTransient);

        var retryAt = DateTimeOffset.UtcNow.AddSeconds(10);
        var cachedTransient = new CachedMusicInfoLookupFailure(connection, retryAt);
        Check.False(MusicInfoPanel.IsLookupRetryDue(cachedTransient, retryAt.AddTicks(-1)));
        Check.True(MusicInfoPanel.IsLookupRetryDue(cachedTransient, retryAt));
        var cachedPermanent = new CachedMusicInfoLookupFailure(rejected, DateTimeOffset.MinValue);
        Check.False(MusicInfoPanel.IsLookupRetryDue(cachedPermanent, retryAt));
    }

    public static void ChoosesConfidentRecordingCandidate()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            var query = Uri.UnescapeDataString(uri.Query);
            if (query.Contains("Common title", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "recordings": [
                        {
                          "id": "wrong-high-score",
                          "score": 100,
                          "title": "Common title",
                          "artist-credit": [{ "name": "Wrong artist" }]
                        },
                        {
                          "id": "right-lower-score",
                          "score": 75,
                          "title": "Common title",
                          "artist-credit": [{ "artist": { "id": "right-artist", "name": "Right artist" } }]
                        }
                      ]
                    }
                    """);
            }

            if (query.Contains("Wrong-only title", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "id": "wrong-only",
                        "score": 100,
                        "title": "Wrong-only title",
                        "artist-credit": [{ "name": "Someone else" }]
                      }]
                    }
                    """);
            }

            if (query.Contains("Exact collision", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "id": "wrong-exact-duration",
                        "score": 100,
                        "title": "Exact collision",
                        "length": 180000,
                        "artist-credit": [{ "name": "Wrong artist" }],
                        "releases": [{ "title": "Wrong album" }]
                      }]
                    }
                    """);
            }

            if (query.Contains("Fuzzy collision", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "id": "wrong-fuzzy-duration",
                        "score": 100,
                        "title": "Fuzzy collisian",
                        "length": 60000,
                        "artist-credit": [{ "name": "Right artist" }],
                        "releases": [{ "title": "Target album" }]
                      }]
                    }
                    """);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using var client = new HttpClient(handler);
        using var service = new MusicBrainzService(client);
        var selected = service.FetchTrackInfoAsync(
                "Common title", "Right artist", string.Empty, TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
        Check.NotNull(selected);
        Check.Equal("right-lower-score", selected!.RecordingId);

        var rejected = service.FetchTrackInfoAsync(
                "Wrong-only title", "Expected artist", string.Empty, TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
        Check.Null(rejected);

        var exactDurationCollision = service.FetchTrackInfoAsync(
                "Exact collision", "Expected artist", "Expected album", TimeSpan.FromMinutes(3))
            .GetAwaiter()
            .GetResult();
        Check.Null(exactDurationCollision);

        var fuzzyDurationCollision = service.FetchTrackInfoAsync(
                "Fuzzy collision", "Right artist", "Target album", TimeSpan.FromMinutes(3))
            .GetAwaiter()
            .GetResult();
        Check.Null(fuzzyDurationCollision);
        Check.Equal(4, handler.Count);
    }

    public static void MapsArtistInformation()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            Check.Equal("/ws/2/artist/artist-123", uri.AbsolutePath);
            Check.Contains("aliases+annotation+genres+tags", uri.Query);
            Check.Contains("url-rels", uri.Query);
            return JsonText("""
                {
                  "id": "artist-123",
                  "name": "The Artist",
                  "sort-name": "Artist, The",
                  "type": "Person",
                  "gender": "Female",
                  "country": "GB",
                  "area": { "name": "United Kingdom" },
                  "begin-area": { "name": "London" },
                  "end-area": { "name": "Paris" },
                  "life-span": { "begin": "1980-04-03", "end": "2020", "ended": true },
                  "disambiguation": "English songwriter",
                  "annotation": " Long-form artist note. ",
                  "relations": [
                    {
                      "type": "image",
                      "ended": true,
                      "url": { "resource": "https://commons.wikimedia.org/wiki/File:Old_photo.jpg" }
                    },
                    {
                      "type": "image",
                      "ended": false,
                      "url": { "resource": "http://commons.wikimedia.org/wiki/File:Insecure_photo.jpg" }
                    },
                    {
                      "type": "image",
                      "ended": false,
                      "url": { "resource": "https://commons.wikimedia.org.example/wiki/File:Lookalike.jpg" }
                    },
                    {
                      "type": "official homepage",
                      "ended": false,
                      "url": { "resource": "https://commons.wikimedia.org/wiki/File:Wrong_relation.jpg" }
                    },
                    {
                      "type": "image",
                      "ended": false,
                      "url": { "resource": "https://commons.wikimedia.org/wiki/File:The_Artist_live.jpg" }
                    }
                  ],
                  "aliases": [
                    { "name": "Stage Name" },
                    { "name": "stage name" },
                    { "name": "Real Name" }
                  ],
                  "genres": [
                    { "count": "7", "name": "art pop" },
                    { "count": 3, "name": "electronic" }
                  ],
                  "tags": [{ "count": 1, "name": "singer-songwriter" }]
                }
                """);
        });

        using var client = new HttpClient(handler);
        using var service = new MusicBrainzService(client);
        var result = service.FetchArtistInfoAsync(" artist-123 ")
            .GetAwaiter()
            .GetResult();

        Check.Equal("artist-123", result.ArtistId);
        Check.Equal("The Artist", result.Name);
        Check.Equal("Artist, The", result.SortName);
        Check.Equal("Person", result.Type);
        Check.Equal("Female", result.Gender);
        Check.Equal("GB", result.Country);
        Check.Equal("United Kingdom", result.Area);
        Check.Equal("London", result.BeginArea);
        Check.Equal("Paris", result.EndArea);
        Check.Equal("1980-04-03", result.BeginDate);
        Check.Equal("2020", result.EndDate);
        Check.True(result.Ended);
        Check.Equal("English songwriter", result.Disambiguation);
        Check.Equal("Long-form artist note.", result.Annotation);
        Check.Equal(
            "https://commons.wikimedia.org/wiki/File:The_Artist_live.jpg",
            result.ImagePageUrl);
        Check.Sequence(new[] { "Stage Name", "Real Name" }, result.Aliases);
        Check.Sequence(new[] { "art pop", "electronic", "singer-songwriter" }, result.Genres);
        Check.Equal(1, handler.Count);
        Check.Throws<ArgumentException>(() =>
            service.FetchArtistInfoAsync(" ").GetAwaiter().GetResult());
        Check.Equal(1, handler.Count);
    }

    public static void MapsAlbumInformation()
    {
        var coverBytes = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
        var handler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                Check.Equal("/ws/2/release/release-123", uri.AbsolutePath);
                Check.Contains("artist-credits+labels+recordings+release-groups+genres+tags", uri.Query);
                return JsonText("""
                    {
                      "id": "release-123",
                      "title": "Album Title",
                      "status": "Official",
                      "date": "2004-05-06",
                      "country": "US",
                      "barcode": "0123456789012",
                      "packaging": "Jewel Case",
                      "disambiguation": "deluxe edition",
                      "artist-credit": [
                        { "name": "Lead", "joinphrase": " & ", "artist": { "id": "artist-lead", "name": "Lead" } },
                        { "artist": { "id": "artist-guest", "name": "Guest" } }
                      ],
                      "label-info": [
                        { "catalog-number": "CAT-42", "label": { "name": "Example Records" } },
                        { "catalog-number": "SELF-1", "label": null }
                      ],
                      "release-group": {
                        "id": "group-123",
                        "primary-type": "Album",
                        "secondary-types": ["Compilation", "Live"],
                        "first-release-date": "2003-11",
                        "genres": [{ "count": 10, "name": "indie rock" }],
                        "tags": [{ "count": 2, "name": "live" }]
                      },
                      "genres": [{ "count": 5, "name": "alternative rock" }],
                      "media": [
                        {
                          "position": 1,
                          "title": "Main program",
                          "format": "CD",
                          "track-count": "2",
                          "tracks": [
                            {
                              "position": "1",
                              "number": "1",
                              "title": "Opening Song",
                              "length": 180000,
                              "recording": {
                                "id": "recording-current",
                                "title": "Opening Song",
                                "artist-credit": [{ "name": "Lead" }]
                              }
                            },
                            {
                              "position": 2,
                              "number": "2",
                              "recording": {
                                "id": "recording-two",
                                "title": "Guest Song",
                                "length": 201234,
                                "artist-credit": [{ "name": "Guest" }]
                              }
                            }
                          ]
                        },
                        {
                          "position": 2,
                          "title": "Bonus disc",
                          "format": "Digital Media",
                          "track-count": 1,
                          "tracks": [{ "position": 1, "number": "B1", "title": "Bonus Track" }]
                        }
                      ]
                    }
                    """);
            }

            if (uri.Host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Equals("/release/release-123/", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "images": [
                        {
                          "approved": true,
                          "front": true,
                          "types": ["Front"],
                          "image": "https://assets.test/album-cover-original",
                          "thumbnails": { "500": "https://assets.test/album-cover-500" }
                        },
                        {
                          "approved": true,
                          "front": false,
                          "types": ["Medium"],
                          "image": "https://assets.test/disc-that-must-not-download",
                          "thumbnails": {}
                        }
                      ]
                    }
                    """);
            }

            if (uri.Equals(new Uri("https://assets.test/album-cover-500")))
            {
                return Bytes(coverBytes);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using var client = new HttpClient(handler);
        using var service = new MusicBrainzService(client);
        var result = service.FetchAlbumInfoAsync("release-123", "recording-current")
            .GetAwaiter()
            .GetResult();

        Check.Equal("release-123", result.ReleaseId);
        Check.Equal("group-123", result.ReleaseGroupId);
        Check.Equal("Album Title", result.Title);
        Check.Equal("Lead & Guest", result.Artist);
        Check.Equal("2003-11", result.FirstReleaseDate);
        Check.Equal("2004-05-06", result.ReleaseDate);
        Check.Equal("Album", result.PrimaryType);
        Check.Sequence(new[] { "Compilation", "Live" }, result.SecondaryTypes);
        Check.Equal("Official", result.Status);
        Check.Equal("US", result.Country);
        Check.Equal("0123456789012", result.Barcode);
        Check.Equal("Jewel Case", result.Packaging);
        Check.Equal("deluxe edition", result.Disambiguation);
        Check.Equal(2, result.Labels.Count);
        Check.Equal("Example Records", result.Labels[0].Name);
        Check.Equal("CAT-42", result.Labels[0].CatalogNumber);
        Check.Equal(string.Empty, result.Labels[1].Name);
        Check.Equal("SELF-1", result.Labels[1].CatalogNumber);
        Check.Sequence(new[] { "CD", "Digital Media" }, result.Formats);
        Check.Equal(3, result.TrackTotal);
        Check.Sequence(new[] { "indie rock", "alternative rock", "live" }, result.Genres);
        Check.Equal(3, result.Tracks.Count);
        Check.Equal("Opening Song", result.Tracks[0].Title);
        Check.Equal(1, result.Tracks[0].MediumPosition);
        Check.Equal("Main program", result.Tracks[0].MediumTitle);
        Check.Equal("CD", result.Tracks[0].MediumFormat);
        Check.Equal("Lead", result.Tracks[0].Artist);
        Check.Equal(TimeSpan.FromMinutes(3), result.Tracks[0].Duration);
        Check.Equal("Guest Song", result.Tracks[1].Title);
        Check.Equal("Guest", result.Tracks[1].Artist);
        Check.Equal(TimeSpan.FromMilliseconds(201234), result.Tracks[1].Duration);
        Check.Equal("B1", result.Tracks[2].Number);
        Check.Equal(2, result.Tracks[2].MediumPosition);
        Check.Equal("Bonus disc", result.Tracks[2].MediumTitle);
        Check.Equal("Digital Media", result.Tracks[2].MediumFormat);
        Check.Equal("Lead & Guest", result.Tracks[2].Artist);
        Check.Sequence(coverBytes, result.CoverArtwork!);
        Check.Equal(ArtworkFetchOutcome.Retrieved, result.CoverOutcome);
        Check.Equal(3, handler.Count);
    }

    public static void MapsRecordingAndArtworkResponses()
    {
        var coverBytes = new byte[] { 1, 3, 5, 7 };
        var discBytes = new byte[] { 2, 4, 6, 8 };
        var handler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");

            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                Check.Contains("Mystral/", request.Headers.UserAgent.ToString());
                var query = Uri.UnescapeDataString(uri.Query);
                Check.Contains("recording:\"Requested title\"", query);
                Check.Contains("artist:\"Lead & Guest\"", query);
                Check.Contains("release:\"Wanted album\"", query);
                return JsonText("""
                    {
                      "recordings": [
                        {
                          "score": "100",
                          "title": "Wrong result",
                          "length": 180000,
                          "artist-credit": [{ "name": "Someone else" }],
                          "releases": []
                        },
                        {
                          "score": "85",
                          "title": "Mapped title",
                          "length": 211000,
                          "first-release-date": "1998",
                          "artist-credit": [
                            { "name": "Lead", "joinphrase": " & " },
                            { "artist": { "name": "Guest" } }
                          ],
                          "tags": [
                            { "count": 2, "name": "ambient" },
                            { "count": 12, "name": "rock" }
                          ],
                          "releases": [
                            {
                              "id": "release-other",
                              "title": "Other album",
                              "status": "Official",
                              "media": [{ "format": "CD", "track-count": 9, "track": [{ "number": "1" }] }]
                            },
                            {
                              "id": "release-good",
                              "title": "Wanted album",
                              "status": "Official",
                              "date": "2001-02-03",
                              "release-group": { "id": "group-good", "primary-type": "Album" },
                              "media": [{ "format": "CD", "track-count": "12", "track": [{ "number": "07" }] }]
                            }
                          ]
                        }
                      ]
                    }
                    """);
            }

            if (uri.Host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Equals("/release/release-good/", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "images": [
                        {
                          "approved": true,
                          "front": true,
                          "types": ["Front"],
                          "image": "http://assets.test/cover-original",
                          "thumbnails": { "1200": "http://assets.test/cover-1200" }
                        },
                        {
                          "approved": true,
                          "front": false,
                          "types": ["Medium"],
                          "image": "http://assets.test/disc-original",
                          "thumbnails": { "500": "http://assets.test/disc-500" }
                        }
                      ]
                    }
                    """);
            }

            if (uri.Equals(new Uri("https://assets.test/cover-1200")))
            {
                return Bytes(coverBytes);
            }

            if (uri.Equals(new Uri("https://assets.test/disc-500")))
            {
                return Bytes(discBytes);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using var client = new HttpClient(handler);
        using var service = new MusicBrainzService(client);
        var result = service.FetchTrackDataAsync(
                "Requested title",
                "Lead & Guest",
                "Wanted album",
                "",
                TimeSpan.FromSeconds(211))
            .GetAwaiter()
            .GetResult();

        Check.NotNull(result);
        Check.Equal("Mapped title", result!.Title);
        Check.Equal("Lead & Guest", result.Artist);
        Check.Equal("rock", result.Genre);
        Check.Equal("2001", result.Year);
        Check.Equal("Wanted album", result.Album);
        Check.Equal("07", result.TrackNumber);
        Check.Equal("12", result.TrackTotal);
        Check.Sequence(coverBytes, result.CoverArtwork!);
        Check.Sequence(discBytes, result.DiscArtwork!);
        Check.Equal(4, handler.Count);

        var empty = service.FetchTrackDataAsync("", "Artist", "Album", "", TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
        Check.Null(empty);
        Check.Equal(4, handler.Count);

        var survivingDiscBytes = new byte[] { 9, 8, 7, 6 };
        var coverFallbackBytes = new byte[] { 5, 5, 5, 5 };
        var partialArtworkHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "score": 100,
                        "title": "Resilient title",
                        "artist-credit": [{ "name": "Resilient artist" }],
                        "releases": [{ "id": "resilient-release", "title": "Resilient album", "media": [] }]
                      }]
                    }
                    """);
            }

            if (uri.AbsolutePath.Equals("/release/resilient-release/", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "images": [
                        { "approved": true, "front": true, "types": ["Front"], "image": "https://assets.test/failing-cover", "thumbnails": {} },
                        { "approved": true, "front": false, "types": ["Medium"], "image": "https://assets.test/working-disc", "thumbnails": {} }
                      ]
                    }
                    """);
            }

            if (uri.Equals(new Uri("https://assets.test/failing-cover")))
            {
                // A permanent failure keeps this a fallback test; transient status
                // retries are covered by RetriesTransientArtworkResponses.
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            // When the indexed front thumbnail fails, the release/front-500 endpoint
            // is the next fallback and can still recover the cover.
            if (uri.AbsolutePath.Equals("/release/resilient-release/front-500", StringComparison.Ordinal))
            {
                return Bytes(coverFallbackBytes);
            }

            if (uri.Equals(new Uri("https://assets.test/working-disc")))
            {
                return Bytes(survivingDiscBytes);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });
        using var partialArtworkClient = new HttpClient(partialArtworkHandler);
        using var partialArtworkService = new MusicBrainzService(partialArtworkClient);
        var partialArtworkResult = partialArtworkService.FetchTrackDataAsync(
                "Resilient title",
                "Resilient artist",
                "Resilient album",
                "",
                TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
        Check.NotNull(partialArtworkResult);
        Check.Sequence(coverFallbackBytes, partialArtworkResult!.CoverArtwork!);
        Check.Sequence(survivingDiscBytes, partialArtworkResult.DiscArtwork!);
        Check.Equal(5, partialArtworkHandler.Count);

        using var malformedClient = new HttpClient(new FakeHttpMessageHandler(_ => JsonText("{")));
        using var malformedService = new MusicBrainzService(malformedClient);
        Check.Throws<InvalidDataException>(() =>
            malformedService.FetchTrackDataAsync(
                    "Song",
                    "Artist",
                    "",
                    "",
                    TimeSpan.Zero)
                .GetAwaiter()
                .GetResult());
    }

    public static void RetriesTransientArtworkResponses()
    {
        var coverBytes = new byte[] { 4, 4, 2, 2 };
        var coverHits = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "score": 100,
                        "title": "Retry title",
                        "artist-credit": [{ "name": "Retry artist" }],
                        "releases": [{ "id": "retry-release", "title": "Retry album", "media": [] }]
                      }]
                    }
                    """);
            }

            if (uri.AbsolutePath.Equals("/release/retry-release/", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "images": [
                        { "approved": true, "front": true, "types": ["Front"], "image": "https://assets.test/retry-cover", "thumbnails": {} }
                      ]
                    }
                    """);
            }

            if (uri.Equals(new Uri("https://assets.test/retry-cover")))
            {
                coverHits++;
                // Standard transient failures are retried before falling back.
                return coverHits switch
                {
                    1 => new HttpResponseMessage(HttpStatusCode.RequestTimeout),
                    2 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    _ => Bytes(coverBytes)
                };
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using var client = new HttpClient(handler);
        using var service = new MusicBrainzService(client);
        var result = service.FetchTrackDataAsync("Retry title", "Retry artist", "Retry album", "", TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();

        Check.NotNull(result);
        Check.Sequence(coverBytes, result!.CoverArtwork!);
        Check.Equal(3, coverHits);

        var transportHits = 0;
        var transportBytes = new byte[] { 7, 7, 7 };
        var transportHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return RecordingReleaseResponse("Transport title", "Transport artist", "transport-release");
            }

            if (uri.AbsolutePath.Equals("/release/transport-release/", StringComparison.Ordinal))
            {
                return FrontArtworkResponse("https://assets.test/transport-cover");
            }

            if (uri.Equals(new Uri("https://assets.test/transport-cover")))
            {
                transportHits++;
                if (transportHits == 1)
                {
                    throw new HttpRequestException("Simulated connection reset.");
                }

                return Bytes(transportBytes);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using (var transportClient = new HttpClient(transportHandler))
        using (var transportService = new MusicBrainzService(transportClient))
        {
            var transportResult = transportService.FetchTrackDataAsync(
                    "Transport title", "Transport artist", string.Empty, string.Empty, TimeSpan.Zero)
                .GetAwaiter()
                .GetResult();
            Check.NotNull(transportResult);
            Check.Sequence(transportBytes, transportResult!.CoverArtwork!);
            Check.Equal(2, transportHits);
        }

        // A cover URL that stays transiently unavailable is retried up to the cap, then gives up
        // gracefully (null cover) without throwing.
        var exhaustedHits = 0;
        var exhaustedHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "score": 100,
                        "title": "Exhausted title",
                        "artist-credit": [{ "name": "Exhausted artist" }],
                        "releases": [{ "id": "exhausted-release", "title": "Exhausted album", "media": [] }]
                      }]
                    }
                    """);
            }

            if (uri.AbsolutePath.Equals("/release/exhausted-release/", StringComparison.Ordinal))
            {
                return JsonText("""
                    {
                      "images": [
                        { "approved": true, "front": true, "types": ["Front"], "image": "https://assets.test/exhausted-cover", "thumbnails": {} }
                      ]
                    }
                    """);
            }

            if (uri.Equals(new Uri("https://assets.test/exhausted-cover")))
            {
                exhaustedHits++;
                return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
            }

            // release/front-500 and release-group fallbacks are also unavailable.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var exhaustedClient = new HttpClient(exhaustedHandler);
        using var exhaustedService = new MusicBrainzService(exhaustedClient);
        var exhaustedResult = exhaustedService.FetchTrackDataAsync(
                "Exhausted title", "Exhausted artist", "Exhausted album", "", TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();

        Check.NotNull(exhaustedResult);
        Check.Null(exhaustedResult!.CoverArtwork);
        // The indexed cover URL was attempted the full number of times before falling back.
        Check.Equal(3, exhaustedHits);
    }

    public static void EnforcesArtworkBoundaries()
    {
        var upgradedBytes = new byte[] { 2, 0, 2, 6 };
        var upgradeHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return RecordingReleaseResponse("Upgrade title", "Boundary artist", "upgrade-release");
            }

            if (uri.AbsolutePath.Equals("/release/upgrade-release/", StringComparison.Ordinal))
            {
                return FrontArtworkResponse("http://coverartarchive.org/release/upgrade-release/front-custom");
            }

            if (uri.AbsolutePath.Equals("/release/upgrade-release/front-custom", StringComparison.Ordinal))
            {
                Check.Equal(Uri.UriSchemeHttps, uri.Scheme);
                return Bytes(upgradedBytes);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using (var client = new HttpClient(upgradeHandler))
        using (var service = new MusicBrainzService(client, enforceTrustedArtworkHosts: true))
        {
            var result = service.FetchTrackDataAsync(
                    "Upgrade title", "Boundary artist", string.Empty, string.Empty, TimeSpan.Zero)
                .GetAwaiter()
                .GetResult();
            Check.NotNull(result);
            Check.Sequence(upgradedBytes, result!.CoverArtwork!);
            Check.Equal(ArtworkFetchOutcome.Retrieved, result.CoverOutcome);
        }

        var untrustedHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return RecordingReleaseResponse("Untrusted title", "Boundary artist", "untrusted-release");
            }

            if (uri.AbsolutePath.Equals("/release/untrusted-release/", StringComparison.Ordinal))
            {
                return FrontArtworkResponse("https://untrusted.example/cover");
            }

            if (uri.AbsolutePath.Equals("/release/untrusted-release/front-500", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            throw new InvalidOperationException("An untrusted artwork host was contacted: " + uri);
        });

        using (var client = new HttpClient(untrustedHandler))
        using (var service = new MusicBrainzService(client, enforceTrustedArtworkHosts: true))
        {
            var result = service.FetchTrackDataAsync(
                    "Untrusted title", "Boundary artist", string.Empty, string.Empty, TimeSpan.Zero)
                .GetAwaiter()
                .GetResult();
            Check.NotNull(result);
            Check.Null(result!.CoverArtwork);
            Check.Equal(ArtworkFetchOutcome.Failed, result.CoverOutcome);
        }

        var redirectHits = 0;
        var redirectHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return RecordingReleaseResponse("Redirect title", "Boundary artist", "redirect-release");
            }

            if (uri.AbsolutePath.Equals("/release/redirect-release/", StringComparison.Ordinal))
            {
                return FrontArtworkResponse("https://coverartarchive.org/release/redirect-release/loop");
            }

            if (uri.AbsolutePath.Equals("/release/redirect-release/loop", StringComparison.Ordinal))
            {
                redirectHits++;
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("/release/redirect-release/loop", UriKind.Relative);
                return response;
            }

            if (uri.AbsolutePath.Equals("/release/redirect-release/front-500", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using (var client = new HttpClient(redirectHandler))
        using (var service = new MusicBrainzService(client, enforceTrustedArtworkHosts: true))
        {
            var result = service.FetchTrackDataAsync(
                    "Redirect title", "Boundary artist", string.Empty, string.Empty, TimeSpan.Zero)
                .GetAwaiter()
                .GetResult();
            Check.NotNull(result);
            Check.Equal(ArtworkFetchOutcome.Failed, result!.CoverOutcome);
            Check.Equal(9, redirectHits);
        }

        var oversizedHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return RecordingReleaseResponse("Oversized title", "Boundary artist", "oversized-release");
            }

            if (uri.AbsolutePath.Equals("/release/oversized-release/", StringComparison.Ordinal))
            {
                return FrontArtworkResponse("https://coverartarchive.org/release/oversized-release/front-custom");
            }

            if (uri.AbsolutePath.Equals("/release/oversized-release/front-custom", StringComparison.Ordinal))
            {
                var response = Bytes([1]);
                response.Content.Headers.ContentLength = 21L * 1024 * 1024;
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return response;
            }

            if (uri.AbsolutePath.Equals("/release/oversized-release/front-500", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            throw new InvalidOperationException("Unexpected request: " + uri);
        });

        using (var client = new HttpClient(oversizedHandler))
        using (var service = new MusicBrainzService(client, enforceTrustedArtworkHosts: true))
        {
            var result = service.FetchTrackDataAsync(
                    "Oversized title", "Boundary artist", string.Empty, string.Empty, TimeSpan.Zero)
                .GetAwaiter()
                .GetResult();
            Check.NotNull(result);
            Check.Null(result!.CoverArtwork);
            Check.Equal(ArtworkFetchOutcome.Failed, result.CoverOutcome);
        }
    }

    private static HttpResponseMessage RecordingReleaseResponse(string title, string artist, string releaseId)
    {
        return Json(new Dictionary<string, object?>
        {
            ["recordings"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["score"] = 100,
                    ["title"] = title,
                    ["artist-credit"] = new object[]
                    {
                        new Dictionary<string, object?> { ["name"] = artist }
                    },
                    ["releases"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = releaseId,
                            ["title"] = title,
                            ["media"] = Array.Empty<object>()
                        }
                    }
                }
            }
        });
    }

    private static HttpResponseMessage FrontArtworkResponse(string imageUri)
    {
        return Json(new Dictionary<string, object?>
        {
            ["images"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["approved"] = true,
                    ["front"] = true,
                    ["types"] = new[] { "Front" },
                    ["image"] = imageUri,
                    ["thumbnails"] = new Dictionary<string, string>()
                }
            }
        });
    }

    public static void ReportsArtworkOutcomesAndDiagnostics()
    {
        // A release is found, but every cover endpoint errors -> Failed + diagnostics.
        var diagnostics = new CapturingArtworkDiagnostics();
        var failingHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "score": 100,
                        "title": "Outcome title",
                        "artist-credit": [{ "name": "Outcome artist" }],
                        "releases": [{ "id": "outcome-release", "title": "Outcome album", "release-group": { "id": "outcome-group" }, "media": [] }]
                      }]
                    }
                    """);
            }

            if (uri.AbsolutePath.Equals("/release/outcome-release/", StringComparison.Ordinal))
            {
                return JsonText("""
                    { "images": [ { "approved": true, "front": true, "types": ["Front"], "image": "https://assets.test/broken-cover", "thumbnails": {} } ] }
                    """);
            }

            // The indexed cover and every front-500 fallback fail with a server error.
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        using var failingClient = new HttpClient(failingHandler);
        using var failingService = new MusicBrainzService(failingClient, diagnostics: diagnostics);
        var failed = failingService.FetchTrackDataAsync(
                "Outcome title", "Outcome artist", "Outcome album", "", TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();

        Check.NotNull(failed);
        Check.Null(failed!.CoverArtwork);
        Check.Equal(ArtworkFetchOutcome.Failed, failed.CoverOutcome);
        Check.True(diagnostics.Entries.Count > 0);
        Check.True(diagnostics.Entries.Any(entry =>
            entry.Contains("cover-indexed", StringComparison.Ordinal)
            && entry.Contains("500", StringComparison.Ordinal)
            && entry.Contains("outcome-release", StringComparison.Ordinal)));

        // Cover Art Archive genuinely has no artwork -> NotAvailable, no failure logged.
        var absentDiagnostics = new CapturingArtworkDiagnostics();
        var absentHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
            {
                return JsonText("""
                    {
                      "recordings": [{
                        "score": 100,
                        "title": "Absent title",
                        "artist-credit": [{ "name": "Absent artist" }],
                        "releases": [{ "id": "absent-release", "title": "Absent album", "media": [] }]
                      }]
                    }
                    """);
            }

            // Release index and every front-500 endpoint report a genuine 404.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var absentClient = new HttpClient(absentHandler);
        using var absentService = new MusicBrainzService(absentClient, diagnostics: absentDiagnostics);
        var absent = absentService.FetchTrackDataAsync(
                "Absent title", "Absent artist", "Absent album", "", TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();

        Check.NotNull(absent);
        Check.Null(absent!.CoverArtwork);
        Check.Equal(ArtworkFetchOutcome.NotAvailable, absent.CoverOutcome);
        Check.Equal(0, absentDiagnostics.Entries.Count);
    }
}

static class ArtistArtworkServiceTests
{
    public static void ResolvesTrustedCommonsArtwork()
    {
        var png = Png(
            2,
            1,
            [
                10, 20, 30, 255,
                40, 50, 60, 255
            ]);
        var handler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            Check.Contains("Mystral/", request.Headers.UserAgent.ToString());
            if (uri.Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase))
            {
                Check.Equal("/w/api.php", uri.AbsolutePath);
                var query = Uri.UnescapeDataString(uri.Query);
                Check.Contains("prop=imageinfo", query);
                Check.Contains("iiurlwidth=512", query);
                Check.Contains("titles=File:Artist Photo.png", query);
                return CommonsMetadata("https://upload.wikimedia.org/wikipedia/commons/thumb/a/a1/Artist_Photo.png/512px-Artist_Photo.png");
            }

            Check.Equal("upload.wikimedia.org", uri.Host);
            Check.Contains("image/*", request.Headers.Accept.ToString());
            var response = Bytes(png);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return response;
        });

        using var client = new HttpClient(handler);
        using var service = new ArtistArtworkService(client);
        const string sourcePage = "https://commons.wikimedia.org/wiki/File:Artist%20Photo.png";
        var result = service.FetchAsync(" artist-123 ", sourcePage)
            .GetAwaiter()
            .GetResult();

        Check.NotNull(result);
        Check.Equal("artist-123", result!.ArtistId);
        Check.Sequence(png, result.Data);
        Check.Equal(sourcePage, result.SourcePageUrl);
        Check.Equal("Jane & Doe", result.Attribution);
        Check.Equal("CC BY-SA 4.0", result.LicenseName);
        Check.Equal("https://creativecommons.org/licenses/by-sa/4.0/", result.LicenseUrl);

        var cached = service.FetchAsync("ARTIST-123", sourcePage)
            .GetAwaiter()
            .GetResult();
        Check.Same(result, cached);
        Check.Equal(2, handler.Count);
    }

    public static void RejectsUnsafeAndInvalidResponses()
    {
        var untouchedHandler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("Unsafe input must not start a request."));
        using (var untouchedClient = new HttpClient(untouchedHandler))
        using (var service = new ArtistArtworkService(untouchedClient))
        {
            Check.Null(service.FetchAsync("artist", "http://commons.wikimedia.org/wiki/File:Photo.png")
                .GetAwaiter().GetResult());
            Check.Null(service.FetchAsync("artist", "https://commons.wikimedia.org.example/wiki/File:Photo.png")
                .GetAwaiter().GetResult());
            Check.Null(service.FetchAsync("artist", "https://commons.wikimedia.org/wiki/File:Photo.png?download=1")
                .GetAwaiter().GetResult());
            Check.Null(service.FetchAsync("artist", "https://commons.wikimedia.org/wiki/Category:Photo.png")
                .GetAwaiter().GetResult());
            Check.Null(service.FetchAsync(" ", "https://commons.wikimedia.org/wiki/File:Photo.png")
                .GetAwaiter().GetResult());
            Check.Equal(0, untouchedHandler.Count);
        }

        var untrustedThumbHandler = new FakeHttpMessageHandler(_ =>
            CommonsMetadata("https://evil.example/artist.png"));
        using (var untrustedThumbClient = new HttpClient(untrustedThumbHandler))
        using (var service = new ArtistArtworkService(untrustedThumbClient))
        {
            Check.Null(service.FetchAsync("artist-untrusted", "https://commons.wikimedia.org/wiki/File:Photo.png")
                .GetAwaiter().GetResult());
            Check.Equal(1, untrustedThumbHandler.Count);
        }

        var redirectHandler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example/w/api.php");
            return response;
        });
        using (var redirectClient = new HttpClient(redirectHandler))
        using (var service = new ArtistArtworkService(redirectClient))
        {
            Check.Null(service.FetchAsync("artist-redirect", "https://commons.wikimedia.org/wiki/File:Photo.png")
                .GetAwaiter().GetResult());
            Check.Equal(1, redirectHandler.Count);
        }

        var invalidImageHandler = new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            if (uri.Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase))
            {
                return CommonsMetadata("https://upload.wikimedia.org/wikipedia/commons/thumb/f/f1/Photo.png/512px-Photo.png");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not really an image", Encoding.UTF8, "image/png")
            };
        });
        using (var invalidImageClient = new HttpClient(invalidImageHandler))
        using (var service = new ArtistArtworkService(invalidImageClient))
        {
            Check.Null(service.FetchAsync("artist-invalid", "https://commons.wikimedia.org/wiki/File:Photo.png")
                .GetAwaiter().GetResult());
            Check.Equal(2, invalidImageHandler.Count);
        }

        var oversizedHandler = new FakeHttpMessageHandler(_ =>
        {
            var response = CommonsMetadata("https://upload.wikimedia.org/wikipedia/commons/photo.png");
            response.Content.Headers.ContentLength = 600_000;
            return response;
        });
        using (var oversizedClient = new HttpClient(oversizedHandler))
        using (var service = new ArtistArtworkService(oversizedClient))
        {
            Check.Null(service.FetchAsync("artist-oversized", "https://commons.wikimedia.org/wiki/File:Photo.png")
                .GetAwaiter().GetResult());
            Check.Equal(1, oversizedHandler.Count);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var cancellationClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("Cancelled input must not start a request.")));
        using var cancellationService = new ArtistArtworkService(cancellationClient);
        Check.Throws<OperationCanceledException>(() =>
            cancellationService.FetchAsync(
                    "artist-cancelled",
                    "https://commons.wikimedia.org/wiki/File:Photo.png",
                    cancelled.Token)
                .GetAwaiter()
                .GetResult());
    }

    private static HttpResponseMessage CommonsMetadata(string thumbnailUrl)
    {
        return Json(new
        {
            query = new
            {
                pages = new[]
                {
                    new
                    {
                        imageinfo = new[]
                        {
                            new
                            {
                                thumburl = thumbnailUrl,
                                thumbwidth = 2,
                                thumbheight = 1,
                                mime = "image/png",
                                thumbmime = "image/png",
                                extmetadata = new Dictionary<string, object>
                                {
                                    ["Artist"] = new { value = "<b>Jane &amp; Doe</b>" },
                                    ["Credit"] = new { value = "Alternate credit" },
                                    ["LicenseShortName"] = new { value = "CC BY-SA 4.0" },
                                    ["LicenseUrl"] = new { value = "https://creativecommons.org/licenses/by-sa/4.0/" }
                                }
                            }
                        }
                    }
                }
            }
        });
    }
}

static class ArtworkDiagnosticsTests
{
    public static void WritesAndRotatesBoundedLog()
    {
        using var temp = TempDir.Create();
        var logPath = Path.Combine(temp.Path, "logs", "artwork.log");
        var diagnostics = new Mystral.Services.FileArtworkDiagnostics(logPath);

        diagnostics.RecordArtworkFailure(
            "cover-indexed", "rel-1", "coverartarchive.org", System.Net.HttpStatusCode.ServiceUnavailable, "HttpRequestException");
        Check.True(File.Exists(logPath));
        var contents = File.ReadAllText(logPath);
        Check.Contains("stage=cover-indexed", contents);
        Check.Contains("release=rel-1", contents);
        Check.Contains("host=coverartarchive.org", contents);
        Check.Contains("status=503", contents);
        Check.Contains("error=HttpRequestException", contents);

        // Pre-fill beyond the 1 MB cap so the next write rotates into artwork.log.1.
        File.WriteAllText(logPath, new string('x', (1024 * 1024) + 16));
        diagnostics.RecordArtworkFailure("disc", null, null, null, "IOException");
        Check.True(File.Exists(logPath + ".1"));
        Check.True(new FileInfo(logPath).Length < 1024 * 1024);
        Check.Contains("stage=disc", File.ReadAllText(logPath));

        // A directory that cannot be created (a file sits where the folder should be)
        // must be swallowed, never thrown into the fetch path.
        var blocker = Path.Combine(temp.Path, "blocker");
        File.WriteAllText(blocker, "x");
        var badDiagnostics = new Mystral.Services.FileArtworkDiagnostics(Path.Combine(blocker, "artwork.log"));
        badDiagnostics.RecordArtworkFailure("cover-indexed", null, null, null, "Exception");
    }
}

static class GitHubReleaseLinksTests
{
    public static void BuildsSafeCompareUris()
    {
        var stableCompare = GitHubReleaseLinks.CreateCompareUri(
            " v2.1.0 ",
            "2.1.1");
        Check.NotNull(stableCompare);
        Check.Equal(
            "https://github.com/ponkis/mystral/compare/v2.1.0...v2.1.1",
            stableCompare!.AbsoluteUri);

        var prereleaseCompare = GitHubReleaseLinks.CreateCompareUri(
            "2.2.0-beta.2",
            "v2.2.0-beta.10");
        Check.NotNull(prereleaseCompare);
        Check.Equal(
            "https://github.com/ponkis/mystral/compare/v2.2.0-beta.2...v2.2.0-beta.10",
            prereleaseCompare!.AbsoluteUri);

        var stablePromotionCompare = GitHubReleaseLinks.CreateCompareUri(
            "2.2.0-rc.1",
            "2.2.0");
        Check.NotNull(stablePromotionCompare);
        Check.Equal(
            "https://github.com/ponkis/mystral/compare/v2.2.0-rc.1...v2.2.0",
            stablePromotionCompare!.AbsoluteUri);

        Check.Null(GitHubReleaseLinks.CreateCompareUri("2.1.0-dev", "2.1.1"));
        Check.Null(GitHubReleaseLinks.CreateCompareUri("2.1.0", "2.1.1-dev"));
        Check.Null(GitHubReleaseLinks.CreateCompareUri("2.1.0", "not a version"));
        Check.Null(GitHubReleaseLinks.CreateCompareUri("2.1.0", "2.1.0/../../main"));
        Check.Null(GitHubReleaseLinks.CreateCompareUri("v2.1.0", "2.1.0"));
        Check.Null(GitHubReleaseLinks.CreateCompareUri("2.1.1", "2.1.0"));
        Check.Null(GitHubReleaseLinks.CreateCompareUri("2.2.0", "2.2.0-rc.1"));
    }
}

static class UpdateDownloadTests
{
    public static void ValidatesInterruptedDownloadsAndFailureMessages()
    {
        var assetName = $"mystral-update-test-{Guid.NewGuid():N}.exe";
        var expectedPath = Path.Combine(Path.GetTempPath(), assetName);
        var response = Samples.Bytes([1, 2, 3]);
        response.Content.Headers.ContentLength = 10;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => response));

        IOException? interrupted = null;
        try
        {
            _ = SettingsWindow.DownloadInstallerAsync(
                    client,
                    "https://example.test/update.exe",
                    assetName,
                    (_, _) => { },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (IOException ex)
        {
            interrupted = ex;
        }

        Check.NotNull(interrupted);
        Check.Contains("connection closed", interrupted!.Message);
        Check.False(File.Exists(expectedPath));
        Check.Equal(
            "The download timed out.",
            SettingsWindow.DescribeUpdateDownloadFailure(new TaskCanceledException()));
        Check.Equal(
            "connection reset by peer",
            SettingsWindow.DescribeUpdateDownloadFailure(
                new HttpRequestException("Request failed.", new IOException("connection reset by peer"))));
    }
}

static class BurnLyricsFetchTests
{
    public static void ReplacesDefinitiveResultsAndPreservesFailures()
    {
        var plain = BurningWindow.CreateFetchedLyricsReplacement(
            LyricsResult.Plain(["New plain line"], sourceText: "New plain line"));
        Check.True(plain.ShouldReplace);
        Check.Equal("New plain line", plain.Unsynchronized);
        Check.Equal(string.Empty, plain.Synchronized);

        var synced = BurningWindow.CreateFetchedLyricsReplacement(
            LyricsResult.Synced([new LyricLine(TimeSpan.FromSeconds(1.25), "New synced line")]));
        Check.True(synced.ShouldReplace);
        Check.Equal(string.Empty, synced.Unsynchronized);
        Check.Equal("[00:01.25]New synced line", synced.Synchronized);

        var notFound = BurningWindow.CreateFetchedLyricsReplacement(LyricsResult.NotFound);
        Check.True(notFound.ShouldReplace);
        Check.Equal(string.Empty, notFound.Unsynchronized);
        Check.Equal(string.Empty, notFound.Synchronized);

        var instrumental = BurningWindow.CreateFetchedLyricsReplacement(LyricsResult.Instrumental);
        Check.True(instrumental.ShouldReplace);
        Check.Equal(string.Empty, instrumental.Unsynchronized);
        Check.Equal(string.Empty, instrumental.Synchronized);

        var failed = BurningWindow.CreateFetchedLyricsReplacement(LyricsResult.Error("Unavailable"));
        Check.False(failed.ShouldReplace);
    }
}

static class BurnMetadataValidationTests
{
    public static void ValidatesFieldsAndSuggestedFileNames()
    {
        var draft = new BurnTrackDraft
        {
            SourcePath = @"C:\Music\original.flac",
            Title = new string('T', BurnTrackDraft.MaxTitleLength),
            Artist = new string('A', BurnTrackDraft.MaxArtistLength),
            Album = new string('L', BurnTrackDraft.MaxAlbumLength),
            Genre = new string('G', BurnTrackDraft.MaxGenreLength),
            Year = "2026",
            TrackNumber = "9998",
            TrackTotal = "9999"
        };

        AudioTagService.ValidateDraft(draft);

        draft.Title += "x";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.Title = "AC/DC: Live?";

        draft.Artist += "x";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.Artist = "Artist";

        draft.Album += "x";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.Album = "Album";

        draft.Genre += "x";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.Genre = "Genre";

        draft.Year = "20A6";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.Year = "2026";

        draft.TrackNumber = "seven";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.TrackNumber = "0";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.TrackNumber = "12";
        draft.TrackTotal = "11";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));

        draft.TrackNumber = "7";
        draft.TrackTotal = "12";
        AudioTagService.ValidateDraft(draft);

        draft.SynchronizedLyrics = "A line without an LRC timestamp";
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.SynchronizedLyrics = "[00:01.25]A timestamped line";
        AudioTagService.ValidateDraft(draft);
        draft.SynchronizedLyrics = string.Empty;

        draft.UnsynchronizedLyrics = new string('L', BurnTrackDraft.MaxLyricsLength + 1);
        Check.Throws<InvalidDataException>(() => AudioTagService.ValidateDraft(draft));
        draft.UnsynchronizedLyrics = string.Empty;

        Check.Equal("AC_DC_ Live_.flac", AudioTagService.CreateSuggestedOutputFileName(draft));

        draft.Title = "CON";
        Check.Equal("_CON.flac", AudioTagService.CreateSuggestedOutputFileName(draft));
        draft.Title = "  ...  ";
        Check.Equal("Untitled.flac", AudioTagService.CreateSuggestedOutputFileName(draft));
        draft.Title = new string('x', 300);
        var boundedName = AudioTagService.CreateSuggestedOutputFileName(draft);
        Check.Equal(255, boundedName.Length);
        Check.True(boundedName.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
    }
}

static class AudioTagServiceTests
{
    public static void ReadsAndSavesMetadataWithoutTouchingSource()
    {
        using var temp = TempDir.Create();
        var sourcePath = Path.Combine(temp.Path, "original.wav");
        var sourceBytes = PcmWave(duration: TimeSpan.FromSeconds(1));
        File.WriteAllBytes(sourcePath, sourceBytes);

        var service = new AudioTagService();
        var draft = service.ReadAsync(sourcePath).GetAwaiter().GetResult();

        Check.Equal(Path.GetFullPath(sourcePath), draft.SourcePath);
        Check.True(draft.Duration >= TimeSpan.FromMilliseconds(990));
        Check.True(draft.Duration <= TimeSpan.FromMilliseconds(1010));
        Check.Equal("", draft.Title);
        Check.Equal("", draft.Artist);

        draft.Title = "Burned title";
        draft.Artist = "Burned artist";
        draft.Genre = "Rock";
        draft.Year = "1995";
        draft.Album = "Burned album";
        draft.TrackNumber = "7";
        draft.TrackTotal = "12";
        draft.UnsynchronizedLyrics = "Plain line one\nPlain line two";
        draft.SynchronizedLyrics = "[00:00.25]Synced line one\n[00:00.75]Synced line two";
        var artworkLoader = new ImageArtworkLoader();
        var coverPng = Png(1, 1, [10, 20, 30, 255]);
        var discPng = Png(1, 1, [40, 50, 60, 255]);
        draft.CoverArtwork = artworkLoader.LoadAsync(coverPng).GetAwaiter().GetResult();
        draft.DiscArtwork = artworkLoader.LoadAsync(discPng).GetAwaiter().GetResult();
        draft.CoverArtworkChanged = true;
        draft.DiscArtworkChanged = true;
        draft.LyricsChanged = true;
        var destinationPath = Path.Combine(temp.Path, "burned.wav");

        service.SaveCopyAsync(draft, destinationPath).GetAwaiter().GetResult();

        Check.Sequence(sourceBytes, File.ReadAllBytes(sourcePath));
        Check.True(File.Exists(destinationPath));
        var saved = service.ReadAsync(destinationPath).GetAwaiter().GetResult();
        Check.Equal("Burned title", saved.Title);
        Check.Equal("Burned artist", saved.Artist);
        Check.Equal("Rock", saved.Genre);
        Check.Equal("1995", saved.Year);
        Check.Equal("Burned album", saved.Album);
        Check.Equal("7", saved.TrackNumber);
        Check.Equal("12", saved.TrackTotal);
        Check.Equal("Plain line one\nPlain line two", saved.UnsynchronizedLyrics);
        Check.Contains("[00:00.25]Synced line one", saved.SynchronizedLyrics);
        Check.Contains("[00:00.75]Synced line two", saved.SynchronizedLyrics);
        Check.NotNull(saved.CoverArtwork);
        Check.NotNull(saved.DiscArtwork);

        // The cover is the only embedded picture and it is a front cover. The disc art
        // must NOT be an embedded picture (players would render it as the cover);
        // it lives in a private tag field instead.
        var savedTrack = new ATL.Track(destinationPath);
        Check.Equal(1, savedTrack.EmbeddedPictures.Count);
        Check.Equal(ATL.PictureInfo.PIC_TYPE.Front, savedTrack.EmbeddedPictures[0].PicType);
        Check.False(savedTrack.EmbeddedPictures.Any(picture =>
            picture.PicType == ATL.PictureInfo.PIC_TYPE.CD));
        Check.True(savedTrack.Lyrics.Any(lyrics =>
            lyrics.UnsynchronizedLyrics.Contains("Plain line one", StringComparison.Ordinal)));
        Check.True(savedTrack.Lyrics.Any(lyrics => lyrics.SynchronizedLyrics.Count >= 2));
        var transcriptionEntry = new ATL.LyricsInfo
        {
            ContentType = ATL.LyricsInfo.LyricsType.TRANSCRIPTION,
            LanguageCode = "und",
            Description = "Preserve this entry",
            Format = ATL.LyricsInfo.LyricsFormat.SYNCHRONIZED
        };
        transcriptionEntry.SynchronizedLyrics.Add(
            new ATL.LyricsInfo.LyricsPhrase(500, "Non-lyrical timed metadata"));
        savedTrack.Lyrics.Add(transcriptionEntry);
        Check.True(savedTrack.Save());
        var discField = savedTrack.AdditionalFields
            .FirstOrDefault(field => field.Key.Contains("MYSTRAL_DISC_ART", StringComparison.OrdinalIgnoreCase))
            .Value;
        Check.NotNull(discField);
        Check.Sequence(discPng, Convert.FromBase64String(discField!));

        // Replacing only the cover must keep exactly one front-cover picture and leave
        // the disc art field untouched (no CD picture is ever introduced).
        var replacedCoverBytes = Png(1, 1, [11, 22, 33, 255]);
        saved.CoverArtwork = artworkLoader.LoadAsync(replacedCoverBytes).GetAwaiter().GetResult();
        saved.CoverArtworkChanged = true;
        saved.DiscArtworkChanged = false;
        var coverReplacedPath = Path.Combine(temp.Path, "cover-replaced.wav");
        service.SaveCopyAsync(saved, coverReplacedPath).GetAwaiter().GetResult();
        var coverReplacedTrack = new ATL.Track(coverReplacedPath);
        Check.Equal(1, coverReplacedTrack.EmbeddedPictures.Count);
        Check.Equal(ATL.PictureInfo.PIC_TYPE.Front, coverReplacedTrack.EmbeddedPictures[0].PicType);
        Check.True(coverReplacedTrack.EmbeddedPictures[0].PictureData.AsSpan().SequenceEqual(replacedCoverBytes));
        Check.False(coverReplacedTrack.EmbeddedPictures.Any(picture =>
            picture.PicType == ATL.PictureInfo.PIC_TYPE.CD));
        var coverReplacedReadback = service.ReadAsync(coverReplacedPath).GetAwaiter().GetResult();
        Check.NotNull(coverReplacedReadback.DiscArtwork);
        Check.Equal("Plain line one\nPlain line two", coverReplacedReadback.UnsynchronizedLyrics);
        Check.Contains("Synced line one", coverReplacedReadback.SynchronizedLyrics);

        // Clearing both editors explicitly removes lyrical entries, while an
        // unchanged draft leaves the source file's original lyric frames alone.
        saved.UnsynchronizedLyrics = string.Empty;
        saved.SynchronizedLyrics = string.Empty;
        saved.LyricsChanged = true;
        var lyricsRemovedPath = Path.Combine(temp.Path, "lyrics-removed.wav");
        service.SaveCopyAsync(saved, lyricsRemovedPath).GetAwaiter().GetResult();
        var lyricsRemoved = service.ReadAsync(lyricsRemovedPath).GetAwaiter().GetResult();
        Check.Equal(string.Empty, lyricsRemoved.UnsynchronizedLyrics);
        Check.Equal(string.Empty, lyricsRemoved.SynchronizedLyrics);
        var lyricsRemovedTrack = new ATL.Track(lyricsRemovedPath);
        Check.False(lyricsRemovedTrack.Lyrics.Any(lyrics =>
            lyrics.ContentType == ATL.LyricsInfo.LyricsType.LYRICS
            && (!string.IsNullOrEmpty(lyrics.UnsynchronizedLyrics)
                || lyrics.SynchronizedLyrics.Count > 0)));
        Check.True(lyricsRemovedTrack.Lyrics.Any(lyrics =>
            lyrics.ContentType == ATL.LyricsInfo.LyricsType.TRANSCRIPTION
            && lyrics.SynchronizedLyrics.Any(phrase => phrase.Text.Contains(
                "Non-lyrical timed metadata",
                StringComparison.Ordinal))));

        // Backward compatibility: a legacy file that stored the disc art as a CD
        // picture must still be read as disc art (not as the cover).
        var legacyPath = Path.Combine(temp.Path, "legacy-disc.wav");
        File.WriteAllBytes(legacyPath, PcmWave(duration: TimeSpan.FromSeconds(1)));
        var legacyCoverPng = Png(1, 1, [1, 2, 3, 255]);
        var legacyDiscPng = Png(1, 1, [4, 5, 6, 255]);
        var legacyTrack = new ATL.Track(legacyPath);
        legacyTrack.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(legacyCoverPng, ATL.PictureInfo.PIC_TYPE.Front));
        legacyTrack.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(legacyDiscPng, ATL.PictureInfo.PIC_TYPE.CD));
        Check.True(legacyTrack.Save());
        var legacyRead = service.ReadAsync(legacyPath).GetAwaiter().GetResult();
        Check.NotNull(legacyRead.CoverArtwork);
        Check.Sequence(legacyCoverPng, legacyRead.CoverArtwork!.Data);
        Check.NotNull(legacyRead.DiscArtwork);
        Check.Sequence(legacyDiscPng, legacyRead.DiscArtwork!.Data);
        // Re-saving a legacy file migrates the disc art out of the CD picture.
        legacyRead.DiscArtworkChanged = true;
        var legacyMigratedPath = Path.Combine(temp.Path, "legacy-migrated.wav");
        service.SaveCopyAsync(legacyRead, legacyMigratedPath).GetAwaiter().GetResult();
        var legacyMigratedTrack = new ATL.Track(legacyMigratedPath);
        Check.False(legacyMigratedTrack.EmbeddedPictures.Any(picture =>
            picture.PicType == ATL.PictureInfo.PIC_TYPE.CD));

        saved.CoverArtwork = null;
        saved.DiscArtwork = null;
        saved.CoverArtworkChanged = true;
        saved.DiscArtworkChanged = true;
        var artworkRemovedPath = Path.Combine(temp.Path, "artwork-removed.wav");
        service.SaveCopyAsync(saved, artworkRemovedPath).GetAwaiter().GetResult();
        var artworkRemoved = service.ReadAsync(artworkRemovedPath).GetAwaiter().GetResult();
        Check.Null(artworkRemoved.CoverArtwork);
        Check.Null(artworkRemoved.DiscArtwork);

        var fallbackPath = Path.Combine(temp.Path, "fallback-artwork.wav");
        File.WriteAllBytes(fallbackPath, PcmWave(duration: TimeSpan.FromSeconds(1)));
        var displayedFallback = Png(1, 1, [70, 80, 90, 255]);
        var unrelatedArtwork = Png(1, 1, [100, 110, 120, 255]);
        var fallbackTrack = new ATL.Track(fallbackPath);
        fallbackTrack.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(displayedFallback, ATL.PictureInfo.PIC_TYPE.Back));
        fallbackTrack.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(unrelatedArtwork, ATL.PictureInfo.PIC_TYPE.Leaflet));
        Check.True(fallbackTrack.Save());

        var fallbackDraft = service.ReadAsync(fallbackPath).GetAwaiter().GetResult();
        Check.True(fallbackDraft.CoverArtworkOriginalType == ATL.PictureInfo.PIC_TYPE.Back);
        fallbackDraft.CoverArtwork = null;
        fallbackDraft.CoverArtworkChanged = true;
        var fallbackRemovedPath = Path.Combine(temp.Path, "fallback-artwork-removed.wav");
        service.SaveCopyAsync(fallbackDraft, fallbackRemovedPath).GetAwaiter().GetResult();

        var fallbackRemovedTrack = new ATL.Track(fallbackRemovedPath);
        Check.False(fallbackRemovedTrack.EmbeddedPictures.Any(picture => picture.PicType == ATL.PictureInfo.PIC_TYPE.Back));
        Check.True(fallbackRemovedTrack.EmbeddedPictures.Any(picture =>
            picture.PicType == ATL.PictureInfo.PIC_TYPE.Leaflet
            && picture.PictureData.AsSpan().SequenceEqual(unrelatedArtwork)));

        var replacementArtwork = Png(1, 1, [130, 140, 150, 255]);
        var replacementDraft = service.ReadAsync(fallbackPath).GetAwaiter().GetResult();
        replacementDraft.CoverArtwork = new ImageArtworkLoader()
            .LoadAsync(replacementArtwork)
            .GetAwaiter()
            .GetResult();
        replacementDraft.CoverArtworkChanged = true;
        var fallbackReplacedPath = Path.Combine(temp.Path, "fallback-artwork-replaced.wav");
        service.SaveCopyAsync(replacementDraft, fallbackReplacedPath).GetAwaiter().GetResult();

        var fallbackReplacedTrack = new ATL.Track(fallbackReplacedPath);
        Check.True(fallbackReplacedTrack.EmbeddedPictures.Any(picture =>
            picture.PicType == ATL.PictureInfo.PIC_TYPE.Back
            && picture.PictureData.AsSpan().SequenceEqual(displayedFallback)));
        Check.True(fallbackReplacedTrack.EmbeddedPictures.Any(picture =>
            picture.PicType == ATL.PictureInfo.PIC_TYPE.Leaflet
            && picture.PictureData.AsSpan().SequenceEqual(unrelatedArtwork)));
        Check.True(fallbackReplacedTrack.EmbeddedPictures.Any(picture =>
            picture.PicType == ATL.PictureInfo.PIC_TYPE.Front
            && picture.PictureData.AsSpan().SequenceEqual(replacementArtwork)));

        draft.Year = "2001-04";
        var invalidDatePath = Path.Combine(temp.Path, "invalid-year.wav");
        Check.Throws<InvalidDataException>(() =>
            service.SaveCopyAsync(draft, invalidDatePath).GetAwaiter().GetResult());
        Check.False(File.Exists(invalidDatePath));

        draft.Year = "2001";
        draft.TrackTotal = "6";
        var invalidTrackTotalPath = Path.Combine(temp.Path, "invalid-track-total.wav");
        Check.Throws<InvalidDataException>(() =>
            service.SaveCopyAsync(draft, invalidTrackTotalPath).GetAwaiter().GetResult());
        Check.False(File.Exists(invalidTrackTotalPath));

        draft.TrackTotal = "twelve";
        var malformedTrackTotalPath = Path.Combine(temp.Path, "malformed-track-total.wav");
        Check.Throws<InvalidDataException>(() =>
            service.SaveCopyAsync(draft, malformedTrackTotalPath).GetAwaiter().GetResult());
        Check.False(File.Exists(malformedTrackTotalPath));
        Check.False(Directory.EnumerateFiles(temp.Path, "*.mystral*", SearchOption.TopDirectoryOnly).Any());

        var disguisedText = Path.Combine(temp.Path, "not-audio.wav");
        File.WriteAllText(disguisedText, "RIFF is not enough to make this audio");
        Check.Throws<InvalidDataException>(() =>
            service.ReadAsync(disguisedText).GetAwaiter().GetResult());
    }
}

static class Samples
{
    public static MediaSnapshot Snapshot(
        string title = "Song",
        string artist = "Artist",
        string album = "Album",
        TimeSpan? duration = null)
    {
        return new MediaSnapshot(
            HasSession: true,
            Title: title,
            Artist: artist,
            Album: album,
            SourceApp: "App",
            Description: "Description",
            StatusText: "Playing",
            Position: TimeSpan.Zero,
            Duration: duration ?? TimeSpan.FromMinutes(3),
            IsPlaying: true,
            CanPlay: true,
            CanPause: true,
            CanNext: true,
            CanPrevious: true,
            CanSeek: true,
            CoverArt: null);
    }

    public static LastFmTrackInfo Track()
    {
        return new LastFmTrackInfo("Song", "Artist", "https://last.fm/track", "Album", TimeSpan.FromSeconds(123));
    }

    public static ScrobbleRecord Record(string title, long timestamp)
    {
        return new ScrobbleRecord
        {
            Title = title,
            Artist = "Artist",
            Album = "Album",
            Timestamp = timestamp,
            Duration = 180
        };
    }

    public static HttpResponseMessage Json(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage JsonText(string value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage Bytes(byte[] value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(value)
        };
    }

    public static byte[] Png(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] PcmWave(TimeSpan duration, int sampleRate = 8000)
    {
        const short channelCount = 1;
        const short bitsPerSample = 16;
        var sampleCount = checked((int)Math.Round(duration.TotalSeconds * sampleRate));
        var dataLength = checked(sampleCount * channelCount * (bitsPerSample / 8));
        using var stream = new MemoryStream(capacity: 44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channelCount * (bitsPerSample / 8));
        writer.Write((short)(channelCount * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();
        return stream.ToArray();
    }
}

sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Count { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++;
        return Task.FromResult(respond(request));
    }
}

sealed class CapturingArtworkDiagnostics : Mystral.Services.IArtworkDiagnostics
{
    public List<string> Entries { get; } = [];

    public void RecordArtworkFailure(
        string stage,
        string? releaseId,
        string? host,
        System.Net.HttpStatusCode? statusCode,
        string exceptionType)
    {
        Entries.Add(string.Join(
            '|',
            stage,
            releaseId ?? "-",
            host ?? "-",
            statusCode is { } code ? ((int)code).ToString() : "-",
            exceptionType));
    }
}

sealed class MemorySecureCredentialStore : ISecureCredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Read(string key)
    {
        lock (_values)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    public void Write(string key, string value)
    {
        lock (_values)
        {
            _values[key] = value;
        }
    }

    public void Delete(string key)
    {
        lock (_values)
        {
            _values.Remove(key);
        }
    }
}

sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mystral-tests-" + Guid.NewGuid().ToString("N"));

    private TempDir()
    {
        Directory.CreateDirectory(Path);
    }

    public static TempDir Create()
    {
        return new TempDir();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

static class Check
{
    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
        }
    }

    public static void Equal<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        if (expected.Count != actual.Count || expected.Where((item, index) => !EqualityComparer<T>.Default.Equals(item, actual[index])).Any())
        {
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    public static void True(bool value, string? message = null)
    {
        if (!value)
        {
            throw new InvalidOperationException(message ?? "Expected true.");
        }
    }

    public static void False(bool value)
    {
        if (value)
        {
            throw new InvalidOperationException("Expected false.");
        }
    }

    public static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got {value}.");
        }
    }

    public static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a value.");
        }
    }

    public static void Same(object? expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException("Expected same instance.");
        }
    }

    public static void Empty<T>(IEnumerable<T> values)
    {
        if (values.Any())
        {
            throw new InvalidOperationException("Expected empty sequence.");
        }
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (expectedArray.Length != actualArray.Length
            || expectedArray.Where((item, index) => !EqualityComparer<T>.Default.Equals(item, actualArray[index])).Any())
        {
            throw new InvalidOperationException($"Expected [{string.Join(", ", expectedArray)}], got [{string.Join(", ", actualArray)}].");
        }
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
