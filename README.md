# STRM Download Proxy

<p align="center">
  <img src="images/logo.png" alt="STRM Download Proxy Logo" width="128" height="128">
</p>

Plugin für [Jellyfin](https://jellyfin.org), das den nativen **Download**-Button
auch für Elemente nutzbar macht, die aus einer `.strm`-Datei aufgelöst wurden.

> Fork von [HLabSolutions/jellyfin-strm-download](https://github.com/HLabSolutions/jellyfin-strm-download)
> (Ausgangscommit `019ac08`, Version 1.0.3.0) mit eigener Plugin-GUID.
> Zur Lizenzlage siehe [NOTICE.md](NOTICE.md).

## Das Problem

Eine `.strm`-Datei ist eine kleine Textdatei, die anstelle des eigentlichen
Videos eine URL enthält. Jellyfin spielt sie korrekt ab, aber der native
Download-Endpunkt `GET /Items/{itemId}/Download` liefert schlicht die Datei
aus, wie sie auf dem Datenträger liegt — für ein `.strm`-Element bekommt der
Client also ein paar Zeilen Text statt des Videos.

## Was das Plugin tut

Es fängt die native Download-Anfrage ab, **bevor** sie Jellyfins Controller
erreicht: Ist das Element eine `.strm`-Datei, wird die darin enthaltene URL
gelesen und der Inhalt vom entfernten Server zum Client durchgereicht
(inklusive `Range`-Unterstützung für Resume). Für alles andere
(Nicht-`.strm`-Elemente) bleibt Jellyfins natives Verhalten unverändert.

Das funktioniert für **jeden Client** — Jellyfin Web, Mobil-Apps, Kodi —, weil
alle dieselbe native URL aufrufen; clientseitige Änderungen sind nicht nötig.

## Voraussetzungen

- Jellyfin **12.1.0** (Zielversion, `targetAbi` 12.1.0.0). Gebaut gegen die
  NuGet-Pakete `Jellyfin.Controller`/`Jellyfin.Model` 12.1.0; nicht kompatibel
  mit der 10.x-Reihe.

## Installation

1. In Jellyfin: **Dashboard → Plugins → Repositories → Repository hinzufügen**
   mit dieser URL:

   ```
   https://raw.githubusercontent.com/cosmicflow2512/jellyfin-strm-download/master/manifest.json
   ```

2. Unter **Katalog** nach **STRM Download Proxy** suchen und installieren.
3. Server neu starten.

Alternativ manuell: das ZIP der
[neuesten Release](https://github.com/cosmicflow2512/jellyfin-strm-download/releases/latest)
herunterladen und nach `plugins/StrmDownload_<version>/` im Jellyfin-Datenordner
entpacken, dann den Server neu starten.

## Konfiguration

Dashboard → Plugins → **STRM Download Proxy**:

| Option | Default | Bedeutung |
|---|---|---|
| `EnableNativeDownloadHook` | an | Schaltet das Abfangen ab, ohne das Plugin zu deinstallieren. Aus = exakt Jellyfins Standardverhalten. |
| `StreamIdleTimeoutSeconds` | `60` | Bricht die Übertragung ab, wenn die Gegenstelle so lange keine Daten mehr liefert. Die Zeit wird nach jedem empfangenen Datenblock zurückgesetzt (Idle-, kein Gesamt-Timeout). `0` deaktiviert die Überwachung. |
| `MaxConcurrentDownloads` | `0` | Begrenzt die Zahl gleichzeitig durchgereichter Downloads. Darüber hinausgehende Anfragen werden mit `503` und `Retry-After` abgewiesen, nicht eingereiht. `0` = unbegrenzt. |

## Bekannte Einschränkungen

- **Die offizielle Android-App benennt die Datei lokal trotzdem `.strm`.**
  `DownloadQueue.prepareMainFile` bildet den Dateinamen aus `item.path` des
  Server-DTOs und wertet `Content-Disposition` gar nicht aus. Das ist
  serverseitig nicht lösbar und wurde bewusst nicht umgangen; Details und der
  vorgeschlagene Weg (Issue in `jellyfin/jellyfin-android`) stehen in
  [`docs/abnahme.md`](docs/abnahme.md). Die serverseitige Namensbildung greift
  für Clients, die `Content-Disposition` respektieren.
- Der Proxy leitet die Datei über den Server (Remote → Jellyfin-Server →
  Client): Das kostet Bandbreite und CPU des Servers, es ist kein Redirect.
- Das Abfangen geschieht sehr früh in Jellyfins HTTP-Pipeline, vor eventuell
  konfigurierten netzwerk-/IP-basierten Zugriffsbeschränkungen: Für
  `.strm`-Downloads greifen diese Beschränkungen nicht. Authentifizierung und
  die Download-Berechtigung des Benutzers prüft das Plugin dagegen selbst.

## Verhalten gegenüber Clients

Die offizielle Jellyfin-App für Android sendet bei **jedem** Download
`Range: bytes=<offset>-` — auch beim ersten Versuch mit Offset 0 — und liest
die Antwort mit `requireNotNull(header("Content-Range"))` für 206 und 416. Eine
206 ohne diesen Header bricht den Download ab, bevor ein Byte geschrieben wird.

Das Plugin stellt deshalb sicher, dass eine 206 ohne verwendbares
`Content-Range` den Client nie erreicht: Wo der Header sich aus
`Content-Length` eindeutig rekonstruieren lässt, wird er rekonstruiert,
andernfalls antwortet das Plugin mit 502 statt mit einer unbrauchbaren
Teilantwort. Die vollständige Tabelle steht in
[`docs/abnahme.md`](docs/abnahme.md).

## Abnahme

Die Tests gegen eine laufende Instanz (curl-Befehle A1–A6, Gerätetests A7/A8)
und ihr aktueller Stand stehen in [`docs/abnahme.md`](docs/abnahme.md).

## Build aus dem Quellcode

```
dotnet build -c Release
dotnet publish -c Release -o ./artifact
```

Das Release-Paket (installierbares ZIP) enthält
`Jellyfin.Plugin.StrmDownload.dll`, `meta.json` und `logo.png` auf derselben
Ebene.
