// Pure, dependency-free logic shared by the dashboard UI. No DOM access here on purpose:
// this file is imported both by app.js (browser) and by tests/ui/logic.test.mjs (plain
// `node --test`, no npm packages) so the fiddly bits -- ref resolution, unit mapping,
// threshold level calc, keyboard reorder index math, name sanitizing -- have a test that
// runs without a browser.

/** Units per Sensor Type (docs/SPEC.md step5 feature 2). */
const UNIT_BY_SENSOR_TYPE = {
  Temperature: '°C',
  Power: 'W',
  Clock: 'MHz',
  Load: '%',
  Level: '%',
  Control: '%',
  Fan: 'RPM',
  Voltage: 'V',
  Data: 'GB',
  SmallData: 'MB',
  Throughput: 'MB/s',
  Factor: '',
};

export function unitForSensorType(type, temperatureUnit = 'C') {
  if (type === 'Temperature') {
    return temperatureUnit === 'F' ? '°F' : '°C';
  }
  return UNIT_BY_SENSOR_TYPE[type] ?? '';
}

/** Converts a Celsius value for presentation only. Deltas/rates deliberately omit +32. */
export function convertTemperature(value, temperatureUnit, delta = false) {
  if (value == null || !Number.isFinite(value) || temperatureUnit !== 'F') {
    return value;
  }

  const converted = delta ? value * 9 / 5 : value * 9 / 5 + 32;
  // Stabilize binary floating-point noise at the presentation boundary. This is still much
  // more precise than the one-decimal UI while making values deterministic for callers.
  return Number(converted.toFixed(10));
}

/** Presentation value/unit for one sensor. Internal snapshot values always remain Celsius. */
export function displaySensorValue(value, sensorType, temperatureUnit = 'C') {
  const temperature = sensorType === 'Temperature';
  return {
    value: temperature ? convertTemperature(value, temperatureUnit) : value,
    unit: unitForSensorType(sensorType, temperatureUnit),
    decimals: temperature || ['Load', 'Level', 'Control', 'Power', 'Voltage'].includes(sensorType) ? 1 : 0,
  };
}

/** Browser-locale proposal used only while the persisted setting is unset. */
export function temperatureUnitForLanguage(language) {
  try {
    const region = new Intl.Locale(language).region;
    return ['US', 'LR', 'MM'].includes(region) ? 'F' : 'C';
  } catch {
    return 'C';
  }
}

/** Hardware-kind picker chips (docs/SPEC.md step5 feature 5). */
const HARDWARE_KIND_BY_TYPE = {
  Cpu: 'CPU',
  GpuNvidia: 'GPU',
  GpuAmd: 'GPU',
  GpuIntel: 'GPU',
  Motherboard: 'Motherboard',
  SuperIO: 'Motherboard',
  EmbeddedController: 'Motherboard',
  Memory: 'Memory',
  Storage: 'Storage',
  Battery: 'Battery',
};

export function hardwareKindFromType(type) {
  return HARDWARE_KIND_BY_TYPE[type] ?? 'Other';
}

/** Sensor-type picker chips (docs/SPEC.md step5 feature 5). */
const SENSOR_TYPE_CATEGORY = {
  Temperature: 'Temperature',
  Power: 'Power',
  Clock: 'Clock',
  Load: 'Load',
  Level: 'Load',
  Control: 'Load',
  Fan: 'Fan',
  Voltage: 'Voltage',
};

export function sensorTypeCategory(type) {
  return SENSOR_TYPE_CATEGORY[type] ?? 'Other';
}

/**
 * ok/watch/critical for a value against a resolved threshold ({watch, critical}), or null
 * when there's no reading or no threshold for this sensor (most non-temperature sensors
 * never get one -- see ThresholdResolver.cs).
 */
export function thresholdLevel(value, threshold) {
  if (value == null || !Number.isFinite(value) || !threshold) {
    return null;
  }

  if (value >= threshold.critical) {
    return 'critical';
  }

  if (value >= threshold.watch) {
    return 'watch';
  }

  return 'ok';
}

