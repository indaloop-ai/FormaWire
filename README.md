# FormaWire

Deformazione dei profili delle placchette per l'elettroerosione a filo con montaggio inclinato.
L'editor apre disegni DXF e mantiene i raggi come curve esatte (archi di ellisse) durante la deformazione.

Verificato su 7 lavori di produzione (confronto con i programmi NC del post Mitsubishi MV1200R): ogni punto della prima
passata conica sta entro 1 µm dai profili calcolati, sia sul piano XY sia sul piano UV.

## Struttura

```
src/FormaWire.Core      modello geometrico, lettura DXF e file di verifica, scrittura DXF, motore di deformazione, documento dell'editor
src/FormaWire.Web       l'editor (Blazor Server, gira in locale nel browser, nessun pacchetto esterno)
src/FormaWire.Cli       programma a riga di comando (formawire deform | verify | info | pick)
tests/FormaWire.Tests   test di regressione senza dipendenze (7 lavori contro NC, 2 lavori dai disegni originali, editor, CAD)
tests/Fixtures          disegni, programmi NC e lavori di riferimento usati come casi di prova
Docs                    lavori di riferimento dell'ufficio tecnico e disegni originali
nuget.config            elenco di sorgenti vuoto — la soluzione non ha pacchetti esterni
```

Compilazione: `dotnet build` (SDK .NET 8).  Test: `dotnet run --project tests/FormaWire.Tests/FormaWire.Tests.csproj`.
Verifiche web (limiti, configurazione e interfaccia): `dotnet run --project tests/FormaWire.Web.Tests/FormaWire.Web.Tests.csproj`.

## Pubblicazione sul sito

Per Ubuntu, systemd, nginx e Cloudflare Tunnel vedi [DEPLOY.md](DEPLOY.md).
`PathBase` è vuoto in locale e diventa `/formawire` sul sito. Il limite predefinito
è **20 MB per DXF**, con **30 circuiti complessivi**; fino a 20 sessioni disconnesse
restano recuperabili per 2 minuti e occupano ancora un posto. I file caricati sono
elaborati in memoria: l'app web non li salva sul disco del server. Salva o esporta
il disegno per conservarlo oltre la durata della sessione.

## L'editor

```
dotnet run --project src/FormaWire.Web
```
poi apri http://localhost:5269 (l'indirizzo compare nel terminale). Si apre la **pagina iniziale**: un grande
riquadro dove trascinare il DXF (o cliccarlo per scegliere il file) e **Inizia da zero** per disegnare
su un foglio vuoto con l'origine (0,0) al centro. L'editor accetta esclusivamente file **DXF**, anche tramite
trascinamento; gli altri formati vengono rifiutati senza modificare il disegno corrente. Un DXF si può
trascinare nella finestra anche dopo; **Nuovo**
torna alla pagina iniziale (Annulla riporta il disegno di prima). In cima al pannello destro restano visibili
quattro pulsanti: **Disegno**, **Profilo XY**, **Spoglie UV**, **Deforma / esporta**. Cliccane uno per aprire
solo i suoi controlli, oppure usa i pulsanti in fondo per passare al precedente o al successivo. Non serve
scorrere gli altri passi e i valori restano impostati quando cambi sezione; *In corso* indica il passo ancora
da completare. Le due righe compatte in alto raccolgono file, vista e strumenti CAD; sotto il disegno ci sono
la riga di comando, gli snap e l’interruttore **Griglia 2D**. **Guida** (o F1) spiega il flusso di lavoro,
gli angoli di montaggio, le spoglie e gli strumenti.

### Più profili nello stesso disegno

Il selettore **Profilo attivo** in cima al pannello destro sceglie la coppia XY/UV
su cui lavorare. Premi **+ Profilo**, poi clicca una nuova catena nel disegno:
il profilo precedente resta memorizzato. Ogni profilo ha la propria profondità UV,
spoglia laterale, correzione frontale, spoglie per elemento, montaggio e tagliente.
**Nome e gestione** permette di rinominarlo o rimuoverlo; rimuovere una configurazione
mantiene il disegno originale e si può annullare. Un elemento appartiene a una sola
coppia: per usarlo in due configurazioni, prima fanne una copia.

Tutti i profili restano visibili; quello attivo è più marcato. Un clic su un altro
profilo lo rende attivo e aggiorna i suoi campi. **Calcola attivo** lavora sul
profilo selezionato; **Calcola tutti** usa le impostazioni indipendenti di ogni
coppia e segnala il nome di un eventuale profilo incompleto.

