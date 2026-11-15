# UI: design, layout, and menus

How Remote-Manager looks, how the main window adapts to its size, and where every command lives. Code:
`Theme/` (the design system), `Views/MainWindow.axaml` with its partial classes (`MainWindow.Sidebar.cs`,
`.Filters.cs`, `.Appearance.cs`, `.Layout.cs`, `.Menu.cs`, `.EmptyState.cs`, `.Tray.cs`, `.Launcher.cs`),
`Connections/SessionBar` (session windows). The self-test checks all of it and saves pictures (`layout-*.png`,
`redesign-*.png`, `form-*.png`, `dialogs-*.png`, `palette-*.png`, `menu-*.png`, `empty-*.png`); `--tour` fills a
scratch data folder with demo connections and saves the pictures used in the README (`tour-*.png`).

## Design language: "Leitstand"

A control room: calm graphite surfaces, one signal color, status that reads like panel lamps, DIN lettering.

| Part | What | Where |
| --- | --- | --- |
| Surfaces | `RmBaseBrush` (window), `RmPanelBrush` (content), `RmCardBrush` (cards), `RmInsetBrush` (wells), strokes `RmStrokeBrush` / `RmStrokeStrongBrush`; text `RmTextBrush`, `RmTextSecondaryBrush`, `RmTextTertiaryBrush` | `Theme/Palette.axaml`, one set per theme |
| Signal color | Amber by default; marks the primary action (one per view), selection, and focus. `RmAccentBrush`, `RmAccentSoftBrush`, `RmAccentTextBrush`, `RmOnAccentBrush` | written at runtime by `Services/Appearance` |
| Status lamps | `RmOnlineBrush`, `RmOfflineBrush`, `RmUnknownBrush`, `RmPendingBrush`, `RmLiveBrush` (`Ellipse.lamp`) | `Theme/Palette.axaml` |
| Type | Bahnschrift (DIN, ships with Windows) for titles, names, section headings, and figures: `pageTitle`, `heading`, `subtitle`, `name`, `section`, `display`. Body text is the system face. Hosts, IPs, IDs, paths, fingerprints: `mono` (Cascadia Mono). Secondary text: `caption`, `hint`, `muted`, `faint` | `Theme/Styles.axaml` |
| Forms | A visible label above every field (`fieldLabel`); a placeholder only gives an example. Sections start with a heading and a hairline (`sectionHead`). Remarks are `Border.note` (also `warning`, `error`) | `Theme/Styles.axaml`, `Views/FormFields.cs`, `Views/DialogKit.cs` |
| Small parts | `typeChip` (connection type in its color), `tag`, `count`, `keycap`, `pill` (label on a picture), `card`, `dialogCard`, `segmented` with `segment`, `chip`, `Button.ghost` / `icon` / `link` / `danger` | `Theme/Styles.axaml`, templates in `Theme/Controls.axaml` |
| Connection picture | Until a session leaves a real preview, a tile shows a panel of lamps lit in a pattern derived from the connection id (`Controls/HostGlyph`), in the type's color. The same connection always gets the same pattern | `Controls/HostGlyph.cs` |

Rules that keep it one system: colors only through `{DynamicResource}` so both themes work; no decorative
gradients or shadows; copy is German, informal ("du"), and says what happens. List rows, tiles, and the app's own
buttons use the app's templates (`RmBareItem`, `RmRowItem`, `RmGhostButton`, ...), so their hover, pressed, and
selected states do not depend on the inner parts of FluentAvalonia's controls. FluentAvalonia still provides
inputs, menus, dialogs, and the accent button.

**Darstellung** ("Mehr" menu, also in the quick launcher): theme (wie Windows, hell, dunkel), signal color
(Bernstein, Polarblau, Minze, Flieder, Koralle, or the Windows accent color), tile size. Saved as
`appearance.theme`, `appearance.accent`, `appearance.tileSize` and applied at once. Text on the signal color is
chosen by contrast (at least 4.5 : 1, checked for every preset in both themes by the self-test).

## Window

The app draws its own title bar (`ExtendClientAreaToDecorationsHint`, frame `RmWindowDecorations` in
`Theme/Controls.axaml`): sidebar toggle, icon and name as a window frame shows them, the search field in the
middle, the quick launcher button with its keys, then the three caption buttons. Nothing below the title bar
repeats logo or name. The bar is the drag area.

The overview remembers its place, size, and monitor (`window.main`). Session windows open on the monitor the
overview is on. The environment variable `REMOTE_MANAGER_SCREEN` ("secondary", a 1-based screen number, or part of
a display name) forces the overview onto that screen; test runs use it to stay off the monitor someone works on.

## Sizes and breakpoints

All widths are in DIP (device-independent pixels; physical pixels divided by the Windows scaling).

