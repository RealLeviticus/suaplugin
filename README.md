# SUA Airspace Plugin

A vatSys plugin and public Cloudflare control page for staging and applying Australian Special Use Airspace activations.

## Features

- Public control page at `https://sua.actuallyleviticus.xyz/`; no browser operator key is required.
- Full-screen airspace map at `https://sua.actuallyleviticus.xyz/map` plots every Danger/Restricted area over Australia and highlights active (red) and pre-active (amber) areas live, refreshing every five seconds. Geometry is a static `areas.geojson` asset generated from the profile's `RestrictedAreas.xml`; the map (Leaflet with CARTO/OpenStreetMap tiles) joins it to the live shared state by area name.
- Activations, time windows, and level edits are written directly to shared Cloudflare D1 storage. The public website operates independently of every plugin installation.
- Each plugin is a reader of the shared desired state and applies it locally when it syncs. No inbound tunnel or router port-forward is used.
- Each plugin uploads an immutable copy of the profile's SUA catalogue and original default schedules, plus genuine controller-created activations and level changes from the vatSys Restricted Area window. Cloud-injected activations are excluded so they cannot feed back into the shared state.
- A scheduled Cloudflare Worker refreshes VATPAC airspace NOTAMs every minute, expands compressed designators such as `R225ABCDEF`, matches the active dataset, and stages the published activation windows automatically.
- Plugin activations use vatSys's standard restricted-area colour with no plugin-added infill.
- Each SUA border keeps the line pattern defined in the dataset, and controllers can change how an area is drawn from the vatSys Restricted Area window.
- When a controller activates an area, their selected line pattern is shared to other connected controllers. A controller who did not activate the area can still restyle it locally without changing anyone else's display; the activating controller's pattern only reapplies if they change it again.
- Dated UTC windows and temporary vertical-limit edits are supported. Original levels restore when an area is deactivated.
- When one scheduled activation window ends, it is removed from the shared planned schedule while any later windows remain staged.

## Using It

Open `https://sua.actuallyleviticus.xyz/`.

- `ACT` saves an H24 or duration-based shared activation. `DEACT` clears saved shared sources; vatSys-native/default activations are read-only and remain active.
- `EDIT` changes levels or replaces the area's dated UTC activation windows.
- `SAVED` means the desired state is stored in D1 and is available to every plugin installation independently.
- `USER: CID` identifies a live activation configured by that controller in vatSys and shared to other connected controllers. The CID comes directly from vatSys's connection state. These sources use a short renewable lease so they disappear after the originating plugin disconnects.
- A live vatSys controller activation displays only its timing state (`ACTIVE`, `PREACT`, or `OFF`) and `USER: CID`; overlapping stored sources do not add `MAN`, `SCHED`, or `SAVED` badges to that row.
- Controllers connected with the OBS facility can view dataset and shared SUA, but cannot retain or publish Restricted Area activations, regardless of their underlying VATSIM certification rating. Local OBS changes are restored to the dataset/shared state and the cloud API ignores OBS controller activations.
- Controller-created areas show `USER LOCKED` instead of website controls and cannot be deactivated through individual, global, or NOTAM actions while their originating controller remains connected.
- `DEFAULT` is profile-defined. Its action column contains a full-width `DATASET LOCKED` box in place of ACT and EDIT, and neither website nor controller-synchronisation actions can remove it.
- The NOTAM panel is refreshed automatically. Current and upcoming airspace NOTAM windows are saved by the scheduled Worker; each NOTAM can also activate or deactivate all matched areas explicitly.
- `CLEAR SHARED ACTIVATIONS` clears desired state and pauses current NOTAM auto-staging until an area or NOTAM is explicitly activated again. It never changes default activations.

The local `http://localhost:5300/` endpoint is loopback-only and redirects to the public page. Plugin synchronisation uses the public Cloudflare API and requires no operator or machine key, so the same release can be copied directly to other controllers.

## Architecture

