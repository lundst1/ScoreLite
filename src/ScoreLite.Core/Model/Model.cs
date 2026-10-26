namespace ScoreLite.Core;

/// <summary>Stabil adress i notbilden (AL-13, DR-10).</summary>
public readonly record struct ScorePosition(int MeasureIndex, int TickInMeasure);

public readonly record struct TimeSig(int Numerator, int Denominator)
{
    public static readonly TimeSig Common = new(4, 4);
    public Frac Length => Frac.Of(Numerator, Denominator);
    public int LengthTicks => Length.ToTicks();
}

public readonly record struct KeySig(int Fifths);

public enum ClefKind { Treble, Bass, Alto, Tenor, Percussion, Tab, Other }
public enum StaffKind { Standard, Percussion, Tab }

/// <summary>Plats för trumsetdefinitioner (V5/V14: inte verifierat mot riktiga filer än).</summary>
public sealed record Drumset(IReadOnlyDictionary<int, string> Names);

public sealed record Staff(int Index, ClefKind Clef, StaffKind Kind);

public sealed record InstrumentSpan(int FromScoreTick, string InstrumentId, int Program, int Bank,
    bool IsPercussion, int TransposeSemitones, Drumset? Drums);

public sealed record Part(string Id, string Name, string ShortName,
    IReadOnlyList<Staff> Staves, IReadOnlyList<InstrumentSpan> Instruments);

/// <remarks>Avvikelse från IF-1 (v0.4): <c>Tpc</c> och <c>Tpc2</c> tillagda för MeiExporter (skriven stavning). 99 = saknas.</remarks>
public sealed record Note(string Id, string PartId, int StaffIndex, int Voice,
    int ScoreTick, int DurationTicks, int MidiPitch, bool TiedToNext, int Tpc = Note.NoTpc, int Tpc2 = Note.NoTpc)
{
    public const int NoTpc = 99;
}

public enum ItemKind { Chord, Rest, MeasureRest }

/// <summary>En (eventuellt nästlad) tuplet som ett objekt ingår i. <c>Id</c> är unikt per parser-körning.</summary>
public sealed record TupletInst(int Id, int Normal, int Actual);

/// <summary>En not i ett <see cref="NotatedItem"/>. <c>NoteId</c> är <see cref="Note.Id"/> (xml:id i MEI).</summary>
public sealed record NotatedNote(string NoteId, int MidiPitch, int Tpc, int Tpc2, bool TieStart, bool TieEnd);

/// <summary>
/// Notbildsmodell för MeiExporter: ackord och pauser i skriven form (notvärde, punkter, tuplets). Avvikelse från IF-1.
/// <c>Tuplets</c> är ordnade från yttersta till innersta. <c>DurationType</c> är MuseScores namn ("quarter", "16th" ...).
/// </summary>
public sealed record NotatedItem(ItemKind Kind, int PartIndex, int StaffIndex, int Voice, int MeasureIndex,
    int ScoreTick, int DurationTicks, string DurationType, int Dots,
    IReadOnlyList<TupletInst> Tuplets, IReadOnlyList<NotatedNote> Notes);

public sealed record Measure(int Index, int StartScoreTick, int ActualLengthTicks,
    TimeSig NominalSig, KeySig Key, int RepeatPreviousMeasures = 0);

public sealed record VoltaInfo(IReadOnlyList<int> Endings, int LengthMeasures);

public sealed record JumpInfo(string JumpTo, string PlayUntil, string ContinueAt, bool PlayRepeats);

/// <summary>
/// Reprisstruktur per takt (A13). Avvikelse från IF-1: hålls separat från <see cref="Measure"/>
/// så att Measure-signaturen i §8 inte växer med spelordningsdetaljer.
/// </summary>
public sealed record MeasureMarks(
    bool StartRepeat, int EndRepeatCount, VoltaInfo? Volta,
    IReadOnlyList<JumpInfo> Jumps, IReadOnlyList<string> Markers)
{
    public static readonly MeasureMarks None =
        new(false, 0, null, Array.Empty<JumpInfo>(), Array.Empty<string>());
}

public readonly record struct DynamicEvent(string PartId, int StaffIndex, int ScoreTick, int Velocity);
public readonly record struct TempoEvent(int ScoreTick, double QuarterNotesPerSecond);

public enum DiagSeverity { Info, Warning, Error }
public sealed record Diagnostic(DiagSeverity Severity, string Code, string Message, string? Where = null);

public sealed record Score(
    string Title,
    IReadOnlyList<Part> Parts,
    IReadOnlyList<Measure> Measures,
    IReadOnlyList<MeasureMarks> Marks,
    IReadOnlyList<Note> Notes,
    IReadOnlyList<NotatedItem> Items,
    IReadOnlyList<DynamicEvent> Dynamics,
    IReadOnlyList<TempoEvent> TempoEvents,
    PlayOrder Order,
    TempoMap Tempo,
    string FormatVersion);

public sealed record LoadResult(Score? Score, IReadOnlyList<Diagnostic> Diagnostics);

public interface IScoreLoader
{
    Task<LoadResult> LoadAsync(Stream mscz, string? fileNameHint, CancellationToken ct);
}
