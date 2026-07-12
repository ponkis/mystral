namespace Mystral.Models;

public sealed class BurnTrackDraft
{
    public required string SourcePath { get; init; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string TrackNumber { get; set; } = string.Empty;
    public string TrackTotal { get; set; } = string.Empty;
    public string Isrc { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public ArtworkAsset? CoverArtwork { get; set; }
    public ArtworkAsset? DiscArtwork { get; set; }
    internal bool CoverArtworkChanged { get; set; }
    internal bool DiscArtworkChanged { get; set; }
    internal ATL.PictureInfo.PIC_TYPE? CoverArtworkOriginalType { get; init; }
    internal byte[]? CoverArtworkOriginalData { get; init; }
}
