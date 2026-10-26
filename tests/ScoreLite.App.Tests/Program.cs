using System.Text;
using System.Text.Json;
using ScoreLite.App;
using ScoreLite.App.Bridge;
using ScoreLite.App.Services;
using ScoreLite.App.ViewModels;
using ScoreLite.Audio;
using ScoreLite.Core;

// Samma minimala testlöpare som övriga testprojekt (T5: brygga och ViewModel).
var tests = new List<(string name, Action run)>();
int failed = 0;
void Test(string name, Action run) => tests.Add((name, run));
void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }
void Eq<T>(T expected, T actual, string what) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: väntade {expected}, fick {actual}"); }
void Near(double expected, double actual, double tol, string what) { if (Math.Abs(expected - actual) > tol) throw new Exception($"{what}: väntade {expected} ± {tol}, fick {actual}"); }
T Run<T>(Task<T> t) => t.GetAwaiter().GetResult();
void Await(Task t) => t.GetAwaiter().GetResult();

// ---------------------------------------------------------------- Hjälpare
string Chord(string type, int pitch) => $"<Chord><durationType>{type}</durationType><Note><pitch>{pitch}</pitch></Note></Chord>";

byte[] ScoreBytes(int parts = 2, int measures = 3)
{
    var sb = new StringBuilder("<museScore version=\"3.01\"><Score><metaTag name=\"workTitle\">Testtitel</metaTag>");
    for (int p = 0; p < parts; p++) sb.Append($"<Part><trackName>Stämma {p}</trackName><Staff id=\"{p + 1}\"/></Part>");
    for (int p = 0; p < parts; p++)
    {
        sb.Append($"<Staff id=\"{p + 1}\">");
        for (int m = 0; m < measures; m++)
            sb.Append($"<Measure><voice>{(m == 0 ? "<TimeSig><sigN>4</sigN><sigD>4</sigD></TimeSig>" : "")}{Chord("half", 60 + p)}{Chord("half", 62 + p)}</voice></Measure>");
        sb.Append("</Staff>");
    }
    sb.Append("</Score></museScore>");
    return Encoding.UTF8.GetBytes(sb.ToString());
}

(ScoreViewModel vm, FakePlaybackEngine engine, FakeScoreBridge bridge, FakeKeepAwake awake, InMemorySettings settings, FakeInterruptions intr) NewVm(Action<InMemorySettings>? cfg = null)
{
    var engine = new FakePlaybackEngine(); var bridge = new FakeScoreBridge(); var awake = new FakeKeepAwake();
    var settings = new InMemorySettings(); cfg?.Invoke(settings); var intr = new FakeInterruptions();
    var vm = new ScoreViewModel(engine, bridge, new ScoreLoader(), settings, awake, intr);
    return (vm, engine, bridge, awake, settings, intr);
}

// Syntetisk geometri: varje not får x = ScoreTick / 10 i sin takt, system 0.
LayoutReadyMessage Layout(Score s, int requestId = 1, int gen = 1) => new(requestId, gen, new[] { new SystemGeometry(0, 0, 100) },
    s.Measures.Select(m => new LayoutItem("m" + m.Index, m.Index, m.StartScoreTick / 10.0, 0))
        .Concat(s.Notes.Select(n => new LayoutItem(n.Id, MeasureIndexOf(s, n.ScoreTick), n.ScoreTick / 10.0, 0))).ToList());
int MeasureIndexOf(Score s, int tick) => s.Measures.Last(m => m.StartScoreTick <= tick).Index;

// ---------------------------------------------------------------- BridgeJson (T5)
Test("BridgeJson: alla C# -> JS-meddelanden har v=1 och rätt t, camelCase-fält", () =>
{
    var msgs = new BridgeMessage[]
    {
        new LoadScoreMessage(3, "<mei/>", 400, 2, "dark"), new SetTimelineMessage(5, new[] { new[] { 0.0, 10.0, 0.0 } }),
        new SetClockMessage(3, "playing", 12043.5, 9.0), new SetMarkerMessage("n418", 27, 480),
        new SetFollowMessage(true), new SetZoomMessage(4), new PingMessage(7),
    };
    var expectT = new[] { "loadScore", "setTimeline", "setClock", "setMarker", "setFollow", "setZoom", "ping" };
    for (int i = 0; i < msgs.Length; i++)
    {
        using var doc = JsonDocument.Parse(BridgeJson.Serialize(msgs[i]));
        Eq(1, doc.RootElement.GetProperty("v").GetInt32(), "v");
        Eq(expectT[i], doc.RootElement.GetProperty("t").GetString(), "t");
    }
});