1. Cloudflare Pages serves the UI and Pages Functions API.
2. Pages Functions store the most recent dataset snapshot and desired activation sources in D1.
3. The automation Worker reads VATPAC's CMS feed every minute and writes matched NOTAM windows to D1.
4. When vatSys finishes loading Restricted Areas, each plugin captures the full Danger/Restricted catalogue and original default schedules. Every five seconds it uploads that dataset plus local controller-created differences and reads the aggregated desired state.
5. Each installation has a generated ID. Cloudflare replaces that installation's leased `controller` sources on every sync, and the originating plugin excludes its own sources from the response to avoid echoing them back into vatSys.
6. Cloud synchronisation and SUA display changes remain dormant until vatSys connects to VATSIM. On disconnect the plugin immediately clears its leased controller sources, removes shared injections and manual local activations, and restores the captured dataset state.
7. While connected, the plugin injects/removes shared plugin activations and calls the same native `DisplayMaps.UpdateDynamicRMaps()` path used by vatSys. Missing dataset-default schedules are restored and cannot be removed. An activating controller uploads their selected line pattern alongside the activation; each installation applies a shared line pattern only when it changes, so a local restyle by a non-activating controller is never overwritten or re-uploaded.
8. The plugin leaves each area's dataset-defined line pattern in place—so controllers can restyle borders from the vatSys Restricted Area window—and only temporarily sets plugin-activated areas to `InfillType.None`. vatSys therefore keeps its own colour, width, rendering path, and minute-refresh behaviour without a replacement overlay map.

Multiple desired sources can coexist for an area. H24 takes precedence over timed windows; windows from all active sources are combined. Removing the final source makes the plugin deactivate the area and restore edited levels.

## Cloud API

Public website endpoints:

| Endpoint | Description |
|---|---|
| `GET /api/sua/areas` | Shared area catalogue and D1-backed desired state |
| `POST /api/sua/activate?name=X&minutes=N` | Stage H24 or a duration |
| `POST /api/sua/deactivate?name=X` | Clear every desired source for an area |
| `POST /api/sua/deactivateall` | Clear all desired state |
| `POST /api/sua/windows?name=X&windows=...` | Replace manual UTC windows |
| `POST /api/sua/levels?name=X&floor=N&ceiling=N` | Stage level edits |
| `GET /api/sua/notams` | Stored current/upcoming airspace NOTAMs |
| `POST /api/sua/notams/activate?id=N&mode=now\|schedule` | Add an explicit NOTAM override |
| `POST /api/sua/notams/deactivate?id=N` | Clear shared state for matched areas and suppress that NOTAM's automatic re-staging |

Private machine endpoint:

| Endpoint | Description |
|---|---|
| `POST /api/plugin/sync` | Snapshot/user-source upload and desired-state response; no key required |

## Build and Deploy

Requirements: Windows, vatSys, .NET Framework 4.8.1 build tools, PowerShell, and Wrangler.

Build directly into the active Australia profile plugin folder:

```powershell
dotnet build .\SuaAirspacePlugin\SuaAirspacePlugin.csproj -c Release -p:VatSysDir="C:\Program Files (x86)\vatSys"
```

Create the versioned release ZIP:

```powershell
.\build-release.ps1
```

Apply D1 migrations and deploy both Cloudflare projects:

```powershell
$env:CLOUDFLARE_ACCOUNT_ID = "<account-id>"
wrangler d1 migrations apply sua-airspace --remote --config .\cloudflare-pages\wrangler.toml
.\build-pages.ps1
.\build-map.ps1
Push-Location .\cloudflare-pages
wrangler pages deploy .\public --project-name sua-airspace --branch main
Pop-Location
wrangler deploy --config .\cloudflare-automation\wrangler.toml
```

`build-map.ps1` regenerates `cloudflare-pages/public/areas.geojson` from the active
profile's `RestrictedAreas.xml` (default
`Documents\vatSys Files\Profiles\Australia\RestrictedAreas.xml`; override with
`-RestrictedAreasXml`). Re-run it whenever the profile's restricted-area geometry
changes with an AIRAC/profile update, then redeploy Pages.

The automation Worker's manual `/refresh` endpoint still uses its private `SUA_SYNC_TOKEN` secret. Plugin synchronisation and the public website do not use that secret.

The tracked example is [SuaAirspacePlugin.config.example.json](SuaAirspacePlugin.config.example.json). Restart vatSys after replacing the DLL or configuration.

## Project Structure

```text
cloudflare-pages/                 Pages UI, Functions API, and D1 migrations
cloudflare-pages/public/map.html  full-screen Leaflet airspace map (/map)
cloudflare-pages/public/areas.geojson  generated area geometry (build-map.ps1)
cloudflare-automation/            scheduled VATPAC NOTAM Worker
SuaAirspacePlugin/CloudSyncService.cs
SuaAirspacePlugin/SuaAirspaceService.cs
SuaAirspacePlugin/SuaUiPage.cs
build-pages.ps1
build-map.ps1
build-release.ps1
```
