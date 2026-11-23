# FormaWire

FormaWire è un editor CAD nel browser per preparare e deformare i profili delle placchette da lavorare con l'elettroerosione a filo. Dal disegno nominale ricava i profili XY e UV tenendo conto delle spoglie e degli angoli di montaggio dell'utensile.

## Funzionalità

- **Importazione DXF** o creazione di un disegno da zero con gli strumenti CAD integrati.
- **Modifica del disegno** con linee, archi, cerchi, raccordi, smussi, offset, trasformazioni, snap e misure.
- **Profili XY e UV** con spoglie laterali, correzione frontale e impostazioni per singolo elemento o zona.
- **Più profili nello stesso disegno**, ciascuno con parametri di lavorazione indipendenti.
- **Deformazione geometrica** in base agli angoli di montaggio e del tagliente, con conservazione delle curve come archi di ellisse nei casi compatibili. Le geometrie che richiedono un'approssimazione vengono segnalate.
- **Viste 2D e 3D** per controllare i profili e la corrispondenza del filo tra XY e UV.
- **Esportazione DXF** dei profili deformati, nominali o di entrambi, con layer separati.
- **Salvataggio del disegno e dei parametri** per riprendere il lavoro in seguito, con Annulla e Ripeti durante la modifica.

## Dal disegno al profilo da tagliare

1. **Disegno**: importa un DXF oppure crea la geometria nell'editor.
2. **Profilo XY**: scegli la catena che descrive il profilo dell'utensile e controllane verso e posizione.
3. **Spoglie UV**: genera il secondo profilo dagli angoli impostati oppure seleziona un profilo già disegnato.
4. **Deforma / esporta**: imposta gli angoli di montaggio, calcola la deformazione e scarica il risultato in DXF.

La **Guida** integrata, accessibile anche con **F1**, descrive gli strumenti e i parametri.

## File e salvataggio

L'editor accetta file **DXF** con linee, archi, cerchi, polilinee ed ellissi. Quote, tratteggi, testi e blocchi non vengono importati. Il limite predefinito è **20 MB per file**.

Nella versione server i disegni caricati vengono elaborati in memoria sul server, che non li salva su disco. Nella versione WebAssembly vengono elaborati direttamente nel browser e non lasciano mai il computer dell'utente. Usa **Salva** per scaricare il disegno con le impostazioni dei profili e poterlo riaprire in seguito. **Esporta** scarica invece i profili scelti per il flusso di lavorazione.

## Versione WebAssembly (Cloudflare Workers)

`src/FormaWire.Wasm` pubblica l'editor come sito statico: l'editor gira nel browser in WebAssembly e non serve alcun server. Il codice dell'editor resta in `src/FormaWire.Web` ed è condiviso da entrambe le versioni. Il sito è servito da un Worker Cloudflare senza codice, che distribuisce soltanto i file: la configurazione è in [`wrangler.jsonc`](wrangler.jsonc).

L'editor è pubblicato all'indirizzo **[indaloop.ai/formawire](https://indaloop.ai/formawire/)**. Le route in `wrangler.jsonc` assegnano al Worker solo `indaloop.ai/formawire` e `indaloop.ai/formawire/*`: il resto di indaloop.ai non cambia. Il Worker è raggiungibile anche dal suo indirizzo `*.workers.dev`, che rimanda a `/formawire/`.

Al primo accesso il browser scarica l'editor (circa 3 MB compressi) mentre una schermata di caricamento ne mostra l'avanzamento; dalle visite successive l'avvio è quasi immediato.

### Pubblicazione automatica da GitHub

1. Nel pannello Cloudflare apri **Workers & Pages → Create → Workers → Import a repository** e collega il repository GitHub.
2. Imposta il progetto:
   - **Project name**: `formawire`, lo stesso `name` di `wrangler.jsonc`;
   - **Build command**: `bash cloudflare-build.sh`;
   - **Deploy command**: `npx wrangler deploy`;
   - **Root directory**: lasciala vuota (radice del repository).
3. Salva e avvia: ogni push su `main` ricompila e pubblica il sito, comprese le route. `cloudflare-build.sh` installa .NET 8, assente nell'ambiente di build di Cloudflare, e compila il sito in `publish/site/formawire`. La prima build richiede qualche minuto in più.
4. Nelle impostazioni del dominio indaloop.ai disattiva **Rocket Loader**: riordina gli script di avvio dell'editor.

Le route non vanno aggiunte a mano nel pannello: le crea `wrangler deploy` da `wrangler.jsonc`.

Per le anteprime dei branch diversi da `main` puoi attivare **Builds for non-production branches** con il comando `npx wrangler versions upload`.

### Pubblicazione manuale

```sh
bash cloudflare-build.sh
npx wrangler deploy
```

- Per cambiare percorso modifica `SITE_PATH` in `cloudflare-build.sh` e le `routes` in `wrangler.jsonc`. Lo script imposta `<base href>` solo nella copia pubblicata: `src/FormaWire.Wasm/wwwroot/index.html` resta su `/` per lo sviluppo locale.
- Gli indirizzi sconosciuti sotto `/formawire/` caricano l'editor, che mostra la pagina 404. `privacy.html` e `termini.html` sono disponibili anche come `/formawire/privacy` e `/formawire/termini`.
- I [Termini d'uso](src/FormaWire.Wasm/wwwroot/termini.html) e l'[informativa sulla privacy](src/FormaWire.Wasm/wwwroot/privacy.html) di questa versione sono pagine statiche del sito. L'informativa dichiara che nessun dato dei disegni lascia il browser, che non ci sono cookie né strumenti di analisi e che Cloudflare non ci invia registri. Per questo `wrangler.jsonc` tiene disattivati i registri del Worker (`observability`). Se attivi su Cloudflare Workers Logs, Web Analytics, Zaraz, Logpush o altri servizi che raccolgono dati dei visitatori, aggiorna prima l'informativa.
- È l'unico progetto che scarica pacchetti da nuget.org (il runtime WebAssembly di Microsoft); vedi `src/FormaWire.Wasm/nuget.config`.

## Tema grafico

L'interfaccia usa il tema **indaloop**, versione scura. `src/FormaWire.Web/wwwroot/indaloop-tema.css`, i caratteri in `fonts/` (licenza SIL OFL) e i loghi in `logo/` sono copiati senza modifiche dal pacchetto del tema: per aggiornarlo basta sostituirli. `app.css` applica il tema all'editor. I colori tecnici del disegno (profili XY e UV, assi, risultato) non cambiano.

FormaWire è un progetto **indaloop** di **MadTools S.r.l.** · [indaloop.ai](https://indaloop.ai)
