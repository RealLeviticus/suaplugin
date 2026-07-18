const NOTAM_URL = "https://cms.vatpac.org/api/notams?pagination%5BpageSize%5D=50&sort%5B1%5D=createdAt%3Adesc";
const designatorRegex = /\b([RDM])(\d{2,3})([A-Z]{0,12})(?:\s*-\s*([A-Z])\b)?/g;
const timeRegex = /\b(\d{6})\s*(\d{2})\s*(\d{4})\s*[zZ]/g;

function decodeHtml(value) {
  return String(value || "")
    .replace(/<[^>]+>/g, " ")
    .replace(/&nbsp;/gi, " ").replace(/&amp;/gi, "&").replace(/&lt;/gi, "<")
    .replace(/&gt;/gi, ">").replace(/&#39;/gi, "'").replace(/&quot;/gi, '"');
}

function parseCmsDate(value) {
  if (!value) return null;
  const match = String(value).trim().match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})(?:\s+(\d{1,2}):(\d{2})(?::\d{2})?\s*(AM|PM))?$/i);
  if (match) {
    let hour = Number(match[4] || 0);
    const suffix = (match[6] || "").toUpperCase();
    if (suffix === "PM" && hour < 12) hour += 12;
    if (suffix === "AM" && hour === 12) hour = 0;
    return new Date(Date.UTC(Number(match[3]), Number(match[2]) - 1, Number(match[1]), hour, Number(match[5] || 0)));
  }
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

function extractDesignators(text) {
  const result = [];
  const add = (value) => { if (!result.includes(value)) result.push(value); };
  for (const match of text.matchAll(designatorRegex)) {
    const prefix = match[1];
    const number = match[2];
    const suffixes = match[3] || "";
    const rangeEnd = match[4] || "";
    if (!suffixes && !rangeEnd) add(prefix + number);
    else if (rangeEnd) {
      for (let i = 0; i < suffixes.length - 1; i++) add(prefix + number + suffixes[i]);
      const start = suffixes ? suffixes.charCodeAt(suffixes.length - 1) : 65;
      for (let code = start; code <= rangeEnd.charCodeAt(0); code++) add(prefix + number + String.fromCharCode(code));
    } else {
      for (const suffix of suffixes) add(prefix + number + suffix);
    }
  }
  return result;
}

function wireDate(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return date.getUTCFullYear() + pad(date.getUTCMonth() + 1) + pad(date.getUTCDate()) +
    pad(date.getUTCHours()) + pad(date.getUTCMinutes());
}

function extractWindows(text) {
  const stamps = [];
  for (const match of text.matchAll(timeRegex)) {
    const value = match[1] + match[2] + match[3];
    stamps.push(new Date(Date.UTC(Number(value.slice(0, 4)), Number(value.slice(4, 6)) - 1,
      Number(value.slice(6, 8)), Number(value.slice(8, 10)), Number(value.slice(10, 12)))));
  }
  const windows = [];
  for (let i = 0; i + 1 < stamps.length; i += 2) {
    if (stamps[i + 1] > stamps[i]) windows.push(`${wireDate(stamps[i])}-${wireDate(stamps[i + 1])}`);
  }
  return windows;
}

function matchAreas(designators, areaNames) {
  const byToken = new Map();
  for (const name of areaNames) {
    const token = name.split(" ")[0].toUpperCase();
    if (!byToken.has(token)) byToken.set(token, []);
    byToken.get(token).push(name);
  }
  const matched = [];
  const unmatched = [];
  for (const designator of designators) {
    const exact = byToken.get(designator.toUpperCase());
    if (exact) {
      for (const name of exact) if (!matched.includes(name)) matched.push(name);
      continue;
    }
    const prefixed = [];
    for (const [token, names] of byToken) {
      const remainder = token.slice(designator.length);
      if (token.startsWith(designator.toUpperCase()) && remainder && /^[A-Z]+$/.test(remainder)) prefixed.push(...names);
    }
    if (prefixed.length) for (const name of prefixed) if (!matched.includes(name)) matched.push(name);
    else unmatched.push(designator);
  }
  return { matched, unmatched };
}

const DATASET_URL = "https://raw.githubusercontent.com/vatSys/australia-dataset/master/RestrictedAreas.xml";

// FNV-1a 32-bit: a cheap stable content hash used to skip DB work when neither
// the division dataset nor the NOTAM feed has changed since the last run.
function fnv1a(str) {
  let hash = 0x811c9dc5;
  for (let i = 0; i < str.length; i++) {
    hash ^= str.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16);
}

function xmlAttr(tag, name) {
  const match = tag.match(new RegExp(`${name}="([^"]*)"`));
  return match ? match[1] : "";
}

