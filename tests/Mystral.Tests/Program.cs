using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mystral.Models;
using Mystral.Parsing;
using Mystral.Services;
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
            ("settings service persists normalized settings and recovers from bad files", AppSettingsServiceTests.PersistsNormalizesAndFallsBack),
            ("local scrobble cache adds, removes, caps, and tolerates corrupt files", LocalScrobbleCacheServiceTests.ManagesHistory),
            ("lyrics service keys, searches, ranks, parses, and caches results", LyricsServiceTests.FetchesBestLyricsAndCaches),
            ("Last.fm service validates, fetches, scrobbles, signs, and caches", LastFmServiceTests.UsesLastFmApiSafely),
            ("models expose expected defaults and computed properties", ModelTests.DefaultsAndComputedPropertiesAreStable),
            ("artwork tint clamps colors and extracts usable dominant tints", ArtworkTintTests.ColorHelpersAreStable),
            ("CD artwork compositor masks and blends the Photoshop layer stack", CdArtworkComposerTests.ComposesMaskedLayerStack),
            ("artwork loader validates decoded image content instead of extensions", ImageArtworkLoaderTests.ValidatesDecodedImageContent),
            ("MusicBrainz maps the best recording, release, cover, and medium artwork", MusicBrainzServiceTests.MapsRecordingAndArtworkResponses),
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
    public static void PersistsNormalizesAndFallsBack()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Path, "settings.json");
        var service = new AppSettingsService(path);
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
            Behavior = new BehaviorSettings { AlwaysOnTop = false }
        });

        Check.Equal(1, changed);
        Check.Equal("key", service.Settings.LastFm.ApiKey);
        Check.Equal("secret", service.Settings.LastFm.ApiSecret);
        Check.Equal("user", service.Settings.LastFm.Username);
        Check.Equal(" pass ", service.Settings.LastFm.Password);
        Check.False(service.Settings.Behavior.AlwaysOnTop);

        var reloaded = new AppSettingsService(path);
        Check.True(reloaded.Settings.LastFm.IsConfigured);
        Check.False(reloaded.Settings.Behavior.AlwaysOnTop);

        File.WriteAllText(path, """{"LastFm":null,"Behavior":null}""");
        var nullNested = new AppSettingsService(path);
        Check.NotNull(nullNested.Settings.LastFm);
        Check.NotNull(nullNested.Settings.Behavior);

        File.WriteAllText(path, "{ nope");
        var corrupt = new AppSettingsService(path);
        Check.False(corrupt.Settings.LastFm.IsConfigured);
        Check.True(corrupt.Settings.Behavior.AlwaysOnTop);
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

static class LyricsServiceTests
{
    public static void FetchesBestLyricsAndCaches()
    {
        Check.Equal("", LyricsService.CreateTrackKey(MediaSnapshot.Empty));
        Check.Equal("song|artist|album", LyricsService.CreateTrackKey(Snapshot(" Song ", " Artist ", " Album ")));

        var handler = new FakeHttpMessageHandler(request =>
        {
            Check.Equal(HttpMethod.Get, request.Method);
            Check.Contains("/api/search?", request.RequestUri!.PathAndQuery);
            Check.Contains("track_name=Song", request.RequestUri!.Query);
            Check.Contains("artist_name=Artist", request.RequestUri!.Query);
            return Json(new[]
            {
                new { id = 1, trackName = "Song", artistName = "Artist", albumName = "Album", duration = 120d, instrumental = false, plainLyrics = "plain", syncedLyrics = (string?)null },
                new { id = 2, trackName = "Song", artistName = "Artist", albumName = "Album", duration = 121d, instrumental = false, plainLyrics = "", syncedLyrics = (string?)"[00:01.00]Line" },
                new { id = 3, trackName = "Song", artistName = "Artist", albumName = "Album", duration = 119d, instrumental = false, plainLyrics = "", syncedLyrics = (string?)"" }
            });
        });

        using var service = new LyricsService(new HttpClient(handler));
        var snapshot = Snapshot("Song - Official Video", "Artist", "Album", TimeSpan.FromSeconds(121));
        var result = service.GetLyricsAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();
        var cached = service.GetLyricsAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();

        Check.Same(result, cached);
        Check.Equal(1, handler.Count);
        Check.Equal(LyricsStatus.Synced, result.Status);
        Check.Equal("Line", result.SyncedLines.Single().Text);
        Check.Equal("LRCLIB", result.TrackInfo!.SourceName);

        using var emptyService = new LyricsService(new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("should not call"))));
        Check.Equal(LyricsStatus.Empty, emptyService.GetLyricsAsync(MediaSnapshot.Empty, CancellationToken.None).GetAwaiter().GetResult().Status);

        using var plainService = new LyricsService(new HttpClient(new FakeHttpMessageHandler(_ => Json(new[]
        {
            new { id = 4, trackName = "Song", artistName = "Artist", albumName = "", duration = 0d, instrumental = false, plainLyrics = "\r\n one \n\n two ", syncedLyrics = (string?)null }
        }))));
        var plain = plainService.GetLyricsAsync(Snapshot("Song", "Artist", "", TimeSpan.Zero), CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(LyricsStatus.Plain, plain.Status);
        Check.Sequence(["one", "two"], plain.PlainLines);

        using var instrumentalService = new LyricsService(new HttpClient(new FakeHttpMessageHandler(_ => Json(new[]
        {
            new { id = 5, trackName = "Song", artistName = "Artist", albumName = "", duration = 0d, instrumental = true, plainLyrics = "", syncedLyrics = "" }
        }))));
        Check.Equal(LyricsStatus.Instrumental, instrumentalService.GetLyricsAsync(Snapshot("Song", "Artist", "", TimeSpan.Zero), CancellationToken.None).GetAwaiter().GetResult().Status);
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
        Check.Equal("Fill in API key, API secret, username, and password.", unconfigured.ValidateCredentialsAsync(new LastFmCredentials()).GetAwaiter().GetResult().Message);
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
        Check.False(new LastFmCredentials { Enabled = true, ApiKey = "a", ApiSecret = "s", Username = "u" }.IsConfigured);
        Check.True(new LastFmCredentials { Enabled = true, ApiKey = "a", ApiSecret = "s", Username = "u", Password = "p" }.IsConfigured);
        Check.True(new BehaviorSettings().CloseToTray);
        Check.Equal("No active track", MediaSnapshot.Empty.Title);
        Check.False(MediaSnapshot.Empty.HasSession);
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(0).LocalDateTime.ToString("yyyy-MM-dd"), new ScrobbleRecord { Timestamp = 0 }.FormattedTime[..10]);
        Check.False(new ScrobbleRecord().IsSelected);

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
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
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
        Check.Null(partialArtworkResult!.CoverArtwork);
        Check.Sequence(survivingDiscBytes, partialArtworkResult.DiscArtwork!);
        Check.Equal(4, partialArtworkHandler.Count);

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
        var artworkLoader = new ImageArtworkLoader();
        draft.CoverArtwork = artworkLoader.LoadAsync(Png(1, 1, [10, 20, 30, 255])).GetAwaiter().GetResult();
        draft.DiscArtwork = artworkLoader.LoadAsync(Png(1, 1, [40, 50, 60, 255])).GetAwaiter().GetResult();
        draft.CoverArtworkChanged = true;
        draft.DiscArtworkChanged = true;
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
        Check.NotNull(saved.CoverArtwork);
        Check.NotNull(saved.DiscArtwork);

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
