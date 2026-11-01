using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kinora.Core.Data;
using Kinora.Core.Playback;
using Kinora.Services;
using LibVLCSharp.Shared;

namespace Kinora.ViewModels;

/// <summary>What the built-in player plays, and what happens once it has been seen.</summary>
/// <param name="Kept">Its place was kept; true when it has just become "watched".</param>
/// <param name="Picture">The poster in Kinora's cache, for Windows' media panel.</param>
/// <param name="TrackScope">Where audio and subtitle choices are remembered: a show has its own.</param>
/// <param name="Next">The episode after this one, if any.</param>
/// <param name="Previous">The episode before this one, if any.</param>
/// <param name="FindSubtitles">Opens the subtitle search; what it gives is the file saved.</param>
public sealed record PlayRequest(string Path, string Title, string? Subtitle, PlaybackKind Kind, long ItemId, Action MarkWatched,
                                 Action<bool>? Kept = null, string? Picture = null, string? TrackScope = null, Func<PlayRequest?>? Next = null,
                                 Func<PlayRequest?>? Previous = null, Func<Action<string>, Task>? FindSubtitles = null);

/// <summary>An audio or subtitle track in the player's menus.</summary>
public sealed partial class TrackChoice : ObservableObject
{
    private readonly Action<TrackChoice> _choose;

    public TrackChoice(int id, string name, string? language, Action<TrackChoice> choose)
    {
        Id = id;
        Name = name;
        Language = language;
        _choose = choose;
    }

    public int Id { get; }
    public string Name { get; }
    public string? Language { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Choose() => _choose(this);
}

/// <summary>
/// The built-in player: VLC's engine drawing into the page, Kinora's controls over it. It starts
/// where it was left (past the first minute), marks the title watched once 90% is behind, goes
/// on to the next episode in the same window, and remembers the tracks chosen.
/// </summary>
public sealed partial class PlayerViewModel : ViewModelBase, IDisposable
{
    private const int CountdownSeconds = 10;
    private const int MinimumScale = 60;
    private const int MaximumScale = 200;

    private readonly AppServices _s;
    private LibVLC _vlc;
    private readonly Action<PlayerViewModel> _closed;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _countdown;
    private PlayRequest _request;
    private PlayRequest? _next;
    private PlayRequest? _previous;
    private long _resumeFrom;
    private TrackMemory? _rememberedAudio;
    private TrackMemory? _rememberedSubtitle;
    private bool _audioSettled;
    private bool _subtitleSettled;
    private bool _started;
    private bool _ended;
    private bool _kept;
    private bool _closing;
    private int _toastVersion;
    private long _subtitleDelayMs;
    private int _scaleVersion;
    private long? _reopeningAt;

    public PlayerViewModel(AppServices s, LibVLC vlc, PlayRequest request, Action<PlayerViewModel> closed)
    {
        _s = s;
        _vlc = vlc;
        _request = request;
        _closed = closed;
        Player = NewPlayer();
        Volume = Math.Clamp(s.Settings.PlayerVolume, 0, 100);
        Prepare();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (--NextCountdown <= 0)
                PlayNext();
        };
    }

    /// <summary>Changes only when the subtitle size does: the page gives it its window.</summary>
    [ObservableProperty]
    public partial MediaPlayer Player { get; private set; }

    public string Title => _request.Title;
    public string? Subtitle => _request.Subtitle;
    public bool HasSubtitle => !string.IsNullOrEmpty(_request.Subtitle);
    public string? Picture => _request.Picture;
    public PlaybackKind Kind => _request.Kind;
    public long ItemId => _request.ItemId;
    public bool HasNext => _next is not null;
    public bool HasPrevious => _previous is not null;
    public bool CanFindSubtitles => _request.FindSubtitles is not null && _s.Settings.HasOpenSubtitlesKey;

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

    /// <summary>A short note over the video: the subtitle delay, the subtitle size.</summary>
    [ObservableProperty]
    public partial string? Toast { get; private set; }

    /// <summary>Seconds left before the next episode starts; 0 when nothing is counting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNextCard), nameof(NextCountdownText))]
    public partial int NextCountdown { get; private set; }

    public bool ShowNextCard => NextCountdown > 0;
    public string NextCountdownText => $"Play now ({NextCountdown} s)";
    public string NextTitle => _next?.Subtitle ?? _next?.Title ?? "";

    public ObservableCollection<TrackChoice> AudioTracks { get; } = [];
    public ObservableCollection<TrackChoice> SubtitleTracks { get; } = [];

    [ObservableProperty]
    public partial bool HasAudioChoice { get; private set; }

    public string SubtitleDelayText => _subtitleDelayMs == 0 ? "0.0 s"
        : (_subtitleDelayMs / 1000.0).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " s";

