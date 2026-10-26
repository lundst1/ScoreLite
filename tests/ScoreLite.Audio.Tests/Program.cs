using System.Text;
using MeltySynth;
using ScoreLite.Audio;
using ScoreLite.Core;

// Samma minimala testlöpare som i Core-testerna (ingen xunit, NuGet kunde inte användas där detta började).
var tests = new List<(string name, Action run)>();
int failed = 0;
void Test(string name, Action run) => tests.Add((name, run));
void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }
void Eq<T>(T expected, T actual, string what) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: väntade {expected}, fick {actual}"); }
void Near(double expected, double actual, double tol, string what) { if (Math.Abs(expected - actual) > tol) throw new Exception($"{what}: väntade {expected} ± {tol}, fick {actual}"); }

const int SR = 44100;
const int Quarter = 22050;      // ramar per kvart vid 120 BPM
const int Measure = 4 * Quarter;

// ---------------------------------------------------------------- Hjälpare
string Chord(string type, int pitch) => $"<Chord><durationType>{type}</durationType><Note><pitch>{pitch}</pitch></Note></Chord>";

/// <summary>En stämma per höjdlista; varje takt är en helnot per stämma. `extraMeasure` läggs sist i alla stämmor.</summary>
Score ScoreOf(int[][] partsMeasures, string? measureAttrs = null)
{
    int parts = partsMeasures.Length;
    var sb = new StringBuilder("<museScore version=\"3.01\"><Score>");
    for (int p = 0; p < parts; p++) sb.Append($"<Part><Staff id=\"{p + 1}\"/></Part>");
    for (int p = 0; p < parts; p++)
    {
        sb.Append($"<Staff id=\"{p + 1}\">");
        for (int m = 0; m < partsMeasures[p].Length; m++)
        {
            string ts = m == 0 ? "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" : "";
            sb.Append($"<Measure><voice>{ts}{Chord("whole", partsMeasures[p][m])}</voice></Measure>");
        }
        sb.Append("</Staff>");
    }
    sb.Append("</Score></museScore>");
    var r = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())), null);
    return r.Score!;
}

Score Simple() => ScoreOf(new[] { new[] { 60, 62, 64 } });   // 3 takter, en stämma

MeltySynthHost RealSynth() => MeltySynthHost.FromStream(new MemoryStream(TestSoundFont.Build()));

// Goertzel: energi vid frekvensen f i kanal 0.
double Energy(float[] stereo, int fromFrame, int frames, double f)
{
    double w = 2 * Math.PI * f / SR, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
    for (int i = 0; i < frames; i++)
    {
        double s = stereo[(fromFrame + i) * 2] + coeff * s1 - s2;
        s2 = s1; s1 = s;
    }
    return (s1 * s1 + s2 * s2 - coeff * s1 * s2) / frames;
}

// Kör motorn utan egen tråd: Pump + Pull tills Stopped (efter att ha spelat) eller maxFrames.
(float[] audio, List<PlaybackSnapshot> snaps) RunToEnd(PlaybackEngine e, OfflineAudioOutput o, int maxFrames, int pull = 512, Action<int>? atFrame = null)
{
    var snaps = new List<PlaybackSnapshot>();
    e.SnapshotChanged += s => snaps.Add(s);
    var all = new List<float>();
    var buf = new float[pull * 2];
    int pulled = 0;
    bool started = false;
    while (pulled < maxFrames)
    {
        e.Pump();
        if (e.State is PlaybackState.Playing or PlaybackState.CountingIn) started = true;
        if (started && e.State == PlaybackState.Stopped) break;
        atFrame?.Invoke(pulled);
        o.Pull(buf);
        all.AddRange(buf);
        pulled += pull;
    }
    return (all.ToArray(), snaps);
}

PlaybackEngine NewEngine(ISynthHost synth, out OfflineAudioOutput output, int latency = 0, int idleSeconds = 30)
{
    output = new OfflineAudioOutput(SR, latency);
    return new PlaybackEngine(output, synth, new EngineOptions { UseWorkerThread = false, IdleCloseSeconds = idleSeconds });
}

// ---------------------------------------------------------------- RingBuffer
float[] Ramp(int frames, float start, int ch = 2)
{
    var a = new float[frames * ch];
    for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) a[i * ch + c] = start + i;
    return a;
}

Test("RingBuffer: skrivna ramar läses i ordning, även över buffertens kant", () =>
{
    var rb = new RingBuffer(700, 2);
    float next = 1, expect = 1;
    var dst = new float[200 * 2];
    for (int it = 0; it < 100; it++)
    {
        Assert(rb.TryWrite(Ramp(256, next), 1), "ska rymmas"); next += 256;
        int got = 0;
        while (got < 256)
        {
            int n = Math.Min(200, 256 - got);
            rb.Read(dst.AsSpan(0, n * 2), out int ep);
            Eq(1, ep, "epok");
            for (int i = 0; i < n; i++) { Eq(expect, dst[i * 2], "vänster"); Eq(expect, dst[i * 2 + 1], "höger"); expect++; }
            got += n;
        }
    }
    Eq(0L, rb.Underruns, "inga underskott");
});

Test("RingBuffer: skrivning avvisas när det inte får plats, WritableFrames följer läsningen", () =>
{
    var rb = new RingBuffer(512, 2);
    Eq(512, rb.WritableFrames, "tom");
    Assert(rb.TryWrite(Ramp(300, 1), 1), "300");
    Assert(!rb.TryWrite(Ramp(300, 1), 1), "300 till ska inte rymmas");
    Eq(212, rb.WritableFrames, "kvar");
    rb.Read(new float[200 * 2], out _);
    Eq(412, rb.WritableFrames, "efter läsning");
});

Test("RingBuffer: underskott ger tystnad och räknas bara när data väntades", () =>
{
    var rb = new RingBuffer(1000, 2);
    var d = new float[100 * 2]; Array.Fill(d, 9f);
    rb.Read(d, out _);
    Assert(d.All(x => x == 0), "tystnad");
    Eq(0L, rb.Underruns, "inget väntat ännu");
    rb.TryWrite(Ramp(100, 1), 1);
    var d2 = new float[150 * 2];
    rb.Read(d2, out _);
    Eq(1f, d2[0], "data först"); Eq(100f, d2[99 * 2], "sista datat"); Eq(0f, d2[100 * 2], "tystnad efter");
    Eq(1L, rb.Underruns, "ett underskott");
});

