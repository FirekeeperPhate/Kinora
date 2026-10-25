using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Data;
using Kinora.Core.Playback;
using Kinora.Services;
using LibVLCSharp.Shared;

namespace Kinora.ViewModels;

/// <summary>What the built-in player plays, and what happens once it has been seen.</summary>
/// <param name="Picture">The poster in Kinora's cache, for Windows' media panel.</param>
public sealed record PlayRequest(string Path, string Title, string? Subtitle, PlaybackKind Kind, long ItemId, Action MarkWatched, string? Picture = null);

/// <summary>An audio or subtitle track in the player's menus.</summary>
public sealed partial class TrackChoice : ObservableObject
{
    private readonly Action<TrackChoice> _choose;

    public TrackChoice(int id, string name, Action<TrackChoice> choose)
    {
        Id = id;
        Name = name;
        _choose = choose;
    }

    public int Id { get; }
    public string Name { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Choose() => _choose(this);
}

/// <summary>
/// The built-in player: VLC's engine drawing into the page, Kinora's controls over it. It starts
/// where it was left (past the first minute), and marks the title watched once 90% is behind.
/// </summary>
public sealed partial class PlayerViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _s;
    private readonly LibVLC _vlc;
    private readonly PlayRequest _request;
    private readonly Action<PlayerViewModel> _closed;
    private readonly DispatcherTimer _timer;
    private readonly long _resumeFrom;
    private bool _started;
    private bool _ended;
    private bool _closing;