// Parse the vatSys australia-dataset RestrictedAreas.xml into catalogue rows.
function parseDatasetAreas(xml) {
  const areas = [];
  const blockRegex = /<RestrictedArea\b([^>]*)>([\s\S]*?)<\/RestrictedArea>/g;
  let block;
  while ((block = blockRegex.exec(xml))) {
    const attrs = block[1];
    const inner = block[2];
    const name = xmlAttr(attrs, "Name").trim();
    if (!name) continue;
    const schedule = [];
    const activationRegex = /<Activation\b([^>]*)\/>/g;
    let act;
    while ((act = activationRegex.exec(inner))) {
      const a = act[1];
      if (xmlAttr(a, "H24").toLowerCase() === "true") {
        if (!schedule.includes("H24")) schedule.push("H24");
      } else {
        const start = xmlAttr(a, "Start");
        const end = xmlAttr(a, "End");
        if (/^\d{4}$/.test(start) && /^\d{4}$/.test(end)) {
          const token = `${start}-${end}`;
          if (!schedule.includes(token)) schedule.push(token);
        }
      }
    }
    areas.push({
      name,
      type: xmlAttr(attrs, "Type").trim(),
      floor: parseInt(xmlAttr(attrs, "AltitudeFloor"), 10) || 0,
      ceiling: parseInt(xmlAttr(attrs, "AltitudeCeiling"), 10) || 0,
      daiw: xmlAttr(attrs, "DAIWEnabled").toLowerCase() === "true",
      hidden: xmlAttr(attrs, "LinePattern") === "None",
      schedule: schedule.join(", "),
    });
  }
  return areas;
}

// The website's restricted-area catalogue comes from the canonical vatSys
// dataset on GitHub, not from whatever RestrictedAreas.xml a controller has
// loaded. A content hash means the 580 rows are only rewritten when the
// division actually changes the dataset, so steady-state D1 writes are zero.
async function refreshAreas(env) {
  const response = await fetch(DATASET_URL, { headers: { Accept: "application/xml", "User-Agent": "SUA-Airspace-Cloudflare/1.0" } });
  if (!response.ok) throw new Error(`Dataset request failed with ${response.status}`);
  const xml = await response.text();
  const hash = fnv1a(xml);
  const stored = await env.DB.prepare("SELECT value FROM metadata WHERE key = 'dataset_hash'").first();
  if (stored && stored.value === hash) return { Success: true, Areas: 0, Unchanged: true };

  const areas = parseDatasetAreas(xml);
  if (areas.length < 100) throw new Error(`Dataset parse returned only ${areas.length} areas; refusing to overwrite.`);

  const now = new Date().toISOString();
  for (let offset = 0; offset < areas.length; offset += 50) {
    const statements = areas.slice(offset, offset + 50).map((area) => env.DB.prepare(
      `INSERT INTO areas
        (name, type, floor, ceiling, daiw, schedule, active, pre_active, hidden, manual,
         h24_manual, scheduled, windows, levels_edited, last_seen)
       VALUES (?, ?, ?, ?, ?, ?, 0, 0, ?, 0, 0, 0, '[]', 0, ?)
       ON CONFLICT(name) DO UPDATE SET
         type=excluded.type, floor=excluded.floor, ceiling=excluded.ceiling, daiw=excluded.daiw,
         schedule=excluded.schedule, hidden=excluded.hidden, last_seen=excluded.last_seen`
    ).bind(area.name, area.type, area.floor, area.ceiling, area.daiw ? 1 : 0, area.schedule, area.hidden ? 1 : 0, now));
    if (statements.length) await env.DB.batch(statements);
  }
  await env.DB.batch([
    env.DB.prepare("INSERT INTO metadata(key, value) VALUES('dataset_hash', ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value").bind(hash),
    // Force the NOTAM matcher to re-run against the new catalogue.
    env.DB.prepare("DELETE FROM metadata WHERE key = 'notam_fingerprint'"),
  ]);
  return { Success: true, Areas: areas.length, Unchanged: false };
}

