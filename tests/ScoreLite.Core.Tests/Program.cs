using System.IO.Compression;
using System.Text;
using ScoreLite.Core;

// Minimal testlöpare. xunit kräver NuGet och kunde inte användas i bygget där detta skrevs.
// Byt till xunit när restore fungerar: testmetoderna är rena funktioner utan delat tillstånd.
var tests = new List<(string name, Action run)>();
int failed = 0;
void Test(string name, Action run) => tests.Add((name, run));
void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }
void Eq<T>(T expected, T actual, string what) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: väntade {expected}, fick {actual}"); }

string corpusDir = Environment.GetEnvironmentVariable("SCORELITE_CORPUS")
                   ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../corpus"));

// ---------------------------------------------------------------- Arkitekturregler
Test("P2: Core refererar bara till .NET-basbiblioteket", () =>
{
    var allowed = new[] { "System", "Microsoft.Win32", "netstandard", "mscorlib", "ScoreLite.Core" };
    var bad = typeof(Frac).Assembly.GetReferencedAssemblies()
        .Where(a => !allowed.Any(p => a.Name!.StartsWith(p, StringComparison.Ordinal))).Select(a => a.Name).ToList();
    Assert(bad.Count == 0, "otillåtna beroenden: " + string.Join(", ", bad));
});

// ---------------------------------------------------------------- Frac och tempo
Test("Frac: aritmetik och tick-avrundning", () =>
{
    Eq(Frac.Of(1, 2), Frac.Of(2, 4), "normalisering");
    Eq(960, Frac.Of(1, 2).ToTicks(), "halvnot");
    Eq(160, (Frac.Of(1, 8) * Frac.Of(2, 3)).ToTicks(), "triolåttondel");
    Assert(Frac.TryParse("-1/4", out var f) && f.ToTicks() == -480, "negativt bråk");
    Assert(Frac.Of(1, 7).IsExactTicks == false, "1/7 helnot är inte exakt");
});

Test("TempoMap: konstant 120 BPM ger 22050 ramar per kvart vid 44100 Hz", () =>
{
    var t = TempoMap.Constant();
    Eq(22050L, t.PlayTickToFrame(480, 44100), "kvart");
    Eq(480L, t.FrameToPlayTick(22050, 44100), "invers");
});

Test("TempoMap: tempoändring är monoton och korrekt", () =>
{
    var t = new TempoMap(new[] { new TempoPoint(0, 2.0), new TempoPoint(960, 1.0) });
    long a = t.PlayTickToFrame(960, 44100), b = t.PlayTickToFrame(1440, 44100);
    Eq(44100L, a, "två kvartar i 120 BPM = 1 s");
    Eq(44100L + 44100L, b, "en kvart i 60 BPM = 1 s till");
    long prev = -1;
    for (long k = 0; k < 4000; k += 7) { long f = t.PlayTickToFrame(k, 44100); Assert(f >= prev, "monotont"); prev = f; }
});

// ---------------------------------------------------------------- Spelordning (syntetisk)
Measure M(int i, int len = 1920) => new(i, i * len, len, TimeSig.Common, new KeySig(0));
MeasureMarks Mk(bool start = false, int end = 0, VoltaInfo? volta = null, JumpInfo[]? jumps = null, string[]? markers = null) =>
    new(start, end, volta, jumps ?? Array.Empty<JumpInfo>(), markers ?? Array.Empty<string>());
string Order(PlayOrder o) => string.Join(",", o.Segments.Select(s => s.MeasureIndex));

Test("PlayOrder: rakt igenom", () =>
{
    var ms = Enumerable.Range(0, 4).Select(i => M(i)).ToList();
    var o = PlayOrderBuilder.Build(ms, ms.Select(_ => MeasureMarks.None).ToList(), new());
    Eq("0,1,2,3", Order(o), "ordning");
    Eq(4L * 1920, o.TotalTicks, "längd");
});

Test("PlayOrder: enkel repris", () =>
{
    var ms = Enumerable.Range(0, 4).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks> { MeasureMarks.None, Mk(start: true), Mk(end: 2), MeasureMarks.None };
    Eq("0,1,2,1,2,3", Order(PlayOrderBuilder.Build(ms, mk, new())), "repris");
});

Test("PlayOrder: repris med 1:a och 2:a volta", () =>
{
    // |: A | B(volta 1) :| C(volta 2) | D
    var ms = Enumerable.Range(0, 4).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks>
    {
        Mk(start: true),
        Mk(end: 2, volta: new VoltaInfo(new[] { 1 }, 1)),
        Mk(volta: new VoltaInfo(new[] { 2 }, 1)),
        MeasureMarks.None,
    };
    Eq("0,1,0,2,3", Order(PlayOrderBuilder.Build(ms, mk, new())), "volta");
});

Test("PlayOrder: D.S. al Coda", () =>
{
    // 0 | 1(segno) | 2(to coda) | 3(D.S. al Coda) | 4(coda) | 5
    var ms = Enumerable.Range(0, 6).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks>
    {
        MeasureMarks.None,
        Mk(markers: new[] { "segno" }),
        Mk(markers: new[] { "coda" }),
        Mk(jumps: new[] { new JumpInfo("segno", "coda", "codab", false) }),
        Mk(markers: new[] { "codab" }),
        MeasureMarks.None,
    };
    Eq("0,1,2,3,1,2,4,5", Order(PlayOrderBuilder.Build(ms, mk, new())), "D.S. al Coda");
});

Test("PlayOrder: D.C. al Fine stannar vid Fine", () =>
{
    var ms = Enumerable.Range(0, 4).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks>
    {
        MeasureMarks.None, Mk(markers: new[] { "fine" }), Mk(),
        Mk(jumps: new[] { new JumpInfo("start", "fine", "", false) }),
    };
    Eq("0,1,2,3,0,1", Order(PlayOrderBuilder.Build(ms, mk, new())), "D.C. al Fine");
});

Test("PlayOrder: saknad markör ger fallback, aldrig krasch", () =>
{
    var ms = Enumerable.Range(0, 3).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks> { MeasureMarks.None, MeasureMarks.None, Mk(jumps: new[] { new JumpInfo("segno", "end", "", false) }) };
    var diag = new List<Diagnostic>();
    Eq("0,1,2", Order(PlayOrderBuilder.Build(ms, mk, diag)), "fallback");
    Assert(diag.Any(d => d.Code == "PLAYORDER_FALLBACK"), "diagnostic");
});

