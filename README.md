# SUA Airspace Plugin

A vatSys plugin and public Cloudflare control page for staging and applying Australian Special Use Airspace activations.

## Features

- Public control page at `https://sua.actuallyleviticus.xyz/`; no browser operator key is required.
- Public activation request form at `https://sua.actuallyleviticus.xyz/request`. A request records one or more selected areas, a separate RA1/RA2/RA3 category for each area, one or more separate activation time slots entered in UTC/Z with the requester's local equivalent displayed underneath, the requester's name or VATSIM CID, contact email, and required activation details explaining why the airspace is needed without activating it automatically. RA1 is shown in green, RA2 in yellow, and RA3 in red. Controllers review pending requests from the `REQUESTS` button on the control page and can accept or decline each one; acceptance stages every requested slot and each area's category, allowing the airspace to deactivate between slots and reactivate at the next one.
- New requests can notify Discord through the private `DISCORD_REQUEST_WEBHOOK_URL` Pages secret. VATSIM Connect scaffolding provides OAuth login, D1-backed sessions, and an `AUTHORIZED_VATSIM_CIDS` controller allow-list. Set `VATSIM_CLIENT_ID`, `VATSIM_CLIENT_SECRET`, `VATSIM_REDIRECT_URI`, and `VATSIM_AUTH_REQUIRED=true` in the Pages environment only after registering the callback `https://sua.actuallyleviticus.xyz/api/auth/callback`; until then the authentication guard remains disabled.
- Full-screen airspace map at `https://sua.actuallyleviticus.xyz/map` plots every Danger/Restricted area over Australia and highlights it live (Danger yellow, Restricted/M red; filled = active, faint = pre-active, outline = deactive), refreshing every five seconds. Each border matches the area's activation draw style. Geometry is a static `areas.geojson` asset generated from the canonical GitHub dataset; the map (Leaflet with CARTO/OpenStreetMap tiles) joins it to the live shared state by area name.
- Activations, time windows, and level edits are written directly to shared Cloudflare D1 storage. The public website operates independently of every plugin installation.
- Each plugin is a reader of the shared desired state and applies it locally when it syncs. No inbound tunnel or router port-forward is used.
- The shared area catalogue and default schedules are owned by the scheduled Worker, which pulls the canonical [vatSys australia-dataset `RestrictedAreas.xml`](https://github.com/vatSys/australia-dataset/blob/master/RestrictedAreas.xml) — so the website always reflects what the division intends, regardless of whatever `RestrictedAreas.xml` a controller has loaded locally. Plugins upload only their genuine controller-created activations and level changes from the vatSys Restricted Area window (cloud-injected activations are excluded so they cannot feed back). Plugins no longer write the catalogue, which removed the dominant source of D1 write usage.
- The Worker writes the catalogue and NOTAM state only when their content hashes change, so steady-state D1 reads/writes are near zero even though it runs every minute. Default active/pre-active state is computed from each area's schedule at read time, so the stored catalogue never needs per-minute rewrites.
- A scheduled Cloudflare Worker refreshes VATPAC airspace NOTAMs every minute, expands compressed designators such as `R225ABCDEF`, matches the dataset, and schedules the published activation windows automatically.
- Plugin activations use vatSys's standard restricted-area colour with no plugin-added infill.
- Each SUA border keeps the line pattern defined in the dataset, and controllers can change how an area is drawn from the vatSys Restricted Area window.
- RA categories are derived from their line pattern everywhere: dashed is RA1 (green, DAIW off), dotted is RA2 (yellow, DAIW on), and solid is RA3 (red, DAIW on). Dataset areas expose their predefined category, while accepted requests and live vatSys activations apply and share the corresponding line pattern and DAIW setting.
- Active Danger areas use the vatSys broken-line pattern so they remain visually distinct from Restricted and Military RA category borders.
- Area type is derived from the designator prefix: `D` is Danger, `R` is Restricted, and `M` is Military. This rule is used by the catalogue, request form, control page, and generated map.
- When a controller activates an area, their selected line pattern is shared to other connected controllers. A controller who did not activate the area can still restyle it locally without changing anyone else's display; the activating controller's pattern only reapplies if they change it again.
- Dated UTC windows and temporary vertical-limit edits are supported. Original levels restore when an area is deactivated.
- When one scheduled activation window ends, it is removed from the shared planned schedule while any later windows remain staged.

## Using It

Open `https://sua.actuallyleviticus.xyz/`.

- `ACT` saves an H24, duration-based, HJ, or HN shared activation. HJ runs from sunrise to sunset and HN from sunset to the next sunrise, calculated daily at the centre of each airspace area. `DEACT` clears saved shared sources; vatSys-native/default activations are read-only and remain active.
- `EDIT` changes levels, stages an RA category with the activation, and selects dated UTC, HJ, or HN timing. A staged category applies only while the activation is active and the previous local/dataset category is restored afterward.
- `SAVED` means the desired state is stored in D1 and is available to every plugin installation independently.
- Pending activation requests can be edited from the `REQUESTS` panel before review, but only their selected areas, RA category, and activation time slots can be changed. The submitted name or CID, contact email, and activation details remain read-only. Accepted or declined requests are no longer editable.
- `USER: CID` identifies a live activation configured by that controller in vatSys and shared to other connected controllers. The CID comes directly from vatSys's connection state. These sources use a short renewable lease so they disappear after the originating plugin disconnects.
- A live vatSys controller activation displays only its timing state (`ACTIVE`, `PREACT`, or `OFF`) and `USER: CID`; overlapping stored sources do not add `MAN`, `SCHED`, or `SAVED` badges to that row.
- Controllers connected with the OBS facility can view dataset and shared SUA, but cannot retain or publish Restricted Area activations, regardless of their underlying VATSIM certification rating. Local OBS changes are restored to the dataset/shared state and the cloud API ignores OBS controller activations.
- Controller-created areas show `USER LOCKED` instead of website controls and cannot be deactivated through individual, global, or NOTAM actions while their originating controller remains connected.
- `DEFAULT` is profile-defined. Its action column contains a full-width `DATASET LOCKED` box in place of ACT and EDIT, and neither website nor controller-synchronisation actions can remove it.
- The NOTAM panel is informational and refreshed automatically. Every matched airspace NOTAM is scheduled from its listed times by the scheduled Worker and re-staged on each run, so it stays scheduled for its whole life. There are no manual activate/deactivate controls — matched areas activate and deactivate automatically as each NOTAM window opens and closes.
- `CLEAR SHARED ACTIVATIONS` clears shared website/manual activations. It never changes default activations, and it no longer pauses NOTAMs — airspace NOTAMs always remain auto-scheduled and re-stage on the next automation run.

The local `http://localhost:5300/` endpoint is loopback-only and redirects to the public page. Plugin synchronisation uses the public Cloudflare API and requires no operator or machine key, so the same release can be copied directly to other controllers.

## Architecture

1. Cloudflare Pages serves the UI and Pages Functions API.
2. Pages Functions store desired activation sources in D1 and serve the aggregated shared state.
3. The automation Worker runs every minute: it pulls the canonical vatSys `RestrictedAreas.xml` from GitHub into the `areas` catalogue and reads VATPAC's CMS feed for matched NOTAM windows. Both are content-hashed, so it only writes to D1 when the dataset or NOTAM feed actually changes.
4. Each plugin reads the aggregated desired state every five seconds and uploads only its genuine controller-created activations and level differences (not the catalogue).
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
| `POST /api/sua/activate?name=X&mode=HJ|HN` | Stage recurring sunrise/sunset or sunset/sunrise activation |
| `POST /api/sua/deactivate?name=X` | Clear every desired source for an area |
| `POST /api/sua/deactivateall` | Clear all desired state |
| `POST /api/sua/windows?name=X&windows=...` | Replace manual UTC windows |
| `POST /api/sua/levels?name=X&floor=N&ceiling=N` | Stage level edits |
| `POST /api/sua/category?name=X&category=RA1\|RA2\|RA3` | Stage an RA category and matching line pattern |
| `GET /api/sua/notams` | Stored current/upcoming airspace NOTAMs |
| `GET /api/sua/requests` | Pending user activation requests |
| `POST /api/sua/requests` | Submit an activation request as JSON |
| `POST /api/sua/requests/update` | Edit the details of a pending request |
| `POST /api/sua/requests/review` | Accept or decline a pending request |
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

`build-map.ps1` regenerates `cloudflare-pages/public/areas.geojson` from the
canonical vatSys GitHub dataset by default (override the source with
`-RestrictedAreasSource`, which also accepts a local file path). Re-run it when
the dataset's restricted-area geometry changes with an AIRAC update, then
redeploy Pages.

The automation Worker's manual `/refresh` endpoint still uses its private `SUA_SYNC_TOKEN` secret. Plugin synchronisation and the public website do not use that secret.

The tracked example is [SuaAirspacePlugin.config.example.json](SuaAirspacePlugin.config.example.json). Restart vatSys after replacing the DLL or configuration.

## VATPAC Docker/PostgreSQL Hosting

A conventional container deployment is available in [vatpac-hosting](vatpac-hosting/README.md). It runs the same static site, API, plugin sync, VATSIM OAuth, Discord notifications, and scheduled refresh logic on Node.js with PostgreSQL. Cloudflare is the temporary testing environment; Docker/PostgreSQL is the intended production destination and will replace the Cloudflare Pages, D1, and Worker services after acceptance testing and cutover.

## Project Structure

```text
cloudflare-pages/                 Pages UI, Functions API, and D1 migrations
cloudflare-pages/public/map.html  full-screen Leaflet airspace map (/map)
vatpac-hosting/                   Docker, PostgreSQL schema, Node host, and VATPAC handoff guide
cloudflare-pages/public/areas.geojson  generated area geometry (build-map.ps1)
cloudflare-automation/            scheduled VATPAC NOTAM Worker
SuaAirspacePlugin/CloudSyncService.cs
SuaAirspacePlugin/SuaAirspaceService.cs
SuaAirspacePlugin/SuaUiPage.cs
build-pages.ps1
build-map.ps1
build-release.ps1
```