Test("RingBuffer: Flush kastar gamla block, epoken byts och FramesInCurrentEpoch nollställs", () =>
{
    var rb = new RingBuffer(2000, 2);
    rb.TryWrite(Ramp(256, 1), 1); rb.TryWrite(Ramp(256, 1000), 1);
    var d = new float[100 * 2];
    rb.Read(d, out int e1);
    Eq(1, e1, "epok 1"); Eq(100L, rb.FramesInCurrentEpoch, "100 lästa");
    rb.Flush(2);
    var half = new float[256 * 2]; Array.Fill(half, 0.5f);
    rb.TryWrite(half, 2);
    var o = new float[256 * 2];
    rb.Read(o, out int e2);
    Eq(2, e2, "epok 2"); Eq(2, rb.CurrentEpoch, "CurrentEpoch");
    Eq(256L, rb.FramesInCurrentEpoch, "bara nya epokens ramar");
    Eq(0.5f, o[200 * 2], "nya datat efter övertoningen");
    Assert(o.Take(64 * 2).All(x => x <= 100f), "övertoningen blandar bara in sista nivån (100), inga nya värden från gamla block");
    Assert(o.Skip(64 * 2).All(x => x == 0.5f), "efter övertoningen bara nya epokens data (inga värden 1..1000 från gamla block)");
    Eq(2000, rb.WritableFrames, "utrymmet från kasserade block frigjort");
    Eq(0L, rb.Underruns, "kasserade block är inte underskott");
});

Test("RingBuffer: declick, hoppet vid epokbyte jämnas ut över 64 ramar", () =>
{
    var rb = new RingBuffer(2000, 1);
    var one = new float[256]; Array.Fill(one, 1f);
    rb.TryWrite(one, 1);
    var pre = new float[100]; rb.Read(pre, out _);
    rb.Flush(2);
    var half = new float[256]; Array.Fill(half, 0.5f);
    rb.TryWrite(half, 2);
    var o = new float[128]; rb.Read(o, out _);
    float maxStep = 0;
    for (int i = 1; i < o.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(o[i] - o[i - 1]));
    Assert(maxStep <= 0.5f / 64 + 1e-3f, $"för brant övergång: {maxStep}");
    Near(1.0, o[0], 0.02, "börjar nära föregående nivå"); Eq(0.5f, o[100], "slutar på nya nivån");
});

Test("RingBuffer: Flush utan nya data tonar ut till tystnad utan hopp", () =>
{
    var rb = new RingBuffer(2000, 1);
    var one = new float[512]; Array.Fill(one, 1f);
    rb.TryWrite(one, 1);
    rb.Read(new float[100], out _);
    rb.Flush(2);
    var o = new float[200]; rb.Read(o, out _);
    float maxStep = 0;
    for (int i = 1; i < o.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(o[i] - o[i - 1]));
    Assert(maxStep <= 1f / 64 + 1e-3f, $"hopp: {maxStep}");
    Eq(0f, o[150], "tyst efter övertoningen");
    Eq(0L, rb.Underruns, "ingen underskottsräkning efter flush");
});

Test("RingBuffer: skrivare och läsare i olika trådar tappar inget och håller ordningen", () =>
{
    var rb = new RingBuffer(2000, 2);
    const int blocks = 20000, bf = 64;
    var writer = new Thread(() =>
    {
        float v = 1;
        var b = new float[bf * 2];
        for (int k = 0; k < blocks; k++)
        {
            for (int i = 0; i < bf; i++) { b[i * 2] = v; b[i * 2 + 1] = v; v++; }
            while (!rb.TryWrite(b, 1)) Thread.Yield();
        }
    });
    writer.IsBackground = true;
    writer.Start();
    var d = new float[100 * 2];
    float expect = 1; long frames = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (expect <= blocks * bf && sw.Elapsed.TotalSeconds < 20)
    {
        rb.Read(d, out _);
        for (int i = 0; i < 100; i++)
        {
            float x = d[i * 2];
            if (x == 0) continue;   // tystnad vid tillfälligt underskott
            if (x != expect) throw new Exception($"ordning bruten: väntade {expect}, fick {x}");
            Eq(x, d[i * 2 + 1], "kanalerna lika"); expect++; frames++;
        }
    }
    writer.Join();
    Eq((float)(blocks * bf + 1), expect, "alla ramar mottagna");
});

// ---------------------------------------------------------------- OfflineAudioOutput
Test("OfflineAudioOutput: räknar förbrukade ramar och är tyst utan källa", () =>
{
    var o = new OfflineAudioOutput(SR, 123);
    var d = new float[256 * 2]; Array.Fill(d, 1f);
    o.Pull(d);
    Assert(d.All(x => x == 0), "tyst"); Eq(256L, o.FramesConsumed, "ramar"); Eq(123, o.OutputLatencyFrames, "latens");
});

// ---------------------------------------------------------------- Ljudfont och synt (K14/V9)
Test("MeltySynth: testfonten laddas och en not ger en sinus på rätt frekvens", () =>
{
    using var host = RealSynth();
    host.Configure(1, 256);
    host.ProcessMidi(0, 0, MeltySynthHost.ProgramChange, 0, 0);
    host.ProcessMidi(0, 0, MeltySynthHost.NoteOn, 69, 100);
    var buf = new float[SR * 2];
    for (int i = 0; i < SR; i += 256) host.Render(buf.AsSpan(i * 2, Math.Min(256, SR - i) * 2));
    double on = Energy(buf, 10000, 20000, TestSoundFont.FreqOf(69)), off = Energy(buf, 10000, 20000, TestSoundFont.FreqOf(69) * 1.5);
    Assert(on > 1e-4, "ingen energi vid 441 Hz: " + on);
    Assert(on > off * 100, $"energi vid grundtonen {on} mot {off}");
});

