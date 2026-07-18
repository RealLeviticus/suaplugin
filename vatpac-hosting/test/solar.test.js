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

test("D193 PEARCE changes correctly across seasons and a future year", () => {
  const latitude = -30.756527777777777, longitude = 115.83583333333334;
  const summer = solarEventsUtc(new Date("2026-01-15T00:00:00Z"), latitude, longitude);
  const winter = solarEventsUtc(new Date("2026-07-15T00:00:00Z"), latitude, longitude);
  const future = solarEventsUtc(new Date("2030-12-15T00:00:00Z"), latitude, longitude);
  assert.equal(summer.sunrise.toISOString().slice(0, 16), "2026-01-14T21:27");
  assert.equal(summer.sunset.toISOString().slice(0, 16), "2026-01-15T11:23");
  assert.equal(winter.sunrise.toISOString().slice(0, 16), "2026-07-14T23:12");
  assert.equal(winter.sunset.toISOString().slice(0, 16), "2026-07-15T09:32");
  assert.equal(future.sunrise.toISOString().slice(0, 16), "2030-12-14T21:08");
  assert.equal(future.sunset.toISOString().slice(0, 16), "2030-12-15T11:15");
  assert.ok((summer.sunset - summer.sunrise) > (winter.sunset - winter.sunrise));
});
