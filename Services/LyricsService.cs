using System.Net;
using System.Net.Http;
using System.Text.Json;
using Mystral.Configuration;
using Mystral.Models;
using Mystral.Parsing;

namespace Mystral.Services;

public sealed class LyricsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, LyricsResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LyricsService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net"),
            Timeout = TimeSpan.FromSeconds(28)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
    }

    public static string CreateTrackKey(MediaSnapshot snapshot)
    {
        if (!snapshot.HasSession)
        {
            return string.Empty;
        }

        return $"{NormalizeKey(snapshot.Title)}|{NormalizeKey(snapshot.Artist)}|{NormalizeKey(snapshot.Album)}";
    }

    public async Task<LyricsResult> GetLyricsAsync(MediaSnapshot snapshot, CancellationToken cancellationToken)
    {
        var key = CreateTrackKey(snapshot);
        if (string.IsNullOrWhiteSpace(key) || !snapshot.HasSession || string.IsNullOrWhiteSpace(snapshot.Title))
        {
            return LyricsResult.Empty;
        }

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = await FetchLyricsAsync(snapshot, cancellationToken);
        _cache[key] = result;
        return result;
    }

    private async Task<LyricsResult> FetchLyricsAsync(MediaSnapshot snapshot, CancellationToken cancellationToken)
    {
        var durationSeconds = Math.Max(0, (int)Math.Round(snapshot.Duration.TotalSeconds));
        var searched = await SearchAsync(snapshot, durationSeconds, cancellationToken);
        if (searched is not null)
        {
            return ToResult(searched);
        }

        return LyricsResult.NotFound;
    }

    private async Task<LrclibLyrics?> SearchAsync(
        MediaSnapshot snapshot,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["track_name"] = CleanSearchTerm(snapshot.Title)
        };

        if (!string.IsNullOrWhiteSpace(snapshot.Artist))
        {
            query["artist_name"] = CleanSearchTerm(snapshot.Artist);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Album))
        {
            query["album_name"] = CleanSearchTerm(snapshot.Album);
        }

        var results = await GetJsonAsync<List<LrclibLyrics>>("/api/search", query, cancellationToken);
        if (results is null || results.Count == 0)
        {
            return null;
        }

        var title = NormalizeKey(snapshot.Title);
        var artist = NormalizeKey(snapshot.Artist);

        return results
            .Where(item => HasAnyLyrics(item))
            .OrderBy(item => item.SyncedLyrics is null ? 1 : 0)
            .ThenBy(item => DurationPenalty(item.Duration, durationSeconds))
            .ThenBy(item => TextPenalty(item.TrackName, title))
            .ThenBy(item => string.IsNullOrWhiteSpace(artist) ? 0 : TextPenalty(item.ArtistName, artist))
            .FirstOrDefault();
    }

    private async Task<T?> GetJsonAsync<T>(
        string endpoint,
        Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var path = endpoint + "?" + string.Join("&", query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        using var response = await _httpClient.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static LyricsResult ToResult(LrclibLyrics lyrics)
    {
        var trackInfo = new LyricsTrackInfo(
            lyrics.TrackName ?? string.Empty,
            lyrics.ArtistName ?? string.Empty,
            lyrics.AlbumName ?? string.Empty,
            lyrics.Duration,
            "LRCLIB");

        if (lyrics.Instrumental)
        {
            return LyricsResult.InstrumentalWithInfo(trackInfo);
        }

        var syncedLines = LrcParser.Parse(lyrics.SyncedLyrics);
        if (syncedLines.Count > 0)
        {
            return LyricsResult.Synced(syncedLines, trackInfo);
        }

        var plainLines = SplitPlainLyrics(lyrics.PlainLyrics);
        return plainLines.Count > 0 ? LyricsResult.Plain(plainLines, trackInfo) : LyricsResult.NotFound;
    }

    private static List<string> SplitPlainLyrics(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return [];
        }

        return lyrics
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static bool HasAnyLyrics(LrclibLyrics lyrics)
    {
        return lyrics.Instrumental
            || !string.IsNullOrWhiteSpace(lyrics.SyncedLyrics)
            || !string.IsNullOrWhiteSpace(lyrics.PlainLyrics);
    }

    private static int DurationPenalty(double duration, int expected)
    {
        if (duration <= 0 || expected <= 0)
        {
            return 12;
        }

        return (int)Math.Abs(Math.Round(duration) - expected);
    }

    private static int TextPenalty(string? value, string expected)
    {
        var normalized = NormalizeKey(value);
        if (normalized == expected)
        {
            return 0;
        }

        if (normalized.Contains(expected, StringComparison.Ordinal)
            || expected.Contains(normalized, StringComparison.Ordinal))
        {
            return 1;
        }

        return 4;
    }

    private static string NormalizeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static string CleanSearchTerm(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim();
        cleaned = RemoveSuffix(cleaned, " - official");
        cleaned = RemoveSuffix(cleaned, " (official");
        cleaned = RemoveSuffix(cleaned, " [official");
        cleaned = RemoveSuffix(cleaned, " - lyric");
        cleaned = RemoveSuffix(cleaned, " (lyric");
        cleaned = RemoveSuffix(cleaned, " [lyric");
        cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal).Trim();
        return cleaned;
    }

    private static string RemoveSuffix(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index > 0 ? value[..index].Trim() : value;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed record LrclibLyrics(
    int Id,
    string? TrackName,
    string? ArtistName,
    string? AlbumName,
    double Duration,
    bool Instrumental,
    string? PlainLyrics,
    string? SyncedLyrics);