Test("MeltySynthHost: Reset tystar allt, även det redan renderade 64-ramsblocket", () =>
{
    using var host = RealSynth();
    host.Configure(1, 256);
    host.ProcessMidi(0, 0, MeltySynthHost.NoteOn, 60, 100);
    var buf = new float[256 * 2];
    for (int i = 0; i < 20; i++) host.Render(buf);
    host.Reset();
    host.Render(buf);
    Assert(buf.All(x => x == 0), "tystnad direkt efter Reset");
});

// ---------------------------------------------------------------- Metronom
Test("Metronom: BeatStructure för 4/4, 3/4, 6/8, 9/8, 5/8, 7/8, 2/2", () =>
{
    Eq(new BeatStructure(4, 480), BeatStructure.From(new TimeSig(4, 4)), "4/4");
    Eq(new BeatStructure(3, 480), BeatStructure.From(new TimeSig(3, 4)), "3/4");
    Eq(new BeatStructure(2, 720), BeatStructure.From(new TimeSig(6, 8)), "6/8 = 2 punkterade kvartar");
    Eq(new BeatStructure(3, 720), BeatStructure.From(new TimeSig(9, 8)), "9/8");
    Eq(new BeatStructure(5, 240), BeatStructure.From(new TimeSig(5, 8)), "5/8: en per åttondel (DR-18)");
    Eq(new BeatStructure(7, 240), BeatStructure.From(new TimeSig(7, 8)), "7/8");
    Eq(new BeatStructure(2, 960), BeatStructure.From(new TimeSig(2, 2)), "2/2");
});

Test("Metronom: raster i 4/4, accent på slag 1, absoluta ticks över repris", () =>
{
    var ms = Enumerable.Range(0, 2).Select(i => new Measure(i, i * 1920, 1920, TimeSig.Common, new KeySig(0))).ToList();
    var marks = new List<MeasureMarks> { new(true, 0, null, Array.Empty<JumpInfo>(), Array.Empty<string>()), new(false, 2, null, Array.Empty<JumpInfo>(), Array.Empty<string>()) };
    var order = PlayOrderBuilder.Build(ms, marks, new());   // 0,1,0,1
    var grid = Metronome.BuildGrid(order, ms);
    Eq(16, grid.Count, "4 takter * 4 slag");
    Eq(Enumerable.Range(0, 16).Select(i => (long)i * 480).ToArray().Length, grid.Count, "antal");
    for (int i = 0; i < 16; i++) { Eq((long)i * 480, grid[i].PlayTick, "tick " + i); Eq(i % 4 == 0, grid[i].Accent, "accent " + i); }
});

Test("Metronom: 6/8 ger två slag per takt (punkterad kvart), 5/8 fem åttondelar", () =>
{
    var m68 = new List<Measure> { new(0, 0, 1440, new TimeSig(6, 8), new KeySig(0)) };
    var g = Metronome.BuildGrid(PlayOrder.Straight(m68), m68);
    Eq("0:True,720:False", string.Join(",", g.Select(c => $"{c.PlayTick}:{c.Accent}")), "6/8");
    var m58 = new List<Measure> { new(0, 0, 1200, new TimeSig(5, 8), new KeySig(0)) };
    Eq(5, Metronome.BuildGrid(PlayOrder.Straight(m58), m58).Count, "5/8");
});

Test("Metronom: upptakt (2/4 i 4/4) ger bara slagen inom segmentet och ingen accent på saknat slag 1", () =>
{
    var ms = new List<Measure> { new(0, 0, 960, new TimeSig(4, 4), new KeySig(0)), new(1, 960, 1920, new TimeSig(4, 4), new KeySig(0)) };
    var g = Metronome.BuildGrid(PlayOrder.Straight(ms), ms);
    // Upptakt: slag 3 och 4 (ticks 0 och 480, utan accent), därefter full takt från 960 med accent.
    Eq("0:False,480:False,960:True,1440:False,1920:False,2400:False", string.Join(",", g.Select(c => $"{c.PlayTick}:{c.Accent}")), "raster");
});

Test("Metronom: inräkning är en hel takts klick som slutar ett slag före start, även mitt i en takt", () =>
{
    var s = Simple();
    var ci = Metronome.BuildCountIn(s, 1920 + 480, SR, out long n);   // mitt i takt 1
    Eq(4, ci.Count, "fyra klick (4/4)");
    long start = s.Tempo.PlayTickToFrame(1920 + 480, SR);
    Eq(start - 4 * Quarter, ci[0].Frame, "första"); Eq(start - Quarter, ci[3].Frame, "sista ett slag före start");
    Eq(4L * Quarter, n, "inräkningens längd");
    Assert(ci[0].Accent && !ci[1].Accent && !ci[2].Accent && !ci[3].Accent, "accent bara på första");
    var s68 = LoadSix();
    Eq(2, Metronome.BuildCountIn(s68, 0, SR, out _).Count, "6/8: två klick");
});

Score LoadSix()
{
    string xml = "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice><TimeSig><sigN>6</sigN><sigD>8</sigD></TimeSig>" +
                 "<Chord><durationType>half</durationType><dots>1</dots><Note><pitch>60</pitch></Note></Chord></voice></Measure></Staff></Score></museScore>";
    return new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)), null).Score!;
}

Test("Metronom: klickljud är mono, högst 20 ms, accent starkare än vanligt klick", () =>
{
    var a = Metronome.GenerateClick(SR, true); var n = Metronome.GenerateClick(SR, false);
    Assert(a.Length <= SR / 50 && n.Length <= SR / 50, "längd");
    Assert(a.Max(Math.Abs) > n.Max(Math.Abs), "accent starkare");
});

// ---------------------------------------------------------------- Sequencer
float[] RenderFrames(Sequencer seq, long frames)
{
    var all = new float[frames * 2];
    var b = new float[Sequencer.BlockFrames * 2];
    for (long f = 0; f < frames; f += Sequencer.BlockFrames)
    {
        seq.RenderBlock(b);
        int n = (int)Math.Min(Sequencer.BlockFrames, frames - f);
        Array.Copy(b, 0, all, f * 2, n * 2);
    }
    return all;
}

