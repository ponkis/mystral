using System.Globalization;
using System.IO;
using System.Net;
using Mystral.Configuration;

namespace Mystral.Services;

/// <summary>
/// Records why an artwork request failed so a production-only transport issue is
/// observable. Implementations must never throw into the fetch path.
/// </summary>
public interface IArtworkDiagnostics
{
    void RecordArtworkFailure(
        string stage,
        string? releaseId,
        string? host,
        HttpStatusCode? statusCode,
        string exceptionType);
}

/// <summary>Discards diagnostics. Used by the isolated service tests by default.</summary>
internal sealed class NullArtworkDiagnostics : IArtworkDiagnostics
{
    public static readonly NullArtworkDiagnostics Instance = new();

    private NullArtworkDiagnostics()
    {
    }

    public void RecordArtworkFailure(
        string stage,
        string? releaseId,
        string? host,
        HttpStatusCode? statusCode,
        string exceptionType)
    {
    }
}

/// <summary>
/// Appends one line per artwork failure to a size-bounded log under the app data
/// directory. Records only non-personal fields (stage, release id, host, status,
/// exception type) — never full URLs, query strings, or image bytes.
/// </summary>
public sealed class FileArtworkDiagnostics : IArtworkDiagnostics
{
    private const long MaxLogBytes = 1 * 1024 * 1024;
    private static readonly object Gate = new();
    private readonly string _logPath;
    private readonly string _backupPath;

    public FileArtworkDiagnostics()
        : this(Path.Combine(AppMetadata.LocalApplicationDataDirectory, "logs", "artwork.log"))
    {
    }

    internal FileArtworkDiagnostics(string logPath)
    {
        _logPath = logPath;
        _backupPath = logPath + ".1";
    }

    public void RecordArtworkFailure(
        string stage,
        string? releaseId,
        string? host,
        HttpStatusCode? statusCode,
        string exceptionType)
    {
        try
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:o} stage={1} release={2} host={3} status={4} error={5}",
                DateTimeOffset.UtcNow,
                stage,
                releaseId ?? "-",
                host ?? "-",
                statusCode is { } code ? ((int)code).ToString(CultureInfo.InvariantCulture) : "-",
                exceptionType);

            lock (Gate)
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never disrupt or fail the artwork fetch.
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            if (File.Exists(_backupPath))
            {
                File.Delete(_backupPath);
            }

            File.Move(_logPath, _backupPath);
        }
        catch
        {
            // A rotation failure must not prevent logging attempts.
        }
    }
}
