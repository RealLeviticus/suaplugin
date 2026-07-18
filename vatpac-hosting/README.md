# VATPAC Docker/PostgreSQL deployment

This directory is the production migration target for the SUA Airspace website, API, plugin sync endpoint, VATSIM OAuth flow, Discord request notifications, and scheduled area/NOTAM refresh. Cloudflare is the temporary development and testing environment; after the Docker/PostgreSQL deployment passes acceptance testing and the plugin is repointed, the Cloudflare Pages, D1, and Worker resources can be retired.

The container currently imports the proven application modules and static assets from the repository's `cloudflare-pages` and `cloudflare-automation` directories. Those are source-code locations only: the running container has no Cloudflare service, account, D1, Pages, Worker, or Wrangler dependency.

## Services

- `site`: Node.js 22 HTTP service on port `8080`.
- `postgres`: PostgreSQL 17 with a persistent named volume.
- Database migrations run automatically and transactionally when `site` starts.
- The canonical vatSys area dataset and VATPAC NOTAM feed refresh every 60 seconds by default. Overlapping refreshes are suppressed.
- HJ and HN activation modes calculate rolling sunrise/sunset windows at each area's dataset-derived geographic centre.
- Activation requests can carry multiple separate time slots; accepted requests activate, deactivate between slots, and reactivate for later slots.
- Each requested area carries its own RA category, and active Danger areas use a broken border.
- The manager includes independent type and status filters, a compact centred editor, and bulk copying of levels and activation timing across selected Danger, Restricted, and Military areas. RA categories are copied only where they apply.
- The live map includes type/status filters, SUA name search, fit-to-filtered-results controls, and five-second state refreshes.
- Scheduled activations deactivate automatically at the end of their windows; the vatSys plugin does not display a deactivation prompt.
- `GET /healthz` checks both the HTTP process and PostgreSQL connection.

The compose file is suitable for evaluation and a simple single-host deployment. VATPAC can point an existing managed PostgreSQL service at the site container instead by setting `DATABASE_URL` and deploying only the `site` image.

## Build and run

The Docker build context must be the repository root because the container deliberately uses the same public assets and API implementation as the Cloudflare version.

```sh
cd vatpac-hosting
cp .env.example .env
# Replace every placeholder and set the public hostname in .env.
docker compose up --build -d
docker compose ps
curl --fail http://127.0.0.1:8080/healthz
```

To build only the application image from the repository root:

```sh
docker build -f vatpac-hosting/Dockerfile -t sua-airspace:latest .
```

The image copies the current `cloudflare-pages/public`, `cloudflare-pages/functions`, and `cloudflare-automation/src` trees during every build. Rebuild the image after updating the repository so the Docker deployment receives the same manager, map, API, and automation code as the Cloudflare test deployment.

The site should sit behind VATPAC's HTTPS reverse proxy or ingress. The proxy must preserve `Host` and set `X-Forwarded-Proto: https`. `PUBLIC_BASE_URL` is the authoritative external URL and avoids ambiguity when OAuth redirects are generated behind a proxy.

## Required configuration

| Variable | Purpose |
|---|---|
| `DATABASE_URL` | PostgreSQL connection URI for a managed or external database. Compose uses standard `PGHOST`, `PGDATABASE`, `PGUSER`, and `PGPASSWORD` variables instead. |
| `POSTGRES_PASSWORD` | Password used by the bundled PostgreSQL service. |
| `PUBLIC_BASE_URL` | Public HTTPS origin without a trailing slash. |
| `SUA_SYNC_TOKEN` | Private bearer protecting the manual automation refresh endpoint. |

Generate the database password and automation token independently. Do not commit `.env` or the installed plugin configuration.

## Optional integration configuration

| Variable | Default | Purpose |
|---|---:|---|
| `DISCORD_REQUEST_WEBHOOK_URL` | empty | Sends activation request notifications to Discord. |
| `VATSIM_AUTH_REQUIRED` | `false` | Requires VATSIM login and controller authorization when `true`. |
| `VATSIM_CLIENT_ID` / `VATSIM_CLIENT_SECRET` | empty | VATSIM Connect application credentials. |
| `VATSIM_REDIRECT_URI` | empty | Must be `PUBLIC_BASE_URL/api/auth/callback`. |
| `AUTHORIZED_VATSIM_CIDS` | empty | Comma-separated CIDs allowed to manage requests and airspace. |
| `DATABASE_SSL` | `false` | Set to `true` for a managed PostgreSQL service requiring TLS. |
| `AUTOMATION_ENABLED` | `true` | Runs dataset and NOTAM refresh inside the site container. |
| `AUTOMATION_INTERVAL_SECONDS` | `60` | Refresh interval, with a minimum of 30 seconds. |

Only one replica should have `AUTOMATION_ENABLED=true`. If VATPAC runs multiple site replicas, enable it on one worker or invoke `POST /api/automation/refresh` from an external scheduler with the `X-SUA-Sync-Token` header.

## Plugin configuration

Point installed plugins at the new public origin. The current plugin sync endpoint does not use `SUA_SYNC_TOKEN`:

```json
{
  "CloudApiUrl": "https://sua.example.vatpac.org/",
  "SyncIntervalSeconds": 5
}
```

## VATSIM Connect

Register this callback with VATSIM Connect:

```text
https://sua.example.vatpac.org/api/auth/callback
```

Set the client ID, client secret, redirect URI, and controller CID allow-list first. Switch `VATSIM_AUTH_REQUIRED` to `true` only after login and callback behavior have been tested on the final HTTPS hostname.

## Production cutover from Cloudflare

The PostgreSQL schema starts empty. The scheduled refresh populates the area catalogue and current NOTAM state automatically, but activation requests and manually staged state are not copied from D1. A one-time D1-to-PostgreSQL export/import should be planned immediately before production cutover if that history must be retained.

Recommended cutover order:

1. Deploy the container and PostgreSQL database on a temporary VATPAC hostname.
2. Verify requests, controller review, VATSIM authorization, Discord delivery, scheduled refresh, and live plugin synchronization.
3. Pause request submissions briefly and migrate any D1 data that must be retained.
4. Move the production hostname to the container and update `CloudApiUrl` in the plugin configuration.
5. Confirm connected plugins are reading and writing PostgreSQL-backed state.
6. Retire Cloudflare Pages, D1, the scheduled Worker, their secrets, and related DNS only after the new service is confirmed stable.

## Handoff checks

```sh
docker compose exec postgres psql -U sua_airspace -d sua_airspace -c "SELECT name, applied_at FROM schema_migrations;"
curl --fail https://sua.example.vatpac.org/healthz
curl --fail https://sua.example.vatpac.org/api/sua/areas
docker compose logs --tail=100 site
```

Before changing the plugin URL, verify request submission, controller authorization, Discord delivery, a plugin sync cycle, and the scheduled refresh logs against the VATPAC-hosted environment.