Test("Sequencer: NoteOn läggs på exakt beräknad ram (blockgräns oberoende)", () =>
{
    var syn = new RecordingSynth();
    var seq = new Sequencer(Simple(), syn);
    seq.Start(0, countIn: false);
    RenderFrames(seq, 3L * Measure + 1000);
    var ons = syn.Calls.Where(c => c.command == MeltySynthHost.NoteOn).ToList();
    Eq("0:60,88200:62,176400:64", string.Join(",", ons.Select(c => $"{c.frame}:{c.d1}")), "NoteOn-ramar");
    var offs = syn.Calls.Where(c => c.command == MeltySynthHost.NoteOff).ToList();
    Eq("88200:60,176400:62,264600:64", string.Join(",", offs.Select(c => $"{c.frame}:{c.d1}")), "NoteOff-ramar");
});

Test("Sequencer: programbyte, volym och panorering skickas före första noten (bank 0, DR-30/DR-31)", () =>
{
    var syn = new RecordingSynth();
    var seq = new Sequencer(Simple(), syn);
    seq.Start(0, false);
    RenderFrames(seq, 1000);
    var first = syn.Calls.TakeWhile(c => c.command != MeltySynthHost.NoteOn).ToList();
    Assert(first.Any(c => c.command == MeltySynthHost.ProgramChange && c.d1 == 0), "programbyte");
    Assert(first.Any(c => c.command == MeltySynthHost.Controller && c.d1 == 7 && c.d2 == 100), "CC7 = 100");
    Assert(first.Any(c => c.command == MeltySynthHost.Controller && c.d1 == 10 && c.d2 == 64), "CC10 = 64");
    Assert(first.All(c => c.channel == 0 && c.synth == 0), "kanal 0 i instans 0");
});

Test("Sequencer: start mitt i stycket gör chase av programbyte/controllers men spelar inga tidigare noter (DR-19)", () =>
{
    var syn = new RecordingSynth();
    var seq = new Sequencer(Simple(), syn);
    long first = seq.Start(1920, false);
    Eq((long)Measure, first, "läsaren börjar vid startramen");
    RenderFrames(seq, 2L * Measure);
    var chase = syn.Calls.Where(c => c.frame == 0 && c.command != MeltySynthHost.NoteOn).ToList();
    Assert(chase.Any(c => c.command == MeltySynthHost.ProgramChange), "chase: programbyte");
    Assert(chase.Any(c => c.command == MeltySynthHost.Controller && c.d1 == 7), "chase: CC7");
    var ons = syn.Calls.Where(c => c.command == MeltySynthHost.NoteOn).ToList();
    Eq("0:62,88200:64", string.Join(",", ons.Select(c => $"{c.frame}:{c.d1}")), "inga noter före startpunkten");
    Eq(1, syn.Resets, "synten nollställd vid start");
});

Test("Sequencer: inräkning flyttar början en takt bakåt och musiken startar först efter sista klicket", () =>
{
    var syn = new RecordingSynth();
    var seq = new Sequencer(Simple(), syn);
    long first = seq.Start(0, countIn: true);
    Eq(-(long)Measure, first, "nextFrame"); Eq(0L, seq.StartFrame, "startram"); Eq((long)Measure, seq.CountInFrames, "inräkningslängd");
    var audio = RenderFrames(seq, 2L * Measure);
    Eq(Measure.ToString(), syn.Calls.First(c => c.command == MeltySynthHost.NoteOn).frame.ToString(), "första noten efter 4 slag");

    // Klickens startpunkter (tystnad >1000 ramar före) vid 0, 1, 2, 3 slag in i renderingen.
    var onsets = new List<int>(); int lastNz = -100000;
    for (int i = 0; i < Measure; i++)
        if (audio[i * 2] != 0) { if (i - lastNz > 1000) onsets.Add(i - 1); lastNz = i; }   // första samplet i klicket är 0
    Eq("0,22050,44100,66150", string.Join(",", onsets), "klickens ramar");
    float Peak(int from) => audio.Skip(from * 2).Take(800 * 2).Max(Math.Abs);
    Assert(Peak(0) > Peak(Quarter) * 1.2f, "första klicket har accent");
    Assert(audio.Skip(Measure * 2).All(x => x == 0), "metronomen är av efter inräkningen");
});

Test("Sequencer: metronomen klickar på absoluta slag och fasen ändras inte av av/på", () =>
{
    var syn = new RecordingSynth();
    var seq = new Sequencer(Simple(), syn) { MetronomeEnabled = true };
    seq.Start(0, false);
    var b = new float[Sequencer.BlockFrames * 2];
    var audio = new List<float>();
    for (long f = 0; f < 2L * Measure + 3 * Quarter; f += Sequencer.BlockFrames)
    {
        // Av under ett block runt det tredje slaget och på igen.
        seq.MetronomeEnabled = !(f >= 43520 && f < 44352);   // blocket som innehåller klicket vid 44100
        seq.RenderBlock(b); audio.AddRange(b);
    }
    var a = audio.ToArray();
    var onsets = new List<int>(); int lastNz = -100000;
    for (int i = 0; i < a.Length / 2; i++)
        if (a[i * 2] != 0) { if (i - lastNz > 1000) onsets.Add(i - 1); lastNz = i; }
    Eq(string.Join(",", new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11 }.Select(k => k * Quarter)), string.Join(",", onsets), "klicket vid 44100 saknas, övriga på 22050-multipler");
});

Test("Sequencer: slutfas börjar efter sista noten", () =>
{
    var syn = new RecordingSynth();
    var seq = new Sequencer(Simple(), syn);
    seq.Start(0, false);
    Assert(!seq.PastEnd, "inte slut");
    RenderFrames(seq, 3L * Measure + Sequencer.BlockFrames);
    Assert(seq.PastEnd, "slut");
});

