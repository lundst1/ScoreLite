using ScoreLite.Audio;
using ScoreLite.Core;

namespace ScoreLite.App;

/// <summary>
/// Reserv när ljudet inte kan startas (AUDIO_INIT_FAILED, SOUNDFONT_LOAD_FAILED, §12.3). <see cref="Load"/> kastar så att
/// <c>ScoreViewModel</c> går in i degraderat läge: notbilden visas men uppspelningsknapparna är inaktiva.
/// </summary>
public sealed class NullPlaybackEngine : IPlaybackEngine
{
    public PlaybackState State => PlaybackState.Unloaded;
    public bool MetronomeEnabled { get; set; }
    public bool CountInEnabled { get; set; }
    public event Action<PlaybackSnapshot>? SnapshotChanged { add { } remove { } }

    public void Load(Score score) => throw new InvalidOperationException("Ljudet kunde inte startas.");
    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void SeekTo(ScorePosition position) { }
    public void SetSelectedParts(IReadOnlySet<string> partIds) { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
