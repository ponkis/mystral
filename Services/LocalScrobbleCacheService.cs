using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

public sealed class LocalScrobbleCacheService
{
    private static readonly LocalScrobbleCacheService _instance = new();
    public static LocalScrobbleCacheService Instance => _instance;

    private readonly string _cachePath;
    private readonly object _fileLock = new();

    private string _activeTitle = string.Empty;
    private string _activeArtist = string.Empty;
    private string _activeAlbum = string.Empty;
    private TimeSpan _activeDuration = TimeSpan.Zero;
    private DateTimeOffset _activeStartedAt = DateTimeOffset.MinValue;
    private double _observedPlayTimeSeconds = 0;
    private DateTimeOffset _lastUpdateTime = DateTimeOffset.MinValue;
    private bool _hasScrobbledActiveTrack = false;

    public event EventHandler<ScrobbleRecord>? ScrobbleAdded;

    private LocalScrobbleCacheService()
    {
        _cachePath = Path.Combine(AppMetadata.LocalApplicationDataDirectory, "scrobbles.json");
    }

    public void Update(MediaSnapshot snapshot)
    {
        if (!snapshot.HasSession || string.IsNullOrWhiteSpace(snapshot.Title))
        {
            ResetActiveTrack();
            return;
        }

        var now = DateTimeOffset.Now;

        // Check if track changed
        if (snapshot.Title != _activeTitle || snapshot.Artist != _activeArtist)
        {
            // Start tracking new track
            _activeTitle = snapshot.Title;
            _activeArtist = snapshot.Artist;
            _activeAlbum = snapshot.Album;
            _activeDuration = snapshot.Duration;
            _activeStartedAt = now;
            _observedPlayTimeSeconds = 0;
            _lastUpdateTime = now;
            _hasScrobbledActiveTrack = false;
            return;
        }

        // Update active duration if it was zero and is now known
        if (_activeDuration == TimeSpan.Zero && snapshot.Duration > TimeSpan.Zero)
        {
            _activeDuration = snapshot.Duration;
        }

        // Accumulate play time if playing
        if (snapshot.IsPlaying)
        {
            if (_lastUpdateTime != DateTimeOffset.MinValue)
            {
                var elapsed = (now - _lastUpdateTime).TotalSeconds;
                // Clamp elapsed time to prevent giant jumps (e.g. system wake/sleep)
                if (elapsed > 0 && elapsed < 10)
                {
                    _observedPlayTimeSeconds += elapsed;
                }
            }

            _lastUpdateTime = now;

            // Check scrobble eligibility (duration must be >= 30 seconds)
            if (!_hasScrobbledActiveTrack && _activeDuration.TotalSeconds >= 30)
            {
                double threshold = Math.Min(_activeDuration.TotalSeconds / 2.0, 240.0);
                if (_observedPlayTimeSeconds >= threshold)
                {
                    _hasScrobbledActiveTrack = true;
                    var record = new ScrobbleRecord
                    {
                        Title = _activeTitle,
                        Artist = _activeArtist,
                        Album = _activeAlbum,
                        Timestamp = _activeStartedAt.ToUnixTimeSeconds(),
                        Duration = (int)_activeDuration.TotalSeconds
                    };
                    SaveScrobbleRecord(record);
                }
            }
        }
        else
        {
            _lastUpdateTime = now;
        }
    }

    private void ResetActiveTrack()
    {
        _activeTitle = string.Empty;
        _activeArtist = string.Empty;
        _activeAlbum = string.Empty;
        _activeDuration = TimeSpan.Zero;
        _activeStartedAt = DateTimeOffset.MinValue;
        _observedPlayTimeSeconds = 0;
        _lastUpdateTime = DateTimeOffset.MinValue;
        _hasScrobbledActiveTrack = false;
    }

    private void SaveScrobbleRecord(ScrobbleRecord record)
    {
        lock (_fileLock)
        {
            try
            {
                var records = LoadAllRecordsInternal();
                records.Insert(0, record); // Newest first

                // Cap total history items at 10,000 for device safety/efficiency
                if (records.Count > 10000)
                {
                    records.RemoveRange(10000, records.Count - 10000);
                }

                var dir = Path.GetDirectoryName(_cachePath);
                if (dir != null)
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_cachePath, json);

                ScrobbleAdded?.Invoke(this, record);
            }
            catch
            {
                // Suppress errors to ensure uninterrupted player behavior
            }
        }
    }

    public void RemoveRecord(ScrobbleRecord record)
    {
        lock (_fileLock)
        {
            try
            {
                var records = LoadAllRecordsInternal();
                var index = records.FindIndex(r => r.Timestamp == record.Timestamp && r.Title == record.Title && r.Artist == record.Artist);
                if (index >= 0)
                {
                    records.RemoveAt(index);
                    SaveAllRecordsInternal(records);
                }
            }
            catch
            {
                // Suppress
            }
        }
    }

    public void RemoveRecords(IEnumerable<ScrobbleRecord> recordsToRemove)
    {
        lock (_fileLock)
        {
            try
            {
                var records = LoadAllRecordsInternal();
                bool changed = false;
                foreach (var record in recordsToRemove)
                {
                    var index = records.FindIndex(r => r.Timestamp == record.Timestamp && r.Title == record.Title && r.Artist == record.Artist);
                    if (index >= 0)
                    {
                        records.RemoveAt(index);
                        changed = true;
                    }
                }

                if (changed)
                {
                    SaveAllRecordsInternal(records);
                }
            }
            catch
            {
                // Suppress
            }
        }
    }

    public bool ClearHistory()
    {
        lock (_fileLock)
        {
            try
            {
                if (File.Exists(_cachePath))
                {
                    File.Delete(_cachePath);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public List<ScrobbleRecord> LoadAllRecords()
    {
        lock (_fileLock)
        {
            return LoadAllRecordsInternal();
        }
    }

    private List<ScrobbleRecord> LoadAllRecordsInternal()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return new List<ScrobbleRecord>();
            }

            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<List<ScrobbleRecord>>(json) ?? new List<ScrobbleRecord>();
        }
        catch
        {
            return new List<ScrobbleRecord>();
        }
    }

    private void SaveAllRecordsInternal(List<ScrobbleRecord> records)
    {
        var dir = Path.GetDirectoryName(_cachePath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_cachePath, json);
    }
}