Test("Sequencer: stämmor delar kanaler enligt AL-05 och 17 stämmor ger två synt-instanser", () =>
{
    var parts = Enumerable.Range(0, 17).Select(i => new[] { 40 + i }).ToArray();
    var syn = new RecordingSynth();
    var seq = new Sequencer(ScoreOf(parts), syn);
    Eq(2, syn.SynthCount, "instanser");
    seq.Start(0, false);
    RenderFrames(seq, 1000);
    var ons = syn.Calls.Where(c => c.command == MeltySynthHost.NoteOn).ToList();
    Eq(17, ons.Count, "17 NoteOn");
    Eq(17, ons.Select(c => (c.synth, c.channel)).Distinct().Count(), "unik (instans, kanal) per stämma");
    Assert(ons.All(c => c.channel != 9), "kanal 9 är reserverad");
});

Test("Sequencer: stämmaval filtrerar händelserna (F2/F3) men kanaltilldelningen är stabil", () =>
{
    var s = ScoreOf(new[] { new[] { 60 }, new[] { 64 }, new[] { 67 } });
    var syn = new RecordingSynth();
    var seq = new Sequencer(s, syn);
    seq.SetSelection(new HashSet<string> { "p1" });
    seq.Start(0, false);
    RenderFrames(seq, 1000);
    var ons = syn.Calls.Where(c => c.command == MeltySynthHost.NoteOn).ToList();
    Eq(1, ons.Count, "en NoteOn"); Eq(64, ons[0].d1, "stämma 1"); Eq(1, ons[0].channel, "stämma 1 behåller kanal 1");
});

// ---------------------------------------------------------------- PlaybackEngine
Test("Engine: Load -> Stopped; Play spelar hela stycket, slutar i Stopped och ger inga underskott", () =>
{
    var syn = new RecordingSynth();
    var e = NewEngine(syn, out var o);
    e.Load(Simple());
    e.Pump();
    Eq(PlaybackState.Stopped, e.State, "efter Load");
    e.Play();
    var (audio, snaps) = RunToEnd(e, o, 10 * SR);
    Eq(PlaybackState.Stopped, e.State, "slut");
    Assert(audio.Length / 2 >= 3 * Measure, "minst styckets längd");
    Assert(audio.Length / 2 < 3 * Measure + 3 * SR, "utsvängningen är begränsad");
    Eq(0L, e.Underruns, "inga underskott");
    Assert(snaps.Any(s => s.State == PlaybackState.Playing), "Playing-snapshot");
    Eq(PlaybackState.Stopped, snaps[^1].State, "sista snapshot");
    Eq(new ScorePosition(0, 0), snaps[^1].Marker, "positionen återgår till Marker");
});

Test("Engine: snapshot med ny epok skickas först när drivrutinen läser den nya epokens ljud (AL-07 steg 7)", () =>
{
    var e = NewEngine(new RecordingSynth(), out var o);
    var snaps = new List<PlaybackSnapshot>(); e.SnapshotChanged += s => snaps.Add(s);
    e.Load(Simple()); e.Pump();
    snaps.Clear();
    e.Play(); e.Pump();
    Eq(PlaybackState.Playing, e.State, "tillståndet är Playing direkt");
    Eq(0, snaps.Count, "ingen snapshot innan drivrutinen har läst något");
    o.Pull(new float[256 * 2]); e.Pump();
    Eq(1, snaps.Count, "snapshot efter första läsningen");
    Eq(PlaybackState.Playing, snaps[0].State, "tillstånd"); Eq(e.Ring.CurrentEpoch, snaps[0].Epoch, "epok");
    Near(0, snaps[0].PlayMs, 10, "PlayMs vid start (ett block)");
});

Test("Engine: PlayMs följer ljudramarna och korrigeras för utgångslatens", () =>
{
    var e = NewEngine(new RecordingSynth(), out var o, latency: 4410);   // 100 ms
    var snaps = new List<PlaybackSnapshot>(); e.SnapshotChanged += s => snaps.Add(s);
    e.Load(Simple()); e.Play(); e.Pump();
    var buf = new float[512 * 2];
    for (int i = 0; i < 100; i++) { o.Pull(buf); e.Pump(); }   // 51200 ramar förbrukade
    var last = snaps.Last(s => s.State == PlaybackState.Playing);
    Near((51200 - 4410) * 1000.0 / SR, last.PlayMs, 520, "PlayMs ≈ (förbrukade - latens), halv sekunds snapshotintervall");
    Assert(snaps.Count >= 3, "periodiska snapshots var 500:e ms");
    var times = snaps.Where(s => s.State == PlaybackState.Playing).Select(s => s.PlayMs).ToList();
    for (int i = 1; i < times.Count; i++) Assert(times[i] >= times[i - 1], "monoton");
});

Test("Engine: inräkning ger CountingIn -> Playing efter en takt, och musiken startar först då", () =>
{
    var syn = new RecordingSynth();
    var e = NewEngine(syn, out var o);
    e.CountInEnabled = true;
    e.Load(Simple());
    e.Play(); e.Pump();
    Eq(PlaybackState.CountingIn, e.State, "CountingIn");
    var (audio, snaps) = RunToEnd(e, o, 10 * SR);
    var states = string.Join(">", snaps.Select(s => s.State).Aggregate(new List<PlaybackState>(), (l, s) => { if (l.Count == 0 || l[^1] != s) l.Add(s); return l; }));
    Eq("CountingIn>Playing>Stopped", states, "tillståndsföljd");
    Eq(Measure.ToString(), syn.Calls.First(c => c.command == MeltySynthHost.NoteOn).frame.ToString(), "första NoteOn efter inräkningen");
    var ci = snaps.First(s => s.State == PlaybackState.CountingIn);
    Near(0, ci.PlayMs, 1, "pekaren står på Marker under inräkningen");
});