Test("BridgeJson: setClock och setMarker följer exemplen i IF-5", () =>
{
    using var clock = JsonDocument.Parse(BridgeJson.Serialize(new SetClockMessage(3, "playing", 12043.5, 9.0)));
    var r = clock.RootElement;
    Eq(3, r.GetProperty("epoch").GetInt32(), "epoch"); Eq("playing", r.GetProperty("state").GetString(), "state");
    Eq(12043.5, r.GetProperty("playMs").GetDouble(), "playMs"); Eq(9.0, r.GetProperty("bridgeDelayMs").GetDouble(), "bridgeDelayMs");
    using var mk = JsonDocument.Parse(BridgeJson.Serialize(new SetMarkerMessage("n418", 27, 480)));
    Eq("n418", mk.RootElement.GetProperty("anchorId").GetString(), "anchorId");
    Eq(27, mk.RootElement.GetProperty("measure").GetInt32(), "measure"); Eq(480, mk.RootElement.GetProperty("tickInMeasure").GetInt32(), "tickInMeasure");
});

Test("BridgeJson: loadScore bär MEI oförändrad (även med citattecken, backslash, </script> och radseparatorer)", () =>
{
    string sep = new string(new[] { (char)0x2028, ' ', (char)0x2029 });
    string nasty = "<mei a=\"x\">\\ ' \" </script><script>alert(1)</script> " + sep + " \n å</mei>";
    string json = BridgeJson.Serialize(new LoadScoreMessage(1, nasty, 400, 2, "light"));
    using var doc = JsonDocument.Parse(json);
    Eq(nasty, doc.RootElement.GetProperty("mei").GetString(), "rundtur");
    Assert(!json.Contains("</script>") && !json.Contains((char)0x2028), "ingen rå </script> eller U+2028 i JSON-texten");
});

Test("BridgeJson: JS-stränglitteralen är säker att sätta in i ett anrop (S4)", () =>
{
    string json = BridgeJson.Serialize(new LoadScoreMessage(1, "'); alert(1); //\"</script>", 400, 2, "light"));
    string lit = BridgeJson.ToJsStringLiteral(json);
    Assert(lit.StartsWith('"') && lit.EndsWith('"'), "citerad");
    Assert(!lit.Contains("</script>") && !lit.Contains('\n'), "inga raka radbrytningar eller sluttaggar");
    Eq(json, JsonSerializer.Deserialize<string>(lit), "rundtur av litteralen");
});

Test("BridgeJson: tolkar alla JS -> C#-meddelanden i IF-5", () =>
{
    Eq("6.3.0", ((ReadyMessage)BridgeJson.Parse("{\"v\":1,\"t\":\"ready\",\"verovioVersion\":\"6.3.0\"}")!).VerovioVersion, "ready");
    var tap = (TapMessage)BridgeJson.Parse("{\"v\":1,\"t\":\"tap\",\"id\":\"n418\",\"measure\":27,\"x\":512.0}")!;
    Eq("n418", tap.Id, "tap.id"); Eq(27, tap.Measure, "tap.measure"); Eq(512.0, tap.X, "tap.x");
    Assert(((TapMessage)BridgeJson.Parse("{\"t\":\"tap\",\"id\":null,\"measure\":2,\"x\":1}")!).Id == null, "id null");
    Assert(((FollowChangedMessage)BridgeJson.Parse("{\"t\":\"followChanged\",\"enabled\":false}")!).Enabled == false, "followChanged");
    Eq(9, ((PongMessage)BridgeJson.Parse("{\"t\":\"pong\",\"id\":9}")!).Id, "pong");
    var err = (RenderErrorMessage)BridgeJson.Parse("{\"t\":\"renderError\",\"code\":\"RENDER_FAILED\",\"message\":\"x\"}")!;
    Eq("RENDER_FAILED", err.Code, "renderError");
    var lr = (LayoutReadyMessage)BridgeJson.Parse(
        "{\"v\":1,\"t\":\"layoutReady\",\"requestId\":2,\"layoutGen\":3,\"systems\":[{\"sys\":0,\"y\":10.5,\"h\":80},{\"sys\":1,\"y\":120,\"h\":80}]," +
        "\"items\":[[\"n1\",0,12.5,0],[\"m0\",0,5,0],[\"n2\",1,40,1]]}")!;
    Eq(2, lr.RequestId, "requestId"); Eq(3, lr.LayoutGen, "layoutGen"); Eq(2, lr.Systems.Count, "system"); Eq(10.5, lr.Systems[0].Y, "y");
    Eq(3, lr.Items.Count, "items"); Eq(new LayoutItem("n2", 1, 40, 1), lr.Items[2], "item");
});

Test("BridgeJson: okänd typ ger UnknownMessage, skräp ger null, aldrig undantag", () =>
{
    Eq("fancy", ((UnknownMessage)BridgeJson.Parse("{\"v\":1,\"t\":\"fancy\"}")!).Type, "okänd");
    foreach (var bad in new[] { null, "", "   ", "inte json", "[1,2]", "42", "{}", "{\"t\":5}", "{\"t\":\"tap\",\"measure\":\"x\"", "{\"t\":\"layoutReady\",\"items\":[[1,2]]}" })
    {
        var m = BridgeJson.Parse(bad);
        Assert(m == null || m is LayoutReadyMessage, $"borde vara null/ofarligt: {bad}");
    }
    var lr = (LayoutReadyMessage)BridgeJson.Parse("{\"t\":\"layoutReady\",\"items\":[[1,2],[\"n1\",0,1.5,0],\"x\"]}")!;
    Eq(1, lr.Items.Count, "bara den välformade posten");
});

