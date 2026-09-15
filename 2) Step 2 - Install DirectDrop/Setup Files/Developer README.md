# DirectDrop

A local, no-cloud, no-app file transfer tool between a Windows 11 PC and an
iPhone: the PC creates a Wi-Fi hotspot, the iPhone scans a QR code, Safari
opens a page served by the PC itself, and files move directly over the LAN.
No internet, no account, no iPhone app.

## Honest status: what's verified vs. what isn't

This was built in a Linux sandbox with no Windows machine and no access to
nuget.org. That shapes what could actually be proven to work:

**Compiled and run for real, with a green test run to show for it:**
- `DirectDrop.Core` - path-traversal protection, token validation, filename
  sanitization, the session/transfer model. Zero dependencies, builds
  anywhere.
- `DirectDrop.Server` - the embedded Kestrel server, token-auth middleware,
  contiguous streaming/resumable upload, range-request download, file listing and real-time transfer events. Pure
  ASP.NET Core, no Windows dependency.
- `tools/DirectDrop.SmokeTest` - a zero-NuGet console tool that starts the
  real server and hits it with real HTTP requests. **Run it yourself with
  `dotnet run --project tools/DirectDrop.SmokeTest`** - no internet or
  Windows required. During development it passed all checks, including:
  path traversal rejected (forward-slash, backslash, absolute, UNC), token
  auth enforced, byte-exact upload/download, contiguous streaming upload
  with byte-offset resume after a dropped connection, HTTP Range requests
  (206 Partial Content), automatic port fallback, and - the spec's
  single biggest concern - **a 220MB single-request upload that grew the
  process's memory by about 2MB**, confirming uploads stream to disk instead
  of buffering in
  RAM.

**Written carefully, cross-checked against Microsoft's documentation, but
NOT compiled or run** (this requires an actual Windows 11 machine):
- `DirectDrop.App` - the WPF UI, `HotspotService` (Windows Mobile Hotspot
  control via `Windows.Networking.NetworkOperators`), `FirewallService`
  (`netsh advfirewall`), and QR generation (QRCoder).
- The iPhone-facing web page (`wwwroot/`) - the upload/resume/download logic
  mirrors what the smoke test proved works server-side, but it's only been
  reviewed, not run in an actual Safari tab.
- `DirectDrop.Tests` (xUnit) - mirrors the smoke test's coverage in a form
  `dotnet test` can run, but xUnit's NuGet packages weren't reachable here,
  so this specific project was never actually executed. Run it yourself:
  `dotnet test src/DirectDrop.Tests`.

**One real bug this process caught, worth knowing about:** the WinRT
tethering API's result enum is `TetheringOperationStatus` with members like
`WiFiDeviceOff` and `NetworkLimitedConnectivity` - an earlier draft used a
made-up type name and member names that would not have compiled. That was
only caught by checking Microsoft Learn directly instead of trusting
memory. If you hit compiler errors in `HotspotService.cs`, that class's own
doc comment explains the one further risk that's genuinely undetermined
without a Windows machine: `NetworkOperatorTetheringManager` normally
requires a `wiFiControl` device capability declared via an MSIX manifest,
which this project's plain unpackaged .exe doesn't have a way to declare.
Full-trust elevated desktop apps often get away without it, but that has
varied by Windows build - **this is exactly why the "open Mobile Hotspot
Settings, then detect and continue automatically" fallback exists**, not a
lesser backup path.

## Getting a compiled .exe without installing anything locally

If you'd rather not install the .NET SDK yourself, `.github/workflows/build.yml`
builds DirectDrop on a free GitHub-hosted Windows runner and hands you back
the finished files:

1. Create a new (can be private) repository on GitHub and push this folder
   to it (`git init && git add . && git commit -m "DirectDrop" && git push`,
   following GitHub's "push an existing repository" instructions for a new
   repo).
2. Go to the repo's **Actions** tab. The "Build DirectDrop" workflow runs
   automatically on push - or click it and press **Run workflow** if it
   doesn't.
3. Once it finishes (a few minutes), open the completed run and scroll to
   **Artifacts**: `DirectDrop-exe` (the portable `.exe`) and
   `DirectDrop-Setup` (the installer) are both there as downloadable zips.

This runs the exact same smoke test and test suite `build.ps1` does, on a
real Windows machine - it either fails loudly with the same test output
you'd see locally, or you get the real, actually-compiled files.

## Getting started (building it yourself)

Requirements: Windows 11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone <this repo>
cd DirectDrop
.\build.ps1
```

This runs the smoke test, runs the full test suite, and publishes a
self-contained `publish\DirectDrop.exe`. Run `.\build.ps1 -SkipTests` to
skip `dotnet test` (e.g. if you're offline), or open `DirectDrop.sln` in
Visual Studio and run `DirectDrop.App` directly (F5) for day-to-day
development - that's faster than a full publish per change.

To also produce `DirectDrop-Setup.exe`, install
[Inno Setup](https://jrsoftware.org/isinfo.php) first; `build.ps1` detects
`iscc.exe` and compiles `installer\DirectDrop.iss` automatically.

## Architecture

```
DirectDrop/
├── DirectDrop.sln
├── build.ps1
├── src/
│   ├── DirectDrop.Core/     Pure logic: path safety, tokens, models. No deps.
│   ├── DirectDrop.Server/   Embedded Kestrel server + wwwroot. Cross-platform.
│   ├── DirectDrop.App/      WPF shell, hotspot, firewall, QR. Windows-only.
│   └── DirectDrop.Tests/    xUnit tests over Core + Server.
├── tools/
│   └── DirectDrop.SmokeTest/  Zero-NuGet integration check - see above.
└── installer/
    └── DirectDrop.iss       Inno Setup script.
