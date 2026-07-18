const radians = (degrees) => degrees * Math.PI / 180;
const degrees = (value) => value * 180 / Math.PI;

function dayOfYear(date) {
  return Math.floor((Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()) - Date.UTC(date.getUTCFullYear(), 0, 0)) / 86400000);
}

// NOAA fractional-year equations using the conventional 90.833 degree
// sunrise/sunset zenith for refraction and the solar disc radius.
export function solarEventsUtc(date, latitude, longitude) {
  if (!Number.isFinite(latitude) || !Number.isFinite(longitude) || Math.abs(latitude) > 90 || Math.abs(longitude) > 180) return null;
  const gamma = 2 * Math.PI / 365 * (dayOfYear(date) - 1);
  const equationMinutes = 229.18 * (0.000075 + 0.001868 * Math.cos(gamma) - 0.032077 * Math.sin(gamma)
    - 0.014615 * Math.cos(2 * gamma) - 0.040849 * Math.sin(2 * gamma));
  const declination = 0.006918 - 0.399912 * Math.cos(gamma) + 0.070257 * Math.sin(gamma)
    - 0.006758 * Math.cos(2 * gamma) + 0.000907 * Math.sin(2 * gamma)
    - 0.002697 * Math.cos(3 * gamma) + 0.00148 * Math.sin(3 * gamma);
  const cosineHourAngle = Math.cos(radians(90.833)) / (Math.cos(radians(latitude)) * Math.cos(declination))
    - Math.tan(radians(latitude)) * Math.tan(declination);
  if (cosineHourAngle < -1 || cosineHourAngle > 1) return null;
  const hourAngle = degrees(Math.acos(cosineHourAngle));
  const solarNoon = 720 - 4 * longitude - equationMinutes;
  const midnight = Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate());
  return { sunrise: new Date(midnight + (solarNoon - 4 * hourAngle) * 60000), sunset: new Date(midnight + (solarNoon + 4 * hourAngle) * 60000) };
}

function wireDate(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return date.getUTCFullYear() + pad(date.getUTCMonth() + 1) + pad(date.getUTCDate()) + pad(date.getUTCHours()) + pad(date.getUTCMinutes());
}

export function solarWindows(mode, latitude, longitude, now = new Date(), daysAhead = 3) {
  const normalized = String(mode || "").toUpperCase();
  if (normalized !== "HJ" && normalized !== "HN") return [];
  const base = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  const events = new Map();
  for (let offset = -1; offset <= daysAhead + 1; offset++) {
    events.set(offset, solarEventsUtc(new Date(base.getTime() + offset * 86400000), Number(latitude), Number(longitude)));
  }
  const windows = [];
  for (let offset = -1; offset <= daysAhead; offset++) {
    const today = events.get(offset), tomorrow = events.get(offset + 1);
    if (!today) continue;
    const from = normalized === "HJ" ? today.sunrise : today.sunset;
    const to = normalized === "HJ" ? today.sunset : tomorrow?.sunrise;
    if (to && to > now) windows.push(`${wireDate(from)}-${wireDate(to)}`);
  }
  return windows;
}
