# Beslutslogg (genomförande)

Format: datum, val, skäl. Avvikelser från designdokumentet v0.4 är markerade **AVVIKELSE**.

## 2026-10-24

1. **AVVIKELSE: M1 påbörjades före att M0 var klart.** Designen kräver att M0 (spikes på Android och iPhone) är klart först. Spikarna (Verovio i WebView, ljudutgång på enhet) kan inte köras i en miljö utan enheter. M1 (`ScoreLite.Core`) är ren .NET, och V1–V8 och V13 stängdes mot källkoden först. V5 (trumset, tabulatur) och V14 är fortfarande öppna och parsern är tolerant mot dem. M0(a), M0(b), M0(d) och V9–V12 kvarstår och ska göras av någon med enheter.
2. **AVVIKELSE: `net8.0` i stället för `net10.0` (DR-01).** Bygget här hade bara .NET 8 SDK. Byt `TargetFramework` när .NET 10 finns. Core använder bara BCL, så inget annat ändras.
3. **AVVIKELSE: testlöpare utan xunit.** NuGet var inte nåbart. `tests/ScoreLite.Core.Tests` är en konsolapp med egen löpare. Testerna är rena funktioner och kan flyttas till xunit.
4. **AVVIKELSE från IF-1:** `Note` har fått `Tpc` och `Tpc2` (för MEI-exporten). Reprisinformation ligger i en separat `Score.Marks` (`MeasureMarks`) i stället för i `Measure`. `Score` har även `Dynamics`, `TempoEvents` och `FormatVersion`. `Measure.RepeatPreviousMeasures` finns men fylls inte i än (V14).
5. **AL-03 utökad:** (a) ett avsnitt utan `startRepeat` börjar efter föregående slutrepris, (b) en överhoppad volta som bär slutreprisen sätter nästa avsnitts start efter voltan, och genomspelningsräknaren nollställs när voltagruppen är slut. Orsak: `goldberg.mscz` gick annars i loop (`PLAYORDER_LIMIT`). Designdokumentets AL-03 bör uppdateras.
6. **2.06 stöds på bästa sätt (DR-28):** `<track>` och absolut `<tick>`, tuplet-referenser och `endSpanner`-voltor. Alla fem 2.06-demos har exakt tickräkning.
7. **Kvalitetskontroll i parsern:** `VOICE_LENGTH_MISMATCH` (info) räknar takter där röst 1 i första stämman inte fyller takten. Mäts på sista not eller paus, eftersom ett negativt `<location>` kan lämna markören mitt i takten.
8. **Demos levereras inte** (licensen per fil är oklar, §14). Testerna läser dem via `SCORELITE_CORPUS`.

## Inte byggt än (M1–M4)

`ChannelAllocator`, `MeiExporter`, `CursorTimelineBuilder`, instrumentbyte mitt i stycket, trumsetsdefinitioner, taktupprepning, hairpins och artikulationer (trohetsnivå 2), alla projekt under `ScoreLite.Audio` och `ScoreLite.App`.

## 2026-10-25 (återskapande av omgång 2)

9. **MeltySynth vendrad** (MIT, v2.4.1, oförändrad, `src/ScoreLite.Audio/MeltySynth/`). Hämtad från grenen `main` på GitHub, inte från en fast tagg: pinna vid tillfälle. Licenstexten ska med på licenssidan (DR-17, DR-35).
10. **Lokal taktart med stretch** (`stretchN`/`stretchD`, t.ex. 18/16 med 3/2 i goldberg.mscz): längder i notsystemet skalas med `stretchD/stretchN`, och i första notsystemet blir den globala taktarten lokal*stretch (reducerat bråk). Verifierat bara mot syntetiska filer, ej mot goldberg (korpus saknas här). [VERIFIERA]
11. **Tonart** läses ur `<KeySig>` (`concertKey` före `accidental`) i första notsystemet. 4.1+-format ej verifierat (V14).
12. **Avvikelse från IF-1:** `Score.Items` (`NotatedItem`/`NotatedNote`/`TupletInst`) bär skriven notation (ackord, pauser, mätpauser, tuplets, punkter, bindebågar) för `MeiExporter`. `Note.Tpc/Tpc2` har default 99 (saknas).
13. **MEI:** skriven höjd = klingande − `TransposeSemitones`. Stavning: tpc vid oktavtransponering (`% 12 == 0`), annars tpc2, reservstavning från höjd om tpc saknas eller inte stämmer med höjden. `accid` skrivs bara när tonart/tidigare förtecken i takten inte redan ger det. Luckor i en röst blir `<space>`. Klavbyten, tabulatur, förslagsnoter och artikulationer ritas inte.
14. **Audio:** `Sequencer` (synkron, deterministisk) är skild från `PlaybackEngine` (tråd, kommandokö, epoker). Motorn har `EngineOptions.UseWorkerThread=false` + `Pump()` för tester. Declick (64 ramar) görs bara när föregående signal är skild från noll. `MeltySynthHost` har reverb/chorus av (CPU/minne, N2). Ett hopp ger snapshot först efter att drivrutinen kasserat gamla epokens block och läst nya (en callback senare).
15. **Testljudfont:** tests genererar en minimal SF2 (looppad sinus) i minnet (`TestSoundFont.cs`), så ingen riktig ljudfont behövs för tester. K14 är fortfarande öppen.
16. **Verovio-spiken (tests/verovio-t6) ej återskapad:** Node finns inte i miljön. MEI-exporten är därför bara validerad som välformad XML med unika id:n, inte renderad av Verovio (V10 öppen).