```

The Core/Server split is deliberate: everything that actually touches
files, tokens, and network bytes has zero Windows dependency, so it's
testable (and was tested) without a Windows machine. Only the last mile -
turning on the hotspot, opening a firewall port, and drawing the WPF window
- is Windows-only, and it's kept as thin as possible around that
cross-platform core.

### Request flow

1. User presses **Start Direct Wi-Fi**. `SessionCoordinator` detects the
   best network adapter, tries to enable Mobile Hotspot automatically, and
   falls back to "open Settings, then poll until it appears" if that fails.
2. Once an adapter is found, `DirectDropServer` starts Kestrel bound to that
   adapter's IP, walking forward through ports if 8765 is taken.
3. A random 256-bit token is generated; the QR code encodes
   `http://<ip>:<port>/?token=<token>`.
4. Phone scans the QR code, its browser loads the page. Every `/api/*` route
   (except `/api/health`) requires that token, via header or query string.
5. Uploads: `app.js` sends the selected `File` as **one contiguous HTTP request**. There is no application-level chunk scheduler, no out-of-order writes and no file reassembly. The server streams the request body directly into a sequential `.part` file. If the connection drops, the client asks `/api/upload/status/{id}` for the authoritative byte offset and resumes with one `File.slice(offset)` request. `File.slice()` is used only for recovery, not as a packaging/chunking system.
6. Downloads: `Results.File(..., enableRangeProcessing: true)` gives the
   browser native HTTP Range support for free.

## Known limitations / not yet built

- Settings now let the user choose both the received-files folder and the
  folder shared to the phone. Settings are stored locally under
  `%LOCALAPPDATA%\DirectDrop\settings.json` and apply on the next session.
- **Wi-Fi network name isn't read back and displayed** -
  `GetCurrentAccessPointConfiguration().Ssid` would supply it; the QR code
  and literal URL are the primary connection path either way (per the
  spec's own DISCOVERY section), so this is cosmetic, not functional.
- **Multiple simultaneous transfers**: the server supports independent
  transfers, while the UI keeps the interaction intentionally simple. The
  phone uses one contiguous request per file; interrupted transfers resume
  from the server's exact byte offset.
- Requires administrator elevation on every launch (see `app.manifest`) -
  a deliberate reliability tradeoff given hotspot control and firewall
  rules both typically need it. Revisit once you've confirmed your target
  machines don't.

## Testing

Automated (see "Honest status" above for what's actually been run):
`tools/DirectDrop.SmokeTest` (no NuGet needed) and `src/DirectDrop.Tests`
(xUnit, needs `dotnet test`) both cover: server startup/shutdown, QR URL
generation, token validation and rejection, path-traversal prevention,
contiguous streaming upload, interrupted-transfer resume, large-file streaming (memory-safety check in the smoke
test), download, resume, port conflict, and multiple simultaneous clients.

Manual - genuinely needs a Windows 11 PC and an iPhone, per the original
spec:

| Test | What to check |
|---|---|
| A | PC and iPhone connected through a PC-created hotspot |
| B | PC has no internet |
| C | Router is completely unavailable |
| D | 1GB video transfer |
| E | 10GB+ video transfer |
| F | Transfer interrupted halfway, then resumed |
| G | iPhone Safari specifically (not just curl/Chrome) |
| H | Windows Defender Firewall enabled |
| I | A second device without a valid token is rejected |

## Security notes

- Every session gets a fresh, cryptographically random 256-bit token
  (`RandomNumberGenerator`), compared in constant time. It's invalidated
  when the session ends.
- The Shared and Upload folders are the *only* filesystem locations the
  iPhone can ever reach - `PathSecurity.ResolveSafePath` resolves `..` and
  rejects anything that would escape those roots, using a hand-rolled
  segment-stack resolver (not `Path.GetFullPath` on a raw string) so the
  behavior doesn't depend on which OS separator conventions happen to be in
  play.
- The firewall rule DirectDrop creates is scoped to one TCP port, one
  profile (Private), and is removed on Stop and on uninstall.


## Final release notes

- PC → phone uses a 4 MiB asynchronous sequential file stream with request/transfer cancellation linked once per response and throttled progress telemetry.
- Phone → PC keeps the 4 MiB asynchronous upload buffer and throttles only progress bookkeeping, not network I/O.
- The desktop now displays a friendly connected-phone type/model derived from the browser user agent instead of the raw IP address.
- The persistent "Files available to your phone" setting is removed. PC → phone files remain session-scoped and are only published when explicitly selected or dropped into DirectDrop.
- The startup screen uses a circular spinner and only exposes "Setting up".
- Pairing language explicitly says "Scan this QR to open the DirectDrop site".


### Fast Mode
Use the ⚡ Fast Mode toggle on the transfer screen when you want DirectDrop to temporarily prioritize local transfer performance. It can switch Windows to the High Performance power plan and, only when the hotspot is using Wi-Fi upstream, disconnect the PC's station Wi-Fi connection. DirectDrop restores the captured settings when Fast Mode is disabled or the app closes. Bluetooth is not disabled automatically.
