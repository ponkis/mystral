namespace Mystral.Models;

/// <summary>
/// Distinguishes artwork that was retrieved, genuinely absent on Cover Art Archive,
/// or that failed to download (transport/decode error) — so the UI can tell the user
/// "there is no cover" apart from "the cover couldn't be fetched right now".
/// </summary>
public enum ArtworkFetchOutcome
{
    Retrieved,
    NotAvailable,
    Failed
}

public sealed record MusicBrainzTrackData(
    string Title,
    string Artist,
    string Genre,
    string Year,
    string Album,
    string TrackNumber,
    string TrackTotal,
    byte[]? CoverArtwork,
    byte[]? DiscArtwork,
    ArtworkFetchOutcome CoverOutcome = ArtworkFetchOutcome.NotAvailable,
    ArtworkFetchOutcome DiscOutcome = ArtworkFetchOutcome.NotAvailable);