Test("PlayOrder: ToScorePosition och FirstPlayTick", () =>
{
    var ms = Enumerable.Range(0, 3).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks> { Mk(start: true), Mk(end: 2), MeasureMarks.None };
    var o = PlayOrderBuilder.Build(ms, mk, new());   // 0,1,0,1,2
    Eq(1920L + 100, o.FirstPlayTick(new ScorePosition(1, 100)), "första");
    Eq(2, o.AllPlayTicks(new ScorePosition(1, 100)).Count, "förekomster");
    Eq(new ScorePosition(0, 50), o.ToScorePosition(2 * 1920 + 50), "andra genomspelningens takt 0");
    Eq(new ScorePosition(1, 50), o.ToScorePosition(3 * 1920 + 50), "andra genomspelningens takt 1");
});

Test("PlayOrder (steg 2): avsnitt utan startrepris börjar efter föregående slutrepris", () =>
{
    var ms = Enumerable.Range(0, 4).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks> { Mk(start: true), Mk(end: 2), MeasureMarks.None, Mk(end: 2) };
    var diag = new List<Diagnostic>();
    Eq("0,1,0,1,2,3,2,3", Order(PlayOrderBuilder.Build(ms, mk, diag)), "två reprisavsnitt");
    Assert(diag.Count == 0, "ingen diagnostik");
});

Test("PlayOrder (steg 2): hoppad volta med slutrepris ger ingen oändlig loop och nollställer genomspelning efter voltagruppen", () =>
{
    // 0 |: · 1 volta1 :| · 2 volta2 · 3 · 4 :| (ingen startrepris för andra avsnittet)
    var ms = Enumerable.Range(0, 5).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks>
    {
        Mk(start: true),
        Mk(end: 2, volta: new VoltaInfo(new[] { 1 }, 1)),
        Mk(volta: new VoltaInfo(new[] { 2 }, 1)),
        MeasureMarks.None,
        Mk(end: 2),
    };
    var diag = new List<Diagnostic>();
    var o = PlayOrderBuilder.Build(ms, mk, diag);
    Assert(!diag.Any(d => d.Code is "PLAYORDER_LIMIT" or "PLAYORDER_FALLBACK"), "ingen fallback: " + Order(o));
    // Regressionsvärde (goldberg.mscz-fixen): avsnittet börjar efter den hoppade voltan. Inte jämfört mot MuseScore (T2).
    Eq("0,1,0,2,3,4,2,3,4", Order(o), "andra avsnittet börjar efter den hoppade voltan");
});

Test("PlayOrder (steg 2): säkerhetsgräns ger rakt igenom och PLAYORDER_LIMIT", () =>
{
    // Hopp som pekar tillbaka till sig själv via två hopp i varandra får aldrig bli oändligt.
    var ms = Enumerable.Range(0, 3).Select(i => M(i)).ToList();
    var mk = new List<MeasureMarks> { Mk(start: true, end: 100000), MeasureMarks.None, MeasureMarks.None };
    var diag = new List<Diagnostic>();
    var o = PlayOrderBuilder.Build(ms, mk, diag);
    Eq("0,1,2", Order(o), "rakt igenom"); Assert(diag.Any(d => d.Code == "PLAYORDER_LIMIT"), "PLAYORDER_LIMIT");
});

// ---------------------------------------------------------------- MsczReader och hårdning (T8)
byte[] MakeZip(Dictionary<string, string> files, bool store = false)
{
    using var ms = new MemoryStream();
    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        foreach (var (name, text) in files)
        {
            var e = z.CreateEntry(name, store ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(text);
        }
    return ms.ToArray();
}

const string MiniScore =
    "<?xml version=\"1.0\"?><museScore version=\"3.01\"><Score><Part><Staff id=\"1\"><StaffType group=\"pitched\"/></Staff></Part>" +
    "<Staff id=\"1\"><Measure><voice><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" +
    "<Chord><durationType>quarter</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord>" +
    "<Rest><durationType>quarter</durationType></Rest>" +
    "<Chord><durationType>half</durationType><Note><pitch>64</pitch><tpc>18</tpc></Note></Chord></voice></Measure></Staff></Score></museScore>";

Test("MsczReader: container.xml pekar ut huvudfilen, Excerpts ignoreras", () =>
{
    var zip = MakeZip(new()
    {
        ["META-INF/container.xml"] = "<container><rootfiles><rootfile full-path=\"Real.mscx\"/></rootfiles></container>",
        ["Real.mscx"] = MiniScore,
        ["Other.mscx"] = "<museScore version=\"3.01\"><Score/></museScore>",
        ["Excerpts/Part1/Part1.mscx"] = "<museScore version=\"3.01\"><Score/></museScore>",
    });
    using var src = MsczReader.Open(new MemoryStream(zip), "Whatever.mscz");
    Eq("Real.mscx", src.EntryName, "huvudfil");
});

Test("MsczReader: MuseScore 4-container listar stilfil först, huvudfilen är första .mscx", () =>
{
    var zip = MakeZip(new()
    {
        ["META-INF/container.xml"] = "<container><rootfiles><rootfile full-path=\"score_style.mss\"/><rootfile full-path=\"Song.mscx\"/><rootfile full-path=\"Thumbnails/thumbnail.png\"/></rootfiles></container>",
        ["score_style.mss"] = "<museScore version=\"4.20\"><Style/></museScore>",
        ["Song.mscx"] = MiniScore,
    });
    using var src = MsczReader.Open(new MemoryStream(zip), "Annat.mscz");
    Eq("Song.mscx", src.EntryName, "huvudfil");
});

Test("MsczReader: faller tillbaka på <namn>.mscx, sedan score.mscx, sedan första", () =>
{
    var a = MakeZip(new() { ["Song.mscx"] = MiniScore, ["other.mscx"] = MiniScore });
    using (var s = MsczReader.Open(new MemoryStream(a), "Song.mscz")) Eq("Song.mscx", s.EntryName, "namn");
    var b = MakeZip(new() { ["x.mscx"] = MiniScore, ["score.mscx"] = MiniScore });
    using (var s = MsczReader.Open(new MemoryStream(b), "Nope.mscz")) Eq("score.mscx", s.EntryName, "score.mscx");
    var c = MakeZip(new() { ["Excerpts/E/E.mscx"] = MiniScore, ["only.mscx"] = MiniScore });
    using (var s = MsczReader.Open(new MemoryStream(c), null)) Eq("only.mscx", s.EntryName, "första i roten");
});

Test("ScoreLoader: minimal partitur ger rätt noter och ticks", () =>
{
    var r = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(MiniScore)), "mini.mscx");
    Assert(r.Score != null, "ska laddas");
    var s = r.Score!;
    Eq(1, s.Measures.Count, "takter");
    Eq(2, s.Notes.Count, "noter");
    Eq(0, s.Notes[0].ScoreTick, "första noten");
    Eq(480, s.Notes[0].DurationTicks, "kvart");
    Eq(960, s.Notes[1].ScoreTick, "efter paus");
    Eq(960, s.Notes[1].DurationTicks, "halvnot");
});

