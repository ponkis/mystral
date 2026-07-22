namespace Mystral.Models;

public sealed record MusicBrainzArtistCredit(
    string ArtistId,
    string Name,
    string JoinPhrase);

public sealed record MusicBrainzTrackInfo(
    string RecordingId,
    string Title,
    string Artist,
    IReadOnlyList<MusicBrainzArtistCredit> ArtistCredits,
    TimeSpan Duration,
    string FirstReleaseDate,
    IReadOnlyList<string> Isrcs,
    IReadOnlyList<string> Genres,
    string Disambiguation,
    string ReleaseId,
    string ReleaseGroupId,
    string Album,
    string TrackNumber,
    string TrackTotal);

public sealed record MusicBrainzArtistInfo(
    string ArtistId,
    string Name,
    string SortName,
    string Type,
    string Gender,
    string Country,
    string Area,
    string BeginArea,
    string EndArea,
    string BeginDate,
    string EndDate,
    bool Ended,
    string Disambiguation,
    string Annotation,
    string ImagePageUrl,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Genres);

public sealed record ArtistArtworkInfo(
    string ArtistId,
    byte[] Data,
    string SourcePageUrl,
    string Attribution,
    string LicenseName,
    string LicenseUrl);

public sealed record MusicBrainzLabelInfo(
    string Name,
    string CatalogNumber);

public sealed record MusicBrainzAlbumTrack(
    string RecordingId,
    int MediumPosition,
    string MediumTitle,
    string MediumFormat,
    int Position,
    string Number,
    string Title,
    string Artist,
    TimeSpan Duration);

public sealed record MusicBrainzAlbumInfo(
    string ReleaseId,
    string ReleaseGroupId,
    string Title,
    string Artist,
    string FirstReleaseDate,
    string ReleaseDate,
    string PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string Status,
    string Country,
    string Barcode,
    string Packaging,
    string Disambiguation,
    IReadOnlyList<MusicBrainzLabelInfo> Labels,
    IReadOnlyList<string> Formats,
    int TrackTotal,
    IReadOnlyList<string> Genres,
    IReadOnlyList<MusicBrainzAlbumTrack> Tracks,
    byte[]? CoverArtwork,
    ArtworkFetchOutcome CoverOutcome);
