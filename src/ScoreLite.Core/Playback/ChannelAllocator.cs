namespace ScoreLite.Core;

/// <summary>Synt-instans och MIDI-kanal (0–15) för en stämma.</summary>
public readonly record struct ChannelAssignment(int Synth, int Channel);

/// <summary>
/// AL-05. Stabil kanaltilldelning som inte beror på stämmaval. Stämmor i filens ordning får nästa lediga kanal
/// 0–15 utom 9 i instans <c>s</c>, därefter instans <c>s + 1</c>. Slagverksstämmor delar kanal 9 i instans 0.
/// </summary>
public static class ChannelAllocator
{
    public const int PercussionChannel = 9;
    public const int ChannelsPerSynth = 15;   // 16 minus slagverkskanalen

    public static IReadOnlyDictionary<string, ChannelAssignment> Allocate(IReadOnlyList<Part> parts)
    {
        var map = new Dictionary<string, ChannelAssignment>(StringComparer.Ordinal);
        int used = 0;   // antal tilldelade melodiska kanaler totalt
        foreach (var p in parts)
        {
            bool perc = p.Instruments.Count > 0 && p.Instruments[0].IsPercussion;
            if (perc) { map[p.Id] = new ChannelAssignment(0, PercussionChannel); continue; }
            int synth = used / ChannelsPerSynth, slot = used % ChannelsPerSynth;
            int channel = slot < PercussionChannel ? slot : slot + 1;   // hoppa över 9
            map[p.Id] = new ChannelAssignment(synth, channel);
            used++;
        }
        return map;
    }

    /// <summary>Antal synt-instanser som krävs (minst 1).</summary>
    public static int SynthCount(IReadOnlyDictionary<string, ChannelAssignment> map) =>
        map.Count == 0 ? 1 : map.Values.Max(a => a.Synth) + 1;
}