Test("Hårdning: skräp, tom ström och avkortad XML ger felkod, aldrig undantag", () =>
{
    foreach (var bytes in new[] { new byte[0], new byte[] { 1, 2, 3, 4, 5 }, Encoding.UTF8.GetBytes("<museScore version=\"3.01\"><Score><Part") })
    {
        var r = new ScoreLoader().Load(new MemoryStream(bytes), null);
        Assert(r.Score == null, "ska inte ladda");
        Assert(r.Diagnostics.Any(d => d.Severity == DiagSeverity.Error), "ska ha felkod");
    }
});

Test("Hårdning: DTD med extern entitet avvisas", () =>
{
    string xxe = "<?xml version=\"1.0\"?><!DOCTYPE museScore [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><museScore version=\"3.01\"><Score>&x;</Score></museScore>";
    var r = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(xxe)), null);
    Assert(r.Score == null, "DTD ska inte tolkas");
});

Test("Hårdning: zip-bomb (hög kompressionsgrad) avvisas med LIMIT_EXCEEDED", () =>
{
    var zip = MakeZip(new() { ["bomb.mscx"] = "<museScore version=\"3.01\">" + new string(' ', 5_000_000) + "</museScore>" });
    var r = new ScoreLoader(new LoadLimits { MaxMscxBytes = 1_000_000 }).Load(new MemoryStream(zip), null);
    Assert(r.Score == null && r.Diagnostics.Any(d => d.Code == "LIMIT_EXCEEDED"), "ska avvisas");
    var r2 = new ScoreLoader(new LoadLimits { MaxCompressionRatio = 10 }).Load(new MemoryStream(zip), null);
    Assert(r2.Score == null && r2.Diagnostics.Any(d => d.Code == "LIMIT_EXCEEDED"), "kompressionsgrad ska avvisas");
});

Test("Hårdning: för stor fil avvisas", () =>
{
    var r = new ScoreLoader(new LoadLimits { MaxFileBytes = 100 }).Load(new MemoryStream(new byte[1000]), null);
    Assert(r.Diagnostics.Any(d => d.Code == "LIMIT_EXCEEDED"), "LIMIT_EXCEEDED");
});

Test("Tuplet: tre triolåttondelar fyller en kvart exakt (3.x-format)", () =>
{
    string tri =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice>" +
        "<Tuplet><normalNotes>2</normalNotes><actualNotes>3</actualNotes><baseNote>eighth</baseNote></Tuplet>" +
        "<Chord><durationType>eighth</durationType><Note><pitch>60</pitch></Note></Chord>" +
        "<Chord><durationType>eighth</durationType><Note><pitch>62</pitch></Note></Chord>" +
        "<Chord><durationType>eighth</durationType><Note><pitch>64</pitch></Note></Chord><endTuplet/>" +
        "<Chord><durationType>quarter</durationType><Note><pitch>65</pitch></Note></Chord>" +
        "<Rest><durationType>half</durationType></Rest></voice></Measure></Staff></Score></museScore>";
    var s = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(tri)), null).Score!;
    Eq("0,160,320,480", string.Join(",", s.Notes.Select(n => n.ScoreTick)), "ticks");
    Assert(s.Notes.Take(3).All(n => n.DurationTicks == 160), "triolernas längd");
});

Test("Dynamik och bindebåge: sammanslagen not får filens velocity", () =>
{
    // Takt 1: D (halv) och C (halv, bindebåge framåt). Takt 2: C (hel, bindebåge bakåt).
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice>" +
        "<Dynamic><subtype>pp</subtype><velocity>33</velocity></Dynamic>" +
        "<Chord><durationType>half</durationType><Note><pitch>62</pitch></Note></Chord>" +
        "<Chord><durationType>half</durationType><Note><Spanner type=\"Tie\"><Tie/><next><location><measures>1</measures></location></next></Spanner><pitch>60</pitch></Note></Chord></voice></Measure>" +
        "<Measure><voice><Chord><durationType>whole</durationType><Note><Spanner type=\"Tie\"><prev><location><measures>-1</measures></location></prev></Spanner><pitch>60</pitch></Note></Chord></voice></Measure></Staff></Score></museScore>";
    var s = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)), null).Score!;
    var ev = EventGenerator.Generate(s, new HashSet<string> { "p0" });
    var ons = ev.Where(e => e.Kind == PlaybackEventKind.NoteOn && e.Data1 == 60).ToList();
    Eq(1, ons.Count, "bara en NoteOn för den bundna noten");
    Eq(33, ons[0].Data2, "velocity från filen");
    Eq(960L, ons[0].PlayTick, "start");
    var off = ev.Single(e => e.Kind == PlaybackEventKind.NoteOff && e.Data1 == 60);
    Eq(960L + 960L + 1920L, off.PlayTick, "sluttick efter bindning över takt");
});

Test("Spelordning: bunden not över reprisgräns avslutas vid segmentets slut", () =>
{
    // Takt 0 (startrepris) slutar med en bunden not vars fortsättning ligger i takt 1 (slutrepris 2). Andra varvet: 0,1,0,1.
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\">" +
        "<Measure><startRepeat/><voice><Chord><durationType>half</durationType><Note><pitch>60</pitch></Note></Chord>" +
        "<Chord><durationType>half</durationType><Note><Spanner type=\"Tie\"><Tie/><next><location><measures>1</measures></location></next></Spanner><pitch>64</pitch></Note></Chord></voice></Measure>" +
        "<Measure><endRepeat>2</endRepeat><voice><Chord><durationType>whole</durationType><Note><Spanner type=\"Tie\"><prev><location><measures>-1</measures></location></prev></Spanner><pitch>64</pitch></Note></Chord></voice></Measure>" +
        "</Staff></Score></museScore>";
    var s = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)), null).Score!;
    Eq("0,1,0,1", string.Join(",", s.Order.Segments.Select(x => x.MeasureIndex)), "spelordning");
    var ev = EventGenerator.Generate(s, new HashSet<string> { "p0" });
    Eq(2, ev.Count(e => e.Kind == PlaybackEventKind.NoteOn && e.Data1 == 64), "E spelas två gånger");
    Eq(2, ev.Count(e => e.Kind == PlaybackEventKind.NoteOff && e.Data1 == 64), "med två NoteOff");
});