Test("JsonScoreBridge: skickar JSON via transporten, tar emot händelser och ignorerar skräp", () =>
{
    var b = new TestBridge();
    var got = new List<string>();
    b.Tap += t => got.Add("tap:" + t.Id); b.Ready += r => got.Add("ready:" + r.VerovioVersion); b.Pong += p => got.Add("pong:" + p.Id);
    b.OnJsMessage("{\"t\":\"tap\",\"id\":\"n1\",\"measure\":0,\"x\":1}");
    b.OnJsMessage("{\"t\":\"ready\",\"verovioVersion\":\"6\"}");
    b.OnJsMessage("{\"t\":\"nytt\"}"); b.OnJsMessage("skräp"); b.OnJsMessage(null);
    Eq("tap:n1,ready:6", string.Join(",", got), "händelser");
    Eq(3, b.IgnoredLog.Count, "ignorerade");
    b.SetClock(1, "paused", 5, 2); b.SetTimeline(2, new[] { new CursorEntry(0, 1, 0) });
    Eq(2, b.Sent.Count, "skickade");
    using var d = JsonDocument.Parse(b.Sent[1]);
    Eq("setTimeline", d.RootElement.GetProperty("t").GetString(), "t");
    Eq(3, d.RootElement.GetProperty("entries")[0].GetArrayLength(), "entry = [playMs, x, sys]");
});

// ---------------------------------------------------------------- BridgeDelayEstimator
Test("BridgeDelayEstimator: halva rundturstiden, median av de fem senaste, okända pong ignoreras", () =>
{
    long now = 0;
    var est = new BridgeDelayEstimator(() => now, ticksPerMs: 1);
    Eq(0.0, est.DelayMs, "start");
    foreach (int rtt in new[] { 20, 20, 20, 400, 20 })
    {
        int id = est.NextPing(); now += rtt; est.OnPong(id);
    }
    Near(10, est.DelayMs, 0.001, "median ignorerar den långsamma rundturen");
    est.OnPong(9999);
    Near(10, est.DelayMs, 0.001, "okänd pong");
    int a = est.NextPing(); now += 100; est.OnPong(a); est.OnPong(a);
    Assert(est.DelayMs >= 10, "dubbel pong räknas en gång");
});

// ---------------------------------------------------------------- ScoreViewModel
Test("ScoreViewModel: laddning fyller stämmor, titel och skickar MEI först när JS är redo", () =>
{
    var (vm, engine, bridge, _, _, _) = NewVm();
    Assert(Run(vm.LoadAsync(ScoreBytes(), "fil.mscz")), "laddning");
    Eq("Testtitel", vm.Title, "titel"); Eq(2, vm.Parts.Count, "stämmor"); Assert(vm.Parts.All(p => p.IsSelected), "alla valda");
    Eq(1, engine.Loads, "engine.Load"); Assert(vm.CanPlay, "CanPlay");
    Eq(0, bridge.Sent.OfType<LoadScoreMessage>().Count(), "inget skickat innan ready");
    vm.ViewportWidthPx = 360; vm.Theme = "dark";
    bridge.RaiseReady();
    var load = bridge.Sent.OfType<LoadScoreMessage>().Single();
    Assert(load.Mei.Contains("xml:id=\"n0\""), "MEI med not-id"); Eq(360, load.ViewportWidthPx, "bredd"); Eq("dark", load.Theme, "tema"); Eq(2, load.ZoomLevel, "zoom");
    Assert(bridge.Sent.OfType<PingMessage>().Any(), "ping efter ready");
});

Test("ScoreViewModel: MEI skickas direkt om JS redan är redo", () =>
{
    var (vm, _, bridge, _, _, _) = NewVm();
    bridge.RaiseReady();
    Run(vm.LoadAsync(ScoreBytes(), "fil.mscz"));
    Eq(1, bridge.Sent.OfType<LoadScoreMessage>().Count(), "loadScore");
});

Test("ScoreViewModel: fatala fel ger meddelande enligt §12.3 och aldrig undantag", () =>
{
    var (vm, engine, _, _, _, _) = NewVm();
    Assert(!Run(vm.LoadAsync(new byte[] { 1, 2, 3 }, "x.mscz")), "ska misslyckas");
    Eq("Filen verkar inte vara ett MuseScore-partitur.", vm.ErrorMessage, "NOT_A_SCORE");
    Assert(!Run(vm.LoadAsync(Encoding.UTF8.GetBytes("<museScore version=\"3.01\"><Score></Score></museScore>"), "x.mscx")), "tom");
    Eq("Partituret innehåller inga noter.", vm.ErrorMessage, "EMPTY_SCORE");
    Eq(0, engine.Loads, "engine laddas inte vid fel"); Assert(!vm.CanPlay, "kan inte spela");
    Assert(!vm.IsLoading, "laddning klar");
});