Nell'esportazione, **Tutti i profili** crea un unico DXF con layer separati
`XY` / `UV`, `XY_2` / `UV_2`, ecc.; togli la spunta per esportare solo quello attivo
sui normali layer `XY` / `UV`. *Entrambi* aggiunge i rispettivi layer `_NOM`;
riaprendo un'esportazione multipla con entrambi, si modificano i profili nominali.
**Salva** conserva il disegno e le impostazioni di tutti i profili, compreso il modo
UV (automatico o disegnato) e le scelte per elemento. Il formato usa commenti DXF
999, ignorati dagli altri CAD. I vecchi file con una sola coppia restano leggibili.

**1 · Disegno.** *Apri file* carica un disegno completo dell'utensile in formato DXF; quote, tratteggi, testi
e blocchi vengono ignorati, si caricano LINE / ARC / CIRCLE / POLYLINE / ELLIPSE. I layer si possono nascondere;
un clic sul nome di un layer lo seleziona. **Ripara disegno** unisce gli estremi più vicini della distanza indicata
(predefinita 0,01 mm; le linee si spostano sugli archi, gli archi non si muovono), toglie doppioni ed elementi di
lunghezza zero e unisce le linee che si continuano — sulla selezione, o su tutto ciò che è visibile.

**2 · Profilo utensile (XY).** *Scegli il profilo* e cliccalo: la catena che passa per quell'elemento diventa il
profilo XY (si ferma dove si incontrano più di due elementi — lì usa Taglia/Cancella, oppure seleziona i pezzi con
Maiusc+clic e *Usa come XY*, nella barra in alto accanto a **Catena**). Una freccia indica il verso
(*Inverti verso*). **Specchia ↔** ribalta tutto il
disegno attorno all'asse verticale al centro del profilo: un utensile destro diventa sinistro nello stesso posto, il
lato del materiale e le spoglie seguono (il verso si specchia anche lui: *Inverti verso* se serve). Su un profilo
chiuso **Punto di partenza** sceglie con un clic dove parte il filo (l'elemento lì viene diviso). **⌖ Profilo →
origine** (**Profilo all’origine** nella barra degli strumenti) porta il punto più a sinistra del profilo a X0 e il punto più alto a
Y0 (esatti anche sulla sommità di un raggio; senza profilo XY usa la selezione o tutto il disegno), *Punto →
origine* un punto cliccato. *Aggiungi* le linee di
entrata/uscita: quanto oltre il profilo finisce ciascuna (entrata e uscita separate — BA1360: 1 e 2,55 mm) e il loro
angolo (45°).

**3 · Secondo profilo (UV) · spoglie.** Tutte le spoglie si fanno qui. La *profondità UV* è la distanza da XY a UV:
l'ufficio tecnico disegna a 5 mm anche su una placchetta da 10 mm (BA1360) — contano gli angoli. Poi
* **Da angoli** (predefinito, ricostruito subito a ogni modifica):
  * **correzione frontale** — sposta tutto il secondo profilo in Y di profondità·tan(angolo), come lo disegna
    l'ufficio tecnico: −2° = 2° di spoglia frontale in meno di quella del montaggio (da 8° a 6°: +5·tan 2° =
    +0,1746 mm su 5 mm, BA1408);
  * **spoglia laterale** — su ogni fianco, verso il materiale (sotto un profilo aperto, dentro uno chiuso;
    *materiale sopra* la inverte);
  * **direzione** — *orizzontale / verticale* (predefinita): i fianchi si spostano in X, i lati frontali in Y, di
    profondità·tan(angolo) qualunque sia la pendenza (come fa l'ufficio tecnico); oppure *perpendicolare al lato*:
    ogni elemento si sposta perpendicolare a sé stesso (raggi concentrici).

  Ogni elemento di XY riceve un ruolo in automatico, scritto sul disegno: le linee entro 20° dall'orizzontale sono
  **frontali** (senza spoglia propria: i loro estremi scorrono fino ai fianchi spostati), gli archi tangenti a due
  linee sono **raggi** (ricostruiti tra le loro linee), il resto sono **fianchi**. La tabella cambia un elemento:
  direzione (→ orizzontale, ↓ verticale, ⊥ perpendicolare) e angolo (0 = nessuna spoglia, negativo = dall'altra
  parte; vuoto = automatico). Con il passo **Spoglie UV** aperto, un clic su un elemento nel disegno trova la sua riga; un clic sul numero di una riga
  seleziona l'elemento. **Zone**: seleziona gli elementi di una zona (finestra nel disegno, o Maiusc+clic sulle
  righe) e impostala nella barra sopra la tabella — angolo dei *fianchi*, dei *frontali*, direzione; i raggi
  continuano a seguire. BA1360 dal suo disegno originale: laterale 1°, poi i denti (finestra) fianchi 0°, il lato
  d'uscita (finestra) fianchi 0,5° = il profilo UV dell'ufficio tecnico. Punte a V e punte raggiate non richiedono
  raccordi. BA1408 si ricostruisce esattamente da *frontale −2°, laterale 1°*; IN0932 da *laterale 3°* più quattro
  elementi scelti dal disegnatore.