| Window width | Sidebar | Detail column | Header |
| --- | --- | --- | --- |
| ≥ 1180 | expanded (240), or icons only (54) if folded with ☰ / Strg+B (saved as `overview.sidebar`) | open or closed as saved (`overview.details`, Strg+I) | buttons with labels |
| < 1180 | icons only (54); ☰ opens it over the content until an entry is chosen or the content is clicked | closed; Strg+I or the header toggle opens it until the window is wide again | buttons with labels while the header is ≥ 700 wide, otherwise icons only (tooltips stay) |

- **Minimum window size 880 × 480.** A 1366 × 768 laptop at 150 % has about 910 × 480 DIP usable (taskbar
  subtracted), so 900 × 560 would not fit there.
- **Start size:** 1320 × 840, but at most 92 % of the screen's working area.
- **Header:** the page title comes first. Below 770 px of header width (`RoomyHeaderWidth`; the start size with the
  detail column open gives 707) the S/M/L switch hides so the title is not cut off; the tile size stays in "Mehr".
- **Tiles:** `SectionWrapPanel` fits as many tiles in a row as possible and shares the row evenly. Three sizes
  (S/M/L in the header): small 214 to 290 px without a picture (lamp, name, host, type), medium 254 to 334 px,
  large 334 to 430 px with a taller picture. Group headers always span the row.
- **List:** below 900 px the group and tag columns hide, below 640 px also "zuletzt verbunden". Name, type, host,
  and description always show; the description column takes the rest of the width and trims.
- **Dialogs** (FluentAvalonia `FAContentDialog`) stay inside the window and scroll their content at every size
  down to the minimum (checked for the connection editor, the group editor, and the import summary).
- **Detail column:** still a column, never an overlay (see the airspace notes in `plan-source.txt`). On narrow
  windows it closes by itself instead.

Thresholds are constants in `MainWindow.Layout.cs` (`WideLayoutWidth`, `NarrowHeaderWidth`, `RoomyHeaderWidth`,
`CompactListWidth`, `NarrowListWidth`).

## Sidebar

A plain list of rows (`SidebarEntry`), rebuilt whenever groups, tags, or counts may have changed: Alle
Verbindungen, Favoriten, Zuletzt verwendet, Aktive Sitzungen (a lamp on the icon while sessions are open), the
group tree (a collapsed group hides its subgroups; one saved flag for sidebar and overview), then the tags. Every
entry shows how many connections it holds. "+" next to "Gruppen" adds a group. Groups and "Alle Verbindungen" are
drop targets. The foot of the sidebar says where device sync stands; a click opens its dialog.

## Header

1. Where you are: the filter title, and the primary actions: tile size (S/M/L), Kacheln/Liste, detail column
   toggle, sync status (only while sync is on), **Verbinden**, **Neue Verbindung** (accent), **Mehr** (…).
2. How it is doing: "n Verbindungen · m online", a bar with the shares of online, offline, and unknown, and the
   offline and unknown counts as lamps with numbers (they are what needs a look).
3. Filter chips: "Alle" and one chip per connection type that occurs in the current view, each with its count,
   plus "Nur online". They narrow the view further and are not saved: a forgotten filter must not hide connections
   after the next start. When the chips hide everything, the empty state offers "Filter zurücksetzen".

## Tiles, list, detail column

- **Tile:** picture (the last session picture, live while a session is open, or the lamp panel), status with the
  answer time ("Online 12 ms"), type chip, "Live" or "Sitzung offen" badge, a round connect button under the
  pointer, name, host, description, tags, and when it was last used ("Zuletzt vor 2 Std.").
- **Detail column:** the picture with status and type, name, description, tags, **Verbinden** with edit and copy
  password next to it, the answer times of the last status checks as a line (a check without an answer leaves a
  gap; only for types whose check measures one), host with a copy button, logon, group, status, last connection,
  start parameters, notes (saved when focus leaves), history.
- **Answer time:** the time a TCP connect to the port took (`StatusChangedEventArgs.Latency`, probes with
  `MeasuresLatency`). Remote tools and connections behind a jump host have none.

## Menus

Labels are German. An ellipsis ("…") means a dialog follows. Shortcuts show next to the entry (German key
names: Strg, Umschalt, Entf, Eingabe).

**Mehr** (header, `MainWindow.Menu.cs`)