## 2026-10-25 (K14 och korpus)

17. **K14 – ljudfont: GeneralUser GS v2.0.3 (SF2, 32 319 396 byte, SHA-256 `9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe`)** föreslås (Q4 återstår). Mätt på desktop (net8, Release) med korpusen och `PlaybackEngine` offline: laddtid 18–30 ms, managed minne 34 MB efter laddning, arbetsminne topp cirka 78 MB, 100–540 gånger realtid, peak 0,5–0,6 efter soft limit, 0 underskott. **Mobil är inte mätt** (M0b). Fonten är 7 % över riktvärdet 30 MB (K8). Licensen (`docs/third-party/GeneralUser-GS-LICENSE.txt`) tillåter fri användning men säger att samplingarnas ursprung inte är fullt känt, vilket DR-17 kräver juridisk granskning av. SF3-vägen förkastad: MeltySynth kan inte läsa SF3 och avkodad storlek är cirka 218 MB. Fonten checkas inte in: `tools/fetch-soundfont.sh` hämtar och SHA-kontrollerar den. Köra K14-testerna: `SCORELITE_SOUNDFONT=<sökväg> dotnet run -c Release --project tests/ScoreLite.Audio.Tests`.
18. **Korpus (`corpus/`, 8 egna filer, MuseScore 4.2–4.7)** används av båda testprojekten som standardkorpus (`SCORELITE_CORPUS` åsidosätter). Den hittade en riktig bugg: MuseScore 4 listar `score_style.mss` som första `rootfile` i `container.xml`, så `MsczReader` valde stilfilen och alla 8 filer gav `EMPTY_SCORE`. Rättat: första `rootfile` som slutar på `.mscx` gäller. Regressionstest tillagt.
19. **RingBuffer.CompleteEpoch:** tystnad efter styckets sista block räknas inte som underskott (R3-mätning).

## 2026-10-25 (Q4)

20. **Q4 godkänd av ägaren: GeneralUser GS v2.0.3 (SF2) är vald ljudfont.** Villkor kvar, **före release**: juridisk granskning enligt DR-17 av (a) fontens licens, särskilt att samplingarnas ursprung inte är fullt känt enligt `docs/third-party/GeneralUser-GS-LICENSE.txt`, (b) Verovio (LGPL, iOS), (c) namnet "MuseScore" (varumärke). Spåras i `docs/legal-review.md`. Mätning på enhet och N2-budget med Verovio återstår.

## 2026-10-25 (appen)

