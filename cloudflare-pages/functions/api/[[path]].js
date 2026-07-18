const jsonHeaders = { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" };

function json(payload, status = 200) {
  return new Response(JSON.stringify(payload), { status, headers: jsonHeaders });
}

async function runStatements(db, statements) {
  for (let offset = 0; offset < statements.length; offset += 50) {
    const batch = statements.slice(offset, offset + 50);
    if (batch.length) await db.batch(batch);
  }
}

function parseJson(value, fallback = []) {
  try { return JSON.parse(value || ""); } catch { return fallback; }
}

function wireDate(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return date.getUTCFullYear() + pad(date.getUTCMonth() + 1) + pad(date.getUTCDate()) +
    pad(date.getUTCHours()) + pad(date.getUTCMinutes());
}

function showWindow(wire) {
  const parts = String(wire).split("-");
  if (parts.length !== 2 || parts[0].length !== 12 || parts[1].length !== 12) return wire;
  return `${parts[0].slice(6, 8)}/${parts[0].slice(4, 6)} ${parts[0].slice(8, 12)}-${parts[1].slice(8, 12)}Z`;
}

function categoryLinePattern(category) {
  return category === "RA1" ? "Dashed" : (category === "RA2" ? "Dotted" : (category === "RA3" ? "Solid" : null));
}

function areaTypeFromName(name, fallback) {
  const prefix = String(name || "").trim().charAt(0).toUpperCase();
  return prefix === "D" ? "Danger" : (prefix === "R" ? "Restricted" : (prefix === "M" ? "Military" : fallback));
}

function requestedPath(context) {
  const path = context.params.path;
  return "/" + (Array.isArray(path) ? path.join("/") : String(path || ""));
}

function authEnabled(env) {
  return String(env.VATSIM_AUTH_REQUIRED || "").toLowerCase() === "true";
}

function cookieValue(request, name) {
  const cookie = request.headers.get("Cookie") || "";
  for (const part of cookie.split(";")) {
    const [key, ...value] = part.trim().split("=");
    if (key === name) return decodeURIComponent(value.join("="));
  }
  return "";
}

async function sha256(value) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest)).map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function currentSession(request, env) {
  const token = cookieValue(request, "sua_session");
  if (!token) return null;
  return env.DB.prepare(
    "SELECT vatsim_cid, vatsim_name, expires_at FROM auth_sessions WHERE token_hash = ? AND expires_at > ?"
  ).bind(await sha256(token), new Date().toISOString()).first();
}

function controllerCids(env) {
  return new Set(String(env.AUTHORIZED_VATSIM_CIDS || "").split(",").map((value) => value.trim()).filter(Boolean));
}

async function authSessionResponse(request, env) {
  const session = await currentSession(request, env);
  return json({ Success: true, AuthEnabled: authEnabled(env), Authenticated: Boolean(session),
    Authorized: Boolean(session && controllerCids(env).has(String(session.vatsim_cid))),
    Cid: session?.vatsim_cid || "", Name: session?.vatsim_name || "" });
}

async function beginVatsimLogin(request, env) {
  if (!env.VATSIM_CLIENT_ID || !env.VATSIM_REDIRECT_URI) return json({ Success: false, Error: "VATSIM OAuth is not configured." }, 503);
  const requestUrl = new URL(request.url);
  const returnTo = (requestUrl.searchParams.get("return") || "/request").startsWith("/")
    ? requestUrl.searchParams.get("return") || "/request" : "/request";
  const state = crypto.randomUUID();
  await env.DB.prepare("DELETE FROM oauth_states WHERE expires_at <= ?").bind(new Date().toISOString()).run();
  await env.DB.prepare("INSERT INTO oauth_states(state, return_to, expires_at) VALUES (?, ?, ?)")
    .bind(state, returnTo, new Date(Date.now() + 10 * 60 * 1000).toISOString()).run();
  const target = new URL("https://auth.vatsim.net/oauth/authorize");
  target.searchParams.set("response_type", "code"); target.searchParams.set("client_id", env.VATSIM_CLIENT_ID);
  target.searchParams.set("redirect_uri", env.VATSIM_REDIRECT_URI); target.searchParams.set("scope", "full_name vatsim_details");
  target.searchParams.set("state", state);
  return Response.redirect(target.toString(), 302);
}