| Entry | Shortcut |
| --- | --- |
| Neue Gruppe … | Strg+Umschalt+N |
| Schnellstart … (tooltip: the global hotkey) | Strg+K |
| Ansicht › Kacheln / Liste | Strg+1 / Strg+2 |
| Ansicht › Seitenleiste | Strg+B |
| Ansicht › Detailbereich | Strg+I |
| Ansicht › Online-Status aktualisieren | F5 |
| Ansicht › Live-Vorschau › Aus, 10 s, 30 s, 1 min, 5 min | |
| Darstellung › Design wie Windows / hell / dunkel, Signalfarbe, Kachelgröße | |
| Importieren … | |
| Sicherung exportieren (JSON) … | |
| Synchronisierung … | |
| Remote-Tools … (paths, TeamViewer sync, AnyDesk import) | |
| Beim Schließen im Infobereich weiterlaufen (check mark) | |
| Tastenkürzel | F1 |
| Nach Updates suchen | |
| Über Remote-Manager … (version, update source) | |

**Connection** (right-click on a tile or row; works on the selection)

Verbinden (Eingabe) · Dateiübertragung · Passwort kopieren, then Bearbeiten … (F2) · Duplizieren · In Gruppe
verschieben … · Zu Favoriten hinzufügen / Aus Favoriten entfernen, then Löschen … (Entf)

Entries that do not apply (several selected, no stored password, no file transfer) are hidden.

**Group** (right-click on a section header or a sidebar group)

Verbinden (n), then Neue Verbindung hier … · Neue Untergruppe …, then Gruppe bearbeiten … · Gruppe löschen … (Entf
on a selected sidebar group). "Keine Gruppe" has only the first three.

**Tag** (right-click on a sidebar tag): Tag entfernen … (Entf on a selected tag). It removes the tag from every
connection; the connections stay. 1Remote imports bring tags instead of folders, so this is how they go away.

**Other shortcuts:** Strg+N new connection, Strg+F search. "Tastenkürzel" (F1) lists every shortcut of the app
with key caps, grouped by area; the list is built from what the code handles (`Views/ShortcutsView`).

## Quick launcher (command palette)

Strg+K or the global hotkey (default Strg+Alt+R) opens it above everything, also above session windows.

- Empty: "Zuletzt verwendet", then the favorites.
- Typing: connections (fuzzy over name, host, tags, group, description, type, weighted by use), then actions
  (everything from "Mehr": new connection, import, sync, theme, tile size, ...; ">" lists only actions).
- An address (`10.0.0.5`, `ssh root@host:2222`, `rdp://server`) offers "Direkt verbinden" per type: a session
  for a connection that is not saved. Strg+Eingabe opens the editor with the address filled in instead.
- Keys: ↑ ↓, Tab between sections, Eingabe runs, Strg+Eingabe shows a connection in the overview, Esc closes.
  "Ändern" records a new global hotkey.

## Notification area

The icon is always there: a click opens the overview; its menu has "Remote-Manager öffnen", "Schnellstart …",
"Favoriten", "Zuletzt verwendet" (the five used last), and "Beenden". With "Beim Schließen im Infobereich
weiterlaufen" (setting `app.closeToTray`, off by default) closing the overview only hides it: sessions, status
checks, and the global hotkey keep running, a note in the corner says where the app went (once), and "Beenden"
ends it. With the setting off, closing the overview ends the app as before.

## Session windows

Every session (RDP, VNC, SSH terminal, SFTP) is its own window. Its title bar is the session bar
(`Connections/SessionBar`): lamp, type chip, name, host, a status line in German, the time connected, the window's
actions, then the caption buttons. The bar never lies over the session surface (the RDP surface and the terminal
are native child windows). While no session is up, the window shows a card with the connection's picture, labelled
fields, and the reason of a failure as a note; after a disconnect the last picture stays dimmed behind it.

RDP details that matter for servers that are not Windows:

- Servers built on FreeRDP (GNOME Remote Desktop, KRdp) and some xrdp setups never send Windows' logon
  notification. The session counts as up three seconds after the transport is connected.
- "An Fenster anpassen": when the server keeps its own desktop size (a mirrored screen), the picture is scaled to
  the window instead of showing scroll bars.
- A password without a user name does not connect by itself; the card asks for the user.
- A refused logon shows on the card in plain words. With a password from the app the RDP control does not open
  its own credential prompt. Its certificate warning still is the Windows dialog (the app's manifest requests
  Common Controls 6 for it; without that the first such dialog ended the process).

## Empty states

| Situation | Text | Action |
| --- | --- | --- |
| No connections at all | Noch keine Verbindungen | Neue Verbindung, Importieren … |
| Search without results | Keine Treffer für „…“ | Suche löschen |
| Chips hide everything | Kein Treffer mit diesen Filtern | Filter zurücksetzen |
| Empty group | Diese Gruppe ist leer | Neue Verbindung hier … |
| Favorites empty | Noch keine Favoriten | (hint: the star) |
| Recent empty | Noch nichts verbunden | |
| No session open | Keine Sitzung offen | |