// ---------------------------------------------------------------- Omgång 2, steg 1: Core-utvidgningar
Score LoadXml(string xml) => new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)), null).Score!;
string C(string type, int pitch, string extra = "", string noteExtra = "") =>
    $"<Chord><durationType>{type}</durationType>{extra}<Note>{noteExtra}<pitch>{pitch}</pitch></Note></Chord>";

Test("Note.Tpc/Tpc2: default 99 när stavning saknas, annars filens värden", () =>
{
    var s = LoadXml(MiniScore.Replace("<tpc>18</tpc>", "<tpc>18</tpc><tpc2>20</tpc2>"));
    Eq(14, s.Notes[0].Tpc, "tpc"); Eq(99, s.Notes[0].Tpc2, "tpc2 saknas");
    Eq(20, s.Notes[1].Tpc2, "tpc2");
    Eq(99, new Note("n0", "p0", 0, 1, 0, 480, 60, false).Tpc, "modell-default");
});

Test("Items: ackord, pauser, mätpaus och tuplets i skriven form", () =>
{
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice>" +
        "<Tuplet><normalNotes>2</normalNotes><actualNotes>3</actualNotes></Tuplet>" +
        C("eighth", 60) + C("eighth", 62) + C("eighth", 64) + "<endTuplet/>" +
        "<Rest><durationType>quarter</durationType></Rest><Rest><durationType>half</durationType></Rest></voice></Measure>" +
        "<Measure><voice><Rest><durationType>measure</durationType><duration>4/4</duration></Rest></voice></Measure></Staff></Score></museScore>";
    var s = LoadXml(xml);
    Eq(6, s.Items.Count, "objekt");
    Assert(s.Items.Take(3).All(i => i.Kind == ItemKind.Chord && i.Tuplets.Count == 1 && i.DurationType == "eighth"), "triolåttondelar");
    Eq(1, s.Items.Take(3).Select(i => i.Tuplets[0].Id).Distinct().Count(), "samma tuplet-id");
    Eq(3, s.Items[0].Tuplets[0].Actual, "actual"); Eq(2, s.Items[0].Tuplets[0].Normal, "normal");
    Eq(ItemKind.Rest, s.Items[3].Kind, "paus"); Assert(s.Items[3].Tuplets.Count == 0, "utanför tuplet");
    Eq(ItemKind.MeasureRest, s.Items[5].Kind, "mätpaus"); Eq(1920, s.Items[5].DurationTicks, "mätpausens längd");
    Eq("n0", s.Items[0].Notes[0].NoteId, "NoteId = Note.Id");
});

Test("Items: bindebåge ger TieStart/TieEnd och punkterad längd bevaras", () =>
{
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice>" +
        "<Chord><durationType>half</durationType><dots>1</dots><Note><Spanner type=\"Tie\"><Tie/><next><location><measures>1</measures></location></next></Spanner><pitch>60</pitch></Note></Chord>" +
        C("quarter", 62) + "</voice></Measure>" +
        "<Measure><voice><Chord><durationType>whole</durationType><Note><Spanner type=\"Tie\"><prev><location><measures>-1</measures></location></prev></Spanner><pitch>60</pitch></Note></Chord></voice></Measure></Staff></Score></museScore>";
    var s = LoadXml(xml);
    Eq(3, s.Items.Count, "objekt");
    Eq(1, s.Items[0].Dots, "punkt"); Eq(1440, s.Items[0].DurationTicks, "punkterad halvnot");
    Assert(s.Items[0].Notes[0].TieStart && !s.Items[0].Notes[0].TieEnd, "start");
    Assert(!s.Items[2].Notes[0].TieStart, "slut-not startar inte om");
});

Test("Stretch: lokal taktart 18/16 med stretch 3/2 ger global längd 3/4 och exakta ticks", () =>
{
    string notes = string.Concat(Enumerable.Range(0, 9).Select(i => C("eighth", 60 + i)));
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice>" +
        "<TimeSig><sigN>18</sigN><sigD>16</sigD><stretchN>3</stretchN><stretchD>2</stretchD></TimeSig>" + notes + "</voice></Measure></Staff></Score></museScore>";
    var r = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)), null);
    var s = r.Score!;
    Eq(1440, s.Measures[0].ActualLengthTicks, "takt 3/4");
    Eq(3, s.Measures[0].NominalSig.Numerator, "taktart täljare");
    Assert(s.Notes.All(n => n.DurationTicks == 160), "åttondelar skalade till 160");
    Eq(1440, s.Notes[^1].ScoreTick + s.Notes[^1].DurationTicks, "fyller takten");
    Assert(r.Diagnostics.All(d => d.Code != "VOICE_LENGTH_MISMATCH"), "ingen längdavvikelse");
});

Test("Stretch gäller per notsystem: andra notsystemet med stretch skalas, första inte", () =>
{
    // Global 3/4. Notsystem 2 har lokal 18/16 stretch 3/2 och 9 åttondelar.
    string n9 = string.Concat(Enumerable.Range(0, 9).Select(i => C("eighth", 60 + i)));
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/><Staff id=\"2\"/></Part>" +
        "<Staff id=\"1\"><Measure><voice><TimeSig><sigN>3</sigN><sigD>4</sigD></TimeSig>" + C("half", 60) + C("quarter", 62) + "</voice></Measure></Staff>" +
        "<Staff id=\"2\"><Measure><voice><TimeSig><sigN>18</sigN><sigD>16</sigD><stretchN>3</stretchN><stretchD>2</stretchD></TimeSig>" + n9 + "</voice></Measure></Staff></Score></museScore>";
    var s = LoadXml(xml);
    Eq(1440, s.Measures[0].ActualLengthTicks, "takt");
    var st2 = s.Notes.Where(n => n.StaffIndex == 1).ToList();
    Eq(9, st2.Count, "noter i notsystem 2"); Assert(st2.All(n => n.DurationTicks == 160), "skalade");
    Eq(960, s.Notes.First(n => n.StaffIndex == 0).DurationTicks, "notsystem 1 oskalat");
});