21. **ScoreLite.App.Core (avvikelse från §7.3):** ViewModels, `IScoreBridge`/`JsonScoreBridge`/`FakeScoreBridge`, tjänstegränssnitt, felmeddelanden (§12.3), `BridgeDelayEstimator` och `NullPlaybackEngine` ligger i ett eget net8.0-bibliotek utan MAUI så att de kan testas (T5). `ScoreLite.App` (MAUI) innehåller bara vyer, plattformskod och `HybridWebViewScoreBridge`. `IFilePicker` heter `IScoreFilePicker` (krockar med MAUI:s).
22. **Notbild i `HybridWebView`** (V11 bekräftad): C# -> JS med `SendRawMessage`, JS -> C# med `HybridWebView.SendRawMessage`. Skriptet `_framework/hybridwebview.js` injiceras av MAUI. **Android skickar JS -> C# som same-origin `fetch`**, därför är CSP `connect-src 'self'` i stället för `'none'` (S5 justerad). Appen har ingen INTERNET-behörighet, och anropen går till appens egen virtuella värd. **`RawMessageReceived` kommer på en bakgrundstråd** och `SendRawMessage` kräver UI-tråden: bryggan skickar allt via `MainThread` (hittat som krasch i emulatorn).
23. **Verovio 6.3.0 (LGPL v3)** levereras som oförändrad `verovio-toolkit-wasm.js` (7,3 MB, WASM inbäddad) i `Resources/Raw/wwwroot/verovio/`. `onRuntimeInitialized` anropas inte pålitligt efter laddning, så `bridge.js` provar `new verovio.toolkit()` tills det lyckas. Zoomnivåer 0–4 ger notlinjeavstånd 5,0 / 6,8 / 9,4 / 12,2 / 16,2 px (mätt i Chrome, `ZOOM_SCALES` 28–90).
24. **V10 bekräftad:** Verovio bevarar alla `xml:id` (noter och takter) för alla 8 korpusfiler (T6), både i Node (`tests/verovio-t6`) och i riktig webbläsare (`tests/web`). Två Verovio-varningar om bindebågar (`Unable to match @tie`, `Expected @tie median or terminal`) i Ing-Maries vals och Plaisir d'amour: MEI-exportens `tie`-attribut matchar inte alltid Verovios krav (bindning över röster eller upprepning). Påverkar bara ritningen.
25. **Android:** `AndroidAudioOutput` (AudioTrack, float, stereo, 44100 Hz, `LowLatency`, latens via `GetTimestamp`), `AndroidAudioSession` (ljudfokus och `BECOMING_NOISY` -> Pause, R4), `MainActivity` med intent-filter för ”Öppna med”. Verifierat i emulator (Pixel 6, API 35, x86_64): öppna med, rendering, uppspelning, pekare, följ-läge, ingen krasch. **Inte verifierat:** ljudets hörbarhet och tidsåtgång på riktig enhet (emulatorn kördes med `-no-audio`), R3 (10 minuter utan underskott), F7 (50 ms), N2 på enhet.
26. **iOS (`IosAudioOutput`, `IosAudioSession`, `AppDelegate.OpenUrl`, `Info.plist`) är skrivna men aldrig kompilerade** (kräver Mac). Alla filer är märkta OVERIFIERAD. `ScoreLite.App.csproj` målar bara iOS på macOS.
27. **Storlek (N2):** Release-APK för enbart arm64, utan trimning eller AOT: 50,6 MB (ljudfonten 30,8 MB, resten .NET-körtid, kod och Verovio). Målet är högst 50 MB per arkitektur: **på gränsen**. Åtgärder att mäta: AAB, `PublishTrimmed`, profilerad AOT, beskuren SF2 (K8).
28. **C# 13 för `ScoreLite.Audio`:** den vendrade MeltySynth-koden kompilerar inte med C# 14 (SDK 10). Koden är oförändrad.
29. **Utvecklingsmiljö:** se `docs/dev-environment.md`. (Ändrat 2026-10-26: alla åtta projekt, även `ScoreLite.App`, ligger nu i `ScoreLite.sln` under mapparna `src` och `tests`; `ScoreLite.App` kräver MAUI-arbetsbelastningen i Visual Studio.) Bygg appen med `dotnet build src/ScoreLite.App -f net10.0-android` med den användarlokala SDK:n.

## 2026-10-26 (provkörning på telefon)

30. **Notbilden är alltid svart på vitt (issue från ägaren, ej genomförd i kod).** Provkörning på riktig telefon: uppspelning fungerar. I mörkt tema (systemet) ritas noterna svarta på `#121212` och syns dåligt (`style.css`: `--paper`/`--ink` per `data-theme`). Beslut: webbvyn ritar alltid vit bakgrund (`#fff`) och svarta linjer och noter, oavsett systemtema; resten av appen (startskärm, knappar, paneler) följer fortfarande systemet. Följder att hantera vid genomförande: (a) `style.css` tar bort mörka paletten för `--paper`/`--ink`; (b) pekaren (`--cursor`, ljusblå) och startmarkören (`--marker`, orange) ska använda ljusa temats färger med tillräcklig kontrast mot vitt; (c) `loadScore.theme` (IF-5) behålls i protokollet för att inte ändra gränssnittet, men påverkar inte papper och bläck; avgör om fältet ska tas bort i nästa version av §8; (d) webbtestet `tests/web/test.mjs` rad ~192-194 (”mörkt tema sätts”) ska ändras till att kontrollera att papper och bläck är desamma i båda teman; (e) kontrollera kanten mellan vit notbild och mörk appram (marginal runt notbilden), och att ingen vit blixt syns vid laddning; (f) designdokument §11.2 uppdaterat.

31. **`docs/architecture.md` införd (2026-10-26)** som samlad, maskinläsbar beskrivning av arkitekturen i nuläge, med regel-ID:n (ARCH-x), ändringskarta och lista över avvikelser. Utvecklaren har beslutat att den ska konsulteras före varje kodändring och att brott mot dokumenterad arkitektur alltid kräver utvecklarens godkännande (ARCH-0.2, ARCH-0.3). Regeln står även i `CLAUDE.md`. Vid konflikt gäller `architecture.md` och `decisions.md` före designdokumentet för nuläget.