Test("ScoreViewModel: varningslistan visar UI-9-koder men inte interna", () =>
{
    var (vm, _, _, _, _, _) = NewVm();
    // En okänd tagg i takten ger UNKNOWN_ELEMENTS (information som ändå visas).
    string xml = Encoding.UTF8.GetString(ScoreBytes(1, 1)).Replace("<Chord>", "<Overraskning/><Chord>");
    Run(vm.LoadAsync(Encoding.UTF8.GetBytes(xml), "x.mscx"));
    Assert(vm.HasWarnings, "varning finns"); Assert(vm.Warnings.Any(w => w.Contains("känns inte igen")), "UNKNOWN_ELEMENTS");
    Assert(ErrorMessages.IsUserVisible(new Diagnostic(DiagSeverity.Warning, "PLAYORDER_FALLBACK", "")), "PLAYORDER_FALLBACK visas");
    Assert(!ErrorMessages.IsUserVisible(new Diagnostic(DiagSeverity.Info, "CURSOR_MISSING_GEOMETRY", "")), "CURSOR_MISSING_GEOMETRY visas inte");
});

Test("ScoreViewModel: layoutReady bygger tidslinjen och skickar setTimeline; en nyare layout ersätter en äldre", () =>
{
    var (vm, _, bridge, _, _, _) = NewVm();
    bridge.RaiseReady();
    Run(vm.LoadAsync(ScoreBytes(), "f.mscz"));
    var score = new ScoreLoader().Load(new MemoryStream(ScoreBytes()), null).Score!;
    bridge.RaiseLayoutReady(Layout(score, 1, 1));
    WaitFor(() => bridge.Sent.OfType<SetTimelineMessage>().Any(), "setTimeline");
    var tl = bridge.Sent.OfType<SetTimelineMessage>().Last();
    Eq(1, tl.LayoutGen, "layoutGen");
    Assert(tl.Entries.Count >= 3, "minst en post per takt");
    for (int i = 1; i < tl.Entries.Count; i++) Assert(tl.Entries[i - 1][0] <= tl.Entries[i][0], "sorterad");
    Eq(0.0, tl.Entries[0][0], "första posten vid 0 ms");
    bridge.RaiseLayoutReady(Layout(score, 2, 2));
    WaitFor(() => bridge.Sent.OfType<SetTimelineMessage>().Any(m => m.LayoutGen == 2), "ny tidslinje");
});

void WaitFor(Func<bool> cond, string what)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (!cond()) { if (sw.ElapsedMilliseconds > 5000) throw new Exception("timeout: " + what); Thread.Sleep(5); }
}

Test("ScoreViewModel: tryck på en not sätter startmarkören (SeekTo + setMarker) på notens takt och tick", () =>
{
    var (vm, engine, bridge, _, _, _) = NewVm();
    bridge.RaiseReady();
    Run(vm.LoadAsync(ScoreBytes(1, 3), "f.mscz"));
    var score = new ScoreLoader().Load(new MemoryStream(ScoreBytes(1, 3)), null).Score!;
    var second = score.Notes.First(n => n.ScoreTick == 1920 + 960);   // andra halvnoten i takt 1
    bridge.RaiseTap(second.Id, 1, 0);
    Eq(new ScorePosition(1, 960), engine.LastSeek, "SeekTo");
    var mk = bridge.Sent.OfType<SetMarkerMessage>().Single();
    Eq(second.Id, mk.AnchorId, "anchorId"); Eq(1, mk.Measure, "measure"); Eq(960, mk.TickInMeasure, "tick");
});

Test("ScoreViewModel: tryck på takt/paus snappar till närmaste notstart på x (DR-10)", () =>
{
    var (vm, engine, bridge, _, _, _) = NewVm();
    bridge.RaiseReady();
    Run(vm.LoadAsync(ScoreBytes(1, 3), "f.mscz"));
    var score = new ScoreLoader().Load(new MemoryStream(ScoreBytes(1, 3)), null).Score!;
    bridge.RaiseLayoutReady(Layout(score));
    // Takt 2: noter vid tick 0 (x=384) och 960 (x=480). Tryck vid x=470 -> närmast tick 960.
    bridge.RaiseTap("m2", 2, 470);
    Eq(new ScorePosition(2, 960), engine.LastSeek, "snappad position");
    Eq(score.Notes.First(n => n.ScoreTick == 3840 + 960).Id, bridge.Sent.OfType<SetMarkerMessage>().Last().AnchorId, "ankare = närmaste not");
    bridge.RaiseTap(null, 2, 380);
    Eq(new ScorePosition(2, 0), engine.LastSeek, "tryck nära takt-början");
});

