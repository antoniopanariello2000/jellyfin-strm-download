# Abnahme

Stand: 15.09.2026. Zielumgebung: Jellyfin **12.1.0** im Docker, `.strm`-URLs
auf eine NzbDAV2-Instanz im LAN, Reverse Proxy nach außen.

## Status der Tests

Die Tests A1–A8 und die Kriterien 1–7 aus dem Hauptauftrag laufen gegen die
**Produktivinstanz** und wurden **nicht** ausgeführt — die Build-Umgebung, in
der dieser Fork entstanden ist, hat keinen Zugriff auf den Jellyfin-Server,
das Testitem oder ein Android-Gerät. Sie sind vom Auftraggeber
nachzuvollziehen. Die Befehle dafür stehen unten.

Was ohne die Instanz verifiziert werden konnte, wurde verifiziert:

| Prüfung | Ergebnis |
|---|---|
| `dotnet build -c Release --warnaserror` gegen `Jellyfin.Controller`/`Jellyfin.Model` **12.1.0** | 0 Warnungen, 0 Fehler |
| Assembly gegen die 12.1.0-Laufzeit geladen, alle Typen und Methodensignaturen aufgelöst | keine `TypeLoadException`; `Plugin` implementiert `IHasWebPages`, `PluginServiceRegistrator` implementiert `IPluginServiceRegistrator`, `configPage.html` ist eingebettet |
| `AuthorizationInfo.IsAuthenticated` / `.IsApiKey` / `.User` / `.UserId` in 12.1.0 | vorhanden |
| `ActivityLog` in 12.1.0 | `Jellyfin.Database.Implementations.Entities.ActivityLog`, ctor `(string, string, Guid)`, `ItemId`, `ShortOverview` |
| `IHeaderDictionary.ContentRange` als typisierte Property | vorhanden, `Headers["Content-Range"]`-Ausweichweg nicht nötig |
| Content-Range-Logik gegen die Parser-Regeln des Android-Clients | A1-, A2-, A3-Formen inkl. fehlendem Header, `*`-Total, begrenztem und Suffix-Range |
| Endungsermittlung gegen die gemessene NzbDAV2-URL-Form | `?extension=mkv` → `.mkv`; `?extension=strm` und ein Traversal-Versuch → `.bin` |
| Release-Paket | ZIP mit DLL, `meta.json`, `logo.png`; `meta.json` deserialisiert mit Jellyfins eigenem `PluginManifest` und `JsonDefaults.Options` |

## Client-Vertrag: offizielle Jellyfin-App für Android

Verifiziert an `jellyfin/jellyfin-android`,
`app/src/main/java/org/jellyfin/mobile/downloads/`.

1. `DownloadQueue.prepareMainFile` holt die URL über
   `api.libraryApi.getDownloadUrl(itemId)` — den nativen Endpunkt.
2. `FileDownloader.downloadAndSave` setzt `rangeStart = to.statSize`, bei einem
   neuen Download also 0.
3. `FileDownloader.download` sendet **immer** `Range: bytes=$rangeStart-`, auch
   beim ersten Versuch, mit `Authorization`-Header (kein Token im Query-String).
4. Die Antwort wird so ausgewertet:

   ```kotlin
   private fun Response.getContentRange() = when (code) {
       200 -> requireNotNull(header("Content-Length")).let(ContentRange::fromContentLengthHeader)
       206, 416 -> requireNotNull(header("Content-Range")).let(ContentRange::fromContentRangeHeader)
       else -> error("Invalid response code $code")
   }
   ```

Daraus folgt für den Server:

* Auf `Range: bytes=0-` ist **206 mit gültigem `Content-Range`** oder **200 mit
  `Content-Length`** zulässig. Eine 206 ohne `Content-Range` bricht den
  Download ab, bevor ein Byte geschrieben wird.
* `Content-Range` muss `bytes <start>-<end>/<total>` sein, `total` numerisch.
  `*` als Gesamtgröße wird abgelehnt.
* Bei Wiederaufnahme muss `start` dem angefragten `rangeStart` entsprechen.
* Andere Statuscodes als 200, 206 und 416 sind ein harter Abbruch ohne
  Wiederholung — ein 502 aus dem Plugin also auch.

