# FormaWire

FormaWire è un editor CAD nel browser per preparare e deformare i profili delle placchette da lavorare con l'elettroerosione a filo. Dal disegno nominale ricava i profili XY e UV tenendo conto delle spoglie e degli angoli di montaggio dell'utensile.

**[Apri FormaWire](https://madnetai.com/formawire/)**

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

## Versione WebAssembly (Cloudflare Pages)

`src/FormaWire.Wasm` pubblica l'editor come sito statico: l'editor gira nel browser e non serve alcun server. Il codice dell'editor resta in `src/FormaWire.Web` ed è condiviso da entrambe le versioni.

```sh
dotnet publish src/FormaWire.Wasm -c Release -o publish/wasm
npx wrangler pages deploy publish/wasm/wwwroot --project-name formawire
```

Al primo accesso il browser scarica l'editor (circa 3 MB compressi) mentre una schermata di caricamento ne mostra l'avanzamento; dalle visite successive l'avvio è quasi immediato.

- Il sito è pensato per la radice del dominio. Per pubblicarlo sotto un percorso, ad esempio `/formawire/`, modifica `<base href>` in `src/FormaWire.Wasm/wwwroot/index.html`.
- Per far compilare il sito a Cloudflare Pages da Git, l'ambiente di build deve installare .NET. Comando di build: `curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && bash dotnet-install.sh --channel 8.0 --install-dir .dotnet && .dotnet/dotnet publish src/FormaWire.Wasm -c Release -o publish/wasm`; cartella di output: `publish/wasm/wwwroot`.
- Disattiva Rocket Loader per il dominio: riordina gli script di avvio dell'editor.
- È l'unico progetto che scarica pacchetti da nuget.org (il runtime WebAssembly di Microsoft); vedi `src/FormaWire.Wasm/nuget.config`.

FormaWire è un progetto di **MadTools S.r.l. / MadNetAI**.