    public PlayerViewModel(AppServices s, LibVLC vlc, PlayRequest request, Action<PlayerViewModel> closed)
    {
        _s = s;
        _vlc = vlc;
        _request = request;
        _closed = closed;
        Player = new MediaPlayer(vlc) { EnableMouseInput = false, EnableKeyInput = false };
        Volume = Math.Clamp(s.Settings.PlayerVolume, 0, 100);

        if (s.Database.GetPlayback(request.Kind, request.ItemId) is { } saved && PlaybackRules.WorthResuming(saved.PositionMs, saved.LengthMs))
        {
            _resumeFrom = saved.PositionMs;
            ResumeText = $"Resumed at {PlaybackRules.Clock(saved.PositionMs)}";
        }

        // VLC's events come on its own threads, and nothing may call it back from there
        Player.Playing += (_, _) => Post(() =>
        {
            IsPlaying = true;
            IsBuffering = false;
            Player.Volume = Volume;   // the audio output exists only now
            Player.Mute = IsMuted;
            RefreshTracks();
        });
        Player.Paused += (_, _) => Post(() => IsPlaying = false);
        Player.Buffering += (_, e) => Post(() => IsBuffering = e.Cache < 100 && !IsPlaying);
        Player.LengthChanged += (_, e) => Post(() => LengthMs = e.Length);
        Player.ESAdded += (_, _) => Post(RefreshTracks);
        Player.ESDeleted += (_, _) => Post(RefreshTracks);
        Player.EndReached += (_, _) => Post(OnEnded);
        Player.EncounteredError += (_, _) => Post(() =>
        {
            IsBuffering = false;
            Message = "This video can't be played.";
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
    }

    public MediaPlayer Player { get; }
    public string Title => _request.Title;
    public string? Subtitle => _request.Subtitle;
    public bool HasSubtitle => !string.IsNullOrEmpty(_request.Subtitle);
    public string? Picture => _request.Picture;

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial bool IsBuffering { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial long TimeMs { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText), nameof(CanSeek))]
    public partial long LengthMs { get; private set; }

    /// <summary>The seek bar, 0-1000. While the user drags it the timer leaves it alone.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    public bool IsSeeking { get; set; }

    public string TimeText => PlaybackRules.Clock(IsSeeking && LengthMs > 0 ? (long)(Progress / 1000 * LengthMs) : TimeMs);
    public string LengthText => LengthMs > 0 ? PlaybackRules.Clock(LengthMs) : "";
    public bool CanSeek => LengthMs > 0;

    [ObservableProperty]
    public partial int Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsFullScreen { get; set; }

    [ObservableProperty]
    public partial bool ControlsVisible { get; set; } = true;

    /// <summary>An error to show over the video.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResume))]
    public partial string? ResumeText { get; private set; }

    public bool ShowResume => ResumeText is not null;

    public ObservableCollection<TrackChoice> AudioTracks { get; } = [];
    public ObservableCollection<TrackChoice> SubtitleTracks { get; } = [];

    [ObservableProperty]
    public partial bool HasAudioChoice { get; private set; }

    [ObservableProperty]
    public partial bool HasSubtitleTracks { get; private set; }

    /// <summary>Asks the page to go full screen or back (it owns the window).</summary>
    public event Action? FullScreenToggleRequested;

    /// <summary>Starts playing; the page calls it once VLC has a window to draw in.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            using var media = new Media(_vlc, new Uri(_request.Path));
            if (_resumeFrom > 0)
                media.AddOption($":start-time={_resumeFrom / 1000.0:0.###}".Replace(',', '.'));
            Player.Play(media);
            _timer.Start();
            if (ResumeText is not null)
                DispatcherTimer.RunOnce(() => ResumeText = null, TimeSpan.FromSeconds(10));
        }
        catch (Exception e) when (e is UriFormatException or VLCException)
        {
            IsBuffering = false;
            Message = $"This video can't be played: {e.Message}";
        }
    }

    private void Tick()
    {
        if (_closing)
            return;
        TimeMs = Math.Max(0, Player.Time);
        if (LengthMs <= 0 && Player.Length > 0)
            LengthMs = Player.Length;
        if (!IsSeeking && LengthMs > 0)
            Progress = Math.Clamp(TimeMs * 1000.0 / LengthMs, 0, 1000);
    }

    // ---- controls ----

    [RelayCommand]
    public void PlayPause()
    {
        if (Message is not null)
            return;
        if (Player.IsPlaying)
            Player.SetPause(true);
        else
            Player.SetPause(false);
    }

    [RelayCommand]
    public void Back() => SeekBy(-10);

    [RelayCommand]
    public void Forward() => SeekBy(30);

    public void SeekBy(int seconds)
    {
        if (!CanSeek)
            return;
        var to = Math.Clamp(Player.Time + seconds * 1000L, 0, Math.Max(0, LengthMs - 1000));
        Player.Time = to;
        TimeMs = to;
        Progress = to * 1000.0 / LengthMs;
    }

    /// <summary>Where the seek bar was let go.</summary>
    public void SeekToProgress()
    {
        if (!CanSeek)
            return;
        var to = (long)(Progress / 1000 * LengthMs);
        Player.Time = to;
        TimeMs = to;
    }

    /// <summary>While dragging: the time shown follows the bar.</summary>
    public void PreviewSeek() => OnPropertyChanged(nameof(TimeText));

    [RelayCommand]
    public void ToggleMute() => IsMuted = !IsMuted;

    public void ChangeVolume(int delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    partial void OnVolumeChanged(int value)
    {
        Player.Volume = value;
        if (value > 0 && IsMuted)
            IsMuted = false;
    }

    partial void OnIsMutedChanged(bool value) => Player.Mute = value;

    [RelayCommand]
    public void ToggleFullScreen() => FullScreenToggleRequested?.Invoke();

    [RelayCommand]
    private void StartOver()
    {
        ResumeText = null;
        Player.Time = 0;
    }

    /// <summary>The next audio or subtitle track, from the keyboard.</summary>
    public void CycleTrack(bool subtitles)
    {
        var list = subtitles ? SubtitleTracks : AudioTracks;
        if (list.Count < 2)
            return;
        var i = list.ToList().FindIndex(t => t.IsSelected);
        Choose(list[(i + 1) % list.Count]);
    }

    private void Choose(TrackChoice track)
    {
        var subtitles = SubtitleTracks.Contains(track);
        if (subtitles)
            Player.SetSpu(track.Id);
        else
            Player.SetAudioTrack(track.Id);
        foreach (var t in subtitles ? SubtitleTracks : AudioTracks)
            t.IsSelected = t == track;
    }

    private void RefreshTracks()
    {
        if (_closing)
            return;
        var audio = Player.AudioTrackDescription.Where(t => t.Id >= 0).ToList();
        var current = Player.AudioTrack;
        Fill(AudioTracks, audio.Select((t, i) => (t.Id, PlaybackRules.TrackName(t.Name, i + 1))), current);

        var subs = Player.SpuDescription.Where(t => t.Id >= 0).ToList();
        var currentSub = Player.Spu;
        Fill(SubtitleTracks, subs.Select((t, i) => (t.Id, PlaybackRules.TrackName(t.Name, i + 1))).Prepend((-1, "Off")), currentSub);

        HasAudioChoice = AudioTracks.Count > 1;
        HasSubtitleTracks = SubtitleTracks.Count > 1;
    }

    private void Fill(ObservableCollection<TrackChoice> list, IEnumerable<(int Id, string Name)> tracks, int current)
    {
        var wanted = tracks.ToList();
        // Two tracks called the same ("English", "English"): number them
        var names = wanted.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var counters = new Dictionary<string, int>();
        wanted = wanted.Select(t => names.Contains(t.Name)
            ? (t.Id, $"{t.Name} {counters[t.Name] = counters.GetValueOrDefault(t.Name) + 1}")
            : t).ToList();
        if (list.Select(t => (t.Id, t.Name)).SequenceEqual(wanted))
        {
            foreach (var t in list)
                t.IsSelected = t.Id == current;
            return;
        }
        list.Clear();
        foreach (var (id, name) in wanted)
            list.Add(new TrackChoice(id, name, Choose) { IsSelected = id == current });
    }

    // ---- the end ----

    private void OnEnded()
    {
        _ended = true;
        Close();
    }

    /// <summary>Back to Kinora: the position is kept (or the title marked watched), the page goes.</summary>
    [RelayCommand]
    public void Close()
    {
        if (_closing)
            return;
        _closing = true;
        _timer.Stop();
        var position = _ended ? LengthMs : Math.Max(0, Player.Time);
        var length = LengthMs > 0 ? LengthMs : Player.Length;
        try
        {
            if (_ended || PlaybackRules.CountsAsWatched(position, length))
            {
                _s.Database.ClearPlayback(_request.Kind, _request.ItemId);
                _request.MarkWatched();
            }
            else if (PlaybackRules.WorthResuming(position, length))
            {
                _s.Database.SavePlayback(_request.Kind, _request.ItemId, position, length);
            }
            else if (Message is null)
            {
                _s.Database.ClearPlayback(_request.Kind, _request.ItemId);
            }
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
        }
        _s.Settings.PlayerVolume = Volume;
        _s.SaveSettings();
        // Stopped before its window goes away
        Player.Stop();
        _closed(this);
    }

    public void Dispose()
    {
        _timer.Stop();
        Player.Dispose();
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);
}