async function refresh(env) {
  const response = await fetch(NOTAM_URL, { headers: { Accept: "application/json", "User-Agent": "SUA-Airspace-Cloudflare/1.0" } });
  if (!response.ok) throw new Error(`VATPAC NOTAM request failed with ${response.status}`);
  const payload = await response.json();
  const now = new Date();

  // Extract airspace NOTAM essentials without touching the DB so an unchanged
  // upstream feed skips every read and write below.
  const extracted = [];
  for (const item of payload.data || []) {
    const attrs = item.attributes || {};
    if (!String(attrs.type || "").toLowerCase().includes("airspace")) continue;
    const end = parseCmsDate(attrs.end);
    if (end && end <= now) continue;
    const content = decodeHtml(attrs.content);
    const designators = extractDesignators(content);
    if (!designators.length) continue;
    let windows = extractWindows(content).filter((window) => window.slice(13) > wireDate(now));
    const start = parseCmsDate(attrs.start);
    if (!windows.length && end && end > now) windows = [`${wireDate(start && start > now ? start : now)}-${wireDate(end)}`];
    extracted.push({
      id: String(item.id || ""), title: String(attrs.title || ""), startText: String(attrs.start || ""),
      endText: String(attrs.end || ""), start, end, designators, content, windows,
    });
  }

  const datasetHashRow = await env.DB.prepare("SELECT value FROM metadata WHERE key = 'dataset_hash'").first();
  const fingerprint = fnv1a(JSON.stringify(extracted.map((item) =>
    [item.id, item.title, item.designators, item.windows, item.end ? item.end.toISOString() : null])) +
    "|" + (datasetHashRow ? datasetHashRow.value : ""));
  const storedFingerprint = await env.DB.prepare("SELECT value FROM metadata WHERE key = 'notam_fingerprint'").first();
  if (storedFingerprint && storedFingerprint.value === fingerprint) {
    return { Success: true, Notams: extracted.length, Unchanged: true };
  }

  const areaResult = await env.DB.prepare("SELECT name FROM areas").all();
  const areaNames = (areaResult.results || []).map((row) => row.name);
  const parsed = extracted.map((item) => {
    const { matched, unmatched } = matchAreas(item.designators, areaNames);
    return { ...item, matched, unmatched };
  });

  const refreshedAt = new Date().toISOString();
  const statements = [env.DB.prepare("DELETE FROM desired_activations WHERE source_type = 'notam'")];
  const liveIds = parsed.map((item) => item.id);
  if (liveIds.length) {
    statements.push(env.DB.prepare(`DELETE FROM notams WHERE id NOT IN (${liveIds.map(() => "?").join(",")})`).bind(...liveIds));
    statements.push(env.DB.prepare(`DELETE FROM notam_deactivations WHERE notam_id NOT IN (${liveIds.map(() => "?").join(",")})`).bind(...liveIds));
  } else {
    statements.push(env.DB.prepare("DELETE FROM notams"));
    statements.push(env.DB.prepare("DELETE FROM notam_deactivations"));
  }

  for (const item of parsed) {
    statements.push(env.DB.prepare(
      `INSERT INTO notams(id, title, start_text, end_text, start_utc, end_utc, designators, matched, unmatched, windows, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(id) DO UPDATE SET title=excluded.title, start_text=excluded.start_text,
         end_text=excluded.end_text, start_utc=excluded.start_utc, end_utc=excluded.end_utc,
         designators=excluded.designators, matched=excluded.matched, unmatched=excluded.unmatched,
         windows=excluded.windows, updated_at=excluded.updated_at`
    ).bind(item.id, item.title, item.startText, item.endText, item.start?.toISOString() || null,
      item.end?.toISOString() || null, JSON.stringify(item.designators), JSON.stringify(item.matched),
      JSON.stringify(item.unmatched), JSON.stringify(item.windows), refreshedAt));

    // Every matched airspace NOTAM is scheduled automatically from its listed
    // windows and re-staged on each run, so it stays scheduled for its life.
    for (const name of item.matched) {
      if (!item.windows.length) continue;
      statements.push(env.DB.prepare(
        `INSERT INTO desired_activations
          (name, source_type, source_id, h24, windows, expires_at, created_at, updated_at)
         VALUES (?, 'notam', ?, 0, ?, ?, ?, ?)`
      ).bind(name, item.id, JSON.stringify(item.windows), item.end?.toISOString() || null, refreshedAt, refreshedAt));
    }
  }
  statements.push(env.DB.prepare(
    "INSERT INTO metadata(key, value) VALUES('notam_fingerprint', ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value"
  ).bind(fingerprint));
  statements.push(env.DB.prepare(
    "INSERT INTO metadata(key, value) VALUES('notams_last_refreshed', ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value"
  ).bind(refreshedAt));
  await env.DB.batch(statements);
  return {
    Success: true,
    Notams: parsed.length,
    AutoStagedAreas: parsed.reduce((sum, item) => sum + item.matched.length, 0),
  };
}

// Refresh the catalogue first (so NOTAM matching sees the current dataset), then
// the NOTAMs. Each is independent — one failing must not block the other.
async function refreshAll(env) {
  const results = {};
  try { results.Areas = await refreshAreas(env); }
  catch (error) { results.Areas = { Success: false, Error: error?.message || "Areas refresh failed." }; }
  try { results.Notams = await refresh(env); }
  catch (error) { results.Notams = { Success: false, Error: error?.message || "NOTAM refresh failed." }; }
  return results;
}

export default {
  async scheduled(_controller, env, context) { context.waitUntil(refreshAll(env)); },
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/refresh") return new Response("Not found", { status: 404 });
    if (!env.SUA_SYNC_TOKEN || request.headers.get("X-SUA-Sync-Token") !== env.SUA_SYNC_TOKEN)
      return Response.json({ Success: false, Error: "Unauthorized." }, { status: 401 });
    return Response.json(await refreshAll(env));
  },
};