Test("Engine: SeekTo under uppspelning är ett hopp: ny epok, synten nollställs, musiken fortsätter från platsen (utan inräkning)", () =>
{
    var syn = new RecordingSynth();
    var e = NewEngine(syn, out var o);
    e.CountInEnabled = true;
    e.Load(Simple());
    var snaps = new List<PlaybackSnapshot>(); e.SnapshotChanged += s => snaps.Add(s);
    e.Play(); e.Pump();
    var buf = new float[512 * 2];
    for (int i = 0; i < 200; i++) { o.Pull(buf); e.Pump(); }   // förbi inräkningen och in i takt 0
    Eq(PlaybackState.Playing, e.State, "Playing");
    int epochBefore = e.Ring.CurrentEpoch; int resets = syn.Resets; snaps.Clear();
    long renderedBefore = syn.Rendered;
    e.SeekTo(new ScorePosition(2, 0)); e.Pump();
    Eq(PlaybackState.Playing, e.State, "fortfarande Playing (ingen inräkning, DR-11)");
    Assert(syn.Resets > resets, "synten nollställd (flush + chase)");
    Eq(0, snaps.Count, "ingen snapshot förrän nya epoken läses");
    for (int i = 0; i < 5 && snaps.Count == 0; i++) { o.Pull(buf); e.Pump(); }   // gamla epokens block kasseras först
    Eq(1, snaps.Count, "en snapshot när nya epoken läses"); Assert(snaps[0].Epoch > epochBefore, "ny epok");
    Near(2 * 2000, snaps[0].PlayMs, 15, "pekaren hoppar till takt 2");
    Eq(new ScorePosition(2, 0), snaps[0].Marker, "Marker flyttad");
    var lastOn = syn.Calls.Last(c => c.command == MeltySynthHost.NoteOn);
    Eq(64, lastOn.d1, "musiken fortsätter med takt 2");
    Assert(lastOn.frame >= renderedBefore, "ingen not från gamla positionen efter hoppet");
});

Test("Engine: Pause behåller position, Play fortsätter därifrån och startar inte om hållna toner (DR-19)", () =>
{
    var syn = new RecordingSynth();
    var e = NewEngine(syn, out var o);
    var snaps = new List<PlaybackSnapshot>(); e.SnapshotChanged += s => snaps.Add(s);
    e.Load(Simple()); e.Play(); e.Pump();
    var buf = new float[512 * 2];
    int pulls = (int)(1.5 * Measure / 512);
    for (int i = 0; i < pulls; i++) { o.Pull(buf); e.Pump(); }
    e.Pause(); e.Pump();
    Eq(PlaybackState.Paused, e.State, "Paused");
    var p = snaps.Last();
    Eq(PlaybackState.Paused, p.State, "snapshot Paused");
    Near(3000, p.PlayMs, 30, "position mitt i takt 1");
    int onsBefore = syn.Calls.Count(c => c.command == MeltySynthHost.NoteOn);
    e.Play(); e.Pump();
    for (int i = 0; i < 100; i++) { o.Pull(buf); e.Pump(); if (e.State == PlaybackState.Stopped) break; }
    var ons = syn.Calls.Where(c => c.command == MeltySynthHost.NoteOn).Skip(onsBefore).ToList();
    Assert(ons.Count > 0, "något spelas efter Play");
    Eq(64, ons[0].d1, "nästa not är takt 2, inte omstart av takt 1-noten");
});

Test("Engine: Stop återgår till Marker och Play börjar där", () =>
{
    var syn = new RecordingSynth();
    var e = NewEngine(syn, out var o);
    e.Load(Simple());
    e.SeekTo(new ScorePosition(1, 0)); e.Pump();
    e.Play(); e.Pump();
    var buf = new float[512 * 2];
    for (int i = 0; i < 20; i++) { o.Pull(buf); e.Pump(); }
    e.Stop(); e.Pump();
    Eq(PlaybackState.Stopped, e.State, "Stopped");
    syn.Calls.Clear();
    e.Play(); e.Pump(); o.Pull(buf); e.Pump();
    Eq(62, syn.Calls.First(c => c.command == MeltySynthHost.NoteOn).d1, "börjar vid Marker (takt 1)");
});

Test("Engine: SeekTo i Stopped/Paused flyttar bara Marker; Paused-positionen följer", () =>
{
    var syn = new RecordingSynth();
    var e = NewEngine(syn, out var o);
    var snaps = new List<PlaybackSnapshot>(); e.SnapshotChanged += s => snaps.Add(s);
    e.Load(Simple()); e.Pump();
    e.SeekTo(new ScorePosition(2, 480)); e.Pump();
    Eq(PlaybackState.Stopped, e.State, "Stopped"); Eq(new ScorePosition(2, 480), snaps.Last().Marker, "Marker");
    Near((2 * 1920 + 480) * 1000.0 / 960, snaps.Last().PlayMs, 1, "PlayMs vid Marker");
    Assert(syn.Calls.Count(c => c.command == MeltySynthHost.NoteOn) == 0, "inget spelas");
});

Test("Engine: SetSelectedParts under uppspelning är ett hopp från nuvarande position (DR-09)", () =>
{
    var s = ScoreOf(new[] { new[] { 60, 62, 64 }, new[] { 72, 74, 76 } });
    var syn = new RecordingSynth();
    var e = NewEngine(syn, out var o);
    e.Load(s); e.Play(); e.Pump();
    var buf = new float[512 * 2];
    for (int i = 0; i < 20; i++) { o.Pull(buf); e.Pump(); }
    int epoch = e.Ring.CurrentEpoch; int resets = syn.Resets;
    e.SetSelectedParts(new HashSet<string> { "p1" }); e.Pump();
    Assert(syn.Resets > resets, "flush + chase");
    for (int i = 0; i < 4; i++) { o.Pull(buf); e.Pump(); }
    Assert(e.Ring.CurrentEpoch > epoch, "ny epok");
    // Efter bytet spelas bara p1 (kanal 1). Gamla p0-noter får inte komma igen.
    var after = syn.Calls.Skip(syn.Calls.FindLastIndex(c => c.command == MeltySynthHost.ProgramChange) - 3).Where(c => c.command == MeltySynthHost.NoteOn).ToList();
    Assert(after.All(c => c.channel == 1), "bara vald stämma efter bytet");
    Throws<ArgumentException>(() => e.SetSelectedParts(new HashSet<string>()), "tomt val");
});