Test("ScoreViewModel: tryck utan geometri använder taktens början, ogiltiga id:n kraschar inte", () =>
{
    var (vm, engine, bridge, _, _, _) = NewVm();
    bridge.RaiseReady();
    Run(vm.LoadAsync(ScoreBytes(1, 3), "f.mscz"));
    bridge.RaiseTap(null, 1, 100);
    Eq(new ScorePosition(1, 0), engine.LastSeek, "taktens början");
    Eq("m1", bridge.Sent.OfType<SetMarkerMessage>().Last().AnchorId, "takt-ankare");
    bridge.RaiseTap("n999999", 99, 0);   // not finns inte, takt klampas
    Eq(2, engine.LastSeek!.Value.MeasureIndex, "klampad takt");
    bridge.RaiseTap("n-5", -4, 0); bridge.RaiseTap("javascript:alert(1)", 0, 0); bridge.RaiseTap("", 0, 0);
    Eq(0, engine.LastSeek!.Value.MeasureIndex, "id behandlas som data");
});

Test("ScoreViewModel: snapshot -> setClock med rätt tillstånd, epok, tid och uppmätt bryggfördröjning; skärmen hålls tänd", () =>
{
    var (vm, engine, bridge, awake, _, _) = NewVm();
    bridge.RaiseReady();
    Run(vm.LoadAsync(ScoreBytes(), "f.mscz"));
    engine.Raise(new PlaybackSnapshot(PlaybackState.CountingIn, 4, 0, new ScorePosition(0, 0)));
    Eq(PlaybackState.CountingIn, vm.State, "State"); Assert(awake.On, "skärmen tänd under inräkning");
    engine.Raise(new PlaybackSnapshot(PlaybackState.Playing, 4, 12043.5, new ScorePosition(0, 0)));
    var clock = bridge.Sent.OfType<SetClockMessage>().Last();
    Eq("playing", clock.State, "state"); Eq(4, clock.Epoch, "epok"); Eq(12043.5, clock.PlayMs, "playMs");
    engine.Raise(new PlaybackSnapshot(PlaybackState.Paused, 5, 100, new ScorePosition(0, 0)));
    Assert(!awake.On, "skärmen släpps vid paus"); Eq("paused", bridge.Sent.OfType<SetClockMessage>().Last().State, "paused");
    engine.Raise(new PlaybackSnapshot(PlaybackState.Stopped, 6, 0, new ScorePosition(0, 0)));
    Eq("stopped", bridge.Sent.OfType<SetClockMessage>().Last().State, "stopped");
    Eq(new[] { "countingIn", "playing", "paused", "stopped" }.Length, bridge.Sent.OfType<SetClockMessage>().Count(), "ett setClock per snapshot");
});

Test("ScoreViewModel: bryggfördröjningen mäts med ping/pong och följer med i setClock", () =>
{
    long now = 0;
    var engine = new FakePlaybackEngine(); var bridge = new FakeScoreBridge();
    var vm = new ScoreViewModel(engine, bridge, new ScoreLoader(), new InMemorySettings(), new FakeKeepAwake(), null,
        new BridgeDelayEstimator(() => now, 1));
    vm.PingBridge();
    int id = bridge.Sent.OfType<PingMessage>().Last().Id;
    now += 30; bridge.RaisePong(id);
    engine.Raise(new PlaybackSnapshot(PlaybackState.Playing, 1, 5, new ScorePosition(0, 0)));
    Near(15, bridge.Sent.OfType<SetClockMessage>().Last().BridgeDelayMs, 0.001, "halva rundturen");
});

Test("ScoreViewModel: Spela/Paus, Stopp och följ-läge (slås på vid Play)", () =>
{
    var (vm, engine, bridge, _, settings, _) = NewVm();
    Run(vm.LoadAsync(ScoreBytes(), "f.mscz"));
    Assert(!vm.StopCommand.CanExecute(null), "Stopp inaktiv i Stopped");
    vm.ToggleFollowCommand.Execute(null);
    Assert(!vm.FollowEnabled, "följ av"); Assert(!settings.FollowEnabled, "sparat");
    vm.PlayPauseCommand.Execute(null);
    Eq(1, engine.Plays, "Play"); Assert(vm.FollowEnabled, "Play slår på följ-läget");
    Assert(bridge.Sent.OfType<SetFollowMessage>().Last().Enabled, "setFollow(true)");
    engine.Raise(new PlaybackSnapshot(PlaybackState.Playing, 1, 0, new ScorePosition(0, 0)));
    Eq("Pausa", vm.PlayPauseLabel, "etikett"); Assert(vm.StopCommand.CanExecute(null), "Stopp aktiv");
    vm.PlayPauseCommand.Execute(null);
    Eq(1, engine.Pauses, "Pause");
    engine.Raise(new PlaybackSnapshot(PlaybackState.Paused, 2, 0, new ScorePosition(0, 0)));
    Eq("Spela", vm.PlayPauseLabel, "etikett");
    vm.StopCommand.Execute(null); Eq(1, engine.Stops, "Stop");
});