    public string SubtitleScaleText => $"{_s.Settings.SubtitleScale}%";

    /// <summary>Asks the page to go full screen or back (it owns the window).</summary>
    public event Action? FullScreenToggleRequested;

    /// <summary>Another video started in the same player: the page updates what depends on it.</summary>
    public event Action? ItemChanged;

    private MediaPlayer NewPlayer()
    {
        var player = new MediaPlayer(_vlc) { EnableMouseInput = false, EnableKeyInput = false };

        // VLC's events come on its own threads, and nothing may call it back from there
        player.Playing += (_, _) => Post(() =>
        {
            IsPlaying = true;
            IsBuffering = false;
            _reopeningAt = null;
            Player.Volume = Volume;   // the audio output exists only now
            Player.Mute = IsMuted;
            if (_subtitleDelayMs != 0)
                Player.SetSpuDelay(_subtitleDelayMs * 1000);
            RefreshTracks();
        });
        player.Paused += (_, _) => Post(() => IsPlaying = false);
        player.Buffering += (_, e) => Post(() => IsBuffering = e.Cache < 100 && !IsPlaying);
        player.LengthChanged += (_, e) => Post(() => LengthMs = e.Length);
        player.ESAdded += (_, _) => Post(RefreshTracks);
        player.ESDeleted += (_, _) => Post(RefreshTracks);
        player.EndReached += (_, _) => Post(OnEnded);
        player.EncounteredError += (_, _) => Post(() =>
        {
            IsBuffering = false;
            Message = "This video can't be played.";
        });
        return player;
    }

