# Projektnotizen – OpenRA TiberianDawnHD Projektmanagement-Boards

_Diese Datei dokumentiert Aufbau und Konventionen der interaktiven HTML-Boards in diesem Ordner, damit künftige Arbeitssitzungen sich schnell orientieren können._

## Übersicht

Drei eigenständige, interaktive Single-File-HTML-Boards (kein Server, kein Build-Schritt, alles inline). Jedes Board ist mit einer Markdown-Datei verknüpft, die automatisch bei jeder Änderung überschrieben wird (nur Chrome/Edge, via File System Access API).

| Board | HTML-Datei | Verknüpfte .md | Zweck |
|---|---|---|---|
| Kanban | `kanban-board.html` | `KANBAN.md` | Allgemeines Projektmanagement (Spalten/Karten) |
| Techtree Blue | `techtree-blue.html` | `TECHTREE-BLUE.md` | Techtree für Fraktion „Blue" |
| Techtree Red | `techtree-red.html` | `TECHTREE-RED.md` | Techtree für Fraktion „Red" (Klon von Blue, rotes Farbschema) |

## Gemeinsame technische Basis

- Dark-Theme über CSS-Custom-Properties (`:root`), pro Board eigener `--accent`-Wert zur Unterscheidung.
- Persistenz: `localStorage` für Live-Daten (eigener Key pro Board) + `IndexedDB` zur Speicherung des `FileSystemFileHandle` (eigene DB pro Board), damit die Verknüpfung zur .md-Datei Reloads überlebt.
- Auto-Save: bei jeder Änderung wird `buildMarkdown()` aufgerufen und das Ergebnis in die verknüpfte .md-Datei geschrieben.
- Funktioniert nur in Chrome/Edge (File System Access API). Safari/Firefox zeigen eine Warnung, Verknüpfungs-Button ist deaktiviert.
- Drag & Drop für Karten zwischen Spalten (Kanban) bzw. Grid-Zellen (Techtree).

### Farben (Akzentfarbe pro Board, zur visuellen Unterscheidung)

- Kanban: Gold/Gelb `--accent: #d9a82e` (Hover `#b8901f`)
- Techtree Blue: Blau `--accent: #5b8def` (Hover `#4a7cd9`)
- Techtree Red: Rot `--accent: #e2574d` (Hover `#c94a42`)
- Semantisch/funktional (in beiden Techtree-Boards identisch): `--upgrade: #b06bdb` (lila), `--exclusive: #e08a3c` (orange, für Entweder/Oder-Linien)

## Kanban-Board (`kanban-board.html`)

Klassisches Spalten/Karten-Board. `STORAGE_KEY = "openra-kanban-data"`, IndexedDB-DB `"kanban-fs"`, vorgeschlagener Dateiname `"KANBAN.md"`.

## Techtree-Boards (Struktur identisch für Blue & Red)

CSS-Grid-Layout: Zeilen × Tier-Spalten (horizontale Achse = Tier 0–3, vertikale Achse = Reihen).

### Datenmodell

```js
{
  tiers: [{ id, title, desc }, ...],
  rows: [
    { id, group: "units" | "buildings" | "upgrades", title, cells: { [tierId]: [card, ...] } }
  ]
}
```

Jede Karte (`card`):
```js
{ id, title, desc, prereqs: [cardId, ...], excludes: [cardId, ...], limit?: number, kind?: "upgrade" }
```

