import assert from "node:assert/strict";
import test from "node:test";
import { PostgresD1Adapter, postgresSql } from "../src/db.js";

test("converts D1 placeholders without changing quoted question marks", () => {
  assert.equal(
    postgresSql("SELECT '?' AS literal, value FROM things WHERE a = ? AND b = ?"),
    "SELECT '?' AS literal, value FROM things WHERE a = $1 AND b = $2",
  );
});

test("exposes D1-compatible all, first, and run results", async () => {
  const queries = [];
  const pool = { async query(sql, values) { queries.push({ sql, values }); return { rows: [{ value: "ok" }], rowCount: 1 }; } };
  const db = new PostgresD1Adapter(pool);
  const statement = db.prepare("SELECT value FROM metadata WHERE key = ?").bind("test");
  assert.deepEqual(await statement.first(), { value: "ok" });
  assert.deepEqual((await statement.all()).results, [{ value: "ok" }]);
  assert.equal((await statement.run()).meta.changes, 1);
  assert.deepEqual(queries[0], { sql: "SELECT value FROM metadata WHERE key = $1", values: ["test"] });
});
