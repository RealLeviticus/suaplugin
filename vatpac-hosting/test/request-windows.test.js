import test from "node:test";
import assert from "node:assert/strict";
import { categoryLinePattern, requestWindows } from "../../cloudflare-pages/functions/api/[[path]].js";

test("request slots are sorted and preserved independently", () => {
  const result = requestWindows({ Windows: [
    { StartUtc: "2030-07-03T09:00:00Z", EndUtc: "2030-07-03T10:00:00Z" },
    { StartUtc: "2030-07-03T05:00:00Z", EndUtc: "2030-07-03T06:00:00Z" },
  ] });
  assert.deepEqual(result.windows.map((slot) => slot.wire), [
    "203007030500-203007030600",
    "203007030900-203007031000",
  ]);
});

test("request slots cannot overlap", () => {
  const result = requestWindows({ Windows: [
    { StartUtc: "2030-07-03T05:00:00Z", EndUtc: "2030-07-03T07:00:00Z" },
    { StartUtc: "2030-07-03T06:00:00Z", EndUtc: "2030-07-03T08:00:00Z" },
  ] });
  assert.equal(result.error, "Activation slots cannot overlap.");
});

test("legacy single-window requests remain supported", () => {
  const result = requestWindows({ StartUtc: "2030-07-03T05:00:00Z", EndUtc: "2030-07-03T06:00:00Z" });
  assert.equal(result.windows[0].wire, "203007030500-203007030600");
});

test("danger and military areas use a broken active border regardless of RA category", () => {
  assert.equal(categoryLinePattern("RA1", "D193 PEARCE"), "Broken");
  assert.equal(categoryLinePattern("RA3", "D193 PEARCE"), "Broken");
  assert.equal(categoryLinePattern("RA1", "M CTA AMX"), "Broken");
  assert.equal(categoryLinePattern("RA3", "M CTA AMX"), "Broken");
  assert.equal(categoryLinePattern("RA2", "R166A"), "Dotted");
});
