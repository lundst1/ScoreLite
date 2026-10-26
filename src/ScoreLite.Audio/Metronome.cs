using ScoreLite.Core;

namespace ScoreLite.Audio;

/// <summary>AL-09 regel 1: antal slag och slaglängd i ticks för en taktart.</summary>
public readonly record struct BeatStructure(int Beats, int BeatTicks)
{
    public static BeatStructure From(TimeSig sig)
    {
        // Sammansatt taktart (6/8, 9/8, 12/8): ett slag är tre åttondelar. Annars ett slag per 1/den (DR-18).
        if (sig.Denominator == 8 && sig.Numerator is 6 or 9 or 12)
            return new BeatStructure(sig.Numerator / 3, 3 * Time.TicksPerWhole / 8);
        return new BeatStructure(Math.Max(1, sig.Numerator), Time.TicksPerWhole / Math.Max(1, sig.Denominator));
    }
}

public readonly record struct Click(long PlayTick, bool Accent);

/// <summary>AL-09 och AL-10: slagraster, inräkning och syntetiska klickljud (DR-17, ingen tredjepartslicens).</summary>
public static class Metronome
{
    /// <summary>Hela stycket i absoluta PlayTick (regel 5: fasen beror inte på om metronomen är på eller av).</summary>
    public static IReadOnlyList<Click> BuildGrid(PlayOrder order, IReadOnlyList<Measure> measures)
    {
        var clicks = new List<Click>();
        foreach (var seg in order.Segments)
        {
            var m = measures[seg.MeasureIndex];
            var bs = BeatStructure.From(m.NominalSig);
            long segEnd = seg.PlayStartTick + seg.LengthTicks;
            int nominal = bs.Beats * bs.BeatTicks;
            // Regel 2: upptakt (kortare än nominell längd) har ursprung vid segmentslut minus nominell längd.
            long origin = seg.LengthTicks < nominal ? segEnd - nominal : seg.PlayStartTick;
            for (int k = 0; k < bs.Beats; k++)
            {
                long tick = origin + (long)k * bs.BeatTicks;
                if (tick < seg.PlayStartTick || tick >= segEnd) continue;   // bara slag inom segmentet
                clicks.Add(new Click(tick, Accent: k == 0));
            }
        }
        return clicks;
    }

    /// <summary>
    /// AL-10: absoluta ramar för inräkningens klick (första är accent), slutande vid <paramref name="startFrame"/>.
    /// Hel takts klick även om start är mitt i en takt.
    /// </summary>
    public static IReadOnlyList<(long Frame, bool Accent)> BuildCountIn(Score score, long startTick, int sampleRate, out long countInFrames)
    {
        var pos = score.Order.ToScorePosition(startTick);
        var sig = score.Measures[Math.Clamp(pos.MeasureIndex, 0, score.Measures.Count - 1)].NominalSig;
        var bs = BeatStructure.From(sig);
        long startFrame = score.Tempo.PlayTickToFrame(startTick, sampleRate);
        long beatFrames = score.Tempo.PlayTickToFrame(startTick + bs.BeatTicks, sampleRate) - startFrame;
        var list = new List<(long, bool)>(bs.Beats);
        for (int k = 0; k < bs.Beats; k++)
            list.Add((startFrame - (long)(bs.Beats - k) * beatFrames, k == 0));
        countInFrames = bs.Beats * beatFrames;
        return list;
    }

    /// <summary>Mono, högst 20 ms, fast förstärkning. Dämpad sinuston: accent högre och ljusare.</summary>
    public static float[] GenerateClick(int sampleRate, bool accent)
    {
        int n = sampleRate * 15 / 1000;
        double freq = accent ? 1760 : 1175, gain = accent ? 0.5 : 0.35;
        var buf = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double env = Math.Exp(-t * 300);
            buf[i] = (float)(Math.Sin(2 * Math.PI * freq * t) * env * gain);
        }
        return buf;
    }
}
