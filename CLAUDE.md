# ScoreLite – handoff till Claude Code

## Obligatorisk arbetsregel (gäller varje kodändring)
1. **Läs `docs/architecture.md` först** (nuläge, regler ARCH-x, ändringskarta §8), sedan berörda delar av designdokumentet och `docs/decisions.md`, innan du ändrar kod, projektfiler, JS eller tester.
2. **Bryt aldrig mot dokumenterad arkitektur utan att fråga utvecklaren (Rickard).** Gäller regler i `architecture.md`, SKA/FÅR INTE i designdokumentet och beslut i `decisions.md`. Stoppa, förklara vilken regel (ID), varför, och alternativ som följer den, och vänta på uttryckligt ja. Först därefter: genomför, logga som nytt beslut i `decisions.md` och uppdatera `architecture.md`.
3. Uppdatera dokumentationen i samma ändring som gör den inaktuell (`architecture.md` §9).

Källa till sanning: `docs/Designdokument-v0.4.md` (SKA/BÖR/FÅR, ID:n P/F/N/S/R/U/K/Q/T/DR/V/IF/AL/M/A). Läs den efter `docs/architecture.md` (arkitektur i nuläge, AI-optimerad). Genomförandebeslut och avvikelser: `docs/decisions.md` (#1–29). Miljö: `docs/dev-environment.md`. Enhetstester att fylla i: `docs/device-checklist.md`. Juridik före release: `docs/legal-review.md`.

## Status (2026-10-26)
Core, Audio och en fungerande Android-app finns. Appen har körts i emulator (Pixel 6, API 35): öppna med, rendering, uppspelning, pekare, följ-läge, startmarkör, stämmapanel. iOS-koden är skriven men aldrig kompilerad. Ljud på riktig enhet är inte provat.

Projekt:
- `src/ScoreLite.Core` – parser, modell, spelordning, händelser, MEI-export, pekartidslinje (net8.0).
- `src/ScoreLite.Audio` – ringbuffert, synt (vendrad MeltySynth, C# 13), metronom, sequencer, motor.
- `src/ScoreLite.App.Core` – ViewModels, brygga (IF-5), tjänstegränssnitt (net8.0, utan MAUI).
- `src/ScoreLite.App` – MAUI (net10.0-android; iOS bara på Mac). `Resources/Raw/wwwroot` = index.html, bridge.js, Verovio 6.3.0. Ljudfonten hämtas: `tools/fetch-soundfont.sh src/ScoreLite.App/Resources/Raw`.

Tester (egna testlöpare, ingen xunit). Korpus: `corpus/` (8 egna MuseScore 4-filer).
- `dotnet run --project tests/ScoreLite.Core.Tests` – 56
- `dotnet run --project tests/ScoreLite.Audio.Tests` – 44 (53 med `SCORELITE_SOUNDFONT=<GeneralUser-GS.sf2>`, Release)
- `dotnet run --project tests/ScoreLite.App.Tests` – 40
- `tests/web`: `node core.test.mjs` (6) och `node test.mjs <mei-mapp>` (45, riktig Chrome/Edge). MEI-mapp: `dotnet run --project tests/verovio-t6/MeiDump -- corpus <mapp>`. `tests/verovio-t6/run.mjs` = Verovio i Node.
- Android: `dotnet build src/ScoreLite.App -f net10.0-android` (användarlokal SDK 10, se dev-environment.md). Emulator: AVD `scorelite` (`ANDROID_AVD_HOME=C:\Users\ricka\tools\avd`).

## Öppet / ej gjort
- **Issue (decisions #30):** notbilden ska alltid vara svart på vit bakgrund, även i mörkt tema. Dokumenterat, ej genomfört i `style.css`/`bridge.js`/webbtestet.
- Uppspelning med ljud är provad av ägaren på riktig Android-telefon (2026-10-26); siffror för R3/F7/F6 saknas fortfarande.
- **Enhet:** R3 (10 min utan underskott), F7 (≤50 ms), F6 (≤300 ms), N2 (APK 50,6 MB arm64 utan trimning, på gränsen), kallstart, minne. Mät på telefon (M0b).
- **iOS:** kompilera och prova `IosAudioOutput`, `IosAudioSession`, `AppDelegate.OpenUrl`, `Info.plist`.
- **Juridik (DR-17):** `docs/legal-review.md` är ej gjord (ljudfontens sampelursprung, Verovio/LGPL på iOS, MuseScore-varumärket, MeltySynth-filerna).
- MeltySynth är hämtad från `main`, inte en fast tagg (decisions #9).
- Bindebågar: Verovio varnar för två filer (decisions #24).
- Instrumentbyte mitt i stycket, trummor/tabulatur, mätupprepning (V14), hairpins/artikulationer, jämförelse mot MuseScore MIDI-export (T2), fler korpusfiler (mål ≥30, V14), tempo vid upprepade segment (`TempoMap.Build`).
- Appen: filväljaren, zoom, rotation, metronom/inräkning är inte provade för hand i emulatorn. Release-signering och ikon/splash är mallens.