async function finishVatsimLogin(request, env) {
  if (!env.VATSIM_CLIENT_ID || !env.VATSIM_CLIENT_SECRET || !env.VATSIM_REDIRECT_URI)
    return json({ Success: false, Error: "VATSIM OAuth is not configured." }, 503);
  const url = new URL(request.url), state = url.searchParams.get("state") || "", code = url.searchParams.get("code") || "";
  const stored = await env.DB.prepare("SELECT return_to FROM oauth_states WHERE state = ? AND expires_at > ?")
    .bind(state, new Date().toISOString()).first();
  if (!stored || !code) return json({ Success: false, Error: "Invalid or expired OAuth state." }, 400);
  await env.DB.prepare("DELETE FROM oauth_states WHERE state = ?").bind(state).run();
  const tokenBody = new URLSearchParams({ grant_type: "authorization_code", client_id: env.VATSIM_CLIENT_ID,
    client_secret: env.VATSIM_CLIENT_SECRET, redirect_uri: env.VATSIM_REDIRECT_URI, code });
  const tokenResponse = await fetch("https://auth.vatsim.net/oauth/token", { method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" }, body: tokenBody });
  if (!tokenResponse.ok) return json({ Success: false, Error: "VATSIM token exchange failed." }, 502);
  const token = await tokenResponse.json();
  const userResponse = await fetch("https://auth.vatsim.net/api/user", { headers: { Authorization: `Bearer ${token.access_token}`, Accept: "application/json" } });
  if (!userResponse.ok) return json({ Success: false, Error: "VATSIM identity lookup failed." }, 502);
  const user = (await userResponse.json()).data || {};
  const cid = String(user.cid || "").trim(), name = String(user.personal?.name_full || "").trim().slice(0, 100);
  if (!/^\d{4,12}$/.test(cid)) return json({ Success: false, Error: "VATSIM returned an invalid CID." }, 502);
  const sessionToken = crypto.randomUUID() + crypto.randomUUID();
  const now = new Date(), expires = new Date(now.getTime() + 7 * 24 * 60 * 60 * 1000);
  await env.DB.prepare("INSERT INTO auth_sessions(token_hash, vatsim_cid, vatsim_name, expires_at, created_at) VALUES (?, ?, ?, ?, ?)")
    .bind(await sha256(sessionToken), cid, name, expires.toISOString(), now.toISOString()).run();
  return new Response(null, { status: 302, headers: { Location: stored.return_to,
    "Set-Cookie": `sua_session=${encodeURIComponent(sessionToken)}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=604800` } });
}

async function logout(request, env) {
  const token = cookieValue(request, "sua_session");
  if (token) await env.DB.prepare("DELETE FROM auth_sessions WHERE token_hash = ?").bind(await sha256(token)).run();
  return new Response(null, { status: 302, headers: { Location: "/request",
    "Set-Cookie": "sua_session=; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=0" } });
}

async function requireSignedIn(request, env) {
  if (!authEnabled(env)) return { allowed: true, session: null };
  const session = await currentSession(request, env);
  return session ? { allowed: true, session } : { allowed: false, response: json({ Success: false, Error: "VATSIM login required.", LoginRequired: true }, 401) };
}

async function requireController(request, env) {
  if (!authEnabled(env)) return { allowed: true, session: null };
  const session = await currentSession(request, env);
  if (!session) return { allowed: false, response: json({ Success: false, Error: "VATSIM login required.", LoginRequired: true }, 401) };
  if (!controllerCids(env).has(String(session.vatsim_cid)))
    return { allowed: false, response: json({ Success: false, Error: "You do not have permission to manage airspace." }, 403) };
  return { allowed: true, session };
}

async function pruneElapsedDesiredWindows(db, nowDate = new Date()) {
  const nowWire = wireDate(nowDate);
  const result = await db.prepare(
    `SELECT name, source_type, source_id, windows, floor, ceiling
       FROM desired_activations
      WHERE h24 = 0 AND windows <> '[]'`
  ).all();
  const statements = [];

  for (const row of result.results || []) {
    const parsed = parseJson(row.windows);
    const stored = (Array.isArray(parsed) ? parsed : []).map((value) => String(value));
    const future = stored.filter((value) =>
      /^\d{12}-\d{12}$/.test(value) && value.slice(13) > nowWire);
    if (future.length === stored.length && future.every((value, index) => value === stored[index])) continue;

    if (!future.length && row.floor === null && row.ceiling === null) {
      statements.push(db.prepare(
        "DELETE FROM desired_activations WHERE name = ? AND source_type = ? AND source_id = ?"
      ).bind(row.name, row.source_type, row.source_id));
    } else {
      statements.push(db.prepare(
        "UPDATE desired_activations SET windows = ? WHERE name = ? AND source_type = ? AND source_id = ?"
      ).bind(JSON.stringify(future), row.name, row.source_type, row.source_id));
    }
  }

  await runStatements(db, statements);
}

async function loadDesired(db, excludedControllerId = "") {
  const nowDate = new Date();
  await pruneElapsedDesiredWindows(db, nowDate);
  const now = nowDate.toISOString();
  const result = await db.prepare(
    `SELECT name, source_type, source_id, controller_cid, h24, windows, floor, ceiling, line_pattern, ra_category, expires_at, updated_at
       FROM desired_activations
      WHERE expires_at IS NULL OR expires_at > ?
      ORDER BY updated_at`
  ).bind(now).all();

  const byName = new Map();
  for (const row of result.results || []) {
    if (excludedControllerId && row.source_type === "controller" && row.source_id === excludedControllerId)
      continue;
    let item = byName.get(row.name);
    if (!item) {
      item = { Name: row.name, H24: false, Windows: [], Floor: null, Ceiling: null, LinePattern: null, RaCategory: null, Sources: [] };
      byName.set(row.name, item);
    }
    item.H24 = item.H24 || Boolean(row.h24);
    for (const window of parseJson(row.windows)) {
      if (!item.Windows.includes(window)) item.Windows.push(window);
    }
    if (row.floor !== null && row.floor !== undefined) item.Floor = Number(row.floor);
    if (row.ceiling !== null && row.ceiling !== undefined) item.Ceiling = Number(row.ceiling);
    // Rows are ordered by updated_at, so the most recent activating controller's
    // draw style wins. Only controller sources carry one; web/NOTAM stay null.
    if (row.line_pattern !== null && row.line_pattern !== undefined && row.line_pattern !== "")
      item.LinePattern = String(row.line_pattern);
    if (/^RA[123]$/.test(String(row.ra_category || ""))) item.RaCategory = String(row.ra_category);
    item.Sources.push({ Type: row.source_type, Id: row.source_id, Cid: row.controller_cid || "" });
  }

  for (const item of byName.values()) item.Windows.sort();
  return Array.from(byName.values()).sort((a, b) => a.Name.localeCompare(b.Name));
}

async function replaceControllerActivations(db, snapshot, catalogueNames) {
  const installationId = String(snapshot?.InstallationId || "").trim().toLowerCase();
  if (!/^[a-f0-9]{32}$/.test(installationId)) return "";

  const requestedLease = Number.parseInt(snapshot?.UserLeaseSeconds || "30", 10) || 30;
  const leaseSeconds = Math.max(15, Math.min(requestedLease, 300));
  const controllerCid = /^\d{4,12}$/.test(String(snapshot?.ControllerCid || "").trim())
    ? String(snapshot.ControllerCid).trim()
    : "";
  const now = new Date();
  const nowText = now.toISOString();
  const expiresAt = new Date(now.getTime() + leaseSeconds * 1000).toISOString();
  const statements = [db.prepare(
    "DELETE FROM desired_activations WHERE source_type = 'controller' AND source_id = ?"
  ).bind(installationId)];
  const controllerRating = String(snapshot?.ControllerRating || "").trim().toUpperCase();
  const controllerFacility = String(snapshot?.ControllerFacility || "").trim().toUpperCase();
  const isObserver = controllerFacility === "OBS" || (!controllerFacility && controllerRating === "OBS");
  const userActivations = isObserver
    ? []
    : (Array.isArray(snapshot?.UserActivations) ? snapshot.UserActivations : []);

  for (const item of userActivations.slice(0, 500)) {
    const name = String(item?.Name || "").trim();
    if (!name || !catalogueNames.has(name)) continue;
    const h24 = Boolean(item.H24);
    const windows = h24 ? [] : (Array.isArray(item.Windows) ? item.Windows : [])
      .map((value) => String(value))
      .filter((value) => /^\d{12}-\d{12}$/.test(value) && value.slice(13) > value.slice(0, 12))
      .slice(0, 20)
      .sort();
    const floorValue = item.Floor === null || item.Floor === undefined ? null : Number.parseInt(item.Floor, 10);
    const ceilingValue = item.Ceiling === null || item.Ceiling === undefined ? null : Number.parseInt(item.Ceiling, 10);
    const floor = Number.isFinite(floorValue) ? floorValue : null;
    const ceiling = Number.isFinite(ceilingValue) ? ceilingValue : null;
    if (!h24 && windows.length === 0 && floor === null && ceiling === null) continue;
    if (floor !== null && ceiling !== null && ceiling < floor) continue;
    const linePattern = typeof item.LinePattern === "string" && item.LinePattern.trim()
      ? item.LinePattern.trim().slice(0, 32)
      : null;
    const raCategory = String(linePattern || "").toLowerCase() === "dashed" ? "RA1"
      : (String(linePattern || "").toLowerCase() === "dotted" ? "RA2"
        : (String(linePattern || "").toLowerCase() === "solid" ? "RA3" : null));

    statements.push(db.prepare(
      `INSERT INTO desired_activations
         (name, source_type, source_id, controller_cid, h24, windows, floor, ceiling, line_pattern, ra_category, expires_at, created_at, updated_at)
       VALUES (?, 'controller', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(name, source_type, source_id) DO UPDATE SET
         controller_cid=excluded.controller_cid, h24=excluded.h24, windows=excluded.windows, floor=excluded.floor,
         ceiling=excluded.ceiling, line_pattern=excluded.line_pattern, ra_category=excluded.ra_category,
         expires_at=excluded.expires_at, updated_at=excluded.updated_at`
    ).bind(name, installationId, controllerCid, h24 ? 1 : 0, JSON.stringify(windows), floor, ceiling,
      linePattern, raCategory, expiresAt, nowText, nowText));
  }

  await runStatements(db, statements);
  return installationId;
}

async function activeControllerCids(db, name) {
  const result = await db.prepare(
    `SELECT controller_cid FROM desired_activations
      WHERE name = ? AND source_type = 'controller' AND (expires_at IS NULL OR expires_at > ?)`
  ).bind(name, new Date().toISOString()).all();
  return Array.from(new Set((result.results || []).map((row) => row.controller_cid).filter(Boolean)));
}

// Default (dataset) timing computed from the catalogue's schedule string
// ("H24" or comma-separated "HHmm-HHmm" daily windows) at read time, so the
// stored catalogue never needs per-minute rewrites to track default state.
function defaultTiming(schedule, now) {
  const parts = String(schedule || "").split(",").map((value) => value.trim()).filter(Boolean);
  if (!parts.length) return { active: false, preActive: false };
  const nowMin = now.getUTCHours() * 60 + now.getUTCMinutes();
  let active = false;
  let preActive = false;
  for (const part of parts) {
    if (part.toUpperCase() === "H24") return { active: true, preActive: true };
    const match = part.match(/^(\d{2})(\d{2})-(\d{2})(\d{2})$/);
    if (!match) continue;
    const start = Number(match[1]) * 60 + Number(match[2]);
    const end = Number(match[3]) * 60 + Number(match[4]);
    const inWindow = end > start ? (nowMin >= start && nowMin < end) : (nowMin >= start || nowMin < end);
    if (inWindow) active = true;
    const minsUntilStart = (start - nowMin + 1440) % 1440;
    if (minsUntilStart > 0 && minsUntilStart <= 15) preActive = true;
  }
  return { active, preActive: active || preActive };
}

function desiredTiming(item) {
  if (!item) return { active: false, preActive: false };
  if (item.H24) return { active: true, preActive: true };

  const now = new Date();
  const nowWire = wireDate(now);
  const preActiveWire = wireDate(new Date(now.getTime() + 15 * 60000));
  let active = false;
  let preActive = false;
  for (const window of item.Windows || []) {
    const parts = String(window).split("-");
    if (parts.length !== 2) continue;
    if (parts[0] <= nowWire && nowWire < parts[1]) active = true;
    if (nowWire < parts[0] && parts[0] <= preActiveWire) preActive = true;
  }
  return { active, preActive: active || preActive };
}

async function areasResponse(env) {
  const [areasResult, desired] = await Promise.all([
    env.DB.prepare("SELECT * FROM areas ORDER BY type, name").all(),
    loadDesired(env.DB),
  ]);
  const desiredByName = new Map(desired.map((item) => [item.Name, item]));
  const now = new Date();

  const areas = (areasResult.results || []).map((row) => {
    const staged = desiredByName.get(row.name);
    const stagedWindows = staged?.Windows || [];
    const timing = desiredTiming(staged);
    const manual = Boolean(staged?.Sources?.some((source) => source.Type === "manual"));
    const controller = Boolean(staged?.Sources?.some((source) => source.Type === "controller"));
    const controllerCids = Array.from(new Set((staged?.Sources || [])
      .filter((source) => source.Type === "controller" && source.Cid)
      .map((source) => source.Cid)));
    const saved = Boolean(staged?.Sources?.some((source) => source.Type !== "controller"));
    const dflt = defaultTiming(row.schedule, now);
    const defaultActive = !Boolean(row.manual) && dflt.active;
    const defaultPreActive = !Boolean(row.manual) && dflt.preActive;
    return {
      Name: row.name,
      Type: areaTypeFromName(row.name, row.type),
      Floor: staged?.Floor ?? row.floor,
      Ceiling: staged?.Ceiling ?? row.ceiling,
      Daiw: Boolean(row.daiw),
      Schedule: staged?.H24 ? "H24" : (row.schedule || ""),
      Active: timing.active || defaultActive,
      PreActive: timing.preActive || defaultPreActive,
      Hidden: Boolean(row.hidden),
      Default: defaultActive || defaultPreActive,
      DefaultSuppressed: false,
      Manual: controller ? false : manual,
      Controller: controller,
      ControllerCids: controllerCids,
      ControllerLocked: controller,
      H24Manual: Boolean(staged?.H24),
      Scheduled: Boolean(stagedWindows.length),
      Windows: stagedWindows,
      LinePattern: staged?.LinePattern ?? row.line_pattern ?? null,
      RaCategory: staged?.RaCategory ?? row.ra_category ?? null,
      LevelsEdited: Boolean(staged && (staged.Floor !== null || staged.Ceiling !== null)),
      Staged: Boolean(staged),
      Saved: controller ? false : saved,
      Sources: staged?.Sources || [],
    };
  });

  return json({
    Loaded: areas.length > 0,
    Areas: areas,
    UtcTime: new Date().toISOString().slice(11, 16).replace(":", "") + "Z",
  });
}

async function requireArea(db, name) {
  if (!name) return false;
  return Boolean(await db.prepare("SELECT 1 AS found FROM areas WHERE name = ?").bind(name).first());
}

async function upsertManual(db, name, values) {
  const existing = await db.prepare(
    "SELECT h24, windows, floor, ceiling, line_pattern, ra_category, expires_at FROM desired_activations WHERE name = ? AND source_type = 'manual' AND source_id = 'web'"
  ).bind(name).first();
  const h24 = values.h24 ?? Boolean(existing?.h24);
  const windows = values.windows ?? parseJson(existing?.windows);
  const floor = values.floor !== undefined ? values.floor : (existing?.floor ?? null);
  const ceiling = values.ceiling !== undefined ? values.ceiling : (existing?.ceiling ?? null);
  const linePattern = values.linePattern !== undefined ? values.linePattern : (existing?.line_pattern ?? null);
  const raCategory = values.raCategory !== undefined ? values.raCategory : (existing?.ra_category ?? null);
  const expiresAt = values.expiresAt !== undefined ? values.expiresAt : (existing?.expires_at ?? null);
  const now = new Date().toISOString();

  await db.prepare(
    `INSERT INTO desired_activations
       (name, source_type, source_id, h24, windows, floor, ceiling, line_pattern, ra_category, expires_at, created_at, updated_at)
     VALUES (?, 'manual', 'web', ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT(name, source_type, source_id) DO UPDATE SET
       h24 = excluded.h24, windows = excluded.windows, floor = excluded.floor,
       ceiling = excluded.ceiling, line_pattern = excluded.line_pattern, ra_category = excluded.ra_category,
       expires_at = excluded.expires_at, updated_at = excluded.updated_at`
  ).bind(name, h24 ? 1 : 0, JSON.stringify(windows), floor, ceiling, linePattern, raCategory, expiresAt, now, now).run();
}

async function activateArea(request, env) {
  const url = new URL(request.url);
  const name = (url.searchParams.get("name") || "").trim();
  if (!await requireArea(env.DB, name)) return json({ Success: false, Error: `Unknown area: ${name}` }, 400);
  const requested = Number.parseInt(url.searchParams.get("minutes") || "0", 10) || 0;
  const minutes = Math.max(0, Math.min(requested, 7 * 24 * 60));
  if (minutes > 0) {
    const from = new Date();
    const to = new Date(from.getTime() + minutes * 60000);
    await upsertManual(env.DB, name, {
      h24: false,
      windows: [`${wireDate(from)}-${wireDate(to)}`],
      expiresAt: to.toISOString(),
    });
  } else {
    await upsertManual(env.DB, name, { h24: true, windows: [], expiresAt: null });
  }
  return json({ Success: true, Name: name, Staged: true });
}

async function setWindows(request, env) {
  const url = new URL(request.url);
  const name = (url.searchParams.get("name") || "").trim();
  if (!await requireArea(env.DB, name)) return json({ Success: false, Error: `Unknown area: ${name}` }, 400);
  const windows = (url.searchParams.get("windows") || "").split(",").filter(Boolean).sort();
  for (const window of windows) {
    if (!/^\d{12}-\d{12}$/.test(window)) return json({ Success: false, Error: `Invalid window: ${window}` }, 400);
    if (window.slice(13) <= window.slice(0, 12))
      return json({ Success: false, Error: `Window ends before it starts: ${window}` }, 400);
  }
  const lastEnd = windows.length ? windows[windows.length - 1].slice(13) : null;
  const expiresAt = windows.length
    ? new Date(Date.UTC(Number(lastEnd.slice(0, 4)), Number(lastEnd.slice(4, 6)) - 1,
        Number(lastEnd.slice(6, 8)), Number(lastEnd.slice(8, 10)), Number(lastEnd.slice(10, 12)))).toISOString()
    : null;
  await upsertManual(env.DB, name, { h24: false, windows, expiresAt });
  return json({ Success: true, Name: name, Staged: true });
}

async function setLevels(request, env) {
  const url = new URL(request.url);
  const name = (url.searchParams.get("name") || "").trim();
  if (!await requireArea(env.DB, name)) return json({ Success: false, Error: `Unknown area: ${name}` }, 400);
  const floorText = url.searchParams.get("floor");
  const ceilingText = url.searchParams.get("ceiling");
  const floor = floorText === null || floorText === "" ? undefined : Number.parseInt(floorText, 10);
  const ceiling = ceilingText === null || ceilingText === "" ? undefined : Number.parseInt(ceilingText, 10);
  if (floor === undefined && ceiling === undefined) return json({ Success: false, Error: "floor or ceiling required." }, 400);
  if (floor !== undefined && ceiling !== undefined && ceiling < floor)
    return json({ Success: false, Error: "Ceiling is below floor." }, 400);
  await upsertManual(env.DB, name, { floor, ceiling });
  return json({ Success: true, Name: name, Staged: true });
}

async function setCategory(request, env) {
  const url = new URL(request.url);
  const name = (url.searchParams.get("name") || "").trim();
  const raCategory = (url.searchParams.get("category") || "").trim().toUpperCase();
  if (!await requireArea(env.DB, name)) return json({ Success: false, Error: `Unknown area: ${name}` }, 400);
  if (!/^RA[123]$/.test(raCategory)) return json({ Success: false, Error: "Select RA1, RA2, or RA3." }, 400);
  await upsertManual(env.DB, name, { raCategory, linePattern: categoryLinePattern(raCategory) });
  return json({ Success: true, Name: name, RaCategory: raCategory });
}

async function createActivationRequest(request, env, session = null) {
  let body;
  try { body = await request.json(); } catch { return json({ Success: false, Error: "Invalid JSON." }, 400); }
  const submittedAreas = Array.isArray(body?.AreaNames) ? body.AreaNames : [body?.AreaName];
  const areaNames = Array.from(new Set(submittedAreas.map((value) => String(value || "").trim()).filter(Boolean))).slice(0, 50);
  const requester = String(body?.Requester || "").trim().slice(0, 80);
  const notes = String(body?.Notes || "").trim().slice(0, 500);
  const raCategory = String(body?.RaCategory || "").trim().toUpperCase();
  const start = new Date(String(body?.StartUtc || ""));
  const end = new Date(String(body?.EndUtc || ""));
  if (!areaNames.length) return json({ Success: false, Error: "Select at least one airspace area." }, 400);
  for (const areaName of areaNames) {
    if (!await requireArea(env.DB, areaName)) return json({ Success: false, Error: `Unknown area: ${areaName}` }, 400);
  }
  if (!requester) return json({ Success: false, Error: "Your name or callsign is required." }, 400);
  if (!/^RA[123]$/.test(raCategory)) return json({ Success: false, Error: "Select RA1, RA2, or RA3." }, 400);
  if (!Number.isFinite(start.getTime()) || !Number.isFinite(end.getTime()))
    return json({ Success: false, Error: "Valid start and end times are required." }, 400);
  if (end <= start) return json({ Success: false, Error: "The end time must be after the start time." }, 400);
  if (end <= new Date()) return json({ Success: false, Error: "The requested time has already ended." }, 400);
  if (end.getTime() - start.getTime() > 7 * 24 * 60 * 60 * 1000)
    return json({ Success: false, Error: "A request cannot be longer than seven days." }, 400);

  const id = crypto.randomUUID();
  const now = new Date().toISOString();
  await env.DB.prepare(
    `INSERT INTO activation_requests
       (id, area_name, area_names, requester, start_utc, end_utc, notes, ra_category, vatsim_cid, vatsim_name, status, created_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'pending', ?)`
  ).bind(id, areaNames[0], JSON.stringify(areaNames), requester, start.toISOString(), end.toISOString(), notes, raCategory,
    session?.vatsim_cid || null, session?.vatsim_name || null, now).run();
  let discordNotified = false;
  if (env.DISCORD_REQUEST_WEBHOOK_URL) {
    const categoryColor = raCategory === "RA1" ? 0x008040 : (raCategory === "RA2" ? 0xd0b000 : 0xc00000);
    const categoryStyle = raCategory === "RA1" ? "Dashed line · DAIW off"
      : (raCategory === "RA2" ? "Dotted line · DAIW on" : "Solid line · DAIW on");
    const startUnix = Math.floor(start.getTime() / 1000), endUnix = Math.floor(end.getTime() / 1000);
    const durationMinutes = Math.round((end.getTime() - start.getTime()) / 60000);
    const duration = durationMinutes >= 60 && durationMinutes % 60 === 0
      ? `${durationMinutes / 60} hour${durationMinutes === 60 ? "" : "s"}`
      : `${durationMinutes} minutes`;
    const requesterIdentity = session?.vatsim_cid
      ? `**${requester}**\n${session.vatsim_name ? `${session.vatsim_name} · ` : ""}VATSIM CID ${session.vatsim_cid}`
      : `**${requester}**`;
    const fields = [
      { name: `Requested airspace (${areaNames.length})`, value: areaNames.map((name) => `• **${name}**`).join("\n").slice(0, 1024), inline: false },
      { name: "RA category", value: `**${raCategory}**\n${categoryStyle}`, inline: true },
      { name: "Duration", value: `**${duration}**`, inline: true },
      { name: "Requested by", value: requesterIdentity.slice(0, 1024), inline: false },
      { name: "Starts", value: `<t:${startUnix}:F>\n<t:${startUnix}:R>`, inline: true },
      { name: "Ends", value: `<t:${endUnix}:F>\n<t:${endUnix}:R>`, inline: true },
    ];
    if (notes) fields.push({ name: "Notes", value: notes.slice(0, 1024), inline: false });
    try {
      const webhookResponse = await fetch(env.DISCORD_REQUEST_WEBHOOK_URL, { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: "SUA Airspace Requests", allowed_mentions: { parse: [] }, embeds: [{
          author: { name: "SUA AIRSPACE · NEW REQUEST" }, title: `${raCategory} activation requested`,
          url: "https://sua.actuallyleviticus.xyz/", description: "A new activation request is waiting for controller review.",
          color: categoryColor, fields, footer: { text: `Request ID · ${id}` }, timestamp: now,
        }] }) });
      discordNotified = webhookResponse.ok;
    } catch { /* A notification failure must not lose the stored request. */ }
  }
  return json({ Success: true, Id: id, DiscordNotified: discordNotified });
}

