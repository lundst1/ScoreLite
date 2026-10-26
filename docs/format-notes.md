# Formatanteckningar (stänger V-registret i designdokumentet §4.2)

Underlag: MuseScores källträd (huvudgren, `version.cmake` = 5.0.0) och de elva demo-partituren i `demos/`.
Allt nedan är kontrollerat mot källkod eller riktiga filer. Det som inte har kunnat kontrolleras står under "Öppet".

## Bekräftat

| ID | Resultat | Belägg |
|---|---|---|
| V1 | Huvudfilen pekas ut av `META-INF/container.xml` (`rootfile full-path`). MuseScores egen läsare provar `<zipnamn>.mscx`, `score.mscx`, första `.mscx` i roten. `Excerpts/` är separata partitur. | `src/engraving/infrastructure/mscreader.cpp`, demos |
| V2 | 480 ticks per kvartsnot. | `src/engraving/types/constants.h` (`DIVISION`) |
| V3 | `<Tempo><tempo>` är kvartsnoter per sekund. Standard 2.0. `<tempo>1</tempo>` för Adagio. | `constants.h`, `Dawn.mscx` |
| V4 | Positioner är relativa. `<location><fractions>` flyttar markören relativt, **även bakåt**. Se "Fällor" nedan. | `measureread.cpp`, `Reunion.mscz`, `Dawn.mscx` |
| V6 | `<pitch>` och `<tpc>` är klingande, `<tpc2>` är skriven stavning. A-klarinett: `transposeDiatonic` −2 och `transposeChromatic` −3. | `Dawn.mscx` |
| V7 | `<startRepeat/>`, `<endRepeat>N</endRepeat>`, `<Spanner type="Volta">` med `<endings>`, `<Jump>` (`jumpTo`, `playUntil`, `continueAt`), `<Marker><label>`. | `jump.cpp`, `marker.cpp`, demos |
| V8 | Filen bär velocity i varje `<Dynamic>`. Tabell: ppp 16, pp 33, p 49, mp 64, mf 80, f 96, ff 112, fff 126. | `dynamic.cpp`, `Dawn.mscx` |
| V13 | Läsare finns för 1.14, 2.06, 3.x, 4.00, 4.10, 4.60 och 5.00. Demos är 2.06 (5 st), 3.01 (5 st) och en fil märkt 4.00 (skapad i 3.6). | `rw/rwregister.cpp`, demos |

## Delvis bekräftat

- **V5 (instrumentdata).** Bekräftat: `<Instrument>` med `longName`, `trackName`, `instrumentId`, `transposeChromatic`, och `<Channel>` med `<program value>`. Banken anges med `<controller ctrl="0">` och `ctrl="32"` och pekar in i MuseScores egen ljudfont, så parsern ignorerar den (DR-30). Äldre filer saknar ofta `Instrument id`. **Öppet:** trumset (`useDrumset`, `Drum`) och tabulatur finns inte i något demo.

## Öppet (kräver filer från MuseScore 4 och 5)

- **V14:** bindebågar i 4.1 och senare (partiella, laissez vibrer), taktupprepning (`measureRepeatCount`), trumset, tabulatur, `audiosettings.json`.
- **V9–V12:** MeltySynth, Verovio, MAUI WebView. Hör till M0 och kräver riktiga enheter.

## Fällor som hittats vid implementationen

1. **Negativ `<location>` före t.ex. `<Tempo>`.** Rösten kan sluta med markören mitt i takten. Mät röstens längd på sista not eller paus, inte på markören (`Reunion.mscz` takt 4).
2. **2.x-format.** Inga `<voice>`-element. Röst anges med `<track>` (notsystem*4 + röst) och `<tick>` är **absolut** tickposition (`adeste.mscx`). Tuplets definieras med `<Tuplet id>` och refereras med `<Tuplet>id</Tuplet>` i Chord. Voltor har `<Volta id>` och `<endSpanner id>`, där slutet kan ligga i en senare takt (`goldberg.mscz`, `Triumph.mscz`). Bindebågar: `<Tie id>` och `<endSpanner id>`.
3. **Repris utan startrepris.** Ett avsnitt som saknar `<startRepeat/>` börjar efter föregående slutrepris. Om den slutreprisen ligger i en volta-1-takt som hoppas över på andra varvet börjar nästa avsnitt efter voltan (`goldberg.mscz` mätvärden: utan regeln fastnar spelordningen i en loop).
4. **Förslagsnoter** är `Chord` med barnelement `acciaccatura`, `appoggiatura` eller `grace*` och ska inte flytta markören.
5. **Tuplets i 3.x:** `<Tuplet>` öppnar, `<endTuplet/>` stänger, nästlat är tillåtet.
6. **`<Rest>` med `durationType` `measure`:** längden står i `<duration>4/4</duration>` (3.x) eller `<duration z="4" n="4"/>` (2.x).

## Mätvärden på demos (kontroll av tickräkning)

Kontroll: röst 1 i första stämman fyller taktens längd exakt. Alla elva demos har noll avvikelser.

| Fil | Format | Takter | Noter | Spelordning (segment) |
|---|---|---|---|---|
| adeste | 2.06 | 5 | 52 | 5 |
| All_Dudes | 2.06 | 37 | 230 | 69 |
| Amazing_grace | 2.06 | 17 | 175 | 17 |
| Brassed_Up | 3.01 | 27 | 347 | 27 |
| Dawn | 4.00 | 19 | 637 | 19 |
| Dynamic_Strings | 3.01 | 19 | 230 | 19 |
| Fugue_1 | 3.01 | 29 | 913 | 29 |
| goldberg | 2.06 | 990 | 17890 | 1960 |
| Reunion | 3.01 | 23 | 352 | 23 |
| Triumph | 2.06 | 81 | 338 | 80 |
| Unclaimed_Gift | 3.01 | 40 | 154 | 45 |

Spelordningen är **inte** jämförd mot MuseScores MIDI-export (T2), eftersom MuseScore inte kunde köras. Segmentantalen är rimliga men obekräftade.

## Korpus med MuseScore 4.2–4.7 (2026-10-25)

Åtta egna filer (`corpus/`): format 4.20, 4.40, 4.50 (2 st), 4.60 (2 st), 4.70 (2 st). Alla laddas, har sammanhängande takter, giltiga tonhöjder och gav inga fel. Bara diagnostiken `UNKNOWN_ELEMENTS` (7–14 element per fil).

- **Bekräftat (V1, V13):** `container.xml` i 4.x listar `score_style.mss`, huvudfilen, miniatyren och JSON-filerna som `rootfile`, med stilfilen först. Huvudfilen är första posten som slutar på `.mscx`.
- **Bekräftat (V4):** relativa `<location><fractions>` och `<Spanner>` med `<prev>/<next>` förekommer i 4.2-filer och parsas.
- **Format:** `<Instrument id="soprano">` med `<instrumentId>voice.soprano</instrumentId>`, `<Channel><program value="52"/><synti>Fluid</synti>`. Tonart skrivs som `<KeySig><concertKey>`. Varje element har `<eid>`. Alla fyra stämmor i `overTheRainBow.mscz` är ett SATB-körsystem där bara Bas har noter (de andra är tomma, avsiktligt).
- **Fortfarande öppet (V14):** trumset, tabulatur, taktupprepning och partiella bindebågar finns inte i korpusen. Hairpins och artikulationer ignoreras (trohetsnivå 2).