Test("2.x: <track> och absolut <tick> ger röster och positioner", () =>
{
    string xml =
        "<museScore version=\"2.06\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\">" +
        "<Measure><TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" +
        "<Chord><track>0</track><durationType>quarter</durationType><Note><pitch>60</pitch></Note></Chord>" +
        "<Chord><track>0</track><durationType>half</durationType><Note><pitch>62</pitch></Note></Chord>" +
        "<tick>0</tick><Chord><track>1</track><durationType>whole</durationType><Note><pitch>48</pitch></Note></Chord></Measure>" +
        "<Measure><tick>1920</tick><Chord><durationType>whole</durationType><Note><pitch>64</pitch></Note></Chord></Measure></Staff></Score></museScore>";
    var r = new ScoreLoader().Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)), null);
    var s = r.Score!;
    Eq(2, s.Measures.Count, "takter");
    var v2 = s.Notes.Single(n => n.Voice == 2);
    Eq(48, v2.MidiPitch, "röst 2"); Eq(0, v2.ScoreTick, "röst 2 start"); Eq(1920, v2.DurationTicks, "röst 2 längd");
    Eq(480, s.Notes.First(n => n.MidiPitch == 62).ScoreTick, "röst 1 position");
    Eq(1920, s.Notes.Single(n => n.MidiPitch == 64).ScoreTick, "takt 2");
    Assert(r.Diagnostics.Any(d => d.Code == "UNSUPPORTED_VERSION"), "2.x ger varning (DR-28)");
});

Test("2.x: tuplet med id och referens i Chord", () =>
{
    string xml =
        "<museScore version=\"2.06\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure>" +
        "<Tuplet id=\"1\"><normalNotes>2</normalNotes><actualNotes>3</actualNotes><baseNote>eighth</baseNote></Tuplet>" +
        "<Chord><durationType>eighth</durationType><Tuplet>1</Tuplet><Note><pitch>60</pitch></Note></Chord>" +
        "<Chord><durationType>eighth</durationType><Tuplet>1</Tuplet><Note><pitch>62</pitch></Note></Chord>" +
        "<Chord><durationType>eighth</durationType><Tuplet>1</Tuplet><Note><pitch>64</pitch></Note></Chord>" +
        "<Rest><durationType>quarter</durationType></Rest><Rest><durationType>half</durationType></Rest></Measure></Staff></Score></museScore>";
    var s = LoadXml(xml);
    Eq("0,160,320", string.Join(",", s.Notes.Select(n => n.ScoreTick)), "ticks");
    Eq(1, s.Items.Take(3).Select(i => i.Tuplets.Single().Id).Distinct().Count(), "samma tuplet");
});

Test("Tuplet: nästlade 3.x-tuplets skalar multiplikativt", () =>
{
    // Yttre 3:2 (kvartar), inre 3:2 (åttondelar i första kvarten).
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice>" +
        "<Tuplet><normalNotes>2</normalNotes><actualNotes>3</actualNotes></Tuplet>" +
        "<Tuplet><normalNotes>2</normalNotes><actualNotes>3</actualNotes></Tuplet>" +
        C("eighth", 60) + C("eighth", 62) + C("eighth", 64) + "<endTuplet/>" + C("quarter", 65) + C("quarter", 67) + "<endTuplet/>" +
        "<Rest><durationType>half</durationType></Rest></voice></Measure></Staff></Score></museScore>";
    var s = LoadXml(xml);
    // Inre åttondel: 240 * 2/3 * 2/3 = 106,67 -> summan av tre = 320 exakt
    Eq(0, s.Notes[0].ScoreTick, "start"); Eq(320, s.Notes[3].ScoreTick, "fjärde not efter inre tuplet (kvart*2/3)");
    Eq(2, s.Items[0].Tuplets.Count, "två nivåer"); Eq(1, s.Items[3].Tuplets.Count, "en nivå");
});

Test("ChannelAllocator: stabil tilldelning, hoppar över kanal 9, slagverk delar kanal 9, ny instans efter 15", () =>
{
    Part P(int i, bool perc = false) => new($"p{i}", $"P{i}", "", new[] { new Staff(0, ClefKind.Treble, StaffKind.Standard) },
        new[] { new InstrumentSpan(0, "x", 0, perc ? 128 : 0, perc, 0, null) });
    var parts = Enumerable.Range(0, 20).Select(i => P(i, perc: i == 3)).ToList();
    var m = ChannelAllocator.Allocate(parts);
    Eq(new ChannelAssignment(0, 0), m["p0"], "första"); Eq(new ChannelAssignment(0, 9), m["p3"], "slagverk");
    Eq(new ChannelAssignment(0, 3), m["p4"], "nästa lediga efter slagverk (ingen lucka)");
    Assert(m.Values.Where(a => a.Channel == 9).All(a => a.Synth == 0), "kanal 9 bara i instans 0");
    Assert(m.Where(kv => kv.Key != "p3").All(kv => kv.Value.Channel != 9), "melodiska stämmor undviker 9");
    Eq(2, ChannelAllocator.SynthCount(m), "två instanser (19 melodiska)");
    Eq(1, m["p19"].Synth, "p19 i instans 1");
    // Oberoende av val: samma indata ger samma tilldelning.
    Assert(ChannelAllocator.Allocate(parts).SequenceEqual(m), "deterministisk");
});

// ---------------------------------------------------------------- Omgång 2, steg 3: MEI och pekartidslinje
System.Xml.Linq.XDocument Mei(Score s) => System.Xml.Linq.XDocument.Parse(MeiExporter.Export(s));
System.Xml.Linq.XNamespace meiNs = "http://www.music-encoding.org/ns/mei";
System.Xml.Linq.XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";
string XmlId(System.Xml.Linq.XElement e) => (string?)e.Attribute(xmlNs + "id") ?? "";
string One(string measureBody, string partExtra = "", string version = "3.01") =>
    $"<museScore version=\"{version}\"><Score><Part><Staff id=\"1\"/>{partExtra}</Part><Staff id=\"1\"><Measure><voice>{measureBody}</voice></Measure></Staff></Score></museScore>";