    /// <summary>Everything that belongs to one video, before it starts.</summary>
    private void Prepare()
    {
        _resumeFrom = 0;
        ResumeText = null;
        if (_s.Database.GetPlayback(_request.Kind, _request.ItemId) is { } saved && PlaybackRules.WorthResuming(saved.PositionMs, saved.LengthMs))
        {
            _resumeFrom = saved.PositionMs;
            ResumeText = $"Resumed at {PlaybackRules.Clock(saved.PositionMs)}";
        }
        (_rememberedAudio, _rememberedSubtitle) = _s.Database.GetTrackChoices(_request.TrackScope);
        _audioSettled = _rememberedAudio is null;
        _subtitleSettled = _rememberedSubtitle is null;
        _next = _request.Next?.Invoke();
        _previous = _request.Previous?.Invoke();
        _ended = false;
        _kept = false;
        _subtitleDelayMs = 0;
        _reopeningAt = null;
        Message = null;
        IsPlaying = false;
        IsBuffering = true;
        LengthMs = 0;
        TimeMs = 0;
        Progress = 0;
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        HasAudioChoice = false;
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(HasSubtitle), nameof(Picture), nameof(HasNext), nameof(HasPrevious),
                                     nameof(NextTitle), nameof(CanFindSubtitles), nameof(SubtitleDelayText) })
            OnPropertyChanged(name);
    }

    /// <summary>Starts playing; the page calls it once VLC has a window to draw in.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        PlayCurrent(_resumeFrom);
        _timer.Start();
    }

    private void PlayCurrent(long fromMs)
    {
        try
        {
            using var media = new Media(_vlc, new Uri(_request.Path));
            if (fromMs > 0)
                media.AddOption(string.Create(CultureInfo.InvariantCulture, $":start-time={fromMs / 1000.0:0.###}"));
            Player.Play(media);
            if (ResumeText is { } shown)
                DispatcherTimer.RunOnce(() => { if (ResumeText == shown) ResumeText = null; }, TimeSpan.FromSeconds(10));
        }
        catch (Exception e) when (e is UriFormatException or VLCException)
        {
            IsBuffering = false;
            Message = $"This video can't be played: {e.Message}";
        }
    }

    private void Tick()
    {
        if (_closing || _ended)
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
        if (Message is not null || _ended)
            return;
        Player.SetPause(Player.IsPlaying);
    }

    [RelayCommand]
    public void Back() => SeekBy(-10);

    [RelayCommand]
    public void Forward() => SeekBy(30);

    public void SeekBy(int seconds)
    {
        if (!CanSeek || _ended)
            return;
        var to = Math.Clamp(Player.Time + seconds * 1000L, 0, Math.Max(0, LengthMs - 1000));
        Player.Time = to;
        TimeMs = to;
        Progress = to * 1000.0 / LengthMs;
    }

    /// <summary>Where the seek bar was let go.</summary>
    public void SeekToProgress()
    {
        if (!CanSeek || _ended)
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

    // ---- next and previous ----

    [RelayCommand]
    public void PlayNext()
    {
        if (_next is { } next)
            SwitchTo(next);
    }

    [RelayCommand]
    public void PlayPrevious()
    {
        if (_previous is { } previous)
            SwitchTo(previous);
    }

    /// <summary>The countdown stopped: back to the show's page.</summary>
    [RelayCommand]
    private void CancelNext()
    {
        _countdown.Stop();
        NextCountdown = 0;
        Close();
    }

    /// <summary>Another episode in the same player: same window, same full screen.</summary>
    private void SwitchTo(PlayRequest request)
    {
        _countdown.Stop();
        NextCountdown = 0;
        KeepProgress();
        Player.Stop();
        _request = request;
        Prepare();
        if (_started)
            PlayCurrent(_resumeFrom);
        ItemChanged?.Invoke();
    }

    // ---- tracks ----

    /// <summary>The next audio or subtitle track, from the keyboard.</summary>
    public void CycleTrack(bool subtitles)
    {
        var list = subtitles ? SubtitleTracks : AudioTracks;
        if (list.Count < 2)
            return;
        var i = list.ToList().FindIndex(t => t.IsSelected);
        Choose(list[(i + 1) % list.Count]);
    }

    /// <summary>A choice of the user: applied, and remembered for the next time.</summary>
    private void Choose(TrackChoice track)
    {
        var subtitles = SubtitleTracks.Contains(track);
        Apply(track, subtitles);
        if (subtitles)
            _subtitleSettled = true;
        else
            _audioSettled = true;
        try
        {
            _s.Database.SaveTrackChoice(_request.TrackScope, subtitles, track.Name, track.Language);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
    }

    private void Apply(TrackChoice track, bool subtitles)
    {
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
        Fill(AudioTracks, audio.Select((t, i) => (t.Id, PlaybackRules.TrackName(t.Name, i + 1), PlaybackRules.TrackLanguage(t.Name))), Player.AudioTrack);

        var subs = Player.SpuDescription.Where(t => t.Id >= 0).ToList();
        Fill(SubtitleTracks, subs.Select((t, i) => (t.Id, PlaybackRules.TrackName(t.Name, i + 1), PlaybackRules.TrackLanguage(t.Name)))
                                 .Prepend((-1, "Off", null)), Player.Spu);

        HasAudioChoice = AudioTracks.Count > 1;

        // What was chosen last time, as soon as the track is there (files next to the video come late)
        if (IsPlaying)
        {
            _audioSettled |= Settle(AudioTracks, _rememberedAudio, subtitles: false, _audioSettled);
            _subtitleSettled |= Settle(SubtitleTracks, _rememberedSubtitle, subtitles: true, _subtitleSettled);
        }
    }

    private bool Settle(ObservableCollection<TrackChoice> list, TrackMemory? remembered, bool subtitles, bool settled)
    {
        if (settled || PlaybackRules.PickTrack(list.Select(t => (t.Id, t.Name, t.Language)).ToList(), remembered) is not { } id)
            return false;
        if (list.FirstOrDefault(t => t.Id == id) is { IsSelected: false } track)
            Apply(track, subtitles);
        return id != -1;   // "Off" holds also against a track that shows up later
    }

    private void Fill(ObservableCollection<TrackChoice> list, IEnumerable<(int Id, string Name, string? Language)> tracks, int current)
    {
        var wanted = tracks.ToList();
        // Two tracks called the same ("English", "English"): number them
        var names = wanted.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var counters = new Dictionary<string, int>();
        wanted = wanted.Select(t => names.Contains(t.Name)
            ? (t.Id, $"{t.Name} {counters[t.Name] = counters.GetValueOrDefault(t.Name) + 1}", t.Language)
            : t).ToList();
        if (list.Select(t => (t.Id, t.Name, t.Language)).SequenceEqual(wanted))
        {
            foreach (var t in list)
                t.IsSelected = t.Id == current;
            return;
        }
        list.Clear();
        foreach (var (id, name, language) in wanted)
            list.Add(new TrackChoice(id, name, language, Choose) { IsSelected = id == current });
    }

    // ---- subtitles ----

    /// <summary>The subtitle search; what it saves starts right away.</summary>
    [RelayCommand]
    private Task FindSubtitles() =>
        _request.FindSubtitles is { } find ? WhilePaused(() => find(AddSubtitleFile)) : Task.CompletedTask;

    /// <summary>A window over the video: it waits for the user.</summary>
    public async Task WhilePaused(Func<Task> work)
    {
        var wasPlaying = Player.IsPlaying;
        if (wasPlaying)
            Player.SetPause(true);
        await work();
        if (wasPlaying && !_closing)
            Player.SetPause(false);
    }

    /// <summary>A subtitle file for this video, shown at once.</summary>
    public void AddSubtitleFile(string file)
    {
        if (_closing)
            return;
        _subtitleSettled = true;
        Player.AddSlave(MediaSlaveType.Subtitle, new Uri(file).AbsoluteUri, true);
        ShowToast($"Subtitles: {Path.GetFileName(file)}");
    }

    [RelayCommand]
    public void SubtitlesEarlier() => ChangeSubtitleDelay(-100);

    [RelayCommand]
    public void SubtitlesLater() => ChangeSubtitleDelay(100);

    [RelayCommand]
    private void ResetSubtitleDelay() => ChangeSubtitleDelay(-_subtitleDelayMs);

    private void ChangeSubtitleDelay(long ms)
    {
        _subtitleDelayMs += ms;
        Player.SetSpuDelay(_subtitleDelayMs * 1000);
        OnPropertyChanged(nameof(SubtitleDelayText));
        ShowToast($"Subtitle delay {SubtitleDelayText}");
    }

    [RelayCommand]
    private void SubtitlesSmaller() => ChangeSubtitleScale(-20);

    [RelayCommand]
    private void SubtitlesBigger() => ChangeSubtitleScale(20);

    private void ChangeSubtitleScale(int step)
    {
        var scale = Math.Clamp(_s.Settings.SubtitleScale + step, MinimumScale, MaximumScale);
        if (scale == _s.Settings.SubtitleScale)
            return;
        _s.Settings.SubtitleScale = scale;
        _s.SaveSettings();
        OnPropertyChanged(nameof(SubtitleScaleText));
        ShowToast($"Subtitle size {scale}%");
        ReopenWithSubtitleScale();
    }

    /// <summary>
    /// VLC reads the size only when its engine starts: a new engine, and the video opens again
    /// where it is, with its tracks. A moment after the last click, not at each one.
    /// </summary>
    private async void ReopenWithSubtitleScale()
    {
        var version = ++_scaleVersion;
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        if (version != _scaleVersion || _closing || _ended || !_started)
            return;

        _reopeningAt = Math.Max(0, Player.Time);
        _rememberedAudio = Selected(AudioTracks) ?? _rememberedAudio;
        _rememberedSubtitle = Selected(SubtitleTracks) ?? _rememberedSubtitle;
        _audioSettled = _rememberedAudio is null;
        _subtitleSettled = _rememberedSubtitle is null;
        Player.Stop();
        IsPlaying = false;
        IsBuffering = true;
        var (vlc, _) = await _s.Video.GetAsync(_s.Settings.SubtitleScale);
        if (_closing || _ended)
            return;
        if (vlc is not null && vlc != _vlc)
        {
            _vlc = vlc;
            var old = Player;
            Player = NewPlayer();
            old.Dispose();
        }
        PlayCurrent(_reopeningAt ?? 0);

        static TrackMemory? Selected(ObservableCollection<TrackChoice> list) =>
            list.FirstOrDefault(t => t.IsSelected) is { } t ? new TrackMemory(t.Name, t.Language) : null;
    }

    private async void ShowToast(string text)
    {
        var version = ++_toastVersion;
        Toast = text;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (version == _toastVersion)
            Toast = null;
    }

    // ---- the end ----

    private void OnEnded()
    {
        if (_closing || _ended)
            return;
        _ended = true;
        IsPlaying = false;
        KeepProgress();
        if (_next is not null && _s.Settings.AutoPlayNextEpisode)
        {
            NextCountdown = CountdownSeconds;
            _countdown.Start();
        }
        else
        {
            Close();
        }
    }

    /// <summary>Once per video: its place is kept, or it becomes watched.</summary>
    private void KeepProgress()
    {
        if (_kept)
            return;
        _kept = true;
        var position = _ended ? LengthMs : _reopeningAt ?? Math.Max(0, Player.Time);
        var length = LengthMs > 0 ? LengthMs : Player.Length;
        var watched = false;
        try
        {
            if (_ended || PlaybackRules.CountsAsWatched(position, length))
            {
                _s.Database.ClearPlayback(_request.Kind, _request.ItemId);
                _request.MarkWatched();
                watched = true;
            }
            else if (PlaybackRules.WorthResuming(position, length))
            {
                _s.Database.SavePlayback(_request.Kind, _request.ItemId, position, length);
            }
            else if (Message is null)
            {
                _s.Database.ClearPlayback(_request.Kind, _request.ItemId);
            }
            _request.Kept?.Invoke(watched);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
        }
    }

    /// <summary>Back to Kinora: the position is kept (or the title marked watched), the page goes.</summary>
    [RelayCommand]
    public void Close()
    {
        if (_closing)
            return;
        _countdown.Stop();
        NextCountdown = 0;
        KeepProgress();
        _closing = true;
        _timer.Stop();
        _s.Settings.PlayerVolume = Volume;
        _s.SaveSettings();
        // Stopped before its window goes away
        Player.Stop();
        _closed(this);
    }

    public void Dispose()
    {
        _timer.Stop();
        _countdown.Stop();
        Player.Dispose();
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);
}