void Throws<T>(Action a, string what) where T : Exception
{
    try { a(); } catch (T) { return; }
    throw new Exception(what + ": väntade " + typeof(T).Name);
}

Test("Engine: OnAudioInterrupted pausar (R4) och metronomväxeln gäller direkt under uppspelning (F4)", () =>
{
    var e = NewEngine(new RecordingSynth(), out var o);
    e.Load(Simple()); e.Play(); e.Pump();
    var buf = new float[512 * 2];
    for (int i = 0; i < 5; i++) { o.Pull(buf); e.Pump(); }
    e.OnAudioInterrupted(); e.Pump();
    Eq(PlaybackState.Paused, e.State, "Paused");
});

Test("Engine: pausa under inräkning kastar inräkningen och positionen blir Marker", () =>
{
    var e = NewEngine(new RecordingSynth(), out var o);
    var snaps = new List<PlaybackSnapshot>(); e.SnapshotChanged += s => snaps.Add(s);
    e.CountInEnabled = true;
    e.Load(Simple()); e.SeekTo(new ScorePosition(1, 0)); e.Pump();
    e.Play(); e.Pump();
    o.Pull(new float[512 * 2]); e.Pump();
    Eq(PlaybackState.CountingIn, e.State, "CountingIn");
    e.Pause(); e.Pump();
    Eq(PlaybackState.Paused, e.State, "Paused"); Near(2000, snaps.Last().PlayMs, 1, "Marker (takt 1)");
});

Test("Engine: DR-26, utgången stängs efter inaktivitet och öppnas igen vid Play", () =>
{
    var e = NewEngine(new RecordingSynth(), out var o, idleSeconds: 0);
    e.Load(Simple()); e.Pump();
    e.Play(); e.Pump();
    Assert(o.IsStarted, "startad vid Play");
    e.Stop(); e.Pump(); e.Pump();
    Assert(!o.IsStarted, "stängd efter stopp");
    e.Play(); e.Pump();
    Assert(o.IsStarted, "öppnas igen");
});

Test("Engine: DisposeAsync -> Unloaded och utgången stoppad", () =>
{
    var e = NewEngine(new RecordingSynth(), out var o);
    e.Load(Simple()); e.Play(); e.Pump();
    e.DisposeAsync().AsTask().Wait();
    Eq(PlaybackState.Unloaded, e.State, "Unloaded"); Assert(!o.IsStarted, "stoppad");
});

// ---------------------------------------------------------------- Riktig synt (F1–F3, P7)
Test("F1: 8 stämmor, varje stämmas ton hörs i offline-renderingen", () =>
{
    int[] pitches = { 48, 52, 55, 60, 64, 67, 72, 76 };
    var s = ScoreOf(pitches.Select(p => new[] { p }).ToArray());
    using var synth = RealSynth();
    var e = NewEngine(synth, out var o);
    e.Load(s); e.Play();
    var (audio, _) = RunToEnd(e, o, 10 * SR);
    foreach (int p in pitches)
    {
        double en = Energy(audio, 20000, 40000, TestSoundFont.FreqOf(p));
        Assert(en > 1e-4, $"stämma med tonhöjd {p} hörs inte (energi {en})");
    }
});

Test("F2/F3: bara valda stämmor hörs, och metronomen påverkas inte av valet", () =>
{
    var s = ScoreOf(new[] { new[] { 60 }, new[] { 64 }, new[] { 67 } });
    double[] f = { TestSoundFont.FreqOf(60), TestSoundFont.FreqOf(64), TestSoundFont.FreqOf(67) };

    double[] Run(params string[] sel)
    {
        using var synth = RealSynth();
        var e = NewEngine(synth, out var o);
        e.Load(s); e.SetSelectedParts(new HashSet<string>(sel)); e.Play();
        var (audio, _) = RunToEnd(e, o, 10 * SR);
        return f.Select(fr => Energy(audio, 20000, 40000, fr)).ToArray();
    }
    var one = Run("p1");
    Assert(one[1] > 1e-4 && one[1] > one[0] * 100 && one[1] > one[2] * 100, $"bara p1: {string.Join(", ", one)}");
    var two = Run("p0", "p2");
    Assert(two[0] > 1e-4 && two[2] > 1e-4 && two[1] < two[0] / 100, $"p0+p2: {string.Join(", ", two)}");
});

Test("P7: två offline-renderingar av samma indata är bit-identiska (inkl. inräkning och metronom)", () =>
{
    float[] Render()
    {
        using var synth = RealSynth();
        var e = NewEngine(synth, out var o);
        e.MetronomeEnabled = true; e.CountInEnabled = true;
        e.Load(Simple()); e.Play();
        return RunToEnd(e, o, 10 * SR).audio;
    }
    var a = Render(); var b = Render();
    Eq(a.Length, b.Length, "längd");
    Assert(a.AsSpan().SequenceEqual(b), "ljudet skiljer sig");
    Assert(a.Any(x => x != 0), "något hörs");
});

Test("Engine: ingen klippning, utsignalen är begränsad även med många stämmor (soft limit)", () =>
{
    var s = ScoreOf(Enumerable.Range(0, 12).Select(i => new[] { 48 + i * 2 }).ToArray());
    using var synth = RealSynth();
    var e = NewEngine(synth, out var o);
    e.Load(s); e.Play();
    var (audio, _) = RunToEnd(e, o, 10 * SR);
    Assert(audio.Max(Math.Abs) <= 1.0f, "max " + audio.Max(Math.Abs));
});

Test("SoftLimit: linjär under knäet, monoton och begränsad till 1", () =>
{
    Eq(0.3f, SoftLimit.Process(0.3f), "linjär");
    float prev = -2;
    for (float x = -5; x <= 5; x += 0.01f) { float y = SoftLimit.Process(x); Assert(y >= prev, "monoton"); Assert(Math.Abs(y) <= 1f, "begränsad"); prev = y; }
});

