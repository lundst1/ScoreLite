# ScoreLite

Mobil uppspelare för MuseScore-filer (.mscz) i .NET MAUI. Specifikation: designdokumentet (v0.4).

## Status

| Milstolpe | Status |
|---|---|
| M0 spikes | Delvis. Filformatet, Verovio (6.3.0) och MAUI-bryggan (`HybridWebView`) verifierade. Mätningar på enhet (M0b) återstår. |
| M1 Core | Delvis. Läsare, parser (2.06 och 3.01+), modell, tempokarta, spelordning och händelsegenerering klara och testade mot elva riktiga partitur. |
| M2 Audio | Klar i kod: ringbuffert, metronom, sequencer, motor, vendrad MeltySynth, 44 tester. Ljudfont GeneralUser GS vald (K14/Q4). Enhetsmätningar återstår. |
| M3–M5 | Android-appen fungerar i emulator och uppspelning är provad på riktig telefon (öppna, rita, spela, pekare). Notbilden är alltid svart på vitt, även i mörkt tema (decisions #30, ej provat på telefon). iOS skriven men ej byggd. Enhetsmätningar återstår (`docs/device-checklist.md`). |
| M6 | Ej påbörjad (juridik, release). |

## Bygga och testa

Kräver .NET 8 SDK (eller 10, se `docs/decisions.md`). Core har inga externa paket.

```
dotnet build src/ScoreLite.Core -c Release
SCORELITE_CORPUS=/sökväg/till/mscz-filer dotnet run --project tests/ScoreLite.Core.Tests -c Release
```

Utan `SCORELITE_CORPUS` körs bara de syntetiska testerna. Om `dotnet restore` inte når NuGet:
`dotnet restore --source <tom-mapp>` fungerar eftersom projekten saknar paketberoenden.

## Struktur

```
src/ScoreLite.Core/    ren .NET (P2): Mscz, Mscx, Model, Timing, Playback
src/ScoreLite.Audio/   ringbuffert, synt, sequencer, motor
src/ScoreLite.App.Core/ ViewModels, brygga (utan MAUI)
src/ScoreLite.App/     MAUI-app (Android; iOS bara på Mac)
tests/                 Core.Tests, Audio.Tests, App.Tests (konsolbaserade), web, verovio-t6
docs/                  architecture.md (börja här), verification-status.md (vad som är provat), Designdokument-v0.4.md, decisions.md, dev-environment.md, device-checklist.md, legal-review.md, format-notes.md
```