- **`group`** bestimmt den strukturellen Zeilentyp: `units` = ⚔ Einheit, `buildings` = 🏛 Gebäude, `upgrades` = ⬆ Super-Power-Upgrade (eigene Zeilengruppe unten, z.B. Ages/Tiers).
- **`kind: "upgrade"`** ist ein davon unabhängiges Zusatz-Flag (🔧), primär für Karten innerhalb von `units`/`buildings`-Zeilen: ein lokales Einheiten-/Gebäude-Upgrade (z.B. „Predator Upgrade"). Voll mechanisch – hat wie jede normale Karte `prereqs`/`excludes`/`limit`. Der Unterschied zu einer normalen Einheiten-/Gebäudekarte ist rein konzeptionell: was die Karte bewirkt, steht im freien `desc`-Text, nicht in der Spielmechanik des Boards selbst.
- **`prereqs`** = UND-Voraussetzungen (alle nötig). Auswahl im Editor: `row.group !== "units" || card.kind === "upgrade"` – reine Einheiten-Platzhalterkarten können nie Voraussetzung sein, aber Gebäude, Super-Power-Upgrades und lokale `kind: "upgrade"`-Karten (auch innerhalb von Einheiten-Zeilen) schon.
- **`excludes`** = ENTWEDER/ODER-Ausschluss, bidirektional synchron gehalten (wird bei A gesetzt, automatisch auch bei B gesetzt/entfernt). Visualisiert mit gestrichelten orangen Linien + „ODER"-Label + 🔀-Chip.
- **`limit`** = maximale Bauanzahl, `-1` = unbegrenzt (Default für neue Karten). Wird nur angezeigt/editierbar, wenn das Feld am Karten-Objekt existiert (`typeof card.limit === "number"`).
- Jede `units`/`buildings`-Zeile hat standardmäßig 2 Karten in Tier 0: die Haupt-Platzhalterkarte (Einheit/Gebäude, mit Limit) + eine Platzhalter-Upgrade-Karte (`kind: "upgrade"`, ebenfalls mit Limit). Über die „+ Einheit"/„+ Gebäude"-Buttons lassen sich weitere normale Karten anlegen, über „+ Upgrade" weitere lokale Upgrade-Karten. Bei `upgrades`-Zeilen gibt es nur die eine Haupt-Karte (Super-Power-Upgrade) – kein zweiter Slot, dafür ein einfacher „+ Karte"-Button.
- **Impacting-Anzeige:** Unter jeder Karte mit `row.group === "upgrades" || card.kind === "upgrade"` wird live, nicht gespeichert, eine „🎯 Impacting"-Box berechnet: alle anderen Karten, deren `prereqs` die ID dieser Karte enthalten (Reverse-Lookup). Zeigt „Noch von keiner Karte als Voraussetzung gewählt", wenn leer.
- **Migration (`migrate(d)` in `load()`):** ältere Datenstände mit dem inzwischen entfernten `kind: "ability"` (rein beschreibende Fähigkeits-Karten aus einer früheren Designphase) werden beim Laden automatisch zu vollwertigen `kind: "upgrade"`-Karten konvertiert (Titel/Beschreibung bleiben erhalten, `prereqs`/`excludes`/`limit` werden mit Defaults aufgefüllt). Dadurch bleiben bestehende Live-Daten in localStorage beim Code-Update nutzbar.
- **`tier.desc`** = optionale, editierbare Kurzbeschreibung pro Tier-Spalte, angezeigt als kleine kursive Zeile unter dem Tier-Titel im Spaltenkopf (`renderTierHeader`). Wird wie der Titel per `onchange` gespeichert. Fehlt das Feld (ältere Daten), wird es als leerer String behandelt – keine Migration nötig.

### Visualisierung

- Text-Chips (immer sichtbar): Voraussetzungen mit Icon-Präfix + Tier-Angabe; Ausschlüsse mit 🔀.
- Optionale SVG-Bezier-Pfeile zwischen Karten (toggle „Linien anzeigen"), inkl. Pfeilspitzen-Marker in der jeweiligen Akzentfarbe.
- Limit-Badge auf der Kartentitelzeile, nur sichtbar wenn Limit ≠ -1.
- Lokale Upgrade-Karten (`kind: "upgrade"`): gestrichelter Rahmen (lila, `--upgrade`), 🔧-Icon statt ⚔/🏛.
- Impacting-Box: gepunkteter Rahmen, 🎯-Icon, nicht ziehbar/editierbar/löschbar.

### Storage-Keys / Dateinamen

| | Techtree Blue | Techtree Red |
|---|---|---|
| `STORAGE_KEY` | `openra-techtree-blue-data` | `openra-techtree-red-data` |
| `SHOW_LINES_KEY` | `openra-techtree-blue-show-lines` | `openra-techtree-red-show-lines` |
| IndexedDB-DB | `techtree-blue-fs` | `techtree-red-fs` |
| Verknüpfte Datei | `TECHTREE-BLUE.md` | `TECHTREE-RED.md` |

### Markdown-Export (`buildMarkdown()`)

Direkt unter der „Stufen: …"-Zeile werden, falls vorhanden, die Tier-Kurzbeschreibungen (`tier.desc`) als Liste ausgegeben (`- **Tier X:** Beschreibung`). Danach Gliederung nach Gruppe (`## Einheiten`, `## Gebäude`, `## Upgrades`), darunter pro Zeile (`### Zeilentitel`) die Karten je Tier. Jede Karte bekommt Voraussetzungen, ggf. Ausschlüsse, Limit-Zeile (nur wenn Feld vorhanden); lokale Upgrade-Karten zusätzlich das 🔧-Präfix im Titel. Karten mit `group === "upgrades" || kind === "upgrade"` bekommen zusätzlich eine „Impacting: …"-Zeile (Reverse-Lookup, live berechnet).

## Stand (zuletzt bearbeitet)

Beide Techtree-Boards syntaktisch geprüft (`node --check` auf extrahiertem `<script>`-Block). Letzte Änderungen:
- Tiers auf 4 Stufen „Tier 0"–„Tier 3" umgestellt (vorher „Tier 1"–„Tier 4"); die untere Upgrade-Zeilengruppe bleibt strukturell erhalten, ist aber konzeptionell nur noch für große Super-Power-Upgrades (z.B. Ages/Tiers) gedacht. Das frühere rein beschreibende `kind: "ability"` (✨) wurde komplett entfernt. Stattdessen können Einheiten-/Gebäude-Zeilen jetzt entweder eine normale Einheiten-/Gebäude-Platzhalterkarte oder eine voll mechanische lokale Upgrade-Karte (`kind: "upgrade"`, 🔧, mit eigenen `prereqs`/`excludes`/`limit`) enthalten; was die Karte bewirkt, steht im `desc`-Feld. Lokale Upgrade-Karten sind jetzt auch als Voraussetzung für andere Karten wählbar (Prereq-Regel erweitert auf `row.group !== "units" || card.kind === "upgrade"`), und die „🎯 Impacting"-Anzeige wurde entsprechend erweitert. Eine `migrate(d)`-Funktion in `load()` konvertiert bestehende Live-Daten mit altem `kind: "ability"` automatisch zu `kind: "upgrade"`, damit echte Browser-Daten beim nächsten Laden nicht verloren gehen.
- Im Tier-Spaltenkopf (`renderTierHeader`) gibt es jetzt eine zweite, kleine editierbare Zeile (`tier.desc`) unter dem Titel – eine kurze, kursive Kurzbeschreibung pro Stufe. Wird wie der Titel per `onchange` in `data.tiers` gespeichert und im Markdown-Export direkt unter der „Stufen: …"-Zeile mit ausgegeben.

In beiden Techtree-Boards (Daten, Render-Logik, Editor-UI, CSS, Legende, Markdown-Export) identisch umgesetzt. Die verknüpften .md-Dateien (TECHTREE-BLUE.md/TECHTREE-RED.md) wurden bewusst nicht manuell überschrieben, da sie bereits echte Live-Daten des Nutzers enthalten – sie werden beim nächsten Öffnen der Boards automatisch durch die Auto-Save-Funktion (inkl. Migration) aktualisiert.

## Etablierte Arbeitsweise für künftige Änderungen

- Neue Boards/Features in einem Board zuerst in `techtree-blue.html` umsetzen, dann identische Änderungen 1:1 nach `techtree-red.html` übertragen (nur Farben/Keys bleiben unterschiedlich).
- Nach jeder JS-Änderung: `<script>`-Block per Regex extrahieren, mit `node --check` auf Syntaxfehler prüfen.
- Zugehörige .md-Datei nach Datenmodell-Änderungen manuell synchron halten (auch wenn das Board sie beim nächsten Live-Edit automatisch überschreibt).