* **Disegnato**: *Scegli nel disegno* un secondo profilo già disegnato (stesso numero di elementi), oppure *Copia XY
  e modifica* e modellalo a mano (Sposta con *mantieni collegati*, maniglie). Si usa così com'è; la correzione
  frontale (spostamento comune in Y) e la spoglia di ogni elemento vengono **misurate** e mostrate; *Leggi gli
  angoli* le mette nei campi e nella tabella.

**4 · Deformazione.** Due angoli: l'**angolo di montaggio** (quanto è inclinato l'utensile sull'attrezzo — la
barretta in macchina, 8°; parametro ROTB) e l'**angolo del tagliente** (quanto è inclinato in più il tagliente — la
placchetta nella barretta; parametro ROTP); i valori predefiniti sono 8° e 6°. Il riepilogo sotto i
campi mostra **Angolo totale** e **Fattore in Y**. *Calcola deformazione* mostra il risultato (rosso XY,
blu UV); dopo il calcolo il pulsante diventa *Ricalcola deformazione*.
*Esporta DXF* nel passo **Deforma / esporta**: **Deformato** (i profili da tagliare, layer XY / UV),
**Originale** (il profilo XY e il secondo profilo come costruiti, layer XY / UV — si riapre qui e negli altri CAD) o
**Entrambi** (deformati su XY / UV, non deformati su XY_NOM / UV_NOM). Il menu **Esporta** in alto offre le stesse
scelte, chiamando gli originali *Profili non deformati*. *Avanzate*: perno Y (IY), UV a Z0.
**Salva** (in alto) scrive il disegno pulito con i layer XY/UV e i parametri (un commento DXF 999) — si riapre
con gli stessi angoli. FormaWire legge anche i parametri dei DXF salvati dalle versioni precedenti.

**Strumenti CAD** (seconda riga in alto, tasto o comando tra parentesi; tutti usano gli snap). Scegli il gruppo
**Seleziona**, **Disegna**, **Trasforma**, **Rifinisci** o **Misura**; sulle finestre più strette i gruppi sono
nel menu a tendina. Le scorciatoie selezionano lo strumento e aprono il suo gruppo. La scelta segue i
comandi di modifica standard dei CAD 2D (AutoCAD: MIRROR, OFFSET, SCALE, COPY, CHAMFER, ALIGN…) e le funzioni
geometriche dei CAM per elettroerosione a filo (inversione del verso, punto di partenza del profilo chiuso):
* **Seleziona** [Esc] clic / Maiusc+clic / finestra (da sinistra a destra: dentro; da destra a sinistra: anche ciò che
  tocca); le linee selezionate mostrano le **maniglie** blu — trascinane una (o cliccala e poi clicca la
  destinazione) per spostare quell'estremo; con *mantieni collegati* le linee che si incontrano lì lo seguono. Una
  sola linea selezionata permette di modificare le coordinate di inizio e fine: espandi **Proprietà selezione**
  in fondo al pannello destro. Con una selezione attiva, **Cancella**, **Isola**, **Inverti**, **Usa come XY** e
  **Usa come UV** compaiono nella barra in alto accanto a **Catena**; restano disponibili negli altri gruppi
  di strumenti e scompaiono quando la selezione è vuota. **Catena** [C] seleziona
  tutto ciò che è collegato all'elemento cliccato.