/**
 * Resolves one widget ref ({role?, id?, hw?}) against the current /api/meta payload
 * ({ sensors: [{id,...}], roles: [{sensorId, role, ...}] }) to the sensor ids it currently
 * covers (docs/SPEC.md "Widget references (r3)"):
 *  - {role} alone expands to every sensor with that role, in identifier order (multi-instance
 *    roles -- e.g. per-core clocks -- become one tile per sensor).
 *  - {id, hw, role?} resolves the exact id first if that sensor exists on this PC, else falls
 *    back to the single first role match, else resolves to nothing (the caller counts it in
 *    the "N widgets unavailable on this PC" notice).
 * Always returns an array (possibly empty), never throws on a malformed/partial meta payload.
 */
export function resolveWidgetRef(ref, meta) {
  if (!ref) {
    return [];
  }

  const sensors = Array.isArray(meta?.sensors) ? meta.sensors : [];
  const roles = Array.isArray(meta?.roles) ? meta.roles : [];
  const hardware = Array.isArray(meta?.hardware) ? meta.hardware : [];

  if (ref.id) {
    const exact = sensors.find((sensor) => sensor.id === ref.id);
    const exactHardware = exact?.hardwareId
      ? hardware.find((item) => item.id === exact.hardwareId)
      : null;
    const hardwareMatches = !ref.hw || !exact?.hardwareId ||
      ref.hw === exact.hardwareId || ref.hw === exactHardware?.id || ref.hw === exactHardware?.name;
    if (exact && hardwareMatches) {
      return [exact.id];
    }
  }

  if (ref.role) {
    const matches = roles.filter((role) => role.role === ref.role).map((role) => role.sensorId);
    const unique = Array.from(new Set(matches));
    unique.sort();

    // An {id, hw, role} ref names one specific widget: only the first role match stands in
    // for it. A bare {role} ref expands to every match (one tile per sensor).
    return ref.id ? unique.slice(0, 1) : unique;
  }

  return [];
}

/** Case-insensitive match against every searchable field of a sensor-picker row (the raw
 * name/hardware name as well as the resolved title/subtitle, so a search still finds a
 * sensor by either its plain-English label or its underlying hardware/sensor name). */
export function matchesSensorSearch(query, entry) {
  const q = (query ?? '').trim().toLowerCase();
  if (!q) {
    return true;
  }

  const haystacks = [
    entry?.name,
    entry?.hardwareName,
    entry?.role,
    entry?.id,
    entry?.title,
    entry?.subtitle,
  ].filter((value) => value != null && value !== '');
  return haystacks.some((value) => String(value).toLowerCase().includes(q));
}

/**
 * Strips C0 control characters (and DEL) for display. Real hardware data has shown up with
 * embedded CR/NUL/etc bytes in sensor and hardware names (docs/SPEC.md); the raw id is always
 * kept as the lookup key, only the human-facing label goes through this.
 */
export function sanitizeDisplayText(value) {
  if (value == null) {
    return '';
  }

  let out = '';
  for (const ch of String(value)) {
    const code = ch.codePointAt(0) ?? 0;
    if (code >= 0x20 && code !== 0x7f) {
      out += ch;
    }
  }

  return out;
}

export function formatValue(value, unit, decimals = 0) {
  if (value == null || !Number.isFinite(value)) {
    return '–'; // en dash: "no reading", never a bare 0.
  }

  const text = value.toFixed(decimals);
  return unit ? `${text} ${unit}` : text;
}

/** Pure array move used by both pointer-drag drop and keyboard reorder. */
export function moveIndex(list, from, to) {
  const arr = list.slice();
  if (
    from < 0 ||
    from >= arr.length ||
    to < 0 ||
    to >= arr.length ||
    from === to ||
    !Number.isInteger(from) ||
    !Number.isInteger(to)
  ) {
    return arr;
  }

  const [item] = arr.splice(from, 1);
  arr.splice(to, 0, item);
  return arr;
}

/**
 * Arrow-key reorder target index math for a widget on a CSS grid with `columnCount` tracks.
 * Returns null when the key isn't a reorder key or the move would go out of bounds (edit mode
 * keyboard reorder, docs/SPEC.md step5 feature 4).
 */