Test("MEI: välformad, unika xml:id, not-id = Note.Id och takt-id = m + index", () =>
{
    var s = LoadXml(MiniScore);
    var doc = Mei(s);
    var ids = doc.Descendants().Select(XmlId).Where(i => i.Length > 0).ToList();
    Eq(ids.Count, ids.Distinct().Count(), "unika id");
    Assert(ids.Contains("n0") && ids.Contains("n1") && ids.Contains("m0"), "n0, n1, m0");
    var note = doc.Descendants(meiNs + "note").First(n => XmlId(n) == "n0");
    Eq("c", (string?)note.Attribute("pname"), "pname"); Eq("4", (string?)note.Attribute("oct"), "oktav"); Eq("4", (string?)note.Attribute("dur"), "dur");
    Assert(doc.Descendants(meiNs + "rest").Any(), "paus");
    Eq("4", (string?)doc.Descendants(meiNs + "scoreDef").First().Attribute("meter.count"), "taktart");
});

Test("MEI: tuplet, bindebåge och punkterad längd", () =>
{
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\"><Measure><voice>" +
        "<Tuplet><normalNotes>2</normalNotes><actualNotes>3</actualNotes></Tuplet>" +
        C("eighth", 60) + C("eighth", 62) + C("eighth", 64) + "<endTuplet/>" +
        "<Rest><durationType>quarter</durationType></Rest><Rest><durationType>eighth</durationType></Rest>" +
        "<Chord><durationType>quarter</durationType><dots>1</dots><Note><Spanner type=\"Tie\"><Tie/><next><location><measures>1</measures></location></next></Spanner><pitch>65</pitch></Note></Chord></voice></Measure>" +
        "<Measure><voice><Chord><durationType>half</durationType><Note><Spanner type=\"Tie\"><prev><location><measures>-1</measures></location></prev></Spanner><pitch>65</pitch></Note></Chord></voice></Measure></Staff></Score></museScore>";
    var doc = Mei(LoadXml(xml));
    var tup = doc.Descendants(meiNs + "tuplet").Single();
    Eq("3", (string?)tup.Attribute("num"), "num"); Eq("2", (string?)tup.Attribute("numbase"), "numbase");
    Eq(3, tup.Elements(meiNs + "note").Count(), "tre noter i tupleten");
    var f = doc.Descendants(meiNs + "note").First(n => (string?)n.Attribute("pname") == "f");
    Eq("i", (string?)f.Attribute("tie"), "tie start"); Eq("1", (string?)f.Attribute("dots"), "punkt");
    Eq("t", (string?)doc.Descendants(meiNs + "note").Last().Attribute("tie"), "tie slut");
});

Test("MEI: ackord blir <chord> med noterna som barn", () =>
{
    var doc = Mei(LoadXml(One("<Chord><durationType>quarter</durationType><Note><pitch>60</pitch></Note><Note><pitch>64</pitch></Note><Note><pitch>67</pitch></Note></Chord><Rest><durationType>measure</durationType><duration>3/4</duration></Rest>")));
    var ch = doc.Descendants(meiNs + "chord").Single();
    Eq(3, ch.Elements(meiNs + "note").Count(), "noter i ackord");
    Assert(ch.Elements(meiNs + "note").All(n => n.Attribute("dur") == null), "dur på ackordet");
});

Test("MEI: stavning via tpc, Cb4 skrivs c♭ i oktav 4 (klingar som b3)", () =>
{
    var doc = Mei(LoadXml(One("<Chord><durationType>whole</durationType><Note><pitch>59</pitch><tpc>7</tpc></Note></Chord>")));
    var n = doc.Descendants(meiNs + "note").Single();
    Eq("c", (string?)n.Attribute("pname"), "pname"); Eq("f", (string?)n.Attribute("accid"), "accid"); Eq("4", (string?)n.Attribute("oct"), "oktav");
});

Test("MEI: tonart ger key.sig och dolda förtecken; förtecken upprepas inte i takten", () =>
{
    // D-dur (2 kors): F# är tonartsförtecken. Därefter F♮ (tpc 13) kräver ♮, och en andra F♮ i samma takt gör det inte.
    string body = "<KeySig><accidental>2</accidental></KeySig>" +
        "<Chord><durationType>quarter</durationType><Note><pitch>66</pitch><tpc>20</tpc></Note></Chord>" +
        "<Chord><durationType>quarter</durationType><Note><pitch>65</pitch><tpc>13</tpc></Note></Chord>" +
        "<Chord><durationType>quarter</durationType><Note><pitch>65</pitch><tpc>13</tpc></Note></Chord>" +
        "<Chord><durationType>quarter</durationType><Note><pitch>66</pitch><tpc>20</tpc></Note></Chord>";
    var doc = Mei(LoadXml(One(body)));
    Eq("2s", (string?)doc.Descendants(meiNs + "scoreDef").First().Attribute("key.sig"), "key.sig");
    var n = doc.Descendants(meiNs + "note").ToList();
    Assert(n[0].Attribute("accid") == null, "F♯ i D-dur utan förtecken");
    Eq("n", (string?)n[1].Attribute("accid"), "F♮ kräver ♮");
    Assert(n[2].Attribute("accid") == null, "upprepad F♮ utan förtecken");
    Eq("s", (string?)n[3].Attribute("accid"), "F♯ efter F♮ kräver ♯ igen");
});

Test("MEI: transponerande instrument skrivs i skriven höjd via tpc2 (A-klarinett: klingande C4 -> skriven E♭4)", () =>
{
    string body = "<Chord><durationType>whole</durationType><Note><pitch>60</pitch><tpc>14</tpc><tpc2>11</tpc2></Note></Chord>";
    var doc = Mei(LoadXml(One(body, "<Instrument><transposeChromatic>-3</transposeChromatic></Instrument>")));
    var n = doc.Descendants(meiNs + "note").Single();
    Eq("e", (string?)n.Attribute("pname"), "pname"); Eq("f", (string?)n.Attribute("accid.ges"), "E♭ (ingår i tonarten, inget synligt förtecken)");
    Assert(n.Attribute("accid") == null, "inget synligt förtecken i Ess-dur"); Eq("4", (string?)n.Attribute("oct"), "oktav");
    // Skriven tonart: klingande C-dur (0) i A-klarinett skrivs i 3 b (Eb-dur).
    Eq("3f", (string?)doc.Descendants(meiNs + "staffDef").First().Attribute("key.sig"), "skriven tonart");
});

Test("MEI: oktavtransponering (gitarr −12) saknar tpc2 och använder tpc", () =>
{
    string body = "<Chord><durationType>whole</durationType><Note><pitch>52</pitch><tpc>18</tpc></Note></Chord>";
    var doc = Mei(LoadXml(One(body, "<Instrument><transposeChromatic>-12</transposeChromatic></Instrument>")));
    var n = doc.Descendants(meiNs + "note").Single();
    Eq("e", (string?)n.Attribute("pname"), "pname"); Eq("4", (string?)n.Attribute("oct"), "skriven oktav = klingande + 1");
});