Test("ScoreViewModel: followChanged från JS ändrar läget utan att eka tillbaka", () =>
{
    var (vm, _, bridge, _, settings, _) = NewVm();
    int before = bridge.Sent.OfType<SetFollowMessage>().Count();
    bridge.RaiseFollowChanged(false);
    Assert(!vm.FollowEnabled, "av"); Assert(!settings.FollowEnabled, "sparat");
    Eq(before, bridge.Sent.OfType<SetFollowMessage>().Count(), "ingen setFollow tillbaka");
    Eq("Följ uppspelning av", vm.FollowLabel, "etikett");
});

Test("ScoreViewModel: metronom och inräkning växlar motorn och sparas (DR-25), och återställs vid nästa start", () =>
{
    var (vm, engine, _, _, settings, _) = NewVm();
    vm.ToggleMetronomeCommand.Execute(null); vm.ToggleCountInCommand.Execute(null);
    Assert(engine.MetronomeEnabled && engine.CountInEnabled, "motorn");
    Assert(settings.MetronomeEnabled && settings.CountInEnabled, "sparat");
    Eq("Metronom på", vm.MetronomeLabel, "etikett"); Eq("Inräkning på", vm.CountInLabel, "etikett");
    var (vm2, engine2, _, _, _, _) = NewVm(s => { s.MetronomeEnabled = true; s.CountInEnabled = true; s.ZoomLevel = 4; s.FollowEnabled = false; });
    Assert(vm2.MetronomeEnabled && vm2.CountInEnabled && engine2.MetronomeEnabled && engine2.CountInEnabled, "återställt");
    Eq(4, vm2.ZoomLevel, "zoom"); Assert(!vm2.FollowEnabled, "följ");
});

Test("ScoreViewModel: zoom i fem steg skickar setZoom och sparas; utanför 0–4 klampas", () =>
{
    var (vm, _, bridge, _, settings, _) = NewVm();
    vm.ZoomInCommand.Execute(null); vm.ZoomInCommand.Execute(null); vm.ZoomInCommand.Execute(null);
    Eq(4, vm.ZoomLevel, "max 4"); Eq(4, settings.ZoomLevel, "sparat");
    Eq(new[] { 3, 4 }.Length, bridge.Sent.OfType<SetZoomMessage>().Count(), "inga skickade utanför intervallet");
    for (int i = 0; i < 10; i++) vm.ZoomOutCommand.Execute(null);
    Eq(0, vm.ZoomLevel, "min 0");
    Assert(bridge.Sent.OfType<SetZoomMessage>().All(z => z.ZoomLevel is >= 0 and <= 4), "alla inom 0–4");
});

Test("ScoreViewModel: stämmaval kopplas till motorn och sista valda stämman kan inte avmarkeras (UI-7, DR-08)", () =>
{
    var (vm, engine, _, _, _, _) = NewVm();
    Run(vm.LoadAsync(ScoreBytes(3, 2), "f.mscz"));
    var p0 = vm.Parts[0]; var p1 = vm.Parts[1]; var p2 = vm.Parts[2];
    vm.TogglePartCommand.Execute(p0); vm.TogglePartCommand.Execute(p1);
    Eq("p2", string.Join(",", engine.LastSelection!.OrderBy(x => x)), "bara p2 kvar");
    Assert(!p2.CanToggle, "sista stämman är låst"); Assert(p2.AccessibilityLabel.Contains("minst en stämma"), "förklaring för skärmläsare");
    vm.TogglePartCommand.Execute(p2);
    Assert(p2.IsSelected, "förblev vald"); Eq(2, engine.SelectionCalls, "ingen extra SetSelectedParts");
    vm.SelectAllPartsCommand.Execute(null);
    Eq(3, engine.LastSelection!.Count, "alla"); Assert(vm.Parts.All(p => p.CanToggle), "alla kan växlas igen");
});

Test("ScoreViewModel: RENDER_FAILED ger degraderat läge men uppspelning och stämmaval fungerar", () =>
{
    var (vm, engine, bridge, _, _, _) = NewVm();
    Run(vm.LoadAsync(ScoreBytes(), "f.mscz"));
    bridge.RaiseRenderError("RENDER_FAILED", "x");
    Assert(vm.RenderDegraded, "degraderat"); Eq("Notbilden kunde inte ritas. Uppspelning fungerar fortfarande.", vm.ErrorMessage, "meddelande");
    Assert(vm.CanPlay, "kan fortfarande spela"); vm.PlayPauseCommand.Execute(null); Eq(1, engine.Plays, "Play");
});

Test("ScoreViewModel: AUDIO_INIT_FAILED visar notbilden men inaktiverar uppspelning", () =>
{
    var (vm, engine, bridge, _, _, _) = NewVm();
    engine.ThrowOnLoad = true;
    bridge.RaiseReady();
    Assert(Run(vm.LoadAsync(ScoreBytes(), "f.mscz")), "laddningen lyckas");
    Assert(vm.AudioFailed && !vm.CanPlay, "ljud misslyckades"); Eq("Ljudet kunde inte startas.", vm.ErrorMessage, "meddelande");
    Assert(!vm.PlayPauseCommand.CanExecute(null), "Spela inaktiv");
    Eq(1, bridge.Sent.OfType<LoadScoreMessage>().Count(), "notbilden skickas ändå");
});

