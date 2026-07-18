import assert from "node:assert/strict";
import test from "node:test";
import { solarEventsUtc, solarWindows } from "../../cloudflare-pages/functions/api/solar.js";

test("calculates sunrise before sunset for an Australian area", () => {
  const events = solarEventsUtc(new Date("2026-07-18T00:00:00Z"), -33.86, 151.21);
  assert.ok(events);
  assert.ok(events.sunrise < events.sunset);
  assert.ok((events.sunset - events.sunrise) / 3600000 > 9);
  assert.ok((events.sunset - events.sunrise) / 3600000 < 11);
});

test("HJ and HN produce recurring complementary dated windows", () => {
  const now = new Date("2026-07-18T00:00:00Z");
  const hj = solarWindows("HJ", -33.86, 151.21, now);
  const hn = solarWindows("HN", -33.86, 151.21, now);
  assert.ok(hj.length >= 3);
  assert.ok(hn.length >= 3);
  assert.match(hj[0], /^\d{12}-\d{12}$/);
  assert.match(hn[0], /^\d{12}-\d{12}$/);
});
