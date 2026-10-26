# Arkitektur (nuläge) – ScoreLite

> Maskinläsbar sammanfattning av arkitekturen **som den är byggd**. Skriven för språkmodeller och utvecklare.
> Den ersätter inte designdokumentet. Den samlar §7, §8 och avvikelserna i `decisions.md` på ett ställe.
> Senast verifierad mot koden: 2026-10-26.

## 0. Hur dokumentet används (läs först)

**ARCH-0.1 Källor och företräde.** Vid konflikt gäller, uppifrån och ned:
1. Detta dokument (`docs/architecture.md`) för *hur koden är organiserad nu*.
2. `docs/decisions.md` (nummer #1–#30) för *varför* och för avvikelser från designdokumentet.
3. `docs/Designdokument-v0.4.md` för *krav* (ID:n P/F/N/S/R/U/K/Q/T/DR/V/IF/AL), algoritmer (AL-xx) och kontrakt (IF-x). Där det är äldre än decisions.md gäller decisions.md.
4. Koden. Om koden och dokumenten skiljer sig är det en bugg i antingen koden eller dokumentationen. Avgör inte i tysthet: fråga utvecklaren (ARCH-0.3).

**ARCH-0.2 Obligatorisk rutin före varje kodändring** (gäller människor och AI-agenter):
1. Läs detta dokument.
2. Slå upp berörda krav-ID:n i `Designdokument-v0.4.md` och berörda beslut i `decisions.md`.
3. Använd §8 (ändringskarta) för att se vilka regler och tester som berörs.
4. Kontrollera att ändringen följer reglerna i §3, §4 och §5.
5. Efter ändringen: uppdatera de dokument som ändringen gör inaktuella (§9), och kör berörda tester (§7).

**ARCH-0.3 Brott mot dokumenterad arkitektur kräver utvecklarens godkännande.** Om en ändring skulle bryta mot en regel i detta dokument, mot ett SKA/FÅR INTE i designdokumentet eller mot ett beslut i `decisions.md`:
- Stoppa. Genomför inte ändringen.
- Fråga utvecklaren (Rickard) med: vilken regel (ID), varför den behöver brytas, alternativ som följer regeln, och konsekvens.
- Först efter uttryckligt ja: genomför, logga avvikelsen som nytt beslut i `decisions.md` och uppdatera detta dokument i samma ändring.
- Detta gäller även när ändringen verkar liten eller uppenbart bättre. Utvecklaren avgör.

**ARCH-0.4 Fråga även vid dokumentationsluckor.** Om ett krav är tvetydigt, saknas eller motsäger annat: fråga. Gissa inte (designdokument §17 punkt 3 och 4).

## 1. Systemöversikt

ScoreLite öppnar MuseScore-filer (`.mscz`), ritar noterna och spelar upp dem med en inbyggd synt. Allt sker lokalt på enheten. Appen har ingen nätverksbehörighet (S5) och sparar bara inställningar (DR-25), aldrig partitur (P1).

Tre samverkande delsystem:
- **Tolkning (Core):** fil → `Score` (modell) → spelordning, ljudhändelser, MEI och pekartidslinje. Ren .NET, ingen I/O utöver en `Stream`.
- **Ljud (Audio):** händelser → synt (MeltySynth) → `RingBuffer` → plattformens ljudutgång. Egen tråd.
- **Notbild (JS i webbvy):** MEI → Verovio → SVG, pekare och tryckhantering. Kommunicerar med C# via JSON-meddelanden (IF-5).

`App.Core` limmar ihop dem i ViewModels utan MAUI-beroende. `App` innehåller bara MAUI-vyer och plattformskod.

## 2. Projekt och beroenden

| Projekt | Mål | Beror på | Innehåll |
|---|---|---|---|
| `src/ScoreLite.Core` | net8.0 | bara BCL | Modell, parser, tempo, spelordning, händelser, MEI, pekartidslinje |
| `src/ScoreLite.Audio` | net8.0, C# 13 | Core, MeltySynth (vendrad) | Ringbuffert, synt, metronom, sequencer, motor, `IAudioOutput` |
| `src/ScoreLite.App.Core` | net8.0 | Core, Audio, CommunityToolkit.Mvvm | ViewModels, brygga (IF-5), tjänstegränssnitt, felmeddelanden |
| `src/ScoreLite.App` | net10.0-android (+ net10.0-ios på Mac) | App.Core, Audio, Core, MAUI | Vyer (XAML), plattformskod, `HybridWebView`-brygga, `wwwroot` (HTML/JS/Verovio), ljudfont |
| `tests/*.Tests` | net8.0 | projektet de testar | Egna konsolbaserade testlöpare (ingen xunit) |
| `tests/web`, `tests/verovio-t6` | Node / net8.0 | – | JS-tester mot riktig webbläsare, MEI-dump |

Beroenderiktning (pil = "får referera till"). Ingen pil får gå åt andra hållet:

```
App ──► App.Core ──► Audio ──► Core
 │         │                    ▲
 │         └────────────────────┘
 └──► Audio, Core (direkt, för plattformsljud och fabrik)
```

**Regler (ARCH-2.x):**
- **ARCH-2.1** `Core` refererar bara BCL (P2). Verifieras av testet "P2: Core refererar bara till .NET-basbiblioteket" i `tests/ScoreLite.Core.Tests/Program.cs`.
- **ARCH-2.2** `Audio` refererar inte MAUI, plattforms-API:er eller WebView.
- **ARCH-2.3** `App.Core` refererar inte MAUI och inte plattforms-API:er. Annars går den inte att testa i `ScoreLite.App.Tests` (net8.0). (Avvikelse från designdokument §7.2/§7.3, se decisions #21. Ingen automatisk kontroll finns än.)
- **ARCH-2.4** All plattformskod (`#if ANDROID/IOS`, `Platforms/`), all XAML och all JS/Verovio-kod ligger i `App`.
- **ARCH-2.5** Tester refererar inte MAUI.
- **ARCH-2.6** MeltySynth är vendrad oförändrad i `src/ScoreLite.Audio/MeltySynth/` och får inte ändras (decisions #9). `Audio` använder C# 13 för att den koden inte kompilerar med C# 14 (decisions #28).
- **ARCH-2.7** MuseScores källkod och `instruments.xml` kopieras aldrig in i repot (DR-02, DR-03).

## 3. Lager och ansvar

### 3.1 Core (`ScoreLite.Core`, namnrymd `ScoreLite.Core`)

| Fil | Ansvar | Krav |
|---|---|---|
| `Mscz/MsczReader.cs` | Zip i minnet, gränser (`LoadLimits`), väljer första `rootfile` som slutar på `.mscx` (decisions #18) | S1, S2, AL-01 |
| `Mscx/MscxParser.cs` | Tolerant strömmande XML-tolkning till `ParsedScore` | AL-01 |
| `Model/Model.cs` | `Score`, `Part`, `Staff`, `Note`, `Measure`, `NotatedItem` m.fl., `Diagnostic`, `LoadResult`, `IScoreLoader` | IF-1, IF-2 |
| `ScoreLoader.cs` | `IScoreLoader`-implementation: läser, tolkar, ger `LoadResult` | IF-2 |
| `Time.cs`, `Timing/TempoMap.cs` | Enda platsen för omräkning mellan ticks, frames och ms | AL-02 |
| `Playback/PlayOrder.cs` | `PlayOrder` och `PlayOrderBuilder` (repriser, voltor, hopp) | AL-03 |
| `Playback/EventGenerator.cs` | Ljudhändelser (`PlaybackEvent`) per vald stämma, `DynamicVelocity` | AL-04 |
| `Playback/ChannelAllocator.cs` | MIDI-kanaltilldelning | AL-05 |
| `Notation/MeiExporter.cs` | Modell → MEI. `xml:id` = `Note.Id` | V10 |
| `Notation/CursorTimelineBuilder.cs` | Geometri från JS + spelordning → pekartidslinje | AL-11 |

Avvikelser: `Score.Items` bär skriven notation (decisions #12). Filplaceringen skiljer sig från designdokument §7.3 (t.ex. `Time.cs` i roten, `PlayOrderBuilder` i `PlayOrder.cs`, ingen `Diagnostics/`-mapp). Följ nuvarande placering.

### 3.2 Audio (`ScoreLite.Audio`)

| Typ | Ansvar | Tråd |
|---|---|---|
| `IAudioOutput` | Plattformsutgång. `SampleRate`=44100, `OutputLatencyFrames`, `FramesConsumed`, `Start(RingBuffer)`, `Stop()` (IF-4) | – |
| `OfflineAudioOutput` | Utgång utan ljudkort, deterministisk, för tester (P7) | testtråd |
| `RingBuffer` | Låsfri en-skrivare/en-läsare, block märkta med epok, `Flush(newEpoch)`, `Underruns`, `FramesInCurrentEpoch` | skrivs av audio worker, läses av driver |
| `ISynthHost` / `MeltySynthHost` | Syntinstanser, reverb/chorus av (decisions #14), `SoftLimit` | audio worker |
| `Metronome` | Klick och taktslagsgrid (AL-09), inräkning (AL-10) | audio worker |
| `Sequencer` | Synkron, deterministisk rendering av block ur händelser (AL-06) | audio worker |
| `PlaybackEngine` : `IPlaybackEngine` | Tillståndsmaskin (AL-08), kommandokö, epokprotokoll (AL-07), `PlaybackSnapshot` | egen tråd `ScoreLite.Audio` |

`EngineOptions.UseWorkerThread=false` + `Pump()` ger synkron körning för tester. `IdleCloseSeconds`=30 stänger ljudutgången vid inaktivitet (DR-26).

### 3.3 App.Core (`ScoreLite.App.Core`, namnrymder `ScoreLite.App.Bridge`, `.Services`, `.ViewModels`)

| Typ | Ansvar |
|---|---|
| `IScoreBridge` | Tunt gränssnitt över IF-5: sju sändmetoder (C#→JS), sex händelser (JS→C#) |
| `JsonScoreBridge` (abstrakt) | Serialiserar/tolkar JSON. Plattformsklassen ger bara `Transport(string json)` och anropar `OnJsMessage` |
| `FakeScoreBridge` | Testdubbel som spelar in allt som skickas |
| `BridgeMessages.cs`, `BridgeJson.cs` | Meddelanderecords och (de)serialisering. Fältet `t` anger typ, `v`=1 |
| `BridgeDelayEstimator` | Uppskattar bryggans fördröjning från `ping`/`pong` (AL-12 regel 5) |
| `ScoreViewModel` | Äger `IPlaybackEngine`, `IScoreBridge`, valda stämmor, växlar, `PlaybackSnapshot`. Tryck → `SeekTo` (AL-13). Bygger tidslinje (AL-11). Reagerar på `IAudioInterruptions` (R4) |
| `StartViewModel` | Filval och start av laddning |
| `ErrorMessages` | Felkod → användartext (§12.3) |
| `NullPlaybackEngine` | Reservmotor när ljud/ljudfont inte kan startas (§12.3) |
| Tjänstegränssnitt | `IScoreFilePicker`, `IKeepAwake`, `ISettings`, `IDialogService`, `IAppNavigation`, `IAudioInterruptions` (+ `InMemorySettings` för tester) |

### 3.4 App (`ScoreLite.App`, MAUI)

| Plats | Ansvar |
|---|---|
| `Views/` (`StartPage`, `ScorePage`, `LicensesPage`) | XAML och code-behind. `ScorePage` skapar `ScoreViewModel` och `HybridWebViewScoreBridge`, äger ping-timer och livscykel (R4) |
| `Services/AppServices.cs` | `PreferencesSettings`, `DisplayKeepAwake`, `MauiFilePicker`, `MauiDialogService`, `ScoreSession`, `SoundFontProvider`, `PlaybackEngineFactory`, `MainThreadContext`, `IncomingFiles` |
| `Services/PlatformAudio.cs` | Väljer plattformens `IAudioOutput` och `IAudioInterruptions` via `#if ANDROID/IOS` |
| `Services/HybridWebViewScoreBridge.cs` | `JsonScoreBridge` över `HybridWebView`. All sändning via `MainThread` |
| `Platforms/Android/` | `AndroidAudioOutput` (AudioTrack), `AndroidAudioSession`, `MainActivity` (intent "Öppna med") |
| `Platforms/iOS/` | `IosAudioOutput`, `IosAudioSession`, `AppDelegate.OpenUrl`. Kompilerar i CI (decisions #34), aldrig körd på enhet |
| `Resources/Raw/wwwroot/` | `index.html`, `style.css`, `bridge.js`, `bridge-core.js`, `verovio/` (6.3.0, LGPL). Allt notbildsrelaterat JS bor här |
| `MauiProgram.cs` | DI-registrering. Tjänster som singletons, ViewModels/sidor som transient |

**Komposition (ARCH-3.x):**
- **ARCH-3.1** Plattformstjänster nås bara via gränssnitten i `App.Core.Services`. ViewModels får inte anropa MAUI direkt.
- **ARCH-3.2** `ScoreSession` delar partitur-bytes och motor mellan start- och partitursidan. `ClearData()` anropas direkt efter laddning så att partituret inte hålls kvar (P1).
- **ARCH-3.3** Motorn skapas av `PlaybackEngineFactory.CreateAsync()`. Vid fel returneras `NullPlaybackEngine` (appen kraschar inte).
- **ARCH-3.4** Ljudfonten (`soundfont.sf2`, hämtas av `tools/fetch-soundfont.sh`, checkas inte in) läses en gång av `SoundFontProvider` och delas.

## 4. Trådmodell

| Tråd | Äger | Får | Får inte |
|---|---|---|---|
| UI (MAUI main) | Vyer, ViewModels, alla `HybridWebView`-anrop, snapshot-leverans | Skicka kommandon till `IPlaybackEngine` utan att vänta | Parsa, rendera ljud, vänta på ljudtråden |
| Laddning (`Task.Run`) | `MsczReader`, `MscxParser`, `MeiExporter`, `PlayOrderBuilder`, `EventGenerator` | Köra länge, med `CancellationToken` | Röra ljudobjekt |
| Audio worker (`ScoreLite.Audio`, högsta prioritet) | `Sequencer`, `ISynthHost`, `Metronome`, skrivsidan av `RingBuffer` | Använda förallokerade buffertar | Ta lås som UI håller, göra I/O, allokera i renderloopen |
| Driver-callback (plattform) | Läsa `RingBuffer`, räkna `FramesConsumed` | Kopiera ramar, uppdatera räknare | Allokera, ta lås, anropa annan managed kod |
| `RawMessageReceived` (bakgrundstråd) | – | Vidarebefordra till UI | Anropa `SendRawMessage` direkt (kräver UI-tråd; decisions #22) |

**Regler (ARCH-4.x):**
- **ARCH-4.1** UI → audio: bara via `IPlaybackEngine` (kommandokö, `Post`). Kommandon körs i ankomstordning.
- **ARCH-4.2** audio → driver: bara via `RingBuffer`.
- **ARCH-4.3** audio → UI: `PlaybackSnapshot` via `SynchronizationContext` (`MainThreadContext`) vid varje tillståndsändring och var 500:e ms under uppspelning.
- **ARCH-4.4** Ingen allokering i renderloopen eller driver-callbacken (designdokument §17 punkt 9). Allokeringstest finns inte än.
- **ARCH-4.5** Tre tidsdomäner (`PlayTick`, `frame`, `ms`). All omräkning sker via `TempoMap` och `Time`. Ingen annan kod räknar om.
- **ARCH-4.6** Nuvarande ljudposition: `playedFrames = max(0, ring.FramesInCurrentEpoch - OutputLatencyFrames)`, `currentFrame = epochStartFrame + playedFrames`.

## 5. Brygga C# ↔ JavaScript (IF-5)

Transport: C# → JS med `HybridWebView.SendRawMessage` (JSON-sträng till `window.bridge.receive`). JS → C# med `HybridWebView.SendRawMessage` (på Android som same-origin `fetch`; därför `connect-src 'self'` i CSP, decisions #22). Alla meddelanden har `v` (=1) och `t` (typ). Okända `t` ignoreras och loggas.

| Riktning | `t` | Syfte |
|---|---|---|
| C#→JS | `loadScore` (`requestId`, `mei`, `viewportWidthPx`, `zoomLevel`, `theme`) | Rita nytt partitur |
| C#→JS | `setTimeline` (`layoutGen`, `entries[playMs,x,sys]`) | Pekartidslinje |
| C#→JS | `setClock` (`epoch`, `state`, `playMs`, `bridgeDelayMs`) | Klocksynk för pekaren |
| C#→JS | `setMarker`, `setFollow`, `setZoom`, `ping` | Startmarkör, följ-läge, zoom, latensmätning |
| JS→C# | `ready`, `layoutReady`, `tap`, `followChanged`, `pong`, `renderError` | Status, geometri, tryck, scroll, fel |

**Regler (ARCH-5.x):**
- **ARCH-5.1** Data serialiseras som JSON. Bygg aldrig kodsträngar att köra med `EvaluateJavaScript` (S4).
- **ARCH-5.2** En ändring av meddelandefält kräver samtidig ändring av: `BridgeMessages.cs`, `BridgeJson.cs`, `bridge.js`/`bridge-core.js`, `tests/web/*.mjs`, `tests/ScoreLite.App.Tests` och designdokument §8.5. Höj `v` vid brytande ändring.
- **ARCH-5.3** Noters identitet i MEI och SVG är `Note.Id` (`xml:id`). Verovio bevarar dem (V10, decisions #24). Ändra inte id-schemat utan godkännande.
- **ARCH-5.4** JS sköter bara rendering, pekare och tryck. Ingen musiklogik (tempo, spelordning) i JS.
- **ARCH-5.5 (tema, decisions #30):** notbilden ritas alltid svart på vit bakgrund. `loadScore.theme` ändrar inte papper och bläck. *Status: beslutad men ej genomförd i kod.*

## 6. Dataflöde

```
Öppna fil:   UI(Stream) ─► IScoreLoader.LoadAsync [laddningstråd] ─► Score + Diagnostics
             ─► PlayOrder, EventGenerator, MeiExporter ─► loadScore ─► JS/Verovio
Layout:      JS layoutReady(geometri) ─► CursorTimelineBuilder ─► setTimeline ─► JS
Tryck:       JS tap(id|measure,x) ─► ScoreViewModel ─► ScorePosition ─► IPlaybackEngine.SeekTo ─► setMarker ─► JS
Spela:       Play() ─► [inräkning] ─► Sequencer ─► Synth+Metronom ─► RingBuffer(epok) ─► IAudioOutput
Klocka:      IAudioOutput.FramesConsumed ─► RingBuffer.FramesInCurrentEpoch ─► PlaybackEngine ─► PlaybackSnapshot
             ─► ScoreViewModel ─► setClock ─► JS-pekare (requestAnimationFrame)
Stämmaval:   SetSelectedParts ─► behandlas som hopp till nuvarande position (DR-09, AL-07)
Rotation/zoom: JS renderar om ─► ny layoutReady ─► ny tidslinje. Uppspelningen påverkas inte.
Avbrott:     IAudioInterruptions.Interrupted ─► ScoreViewModel ─► Pause (R4)
```

## 7. Tester (var varje regel kontrolleras)

| Område | Kommando | Antal |
|---|---|---|
| Core + arkitekturregel P2 | `dotnet run --project tests/ScoreLite.Core.Tests` | 56 |
| Audio | `dotnet run --project tests/ScoreLite.Audio.Tests` (Release + `SCORELITE_SOUNDFONT` ger 53) | 44 |
| App.Core (ViewModels, brygga) | `dotnet run --project tests/ScoreLite.App.Tests` | 40 |
| JS-logik | `node tests/web/core.test.mjs` | 6 |
| JS i riktig webbläsare | `node tests/web/test.mjs <mei-mapp>` | 45 |
| MEI-mapp | `dotnet run --project tests/verovio-t6/MeiDump -- corpus <mapp>` | – |
| Android-bygge | `dotnet build src/ScoreLite.App -f net10.0-android` | – |
| iOS-kompilering (CI, macOS) | `.github/workflows/ios-build.yml`: net8.0-testerna + `dotnet build -f net10.0-ios` mot simulator, utan signering (decisions #32) | – |

Antalen är hämtade från `CLAUDE.md` och ändras när tester läggs till. Korpus: `corpus/` (8 egna filer). CI kompilerar iOS-koden men inget test kör den, och inget täcker UI eller prestanda på enhet (se `docs/device-checklist.md`).

## 8. Ändringskarta (vad berörs om jag ändrar X)

| Jag ändrar | Läs/uppdatera också | Kör |
|---|---|---|
| Modell (`Model.cs`) | IF-1, `MscxParser`, `MeiExporter`, `EventGenerator`, decisions #12 | Core.Tests, Audio.Tests, App.Tests |
| Tolkning av `.mscx` | AL-01, `docs/format-notes.md`, decisions #10–13, #18 | Core.Tests + korpus |
| Spelordning / repriser | AL-03, `TempoMap`, `CursorTimelineBuilder` | Core.Tests, Audio.Tests |
| Tid/tempo | AL-02, ARCH-4.5. Ändra bara i `Time.cs`/`TempoMap.cs` | Core.Tests, Audio.Tests |
| Motor / epoker / buffert | IF-3, IF-4, AL-06–08, ARCH-4.x, decisions #14, #19 | Audio.Tests, App.Tests |
| Ljudutgång (plattform) | IF-4, R3, F7, `PlatformAudio.cs`, `docs/device-checklist.md` | Android-bygge, mätning på enhet |
| Brygga / JS | ARCH-5.2, IF-5, AL-12, AL-13, decisions #22–23 | App.Tests, `tests/web` |
| MEI-export | V10, decisions #13, #24 | MeiDump + `tests/web/test.mjs` |
| Notbildens utseende/tema | ARCH-5.5, decisions #30, designdokument §11.2 | `tests/web/test.mjs` (rad ~192–194 ska ändras) |
| ViewModels | §11.3, UI-x, `ScorePage.xaml.cs` | App.Tests |
| Tjänstegränssnitt | `Services.cs`, `MauiProgram.cs`, `AppServices.cs`, plattformsklasser | App.Tests, Android-bygge |
| Projektreferenser / TFM | §2 (ARCH-2.x), `dev-environment.md`, `ScoreLite.sln` | Alla tester |
| Beroenden (NuGet, Verovio, ljudfont) | `docs/legal-review.md`, DR-17, N2 (APK ≤ 50 MB) | Release-bygge, storlek |
| Gränser/säkerhet | §12, S1–S5, CSP i `index.html` | Core.Tests, `tests/web` |

## 9. Hålla dokumentationen aktuell

- Ändrar du ett publikt gränssnitt i designdokument §8: uppdatera §8 (designdokumentet) i samma ändring (§17 punkt 5).
- Avviker du från §5–§9 i designdokumentet: logga i `decisions.md` med datum, val och skäl innan koden checkas in (§17 punkt 12), och uppdatera detta dokument. **Kräver utvecklarens godkännande (ARCH-0.3).**
- Lägger du till, flyttar eller tar bort en fil/typ som nämns i §3: uppdatera tabellen.
- Ändrar du testantal: uppdatera `CLAUDE.md` och §7.
- Ändrar du status för iOS, enhetsmätningar eller juridik: uppdatera `docs/verification-status.md`, `CLAUDE.md` ("Öppet / ej gjort") och `docs/device-checklist.md`.

## 10. Kända avvikelser mellan designdokument och kod

| Designdokument | Faktiskt | Beslut |
|---|---|---|
| §7.2: tre projekt, `App` → `Audio` → `Core` | Fjärde projekt `App.Core` (net8.0, utan MAUI) mellan `App` och `Audio` | #21 |
| §7.3: `ViewModels/`, `Services/` i `App` | Ligger i `App.Core`; `IFilePicker` heter `IScoreFilePicker` | #21 |
| §7.3: filplacering i Core | `Time.cs` i roten, `PlayOrderBuilder` i `PlayOrder.cs`, `PlaybackEvent` i `EventGenerator.cs`, ingen `Diagnostics/` | #12, #14 |
| IF-1: modell | `Score.Items` med `NotatedItem` m.fl. tillagt; `Note.Tpc/Tpc2` | #12 |
| S5: CSP `connect-src 'none'` | `connect-src 'self'` (HybridWebView på Android) | #22 |
| §11.2: tema följer systemet överallt | Notbilden alltid svart på vit (beslutad, ej genomförd) | #30 |
| §16/§17: `soundfont.sf3` | `soundfont.sf2` (GeneralUser GS 2.0.3), hämtas, ej incheckad | #17, #20 |
| §17 punkt 8–9: arkitektur- och allokeringstest | P2-test finns i Core.Tests. Test för `App.Core` utan MAUI och för allokering saknas | – |
| Lösningsfilen | Alla åtta projekt i `ScoreLite.sln` (ändrat 2026-10-26) | #29 |

## 11. Ordlista (för snabb orientering)

`Epok`: räknare som ökar vid hopp/stopp. Block med gammal epok kasseras av läsaren. `Snapshot`: `PlaybackSnapshot(State, Epoch, PlayMs, Marker)`. `Marker`/startmarkör: den position uppspelning börjar från (`ScorePosition`). `PlayTick`: musikalisk tid i spelordningen. `Spelordning`: partituret med repriser/hopp utvecklade (`PlayOrder`). `Brygga`: JSON-kanalen C# ↔ JS. `Tidslinje`: lista `[playMs, x, system]` som låter JS rita pekaren. `Korpus`: testfilerna i `corpus/`. `[FAKTA]/[VERIFIERA]/[DEFAULT]`: verifieringsstatus i designdokumentet.
