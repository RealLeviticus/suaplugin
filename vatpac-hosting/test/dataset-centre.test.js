import assert from "node:assert/strict";
import test from "node:test";
import { parseDatasetAreas } from "../../cloudflare-automation/src/index.js";

test("derives an airspace solar reference point from dataset coordinates", () => {
  const xml = `<RestrictedAreas><Areas><RestrictedArea Name="R999 TEST" Type="Restricted" AltitudeFloor="0" AltitudeCeiling="100" LinePattern="Solid"><Area>-330000.000+1500000.000/-340000.000+1520000.000/-330000.000+1520000.000</Area></RestrictedArea></Areas></RestrictedAreas>`;
  const areas = parseDatasetAreas(xml);
  assert.equal(areas.length, 1);
  assert.equal(areas[0].latitude, -33.5);
  assert.equal(areas[0].longitude, 151);
});