async function activationRequestsResponse(env) {
  const result = await env.DB.prepare(
    `SELECT id, area_name, area_names, requester, start_utc, end_utc, notes, ra_category, status, created_at, reviewed_at
       FROM activation_requests
      WHERE status = 'pending'
      ORDER BY start_utc, created_at`
  ).all();
  return json({ Success: true, Requests: (result.results || []).map((row) => ({
    Id: row.id, AreaName: row.area_name,
    AreaNames: (() => { const names = parseJson(row.area_names); return names.length ? names : [row.area_name]; })(),
    Requester: row.requester, RaCategory: row.ra_category || "RA1",
    StartUtc: row.start_utc, EndUtc: row.end_utc, Notes: row.notes,
    Status: row.status, CreatedAt: row.created_at, ReviewedAt: row.reviewed_at,
  })) });
}

async function updateActivationRequest(request, env) {
  let body;
  try { body = await request.json(); } catch { return json({ Success: false, Error: "Invalid JSON." }, 400); }
  const id = String(body?.Id || "").trim();
  const submittedAreas = Array.isArray(body?.AreaNames) ? body.AreaNames : [];
  const areaNames = Array.from(new Set(submittedAreas.map((value) => String(value || "").trim()).filter(Boolean))).slice(0, 50);
  const requester = String(body?.Requester || "").trim().slice(0, 80);
  const notes = String(body?.Notes || "").trim().slice(0, 500);
  const raCategory = String(body?.RaCategory || "").trim().toUpperCase();
  const start = new Date(String(body?.StartUtc || ""));
  const end = new Date(String(body?.EndUtc || ""));
  if (!id) return json({ Success: false, Error: "Request id is required." }, 400);
  if (!areaNames.length) return json({ Success: false, Error: "Select at least one airspace area." }, 400);
  for (const areaName of areaNames) {
    if (!await requireArea(env.DB, areaName)) return json({ Success: false, Error: `Unknown area: ${areaName}` }, 400);
  }
  if (!requester) return json({ Success: false, Error: "Requester is required." }, 400);
  if (!/^RA[123]$/.test(raCategory)) return json({ Success: false, Error: "Select RA1, RA2, or RA3." }, 400);
  if (!Number.isFinite(start.getTime()) || !Number.isFinite(end.getTime()))
    return json({ Success: false, Error: "Valid start and end times are required." }, 400);
  if (end <= start) return json({ Success: false, Error: "The end time must be after the start time." }, 400);
  if (end <= new Date()) return json({ Success: false, Error: "The requested time has already ended." }, 400);
  if (end.getTime() - start.getTime() > 7 * 24 * 60 * 60 * 1000)
    return json({ Success: false, Error: "A request cannot be longer than seven days." }, 400);

  const result = await env.DB.prepare(
    `UPDATE activation_requests
        SET area_name = ?, area_names = ?, requester = ?, start_utc = ?, end_utc = ?, notes = ?, ra_category = ?
      WHERE id = ? AND status = 'pending'`
  ).bind(areaNames[0], JSON.stringify(areaNames), requester, start.toISOString(), end.toISOString(), notes, raCategory, id).run();
  if (!result.meta?.changes) return json({ Success: false, Error: "This request is no longer pending." }, 409);
  return json({ Success: true, Id: id });
}

