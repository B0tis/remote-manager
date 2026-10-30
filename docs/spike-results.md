# Phase 0 spike results: embedded RDP in Avalonia 12

Date: 2026-10-29. Machine: Windows 11 Pro 10.0.26200, .NET SDK 10.0.403 (runtime 10.0.14), three monitors, all at 100 % scaling.

Stack: Avalonia 12.1.4, FluentAvaloniaUI 3.1.0, Devolutions.MsRdpEx 2026.10.26 (`<MsRdpExAvalonia>true</MsRdpExAvalonia>`), x64.

## Decision

**Path A: MsRdpEx `RdpClientView`.** It builds and runs with Avalonia 12.1.4, FluentAvalonia 3.1.0, and .NET 10, so the NativeControlHost + WinForms fallback (path B) and FreeRDP (path C) are not needed. One MsRdpEx bug needs a workaround in our code (see "Disconnect handling"). Neither the bug nor any other finding argues against path A.

## How it was tested

`RemoteManager.App.exe --selftest` runs automated checks and writes `%LOCALAPPDATA%\RemoteManager\spike\selftest.log` plus probe screenshots. It exits with code 0 when every check it ran passed.

```powershell
dotnet build
src\RemoteManager.App\bin\Debug\net10.0-windows\RemoteManager.App.exe --selftest [--composition=winui|dcomp|redirection] [--overlay-popups]
```

Checks that need a real RDP server run only when `RM_SPIKE_HOST` is set. `RM_SPIKE_USER`, `RM_SPIKE_DOMAIN`, and `RM_SPIKE_PASSWORD` prefill the form and are never logged. **No RDP server was reachable during this spike, so the live-session checks have not run yet** (see "Still open").

Results of the automated run: every check passed in the default composition mode and in `WinUIComposition`, `DirectComposition`, and `RedirectionSurface`.

## Results

| Topic | Result | Evidence |
|---|---|---|
| Build and start | ✅ `dotnet build` has 0 warnings with `TreatWarningsAsErrors`; the app starts, shows its window, and exits with code 0 on close | manual run, CI |
| Two parallel RDP controls | ✅ Both ActiveX clients reach `ClientReady` in their own tabs | selftest |
| Tab switching | ✅ Each tab's view stays in one `Grid` and is switched with `IsVisible`. HWNDs stay the same, both clients stay ready, and only the selected host HWND is visible (6 switches) | selftest |
| Tab close | ✅ `RdpClientView.Dispose()` destroys the HWND. Calling it is **required**: MsRdpEx deliberately keeps detached views alive for docking | selftest |
| Disconnect handling | ⚠️ Works with a workaround (MsRdpEx bug, see below). Failed connects report within 0.1 to 0.7 s with the localized Windows error text, and the same view can connect again | selftest |
| Session screenshot | 🟡 Implemented; not yet run against a live session | code, see below |
| Airspace: menus, flyouts, ComboBox, tooltips | ✅ Drawn above the RDP surface because Avalonia popups are native windows (`OverlayPopups = false`, the default) | `probe-menu.png` |
| Airspace: `--overlay-popups` | ❌ Popups are clipped by the RDP HWND. Never enable overlay popups | `overlay/probe-menu.png` |
| Airspace: `FAContentDialog` (in-window) | ❌ Completely hidden, including its smoke layer | `probe-contentdialog-no-workaround.png` |
| Airspace: workaround | ✅ Hiding the `RdpClientView` and showing its snapshot while the dialog is open works | `probe-contentdialog-workaround.png` |
| Airspace: own `Window` dialog | 🟡 Not probed on its own. A top-level window sits above child HWNDs by Win32 design, the same mechanism the menu probe verified | inferred |
| Composition mode | ✅ No difference between WinUI, DirectComposition, and RedirectionSurface in any check | selftest ×3 |
| Keyboard focus (Win key, Alt+Tab) | 🟡 Wired; needs a live session to verify | see below |
| DPI and resize | 🟡 Wired; needs a live session and a monitor with different scaling | see below |
| Direct Windows logon (NLA) | 🟡 Wired; needs a live session | see below |

### MsRdpEx bug: `RdpClientView.Disconnected` never fires

The RDP v11 control (`A0C63C30-…`) raises the classic `IMsTscAxEvents.OnDisconnected` (DISPID 4). MsRdpEx's `RdpClientEventSink` maps DISPID 4 only to the raw `RdpClientEventSubscription.Disconnected(int)` event. `RdpActiveXSession` listens only to `DisconnectedWithDetails`, which is raised only for the newer `IRemoteDesktopClientEvents` DISPID 753. As a result:

- `RdpClientView.Disconnected` and the status line "Disconnected: …" never appear;
- `IsConnectionActive` and `IsLoginCompleted` stay `true` after a disconnect.

The RDP client's event log (`Microsoft-Windows-TerminalServices-RDPClient/Operational`, events 1024/1026) showed the control disconnecting after about 1 s while the app still showed "Connecting...".

