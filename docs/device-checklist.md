# Enhetschecklista (T10, M0b)

Fylls i på riktiga enheter. Emulatorn (Pixel 6, API 35) har bara använts för rök-test, se decisions.md #25.

## Rök-test som gjorts i emulatorn (2026-10-25, Android API 35, x86_64, utan ljud)
- [x] Appen startar, startskärmen visas.
- [x] ”Öppna med” (VIEW-intent, `application/x-musescore`) öppnar `Nocturne.mscz`.
- [x] Notbilden ritas av Verovio i `HybridWebView`, med stämnamn och flera system.
- [x] Spela: knappen byter till paus, pekaren rör sig, vyn följer.
- [x] Tryck på en not sätter startmarkören (flaggan syns). Stämmor-panelen öppnas med en kryssruta per stämma.
- [ ] Filväljaren (knappen ”Öppna fil”), avmarkera stämmor, zoom, rotation, metronom/inräkning: ej provat för hand.

## Provat av ägaren på riktig Android-telefon (2026-10-26)
- [x] Uppspelning med ljud fungerar (ej mätt, inga siffror för R3/F6/F7).
- [ ] **Åtgärdat i kod 2026-10-26, ej kontrollerat på telefon:** mörkt systemtema gav svarta noter på mörk bakgrund. Notbilden är nu alltid svart på vitt (decisions.md #30). Kontrollera i mörkt tema: vit notbild, synlig pekare och startmarkör, ingen mörk blixt vid laddning.

## Att göra på enhet
| Punkt | Krav | Resultat |
|---|---|---|
| Ljud hörs, stämmor och metronom | F1–F5 | |
| 10 minuters uppspelning | R3: 0 buffertunderskott (`Underruns`) | |
| Pekaravvikelse | F7: högst 50 ms (T7) | |
| Hopp till hörbart ljud | F6: högst 300 ms | |
| Samtal, notifiering, hörlurar ut, bakgrund | R4: pausar, position behålls | |
| Rotation under uppspelning | omlayout, uppspelning orörd | |
| Stort partitur (20+ stämmor) | öppnar inom 3 s, minne under 300 MB | |
| Installationsstorlek per arkitektur | N2: högst 50 MB (nu 50,6 MB utan trimning) | |
| Kallstart | högst 2 s | |
| Tillgänglighet (TalkBack/VoiceOver, stor text) | N4: etiketter och 44 dp | |
| iPhone: bygg och kör | allt ovan, plus `IosAudioOutput` | kompilerar i CI (simulator, 2026-10-26). **Ej körd på simulator eller enhet** |