async function reviewActivationRequest(request, env) {
  let body;
  try { body = await request.json(); } catch { return json({ Success: false, Error: "Invalid JSON." }, 400); }
  const id = String(body?.Id || "").trim();
  const decision = String(body?.Decision || "").trim().toLowerCase();
  if (decision !== "accept" && decision !== "decline")
    return json({ Success: false, Error: "Decision must be accept or decline." }, 400);
  const row = await env.DB.prepare(
    "SELECT * FROM activation_requests WHERE id = ? AND status = 'pending'"
  ).bind(id).first();
  if (!row) return json({ Success: false, Error: "This request is no longer pending." }, 409);

  const now = new Date().toISOString();
  const statements = [];
  if (decision === "accept") {
    const start = new Date(row.start_utc);
    const end = new Date(row.end_utc);
    if (end <= new Date()) return json({ Success: false, Error: "This request has already expired." }, 409);
    const requestedAreas = parseJson(row.area_names);
    const areaNames = requestedAreas.length ? requestedAreas : [row.area_name];
    for (const areaName of areaNames) statements.push(env.DB.prepare(
      `INSERT INTO desired_activations
         (name, source_type, source_id, h24, windows, line_pattern, ra_category, expires_at, created_at, updated_at)
       VALUES (?, 'request', ?, 0, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(name, source_type, source_id) DO UPDATE SET
         windows=excluded.windows, line_pattern=excluded.line_pattern, ra_category=excluded.ra_category,
         expires_at=excluded.expires_at, updated_at=excluded.updated_at`
    ).bind(areaName, row.id, JSON.stringify([`${wireDate(start)}-${wireDate(end)}`]),
      categoryLinePattern(row.ra_category || "RA1"), row.ra_category || "RA1", end.toISOString(), now, now));
  }
  statements.push(env.DB.prepare(
    "UPDATE activation_requests SET status = ?, reviewed_at = ? WHERE id = ? AND status = 'pending'"
  ).bind(decision === "accept" ? "accepted" : "declined", now, id));
  await env.DB.batch(statements);
  return json({ Success: true, Id: id, Status: decision === "accept" ? "accepted" : "declined" });
}

