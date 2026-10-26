# Designdokument: ScoreLite – mobil MuseScore-uppspelare i MAUI (v0.4)

Oct 20, 2026 · @Rickard

**Version 0.4 (kontrollerad mot MuseScores källkod).** ScoreLite är en skrivskyddad mobilspelare för `.mscz`-filer i .NET MAUI. Dokumentet är specifikationen appen ska byggas efter och är skrivet så att både utvecklare och språkmodeller kan följa det utan att gissa. Ändringar finns i §20.

## 1. Läsanvisning

- Dokumentet är en specifikation, inte en diskussion. **SKA** och **SKA INTE** är krav. **BÖR** gäller om inget starkt skäl finns, och avvikelsen ska dokumenteras. **FÅR** är tillåtet men valfritt.
- Prioritet vid konflikt: §6 (beslutsregistret) före §5 (krav) före §9 (algoritmer) före övrig text.
- Varje ID är unikt och ska användas vid hänvisning: P = princip, F = funktionskrav, N = kvalitetskrav, S = säkerhetskrav, R = robusthetskrav, U = icke-mål, K = risk, Q = fråga till människa, T = testnivå, DR = beslut, V = sak att verifiera, IF = gränssnitt, AL = algoritm, M = milstolpe.
- Text är på svenska. Identifierare, filnamn, JSON-fält och kod är på engelska och ska skrivas exakt som de står.
- Kodblock i §8 och §9 är normativa för namn, signaturer, ordning och beteende. Allt som inte anges får implementatören välja.
- Saknas information: använd ett \[DEFAULT\]-värde. Finns inget, anta det enklaste som uppfyller kraven, skriv antagandet i `docs/decisions.md` och fortsätt. Fråga bara om något blockerar (se §18).

Statusetiketter:

| Etikett | Betydelse | Hur den behandlas |
| --- | --- | --- |
| \[FAKTA\] | Läst i en angiven källa. | Kan litas på. |
| \[BESLUT\] | Designval som är fattat. | Följs. Ändras bara genom en ny rad i §6. |
| \[DEFAULT\] | Förvalt värde som ägaren kan ändra. | Följs tills annat sägs. Ska vara billigt att byta. |
| \[VERIFIERA\] | Antagande från minnet, inte kontrollerat mot källa. | Behandla **aldrig** som fakta. Kontrollera mot riktiga filer eller dokumentation i M0 och skriv resultatet i `docs/format-notes.md`. Se V-registret i §4.2. |

## 2. Sammanfattning och designprinciper

Appen läser en `.mscz`, visar noterna och spelar upp dem med valda stämmor, metronom, inräkning, valfri startpunkt och en pekare som följer ljudet. Fem beslut bär designen:

1. **Egen parser i C#** (DR-02): ingen MuseScore-kod används.
2. **Instrumentdata läses ur filen** (DR-03): ingen inbyggd instrumentlista behövs för att täcka alla instrument.
3. **Verovio i en WebView ritar noterna** (DR-04): ingen layoutmotor skrivs i C#.
4. **En SoundFont-synt renderar ljudet** (DR-05) och metronomen mixas in i samma ström (DR-07).
5. **Ljudramarna är tidens sanning** (DR-06): pekaren styrs av hur många ramar som faktiskt spelats.

Designprinciper (invarianter som aldrig får brytas):

| ID | Princip |
| --- | --- |
| P1 | Appen är skrivskyddad och offline: den ändrar aldrig användarens filer och har ingen nätverksåtkomst. |
| P2 | `ScoreLite.Core` är ren .NET utan beroende av UI, plattform eller WebView. |
| P3 | Ljudramar är sanningen om tid. Ingen annan klocka får styra uppspelningens position. |
| P4 | Notbild och ljud delar bara identiteter (`Note.Id`, `ScorePosition`). Inget annat tillstånd delas mellan dem. |
| P5 | Fail soft: en delvis tolkad fil som går att använda är bättre än en krasch. Allt som hoppas över rapporteras som `Diagnostic`. |
| P6 | Inget utanför omfånget (U1–U10) byggs, även om det vore enkelt. |
| P7 | Determinism: samma fil ger samma `Score`, `PlayOrder` och händelseström. Core använder inga slumptal och ingen systemtid. |

## 3. Ordlista

En term har exakt en betydelse i hela dokumentet och i koden. Använd inte synonymer.

| Term | Betydelse | Kodnamn |
| --- | --- | --- |
| Stämma | En rad i partiturets instrumentlista. Kan ha flera notsystem och byta instrument under stycket. I gränssnittet heter den ”stämma”. | `Part` |
| Instrument | Ljuddefinition (MIDI-program, bank, transponering, omfång, trumset) som gäller för en stämma under ett tickintervall. | `InstrumentSpan` |
| Notsystem | En notrad inom en stämma (piano har två). | `Staff` |
| Röst | Röst 1–4 inom ett notsystem. | `Note.Voice` |
| Tick | Heltalsenhet för musikalisk tid. 480 per kvartsnot (bekräftat i källkoden, V2). | `int` |
| ScoreTick | Tickposition i partituret som det är skrivet, med repriser inte utvecklade. | `int` |
| PlayTick | Tickposition i den utvecklade spelordningen. Växer monotont under uppspelning. | `long` |
| ScorePosition | Stabil adress i notbilden: takt och tick inom takten. Startmarkören lagras som detta. | `ScorePosition` |
| Spelordning | Lista av takter i den ordning de spelas, med repriser, voltor och hopp utvecklade. | `PlayOrder` |
| Ram | Ett ljudsampel per kanal. Varar 1/SampleRate sekunder. SampleRate är 44100 Hz \[DEFAULT\]. | `long frames` |
| PlayMs | Tid i millisekunder från början av spelordningen: `frame * 1000 / SampleRate`. | `double PlayMs` |
| Epok | Heltal som ökar vid varje hopp, valändring och stopp. Ljud och klockvärden från en äldre epok kastas. | `int Epoch` |
| Startmarkör | Platsen uppspelningen börjar från. Visas som en liten flagga i notbilden. | `Marker` |
| Uppspelningspekare | Den rörliga linje som visar var ljudet är just nu. | (i `bridge.js`) |
| Valda stämmor | Mängden stämmor som ska höras. Aldrig tom. | `SelectedPartIds` |
| Ankarpunkt | Meddelandet `setClock` (epok, tillstånd, `PlayMs`, bryggfördröjning) som styr pekaren i JS. | `setClock` (IF-5) |
| Chase | Att återskapa senaste programbyte och controller-värden per kanal fram till en startpunkt innan första noten spelas. | `Chase` |
| Inräkning | En takts klick före uppspelningen. | `CountIn` |
| Klingande höjd | Tonhöjd som hörs. Används för ljud. | `MidiPitch` |
| Skriven höjd | Tonhöjd som står i noterna för ett transponerande instrument. Används för notbilden. | (exporteras till MEI) |

## 4. Underlag och verifieringsstatus

Underlaget är MuseScores källträd (uppladdad zip av huvudgrenen, som enligt `version.cmake` är version 5.0.0 under utveckling) samt wikisidorna och handboken som lästes tidigare. **Läst i källträdet:** `src/engraving` (filläsare i `rw`, datamodell i `dom`, `infrastructure/mscreader`, `playback`), `src/project`, `share/instruments`, `share/sound`, `demos` och `version.cmake`. **Inte läst eller inte tillgängligt:** ramverksmodulen `muse` (ingår inte i zip-filen, och därmed inte ljudmotorn), filer sparade av MuseScore 4.1 eller senare (demos har bara format 2.06 och 3.01 samt en fil märkt 4.00 som skapats i 3.6), filer med trumset, tabulatur eller taktupprepning, samt MeltySynth, Verovio och MAUI. Det som fortfarande är antagande är markerat \[VERIFIERA\].

### 4.1 Verifierade fakta \[FAKTA\]

| ID | Fakta | Källa | Konsekvens för ScoreLite |
| --- | --- | --- | --- |
| A1 | MuseScore Studio är C++/Qt och licensierat under GPL v3. | README, `LICENSE.txt` | Kopierad kod gör appen till GPL (DR-02, DR-17). |
| A2 | Källträdet innehåller bland annat `src/engraving` (`rw` läser och skriver filer, `dom` är modellen, `playback` bygger uppspelningshändelser), `src/project` och `src/importexport`. Ramverket `muse` med ljudmotorn ingår inte i zip-filen. | Zip, wiki: CodeStructure | Layout lämnas till Verovio. Ljudmotorn kan inte läsas, så wikins beskrivning (A3) kan inte kontrolleras. |
| A3 | Wikin beskriver tre ljudlager i separata trådar (huvudapp, audio worker, audio driver) som pratar via en RPC-kanal och en `AudioBuffer`. Sidan kan vara föråldrad. | Wiki: Audio overview | Samma trådmodell används i §7.4. |
| A4 | `.mscz` är ett ZIP-arkiv. MuseScore väljer huvudfil i ordningen `<zipnamn>.mscx`, `score.mscx`, första `.mscx` i roten. `META-INF/container.xml` finns i demo-filerna (`rootfile full-path`) men MuseScores egen läsare använder den bara för att avgöra att filen är en behållare. Övriga poster: `Thumbnails/thumbnail.png`, `Pictures/*`, `score_style.mss`, `chordlist.xml`, `audiosettings.json`, `viewsettings.json`, `automation.json` och `Excerpts/<namn>/<namn>.mscx` (utdrag av stämmor som egna partitur). | `infrastructure/mscreader.cpp`, demos | AL-01, DR-29. |
| A5 | Det finns läsare för formatversion 1.14, 2.06, 3.x (302), 4.00, 4.10, 4.60 och 5.00. `MSC_VERSION` är 500 ("5.00"). Demos: fem filer i 2.06, fem i 3.01 och en märkt 4.00. | `rw/rwregister.cpp`, `types/constants.h`, demos | DR-28. |
| A6 | Ticks: `DIVISION = 480` per kvartsnot. | `types/constants.h` | V2 stängd. |
| A7 | Tempo lagras som `BeatsPerSecond` (kvartsnoter per sekund). `DEFAULT_TEMPO` är 2.0 (120 BPM). I filen: `<Tempo><tempo>1</tempo>` för Adagio. Gradvisa tempoändringar har egen klass (`GradualTempoChange`, `TempoTimeline`). | `types/constants.h`, `Dawn.mscx`, `dom/` | V3 stängd. Gradvisa ändringar ligger kvar i trohetsnivå 2. |
| A8 | Filstruktur: `<museScore version>` → `<Score>` → `<Part>` (med `<Staff id>`-definitioner, `<trackName>` och `<Instrument>`) → därefter ett `<Staff id="N">`-block per notsystem med `<Measure>`. I en takt finns ett `<voice>` per röst. Tomma `<voice/>` skrivs för att behålla röstnumret. Noter: `<Chord>` med `<durationType>` och `<Note>` med `<pitch>` (klingande MIDI-höjd), `<tpc>` och `<tpc2>`. | `Dawn.mscx`, `rw/read500/measureread.cpp`, `rw/write/twrite.cpp` | AL-01. |
| A9 | Positioner lagras inte absolut. Tickräknaren per röst flyttas av varaktigheter. `<location><fractions>` är en relativ flytt (bråk av helnot) från aktuell position. I spanners kan `<measures>` och `<voices>` förekomma. En takt med avvikande längd har `len="2/4"` och `<irregular>1</irregular>`. En hel takts paus är `<Rest><durationType>measure</durationType><duration>4/4</duration>`. | `Reunion.mscz`, `Dawn.mscx`, `measureread.cpp` | V4 stängd. AL-01. |
| A10 | Tuplets: `<Tuplet>` (med `normalNotes`, `actualNotes`, `baseNote`) öppnar och `<endTuplet/>` stänger. De kan nästlas. | `Reunion.mscz`, `measureread.cpp` | AL-01. |
| A11 | Instrument i filen: `<Instrument id="flute">` (id saknas i många äldre filer) med `longName`, `shortName`, `trackName`, `minPitchP/maxPitchP/minPitchA/maxPitchA`, `instrumentId` (MusicXML-id), `transposeDiatonic` och `transposeChromatic` (bara om de är skilda från 0), `Articulation` med `velocity` och `gateTime`, samt en eller flera `<Channel>`. En kanal har valfritt `name` (arco, pizzicato, tremolo, open, mute), `<controller ctrl value>` (banken anges med ctrl 0 och ctrl 32), `<program value>`, `<synti>`, `midiPort` och `midiChannel`. Trumset skrivs som `<useDrumset>` följt av `Drum`-element (enligt skrivarkoden, inget demo har det). Klav G skrivs inte. | `Dawn.mscx`, `twrite.cpp` | V5 delvis stängd. DR-30. |
| A12 | Dynamik: `<Dynamic><subtype>pp</subtype><velocity>33</velocity>`. Filen bär alltså själva velocityn. Kodens tabell: ppp 16, pp 33, p 49, mp 64, mf 80, f 96, ff 112, fff 126, ffff 127, fp 96, sf och sfz 112. Filer kan ha andra värden än tabellen (till exempel fp 110), därför gäller filens värde. Hairpin är en spanner med `veloChange`. | `dom/dynamic.cpp`, `Dawn.mscx` | V8 stängd. DR-32. |
| A13 | Repriser: `<startRepeat/>` och `<endRepeat>N</endRepeat>` i `<Measure>`. Volta: spannern `<Volta>` med `<endings>`, längd i takter i `<next><location><measures>`. Hopp: `<Jump>` med `jumpTo`, `playUntil`, `continueAt` och `playRepeats` (förvalt av). Standardhopp: D.C. (start→end), D.C. al Fine (start→fine), D.C. al Coda (start→coda, fortsätt vid codab), D.S. (segno→end), D.S. al Fine, D.S. al Coda samt Doppia coda. `<Marker>` har `<label>`: segno=`segno`, kodasymbol=`codab`, varcoda=`varcoda`, Fine=`fine`, To Coda=`coda`. | `dom/jump.cpp`, `dom/marker.cpp`, `Brassed_Up.mscx`, `Unclaimed_Gift.mscx` | V7 stängd. AL-03. |
| A14 | MuseScores spelordningsklass `RepeatList` är cirka 1000 rader och hanterar bland annat överlappande och öppna voltor. | `dom/repeatlist.cpp` | AL-03 är avsiktligt enklare. T2 avgör avvikelser. |
| A15 | Taktupprepning (simile) finns som egen funktion (`MeasureRepeat`, `<measureRepeatCount>` i taktläsaren) och måste utvecklas vid uppspelning. | `dom/measurerepeat.h`, `measureread.cpp` | DR-33, AL-03. |
| A16 | Bindebågar är spanners i noten i format 3.x (`<Spanner type="Tie">` med plats för föregående eller nästa not). 5.0-läsaren har dessutom `PartialTie` och laissez vibrer. | `Unclaimed_Gift.mscx`, `read500/tread.cpp` | AL-01, V14. |
| A17 | `audiosettings.json` innehåller per spår `partId`, `instrumentId`, `volumeDb`, `balance`, `mute` och `solo` samt ljudval. | `project/internal/projectaudiosettings.cpp` | DR-31. |
| A18 | `instruments.xml` har 644 instrument. | `share/instruments/instruments.xml` | N1:s omfattning. |
| A19 | Ljudfonter i repot: `FluidR3Mono_GM.sf3` (23,7 MB) och `MS Basic.sf3` (51,3 MB), båda SF3 (Ogg-komprimerade). Licensfilerna anger MIT för FluidR3 och härledda fonter, med krav på att erkännanden och copyright-notiser följer med. READMEn säger att den medföljande "MuseScore\_General" bara är en platshållare, en kopia av `FluidR3Mono_GM.sf3`. | `share/sound/*` | DR-05, DR-35, K14. |
| A20 | Demos: elva partitur. De täcker repriser, voltor, D.S. al Coda, trioler, transponerande instrument (A-klarinett, F-horn, kontrabas), upptakt (`len="2/4"`) och namngivna kanaler. De saknar trumset, tabulatur, taktupprepning och format 4.1 eller senare. | `demos/` | Räcker inte som korpus (§14). |

