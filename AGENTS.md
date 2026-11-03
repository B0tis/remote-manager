Project: Remote-Manager, a modern remote connection manager for Windows desktop (alternative to 1Remote / RDCMan). Owner: Botis. Repo: https://github.com/B0tis/remote-manager (public).

Full plan (German, canvas source): docs/plan-source.txt. Read it first.

Locked decisions: C# on .NET 10 with Avalonia 12 and FluentAvalonia. Windows desktop only. Embedded RDP via Devolutions MsRdpEx RdpClientView (MIT); fallback is your own NativeControlHost plus WinForms wrapper around MsRdpClient; last resort FreeRDP. Modern tile and list overview (preview or icon, type badge, online status, description, tags, groups, favorites). TeamViewer, AnyDesk, and RustDesk are managed in the app and launched externally. Extended start params per connection (RDP direct Windows logon).

Rules: work on a branch, open a PR with gh, and never merge (Botis merges). Never commit secrets. Small, verified steps: build and run before claiming done.
