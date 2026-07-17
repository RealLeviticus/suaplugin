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

function requestedPath(context) {
  const path = context.params.path;
  return "/" + (Array.isArray(path) ? path.join("/") : String(path || ""));
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
    `SELECT name, source_type, source_id, controller_cid, h24, windows, floor, ceiling, expires_at, updated_at
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
      item = { Name: row.name, H24: false, Windows: [], Floor: null, Ceiling: null, Sources: [] };
      byName.set(row.name, item);
    }
    item.H24 = item.H24 || Boolean(row.h24);
    for (const window of parseJson(row.windows)) {
      if (!item.Windows.includes(window)) item.Windows.push(window);
    }
    if (row.floor !== null && row.floor !== undefined) item.Floor = Number(row.floor);
    if (row.ceiling !== null && row.ceiling !== undefined) item.Ceiling = Number(row.ceiling);
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

    statements.push(db.prepare(
      `INSERT INTO desired_activations
         (name, source_type, source_id, controller_cid, h24, windows, floor, ceiling, expires_at, created_at, updated_at)
       VALUES (?, 'controller', ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(name, source_type, source_id) DO UPDATE SET
         controller_cid=excluded.controller_cid, h24=excluded.h24, windows=excluded.windows, floor=excluded.floor,
         ceiling=excluded.ceiling, expires_at=excluded.expires_at, updated_at=excluded.updated_at`
    ).bind(name, installationId, controllerCid, h24 ? 1 : 0, JSON.stringify(windows), floor, ceiling,
      expiresAt, nowText, nowText));
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
    const defaultActive = !Boolean(row.manual) && Boolean(row.active);
    const defaultPreActive = !Boolean(row.manual) && Boolean(row.pre_active);
    return {
      Name: row.name,
      Type: row.type,
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
    "SELECT h24, windows, floor, ceiling, expires_at FROM desired_activations WHERE name = ? AND source_type = 'manual' AND source_id = 'web'"
  ).bind(name).first();
  const h24 = values.h24 ?? Boolean(existing?.h24);
  const windows = values.windows ?? parseJson(existing?.windows);
  const floor = values.floor !== undefined ? values.floor : (existing?.floor ?? null);
  const ceiling = values.ceiling !== undefined ? values.ceiling : (existing?.ceiling ?? null);
  const expiresAt = values.expiresAt !== undefined ? values.expiresAt : (existing?.expires_at ?? null);
  const now = new Date().toISOString();

  await db.prepare(
    `INSERT INTO desired_activations
       (name, source_type, source_id, h24, windows, floor, ceiling, expires_at, created_at, updated_at)
     VALUES (?, 'manual', 'web', ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT(name, source_type, source_id) DO UPDATE SET
       h24 = excluded.h24, windows = excluded.windows, floor = excluded.floor,
       ceiling = excluded.ceiling, expires_at = excluded.expires_at, updated_at = excluded.updated_at`
  ).bind(name, h24 ? 1 : 0, JSON.stringify(windows), floor, ceiling, expiresAt, now, now).run();
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

  for (let offset = 0; offset < areas.length; offset += 50) {
    const statements = areas.slice(offset, offset + 50).map((area) => env.DB.prepare(
      `INSERT INTO areas
        (name, type, floor, ceiling, daiw, schedule, active, pre_active, hidden, manual,
         h24_manual, scheduled, windows, levels_edited, last_seen)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(name) DO UPDATE SET
         type=excluded.type, floor=excluded.floor, ceiling=excluded.ceiling, daiw=excluded.daiw,
         schedule=excluded.schedule, active=excluded.active, pre_active=excluded.pre_active,
         hidden=excluded.hidden, manual=excluded.manual, h24_manual=excluded.h24_manual,
         scheduled=excluded.scheduled, windows=excluded.windows, levels_edited=excluded.levels_edited,
         last_seen=excluded.last_seen`
    ).bind(area.Name, area.Type, area.Floor, area.Ceiling, area.Daiw ? 1 : 0, area.Schedule || "",
      area.Active ? 1 : 0, area.PreActive ? 1 : 0, area.Hidden ? 1 : 0, area.Manual ? 1 : 0,
      area.H24Manual ? 1 : 0, area.Scheduled ? 1 : 0, JSON.stringify(area.Windows || []),
      area.LevelsEdited ? 1 : 0, syncTime));
    if (statements.length) await env.DB.batch(statements);
  }

  // The catalogue is shared by every website user and plugin installation.
  // A plugin may add or refresh entries, but must never prune entries supplied
  // by another installation just because its local profile differs.
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
    if (path === "/sua/areas" && method === "GET") return areasResponse(context.env);
    if (path === "/sua/notams" && method === "GET") return notamsResponse(context.env);
    if (path === "/plugin/sync" && method === "POST") return pluginSync(context.request, context.env);

    if (method !== "POST") return json({ Error: "Not found." }, 404);
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
      const notams = await context.env.DB.prepare(
        "SELECT id FROM notams WHERE end_utc IS NULL OR end_utc > ?"
      ).bind(new Date().toISOString()).all();
      const now = new Date().toISOString();
      const statements = [context.env.DB.prepare(
        "DELETE FROM desired_activations WHERE source_type <> 'controller' OR expires_at <= ?"
      ).bind(now)];
      for (const row of notams.results || []) {
        statements.push(context.env.DB.prepare(
          `INSERT INTO notam_deactivations(notam_id, created_at) VALUES (?, ?)
           ON CONFLICT(notam_id) DO UPDATE SET created_at=excluded.created_at`
        ).bind(row.id, now));
      }
      await runStatements(context.env.DB, statements);
      return json({ Success: true });
    }
    if (path === "/sua/windows") return setWindows(context.request, context.env);
    if (path === "/sua/levels") return setLevels(context.request, context.env);
    if (path === "/sua/notams/activate") return activateNotam(context.request, context.env);
    if (path === "/sua/notams/deactivate") return deactivateNotam(context.request, context.env);
    return json({ Error: "Not found." }, 404);
  } catch (error) {
    return json({ Success: false, Error: error?.message || "Cloud API error." }, 500);
  }
}