async function notamsResponse(env) {
  const now = new Date().toISOString();
  const result = await env.DB.prepare(
    `SELECT n.*, CASE WHEN d.notam_id IS NULL THEN 0 ELSE 1 END AS suppressed
       FROM notams n
       LEFT JOIN notam_deactivations d ON d.notam_id = n.id
      WHERE n.end_utc IS NULL OR n.end_utc > ?
      ORDER BY n.start_utc, n.id`
  ).bind(now).all();
  return json({
    Success: true,
    Notams: (result.results || []).map((row) => ({
      Id: Number(row.id),
      Title: row.title,
      Start: row.start_utc || row.start_text,
      End: row.end_utc || row.end_text,
      Status: row.start_utc && row.start_utc > now ? "UPCOMING" : "CURRENT",
      Designators: parseJson(row.designators),
      Matched: parseJson(row.matched),
      Unmatched: parseJson(row.unmatched),
      Windows: parseJson(row.windows).map(showWindow),
      AutoStaged: !Boolean(row.suppressed),
      Suppressed: Boolean(row.suppressed),
    })),
  });
}

async function activateNotam(request, env) {
  const url = new URL(request.url);
  const id = url.searchParams.get("id") || "";
  const mode = (url.searchParams.get("mode") || "now").toLowerCase();
  const row = await env.DB.prepare("SELECT * FROM notams WHERE id = ?").bind(id).first();
  if (!row) return json({ Success: false, Error: `Unknown NOTAM id ${id}` }, 400);
  const matched = parseJson(row.matched);
  const storedWindows = parseJson(row.windows);
  const useSchedule = mode === "schedule" && storedWindows.length > 0;
  const now = new Date().toISOString();
  const statements = [env.DB.prepare("DELETE FROM notam_deactivations WHERE notam_id = ?").bind(id)];
  for (const name of matched) {
    statements.push(env.DB.prepare(
    `INSERT INTO desired_activations
       (name, source_type, source_id, h24, windows, expires_at, created_at, updated_at)
     VALUES (?, 'manual', ?, ?, ?, ?, ?, ?)
     ON CONFLICT(name, source_type, source_id) DO UPDATE SET
       h24 = excluded.h24, windows = excluded.windows, expires_at = excluded.expires_at, updated_at = excluded.updated_at`
    ).bind(name, `notam-${id}`, useSchedule ? 0 : 1, JSON.stringify(useSchedule ? storedWindows : []),
      useSchedule ? row.end_utc : null, now, now));
  }
  await runStatements(env.DB, statements);
  return json({ Success: true, Id: Number(id), Mode: useSchedule ? "schedule" : "now", Activated: matched, Unmatched: parseJson(row.unmatched) });
}

