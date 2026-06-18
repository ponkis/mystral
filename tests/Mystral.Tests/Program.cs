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
            ("artwork tint clamps colors and extracts usable dominant tints", ArtworkTintTests.ColorHelpersAreStable)
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
}