**Workaround (in `SessionView`):** after `ClientReady`, call `GetClient<IMsRdpClient>().Subscribe()`. Handle its raw `Disconnected(int)` event, keep our own `IsSessionActive` and `IsLoggedIn` state, and get the error text from `IMsRdpClient10.GetErrorDescription(reason, ExtendedDisconnectReason)`. The library's own `Disconnected` stays subscribed and is de-duplicated, so a fixed MsRdpEx works without changes. This is worth reporting upstream (Devolutions/MsRdpEx), and the MIT license allows a fork if needed.

### Session screenshot (tile preview)

`SessionCapture` finds the control's `OPWindowClass` ("Output Painter Child Window") under `HostWindowHandle`, the same window MsRdpEx's `RdpView.cs` copies from. It captures with `PrintWindow(PW_RENDERFULLCONTENT)` and falls back to `BitBlt` from the window DC. A frame is treated as blank when almost all sampled pixels have the same color. PNGs go to `%LOCALAPPDATA%\RemoteManager\thumbnails\<id>.png`, encoded by Avalonia, so no System.Drawing is needed.

Timing matters, because after the disconnect event the surface may already be gone:

- **User disconnect:** capture first, then call `Disconnect()`.
- **Server or network disconnect:** an in-memory frame is grabbed every 10 s while the tab is visible and logged in, and the newest non-blank frame is saved.
- The disconnected tab then shows the thumbnail in place of the (hidden) native surface.

Not yet verified: that the PNG of a live session is non-black, and whether `PrintWindow` works on a hidden tab. The live selftest checks both and logs the capture method. `IRemoteDesktopClientActions::GetSnapshot` was not tried.

### Keyboard and focus

- `UseRemoteKeyboardShortcuts` maps to the control's `KeyboardHookMode`. It is a per-connection checkbox ("Win keys to session", default on) and applies at connect time.
- `FocusSession()` runs after logon and every time a tab becomes active.
- Tab and Shift+Tab out of the session move Avalonia focus through MsRdpEx's `FocusReleased` handling.
- According to MsRdpEx, input is fully native: no low-level hook and no message forwarding.
- To verify manually: Win key, Alt+Tab, and Ctrl+Alt+End reach the session when it has focus; Alt+Tab stays local with the checkbox off; Ctrl+Alt+Break toggles fullscreen.

### DPI and resize

- `app.manifest` declares PerMonitorV2.
- `RdpClientView` computes pixel sizes from `RenderScaling` and passes the scale factor to `UpdateSessionDisplaySettings`.
- In "Fit to window" mode, a resize triggers one debounced (250 ms) resolution update. Fixed resolutions and "Smart sizing" use client-side scaling.
- The selftest resizes the window and checks that the remote desktop size matches the viewport. When a screen with different scaling exists, it also moves the window there.
- All three monitors here run at 100 %, so mixed DPI could not be tested.

### Connection fields, NLA, and password handling

- Fields: host, user, domain, password, and display mode (fit to window, smart sizing, 1920×1080, 1280×720).
- `RdpClientView.Connect` always sets `EnableCredSspSupport = true`, so NLA is always on; the checkbox is read-only. Making NLA configurable later means using `GetClient<T>()` plus `ConnectConfigured()`.
- We set `AuthenticationLevel = 2` (warn on certificate problems) before connecting.
- The password goes to the control through `ClearTextPassword`, and the TextBox is cleared right after `Connect`. It is never logged, and status lines come only from connection events.
- Remaining exposure: the immutable `RdpConnectionSettings` record holds the string until GC. A `SecureString` or pinned buffer would not change much while MsRdpEx takes a `string`.

### Other findings

- An idle `RdpClientView` paints a white rectangle over everything. The view stays hidden until Connect, which calls `UpdateLayout()` first so the initial session size matches the viewport (otherwise it falls back to 200×200).
- In FluentAvalonia 3, `FATabView.TabItems` is read-only (`ItemsSourceView`). Tabs are owned through `TabItemsSource` (an `ObservableCollection`).
- On this German Windows, the RDP error descriptions are localized.

## Still open (needs a reachable RDP server)

Set `RM_SPIKE_HOST`, `RM_SPIKE_USER`, `RM_SPIKE_DOMAIN`, and `RM_SPIKE_PASSWORD`, then run `--selftest`, or connect manually and check:

1. The session reaches the desktop without a credential prompt (direct logon, NLA).
2. The live snapshot PNG is not black; note the capture method in the log.
3. A tab switch while connected keeps the session alive and repaints correctly.
4. After a resize in fit-to-window mode, the remote resolution matches the viewport.
5. Win key, Alt+Tab, and Ctrl+Alt+End go to the session (manual).
6. Moving to a monitor with different scaling keeps the image usable (needs a second scaling setup).
7. A server-side logoff or disconnect (for example `logoff` inside the session) leaves a thumbnail on the tab.

## Implications for Phase 1

- Keep `IProtocolProvider` / `RdpEmbeddedProvider` around `RdpClientView` and the disconnect workaround in one place.
- Use no in-window overlays over sessions: dialogs are own windows or swap the session for its snapshot, and the detail panel is a column. Never enable `OverlayPopups`.
- Session views live in one host panel and switch with `IsVisible`. Closing a tab must call `Dispose()`.
