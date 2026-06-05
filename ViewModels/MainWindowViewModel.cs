using Mystral.Models;
using Mystral.Services;

namespace Mystral.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private LyricsResult _lyrics = LyricsResult.Empty;
    private LastFmTrackInfo? _currentLastFmInfo;
    private bool _disposed;

    public AppSettingsService SettingsService { get; } = new();
    public MediaSessionService MediaService { get; } = new();
    public LyricsService LyricsService { get; } = new();
    public VolumeService VolumeService { get; } = new();
    public LastFmService LastFmService { get; }

    public MainWindowViewModel()
    {
        LastFmService = new LastFmService(SettingsService);
    }

    public MediaSnapshot Snapshot
    {
        get => _snapshot;
        set => SetProperty(ref _snapshot, value);
    }

    public LyricsResult Lyrics
    {
        get => _lyrics;
        set => SetProperty(ref _lyrics, value);
    }

    public LastFmTrackInfo? CurrentLastFmInfo
    {
        get => _currentLastFmInfo;
        set => SetProperty(ref _currentLastFmInfo, value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        VolumeService.Dispose();
        LastFmService.Dispose();
        LyricsService.Dispose();
        MediaService.Dispose();
    }
}