Test("MEI: saknad tpc ger reservstavning från höjden", () =>
{
    var doc = Mei(LoadXml(One("<Chord><durationType>whole</durationType><Note><pitch>61</pitch></Note></Chord>")));
    var n = doc.Descendants(meiNs + "note").Single();
    Eq("c", (string?)n.Attribute("pname"), "pname"); Eq("s", (string?)n.Attribute("accid"), "c♯");
});

Test("MEI: framåtflytt via <location> ger <space>, mätpaus ger <mRest>, tom röst inte krasch", () =>
{
    string body = C("quarter", 60) + "<location><fractions>1/4</fractions></location>" + C("half", 62);
    var doc = Mei(LoadXml(One(body)));
    var sp = doc.Descendants(meiNs + "space").Single();
    Eq("4", (string?)sp.Attribute("dur"), "space-längd (kvart)");
    var doc2 = Mei(LoadXml(One("<Rest><durationType>measure</durationType><duration>4/4</duration></Rest>")));
    Assert(doc2.Descendants(meiNs + "mRest").Any(), "mRest");
});

Test("MEI: stämmor med flera notsystem numreras löpande och piano får klammer", () =>
{
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"><StaffType group=\"pitched\"/></Staff><Staff id=\"2\"><StaffType group=\"pitched\"/></Staff><Instrument><clef staff=\"2\">F</clef></Instrument></Part>" +
        "<Part><Staff id=\"3\"/></Part>" +
        "<Staff id=\"1\"><Measure><voice>" + C("whole", 72) + "</voice></Measure></Staff>" +
        "<Staff id=\"2\"><Measure><voice>" + C("whole", 48) + "</voice></Measure></Staff>" +
        "<Staff id=\"3\"><Measure><voice>" + C("whole", 60) + "</voice></Measure></Staff></Score></museScore>";
    var doc = Mei(LoadXml(xml));
    Eq("1,2,3", string.Join(",", doc.Descendants(meiNs + "staffDef").Select(d => (string?)d.Attribute("n"))), "staffDef");
    Assert(doc.Descendants(meiNs + "staffGrp").Any(g => (string?)g.Attribute("symbol") == "brace"), "klammer");
    Eq("F", (string?)doc.Descendants(meiNs + "staffDef").First(d => (string?)d.Attribute("n") == "2").Attribute("clef.shape"), "basklav");
    Eq(3, doc.Descendants(meiNs + "measure").Single().Elements(meiNs + "staff").Count(), "tre staff i takten");
});

Test("MEI: skräpdata i modellen ger aldrig undantag (alla ElementKind/okänd durationType)", () =>
{
    var s = LoadXml(One("<Chord><durationType>banan</durationType><Note><pitch>60</pitch></Note></Chord>"));
    var doc = Mei(s);
    Assert(doc.Descendants(meiNs + "note").Any(), "not finns");
});

Test("CursorTimeline: poster per tickposition, upprepade segment ger upprepade poster, minsta x vinner", () =>
{
    // Takt 0: C (kvart) + D (kvart) + ackord E/G (halv), takt 1 med slutrepris 2 -> 0,1,0,1? Här: repris över takt 0.
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\">" +
        "<Measure><startRepeat/><endRepeat>2</endRepeat><voice>" + C("quarter", 60) + C("quarter", 62) +
        "<Chord><durationType>half</durationType><Note><pitch>64</pitch></Note><Note><pitch>67</pitch></Note></Chord></voice></Measure>" +
        "<Measure><voice>" + C("whole", 60) + "</voice></Measure></Staff></Score></museScore>";
    var s = LoadXml(xml);
    Eq("0,0,1", string.Join(",", s.Order.Segments.Select(x => x.MeasureIndex)), "spelordning");
    // n0=C n1=D n2=E n3=G (sorterat på tick, sedan höjd), n4 = C i takt 1
    var items = new List<LayoutItem>
    {
        new("m0", 0, 10, 0), new("n0", 0, 20, 0), new("n1", 0, 60, 0), new("n2", 0, 100, 0), new("n3", 0, 90, 0),
        new("m1", 1, 150, 0), new("n4", 1, 160, 0),
    };
    var diag = new List<Diagnostic>();
    var tl = CursorTimelineBuilder.Build(s, items, 44100, diag);
    Eq(7, tl.Count, "3 poster i vardera av två genomspelningar + 1 i takt 1");
    Eq("0,500,1000,2000,2500,3000,4000", string.Join(",", tl.Select(e => (int)Math.Round(e.PlayMs))), "tider vid 120 BPM");
    Eq(90.0, tl[2].X, "minsta x bland ackordets noter");
    Eq(20.0, tl[3].X, "andra genomspelningen börjar om på första noten");
    Assert(tl.Zip(tl.Skip(1), (a, b) => a.PlayMs <= b.PlayMs).All(x => x), "sorterad");
    Assert(diag.Count == 0, "ingen diagnostik");
});

Test("CursorTimeline: saknad geometri hoppas över och räknas, helt saknad takt återanvänder föregående x", () =>
{
    var s = LoadXml(
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Staff id=\"1\">" +
        "<Measure><voice>" + C("whole", 60) + "</voice></Measure><Measure><voice>" + C("whole", 62) + "</voice></Measure>" +
        "<Measure><voice><Rest><durationType>measure</durationType><duration>4/4</duration></Rest></voice></Measure></Staff></Score></museScore>");
    var diag = new List<Diagnostic>();
    var tl = CursorTimelineBuilder.Build(s, new List<LayoutItem> { new("n0", 0, 30, 0), new("m2", 2, 300, 1) }, 44100, diag);
    Eq(3, tl.Count, "tre poster"); Eq(30.0, tl[1].X, "takt 1 återanvänder föregående x"); Eq(300.0, tl[2].X, "takt 2 pauser använder taktens x");
    Eq(1, tl[2].Sys, "system");
    Assert(diag.Any(d => d.Code == "CURSOR_MISSING_GEOMETRY"), "diagnostik");
});

