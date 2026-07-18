import http from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { onRequest } from "../../cloudflare-pages/functions/api/[[path]].js";
import automation from "../../cloudflare-automation/src/index.js";
import { createDatabase } from "./db.js";

const directory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(directory, "../..");
const publicDirectory = path.join(root, "cloudflare-pages", "public");
const migrationsDirectory = path.join(root, "vatpac-hosting", "migrations");
const port = Number.parseInt(process.env.PORT || "8080", 10);
const publicBaseUrl = String(process.env.PUBLIC_BASE_URL || `http://localhost:${port}`).replace(/\/$/, "");
const trustProxy = String(process.env.TRUST_PROXY || "true").toLowerCase() === "true";
const databaseSsl = String(process.env.DATABASE_SSL || "false").toLowerCase() === "true";
const databaseUrl = process.env.DATABASE_URL || (process.env.PGHOST
  ? `postgresql://${encodeURIComponent(process.env.PGUSER || "postgres")}:${encodeURIComponent(process.env.PGPASSWORD || "")}@${process.env.PGHOST}:${process.env.PGPORT || "5432"}/${encodeURIComponent(process.env.PGDATABASE || "postgres")}`
  : "");
const { pool, DB } = await createDatabase({ databaseUrl, ssl: databaseSsl, migrationsDirectory });
const env = { ...process.env, DB };

const contentTypes = new Map([
  [".html", "text/html; charset=utf-8"], [".js", "text/javascript; charset=utf-8"],
  [".css", "text/css; charset=utf-8"], [".json", "application/json; charset=utf-8"],
  [".geojson", "application/geo+json; charset=utf-8"], [".png", "image/png"],
  [".svg", "image/svg+xml"], [".ico", "image/x-icon"],
]);

function requestUrl(request) {
  if (process.env.PUBLIC_BASE_URL) return new URL(request.url, publicBaseUrl).toString();
  const protocol = trustProxy ? String(request.headers["x-forwarded-proto"] || "http").split(",")[0].trim() : "http";
  const host = trustProxy ? String(request.headers["x-forwarded-host"] || request.headers.host) : request.headers.host;
  return new URL(request.url, `${protocol}://${host}`).toString();
}

async function webRequest(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const body = chunks.length ? Buffer.concat(chunks) : undefined;
  const method = request.method || "GET";
  return new Request(requestUrl(request), { method, headers: request.headers, body: method === "GET" || method === "HEAD" ? undefined : body });
}

async function sendWebResponse(response, output) {
  output.statusCode = response.status;
  response.headers.forEach((value, name) => output.setHeader(name, value));
  output.end(Buffer.from(await response.arrayBuffer()));
}

async function serveStatic(urlPath, output) {
  const routes = new Map([["/", "index.html"], ["/request", "request.html"], ["/map", "map.html"]]);
  const relative = routes.get(urlPath) || urlPath.replace(/^\/+/, "");
  const target = path.resolve(publicDirectory, relative);
  if (target !== publicDirectory && !target.startsWith(publicDirectory + path.sep)) return false;
  try {
    if (!(await stat(target)).isFile()) return false;
    output.statusCode = 200;
    output.setHeader("Content-Type", contentTypes.get(path.extname(target).toLowerCase()) || "application/octet-stream");
    output.setHeader("Cache-Control", path.extname(target) === ".html" ? "no-cache" : "public, max-age=3600");
    output.end(await readFile(target));
    return true;
  } catch { return false; }
}

async function runAutomation() {
  let task = Promise.resolve();
  await automation.scheduled(null, env, { waitUntil(value) { task = Promise.resolve(value); } });
  await task;
}

let automationRunning = false;
async function guardedAutomation() {
  if (automationRunning) return;
  automationRunning = true;
  try { console.log("Starting scheduled catalogue and NOTAM refresh"); await runAutomation(); console.log("Scheduled refresh complete"); }
  catch (error) { console.error("Scheduled refresh failed", error); }
  finally { automationRunning = false; }
}

const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url || "/", publicBaseUrl);
    if (url.pathname === "/healthz") {
      await pool.query("SELECT 1");
      response.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
      response.end(JSON.stringify({ status: "ok" }));
      return;
    }
    if (url.pathname === "/api/automation/refresh" && request.method === "POST") {
      const incoming = await webRequest(request);
      const refreshRequest = new Request(new URL("/refresh", publicBaseUrl), {
        method: "POST", headers: incoming.headers, body: await incoming.arrayBuffer(),
      });
      await sendWebResponse(await automation.fetch(refreshRequest, env), response);
      return;
    }
    if (url.pathname.startsWith("/api/")) {
      const apiPath = url.pathname.slice(5).split("/").filter(Boolean);
      await sendWebResponse(await onRequest({ request: await webRequest(request), env, params: { path: apiPath } }), response);
      return;
    }
    if (await serveStatic(url.pathname, response)) return;
    response.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" }); response.end("Not found");
  } catch (error) {
    console.error(error);
    if (!response.headersSent) response.writeHead(500, { "Content-Type": "application/json; charset=utf-8" });
    response.end(JSON.stringify({ Success: false, Error: "Internal server error." }));
  }
});

server.listen(port, "0.0.0.0", () => console.log(`SUA Airspace site listening on port ${port}`));

if (String(process.env.AUTOMATION_ENABLED || "true").toLowerCase() === "true") {
  const seconds = Math.max(30, Number.parseInt(process.env.AUTOMATION_INTERVAL_SECONDS || "60", 10) || 60);
  setInterval(guardedAutomation, seconds * 1000).unref();
  if (String(process.env.AUTOMATION_RUN_ON_START || "true").toLowerCase() === "true") setTimeout(guardedAutomation, 1000).unref();
}

async function shutdown(signal) {
  console.log(`${signal} received; shutting down`);
  server.close(async () => { await pool.end(); process.exit(0); });
  setTimeout(() => process.exit(1), 10000).unref();
}
process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("SIGINT", () => shutdown("SIGINT"));
