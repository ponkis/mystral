using System;
using System.Text.Json.Serialization;

namespace Mystral.Models;

public sealed class ScrobbleRecord
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public long Timestamp { get; set; } // Unix timestamp (seconds)
    public int Duration { get; set; } // Duration in seconds

    [JsonIgnore]
    public string FormattedTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public bool IsSelected { get; set; } = false;
}