Test("CursorTimeline: alla stämmors noter används oberoende av stämmaval", () =>
{
    string xml =
        "<museScore version=\"3.01\"><Score><Part><Staff id=\"1\"/></Part><Part><Staff id=\"2\"/></Part>" +
        "<Staff id=\"1\"><Measure><voice>" + C("half", 60) + C("half", 62) + "</voice></Measure></Staff>" +
        "<Staff id=\"2\"><Measure><voice>" + C("quarter", 48) + C("quarter", 50) + C("half", 52) + "</voice></Measure></Staff></Score></museScore>";
    var s = LoadXml(xml);
    var items = s.Notes.Select(n => new LayoutItem(n.Id, 0, n.ScoreTick / 10.0, 0)).ToList();
    var tl = CursorTimelineBuilder.Build(s, items, 44100);
    Eq("0,500,1000", string.Join(",", tl.Select(e => (int)Math.Round(e.PlayMs))), "ticks 0, 480, 960 (stämmornas starter slås ihop)");
});

// ---------------------------------------------------------------- Korpus (T1/T2-förstadium)
void CorpusChecks(string path)
{
    using var fs = File.OpenRead(path);
    var r = new ScoreLoader().Load(fs, Path.GetFileName(path));
    Assert(r.Score != null, "laddning misslyckades: " + string.Join("; ", r.Diagnostics.Select(d => d.Code)));
    Assert(r.Diagnostics.All(d => d.Severity != DiagSeverity.Error), "fel i diagnostik");
    var s = r.Score!;
    Assert(s.Parts.Count > 0 && s.Measures.Count > 0 && s.Notes.Count > 0, "tomt partitur");

    int[] starts = s.Measures.Select(m => m.StartScoreTick).ToArray();
    for (int i = 1; i < starts.Length; i++)
        Eq(starts[i - 1] + s.Measures[i - 1].ActualLengthTicks, starts[i], $"takt {i} startar där föregående slutar");

    int MeasureOf(int tick) { int k = Array.BinarySearch(starts, tick); return k >= 0 ? k : ~k - 1; }
    var overflow = 0; var bad = 0;
    foreach (var n in s.Notes)
    {
        Assert(n.MidiPitch is >= 0 and <= 127 && n.DurationTicks > 0, "ogiltig not " + n.Id);
        int mi = MeasureOf(n.ScoreTick);
        if (mi < 0 || mi >= s.Measures.Count) { bad++; continue; }
        var m = s.Measures[mi];
        if (n.ScoreTick + n.DurationTicks > m.StartScoreTick + m.ActualLengthTicks) overflow++;
    }
    Assert(bad == 0, $"{bad} noter utanför partituret");
    // Tickräkningen är rimlig om (nästan) inga noter sticker ut ur sin takt.
    Assert(overflow <= Math.Max(2, s.Notes.Count / 500), $"{overflow} av {s.Notes.Count} noter sticker ut ur sin takt");

    // Spelordning: sammanhängande, och varje takt förekommer minst en gång (om inte hoppas över av volta/fine)
    long t = 0;
    foreach (var seg in s.Order.Segments) { Eq(t, seg.PlayStartTick, "segment sammanhängande"); t += seg.LengthTicks; }

    // Tempokarta: monoton
    long prev = -1;
    foreach (long tk in Enumerable.Range(0, 200).Select(k => k * Math.Max(1, s.Order.TotalTicks / 200)))
    { long f = s.Tempo.PlayTickToFrame(tk, 44100); Assert(f >= prev, "tempokarta monoton"); prev = f; }

    // Händelser: lika många NoteOn som NoteOff, sorterade, endast valda stämmor
    var all = s.Parts.Select(p => p.Id).ToHashSet();
    var ev = EventGenerator.Generate(s, all);
    Eq(ev.Count(e => e.Kind == PlaybackEventKind.NoteOn), ev.Count(e => e.Kind == PlaybackEventKind.NoteOff), "NoteOn = NoteOff");
    for (int i = 1; i < ev.Count; i++) Assert(ev[i - 1].PlayTick <= ev[i].PlayTick, "sorterade");
    if (s.Parts.Count > 1)
    {
        var one = new HashSet<string> { s.Parts[0].Id };
        Assert(EventGenerator.Generate(s, one).All(e => e.PartId == s.Parts[0].Id), "F2: bara vald stämma");
    }
    // P7: samma fil ger samma modell och samma händelser.
    fs.Position = 0;
    var r2 = new ScoreLoader().Load(fs, Path.GetFileName(path));
    Assert(r2.Score != null && r2.Score.Notes.SequenceEqual(s.Notes), "P7: noterna skiljer sig mellan två laddningar");
    Assert(EventGenerator.Generate(r2.Score!, all).SequenceEqual(ev), "P7: händelserna skiljer sig mellan två laddningar");
    // MEI: välformad, unika id:n, varje not med exakt en <note xml:id>.
    var meiDoc = System.Xml.Linq.XDocument.Parse(MeiExporter.Export(s));
    var xmlId = (System.Xml.Linq.XNamespace)"http://www.w3.org/XML/1998/namespace";
    var meiIds = meiDoc.Descendants().Select(e => (string?)e.Attribute(xmlId + "id")).Where(i => i != null).ToList();
    Eq(meiIds.Count, meiIds.Distinct().Count(), "MEI: unika id");
    Eq(s.Notes.Count, meiIds.Count(i => i![0] == 'n'), "MEI: en <note> per Note");
    Eq(s.Measures.Count, meiIds.Count(i => i![0] == 'm'), "MEI: en <measure> per Measure");
    var secs = s.Tempo.PlayTickToSeconds(s.Order.TotalTicks);
    Console.WriteLine($"      {s.FormatVersion,5} | {s.Parts.Count,2} stämmor | {s.Measures.Count,4} takter | {s.Notes.Count,6} noter | {s.Order.Segments.Count,4} segment | {secs,6:F1} s | {r.Diagnostics.Count} diag");
    foreach (var d in r.Diagnostics) Console.WriteLine($"             - {d.Severity} {d.Code}: {d.Message}");
}

if (Directory.Exists(corpusDir))
    foreach (var f in Directory.EnumerateFiles(corpusDir).Where(f => f.EndsWith(".mscz") || f.EndsWith(".mscx")).OrderBy(f => f))
    {
        string path = f;
        Test("Korpus: " + Path.GetFileName(f), () => CorpusChecks(path));
    }
else Console.WriteLine($"(Ingen korpus hittades i {corpusDir}. Sätt SCORELITE_CORPUS för att köra korpustesterna.)");

// ---------------------------------------------------------------- Körning
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("  OK   " + name); }
    catch (Exception e) { failed++; Console.WriteLine("  FAIL " + name + "\n         " + e.Message); }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} tester gröna");
return failed == 0 ? 0 : 1;
