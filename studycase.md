# FormaWire: un’alternativa gratuita e open source per l’elettroerosione a filo

Dall’esperienza nella produzione di utensili e dall’uso dell’AI nello sviluppo nasce FormaWire: un’applicazione per preparare e deformare i profili delle placchette da lavorare con l’elettroerosione a filo. L’abbiamo costruita per rendere più semplice un’attività tecnica precisa, con particolare attenzione ai raggi, ai raccordi e alla possibilità di controllare il risultato.

Abbiamo scelto di rilasciarla gratuitamente e come progetto open source, per mettere a disposizione del settore un’alternativa ai programmi datati che continuano a richiedere tempo, correzioni e denaro.

## Il problema che conoscevamo dal lavoro sul campo

Nella produzione di utensili, preparare un profilo per il taglio richiede di tenere conto di come la placchetta verrà montata e degli angoli necessari alla lavorazione. Il disegno nominale deve diventare una geometria utilizzabile nel processo di elettroerosione.

Per svolgere questo passaggio, molte aziende del settore utilizzavano versioni molto vecchie di software specialistici. Era una situazione che conoscevamo dal lavoro sul campo: interfacce difficili da capire, operazioni poco immediate e problemi nella gestione dei raggi e delle geometrie curve.

Chi usava questi strumenti doveva conoscere anche i loro limiti. Occorreva ricordare sequenze di comandi, verificare passaggi poco chiari e intervenire quando il risultato non corrispondeva al profilo atteso. I problemi sui raccordi aggiungevano controlli e correzioni proprio nei punti in cui la continuità della geometria conta di più.

Al costo del software si sommava così quello del tempo necessario a usarlo, controllarlo e compensarne i difetti.

Da questa esperienza è nata la decisione di costruire FormaWire.

## La conoscenza del settore ha guidato lo sviluppo

Il punto di partenza erano i disegni, le procedure e i problemi che conoscevamo. Sapevamo quali parametri servivano a chi prepara un utensile: profilo XY, secondo profilo UV, spoglie, angoli di montaggio, entrate e uscite del filo.

Questa conoscenza ci ha permesso di definire che cosa il programma dovesse calcolare, come presentarlo all’operatore e quali risultati confrontare per verificarne il comportamento.

L’AI ci ha affiancato nello sviluppo del codice, nell’analisi delle logiche geometriche e nel miglioramento dell’interfaccia. Abbiamo usato questo supporto per trasformare l’esperienza del settore in uno strumento utilizzabile e per rivederlo attraverso prove successive.

Nel funzionamento di FormaWire, i calcoli sono eseguiti da un motore geometrico con regole esplicite e verificabili. L’applicazione può essere eseguita in locale e utilizzata dal browser, senza richiedere un servizio AI per elaborare ogni disegno.

## Una cura particolare per raggi e raccordi

Uno dei punti centrali del progetto era il trattamento delle curve durante la deformazione.

Quando si trasforma un profilo per tener conto dell’inclinazione, la geometria di un arco può cambiare. Nel modello utilizzato da FormaWire, una coppia di archi compatibili può generare una sezione ellittica: rappresentarla correttamente è parte del calcolo.

Il motore conserva queste curve come archi di ellisse e le esporta nel DXF come entità geometriche dedicate. Per i casi rappresentabili in questa forma, il profilo mantiene una curva continua anche nel file esportato.

Quando una geometria richiede un’approssimazione, FormaWire la esegue entro una tolleranza e la segnala. Questo rende visibile all’operatore come è stato costruito il risultato.

## Un percorso più chiaro, dal disegno al profilo da tagliare

Abbiamo organizzato il lavoro in quattro passaggi:

1. **Aprire o disegnare il profilo.** Si può importare un DXF oppure iniziare da un foglio vuoto con gli strumenti CAD integrati.
2. **Scegliere il profilo XY.** L’operatore identifica la catena di elementi che descrive il profilo dell’utensile e ne controlla verso e posizione.
3. **Definire il secondo profilo UV e le spoglie.** Il secondo profilo può essere ricavato dagli angoli impostati oppure scelto da un disegno esistente.
4. **Calcolare la deformazione ed esportare.** Gli angoli di montaggio determinano la trasformazione; i profili si possono controllare nelle viste 2D e 3D ed esportare in DXF per il flusso di lavoro successivo.

L’interfaccia lascia ampio spazio al disegno. I comandi legati alla selezione compaiono vicino agli strumenti, i parametri sono raccolti per fase e una guida integrata spiega il percorso. L’obiettivo è rendere più facile trovare il comando necessario e capire quale passaggio si sta eseguendo.

## Verifiche su lavori reali

Lo sviluppo è stato accompagnato da confronti con disegni e programmi di lavorazione esistenti.

Il progetto comprende verifiche automatiche su **sette lavori di produzione**, confrontando i profili XY e UV calcolati con i punti della prima passata conica dei programmi NC di riferimento. Altri controlli riguardano la continuità dei profili, la trasformazione degli archi, l’esportazione e la riapertura dei DXF.

Queste prove permettono di verificare il comportamento del motore sui casi disponibili e di individuare regressioni quando il codice cambia. La documentazione indica anche le configurazioni per cui mancano ancora confronti con programmi macchina.

## Perché lo rilasciamo gratuitamente e open source

FormaWire nasce da un problema condiviso da chi lavora in questo settore. Abbiamo scelto di mettere a disposizione anche il risultato del lavoro, attraverso un rilascio gratuito e open source.

Vogliamo che le aziende possano accedere a uno strumento aggiornabile senza acquistare una licenza o un abbonamento al software. La disponibilità del codice permette inoltre di comprenderne il funzionamento, proporre correzioni e contribuire alla sua evoluzione secondo la licenza del progetto.

Il beneficio che cerchiamo è concreto: ridurre il tempo assorbito dalle difficoltà del programma, dai controlli ripetuti e dalle correzioni delle geometrie, lasciando più spazio al lavoro tecnico sull’utensile.

Per MadNetAI, questo progetto mostra come l’esperienza in un mestiere e l’AI applicata allo sviluppo possano produrre strumenti utili anche per esigenze molto specifiche. Con FormaWire vogliamo condividere questa possibilità con l’industria da cui il progetto è nato.

<!-- Prima della pubblicazione: aggiungere il collegamento al repository pubblico e alla licenza del rilascio. -->
