import { readFile, readdir } from "node:fs/promises";
import path from "node:path";
import { Pool } from "pg";

export function postgresSql(sql) {
  let output = "", parameter = 0, quote = null;
  for (let index = 0; index < sql.length; index++) {
    const character = sql[index];
    if (quote) {
      output += character;
      if (character === quote) {
        if (sql[index + 1] === quote) output += sql[++index];
        else quote = null;
      }
    } else if (character === "'" || character === '"') {
      quote = character; output += character;
    } else if (character === "?") {
      output += `$${++parameter}`;
    } else output += character;
  }
  return output;
}

class Statement {
  constructor(database, sql, values = []) { this.database = database; this.sql = sql; this.values = values; }
  bind(...values) { return new Statement(this.database, this.sql, values); }
  async execute(client = this.database.pool) { return client.query(postgresSql(this.sql), this.values); }
  async run() { const result = await this.execute(); return { success: true, meta: { changes: result.rowCount || 0 } }; }
  async all() { const result = await this.execute(); return { success: true, results: result.rows || [] }; }
  async first() { const result = await this.execute(); return result.rows?.[0] || null; }
}

export class PostgresD1Adapter {
  constructor(pool) { this.pool = pool; }
  prepare(sql) { return new Statement(this, sql); }
  async batch(statements) {
    const client = await this.pool.connect();
    try {
      await client.query("BEGIN");
      const results = [];
      for (const statement of statements) {
        const result = await statement.execute(client);
        results.push({ success: true, results: result.rows || [], meta: { changes: result.rowCount || 0 } });
      }
      await client.query("COMMIT");
      return results;
    } catch (error) {
      await client.query("ROLLBACK");
      throw error;
    } finally { client.release(); }
  }
}

export async function createDatabase({ databaseUrl, ssl = false, migrationsDirectory }) {
  if (!databaseUrl) throw new Error("DATABASE_URL is required.");
  const pool = new Pool({ connectionString: databaseUrl, ssl: ssl ? { rejectUnauthorized: false } : false });
  await pool.query("SELECT 1");
  await pool.query("CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW())");
  const files = (await readdir(migrationsDirectory)).filter((name) => name.endsWith(".sql")).sort();
  for (const name of files) {
    const existing = await pool.query("SELECT 1 FROM schema_migrations WHERE name = $1", [name]);
    if (existing.rowCount) continue;
    const client = await pool.connect();
    try {
      await client.query("BEGIN");
      await client.query(await readFile(path.join(migrationsDirectory, name), "utf8"));
      await client.query("INSERT INTO schema_migrations(name) VALUES ($1)", [name]);
      await client.query("COMMIT");
      console.log(`Applied PostgreSQL migration ${name}`);
    } catch (error) {
      await client.query("ROLLBACK");
      throw error;
    } finally { client.release(); }
  }
  return { pool, DB: new PostgresD1Adapter(pool) };
}
