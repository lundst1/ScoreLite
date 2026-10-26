# Verifieringsstatus – vad som är provat och inte (2026-10-26)

> Samlad lista för människor och AI-agenter. Påstå aldrig att något i kolumnen "Ej provat" fungerar. Uppdatera listan när något provas.
> Detaljer finns i `docs/decisions.md` (#-nummer), `docs/device-checklist.md` och designdokumentets V-register (§4.2).

## Nivåer
- **Automatiskt test:** körs av testlöparna eller CI (se `docs/architecture.md` §7).
- **Emulator:** provat för hand i Android-emulatorn (Pixel 6, API 35, utan ljud).
- **Ägaren, telefon:** provat av ägaren på riktig Android-telefon. Utan mätvärden.
- **Kompilerar (CI):** bygger på macOS-runner. Säger inget om beteende.
- **Ej provat:** ingen verifiering finns.

## 1. Plattform: Android

| Område | Status | Källa |
|---|---|---|
| Öppna med, rendering, uppspelning, pekare, följ-läge, startmarkör, stämmapanel | Emulator | decisions #25 |
| Uppspelning med hörbart ljud | Ägaren, telefon (utan mätvärden) | 2026-10-26 |
| Filväljaren (knappen "Öppna fil"), avmarkera stämmor, zoom, rotation, metronom, inräkning | **Ej provat** för hand | device-checklist |
| Ljudets kvalitet och tidsåtgång på enhet | **Ej provat** (inga mätvärden) | decisions #25 |
| R3: 10 min utan buffertunderskott | **Ej provat** | device-checklist |
| F7: pekaravvikelse ≤ 50 ms | **Ej provat** | device-checklist |
| F6: hopp till hörbart ljud ≤ 300 ms | **Ej provat** | device-checklist |
| R4: samtal, notifiering, hörlurar ut, bakgrund | **Ej provat** på enhet (logik täcks av App.Tests) | device-checklist |
| Stort partitur (20+ stämmor): öppnar inom 3 s, minne under 300 MB | **Ej provat** | device-checklist |
| Kallstart ≤ 2 s, minnesanvändning | **Ej provat** | CLAUDE.md |
| N2: APK ≤ 50 MB | Mätt 50,6 MB (arm64, utan trimning). Över gränsen. Åtgärder ej provade (AAB, trimning, AOT, beskuren SF2) | decisions #27 |
| N4: tillgänglighet (TalkBack, stor text, 44 dp) | **Ej provat** | device-checklist |
| Ljusa/mörka teman | **Känd bugg**: noter svarta på mörk bakgrund (decisions #30, ej åtgärdad) | decisions #30 |
| Release-signering, ikon, splash | **Ej gjort** (mallens) | CLAUDE.md |

## 2. Plattform: iOS

| Område | Status | Källa |
|---|---|---|
| Kompilering av `Platforms/iOS/*` och projektet | Kompilerar (CI, simulator, workload-set 10.0.401, Xcode 26.6) | decisions #34 |
| Appen startar i simulatorn | **Ej provat** (CI kör ingen simulator) | – |
| `IosAudioOutput`: ljud, latens, `OutputLatencyFrames`, buffertstorlek | **Ej provat** | decisions #26, #34 |
| `IosAudioSession`: avbrott (samtal, Siri) och utdragna hörlurar | **Ej provat** | decisions #26 |
| `AppDelegate.OpenUrl` ("Öppna med"), `Info.plist` (filtyper, bakgrundsläge) | **Ej provat** | decisions #26 |
| `HybridWebView` på iOS (brygga, CSP `connect-src`) | **Ej provat**. Brygg-beteendet är bara känt från Android (decisions #22) | decisions #22 |
| Verovio/WASM i iOS-webbvy | **Ej provat** | – |
| Signering, provisioning, distribution (Apple Developer-konto) | **Ej gjort** | – |
| Alla krav i device-checklist på iPhone | **Ej provat** | device-checklist |
| CI-bygget vid ny Xcode/runner | **Bräckligt**: pinnat workload-set och Xcode-sökväg (decisions #34) | decisions #34 |

## 3. Core: filformat och notation

| Område | Status | Källa |
|---|---|---|
| Parser mot 8 egna MuseScore 4.2–4.7-filer | Automatiskt test (korpus) | decisions #18 |
| Mål på ≥ 30 korpusfiler (V14), inkl. äldre format (2.06, 3.x) i större urval | **Ej uppnått** (8 filer) | CLAUDE.md |
| Format 4.1+ för tonart, bindebågar, taktupprepning, trumset, tabulatur, instrumentets ljudval (V14) | **Ej verifierat** | decisions #11, V14 |
| Lokal taktart med stretch (goldberg.mscz) | Bara syntetiska filer. [VERIFIERA] | decisions #10 |
| Instrumentbyte mitt i stycket | **Ej stött** | CLAUDE.md |
| Trummor, tabulatur, förslagsnoter, artikulationer, hairpins | **Ritas/spelas inte** | decisions #13 |
| Mätupprepning (taktupprepning, V14) | **Ej stött** | CLAUDE.md |
| Tempo vid upprepade segment (`TempoMap.Build`) | **Ej provat/otydligt**: öppen punkt | CLAUDE.md |
| Jämförelse mot MuseScores MIDI-export (T2) | **Ej gjord** | CLAUDE.md |
| Bindebågar i MEI: Verovio varnar för två filer (Ing-Maries vals, Plaisir d'amour) | **Känd begränsning**, påverkar bara ritningen | decisions #24 |
| Verovio bevarar `xml:id` (V10) | Bekräftat för 8 filer (Node + Chrome) | decisions #24 |

## 4. Ljud (Audio)

| Område | Status | Källa |
|---|---|---|
| RingBuffer, Sequencer, Metronome, PlaybackEngine (epoker, hopp, inräkning) | Automatiskt test (offline) | decisions #14 |
| Med riktig ljudfont (GeneralUser GS): laddtid, minne, hastighet, 0 underskott | Mätt på desktop (net8, Release). **Mobil ej mätt** | decisions #17 |
| Allokeringstest för renderloop och driver-callback (designdokument §17.9) | **Saknas** | architecture.md ARCH-4.4 |
| Instabilt test: ett test i Audio- eller Core-löparna fallerade en gång i CI och passerade vid omkörning | **Ej utrett** | CI 2026-10-26 |
| MeltySynth hämtad från `main`, inte fast tagg | **Ej pinnad** | decisions #9 |

## 5. App.Core, brygga och webb

| Område | Status | Källa |
|---|---|---|
| ViewModels, brygga (JSON), felmeddelanden | Automatiskt test (40 st, med fejkbrygga) | architecture.md §7 |
| `bridge.js`: pekare, följ-läge, tryck, omlayout | Automatiskt test i riktig Chrome/Edge (45 + 6) | CLAUDE.md |
| `HybridWebView`-bryggan på Android | Emulator (+ ägaren, telefon) | decisions #22 |
| Kontrollen att `App.Core` inte refererar MAUI (ARCH-2.3) | **Saknas** (ingen automatisk kontroll) | architecture.md |
| P2-regeln (Core refererar bara BCL) | Automatiskt test | Core.Tests |
| UI-tester (XAML-sidor), skärmbildstester | **Saknas** | – |

## 6. Juridik och leverans

| Område | Status | Källa |
|---|---|---|
| Juridisk granskning (DR-17): ljudfontens sampelursprung, Verovio/LGPL (särskilt iOS), namnet "MuseScore", MeltySynth-filerna | **Ej gjord** (`docs/legal-review.md`) | decisions #20 |
| Licenssida i appen | Sida finns (`LicensesPage`). Innehållets fullständighet **ej granskad** | – |
| Ljudfonten i git-historiken (commit `90f2e84`) | **Kvar i historiken** (spåras inte längre) | decisions #33 |
| Butiksmaterial, namn (Q2), licensmodell (Q1), referensenheter (Q3) | **Ej beslutat** | designdokument §18 |
| Batterimål (Q6) | **Ej beslutat** | designdokument §18 |

## 7. Dokumentation som är känd inaktuell
Åtgärdat 2026-10-26: designdokumentets V10/V11 (§4.2), S5 och §12.2 (CSP `connect-src 'self'`), §7.2 och §7.3 (projekt och filplacering) och ändringsloggen (§20).
Kvar: V12 (lägsta OS-versioner) är ej kontrollerad. Övriga V-rader (V3, V4, V5, V13, V14) har öppna delar enligt §4.2. AL-09 (§9) nämner fortfarande `click_accent.wav` och `click.wav` i text, men klicken är syntetiska. IF-1 (§8.1) saknar `Score.Items`.
