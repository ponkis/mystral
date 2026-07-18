using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mystral.Configuration;
using Mystral.Models;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Mystral.Services;

public sealed partial class AnimatedArtworkService : IDisposable
{
    private const int PreferredVideoDimension = 768;
    private const long MaximumDownloadBytes = 40 * 1024 * 1024;
    private const int MaximumCachedFiles = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly string _cacheDirectory;

    public AnimatedArtworkService()
        : this(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(75)
        })
    {
    }

    internal AnimatedArtworkService(HttpClient httpClient, string? cacheDirectory = null)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppMetadata.UserAgent);
        _cacheDirectory = cacheDirectory
            ?? Path.Combine(AppMetadata.LocalApplicationDataDirectory, "animated-artwork");
    }

    public static string CreateArtworkKey(MediaSnapshot snapshot)
    {
        if (!snapshot.HasSession)
        {
            return string.Empty;
        }

        snapshot = AppleMusicMediaMetadata.NormalizeLyricsLookup(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.Artist) || string.IsNullOrWhiteSpace(snapshot.Album))
        {
            return string.Empty;
        }

        return $"{NormalizeKey(snapshot.Artist)}|{NormalizeKey(snapshot.Album)}";
    }

    public async Task<string?> GetAnimatedArtworkAsync(MediaSnapshot snapshot, CancellationToken cancellationToken)
    {
        snapshot = AppleMusicMediaMetadata.NormalizeLyricsLookup(snapshot);
        var key = CreateArtworkKey(snapshot);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached is not null && File.Exists(cached) ? cached : null;
        }

        var cachedFile = GetCacheFilePath(key);
        if (File.Exists(cachedFile))
        {
            TouchCacheFile(cachedFile);
            _cache[key] = cachedFile;
            return cachedFile;
        }

        string? result;
        try
        {
            result = await FetchAnimatedArtworkAsync(snapshot, cachedFile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Network/parse/decode failures are non-fatal: fall back to static art
            // without caching, so a later track change can retry.
            return null;
        }

        _cache[key] = result;
        return result;
    }

    private async Task<string?> FetchAnimatedArtworkAsync(
        MediaSnapshot snapshot,
        string cacheFilePath,
        CancellationToken cancellationToken)
    {
        var searchUrl = "https://artwork.m8tec.top/api/v1/artwork/search"
            + $"?artist={Uri.EscapeDataString(snapshot.Artist.Trim())}"
            + $"&album={Uri.EscapeDataString(snapshot.Album.Trim())}";

        using var response = await _httpClient.GetAsync(searchUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var info = await JsonSerializer.DeserializeAsync<AnimatedArtworkInfo>(stream, JsonOptions, cancellationToken);
        var masterUrl = info?.Url ?? info?.UrlTall;
        if (string.IsNullOrWhiteSpace(masterUrl)
            || !Uri.TryCreate(masterUrl, UriKind.Absolute, out var masterUri)
            || masterUri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var masterPlaylist = await _httpClient.GetStringAsync(masterUri, cancellationToken);
        var mediaPlaylistUri = SelectStreamVariantUri(masterPlaylist, masterUri) ?? masterUri;
        var mediaPlaylist = ReferenceEquals(mediaPlaylistUri, masterUri)
            ? masterPlaylist
            : await _httpClient.GetStringAsync(mediaPlaylistUri, cancellationToken);

        var videoUri = ResolveSegmentFileUri(mediaPlaylist, mediaPlaylistUri);
        if (videoUri is null)
        {
            return null;
        }

        return await DownloadVideoAsync(videoUri, cacheFilePath, cancellationToken);
    }

    internal static Uri? SelectStreamVariantUri(string masterPlaylist, Uri masterUri)
    {
        var lines = masterPlaylist
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var variants = new List<(bool IsAvc, int Dimension, Uri Uri)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)
                || i + 1 >= lines.Count
                || lines[i + 1].StartsWith('#'))
            {
                continue;
            }

            if (!Uri.TryCreate(masterUri, lines[i + 1], out var variantUri))
            {
                continue;
            }

            var codecsMatch = StreamCodecsRegex().Match(lines[i]);
            var resolutionMatch = StreamResolutionRegex().Match(lines[i]);
            var isAvc = codecsMatch.Success
                && codecsMatch.Groups[1].Value.Contains("avc1", StringComparison.OrdinalIgnoreCase);
            var dimension = resolutionMatch.Success
                ? Math.Max(
                    int.Parse(resolutionMatch.Groups[1].Value),
                    int.Parse(resolutionMatch.Groups[2].Value))
                : 0;
            variants.Add((isAvc, dimension, variantUri));
        }

        if (variants.Count == 0)
        {
            return null;
        }

        // Prefer H.264 (HEVC needs an optional Windows codec) at a resolution near the target.
        return variants
            .OrderByDescending(variant => variant.IsAvc)
            .ThenBy(variant => Math.Abs(variant.Dimension - PreferredVideoDimension))
            .First()
            .Uri;
    }

    internal static Uri? ResolveSegmentFileUri(string mediaPlaylist, Uri playlistUri)
    {
        var lines = mediaPlaylist
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        string? mapFile = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var match = MapUriRegex().Match(line);
                if (!match.Success)
                {
                    return null;
                }

                mapFile = match.Groups[1].Value;
                continue;
            }

            // Every segment must live in the same file as the map (single-file
            // byte-range playlist); anything else needs concatenation we don't do.
            if (!line.StartsWith('#') && !string.Equals(line, mapFile, StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(mapFile)
            || !Uri.TryCreate(playlistUri, mapFile, out var videoUri)
            || videoUri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return videoUri;
    }

    private async Task<string?> DownloadVideoAsync(Uri videoUri, string cacheFilePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var tempPath = cacheFilePath + ".tmp";
        try
        {
            using var response = await _httpClient.GetAsync(
                videoUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            {
                return null;
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaximumDownloadBytes)
                    {
                        return null;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (total == 0)
                {
                    return null;
                }
            }

            if (!await RemuxToSeekableMp4Async(tempPath, cacheFilePath, cancellationToken))
            {
                return null;
            }

            PruneCacheDirectory();
            return cacheFilePath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    // Cover art streams arrive as fragmented MP4 (no seek index), which WPF's
    // MediaPlayer can play but never rewind, so a loop would freeze at the end.
    // Windows' MediaTranscoder rewrites the download once into a flat MP4 with a
    // real sample table; the cached copy then seeks (and loops) like any video.
    private async Task<bool> RemuxToSeekableMp4Async(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
            var targetFolder = await StorageFolder.GetFolderFromPathAsync(_cacheDirectory);
            var targetFile = await targetFolder.CreateFileAsync(
                Path.GetFileName(targetPath),
                CreationCollisionOption.ReplaceExisting);
            var profile = await MediaEncodingProfile.CreateFromFileAsync(sourceFile);
            var transcoder = new MediaTranscoder();
            var prepared = await transcoder.PrepareFileTranscodeAsync(sourceFile, targetFile, profile);
            if (!prepared.CanTranscode)
            {
                TryDeleteFile(targetPath);
                return false;
            }

            await prepared.TranscodeAsync().AsTask(cancellationToken);
            return true;
        }
        catch
        {
            TryDeleteFile(targetPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private string GetCacheFilePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDirectory, hash[..32].ToLowerInvariant() + "-v2.mp4");
    }

    private static void TouchCacheFile(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
    }

    private void PruneCacheDirectory()
    {
        try
        {
            var files = new DirectoryInfo(_cacheDirectory)
                .GetFiles("*.mp4")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaximumCachedFiles);
            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string NormalizeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    [GeneratedRegex("CODECS=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex StreamCodecsRegex();

    [GeneratedRegex(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StreamResolutionRegex();

    [GeneratedRegex("URI=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex MapUriRegex();

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed record AnimatedArtworkInfo(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("url_tall")] string? UrlTall,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("album")] string? Album);