Test("ScoreViewModel: ljudavbrott pausar bara under uppspelning (R4)", () =>
{
    var (vm, engine, _, _, _, intr) = NewVm();
    Run(vm.LoadAsync(ScoreBytes(), "f.mscz"));
    intr.Raise(); Eq(0, engine.Pauses, "inget att pausa i Stopped");
    engine.Raise(new PlaybackSnapshot(PlaybackState.Playing, 1, 0, new ScorePosition(0, 0)));
    intr.Raise(); Eq(1, engine.Pauses, "pausad");
});

Test("ScoreViewModel: Dispose släpper skärmen och slutar lyssna på motorn", () =>
{
    var (vm, engine, bridge, awake, _, _) = NewVm();
    engine.Raise(new PlaybackSnapshot(PlaybackState.Playing, 1, 0, new ScorePosition(0, 0)));
    Assert(awake.On, "tänd");
    vm.Dispose();
    Assert(!awake.On, "släppt");
    int n = bridge.Sent.Count; engine.Raise(new PlaybackSnapshot(PlaybackState.Playing, 2, 0, new ScorePosition(0, 0)));
    Eq(n, bridge.Sent.Count, "inga fler setClock");
});

Test("ScoreViewModel: rotation (ny bredd) ger omlayout via setZoom men rör inte uppspelningen", () =>
{
    var (vm, engine, bridge, _, _, _) = NewVm();
    bridge.RaiseReady(); Run(vm.LoadAsync(ScoreBytes(), "f.mscz"));
    int before = bridge.Sent.OfType<SetZoomMessage>().Count();
    vm.OnViewportChanged(800);
    Eq(before + 1, bridge.Sent.OfType<SetZoomMessage>().Count(), "omlayout begärd"); Eq(0, engine.Pauses + engine.Stops, "uppspelning orörd");
    vm.OnViewportChanged(800); Eq(before + 1, bridge.Sent.OfType<SetZoomMessage>().Count(), "ingen ändring, ingen omlayout");
});

// ---------------------------------------------------------------- StartViewModel
Test("StartViewModel: vald fil läses till minnet och lämnas till navigeringen", () =>
{
    var nav = new FakeNav(); var picker = new FakePicker(new PickedFile("sång.mscz", () => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }))));
    var vm = new StartViewModel(picker, nav, new FakeDialogs());
    Await(Task.Run(async () => await vm.OpenFileCommand.ExecuteAsync(null)));
    Eq("sång.mscz", nav.Name, "namn"); Eq(3, nav.Data!.Length, "data");
});

Test("StartViewModel: avbruten filväljare gör ingenting; fel visas som dialog, ingen krasch", () =>
{
    var nav = new FakeNav(); var dlg = new FakeDialogs();
    Await(Task.Run(async () => await new StartViewModel(new FakePicker(null), nav, dlg).OpenFileCommand.ExecuteAsync(null)));
    Assert(nav.Data == null && dlg.Messages.Count == 0, "inget hände");
    var bad = new FakePicker(new PickedFile("x.mscz", () => throw new IOException("åtkomst nekad")));
    Await(Task.Run(async () => await new StartViewModel(bad, nav, dlg).OpenFileCommand.ExecuteAsync(null)));
    Eq(1, dlg.Messages.Count, "dialog"); Assert(nav.Data == null, "ingen navigering");
});

Test("StartViewModel: filer över 50 MB avvisas med LIMIT_EXCEEDED (DR-27, S2), även utan känd längd", () =>
{
    var nav = new FakeNav(); var dlg = new FakeDialogs();
    var big = new PickedFile("stor.mscz", () => Task.FromResult<Stream>(new EndlessStream(StartViewModel.MaxFileBytes + 10)));
    Await(new StartViewModel(new FakePicker(null), nav, dlg).OpenAsync(big));
    Eq("Partituret är för stort för att öppnas i appen.", dlg.Messages.Single(), "meddelande"); Assert(nav.Data == null, "ingen navigering");
});

// ---------------------------------------------------------------- Med riktig motor och korpus
Test("Integration: ScoreViewModel + riktig PlaybackEngine (offline) ger setClock från Play till slut", () =>
{
    var output = new OfflineAudioOutput(44100);
    var engine = new PlaybackEngine(output, new SilentSynth(), new EngineOptions { UseWorkerThread = false });
    var bridge = new FakeScoreBridge();
    var vm = new ScoreViewModel(engine, bridge, new ScoreLoader(), new InMemorySettings(), new FakeKeepAwake());
    bridge.RaiseReady();
    Run(vm.LoadAsync(ScoreBytes(1, 1), "f.mscz"));
    engine.Pump();
    vm.PlayPauseCommand.Execute(null);
    var buf = new float[512 * 2];
    for (int i = 0; i < 2000 && engine.State != PlaybackState.Stopped || i < 3; i++) { engine.Pump(); output.Pull(buf); }
    var states = bridge.Sent.OfType<SetClockMessage>().Select(c => c.State).ToList();
    Assert(states.Contains("playing") && states[^1] == "stopped", "playing ... stopped: " + string.Join(",", states));
});