### 4.2 Register över det som ska verifieras \[VERIFIERA\]

Status per punkt efter genomgången av källträdet. Det som är öppet ska stängas i M0 genom att inspektera filer, framför allt sådana som skapats med MuseScore 4 och 5, och skriva resultatet i `docs/format-notes.md`. Kolumnen ”Kvar” anger vad som återstår.

| ID | Antagande | Status | Resultat och korrigering | Kvar |
| --- | --- | --- | --- | --- |
| V1 | `.mscx` ligger i ZIP-roten, utpekad av `container.xml`. | Bekräftad, korrigerad | Posten heter `<zipnamn>.mscx` och pekas ut av `rootfile full-path`. MuseScore själv provar `<zipnamn>.mscx`, `score.mscx` och första `.mscx` i roten. `Excerpts/` är separata partitur. AL-01 är ändrad (A4). | Inget. |
| V2 | 480 ticks per kvartsnot. | Bekräftad | `DIVISION = 480` (A6). | Inget. |
| V3 | Tempo lagras som kvartsnoter per sekund. | Bekräftad | Se A7. | Filformatet för gradvisa tempoändringar. |
| V4 | Notpositioner är implicita, med relativa `location`-element. | Bekräftad, preciserad | Se A8 och A9. Relativ flytt, inte absolut position. | Kontrollera i filer från 4.1 och senare. |
| V5 | Varje stämma bär instrumentdata. | Delvis bekräftad, korrigerad | Se A11. Banken är controller 0 och 32, inget eget fält. Flera `Channel` per instrument. `Instrument id` saknas ofta i äldre filer. | Trumset (`Drum`) och tabulatur i riktiga filer. |
| V6 | Transponerande instrument lagrar klingande höjd samt en stavning för skriven höjd. | Bekräftad | `<pitch>` och `<tpc>` är klingande. `<tpc2>` är skriven stavning. Transponeringen ges av `transposeDiatonic` och `transposeChromatic` (A-klarinett: −2 och −3, `Dawn.mscx`). | Inget. |
| V7 | Namn och attribut för repriser, voltor, hopp, marker, coda och fine. | Bekräftad | Se A13. | Taktupprepning (A15) och flera nummer i `<endings>`. |
| V8 | Standardvelocity per dynamikmärke. | Bekräftad, förenklad | Filen bär velocityn i varje `<Dynamic>` (A12). Tabellen behövs bara som reserv. | Standardnivå när inget märke finns (DR-32 antar mf). |
| V9 | MeltySynth stöder SF2 men inte SF3, har 16 kanaler med slagverk på kanal 10 och kan dela `SoundFont` mellan instanser. | Bekräftad delvis | MeltySynth läser SF2 men inte SF3 (källkoden). Delat `SoundFont` mellan instanser fungerar (AL-05, 17 stämmor testat). Slagverk ligger på kanal 9. | Mätning på enhet (M0b). |
| V10 | Verovio bevarar `xml:id`, ger geometri via DOM och hanterar slagverk och tabulatur. | Delvis bekräftad (2026-10-25, decisions #24) | Verovio 6.3.0 bevarar alla `xml:id` (noter och takter) för alla 8 korpusfiler, både i Node och i riktig webbläsare. Geometri via DOM fungerar. Två varningar om bindebågar i två filer. | Slagverk och tabulatur (ritas inte, decisions #13). |
| V11 | MAUI:s `HybridWebView` eller `WebView` stöder meddelanden åt båda håll. | Bekräftad på Android (2026-10-25, decisions #22) | `HybridWebView`: C# → JS med `SendRawMessage`, JS → C# med `HybridWebView.SendRawMessage`. På Android skickar JS → C# som same-origin `fetch`, därför `connect-src 'self'` (S5). `RawMessageReceived` kommer på en bakgrundstråd och `SendRawMessage` kräver UI-tråden. | iOS (ej provat, `docs/verification-status.md`). |
| V12 | Lägsta OS-versioner som vald MAUI-version stöder. | Öppen | Ej kontrollerat. | MAUI-dokumentationen. |
| V13 | Skillnader mellan formatversionerna som påverkar parsern. | Delvis bekräftad | MuseScore har separata läsare per version (A5). `README` för `rw` säger att 114–400 delar kod och att 410, 460 och 500 är kopior som utvecklats vidare. Version 5.00 är under utveckling. | Jämför 4.0, 4.1, 4.6 och 5.0 på riktiga filer. |
| V14 | Hur 4.1 och senare skriver bindebågar (inklusive partiella och laissez vibrer), taktupprepning, trumset, tabulatur och instrumentets ljudval. | Öppen | Inga demo-filer i dessa format. | M0: skapa filer med MuseScore 4 och 5. |

## 5. Krav

Varje krav har ett accepttest som kan avgöras med ett mätvärde eller ett ja/nej. Siffror märkta \[DEFAULT\] är förslag som ägaren kan ändra.

### 5.1 Funktionskrav

F1–F3 är en enda mekanism: en mängd valda stämmor (DR-08). Standard är alla stämmor valda.

| ID | Krav | Accepttest |
| --- | --- | --- |
| F1 | Appen SKA kunna spela upp alla stämmor. | I ett partitur med minst 8 stämmor ger offline-rendering ljud från varje stämmas noter. |
| F2 | Appen SKA kunna spela upp en vald stämma. | Med en stämma vald innehåller utdata inga noter från andra stämmor. Metronomen påverkas inte. |
| F3 | Appen SKA kunna spela upp flera valda stämmor. | Med en valfri delmängd vald innehåller utdata exakt de stämmornas noter. |
| F4 | Användaren SKA kunna slå av och på metronomen, även under uppspelning. | Klick läggs på beräknad ramposition utan avvikelse. Första slaget i takten har accent. Fungerar i 4/4 och 6/8. |
| F5 | Användaren SKA kunna slå av och på inräkning. | På: antal klick är lika med taktartens räknetal vid startpositionen, och inget musikljud spelas före sista klicket. Av: musiken startar direkt. |
| F6 | Användaren SKA kunna ställa sig var som helst i stycket och starta därifrån. | Tryck på valfri not, paus eller takt sätter startmarkören. Med inräkning av ger start efter tryck, och hopp under pågående uppspelning, hörbart ljud från den platsen inom 300 ms \[DEFAULT\]. Med inräkning på börjar klicken inom 300 ms. |
| F7 | En grafisk pekare SKA visa var uppspelningen är. | Pekaren avviker högst 50 ms \[DEFAULT\] från det hörbara ljudet under ett helt stycke, ligger på rätt notsystemrad, och vyn scrollar så att den hålls synlig. |

### 5.2 Kvalitetskrav

| ID | Krav | Accepttest |
| --- | --- | --- |
| N1 | Alla instrumenttyper i MuseScore SKA stödjas. ”Stöd” definieras i §10. | Testmatrisen i §14 går igenom för varje instrument utan fel. |
| N2 | Appen SKA vara lightweight jämfört med MuseScore. | Budgetarna i §13 uppfylls på referensenheterna. |
| N3 | Appen SKA gå att köra på Android (API 26+) och iPhone (iOS 15+) \[DEFAULT, se V12\]. | Samma funktionstester passerar på båda. |
| N4 | Gränssnittet SKA vara användbart med skärmläsare och stora texter. | Alla kontroller har tillgänglighetsetikett och minst 44 × 44 dp tryckyta. |

### 5.3 Säkerhets- och robusthetskrav

Detaljer och beteenden finns i §12.

| ID | Krav |
| --- | --- |
| S1 | En `.mscz` SKA INTE packas upp till disk. Läs poster direkt ur arkivet i minnet. |
| S2 | Storleksgränser SKA gälla: arkivet, varje post och totalt uppackad storlek (§12). |
| S3 | XML SKA läsas med DTD-hantering avstängd. |
| S4 | Data från filen SKA INTE skickas till JavaScript som kod. Det skickas som JSON-data. |
| S5 | WebView SKA bara visa lokalt paketerat innehåll och SKA INTE ha nätverksåtkomst. (CSP: se §12.2, `connect-src 'self'`.) |
| R1 | Inget undantag från parser, synt eller brygga SKA nå användaren som en krasch. Det blir ett felmeddelande (§12). |
| R2 | En delvis tolkbar fil SKA öppnas med varningar i stället för att avvisas (P5). |
| R3 | Ljudet SKA inte knastra på grund av arbete i UI- eller parsertråd. Mått: noll buffertunderskott under ett 10 minuters uppspelningstest. |
| R4 | Avbrott (samtal, ljudfokus, hörlurar utdragna, appen i bakgrunden) SKA hanteras: uppspelningen pausas och position behålls. |

### 5.4 Icke-mål

Det här SKA INTE byggas (P6). Även ”små” tillägg räknas.

| ID | Icke-mål |
| --- | --- |
| U1 | Redigering och notinmatning (inklusive MIDI-inmatning). |
| U2 | Tempoändring eller tempoval av användaren. |
| U3 | Transponering. |
| U4 | Slingor (loop). |
| U5 | Mixer: volym eller panorering per stämma. |
| U6 | Export, utskrift och delning. |
| U7 | Plugins och extensions. |
| U8 | MuseSounds och externa ljudbibliotek. |
| U9 | Andra filformat än `.mscz`. Okomprimerad `.mscx` FÅR öppnas eftersom innehållet är detsamma \[DEFAULT\]. |
| U10 | Konto, moln, nätverksfunktioner och analys/telemetri. |

## 6. Beslutsregister

Ett beslut ändras bara genom att raden här ändras. Status \[BESLUT\] är fast. \[DEFAULT\] gäller tills ägaren säger annat och ska hållas billigt att byta.

| ID | Beslut | Skäl | Förkastat alternativ | Status |
| --- | --- | --- | --- | --- |
| DR-01 | .NET 10 (LTS) med MAUI. MVVM med CommunityToolkit.Mvvm. | Långtidssupport och etablerat mönster. | .NET 9. | \[DEFAULT\] (V12) |
| DR-02 | Egen parser skriven utifrån filformatet och observerat beteende. Ingen MuseScore-kod kopieras eller översätts. | MuseScore är GPL v3 (A1). Ren parser håller appens licens fri. | Översätta MuseScores C++ till C#. Då blir appen GPL. | \[BESLUT\] |
| DR-03 | Instrumentdata läses ur partituret. `instruments.xml` levereras inte med appen. | Licens, och nya instrument fungerar utan appuppdatering. | Bunta in `instruments.xml`. | \[BESLUT\] (villkor V5, reserv i §10) |
| DR-04 | Notbild: Verovio (JS/WASM) i en WebView. Indata är MEI från `MeiExporter` med egna `xml:id`. | Ingen layoutmotor i C#. Id-bevarande ger enkel koppling till modellen. | Egen renderare (SkiaSharp). MusicXML som indata. | \[BESLUT\] (villkor V10) |
| DR-05 | Synt: MeltySynth (ren C#, MIT), vendrad. Ljudfont: **GeneralUser GS v2.0.3 som SF2** (32,3 MB, GM/GS-kompatibel, egen tillåtande licens, se `docs/third-party/`). Mätt i K14 (desktop, net8): laddtid 20–30 ms, managed minne 34 MB, arbetsminne topp cirka 80 MB, rendering 100–500 gånger realtid. Fonten ligger inte i repot utan hämtas med `tools/fetch-soundfont.sh` (SHA-256-kontrollerad). Väntar på Q4 (godkännande av storlek och licens). | MeltySynth kan inte läsa SF3 (”SoundFont3 is not yet supported”, bekräftat i källkoden) och MuseScores SF3 blir cirka 218 MB avkodad. | SF3-avkodare (för stort minne), FluidSynth via P/Invoke, FluidR3Mono\_GM.sf3, TimGM6mb (GPL v2). | \[BESLUT\] (V9 delvis stängd, Q4 öppen) |
| DR-06 | Tid: ljudramar. Ljud går via en ringbuffert på 200 ms med epokmärkning. | Pekaren måste följa det som faktiskt hörs (P3). | Klocka eller timer i UI-tråden. | \[BESLUT\] |
| DR-07 | Metronomen mixas i samma buffert som synten, med egna klicksampling. | Exakt fas mot musiken. | Metronom som MIDI-instrument eller separat spelare. | \[BESLUT\] |
| DR-08 | F1–F3 är en mängd valda stämmor. Mängden är aldrig tom. | En mekanism i stället för tre lägen. | Tre separata lägen. | \[BESLUT\] |
| DR-09 | Ändrat stämmaval under uppspelning behandlas som ett hopp till nuvarande position (flush + chase). | Bufferten är renderad i förväg. En mekanism för både hopp och valändring. | Tysta kanaler i den redan renderade bufferten. | \[BESLUT\] |
| DR-10 | Startmarkören är en `ScorePosition` och snappar till närmaste notstart i den tryckta takten. Vid upprepad del startar uppspelningen i första förekomsten i spelordningen. | Element-id är tvetydiga för ackord och flera röster. | Lagra element-id. | \[DEFAULT\] |
| DR-11 | Inräkning görs bara vid start från `Stopped` eller `Paused`, inte vid hopp under pågående uppspelning. | Ett hopp ska kännas omedelbart. | Alltid inräkning. | \[DEFAULT\] |
| DR-12 | Pekaren animeras i JS från en tidslinje som skickas en gång, korrigerad av ankarpunkter från C#. Pekaren rör sig i steg från not till not. Ingen tempofaktor (U2). | Minimal trafik över bryggan och enkel logik. | C# skickar position varje bildruta. Interpolation mellan noter. | \[BESLUT\] |
| DR-13 | Instrumentkanalbyte via text (arco, pizzicato, dämpare) ingår inte i v1. Standardkanalen används. | Begränsar omfånget. | Stöd i v1. | \[DEFAULT\] |
| DR-14 | Inga nätverksbehörigheter och ingen telemetri. | P1, U10, enklare butiksdeklaration. | Kraschrapportering över nätet. | \[BESLUT\] |
| DR-15 | Notbilden har löpande layout anpassad till skärmbredd. Följ-läge (automatisk scroll) pausas när användaren scrollar själv och återupptas vid Play eller tryck på följ-knappen. | Små skärmar, och användaren ska inte bli motarbetad. | Fast A4-sidbrytning. | \[DEFAULT\] |
| DR-16 | Reservplan om V10 inte håller: Verovio kompilerat som native bibliotek och anropat via P/Invoke, med egen visning. Kräver ny juridisk granskning (LGPL och iOS). | Bevarar Verovios layout utan WebView. | Egen layoutmotor. | \[BESLUT\] (aktiveras av M0) |
| DR-17 | ”MuseScore” är ett varumärke. Appens namn är ett arbetsnamn. Verovio (LGPL) och vald SoundFont granskas juridiskt före release. Klickljuden skapas av projektet självt (syntetiska) så att de inte bär någon tredjepartslicens. Appen visar de erkännanden och copyright-notiser som vald SoundFonts licens kräver (DR-35). | Undvika varumärkes- och licensproblem. | Ingen granskning. Färdiga klickljud från nätet. | \[BESLUT\] |
| DR-18 | Oregelbundna taktarter (5/8, 7/8) ger ett klick per åttondel med accent på första. | Enkelt och förutsägbart. | Gruppering som 3+2. | \[DEFAULT\] |
| DR-19 | Noter som började före startpunkten och fortfarande skulle klinga startas inte. | Enklare chase. | Återstarta hållna toner. | \[DEFAULT\] |
| DR-20 | Ingen lista över senast öppnade filer i v1. | Begränsar omfånget. | Lista i v1. | \[DEFAULT\] |
| DR-21 | Lägsta OS-versioner: Android API 26, iOS 15. | Rimlig täckning. Måste stämma med MAUI-kraven. | Nyare golv. | \[DEFAULT\] (V12) |
| DR-22 | Skärmen hålls tänd under uppspelning. | Notbilden ska vara synlig. | Låta skärmen släcka. | \[DEFAULT\] |
| DR-23 | Ljud: 44100 Hz, stereo, 32-bitars flyttal internt. | Stöds överallt, bra kvalitet. | 48000 Hz. | \[DEFAULT\] |
| DR-24 | Renderblock om 256 ramar (cirka 5,8 ms vid 44100 Hz) för schemaläggning av händelser. | Fin tidsupplösning utan hög CPU-last. | Större block. | \[DEFAULT\] |
| DR-25 | Appen sparar bara inställningar lokalt (metronom, inräkning, zoomnivå, följ-läge) i plattformens `Preferences`. Inga partitur eller deras innehåll sparas. | Användaren förväntar sig att växlarna är kvar. P1 hålls. | Spara partitursammanfattningar eller historik. | \[DEFAULT\] |
| DR-26 | Ljudutgången stängs efter 30 sekunders paus eller stopp och öppnas igen vid `Play`. | Sparar batteri och släpper ljudfokus. | Hålla ljudutgången öppen. | \[DEFAULT\] |
| DR-27 | Filen läses en gång till minnet (högst 50 MB, §12.1) och filhandtaget släpps direkt. På iOS hålls åtkomsten via security-scoped URL bara under läsningen. På Android används den URI som filväljaren eller ”Öppna med” ger. | Håller P1 och undviker kvarhängande filrättigheter. | Läsa strömmande från filen under hela sessionen. | \[DEFAULT\] |

### 6.1 Beslut tillagda efter genomgången av MuseScores källkod (v0.4)

| ID | Beslut | Skäl | Förkastat alternativ | Status |
| --- | --- | --- | --- | --- |
| DR-28 | Formatstöd: version 3.01 och senare (3.x, 4.x, 5.x) stöds fullt ut. Version 2.06 läses så gott det går. 1.x stöds inte. Parsern grenas per version bara där skillnader har visats. MuseScores läsargränser är under 400, 400–409, 410–459, 460–499 och 500 eller högre. | MuseScore har läsare för alla dessa (A5) och demo-filerna är till stor del 2.06 och 3.01. | Bara 4.x och senare. Fullt 2.x-stöd. | \[DEFAULT\] |
| DR-29 | Bara huvudpartituret tolkas. Filer under `Excerpts/` ignoreras, och valbara stämmor är de som finns i huvudpartituret. | Utdrag är separata partitur och skulle dubblera stämmorna (A4). | Visa utdrag som egna partitur. | \[BESLUT\] |
| DR-30 | Första `<Channel>` i varje instrument gäller. Namngivna kanaler (arco, pizzicato, tremolo, open, mute) används inte i v1. Bank är alltid 0 (melodiskt) eller 128 (slagverk). Filens controller 0 och 32 ignoreras. | Bankvärdena i filen pekar in i MuseScores egen ljudfont (till exempel controller 32 = 17 i `Dawn.mscx`) och finns inte i en vanlig GM-font. | Följa filens bankval. | \[BESLUT\] |
| DR-31 | Filens mixerinställningar och ljudval (`audiosettings.json`: volym, balans, mute, solo) ignoreras i v1. Alla stämmor spelas med förvalen för volym (CC 7 = 100) och panorering (CC 10 = 64). | U5 och U8. F1 ska spela alla stämmor. Ljudbalansen kan avvika från författarens. | Läsa och tillämpa filens mixer. | \[DEFAULT\] |
| DR-32 | Velocity tas från närmaste föregående `<Dynamic>` i samma notsystem. Tabellen `DynamicVelocity` (A12) är reserv när `<velocity>` saknas. Utan dynamikmärke används mf (80). | Filen bär själva värdet (A12). | Enbart tabell. | \[DEFAULT\] |
| DR-33 | Taktupprepning utvecklas vid uppspelning: en takt med taktupprepning spelar om det angivna antalet föregående takter. | MuseScore har funktionen (A15) och annars blir takterna tysta. | Spela tystnad. | \[DEFAULT\] (V14) |
| DR-34 | Okänd filversion eller okänt element stoppar aldrig laddningen (P5). En version nyare än 5.00 ger varningen `UNSUPPORTED_VERSION`. | Huvudgrenen är 5.0 under utveckling (A5). | Avvisa nyare filer. | \[BESLUT\] |
| DR-35 | Appen visar en licenssida med de erkännanden och copyright-notiser som vald SoundFonts licens kräver. | MIT-licenserna för FluidR3-härledda fonter kräver det (A19). | Ingen licenssida. | \[BESLUT\] |

## 7. Arkitektur

### 7.1 Komponenter och dataflöde

```
.mscz ─► MsczReader ─► MscxParser ─► Score (modell, ticks)
         (zip i minnet)  (XmlReader)        │
          ┌──────────────────────────────────┼───────────────────────────┐
          ▼                                  ▼                           ▼
     MeiExporter                   TempoMap + PlayOrderBuilder     EventGenerator
     (xml:id = Note.Id)                      │                  (valda stämmor)
          │                                  ▼                           │
          ▼                              PlayOrder                       ▼
  WebView: Verovio ─► SVG                    │                      Sequencer (audio worker)
          │  ▲                               │                           │
  geometri│  │tryck (Note.Id,                │                    Synth + Metronom
          ▼  │ ScorePosition)                │                           │
   CursorTimelineBuilder ◄───────────────────┘                           ▼
          │                                                         RingBuffer (epok)
          ▼                                                              │
  JS-pekare (requestAnimationFrame)                                      ▼
          ▲                                                       IAudioOutput ─► ljudkort
          └───── setClock ◄── PlaybackEngine ◄── FramesInCurrentEpoch ◄─┘
```

### 7.2 Beroenderegler

Riktning: `App` → `App.Core` → `Audio` → `Core`. Ingen pil går åt andra hållet. (Ändrat efter v0.4: `ScoreLite.App.Core` tillagt, decisions #21. Reglerna ARCH-2.x i `docs/architecture.md` gäller.)

| Projekt | Får referera till | Får INTE referera till |
| --- | --- | --- |
| `ScoreLite.Core` | Bara .NET-basbiblioteket (BCL). | MAUI, plattforms-API:er, WebView, `ScoreLite.Audio`, MeltySynth. |
| `ScoreLite.Audio` | `ScoreLite.Core`, MeltySynth, BCL. | MAUI, plattforms-API:er, WebView. |
| `ScoreLite.App.Core` | `ScoreLite.Core`, `ScoreLite.Audio`, CommunityToolkit.Mvvm, BCL. | MAUI, plattforms-API:er, WebView. Målar net8.0 så att ViewModels och bryggan kan testas utan MAUI. |
| `ScoreLite.App` | `ScoreLite.App.Core`, `ScoreLite.Core`, `ScoreLite.Audio`, MAUI. | Inget förbjudet. All plattformskod, XAML och all JS/Verovio-kod bor här. |
| `*.Tests` | Projektet de testar. | MAUI. |

### 7.3 Repostruktur

Filnamn och mappar är normativa. Lägg inte kod på andra ställen. (Uppdaterad 2026-10-26 efter hur koden faktiskt är organiserad.)

```
/src
  ScoreLite.Core/
    Mscz/MsczReader.cs            # zip i minnet, gränser (S1, S2)
    Mscx/MscxParser.cs            # tolerant, strömmande (AL-01)
    Model/Model.cs                # Score, Part, Staff, Note, Measure, NotatedItem, ScorePosition, InstrumentSpan, Diagnostic, LoadResult, IScoreLoader
    ScoreLoader.cs                # IScoreLoader (IF-2)
    Time.cs                       # ticks, frames, ms (AL-02)
    Timing/TempoMap.cs
    Playback/PlayOrder.cs         # PlayOrder och PlayOrderBuilder
    Playback/EventGenerator.cs    # EventGenerator, PlaybackEvent, DynamicVelocity
    Playback/ChannelAllocator.cs
    Notation/MeiExporter.cs, CursorTimelineBuilder.cs
  ScoreLite.Audio/
    IAudioOutput.cs, OfflineAudioOutput.cs, RingBuffer.cs,
    SynthHost.cs, Metronome.cs, Sequencer.cs, PlaybackEngine.cs
    MeltySynth/                   # vendrad, oförändrad (decisions #9)
  ScoreLite.App.Core/             # net8.0, utan MAUI (decisions #21)
    Bridge/                       # IScoreBridge, JsonScoreBridge, BridgeMessages, BridgeJson
    Services/Services.cs          # IScoreFilePicker, IKeepAwake, ISettings, IDialogService, IAppNavigation, IAudioInterruptions
    ViewModels/                   # ScoreViewModel, StartViewModel, ErrorMessages
    BridgeDelayEstimator.cs, NullPlaybackEngine.cs
  ScoreLite.App/                  # MAUI
    Views/                        # StartPage, ScorePage, LicensesPage
    Services/                     # AppServices.cs, PlatformAudio.cs, HybridWebViewScoreBridge.cs
    Platforms/Android/            # AndroidAudioOutput, AndroidAudioSession, MainActivity
    Platforms/iOS/                # IosAudioOutput (+ IosAudioSession), AppDelegate
    Resources/Raw/wwwroot/        # index.html, style.css, bridge.js, bridge-core.js, verovio/
    Resources/Raw/soundfont.sf2   # hämtas av tools/fetch-soundfont.sh, checkas inte in (DR-05, decisions #17)
/tests
  ScoreLite.Core.Tests/  ScoreLite.Audio.Tests/  ScoreLite.App.Tests/  web/  verovio-t6/
/corpus                           # 8 egna MuseScore 4-filer
/docs
  architecture.md   Designdokument-v0.4.md   decisions.md   device-checklist.md
  verification-status.md   dev-environment.md   format-notes.md   legal-review.md
```

Metronomens klick är syntetiska (`Metronome.cs`, DR-17). `click_accent.wav` och `click.wav` används inte.

### 7.4 Trådar och klockdomäner

| Tråd | Äger | FÅR | FÅR INTE |
| --- | --- | --- | --- |
| UI | Vyer, ViewModels, WebView-meddelanden. | Skicka kommandon till `IPlaybackEngine` utan att vänta. | Parsa, rendera ljud, vänta på ljudtråden. |
| Laddning (`Task.Run`) | `MsczReader`, `MscxParser`, `MeiExporter`, `PlayOrderBuilder`, `EventGenerator`. | Köra så länge det behövs, med `CancellationToken`. | Röra ljudobjekt. |
| Audio worker | `Sequencer`, `SynthHost`, `Metronome`, skrivsidan av `RingBuffer`. | Använda förallokerade buffertar. | Ta lås som UI-tråden håller, göra I/O, allokera i renderloopen. |
| Driver-callback | Läsa `RingBuffer` och räkna `FramesConsumed`. | Bara kopiera ramar och uppdatera räknarna. | Allokera, ta lås, anropa annan managed kod. |

Kommunikation mellan trådar:

- UI → audio worker: en kommandokö (`Play`, `Pause`, `Stop`, `Seek`, `SetSelection`, `SetMetronome`, `SetCountIn`). Kommandon utförs i den ordning de kom.
- Audio worker → driver: lås-fri enkelskrivare/enkelläsare-ringbuffert (`RingBuffer`). Varje block är märkt med epok.
- Audio worker → UI: `PlaybackSnapshot` vid varje tillståndsändring och därutöver var 500:e ms under uppspelning. Levereras på UI-tråden.

Tre tidsdomäner finns. Alla omvandlingar går via `TempoMap` och `Time` (AL-02). Ingen annan kod får räkna om mellan dem.

| Domän | Enhet | Används av | Omvandling |
| --- | --- | --- | --- |
| Musikalisk | `PlayTick` (`long`) | Modell, `PlayOrder`, `EventGenerator` | `TempoMap.PlayTickToFrame(tick)` |
| Ljud | `frame` (`long`, absolut i spelordningen) | `Sequencer`, `RingBuffer`, `IAudioOutput` | `frame * 1000.0 / SampleRate` ger `PlayMs` |
| Visning | `ms` (`double`, JS `performance.now()`) | JS-pekaren | Via `setClock` (AL-12) |

Nuvarande ljudposition (normativt):

```
playedFrames  = max(0, ring.FramesInCurrentEpoch - OutputLatencyFrames)
currentFrame  = epochStartFrame + playedFrames
```

`FramesInCurrentEpoch` är antalet ramar drivrutinen har läst ur den nuvarande epokens block (IF-4). Räknaren nollställs när första blocket av en ny epok läses.

### 7.5 Dataflöde per händelse

1. **Öppna fil:** UI tar en `Stream` → `IScoreLoader.LoadAsync` (laddningstråd) → `Score` + `Diagnostic`-lista → `PlayOrder`, händelser och MEI byggs → MEI skickas till WebView (`loadScore`).
2. **Layout klar:** JS rapporterar geometri (`layoutReady`) → `CursorTimelineBuilder` bygger tidslinjen → skickas till JS (`setTimeline`).
3. **Tryck på notbild:** JS skickar `tap` → ViewModel mappar `Note.Id` eller takt till `ScorePosition` → `IPlaybackEngine.SeekTo` → startmarkören flyttas och `setMarker` skickas till JS.
4. **Play:** `Play()` → om `CountInEnabled` och tillståndet var `Stopped` eller `Paused`: inräkning (AL-10) → sedan musik. Varje tillståndsändring ger ett `PlaybackSnapshot` → ViewModel skickar `setClock` till JS.
5. **Ändra stämmaval:** `SetSelectedParts` → behandlas som hopp till nuvarande position (DR-09, AL-07).
6. **Rotation eller zoom:** JS renderar om → ny `layoutReady` → ny tidslinje → ny `setTimeline`. Uppspelningen påverkas inte.

## 8. Kontrakt (gränssnitt och meddelanden)

Namn, signaturer och fält i det här avsnittet är normativa (IF-1–IF-5). Implementatören FÅR lägga till privata medlemmar men SKA INTE ändra publika signaturer utan att uppdatera dokumentet.

### 8.1 IF-1: Modell (`ScoreLite.Core.Model`)

```csharp
public static class Time { public const int TicksPerQuarter = 480; }   // V2

public readonly record struct ScorePosition(int MeasureIndex, int TickInMeasure);

public sealed record Score(
    string Title,
    IReadOnlyList<Part> Parts,
    IReadOnlyList<Measure> Measures,   // index = MeasureIndex, i skriven ordning
    IReadOnlyList<Note> Notes,         // sorterade på ScoreTick, sedan PartId, sedan Voice
    PlayOrder Order,
    TempoMap Tempo);                   // uttryckt i PlayTick, byggd efter PlayOrder

public sealed record Part(string Id, string Name, string ShortName,
    IReadOnlyList<Staff> Staves, IReadOnlyList<InstrumentSpan> Instruments);

public sealed record Staff(int Index, ClefKind Clef, StaffKind Kind);   // Kind: Standard, Percussion, Tab

public sealed record InstrumentSpan(int FromScoreTick, string InstrumentId, int Program, int Bank,
    bool IsPercussion, int TransposeSemitones, Drumset? Drums);

public sealed record Note(string Id, string PartId, int StaffIndex, int Voice,
    int ScoreTick, int DurationTicks, int MidiPitch, bool TiedToNext);

public sealed record Measure(int Index, int StartScoreTick, int ActualLengthTicks,
    TimeSig NominalSig, KeySig Key,
    int RepeatPreviousMeasures);           // ActualLength < Nominal för upptakt

public sealed record Diagnostic(DiagSeverity Severity, string Code, string Message, string? Where);
```

Regler:

- `Part.Id` är `p` + stämmans 0-baserade ordning i filen (till exempel `p0`, `p1`). `Staff.Index` är 0-baserat inom stämman.
- `Note.Id` är stabil för en given fil och en given programversion, och används som `xml:id` i MEI. Format: `n` + löpnummer i `Notes`-ordningen (till exempel `n0`, `n1`).
- Takter får `xml:id` enligt `m` + `MeasureIndex` (till exempel `m12`).
- Alla tider i modellen är `int` ticks utom `PlayTick` som är `long`.

* `InstrumentSpan.Bank` är alltid 0 (melodiskt) eller 128 (slagverk), och `Program` kommer från instrumentets första `<Channel>` (DR-30).
* `Measure.RepeatPreviousMeasures` är 0 för en vanlig takt och 1, 2 eller 4 för en taktupprepning (DR-33).

### 8.2 IF-2: Laddning

```csharp
public interface IScoreLoader
{
    Task<LoadResult> LoadAsync(Stream mscz, string? fileNameHint, CancellationToken ct);   // .mscz eller okomprimerad .mscx
}

public sealed record LoadResult(Score? Score, IReadOnlyList<Diagnostic> Diagnostics);
// Score == null betyder ett fatalt fel. Då innehåller Diagnostics minst ett fel med kod från §12.
```

### 8.3 IF-3: Uppspelning

Alla metoder och egenskaper anropas från UI-tråden och returnerar direkt. Arbetet sker på audio worker (§7.4).

```csharp
public enum PlaybackState { Unloaded, Stopped, CountingIn, Playing, Paused }

public sealed record PlaybackSnapshot(
    PlaybackState State,
    int Epoch,
    double PlayMs,              // absolut tid i spelordningen, redan korrigerad för utgångslatens
    ScorePosition Marker);

public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackState State { get; }
    void Load(Score score);                                   // Unloaded/Stopped -> Stopped
    void Play();
    void Pause();
    void Stop();                                              // positionen återgår till Marker
    void SeekTo(ScorePosition position);                      // sätter alltid Marker. Under Playing: hopp (AL-07)
    void SetSelectedParts(IReadOnlySet<string> partIds);      // SKA INTE vara tom
    bool MetronomeEnabled { get; set; }
    bool CountInEnabled { get; set; }
    event Action<PlaybackSnapshot> SnapshotChanged;           // på UI-tråden
}
```

### 8.4 IF-4: Ljudutgång och buffert

```csharp
public interface IAudioOutput : IDisposable
{
    int SampleRate { get; }               // 44100 (DR-23)
    int OutputLatencyFrames { get; }      // rapporterad eller uppmätt
    long FramesConsumed { get; }          // monoton. Uppdateras av driver-callbacken
    void Start(RingBuffer source);
    void Stop();
}

public sealed class RingBuffer             // en skrivare, en läsare, lås-fri
{
    public RingBuffer(int capacityFrames, int channels);   // 200 ms (DR-06)
    public int WritableFrames { get; }
    public bool TryWrite(ReadOnlySpan<float> interleaved, int epoch);   // audio worker
    public int Read(Span<float> dest, out int epoch);                   // driver. Fyller med tystnad vid underskott och räknar upp Underruns
    public void Flush(int newEpoch);                                    // kasta allt buffrat. Läsaren hoppar block med äldre epok
    public int CurrentEpoch { get; }               // epoken för det senast lästa blocket
    public long FramesInCurrentEpoch { get; }      // lästa ramar sedan nuvarande epok började. Nollställs vid epokbyte
    public long Underruns { get; }
}
```

`OfflineAudioOutput` implementerar `IAudioOutput` utan ljudkort och drar block i egen takt. Den används i tester och SKA ge bit-identiskt resultat för samma indata (P7).

### 8.5 IF-5: Brygga mellan C# och JavaScript

Transport: C# → JS anropar en enda funktion, `window.bridge.receive(jsonString)`. JS → C# använder plattformens meddelandemekanism (V11) och skickar en JSON-sträng. Varje meddelande har fälten `v` (protokollversion, nu `1`) och `t` (typ). Okända `t` ignoreras och loggas. All data serialiseras som JSON och SKA INTE byggas ihop som kodsträngar (S4).

`PlayMs` är tid i millisekunder från början av spelordningen. Koordinater är CSS-pixlar relativt övre vänstra hörnet av notbilden.

| Riktning | `t` | Fält |
| --- | --- | --- |
| C# → JS | `loadScore` | `requestId` (int), `mei` (string), `viewportWidthPx` (int), `zoomLevel` (0–4), `theme` (`light` eller `dark`) |
| C# → JS | `setTimeline` | `layoutGen` (int), `entries`: lista av `[playMs, x, sys]` sorterad på `playMs` |
| C# → JS | `setClock` | `epoch` (int), `state` (`stopped`, `countingIn`, `playing`, `paused`), `playMs` (double), `bridgeDelayMs` (double) |
| C# → JS | `setMarker` | `anchorId` (string: `n..` eller `m..`), `measure` (int), `tickInMeasure` (int) |
| C# → JS | `setFollow` | `enabled` (bool) |
| C# → JS | `setZoom` | `zoomLevel` (0–4) |
| C# → JS | `ping` | `id` (int) |
| JS → C# | `ready` | `verovioVersion` (string) |
| JS → C# | `layoutReady` | `requestId`, `layoutGen` (int), `systems`: lista av `{sys, y, h}`, `items`: lista av `[id, measure, x, sys]` för varje not och takt |
| JS → C# | `tap` | `id` (string eller null), `measure` (int), `x` (double) |
| JS → C# | `followChanged` | `enabled` (bool). Skickas när användaren scrollar själv |
| JS → C# | `pong` | `id` (int) |
| JS → C# | `renderError` | `code` (string), `message` (string) |

Exempel:

```json
{"v":1,"t":"setClock","epoch":3,"state":"playing","playMs":12043.5,"bridgeDelayMs":9.0}
{"v":1,"t":"tap","id":"n418","measure":27,"x":512.0}
```

## 9. Algoritmer och beteenden

Varje algoritm har ett ID, indata, utdata och regler. Följ reglerna i den ordning de står. Punkter som beror på ett V-ID får inte behandlas som fakta förrän V-ID:t är stängt i `docs/format-notes.md`.

### AL-01: Läsa och tolka filen

Indata: `Stream`. Utdata: `LoadResult` (IF-2).

1. `MsczReader` öppnar en `ZipArchive` (läsläge) direkt på strömmen i minnet. Välj huvudfilen i denna ordning: (a) `rootfile full-path` i `META-INF/container.xml` om den finns och posten finns, (b) `<fileNameHint utan ändelse>.mscx`, (c) `score.mscx`, (d) första `.mscx` i roten (inte i undermapp). Poster under `Excerpts/` ignoreras (DR-29). Tillämpa gränserna i §12. Packa aldrig upp till disk (S1). Bilder, miniatyr, stilfil, ackordlista och JSON-filer ignoreras i v1.
2. Om indata inte är ett ZIP-arkiv men börjar som XML: tolka som okomprimerad `.mscx` (U9).
3. `MscxParser` använder `XmlReader` med `DtdProcessing.Prohibit` och `XmlResolver = null` (S3) och läser strömmande. Läs rotens `version`-attribut och logga det. Grena på version där DR-28 kräver det.
4. Läs per `<Part>`: `<trackName>`, notsystemdefinitionerna (`<Staff id>` med `<StaffType group>` pitched, percussion eller tablature, samt klav) och `<Instrument>` enligt A11. Instrumentets första `<Channel>` ger program (DR-30). Läs sedan `<Staff id="N">`-blocken: varje `<voice>` i en takt är en röst, där ordningen ger röstnumret och tomma `<voice/>` räknas. Läs `Chord`, `Rest`, `Note` (`pitch`, `tpc`, `tpc2`), `Tuplet` och `endTuplet`, bindebågar, `Dynamic`, `Tempo`, `TimeSig`, `KeySig`, `startRepeat`, `endRepeat`, `Volta`, `Jump` och `Marker`.
5. Beräkna tickposition per röst: tickräknaren börjar vid taktens början. Varje `Chord` eller `Rest` flyttar den med sin varaktighet (`durationType`, punkter och tuplet-skalning). `<location><fractions>` flyttar den relativt (A9). Taktens faktiska längd är `len` om det finns, annars taktartens. Fördela en tuplets totala ticks över dess noter så att summan blir exakt, med heltalsaritmetik. Vid avrundning läggs `Diagnostic` med kod `TICK_ROUNDING` (varning).
6. Tonhöjd: `<pitch>` är klingande höjd (`MidiPitch`). Skriven stavning för transponerande instrument ges av `<tpc2>` och instrumentets transponering. Notbilden (MEI) använder skriven höjd.
7. Tolerans: okänt element hoppas över med hela sitt delträd (räkna antalet och lägg en sammanfattande `Diagnostic` `UNKNOWN_ELEMENTS`). Saknat värde ger varning och ett \[DEFAULT\]. Fel är fatalt (`Score = null`) bara om inga takter eller inga stämmor hittas.
8. Resultatet SKA vara deterministiskt (P7): sortera `Notes` på `ScoreTick`, sedan `PartId`, sedan `Voice`.

### AL-02: Tid, tempo och omvandling

- `TempoMap` är uttryckt i `PlayTick` och byggs efter `PlayOrder`. Den består av en sorterad lista `(PlayTick, quarterNotesPerSecond)`. Saknas tempo används 2.0 (120 BPM) \[DEFAULT\]. Enheten är kvartsnoter per sekund (V3).
- `PlayTickToFrame(t)` räknar `FrameAtSegmentStart + round((t - segmentStartTick) * SampleRate / (480 * qps))`. `FrameAtSegmentStart` förberäknas för varje tempoändring med en enda avrundning per segment. Funktionen SKA vara monoton och använda binärsökning.
- `FrameToPlayTick(f)` är inversen.
- Gradvisa tempoändringar (ritardando, accelerando) ingår i trohetsnivå 2 (AL-04). Tills dess gäller senast kända tempo.

### AL-03: Spelordning (repriser, voltor, hopp)

Indata: `Measures` med repris-, volta- och hoppinformation (V7). Utdata: `PlayOrder`.

```csharp
public sealed record PlaySegment(int MeasureIndex, long PlayStartTick, int LengthTicks);

public sealed class PlayOrder
{
    public IReadOnlyList<PlaySegment> Segments { get; }
    public long TotalTicks { get; }
    public ScorePosition ToScorePosition(long playTick);              // binärsökning
    public long FirstPlayTick(ScorePosition position);                // första förekomsten (DR-10)
    public IReadOnlyList<long> AllPlayTicks(ScorePosition position);  // alla förekomster
}
```

Regler:

1. Grundregel: spela takt 0 till N−1 i ordning.
2. `<startRepeat/>` sätter reprisstart (förvalt takt 0). `<endRepeat>N</endRepeat>` betyder att avsnittet spelas N gånger totalt (förvalt 2 om värdet saknas).
3. Volta: en `<Volta>` täcker så många takter som `<next><location><measures>` anger och gäller för de genomspelningar som `<endings>` listar. Under genomspelning p (1-baserad) spelas bara de voltor som listar p. Överlappande eller öppna voltor hanteras inte i v1 (A14) och ger `PLAYORDER_FALLBACK`.
4. Hopp: `<Jump>` utlöses efter att dess takt spelats. Gå till markören vars `<label>` är `jumpTo` (`start` är takt 0). Spela tills `playUntil` nås (`end` är partiturets slut, `fine` är takten med Fine-markören, där uppspelningen slutar, och `coda` är takten med ”To Coda”-markören, där uppspelningen fortsätter vid markören med etiketten `continueAt`). Varje hopp används högst en gång. Efter ett hopp tas inga repriser eftersom `playRepeats` är av som standard.
5. Taktupprepning: en takt med taktupprepning spelar om de `RepeatPreviousMeasures` föregående takterna (DR-33).
6. Säkerhetsgräns: högst `20 * N + 1000` segment. Överskrids den avbryts byggandet, spelordningen byggs om som rakt igenom och `Diagnostic` `PLAYORDER_LIMIT` läggs till.
7. Tvetydig eller inkonsekvent struktur ger rakt igenom och `Diagnostic` `PLAYORDER_FALLBACK`.
8. Resultatet SKA stämma med MuseScores egen MIDI-export för korpusfilerna (§14). Tolkningen av `playUntil` och `continueAt` i regel 4 följer MuseScores jumptabell (A13) men ska bekräftas av T2.

### AL-04: Händelser från modell och spelordning

Indata: `Score`, `SelectedPartIds`. Utdata: sorterad `PlaybackEvent[]`.

```csharp
public enum PlaybackEventKind : byte { ProgramChange = 0, Controller = 1, NoteOff = 2, NoteOn = 3 }
public readonly record struct PlaybackEvent(long PlayTick, PlaybackEventKind Kind, string PartId, int Data1, int Data2);
// ProgramChange: Data1=program, Data2=bank. Controller: Data1=nummer, Data2=värde. Note*: Data1=MIDI-höjd, Data2=velocity.
```

Regler:

1. Generera bara händelser för valda stämmor.
2. För varje segment i spelordningen och varje not i segmentets takt: `onTick = segment.PlayStartTick + (note.ScoreTick - measure.StartScoreTick)`. Slut: `onTick + DurationTicks`.
3. Bindebågar: slå ihop en not med nästa bara när nästa not ligger i samma segment eller i det omedelbart följande segmentet. Annars slutar noten vid segmentets slut.
4. Klingande höjd (`MidiPitch`) används, begränsad till 0–127. Slagverk använder trumsetets tonhöjd.
5. Velocity: använd `<velocity>` i närmaste föregående `<Dynamic>` i samma notsystem (A12, DR-32). Saknas värdet används `DynamicVelocity`. Utan dynamikmärke gäller mf (80).
6. Programbyte skickas i början av spelordningen för instrumentets första `<Channel>` och vid varje instrumentbyte (varje förekomst). Bank är alltid 0 eller 128, och controller 0 och 32 i filen ignoreras (DR-30). Startvärden: volym (CC 7) 100 och panorering (CC 10) 64 (DR-31).
7. Sortering: `PlayTick` stigande, sedan `Kind` (`ProgramChange`, `Controller`, `NoteOff`, `NoteOn`), sedan `PartId`, sedan höjd. Sorteringen SKA vara stabil.
8. Trohetsnivåer:
   - Nivå 1 (v1): höjd, längd, bindebågar, trioler, pauser, tempo och stegvisa tempoändringar, repriser och hopp, taktupprepning, statisk dynamik, transponerande instrument.
   - Nivå 2 (v1.1): hairpins (`veloChange`), artikulationernas `velocity` och `gateTime` ur instrumentet (staccato och legato), förslagsnoter, tremolo, gradvisa tempoändringar.
   - Nivå 3 (senare): triller och ornament, arpeggio, kanalbyte via text, swing.

### AL-05: Kanaltilldelning och synt-instanser

Kanaltilldelningen SKA vara stabil och oberoende av vilka stämmor som är valda.

1. Gå igenom stämmorna i filens ordning. Varje stämma som inte är slagverk får nästa lediga MIDI-kanal 0–15 utom 9 i synt-instans `s`. Är alla lediga kanaler i instans `s` upptagna går tilldelningen vidare till instans `s + 1`.
2. Alla slagverksstämmor delar kanal 9 i synt-instans 0 \[DEFAULT\]. Olika trumkit i samma stycke stöds inte i v1.
3. En stämma med instrumentbyte behåller sin kanal. Bytet görs med programbyte (AL-04 regel 6).
4. `SynthHost` skapar en MeltySynth-`Synthesizer` per instans, alla med samma delade `SoundFont`-objekt (V9), renderar dem till flyttalsbuffertar och summerar. Summan SKA begränsas så att den inte klipper (mjuk begränsare). Nivå och kurva väljs vid lyssningstest i M2.

### AL-06: Renderloop i audio worker

Alla positioner är absoluta ramar i spelordningen (`long`, kan vara negativa under inräkning). `BlockFrames` är 256 (DR-24).

```
loop medan motorn lever:
    töm kommandokön och utför kommandona i ordning        // AL-07, AL-08
    om state är Playing eller CountingIn:
        medan ring.WritableFrames >= BlockFrames:
            blockStart = nextFrame
            blockEnd   = nextFrame + BlockFrames
            för varje händelse e med PlayTickToFrame(e.PlayTick) i [blockStart, blockEnd):
                rendera synten fram till (frame(e) - blockStart), tillämpa e, fortsätt
            rendera resten av blocket
            för varje metronomklick i [blockStart, blockEnd): mixa klicket med rätt förskjutning
            ring.TryWrite(block, currentEpoch)
            nextFrame = blockEnd
            om nextFrame > sistaHändelseFrame: gå till slutfas
    annars: vänta på kommandosignal (högst 2 ms)
```

Regler:

- Buffertar förallokeras vid start. Ingen allokering i loopen (§7.4).
- Slutfas: rendera synt-utsvängning tills utsignalen är tyst eller högst 2 sekunder, sätt sedan tillståndet till `Stopped` med positionen vid `Marker`.
- Audio worker kör med förhöjd trådprioritet där plattformen tillåter det.
- Efter 30 sekunder i `Stopped` eller `Paused` stängs ljudutgången och öppnas igen vid nästa `Play` (DR-26).

### AL-07: Hopp, valändring, stopp och uppspelningsstart (epokprotokollet)

Används av `SeekTo` under uppspelning, av `SetSelectedParts` under uppspelning (DR-09), av `Stop` och av `Play`.

1. Bestäm `startTick`: vid hopp `PlayOrder.FirstPlayTick(position)`. Vid valändring under uppspelning nuvarande `PlayTick`. Vid `Play` från paus nuvarande position. Vid `Play` från stopp `FirstPlayTick(Marker)`.
2. `epoch = epoch + 1`, `ring.Flush(epoch)`.
3. Stäng alla toner och återställ controllers i alla synt-instanser. Nollställ metronomen.
4. Hämta händelser för valda stämmor (cacha per val).
5. **Chase:** för varje kanal, tillämpa den senaste `ProgramChange` och den senaste `Controller`-händelsen per controller-nummer med `PlayTick < startTick`, i ordning, före första blocket. `NoteOn` och `NoteOff` tillämpas inte (DR-19).
6. `nextFrame = PlayTickToFrame(startTick) - countInFrames` (`countInFrames` är 0 om ingen inräkning). `epochStartFrame = nextFrame`.
7. Skicka `PlaybackSnapshot` med den nya epoken **först när** `ring.CurrentEpoch == epoch`, det vill säga när drivrutinen har börjat läsa den nya epokens ljud. Då hör användaren ljudet och pekaren startar samtidigt.

### AL-08: Tillståndsmaskin

| Tillstånd | Händelse | Nytt tillstånd | Åtgärd |
| --- | --- | --- | --- |
| `Unloaded` | `Load` | `Stopped` | `Marker = (0,0)`, alla stämmor valda, `epoch++`. |
| `Stopped`, `Paused` | `Load` | `Stopped` | Som ovan. |
| `Stopped` | `Play` | `CountingIn` om inräkning är på, annars `Playing` | AL-07 från `Marker`. |
| `Paused` | `Play` | `CountingIn` om inräkning är på, annars `Playing` | AL-07 från pausad position. `Marker` oförändrad. |
| `Playing`, `CountingIn` | `Play` | oförändrat | Ignoreras. |
| `Playing` | `Pause` | `Paused` | 10 ms utfasning, stäng alla toner, behåll position. |
| `CountingIn` | `Pause` | `Paused` | Kasta inräkningen. Positionen blir `Marker`. |
| `Playing`, `CountingIn`, `Paused` | `Stop` | `Stopped` | `Flush`, stäng alla toner, position = `Marker`. |
| `Stopped` | `SeekTo(p)` | `Stopped` | `Marker = p`. Skicka `setMarker`. |
| `Paused` | `SeekTo(p)` | `Paused` | `Marker = p` och pausad position = `p`. |
| `Playing` | `SeekTo(p)` | `Playing` | `Marker = p`, AL-07 utan inräkning (DR-11). |
| `CountingIn` | `SeekTo(p)` | `CountingIn` | `Marker = p`, AL-07 med ny inräkning. |
| `Stopped`, `Paused` | `SetSelectedParts` | oförändrat | Spara valet. Händelser byggs om vid nästa AL-07. |
| `Playing` | `SetSelectedParts` | `Playing` | Spara valet, AL-07 från nuvarande position utan inräkning. |
| `CountingIn` | `SetSelectedParts` | `CountingIn` | Spara valet, AL-07 med samma start och inräkning. |
| `Playing` | slut på stycket | `Stopped` | Position = `Marker`. |
| `Playing`, `CountingIn` | OS-avbrott eller utdragna hörlurar | `Paused` | Som `Pause` (R4). |
| valfritt | `MetronomeEnabled` ändras | oförändrat | Gäller från nästa block. |
| valfritt | `CountInEnabled` ändras | oförändrat | Gäller vid nästa `Play`. |
| valfritt | `DisposeAsync` | `Unloaded` | Frigör ljud och trådar. |

### AL-09: Metronomens taktslagsgrid

1. Räknesätt per takt utifrån `NominalSig` (`num/den`): om `den == 8` och `num` är 6, 9 eller 12 gäller sammansatt taktart. Ett slag är då tre åttondelar (punkterad kvart) och antalet slag är `num / 3`. I alla andra fall gäller ett slag per notvärde `1/den` och antalet slag är `num`. Det ger ett klick per åttondel i 5/8 och 7/8 (DR-18).
2. Gridens ursprung för en takt är `segment.PlayStartTick`. För en upptakt (`ActualLengthTicks` mindre än nominell längd) är ursprunget `segmentslut - nominell längd`, och bara slag som ligger inom segmentet ger klick.
3. Slag 1 i takten får accentljud (`click_accent.wav`). Övriga slag får `click.wav`. Accent faller bort när slag 1 ligger före upptaktens början.
4. Varje klick placeras på `PlayTickToFrame(slagets tick)` och mixas in med exakt rampförskjutning (AL-06).
5. Griden bygger på absoluta ticks, så fasen ändras inte när metronomen slås av och på under uppspelning.
6. Klickljuden är mono, 44100 Hz, högst 20 ms, med fast förstärkning. De genereras av projektet självt (till exempel en kort dämpad sinuston) och har ingen tredjepartslicens (DR-17).

### AL-10: Inräkning

1. Utlöses av `Play` från `Stopped` eller `Paused` när `CountInEnabled` är sant (DR-11).
2. Använd taktarten (nominell) och tempot vid startpositionen. Antal klick `n` följer AL-09 regel 1. Slaglängd i ramar = `PlayTickToFrame(startTick + slagTicks) - PlayTickToFrame(startTick)`.
3. Klick läggs på `startFrame - (n - k) * slagRamar` för `k = 0..n-1`. Första klicket har accent. Inräkningen använder alltid klickljuden, oberoende av `MetronomeEnabled`.
4. Även om startpositionen är mitt i en takt spelas en hel takts klick.
5. Tillståndet är `CountingIn` tills `currentFrame >= startFrame`, därefter `Playing`.
6. Pekaren står på `Marker` under inräkningen.

### AL-11: Bygga pekarens tidslinje (`CursorTimelineBuilder`)

Indata: `items` och `systems` från `layoutReady`, `Score`, `PlayOrder`, `TempoMap`. Utdata: `entries` enligt IF-5 (`[playMs, x, sys]`).

1. Slå upp `x` och `sys` för varje not-id och takt-id i `items`.
2. För varje segment i spelordningen: lägg en post vid takstart (`x` = takts id) och en post för varje särskild tickposition där någon not i någon stämma börjar (`x` = minsta `x` bland noterna vid den tickpositionen). `playMs = PlayTickToFrame(tick) * 1000 / SampleRate`.
3. Tidslinjen använder **alla** stämmors noter, oberoende av stämmaval, så att pekarens rörelse inte ändras när valet ändras.
4. Saknas geometri för ett id hoppas posten över och en `Diagnostic` `CURSOR_MISSING_GEOMETRY` (information) räknas upp. Saknas geometri för hela takten återanvänds föregående post.
5. Sortera på `playMs`. Upprepade segment ger upprepade poster med senare `playMs`.
6. Bygg om vid varje `layoutReady` (ny `layoutGen`). Pekaren rör sig i steg från not till not, som i MuseScore, utan interpolation.

### AL-12: Pekare och följ-läge i JS (`bridge.js`)

1. Vid `setClock`: spara `anchor = { playMs, localAt: performance.now(), state, bridgeDelayMs }`.
2. Under `requestAnimationFrame` när `state == playing`: `t = anchor.playMs + (performance.now() - anchor.localAt) + anchor.bridgeDelayMs`. Hitta sista posten med `playMs <= t` (binärsökning) och placera pekaren på `(x, system y, system h)`. Ge pekaren en kort CSS-övergång (högst 80 ms).
3. I tillstånden `stopped`, `countingIn` och `paused` ritas ingen rörlig pekare. Startmarkören visas där `setMarker` sa. Vid `paused` placeras pekaren på posten för `anchor.playMs`.
4. C# skickar `setClock` vid varje tillståndsändring och var 500:e ms under `Playing`. Det håller avvikelsen inom F7.
5. `bridgeDelayMs` mäts av C# med `ping`/`pong` (halva rundturstiden) efter laddning och sedan var 10:e sekund.
6. Följ-läge: när pekaren byter system och följ-läge är på, scrolla mjukt (högst 200 ms) så att systemet hamnar cirka 30 % ned från vyns topp \[DEFAULT\]. Detekteras en egen scroll av användaren stängs följ-läget av och `followChanged { enabled: false }` skickas. Det slås på igen med `setFollow` (vid `Play` eller tryck på följ-knappen).
7. Utbytt tidslinje under uppspelning: fortsätt från samma `t`.

### AL-13: Tryck i notbilden

1. JS tar elementet under fingret (`document.elementFromPoint`) och går uppåt till närmaste element vars `id` matchar `n` + siffror eller `m` + siffror \[V10\]. Hittas inget: välj närmaste takt på `x` och system.
2. JS skickar `tap { id, measure, x }`.
3. ViewModel avgör `ScorePosition`: är `id` en not blir positionen notens takt och tick i takten. Annars väljs närmaste tickposition i takten på `x` med hjälp av `items` (DR-10). Anropa sedan `IPlaybackEngine.SeekTo(position)` och skicka `setMarker` med närmaste not-id (eller takt-id) som `anchorId`.

## 10. Instrumentstöd (N1)

Appen SKA INTE ha en egen lista över MuseScores instrument (DR-03). All instrumentinformation läses ur partituret. Det gör att instrument som tillkommer i nyare MuseScore-versioner fungerar så länge filen bär samma fält.

MuseScores katalog innehåller 644 instrument (A18). Äldre filer saknar ofta `Instrument id`. Använd då `instrumentId` (MusicXML-id) och därefter `trackName`.

### 10.1 Vad ”stöd” betyder

Ett instrument är stött när alla fyra punkterna gäller:

| Punkt | Krav |
| --- | --- |
| Öppnas | Filen läses utan fatalt fel (R1, R2). |
| Notbild | Rätt klav, rätt antal notsystem, rätt notsystemtyp (standard, slagverk, tabulatur) och rätt skriven höjd för transponerande instrument. |
| Ljud | Ett lämpligt General MIDI-ljud spelas, valt av programmet i filen. Slagverk spelas på slagverkskanalen. Ljudet behöver inte vara identiskt med MuseScores (U8). |
| Namn | Långt och kort namn från filen visas i stämmalistan. |

### 10.2 Reservregler

Gäller när filen saknar information (V5). Varje reservregel lägger en `Diagnostic` av typen information.

| Saknas | Reserv |
| --- | --- |
| MIDI-program | Program 0 (flygel). |
| Bank | Bank 0. |
| Klav | G-klav. |
| Okänd notsystemtyp | Standardnotsystem. |
| Transponering | 0 halvtoner. |
| Slagverk utan trumdefinition | Använd MIDI-höjden som den är. |
| Namn | ”Stämma N” där N är stämmans ordning. |

### 10.3 Familjer som SKA ha eget test

1. Stråkinstrument.
2. Träblås.
3. Bleck.
4. Klaviatur med flera notsystem (till exempel piano).
5. Plockade instrument, inklusive gitarr med tabulatur.
6. Röster och kör.
7. Tonat slagverk.
8. Otonat slagverk och trumset.
9. Transponerande instrument (till exempel klarinett i B).
10. En stämma som byter instrument mitt i stycket.

### 10.4 Testmatris

Utanför appen: skapa med MuseScore ett litet partitur per instrument i MuseScores instrumentlista och spara dem i testkorpusen. Filen `instruments.xml` används bara lokalt som testdata och levereras aldrig. CI SKA köra varje partitur genom `IScoreLoader`, `MeiExporter` och en offline-rendering och kräva: inga fatala fel, minst en `NoteOn`, och att alla reservregler som aktiverades är loggade.

### 10.5 Kända begränsningar i v1

- Instrumentkanalbyte via text (arco, pizzicato, dämpare) saknas (DR-13).
- MuseSounds-ljud ignoreras och GM-ljud används (U8).
- Flera olika trumkit i samma stycke delar ett kit (AL-05).

* Bara första `<Channel>` per instrument används. Namngivna kanaler (arco, pizzicato, tremolo, open, mute) kräver kanalbyte via text (DR-13, DR-30).
* Filens mixer och ljudval (`audiosettings.json`) ignoreras. En stämma som författaren har tystat spelas ändå (DR-31).
* Filens banknummer ignoreras. Bank 0 eller 128 används (DR-30).
* Trumset och tabulatur är inte verifierade mot riktiga filer (V5, V14).

## 11. Användargränssnitt

### 11.1 Skärmar

Två skärmar räcker. Notbilden visar alltid hela partituret oavsett stämmaval.

```
┌─────────────────────────────────────┐
│ ‹  Titel                       [Stämmor] │
├─────────────────────────────────────┤
│                                     │
│   Notbild (WebView, vertikal scroll)│
│   ⚑ startmarkör    │ uppspelningspekare│
│                                     │
├─────────────────────────────────────┤
│  ▶/⏸    ■    Metronom   Inräkning   Följ │
└─────────────────────────────────────┘
```

| ID | Skärm och kontroll | Beteende | Tillgänglighetsetikett |
| --- | --- | --- | --- |
| UI-1 | Start: knappen ”Öppna fil” | Öppnar filväljare. Vid val: filen läses enligt DR-27, laddning (§7.5 steg 1), sedan partiturskärmen. Appen registreras även som mottagare för `.mscz` (Android intent-filter, iOS UTType). | ”Öppna fil” |
| UI-2 | Partitur: Spela/Paus | Växlar mellan `Play` och `Pause`. Visar rätt ikon efter tillstånd. | ”Spela” eller ”Pausa” |
| UI-3 | Partitur: Stopp | `Stop`. Inaktiv i `Stopped`. | ”Stoppa” |
| UI-4 | Partitur: Metronom | Växel för `MetronomeEnabled`. Fungerar under uppspelning. | ”Metronom på” eller ”av” |
| UI-5 | Partitur: Inräkning | Växel för `CountInEnabled`. | ”Inräkning på” eller ”av” |
| UI-6 | Partitur: Följ | Växel för följ-läge (AL-12). Visas som avstängd när användaren har scrollat själv. | ”Följ uppspelning på” eller ”av” |
| UI-7 | Partitur: Stämmor | Öppnar en bottenpanel med en rad per stämma (kryssruta och namn) och genvägen ”Alla”. Sista valda stämman kan inte avmarkeras (kryssrutan är inaktiv med förklaring för skärmläsare). Ändringar gäller direkt (AL-08). | ”Välj stämmor” |
| UI-8 | Notbilden | Tryck sätter startmarkören (AL-13). Scroll är fri. Zoom i fem steg (`zoomLevel` 0–4) med knappar eller nyp; omlayout vid ändring. | ”Notbild” |
| UI-9 | Varningar | Om laddningen gav varningar visas en liten informationsknapp som öppnar listan. Fatala fel visas som dialog (§12.3). | ”Visa varningar” |

### 11.2 Beteenden

- Under laddning visas en förloppsindikator. Gränssnittet SKA vara reaktivt (R3, §13).
- Skärmen hålls tänd under uppspelning (DR-22) och släpps vid paus och stopp.
- Metronom, inräkning, zoomnivå och följ-läge sparas lokalt och återställs vid nästa start (DR-25). Inget om partituren sparas.
- Ljust och mörkt tema följer systemet och skickas till JS med `loadScore.theme`. **Undantag:** notbilden (webbvyn) ritas alltid med svarta linjer och noter på vit bakgrund, oavsett tema, eftersom svarta noter är svåra att se på mörk bakgrund. Pekare och startmarkör använder därför ljusa temats färger. Se decisions.md #30.
- Stående och liggande läge stöds. Rotation utlöser omlayout (§7.5 steg 6).
- Alla kontroller har minst 44 × 44 dp tryckyta (N4).

### 11.3 MVVM-ansvar

| Typ | Ansvar |
| --- | --- |
| `StartViewModel` | Filval och start av laddning. |
| `ScoreViewModel` | Äger `IPlaybackEngine`, `IScoreBridge`, valda stämmor, växlarna och `PlaybackSnapshot`. Översätter `tap` till `SeekTo` (AL-13) och bygger tidslinjen (AL-11). |
| `IScoreBridge` | Tunt lager över WebView-meddelandena i IF-5. Har en fejkimplementation för tester. |
| `IFilePicker`, `IKeepAwake`, `ISettings` | Plattformstjänster bakom gränssnitt. `ISettings` omsluter `Preferences`. |

## 12. Säkerhet och robusthet

Filerna är opålitlig indata. Appen läser filer från okända källor, så S1–S5 och R1–R4 gäller utan undantag.

### 12.1 Gränser för indata (S1, S2)

Gränserna kontrolleras under läsning med en räknande ström. Överskrids en gräns avbryts laddningen med `LIMIT_EXCEEDED`.

| Gräns | Värde \[DEFAULT\] |
| --- | --- |
| Filstorlek (komprimerad) | 50 MB |
| Uppackad storlek för `.mscx`-posten | 150 MB |
| Förhållande uppackat/komprimerat | 100 : 1 |
| Antal ZIP-poster | 1000 |
| Antal stämmor | 128 |
| Antal takter | 5000 |
| Antal noter | 500 000 |

### 12.2 Övriga regler

- **XML (S3):** `DtdProcessing.Prohibit` och `XmlResolver = null`. Inga externa entiteter.
- **Ingen uppackning till disk (S1):** ZIP-poster läses direkt ur arkivet. Postnamn används aldrig som sökväg.
- **Filåtkomst (DR-27):** filen läses en gång till minnet och filhandtaget släpps. På iOS hålls security-scoped åtkomst bara under läsningen.
- **Brygga (S4):** all data till JS är JSON. Ingen sträng från filen sätts ihop till kod. Not-id och takt-id som JS tar emot behandlas som data.
- **WebView (S5):** `index.html` laddas från appens paketerade resurser. Sidan har en Content-Security-Policy utan externt nätverk och endast det som Verovios WASM kräver, till exempel `'wasm-unsafe-eval'` (V11). `connect-src 'self'` behövs för att `HybridWebView` ska kunna skicka meddelanden till C# på Android (same-origin `fetch` mot appens egen virtuella värd, decisions #22). Appen har ingen INTERNET-behörighet. Ingen navigering bort från appens innehåll.
- **Integritet (P1, DR-14, DR-25):** inga nätverksbehörigheter i manifest eller `Info.plist`, ingen telemetri. Bara inställningar sparas, aldrig partituren. Lokal logg får finnas, högst 1 MB, och innehåller aldrig filinnehåll eller filnamn.
- **Parsern kastar inte (R1):** alla undantag i laddningen fångas i `IScoreLoader` och blir `Diagnostic` eller ett fatalt `LoadResult`.

### 12.3 Felkoder och beteende

| Kod | Utlösare | Meddelande till användaren | Beteende |
| --- | --- | --- | --- |
| `NOT_A_SCORE` | Varken ZIP med `.mscx` eller XML. | ”Filen verkar inte vara ett MuseScore-partitur.” | Fatalt. Tillbaka till start. |
| `CORRUPT_ARCHIVE` | ZIP går inte att läsa. | ”Filen är skadad och kunde inte öppnas.” | Fatalt. |
| `LIMIT_EXCEEDED` | Gräns i §12.1 överskriden, eller minnesbrist vid laddning. | ”Partituret är för stort för att öppnas i appen.” | Fatalt. |
| `EMPTY_SCORE` | Inga stämmor eller inga takter. | ”Partituret innehåller inga noter.” | Fatalt. |
| `UNSUPPORTED_VERSION` | Filversion som parsern inte känner igen. | ”Den här filversionen stöds inte fullt ut. Vissa delar kan saknas.” | Försök tolka. Fatalt bara om tolkningen misslyckas. Annars varning. |
| `UNKNOWN_ELEMENTS`, `TICK_ROUNDING`, `PLAYORDER_LIMIT`, `PLAYORDER_FALLBACK` | Se AL-01 till AL-03. | Visas i varningslistan (UI-9). | Fortsätt. |
| `CURSOR_MISSING_GEOMETRY` | Se AL-11. | Visas inte för användaren. | Bara logg och räknare. |
| `RENDER_FAILED` | JS skickar `renderError`. | ”Notbilden kunde inte ritas. Uppspelning fungerar fortfarande.” | Degraderat läge: uppspelning och stämmaval fungerar. Notbild och pekare saknas. Startmarkör kan inte sättas. |
| `AUDIO_INIT_FAILED`, `SOUNDFONT_LOAD_FAILED` | Ljud kan inte startas. | ”Ljudet kunde inte startas.” med knappen ”Försök igen”. | Degraderat läge: notbilden visas. Uppspelningsknappar är inaktiva. |
| (ingen kod) | OS-avbrott (R4). | Ingen dialog. | `Paused` enligt AL-08. |

## 13. Prestanda- och kvalitetsbudgetar

Målen är \[DEFAULT\] och gäller på referensenheterna: en lågpresterande Android-telefon med Android 8–10 och 3 GB RAM, samt en iPhone med lägsta stödda iOS-version. Ägaren väljer exakta modeller.

| Mått | Mål | Mätmetod |
| --- | --- | --- |
| Installationsstorlek per arkitektur (N2) | Högst 50 MB | Release-bygge: AAB-uppdelning respektive iOS app thinning. Verovio och SoundFont är de största posterna. Kandidatfonten FluidR3Mono\_GM.sf3 är 23,7 MB komprimerad. |
| Kallstart till startskärm | Högst 2 s | Tid från appstart till första ritade bildruta. |
| Öppna 10 stämmor och 100 takter | Notbild synlig inom 2 s, redo att spela inom 3 s | Tid från filval, medianen av 10 körningar. |
| Minnesanvändning under uppspelning | Högst 300 MB | Plattformens minnesprofilering. |
| CPU under uppspelning | Högst 20 % av en kärna | Plattformens profilering, 10 stämmor. |
| Buffertunderskott (R3) | 0 under 10 minuter | `RingBuffer.Underruns`. |
| Hopp till hörbart ljud (F6) | Högst 300 ms | Offline-mätning av tid mellan kommando och första ljudblocket i ny epok. |
| Pekaravvikelse (F7) | Högst 50 ms | Automatiserat synktest (§14). |
| Batteri | Mäts i M5. Målet sätts efter första mätningen. | Plattformens batteriprofilering under 30 minuters uppspelning. |

Om ett mål missas: dokumentera mätvärdet, välj en åtgärd ur §15 och ändra inte målet utan att uppdatera beslutsregistret.

## 14. Teststrategi

Testbarhet är ett konstruktionskrav: `Core` är ren .NET (P2), ljud går via `IAudioOutput` med en offline-implementation, och bryggan går via `IScoreBridge` med en fejk. Varje nivå nedan har en grind som SKA vara grön innan milstolpen den hör till räknas som klar (§16).

| Nivå | Vad som testas | Grind |
| --- | --- | --- |
| T1 Enhet (Core) | `MsczReader`, `MscxParser`, `TempoMap`, `PlayOrderBuilder`, `EventGenerator`, `ChannelAllocator`, `MeiExporter`, `CursorTimelineBuilder`. | Alla tester gröna på korpusen. |
| T2 Facit mot MuseScore | För varje korpusfil: exportera MIDI och MusicXML med MuseScore lokalt och spara som facit i `tests/corpus/expected/`. Jämför antal noter, höjd samt start och slut i ticks och millisekunder (inklusive repriser). | Noll oförklarade skillnader. Förklarade skillnader listas i `tests/corpus/known-differences.md`. |
| T3 Ljud (offline) | `OfflineAudioOutput` ger bit-identisk PCM för samma indata (P7). F1–F3: energi per stämma. F4: klick på exakt ram. F5: antal klick och tystnad före sista klicket. F6/AL-07: chase och epokbyte. | Alla gröna. |
| T4 Ringbuffert | Stresstest med en skrivare och en läsare, `Flush` och epokbyte. | Inga förlorade eller dubblerade ramar, inget block från gammal epok läses efter `Flush`. |
| T5 Brygga och ViewModel | JSON-rundtur för alla meddelanden i IF-5 (inklusive okända `t`). `tap` → `SeekTo`. `CursorTimelineBuilder` mot syntetisk geometri. | Alla gröna. |
| T6 Notbild | Kör MEI från korpusen genom Verovio utan UI. Varje `Note.Id` och takt-id SKA finnas i SVG. Ögonkontroll mot MuseScore för ett urval. | Inga saknade id. Urvalet granskat. |
| T7 Synk på enhet | Instrumenterad körning loggar ljudklocka och pekarposition och räknar största avvikelse. | Högst 50 ms (F7). |
| T8 Hårdning | Felaktiga ZIP, ZIP-bomber, DTD-nyttolaster, avkortad XML, gränsfall i §12.1. | Ingen krasch. Rätt felkod enligt §12.3. |
| T9 Instrumentmatris | Se §10.4. | Alla instrument passerar. |
| T10 Enhetsmatris (manuell) | Avbrott, bakgrund, rotation, hörlurar ut, stort partitur, 10 minuters spel. | Checklistan i `docs/device-checklist.md` är ifylld. |

Korpusregler:

- Minst 30 filer. De ska täcka repriser, voltor, D.S. och D.C. med coda, upptakt, trioler, tempoändringar, transponerande instrument, trumset, tabulatur, taktupprepning, mer än 20 stämmor och formaten 3.x, 4.x och 5.x.
- Repots `demos` (elva partitur) FÅR ingå efter licenskontroll men räcker inte. De är i formaten 2.06 och 3.01 (plus en fil märkt 4.00 som skapats i 3.6) och saknar trumset, tabulatur och taktupprepning (A20).
- Skapa minst 10 filer med MuseScore 4 och 5 som täcker det demos saknar (trumset, tabulatur, taktupprepning, bindebågar, hopp, flera kanaler). De behövs också för att stänga V14.
- Varje fils licens SKA vara kontrollerad innan den läggs i repot. Filer utan tydlig licens läggs inte in.

## 15. Risker och reservplaner

| ID | Risk | Signal | Åtgärd |
| --- | --- | --- | --- |
| K1 | Verovio klarar inte slagverk, tabulatur eller komplex notation. | M0 eller T6 visar fel notbild. | Visa ett förenklat men korrekt standardnotsystem för de stämmorna. Om det inte räcker: DR-16. |
| K2 | Verovio-WASM är för långsamt eller stort på enkla enheter. | Budgetar i §13 missas. | Rendera system för system vid behov. Om det inte räcker: DR-16. |
| K3 | Verovio bevarar inte `xml:id` från MEI (V10). | M0 visar saknade id. | Koppla via takt och ordning i stället, eller använd Verovios tidskarta som källa för pekaren. Dokumentera i `docs/decisions.md`. |
| K4 | Parsern tolkar vissa `.mscx`-varianter fel. | T2 visar skillnader. | Versionsgrenar i parsern. Markera ostödda konstruktioner som varning. |
| K5 | Spelordningen avviker från MuseScore. | T2 visar skillnader i repriser eller hopp. | Rätta enligt T2. Reserv: rakt igenom med `PLAYORDER_FALLBACK`. |
| K6 | Pekaren glider ur synk. | T7 över 50 ms. | Korta ankarintervallet (till exempel 250 ms). Förbättra uppskattningen av utgångslatens. |
| K7 | GC-pauser ger buffertunderskott. | `Underruns` större än 0. | Förläng ringbufferten till 400 ms. Granska allokeringar i renderloopen. Sätt GC-läge för låg latens under uppspelning. |
| K8 | SoundFont är för stor eller har fel licens. | Storlek över 30 MB, eller licensgranskningen nekar. | Välj en mindre GM-SF2, eller skapa en beskuren SF2 med bara de ljud som behövs. Hämta inte något över nätet (P1). |
| K9 | MeltySynth stöder bara SF2 (V9). | M0. | Konvertera ljudfonten till SF2 offline, eller byt synt (DR-05). |
| K10 | GPL/LGPL-frågor blockerar release. | Juridisk granskning före M6 säger nej. | Ingen release före lösning. Byt komponent eller ändra appens licens. |
| K11 | MAUI-plattformsbuggar i WebView eller ljudtjänster. | Instabilt beteende i M0 eller M3. | Håll plattformskod tunn bakom gränssnitt (§7.2). Byt bryggans implementation utan att ändra IF-5. |
| K12 | Stora partitur ger lång laddning eller minnesbrist. | Budgetar missas. | Gränserna i §12.1. Strömmande parser. Lazy rendering. |
| K13 | iOS avbryter eller avvisar bakgrundsljud. | M5 eller butiksgranskning. | Följ Apples riktlinjer för bakgrundsljud. Testa avbrott på enhet. |

**DR-16 i korthet:** Om V10 eller K1/K2 inte kan lösas kompileras Verovio som native bibliotek per plattform och anropas via P/Invoke. Visningen sker då utan WebView, med egen ritning av Verovios SVG. Det kräver ny juridisk granskning av LGPL på iOS, och IF-5 ersätts av ett C#-gränssnitt med samma innehåll.

### Risker tillagda efter genomgången av källkoden (v0.4)

| ID | Risk | Signal | Åtgärd |
| --- | --- | --- | --- |
| K14 | Repots ljudfonter är SF3 (Ogg-komprimerade) och vald synt kan inte läsa dem. | M0: MeltySynth laddar inte `FluidR3Mono_GM.sf3` (bekräftat). | **Löst i kod:** GeneralUser GS som SF2 (DR-05). Kvar: Q4, att fonten (32,3 MB) ligger 7 % över riktvärdet 30 MB (K8) och att N2 (50 MB per arkitektur) måste mätas med Verovio, samt juridisk granskning av ursprunget för samplingarna (licensen anger att det inte är fullt känt). Reserv: beskuren SF2 med bara GM-instrumenten som behövs. |
| K15 | Korpusen saknar format 4.1 och senare, trumset, tabulatur och taktupprepning. | T2 kan inte köras för dem. | Skapa filerna med MuseScore 4 och 5 i M0 (§14). |
| K16 | Formatet 5.00 är under utveckling (huvudgrenen är 5.0.0) och kan ändras. | Parsern bryter på nyare filer. | Versionstolerant parser (P5, DR-34). Kontrollera mot släppt version före M6. |
| K17 | Spelordningen avviker från MuseScores i fall som AL-03 medvetet inte hanterar (överlappande och öppna voltor). | T2 visar skillnader. | `PLAYORDER_FALLBACK`. Utöka AL-03 först när korpusen visar behov. |

## 16. Genomförandeplan

Ordning och beroenden: M0 → M1 → (M2 och M3 parallellt) → M4 → M5 → M6. En milstolpe är klar först när alla punkter i ”Klar när” gäller och grindarna (§14) är gröna. Ingen kod för M1 eller senare skrivs innan M0 är klar.

| ID | Mål | Innehåll | Klar när |
| --- | --- | --- | --- |
| M0 | Spikes: avgör genomförbarheten | (a) Verovio i WebView på Android och iPhone: rendera ett hårdkodat MEI med 3 stämmor och 30 takter, få `layoutReady`, tryck → id. (b) MeltySynth och vald SoundFont via `AudioTrack` respektive `AVAudioEngine` med `RingBuffer`; mät `OutputLatencyFrames`. (c) Skapa med MuseScore 4 och 5 minst 10 filer som täcker det demos saknar (trumset, tabulatur, taktupprepning, bindebågar, hopp), stäng V14 och packa upp minst 5 riktiga `.mscz` (3.x och 4.x). (d) Välj synt och SoundFont enligt DR-05 (K14). | Alla öppna punkter i §4.2 stängda i `docs/format-notes.md`. (a) renderar inom 1,5 s på referensenheten och alla not-id går att hämta. (b) 10 minuter utan buffertunderskott och ett fungerande epokbyte. Synt och SoundFont valda med känd licens. K1, K3 och K9 bedömda och beslut om DR-16 skrivet i `docs/decisions.md`. |
| M1 | Core | `MsczReader`, `MscxParser`, modell, `TempoMap`, `PlayOrderBuilder`, `EventGenerator`, `ChannelAllocator`. | T1 och T2 gröna på korpusen. |
| M2 | Uppspelning | `RingBuffer`, `SynthHost`, `Metronome`, `Sequencer`, `PlaybackEngine` (AL-06–AL-10), plattformsimplementationer av `IAudioOutput`, stämmaval, bakgrundsljud och avbrott (R4). | T3 och T4 gröna. F1–F6 fungerar utan notbild i en testapp. |
| M3 | Notbild | `MeiExporter`, WebView, `bridge.js` (`loadScore`, `layoutReady`, `setZoom`), omlayout vid rotation. | T5 och T6 gröna. |
| M4 | Styrning och pekare | UI-kontroller (§11), `tap` → `SeekTo`, `setMarker`, `CursorTimelineBuilder`, pekare och följ-läge, lokala inställningar (DR-25). | T5 och T7 gröna. F7 uppfylls. |
| M5 | Instrumenttäckning och hårdning | Alla instrumentfamiljer (§10), säkerhet (§12), budgetar (§13). | T8, T9 och T10 gröna. Alla budgetar i §13 uppfyllda eller beslutade enligt §13. |
| M6 | Release | Namn, ikoner, butiksmaterial, signering, juridisk granskning. | K10 stängd. Alla F, N, S och R-krav verifierade. |

## 17. Instruktioner till implementerande agent

Dessa regler gäller för varje utvecklare eller språkmodell som bygger efter dokumentet.

1. Läs §1 först. Följ prioritetsordningen där.
2. Arbeta i milstolpeordning (§16). Börja med M0.
3. Gissa aldrig element- eller attributnamn i `.mscx`. När du är osäker: öppna en riktig fil, skriv resultatet i `docs/format-notes.md` och stäng motsvarande V-ID.
4. Skiljer sig dokumentet från din kunskap om MuseScore, Verovio, MAUI eller MeltySynth: för \[FAKTA\] gäller dokumentet. För \[VERIFIERA\] gäller filen eller aktuell dokumentation. Förlita dig inte på minnet för externa API:er. Kontrollera dem.
5. Ändra inte publika signaturer i §8 utan att också uppdatera dokumentet.
6. Bygg ingenting som finns i U1–U10, även om det vore enkelt (P6).
7. Skriv tester samtidigt med koden. En uppgift är klar först när dess grind i §14 är grön.
8. Håll `Core` fritt från plattformsberoenden (P2). Lägg in ett arkitekturtest som misslyckas om `Core` refererar något utöver BCL.
9. Allokera inte i renderloopen eller driver-callbacken. Lägg in ett allokeringstest.
10. Kopiera aldrig MuseScores källkod eller `instruments.xml` till repot (DR-02, DR-03). Du FÅR läsa källkoden som beteendereferens men ska skriva egen implementation.
11. Vid val mellan alternativ: välj \[DEFAULT\] och skriv datum, val och skäl i `docs/decisions.md`.
12. Varje avvikelse från §5–§9 loggas i `docs/decisions.md` med skäl innan koden checkas in.
13. Ställ frågor till människa bara om punkterna i §18.
14. Ha små incheckningar. Börja varje meddelande med det ID som uppgiften hör till, till exempel `AL-03: ...`.

## 18. Frågor som kräver mänskligt beslut

Tidigare öppna frågor om inräkning vid hopp, vilken förekomst som startas, hållna toner, lägsta OS, senast-öppnade-lista och oregelbundna taktarter är \[DEFAULT\]-beslut i §6 (DR-11, DR-10, DR-19, DR-21, DR-20, DR-18). Ändra dem där. Kvar att avgöra:

| ID | Fråga | Förvalt tills svar finns |
| --- | --- | --- |
| Q1 | Vilken licensmodell ska appen ha (sluten, kommersiell eller öppen)? Det avgör hur strikt DR-02 och DR-17 måste följas. | Sluten, med ren parser. |
| Q2 | Vad ska appen heta? Namnet får inte innehålla ”MuseScore” utan tillstånd. | ”ScoreLite” som arbetsnamn. |
| Q3 | Vilka två referensenheter ska användas för budgetarna i §13? | En Android med 3 GB RAM och Android 8–10, och en iPhone med lägsta stödda iOS. |
| Q4 | Godkänn SoundFont (storlek och licens). | Förslag efter K14: GeneralUser GS v2.0.3 (32,3 MB SF2). FluidR3Mono\_GM.sf3 går inte (SF3). Se DR-05. |
| Q5 | Ska okomprimerad `.mscx` kunna öppnas (U9)? | Ja. |
| Q6 | Vilket batterimål ska gälla? | Sätts efter första mätningen i M5. |

## 19. Källor

Dessa källor har lästs. Påståenden om MeltySynth, Verovio och MAUI:s webbvy är inte hämtade från källa och är därför \[VERIFIERA\] (V9–V12).

- MuseScore-main.zip (uppladdat källträd, huvudgren, version 5.0.0): `src/engraving/rw`, `src/engraving/dom`, `src/engraving/infrastructure/mscreader.cpp`, `src/engraving/playback`, `src/project/internal/projectaudiosettings.cpp`, `share/instruments`, `share/sound`, `demos` och `version.cmake`
- MuseScore Studio-repot och README: https://github.com/musescore/MuseScore
- Kodstruktur (wiki): https://github.com/musescore/MuseScore/wiki/CodeStructure
- Audio overview (wiki): https://github.com/musescore/MuseScore/wiki/Audio-overview
- Filformat (handbok): https://handbook.musescore.org/3/advanced-topics/file-formats
- Instrumentdefinitioner (`instruments.xml`): https://musescore.org/node/15803
- Filtypsbeskrivning för `.mscz`: https://fileinfo.com/extension/mscz

## 20. Ändringslogg

### Ändringar efter v0.4 (2026-10-26, löpande, bakgrund i decisions.md)

| Ändring | Var |
| --- | --- |
| `ScoreLite.App.Core` tillagt som eget projekt. Beroenderiktning och repostruktur uppdaterade till hur koden är byggd. | §7.2, §7.3, decisions #21 |
| `connect-src 'self'` i CSP (HybridWebView på Android). | S5, §12.2, decisions #22 |
| V10 och V11 uppdaterade: Verovio bevarar `xml:id`, `HybridWebView`-bryggan bekräftad på Android. | §4.2, decisions #22–24 |
| Notbilden ritas alltid svart på vit bakgrund oavsett tema. | §11.2, decisions #30 |
| Ljudfont: GeneralUser GS 2.0.3 (SF2), hämtas och checkas inte in. | DR-05, decisions #17, #20 |

### Ändringar i v0.4 (efter genomgång av MuseScores källträd)

| Ändring | Var |
| --- | --- |
| Underlaget är nu källträdet. Fakta A1–A20 och verifieringsregistret V1–V14 har status, resultat och vad som återstår. V1, V2, V3, V4, V6, V7 och V8 är bekräftade. V5 och V13 är delvis bekräftade. V9–V12 och V14 är öppna. | §4 |
| Huvudfilen väljs via `container.xml`, sedan `<filnamn>.mscx`, `score.mscx` och första `.mscx` i roten. `Excerpts/` ignoreras. `LoadAsync` tar `fileNameHint`. | AL-01, IF-2, DR-29 |
| Positionsmodellen preciserad: relativ `location`, tomma `<voice/>` behåller röstnumret, tuplets med start- och slutelement, upptakt via `len`. | AL-01, A8–A10 |
| Bank och kanal: första `<Channel>` gäller och banknumren i filen ignoreras, eftersom de pekar in i MuseScores egen ljudfont. Filens mixer och ljudval ignoreras. | DR-30, DR-31, AL-04 |
| Velocity tas från filens `<Dynamic>`. Tabellen är reserv. | DR-32, AL-04 |
| Spelordning omskriven efter filens hopptabell (etiketter `jumpTo`, `playUntil`, `continueAt`). Taktupprepning tillagd. Överlappande och öppna voltor hanteras inte i v1. | AL-03, DR-33, K17 |
| Formatstöd: 3.01 och senare fullt ut, 2.06 på bästa sätt. Okänd eller nyare version stoppar aldrig laddningen. | DR-28, DR-34 |
| Ljudfont och synt: repots fonter är SF3, och MeltySynth stöder troligen bara SF2. DR-05 omformulerad till två spår, med `FluidR3Mono_GM.sf3` (23,7 MB, MIT) som första kandidat. Licenssida krävs. | DR-05, DR-35, A19, K14 |
| Korpusen: repots demos räcker inte (bara 2.06 och 3.01, inget trumset, ingen tabulatur, ingen taktupprepning). Minst 10 egna filer med MuseScore 4 och 5 krävs. | §14, K15, M0 |
| Nya risker: format 5.00 under utveckling (K16) och spelordning i fall som inte hanteras (K17). | §15 |

### Ändringar i v0.3 (sedan v0.2)

| Ändring | Var |
| --- | --- |
| Påståendet om när Audio overview senast redigerades är borttaget eftersom det inte kunde styrkas. Sidan markeras som möjligen föråldrad. | A3 |
| Accepttestet för F6 skiljer nu på inräkning på och av. | F6 |
| Klickljuden ska skapas av projektet självt, så att de inte bär en tredjepartslicens. | DR-17, AL-09 |
| Nya beslut: lokala inställningar (DR-25), stängning av ljudutgång efter 30 s (DR-26) och filåtkomst som läser filen en gång till minnet (DR-27). | §6, §11.2, §12.2 |
| `Part.Id` har fast format (`p0`, `p1`, ...). `IPlaybackEngine` anropas bara från UI-tråden. `ISettings` tillagt. | §8.1, §8.3, §11.3 |
| Ordlistan har `PlayMs`, och ”ankarpunkt” kopplas till `setClock`. Diagrammet och klockdomänerna använder `FramesInCurrentEpoch` och absoluta ramar. | §3, §7.1, §7.4 |
| `docs/device-checklist.md` tillagd i repostrukturen. | §7.3 |