* **Linea** [L] continua; si blocca ogni 45° vicino al cursore (**POLARE**, F10). Un numero scritto = la lunghezza nella
  direzione del cursore (o dell'angolo bloccato); **Tab** = scrivi invece l'angolo (poi la lunghezza, o clicca);
  `1.5` Tab `-90` Invio disegna 1,5 mm verso il basso. **Arco** [A] per tre punti (inizio, un punto sull'arco,
  fine), con anteprima; **Cerchio** [CE] centro → raggio (scritto o cliccato).
* **Sposta** [M] punto base → destinazione (prima la selezione; *mantieni collegati* allunga le linee vicine);
  **Copia** [K] come Sposta ma gli originali restano, una copia a ogni clic (le copie di elementi XY/UV vanno sul
  layer 0); **Ruota** [O] centro → angolo (scritto o cliccato); **Specchia** [I] due punti dell'asse (si blocca ogni
  45°; Tab per l'angolo e Invio), con anteprima — *tieni l'originale* fa una copia specchiata (metà profilo → profilo
  simmetrico), senza spunta gli elementi si ribaltano restando nel profilo e, se è tutto il profilo XY, il lato del
  materiale segue; **Scala** [SC] punto fermo → fattore scritto (25.4 = da pollici a mm; *Scala tutto* nel passo 1);
  **Stira** [S] finestra attorno agli estremi da spostare → punto base → destinazione; **Allinea** [AL] clic su una
  linea: il disegno (o la selezione) ruota attorno al suo inizio finché è orizzontale o verticale.
* **Taglia** [T] clic sulla parte da togliere (ogni elemento visibile fa da limite); **Estendi** [E] clic vicino a un
  estremo: si allunga fino al prossimo elemento — oppure scrivi una lunghezza (casella *di … mm* o riga di comando)
  e ogni clic allunga di quella misura (negativa accorcia); **Spezza** [B] divide nel punto cliccato (con gli snap) — in
  una coppia XY/UV disegnata si spezza anche l'elemento corrispondente, così l'abbinamento resta uno a uno;
  **Raccorda** [R] due linee dalla parte da tenere: R0 le fa incontrare esattamente, R>0 arrotonda lo spigolo
  (inserito nel profilo); **Smusso** [SM] come Raccorda ma con un taglio dritto (`0.5` uguale sulle due linee,
  `0.5;1` diverso); **Offset** [P] copia parallela a una distanza: clic sull'elemento, poi dalla parte voluta — con
  *tutta la catena* l'intero profilo collegato, con gli spigoli chiusi (linee allungate o accorciate, raggi che
  crescono o calano, un arco attorno allo spigolo se le due copie non si incontrano).
* **Misura** [D] distanza, ΔX, ΔY, angolo tra due punti.
* **Snap ad oggetto** FIN MED CEN INT PER VIC ORI (ORI = l'origine 0,0, segnata sul disegno da una freccia rossa X e
  una verde Y; pulsanti sotto il disegno, F3 tutti acceso/spento) con un simbolo sul punto agganciato; **POLARE**
  45° [F10]; **ORTO** [F8]. Un elastico mostra lunghezza e angolo mentre si sceglie il secondo punto.
* **Griglia 2D**: l’interruttore nella barra sotto il disegno mostra o nasconde la griglia; **F7** fa la stessa
  cosa. Spegnerla lascia visibili il disegno e gli assi e non modifica gli snap. La griglia si adatta allo zoom
  e, quando è visibile, il passo compare in basso a sinistra. In 3D l’interruttore è disabilitato; tornando in
  2D ritrovi la scelta precedente.
* **Riga di comando**: scrivi un punto invece di cliccare — `x,y` (o `x;y` con la virgola decimale), `@dx,dy`
  relativo, `@lunghezza<angolo` polare, `<angolo` per bloccare un angolo, o solo una lunghezza; un numero è il
  raggio in Raccorda e in Cerchio, la misura in Smusso, la distanza in Offset, l'angolo in Ruota, il fattore in
  Scala, la lunghezza in Estendi; `L A CE SP CP RU MI SC ST AL TA ES SPE RA SM OF DI CA` cambiano strumento (vanno
  anche quelli inglesi). Basta digitare una cifra per iniziare.
* Annulla/Ripeti (Ctrl+Z / Ctrl+Y), Canc, Ctrl+A, F = mostra tutto, Z = zoom sulla selezione, rotella = zoom,
  trascinamento con il tasto centrale/destro o Spazio+trascina = sposta la vista. **3D** passa a una vista
  orbitale: XY a Z0, UV a Z = −profondità, linee grigie = il filo tra gli elementi accoppiati, rosso/blu = il
  risultato deformato.

## Riga di comando

```
formawire deform disegno.dxf --upper XY --lower UV --rotb 8 [--depth 5] -o disegno_DEF.dxf
formawire deform disegno.dxf --upper PROFILO --depth 5 --rotb 8 --generate-lower --front 6 --relief 1
formawire deform disegno.dxf --upper PROFILO --depth 5 --rotb 8 --generate-lower --front 6 --side 1-3:1 --side 5-9:-1
formawire pick   BA1403A1_0.dxf --layer 1 --near 30.28,16.9 --reverse --range 3-13 -o profilo.dxf
formawire info   disegno.dxf --upper PROFILO        # elenca gli elementi della catena con i loro numeri
```