async function deactivateNotam(request, env) {
  const id = new URL(request.url).searchParams.get("id") || "";
  const row = await env.DB.prepare("SELECT matched FROM notams WHERE id = ?").bind(id).first();
  if (!row) return json({ Success: false, Error: `Unknown NOTAM id ${id}` }, 400);

  const matched = parseJson(row.matched);
  const now = new Date().toISOString();
  const statements = [env.DB.prepare(
    `INSERT INTO notam_deactivations(notam_id, created_at) VALUES (?, ?)
     ON CONFLICT(notam_id) DO UPDATE SET created_at=excluded.created_at`
  ).bind(id, now)];

  for (const name of matched) {
    statements.push(env.DB.prepare(
      `DELETE FROM desired_activations
        WHERE name = ? AND (source_type <> 'controller' OR expires_at <= ?)`
    ).bind(name, now));
  }
  await runStatements(env.DB, statements);
  return json({ Success: true, Id: Number(id), Deactivated: matched });
}

async function pluginSync(request, env) {
  let snapshot;
  try { snapshot = await request.json(); } catch { return json({ Success: false, Error: "Invalid JSON." }, 400); }

  const disconnectId = String(snapshot?.InstallationId || "").trim().toLowerCase();
  if (snapshot?.Disconnected === true && /^[a-f0-9]{32}$/.test(disconnectId)) {
    await env.DB.prepare(
      "DELETE FROM desired_activations WHERE source_type = 'controller' AND source_id = ?"
    ).bind(disconnectId).run();
    return json({ Success: true, Desired: [], SuppressedDefaults: [] });
  }

  const syncTime = new Date().toISOString();
  const catalogueLoaded = snapshot?.Loaded === true && Array.isArray(snapshot?.Areas) && snapshot.Areas.length > 0;
  const areas = catalogueLoaded ? snapshot.Areas : [];

  // The area catalogue is owned by the scheduled Worker, sourced from the
  // canonical vatSys GitHub dataset — the plugin no longer writes it. This
  // keeps the shared list consistent with what the division intends regardless
  // of a controller's local RestrictedAreas.xml, and removes the per-sync
  // 580-row upsert that dominated D1 write usage. The plugin's own area names
  // are still used below only to validate its controller activations.
  if (catalogueLoaded) {
    await env.DB.prepare(
      "INSERT INTO metadata(key, value) VALUES('plugin_last_seen', ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value"
    ).bind(syncTime).run();
  }
  const installationId = catalogueLoaded
    ? await replaceControllerActivations(env.DB, snapshot, new Set(areas.map((area) => String(area.Name || "").trim())))
    : "";
  const desired = await loadDesired(env.DB, installationId);
  // Kept empty for older plugin releases so they immediately restore any
  // defaults that a previous API version told them to suppress.
  return json({ Success: true, Desired: desired, SuppressedDefaults: [] });
}

