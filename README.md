# Strm Download

<p align="center">
  <img src="images/logo.png" alt="Strm Download logo" width="128" height="128">
</p>

Plugin per [Jellyfin](https://jellyfin.org) che rende utilizzabile il pulsante
**Download** nativo anche sugli elementi risolti da un file `.strm`.

## Il problema

Un file `.strm` è un piccolo file di testo che contiene un URL al posto del
file video vero e proprio. Jellyfin lo riproduce correttamente, ma il suo
endpoint di download nativo (`Items/{itemId}/Download`) scarica semplicemente
il file così com'è su disco — quindi, per un elemento `.strm`, il client
riceve poche righe di testo invece del video, e app come Moonfin rifiutano il
file con un errore tipo *"Downloaded file signature does not match extension"*.

## Cosa fa questo plugin

Intercetta la richiesta di download nativa **prima** che raggiunga il
controller di Jellyfin: se l'elemento è un file `.strm`, legge l'URL al suo
interno e fa da proxy, scaricando il contenuto reale dal server remoto e
inoltrandolo al client (con supporto alle richieste `Range`, per il resume).
Per tutto il resto (elementi non-`.strm`, permessi, autenticazione) il
comportamento nativo di Jellyfin resta invariato.

Funziona per **qualsiasi client** — Jellyfin Web, app mobile (Moonfin,
Findroid, Infuse, ecc.), Kodi — perché tutti chiamano lo stesso URL nativo;
non serve alcuna modifica lato client.

## Requisiti

- Jellyfin **12.0** o successivo (usa API introdotte in questa versione;
  non è compatibile con le serie 10.x).

## Installazione

1. In Jellyfin: **Dashboard → Plugin → Repository → aggiungi repository**
   con questo URL:

   ```
   https://raw.githubusercontent.com/HLabSolutions/jellyfin-strm-download/master/manifest.json
   ```

2. Vai su **Catalogo**, cerca **Strm Download**, installa.
3. Riavvia il server.

In alternativa, per un'installazione manuale: scarica lo zip dell'
[ultima release](https://github.com/HLabSolutions/jellyfin-strm-download/releases/latest)
ed estrailo in `plugins/StrmDownload_<versione>/` nella cartella dati di
Jellyfin, poi riavvia il server.

## Configurazione

Dashboard → Plugin → **Strm Download**: un unico interruttore
("Abilita l'intercettazione del download nativo") permette di disattivare il
plugin senza disinstallarlo, per tornare al comportamento predefinito di
Jellyfin in caso di problemi.

## Limitazioni note

- Il proxy fa passare il file dal server (remoto → server Jellyfin → client):
  usa banda e CPU del server, non è un semplice redirect.
- L'intercettazione avviene molto presto nella pipeline HTTP di Jellyfin,
  prima delle eventuali restrizioni di accesso per rete/IP configurate sul
  server: per i soli download di file `.strm`, quelle restrizioni non si
  applicano. Tutti gli altri controlli (autenticazione, permessi di download
  dell'utente) restano invariati.

## Build da sorgente

```
dotnet publish -c Release -o ./artifact
```

Il pacchetto per una release (zip installabile) contiene
`Jellyfin.Plugin.StrmDownload.dll`, `meta.json` e `logo.png` allo stesso
livello.
