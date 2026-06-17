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

    public event EventHandler<ScrobbleRecord>? ScrobbleAdded;

    private LocalScrobbleCacheService()
    {
        _cachePath = Path.Combine(AppMetadata.LocalApplicationDataDirectory, "scrobbles.json");
    }

    public void AddRecord(ScrobbleRecord record)
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