export function computeKeyboardMoveTarget(currentIndex, key, columnCount, itemCount) {
  if (itemCount <= 0 || !Number.isInteger(currentIndex)) {
    return null;
  }

  const cols = Math.max(1, Math.trunc(columnCount) || 1);
  let target;
  switch (key) {
    case 'ArrowLeft':
      target = currentIndex - 1;
      break;
    case 'ArrowRight':
      target = currentIndex + 1;
      break;
    case 'ArrowUp':
      target = currentIndex - cols;
      break;
    case 'ArrowDown':
      target = currentIndex + cols;
      break;
    default:
      return null;
  }

  if (target < 0 || target >= itemCount || target === currentIndex) {
    return null;
  }

  return target;
}

/**
 * The notes widget's freshness label (docs/SPEC.md Component 7 / step5 feature 2): hidden
 * entirely under 30 minutes old, otherwise "from <source>, N min ago" with source shown only
 * when present.
 */
export function ageMinutesLabel(ageMinutes, source) {
  if (!(ageMinutes > 30)) {
    return { dimmed: false, text: '' };
  }

  const minutes = Math.max(0, Math.round(ageMinutes));
  const text = source ? `from ${source}, ${minutes} min ago` : `${minutes} min ago`;
  return { dimmed: true, text };
}

/**
 * Which quick-filter-bar group (docs/SPEC.md step5 feature 6: CPU/GPU/Board/Memory/Storage/
 * Fans/Analysis) a role belongs to, by its dotted prefix. Returns null for a role with no
 * group (e.g. battery.*) or no role at all -- callers treat null as "always visible,
 * unaffected by quick filters" rather than guessing.
 */
export function groupForRole(role) {
  if (!role) {
    return null;
  }

  if (role.startsWith('cpu.')) return 'CPU';
  if (role.startsWith('gpu.') || role.startsWith('igpu.')) return 'GPU';
  if (role.startsWith('board.')) return 'Board';
  if (role.startsWith('dimm.') || role.startsWith('ram.') || role.startsWith('pagefile.')) return 'Memory';
  if (role.startsWith('storage.')) return 'Storage';
  if (role.startsWith('fan.')) return 'Fans';
  return null;
}

/** True when every sample in a fan/sensor's visible history is exactly 0 (docs/SPEC.md step5
 * feature 3: "Fans/sensors that have read 0 for the whole visible history are dimmed"). An
 * empty or all-null history is not "connected" evidence either way, so this returns false. */
export function isLikelyDisconnected(history) {
  if (!Array.isArray(history) || history.length === 0) {
    return false;
  }

  return history.every((value) => value === 0 || value === 0.0);
}

/**
 * A chart series is "not connected" when every sample in the visible window is 0 or absent
 * (docs/SPEC.md step5-polish "Charts": fans/pumps that never spin in the window shouldn't
 * clutter a chart with a flat zero line). Unlike {@link isLikelyDisconnected} (tile dimming,
 * which deliberately treats an all-null history as inconclusive), a chart has to decide
 * in/out right now, so null and 0 are both "nothing to show" here. An empty window has
 * nothing to judge yet, so it's treated as connected (never hidden by default).
 */
export function isChartSeriesConnected(data) {
  if (!Array.isArray(data) || data.length === 0) {
    return true;
  }

  return !data.every((value) => value == null || value === 0);
}

const CHART_HEADING_BY_UNIT = {
  '°C': 'Temperatures (°C)',
  '°F': 'Temperatures (°F)',
  W: 'Power (W)',
  '%': 'Load (%)',
  RPM: 'Fans (RPM)',
};

/**
 * The chart title (docs/SPEC.md step5-polish "Charts"): when every series shares one of the
 * four common units, a fixed heading by content ("Temperatures (°C)", "Power (W)",
 * "Load (%)", "Fans (RPM)") replaces the old unit-agnostic "Last 5 min (C)". A mixed-unit or
 * uncommon-unit chart instead names its first series plus a count of the rest:
 * "<first series title> +N (unit)". `note` is the small "last 5 min" caption shown under the
 * heading either way. `series` is an array of `{ title, unit }` (already-resolved sensor
 * labels), in on-chart order.
 */
