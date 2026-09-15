# NOTICE

## Herkunft

Dieses Repository ist ein Fork von
[HLabSolutions/jellyfin-strm-download](https://github.com/HLabSolutions/jellyfin-strm-download),
abgezweigt vom Commit `019ac08` (Plugin-Version 1.0.3.0).

## Lizenzlage

Das Upstream-Repository enthaelt **keine LICENSE-Datei**. Damit ist die
Lizenzlage des uebernommenen Codes ungeklaert: ohne ausdrueckliche
Lizenzgewaehrung stehen die Rechte an dem Werk allein beim Upstream-Autor.

Konsequenzen fuer diesen Fork:

* Dieser Fork wird **nicht** unter einer selbst gewaehlten Lizenz
  veroeffentlicht. Es wird bewusst keine LICENSE-Datei angelegt.
* Der Fork ist fuer den privaten Gebrauch des Betreibers gedacht.
* Die Klaerung der Lizenzfrage mit dem Upstream-Autor steht aus.

Sobald der Upstream-Autor eine Lizenz benennt oder nachtraegt, ist diese
Datei durch die entsprechende LICENSE-Datei zu ersetzen bzw. zu ergaenzen.

## Eigene Plugin-Identitaet

Damit dieser Fork nicht mit dem Original kollidiert (Jellyfin unterscheidet
Plugins ueber ihre GUID), verwendet er eine eigene Identitaet:

| Feld | Upstream | Fork |
|---|---|---|
| Name | `Strm Download` | `STRM Download Proxy` |
| GUID | `a7f4e6b0-3c2d-4e1a-9b8f-6d5c4b3a2f10` | `ca353088-843b-41cc-932a-c758ab4180ba` |
| Version | `1.0.3.0` | beginnt neu bei `1.0.0.0` |
| targetAbi | `12.0.0.0` | `12.1.0.0` |

Original und Fork koennen dadurch parallel installiert sein; das ist nicht
empfohlen, da beide dieselbe Download-Route abfangen wuerden.