Wie das Plugin diesen Vertrag erfüllt, steht in
[`Content-Range`](#content-range-garantie) weiter unten.

### Content-Range-Garantie

`StrmDownloadInterceptorMiddleware` entscheidet über `Content-Range`, **bevor**
irgendetwas in die Antwort geschrieben wird:

| Antwort der Gegenstelle | Was der Client bekommt |
|---|---|
| 206 mit `bytes <start>-<end>/<total>` | unverändert durchgereicht |
| 206 ohne `Content-Range` **und** Client hat `bytes=0-` (oder gar keinen Range) angefragt | rekonstruiert als `bytes 0-<len-1>/<len>` aus `Content-Length`, Warnung im Log |
| 206 mit `*` als Total, sonst wie oben | ebenso rekonstruiert |
| 206, die sich nicht vervollständigen lässt | **502**, Fehler im Log — der Client sieht nie eine unbrauchbare 206 |
| 200 mit `Content-Length` | unverändert durchgereicht |
| 416 mit `bytes */<total>` | unverändert durchgereicht (so erkennt der Client einen bereits vollständigen Download) |
| 416 ohne `Content-Range` | durchgereicht, Warnung im Log |

Der Header wird explizit mit `InvariantCulture` formatiert, nicht über
`ContentRangeHeaderValue.ToString()`, damit das Format unabhängig von der
Server-Kultur festliegt.

## Was serverseitig nicht lösbar ist

`DownloadQueue.prepareMainFile` bildet den lokalen Dateinamen so:

```kotlin
fileName = downloadWithFiles.download.item.path?.replace(Regex("^.*[\\\\/]"), "") ?: error("Missing item path")
```

Der Name stammt also aus `item.path` des Server-DTOs — dem Pfad der
`.strm`-Datei. `Content-Disposition` wird nicht ausgewertet. Die Datei heißt
auf dem Gerät folglich `… S01E05 … .strm`, **unabhängig davon, was der Server
sendet**.

Das ist bewusst **nicht** umgangen worden. Richtiger Weg, falls die App die so
abgelegte Datei nicht abspielt: ein Issue in `jellyfin/jellyfin-android` mit
dem Vorschlag, den Dateinamen aus `Content-Disposition` oder aus
`item.container` abzuleiten.

Die serverseitige Namensbildung (T6) bleibt trotzdem gültig — für Clients, die
`Content-Disposition` respektieren.

## Testbefehle

```bash
KEY='<token>'
ITEM='5cd5e807bcaa4e0f53054003c8df2b70'
AUTH="Authorization: MediaBrowser Token=\"$KEY\""
HOST='http://192.168.0.20:8096'
```

Nützliches Erkennungsmerkmal: Antworten, die das Plugin selbst erzeugt, tragen
**kein** `X-Response-Time-ms`. Ist der Header da, hat die Middleware den
Request durchgereicht.

**A1 — Erstdownload, exakt der erste Request der App**

```bash
curl -sS -D - -o /dev/null --max-time 30 -H "$AUTH" -H 'Range: bytes=0-' "$HOST/Items/$ITEM/Download"
```

Erwartet: 206 mit `Content-Range: bytes 0-3169382204/3169382205`, oder 200 mit
`Content-Length: 3169382205`. Eine 206 ohne `Content-Range` ist ein Fehlschlag.

**A2 — Wiederaufnahme**

```bash
curl -sS -D - -o /dev/null --max-time 30 -H "$AUTH" -H 'Range: bytes=1000000-' "$HOST/Items/$ITEM/Download"
```

Erwartet: 206, `Content-Range` beginnt bei exakt 1000000, `total` unverändert.

**A3 — Bereits vollständig**

```bash
curl -sS -D - -o /dev/null --max-time 30 -H "$AUTH" -H 'Range: bytes=3169382205-' "$HOST/Items/$ITEM/Download"
```

Erwartet: 416 **mit** `Content-Range`.

**A4 — Auth** (gegen `$HOST` und gegen die externe Domain)

```bash
curl -sS -D - -o /dev/null --max-time 30 -H 'Range: bytes=0-' "$HOST/Items/$ITEM/Download"
```

Erwartet: 401, keine Nutzdaten, kein `Content-Range`.

**A5 — Berechtigung.** Token eines Users ohne `EnableContentDownloading`: 403.

**A6 — Regression.** Derselbe Aufruf gegen ein Item ohne `.strm`-Pfad verhält
sich wie ohne Plugin. Ein Item mit `LocationType: Virtual` wird weiterhin an
Jellyfin durchgereicht und ergibt dort 400 — korrekt, kein Bug.

**A7 — Gerätetest.** Dieselbe Episode in der offiziellen Android-App bis 100 %
herunterladen. Zu dokumentieren: erreichte Dateigröße, lokaler Dateiname, und
ob die App die Datei offline abspielt.

**A8 — Abbruch und Fortsetzung.** Download in der App bei ~10 % abbrechen und
erneut starten. Die App sendet `Range: bytes=<bisher>-`; der Download muss dort
weiterlaufen und die Datei am Ende die volle Größe haben.

### Ergebnisse A7 / A8

Noch nicht ausgeführt — siehe oben. Hier einzutragen:

| Test | Dateigröße | Lokaler Dateiname | Offline abspielbar | Notiz |
|---|---|---|---|---|
| A7 | | | | |
| A8 | | | | |

## Rollout

1. `dotnet publish -c Release -o ./artifact` oder das ZIP aus der CI verwenden.
2. DLL auf der Instanz austauschen, Jellyfin neu starten.
3. A1–A6 per curl.
4. Erst danach A7 und A8 am Gerät.

Bis T1 im Einsatz ist, darf das Plugin auf der extern erreichbaren Instanz
nicht aktiv sein; `EnableNativeDownloadHook` = aus genügt zum Abschalten.