export function describeChart(series) {
  const list = Array.isArray(series) ? series : [];
  if (list.length === 0) {
    return { heading: 'No data yet', note: '' };
  }

  const firstUnit = list[0].unit ?? '';
  const sameUnit = list.every((s) => (s.unit ?? '') === firstUnit);
  if (sameUnit && CHART_HEADING_BY_UNIT[firstUnit]) {
    return { heading: CHART_HEADING_BY_UNIT[firstUnit], note: 'last 5 min' };
  }

  const extra = list.length - 1;
  const unitSuffix = firstUnit ? ` (${firstUnit})` : '';
  const heading = extra > 0 ? `${list[0].title} +${extra}${unitSuffix}` : `${list[0].title}${unitSuffix}`;
  return { heading, note: 'last 5 min' };
}

/**
 * Text for one trend line, e.g. "rising 2.0 °C/min, ~6 min to limit".
 * - The rate carries its unit per minute ("°C/min", "W/min", ...).
 * - The time-to-limit is shown only when the reading is already within 10 degrees of its
 *   watch level. Projecting a noisy 3-minute slope from a cool idle reading ("CPU at 64 °C,
 *   ~6 min to limit") is alarming noise, not information.
 */
export function formatTrendText(trend, unit, currentValue, threshold) {
  const direction = trend.slopePerMin > 0 ? 'rising' : 'falling';
  const rateUnit = unit ? ` ${unit}/min` : '/min';
  let text = `${direction} ${Math.abs(trend.slopePerMin).toFixed(1)}${rateUnit}`;
  const nearLimitDelta = unit === '°F' ? 18 : 10;
  const nearLimit =
    typeof currentValue === 'number' &&
    threshold != null &&
    typeof threshold.watch === 'number' &&
    currentValue >= threshold.watch - nearLimitDelta;
  if (trend.etaMinutes != null && trend.slopePerMin > 0 && nearLimit) {
    text += `, ~${Math.max(1, Math.round(trend.etaMinutes))} min to limit`;
  }
  return text;
}

/** Stable ascending sort by remaining headroom to the critical threshold. */
export function sortByHeadroom(items, valueFor = (item) => item.value, criticalFor = (item) => item.critical) {
  return items
    .map((item, index) => ({ item, index }))
    .sort((left, right) => {
      const leftValue = valueFor(left.item);
      const rightValue = valueFor(right.item);
      const leftCritical = criticalFor(left.item);
      const rightCritical = criticalFor(right.item);
      const leftHeadroom = Number.isFinite(leftValue) && Number.isFinite(leftCritical)
        ? leftCritical - leftValue
        : Number.POSITIVE_INFINITY;
      const rightHeadroom = Number.isFinite(rightValue) && Number.isFinite(rightCritical)
        ? rightCritical - rightValue
        : Number.POSITIVE_INFINITY;
      return leftHeadroom - rightHeadroom || left.index - right.index;
    })
    .map(({ item }) => item);
}

function concernMatchesFocus(concern, focus) {
  const role = concern?.role ?? '';
  if (focus === 'cpu') {
    return role.startsWith('cpu.') || role === 'fan.cpu' || role === 'fan.cpu.opt' || role === 'fan.pump';
  }
  if (focus === 'gpu') {
    return role.startsWith('gpu.');
  }
  if (focus === 'gaming') {
    return role.startsWith('fps.') || role.startsWith('frametime.') || role.startsWith('cpu.') ||
      role.startsWith('gpu.') || role.startsWith('ram.');
  }
  return false;
}

/** Focused domain first; off-domain critical concerns next; all remaining concerns follow. */
export function orderConcernsForFocus(concerns, focus) {
  const list = Array.isArray(concerns) ? concerns : [];
  if (!focus) {
    return list.slice();
  }

  const focused = [];
  const critical = [];
  const remaining = [];
  for (const concern of list) {
    if (concernMatchesFocus(concern, focus)) focused.push(concern);
    else if (concern?.level === 'critical') critical.push(concern);
    else remaining.push(concern);
  }
  return [...focused, ...critical, ...remaining];
}

export function forkPresetName(name) {
  const suffix = ' (custom)';
  const base = String(name ?? '').slice(0, 100 - suffix.length).trimEnd() || 'Preset';
  return `${base}${suffix}`;
}