* DXF: il profilo superiore su un layer, quello inferiore su un altro (LINE / ARC / LWPOLYLINE / POLYLINE / ELLIPSE).
  Le entità sparse vengono messe in catena da sole. Profondità = differenza di quota Z dei layer, oppure `--depth`.
  I due profili devono avere lo stesso numero di elementi, in corrispondenza uno a uno.
* Da un disegno completo (`pick`): prende l'elemento più vicino a `--near x,y` (anche solo su un layer), segue gli
  estremi collegati nei due versi fino a un estremo libero o una diramazione, tiene `--range a-b`, porta l'angolo di
  riferimento nell'origine (X del punto più a sinistra, Y del punto più alto — la convenzione dell'ufficio tecnico;
  oppure `--origin x,y` / `none`), aggiunge le linee di entrata/uscita come nei lavori (45° dal primo/ultimo vertice,
  verso il basso e verso l'esterno, fino a `--lead` mm (predefinito 1) oltre il profilo in X; `--lead-angle` cambia
  l'angolo, `--lead 0` per nessuna) e scrive la catena sul layer `XY`.
* Solo profilo superiore (`--generate-lower`): il profilo inferiore si costruisce come fa l'ufficio tecnico.
  `--front <gradi>` è la spoglia frontale voluta dal disegno: il profilo inferiore si sposta di +Y profondità·tan(ROTB
  − front) (attrezzo 8°, disegno 6° → +profondità·tan 2°; verificato su BA1408). `--relief <gradi>` dà quella spoglia
  laterale a ogni fianco, con i ruoli automatici dell'editor (BA1408: `--relief 1`). Oppure per numero:
  `--side a-b:<gradi>` sposta gli elementi a..b in X di profondità·tan(gradi) (segno = verso; i numeri degli elementi
  li stampa `info --upper`). Gli elementi tra tratti spostati in modo diverso diventano raccordi (la sommità del dente /
  le spalle nei lavori di produzione). Un arco non può fare da raccordo. I profili inferiori di BA1408 e IN0932 si
  ricostruiscono esattamente con queste regole.
* DXF in uscita (R2000): layer `XY` (rosso) = profilo superiore deformato a Z0, layer `UV` (blu) = profilo inferiore
  deformato a Z = −profondità (`--flat` lo mette a Z0). `--nominal` aggiunge i profili non deformati su
  `XY_NOM` / `UV_NOM`.

## Come funziona la deformazione

Modello: profilo superiore a Z=0, profilo inferiore a Z=−h, accoppiati elemento per elemento (superficie rigata).
Con α = ROTP + ROTB:

1. Comprime Y di cos α attorno al perno Y = IY.
2. Inclina la superficie rigata di α attorno all'asse X e la taglia con i piani Z=0 e Z=−h.
   Sono le vere sezioni, in macchina, della placchetta inclinata.
3. Sposta la sezione inferiore di −h·tan α (+ h·tan DROT): i lati frontali hanno il filo verticale, quindi la spoglia
   frontale viene solo dall'inclinazione dell'attrezzo (ROTB).
4. Scala Y di cos α / cos ROTP attorno al perno.

Effetto sul disegno: Y × cos α / cos ROTP; i fianchi con spoglia e si inclinano di tan e · sin α (mm per mm di Y);
conicità laterale apparente = tan e / cos α; X invariata.

Elementi: una coppia di linee → una linea (coppie complanari; altrimenti una polilinea approssimata, con avviso).
Una coppia di archi con la stessa ampiezza è un cono quadrico, quindi la sua sezione piana è una conica esatta:
il motore campiona tutto il cono, calcola la conica (minimi quadrati algebrici, esatta a ~1e-12), controlla lo scarto
e ritaglia l'arco con gli estremi trasformati → entità DXF `ELLIPSE`. Ciò che non si può rappresentare in modo esatto
diventa una polilinea entro tolleranza e viene segnalato.

## Non ancora coperto

* ROTP ≠ 0 e la variante tangenziale sono implementati, ma non ancora verificati su un programma macchina:
  BA1360 (ROTP 6) e IN0899 (ROTP 5) si ricostruiscono dai loro disegni e si deformano in elementi esatti, ma i loro
  file NC non sono ancora tra i casi di prova.
* La correzione aggiuntiva `--drot` riproduce una logica storica non verificata nei casi di prova; usa
  invece `--front`, il cui segno è verificato su BA1408.