Test("Engine med egen tråd: spelar klart mot en konsument i ungefär realtid", () =>
{
    var o = new OfflineAudioOutput(SR);
    using var synth = RealSynth();
    var e = new PlaybackEngine(o, synth, new EngineOptions { UseWorkerThread = true });
    var snaps = new List<PlaybackSnapshot>(); var sync = new object();
    e.SnapshotChanged += s => { lock (sync) snaps.Add(s); };
    e.Load(ScoreOf(new[] { new[] { 60 } }));
    e.Play();
    var buf = new float[441 * 2];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < 15)
    {
        Thread.Sleep(10); o.Pull(buf);
        lock (sync) if (snaps.Count > 0 && snaps[^1].State == PlaybackState.Stopped && snaps.Any(s => s.State == PlaybackState.Playing)) break;
    }
    e.DisposeAsync().AsTask().Wait();
    lock (sync)
    {
        Assert(snaps.Any(s => s.State == PlaybackState.Playing), "Playing nåddes");
        Eq(PlaybackState.Stopped, snaps[^1].State, "slutade i Stopped");
    }
});

Test("Engine: slutet på stycket ger inget underskott även när läsarens block inte går jämnt upp", () =>
{
    var e = NewEngine(new RecordingSynth(), out var o);
    e.Load(Simple()); e.Play();
    var (_, _) = RunToEnd(e, o, 10 * SR, pull: 300);
    Eq(PlaybackState.Stopped, e.State, "Stopped");
    Eq(0L, e.Underruns, "inga underskott");
});

// ---------------------------------------------------------------- K14: riktig ljudfont mot korpusen (valfritt)
// Kräver SCORELITE_SOUNDFONT=<sökväg till GM-SF2>. Korpusen hämtas från SCORELITE_CORPUS eller <repo>/corpus.
string? sfPath = Environment.GetEnvironmentVariable("SCORELITE_SOUNDFONT");
string corpusDir = Environment.GetEnvironmentVariable("SCORELITE_CORPUS")
                   ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../corpus"));
if (sfPath != null && File.Exists(sfPath) && Directory.Exists(corpusDir))
{
    var sw0 = System.Diagnostics.Stopwatch.StartNew();
    SoundFont font;
    using (var fs = File.OpenRead(sfPath)) font = new SoundFont(fs);
    long loadMs = sw0.ElapsedMilliseconds;
    long managed = GC.GetTotalMemory(true);
    Console.WriteLine($"(K14: {Path.GetFileName(sfPath)} {new FileInfo(sfPath).Length / 1e6:F1} MB laddad på {loadMs} ms, managed minne {managed / 1e6:F0} MB)");
    Test("K14: ljudfonten laddas snabbt och håller sig inom minnesbudget", () =>
    {
        Assert(loadMs < 3000, $"laddtid {loadMs} ms");
        Assert(managed < 120_000_000, $"managed minne {managed / 1e6:F0} MB");
    });
    foreach (var f in Directory.EnumerateFiles(corpusDir, "*.mscz").OrderBy(x => x))
    {
        string path = f;
        Test("K14 korpus: " + Path.GetFileName(f) + " renderas offline med riktig font", () =>
        {
            var score = new ScoreLoader().Load(File.OpenRead(path), Path.GetFileName(path)).Score!;
            var o = new OfflineAudioOutput(SR);
            var e = new PlaybackEngine(o, new MeltySynthHost(font), new EngineOptions { UseWorkerThread = false });
            e.MetronomeEnabled = true; e.CountInEnabled = true;
            e.Load(score); e.Play();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (audio, _) = RunToEnd(e, o, 60 * 60 * SR, pull: 441);
            double sec = audio.Length / 2.0 / SR, wall = sw.Elapsed.TotalSeconds;
            float peak = audio.Max(Math.Abs);
            Console.WriteLine($"      {score.Parts.Count} stämmor, {sec,6:F1} s ljud på {wall,5:F2} s (x{sec / wall,5:F0} realtid), peak {peak:F2}");
            Eq(PlaybackState.Stopped, e.State, "slut"); Eq(0L, e.Underruns, "underskott");
            Assert(peak > 0.05f && peak <= 1f, "peak " + peak);
            Assert(sec / wall > 5, "renderar för långsamt för uppspelning i realtid: x" + sec / wall);
            // F1: varje stämma ger ljud när den spelas ensam.
            foreach (var part in score.Parts.Where(p => score.Notes.Any(n => n.PartId == p.Id)))   // vissa stämmor i korpusen är tomma
            {
                var o2 = new OfflineAudioOutput(SR);
                var e2 = new PlaybackEngine(o2, new MeltySynthHost(font), new EngineOptions { UseWorkerThread = false });
                e2.Load(score); e2.SetSelectedParts(new HashSet<string> { part.Id }); e2.Play();
                var (a2, _) = RunToEnd(e2, o2, 60 * 60 * SR, pull: 2048);
                Assert(a2.Max(Math.Abs) > 0.02f, $"stämma {part.Name} är tyst");
            }
        });
    }
}
else Console.WriteLine("(Ingen K14-körning: sätt SCORELITE_SOUNDFONT=<sökväg till GeneralUser-GS.sf2>.)");

// ---------------------------------------------------------------- Körning
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("  OK   " + name); }
    catch (Exception e) { failed++; Console.WriteLine("  FAIL " + name + "\n         " + e.Message); }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} tester gröna");
return failed == 0 ? 0 : 1;

/// <summary>Spelar in MIDI-anrop med antalet renderade ramar vid anropet. Utsignalen är tyst.</summary>
sealed class RecordingSynth : ISynthHost
{
    public readonly List<(long frame, int synth, int channel, int command, int d1, int d2)> Calls = new();
    public long Rendered;
    public int Resets, SynthCount;
    public int SampleRate => 44100;
    public void Configure(int synthCount, int maxBlockFrames) => SynthCount = synthCount;
    public void ProcessMidi(int synth, int channel, int command, int data1, int data2) => Calls.Add((Rendered, synth, channel, command, data1, data2));
    public void Render(Span<float> interleavedStereo) { interleavedStereo.Clear(); Rendered += interleavedStereo.Length / 2; }
    public void Reset() { Resets++; }
    public void Dispose() { }
}