export async function onRequest(context) {
  const path = requestedPath(context);
  const method = context.request.method.toUpperCase();

  try {
    if (path === "/auth/session" && method === "GET") return authSessionResponse(context.request, context.env);
    if (path === "/auth/login" && method === "GET") return beginVatsimLogin(context.request, context.env);
    if (path === "/auth/callback" && method === "GET") return finishVatsimLogin(context.request, context.env);
    if (path === "/auth/logout" && (method === "GET" || method === "POST")) return logout(context.request, context.env);
    if (path === "/sua/areas" && method === "GET") return areasResponse(context.env);
    if (path === "/sua/notams" && method === "GET") return notamsResponse(context.env);
    if (path === "/sua/requests" && method === "GET") {
      const auth = await requireController(context.request, context.env); if (!auth.allowed) return auth.response;
      return activationRequestsResponse(context.env);
    }
    if (path === "/plugin/sync" && method === "POST") return pluginSync(context.request, context.env);

    if (method !== "POST") return json({ Error: "Not found." }, 404);
    if (path === "/sua/requests") {
      const auth = await requireSignedIn(context.request, context.env); if (!auth.allowed) return auth.response;
      return createActivationRequest(context.request, context.env, auth.session);
    }
    const controllerAuth = await requireController(context.request, context.env);
    if (!controllerAuth.allowed) return controllerAuth.response;
    if (path === "/sua/requests/update") return updateActivationRequest(context.request, context.env);
    if (path === "/sua/requests/review") return reviewActivationRequest(context.request, context.env);
    if (path === "/sua/activate") return activateArea(context.request, context.env);
    if (path === "/sua/deactivate") {
      const name = (new URL(context.request.url).searchParams.get("name") || "").trim();
      const lockedBy = await activeControllerCids(context.env.DB, name);
      if (lockedBy.length) {
        return json({
          Success: false,
          Error: `Locked by connected controller${lockedBy.length > 1 ? "s" : ""}: ${lockedBy.join(", ")}`,
          Locked: true,
          ControllerCids: lockedBy,
        }, 423);
      }
      await context.env.DB.prepare("DELETE FROM desired_activations WHERE name = ?").bind(name).run();
      return json({ Success: true, Name: name, Staged: false });
    }
    if (path === "/sua/deactivateall") {
      // Clears shared website/manual activations only. Airspace NOTAMs are always
      // auto-scheduled from their listed times, so they are not paused here and
      // re-stage automatically on the next automation run.
      const now = new Date().toISOString();
      await context.env.DB.prepare(
        "DELETE FROM desired_activations WHERE source_type <> 'controller' OR expires_at <= ?"
      ).bind(now).run();
      return json({ Success: true });
    }
    if (path === "/sua/windows") return setWindows(context.request, context.env);
    if (path === "/sua/levels") return setLevels(context.request, context.env);
    if (path === "/sua/category") return setCategory(context.request, context.env);
    if (path === "/sua/notams/activate") return activateNotam(context.request, context.env);
    if (path === "/sua/notams/deactivate") return deactivateNotam(context.request, context.env);
    return json({ Error: "Not found." }, 404);
  } catch (error) {
    return json({ Success: false, Error: error?.message || "Cloud API error." }, 500);
  }
}
