# UI: layout and menus

How the main window adapts to its size and where every command lives. Code: `Views/MainWindow.axaml`,
`MainWindow.Layout.cs` (breakpoints), `MainWindow.Menu.cs` ("Mehr"), `MainWindow.EmptyState.cs`, styles in
`App.axaml`. The self-test (`SelfTest.Layout.cs`) checks all of it and saves `layout-*.png`, `menu-*.png`, and
`empty-*.png` probes.

## Sizes and breakpoints

All widths are in DIP (device-independent pixels; physical pixels divided by the Windows scaling).

| Window width | Sidebar | Detail column | Header |
| --- | --- | --- | --- |
| ≥ 1180 | expanded (240) | open or closed as saved (`overview.details`, Strg+I) | buttons with labels |
| < 1180 | icons only (48); ☰ opens it as an overlay | closed; Strg+I or the header toggle opens it until the window is wide again | buttons with labels while the header is ≥ 700 wide, otherwise icons only (tooltips stay) |

- **Minimum window size 880 × 480.** A 1366 × 768 laptop at 150 % has about 910 × 480 DIP usable (taskbar
  subtracted), so 900 × 560 would not fit there.
- **Start size:** 1320 × 840, but at most 92 % of the screen's working area.
- **Tiles:** `SectionWrapPanel` fits as many tiles in a row as possible at 240 px or more and shares the row
  evenly. A tile is at most 320 px wide. Group headers always span the row.
- **List:** below 900 px the group and tag columns hide, below 640 px also "zuletzt verbunden". Name, type, host,
  and description always show; the description column takes the rest of the width and trims.
- **Dialogs** (FluentAvalonia `FAContentDialog`) stay inside the window and scroll their content at every size
  down to the minimum (checked for the connection editor).
- **Detail column:** still a column, never an overlay (see the airspace notes in `plan-source.txt`). On narrow
  windows it closes by itself instead.

Thresholds are constants in `MainWindow.Layout.cs` (`WideLayoutWidth`, `NarrowHeaderWidth`, `CompactListWidth`,
`NarrowListWidth`). The sidebar threshold is also set in `MainWindow.axaml` (`ExpandedModeThresholdWidth`).

## Header

No logo and no app name inside the window: the title bar already shows both. The header has two rows:

1. Where you are: the filter title ("Alle Verbindungen", a group path, a tag) and "n Verbindungen · m online".
2. Search (stretches up to 560 px), view switch Kacheln/Liste, detail column toggle, sync status (only while sync
   is on), **Verbinden**, **Neue Verbindung** (accent), **Mehr** (…).

## Menus

Labels are German. An ellipsis ("…") means a dialog follows. Shortcuts show next to the entry (German key
names: Strg, Umschalt, Entf, Eingabe).

**Mehr** (header, `MainWindow.Menu.cs`)

| Entry | Shortcut |
| --- | --- |
| Neue Gruppe … | Strg+Umschalt+N |
| Schnellstart … (tooltip: the global hotkey) | Strg+K |
| Ansicht › Kacheln / Liste | Strg+1 / Strg+2 |
| Ansicht › Detailbereich | Strg+I |
| Ansicht › Online-Status aktualisieren | F5 |
| Ansicht › Live-Vorschau › Aus, 10 s, 30 s, 1 min, 5 min | |
| Importieren … | |
| Sicherung exportieren (JSON) … | |
| Synchronisierung … | |
| Remote-Tools … (paths, TeamViewer sync, AnyDesk import) | |
| Nach Updates suchen | |
| Über Remote-Manager … (version, update source) | |

**Connection** (right-click on a tile or row; works on the selection)

Verbinden (Eingabe) · Dateiübertragung · Passwort kopieren — Bearbeiten … (F2) · Duplizieren · In Gruppe
verschieben … · Zu Favoriten hinzufügen / Aus Favoriten entfernen — Löschen … (Entf)

Entries that do not apply (several selected, no stored password, no file transfer) are hidden.

**Group** (right-click on a section header or a sidebar group)

Verbinden (n) — Neue Verbindung hier … · Neue Untergruppe … — Gruppe bearbeiten … · Gruppe löschen … (Entf on a
selected sidebar group). "Keine Gruppe" has only the first three.

**Other shortcuts:** Strg+N new connection, Strg+F search.

## Empty states

| Situation | Text | Action |
| --- | --- | --- |
| No connections at all | Noch keine Verbindungen | Neue Verbindung, Importieren … |
| Search without results | Keine Treffer für „…“ | Suche löschen |
| Empty group | Diese Gruppe ist leer | Neue Verbindung hier … |
| Favorites empty | Noch keine Favoriten | (hint: the star) |
| Recent empty | Noch nichts verbunden | |

## Theme

Light and dark follow Windows. Colors come from FluentAvalonia theme resources (`TextFillColorSecondaryBrush`,
`CardBackgroundFillColorDefaultBrush`, `SystemFillColorCriticalBrush`, ...). The app's only own color resource is
`FavoriteBrush` (the favorite star; one shade per theme, `App.axaml`). Type badge colors (RDP blue, SSH green, ...)
are the handlers' accent colors with white text and read the same in both themes. Text styles: `pageTitle` (22,
semibold), `subtitle` (16, semibold), `caption` (12, secondary color).