string corpusDir = Environment.GetEnvironmentVariable("SCORELITE_CORPUS")
                   ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../corpus"));
if (Directory.Exists(corpusDir))
    foreach (var f in Directory.EnumerateFiles(corpusDir, "*.mscz").OrderBy(x => x))
    {
        string path = f;
        Test("Korpus (ViewModel): " + Path.GetFileName(f), () =>
        {
            var (vm, engine, bridge, _, _, _) = NewVm();
            bridge.RaiseReady();
            Assert(Run(vm.LoadAsync(File.ReadAllBytes(path), Path.GetFileName(path))), "laddning: " + vm.ErrorMessage);
            var load = bridge.Sent.OfType<LoadScoreMessage>().Single();
            Assert(load.Mei.Length > 1000, "MEI skickad"); Assert(vm.Parts.Count > 0, "stämmor");
            var score = new ScoreLoader().Load(new MemoryStream(File.ReadAllBytes(path)), null).Score!;
            bridge.RaiseLayoutReady(Layout(score));
            WaitFor(() => bridge.Sent.OfType<SetTimelineMessage>().Any(), "tidslinje");
            var tl = bridge.Sent.OfType<SetTimelineMessage>().Single();
            Assert(tl.Entries.Count >= score.Order.Segments.Count, "minst en post per segment");
            bridge.RaiseTap(score.Notes[score.Notes.Count / 2].Id, 0, 0);
            Assert(engine.LastSeek != null, "tryck gav SeekTo");
        });
    }

// ---------------------------------------------------------------- Körning
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("  OK   " + name); }
    catch (Exception e) { failed++; Console.WriteLine("  FAIL " + name + "\n         " + e.Message); }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} tester gröna");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------- Fejkar
sealed class FakePlaybackEngine : IPlaybackEngine
{
    public int Loads, Plays, Pauses, Stops, SelectionCalls;
    public bool ThrowOnLoad;
    public ScorePosition? LastSeek;
    public HashSet<string>? LastSelection;
    public PlaybackState State { get; private set; } = PlaybackState.Unloaded;
    public bool MetronomeEnabled { get; set; }
    public bool CountInEnabled { get; set; }
    public event Action<PlaybackSnapshot>? SnapshotChanged;
    public void Load(Score score) { if (ThrowOnLoad) throw new InvalidOperationException("ljud"); Loads++; State = PlaybackState.Stopped; }
    public void Play() => Plays++;
    public void Pause() => Pauses++;
    public void Stop() => Stops++;
    public void SeekTo(ScorePosition position) => LastSeek = position;
    public void SetSelectedParts(IReadOnlySet<string> partIds) { SelectionCalls++; LastSelection = new HashSet<string>(partIds); }
    public void Raise(PlaybackSnapshot s) { State = s.State; SnapshotChanged?.Invoke(s); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeKeepAwake : IKeepAwake { public bool On; public void SetKeepScreenOn(bool on) => On = on; }
sealed class FakeInterruptions : IAudioInterruptions { public event Action? Interrupted; public void Raise() => Interrupted?.Invoke(); }
sealed class FakeNav : IAppNavigation
{
    public byte[]? Data; public string? Name;
    public Task ShowScoreAsync(byte[] data, string fileName) { Data = data; Name = fileName; return Task.CompletedTask; }
    public Task ShowStartAsync() => Task.CompletedTask;
}
sealed class FakePicker : IScoreFilePicker { private readonly PickedFile? _f; public FakePicker(PickedFile? f) => _f = f; public Task<PickedFile?> PickAsync() => Task.FromResult(_f); }
sealed class FakeDialogs : IDialogService
{
    public List<string> Messages = new();
    public Task ShowErrorAsync(string title, string message) { Messages.Add(message); return Task.CompletedTask; }
}
sealed class EndlessStream : Stream
{
    private long _left; public EndlessStream(long length) => _left = length;
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set { } }
    public override int Read(byte[] buffer, int offset, int count) { int n = (int)Math.Min(count, _left); _left -= n; return n; }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
sealed class SilentSynth : ISynthHost
{
    public int SampleRate => 44100;
    public void Configure(int synthCount, int maxBlockFrames) { }
    public void ProcessMidi(int synth, int channel, int command, int data1, int data2) { }
    public void Render(Span<float> interleavedStereo) => interleavedStereo.Clear();
    public void Reset() { }
    public void Dispose() { }
}
sealed class TestBridge : JsonScoreBridge
{
    public List<string> Sent = new(); public List<string> IgnoredLog = new();
    public TestBridge() => Ignored += s => IgnoredLog.Add(s);
    protected override void Transport(string json) => Sent.Add(json);
}
