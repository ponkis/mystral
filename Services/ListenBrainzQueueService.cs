using System.IO;
using System.Text.Json;
using Mystral.Configuration;
using Mystral.Models;

namespace Mystral.Services;

/// <summary>
/// Plays waiting for the ListenBrainz-compatible server (Settings →
/// Scrobbling). Every finished play is queued here first and sent from here,
/// so a play made while the server is unreachable (offline, restarting, the
/// app closed) is sent later instead of lost. Servers keep one play per
/// moment and song, so sending one twice is harmless. The file
/// (<c>listenbrainz-listens.json</c> in the local app data folder) holds at
/// most <see cref="MaximumQueued"/> plays; past that the oldest go.
/// </summary>
public sealed class ListenBrainzQueueService
{
    internal const int MaximumQueued = 5000;
    private const int BatchSize = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _queuePath;
    private readonly object _fileLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public ListenBrainzQueueService()
        : this(Path.Combine(AppMetadata.LocalApplicationDataDirectory, "listenbrainz-listens.json"))
    {
    }

    internal ListenBrainzQueueService(string queuePath)
    {
        _queuePath = queuePath;
    }

    public int Count
    {
        get
        {
            lock (_fileLock)
            {
                return Load().Count;
            }
        }
    }

    public void Enqueue(ListenBrainzListen listen)
    {
        ArgumentNullException.ThrowIfNull(listen);
        lock (_fileLock)
        {
            var queued = Load();
            queued.Add(listen);
            if (queued.Count > MaximumQueued)
            {
                queued.RemoveRange(0, queued.Count - MaximumQueued);
            }

            Save(queued);
        }
    }

    /// <summary>Forgets every waiting play (disconnecting).</summary>
    public void Clear()
    {
        lock (_fileLock)
        {
            try
            {
                File.Delete(_queuePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Save([]);
            }
        }
    }

    /// <summary>
    /// Sends what is queued, oldest first, a batch at a time. A batch the
    /// server can't take right now (<see cref="ListenBrainzUnavailableException"/>,
    /// or the token refused) stays for the next try; one it refuses outright
    /// is dropped, since sending it again would fail the same way. Returns how
    /// many were sent.
    /// </summary>
    public async Task<int> FlushAsync(
        Func<IReadOnlyList<ListenBrainzListen>, CancellationToken, Task> send,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (!await _flushGate.WaitAsync(0, cancellationToken))
        {
            return 0;
        }

        var sent = 0;
        try
        {
            while (true)
            {
                List<ListenBrainzListen> batch;
                lock (_fileLock)
                {
                    batch = Load().Take(BatchSize).ToList();
                }

                if (batch.Count == 0)
                {
                    return sent;
                }

                try
                {
                    await send(batch, cancellationToken);
                    sent += batch.Count;
                }
                catch (Exception ex) when (ex is ListenBrainzUnavailableException or ListenBrainzUnauthorizedException or OperationCanceledException)
                {
                    return sent;
                }
                catch (ListenBrainzRefusedException)
                {
                    // Refused outright: it would be refused again.
                }

                lock (_fileLock)
                {
                    var queued = Load();
                    queued.RemoveAll(listen => batch.Contains(listen));
                    Save(queued);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private List<ListenBrainzListen> Load()
    {
        try
        {
            if (!File.Exists(_queuePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<ListenBrainzListen>>(File.ReadAllText(_queuePath), JsonOptions)?
                .Where(listen => listen is not null
                    && !string.IsNullOrWhiteSpace(listen.Artist)
                    && !string.IsNullOrWhiteSpace(listen.Track))
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save(List<ListenBrainzListen> queued)
    {
        try
        {
            var directory = Path.GetDirectoryName(_queuePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _queuePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(queued, JsonOptions));
            File.Move(temporaryPath, _queuePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A queue that cannot be written loses nothing already sent; the
            // play is simply not kept for later.
        }
    }
}
