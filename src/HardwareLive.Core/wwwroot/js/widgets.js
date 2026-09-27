// Renders the five widget kinds (docs/SPEC.md step5 feature 2). Every hardware/sensor name
// reaches the DOM through setSanitizedText (textContent under the hood) -- never innerHTML.
// Canvas drawing reads colors via CSS custom properties so charts/sparklines follow the
// active theme (dark default, light via prefers-color-scheme) automatically.

import { el, setSanitizedText, setVar, clear } from './dom.js';
import {
  formatValue,
  thresholdLevel,
  resolveSensorLevel,
  VALUE_UNIT_SEPARATOR,
  isLikelyDisconnected,
  isChartSeriesConnected,
  describeChart,
  ageMinutesLabel,
  formatTrendText,
  displaySensorValue,
  convertTemperature,
  orderConcernsForFocus,
  describeFpsStatus,
} from './logic.js';

/** docs/SPEC.md step7-fps item 5: fps.app is a string (the tracked process name), not a
 * sensor -- this pseudo sensor id is resolved specially in app.js's render plan and rendered
 * specially here instead of going through the normal sensor-value tile path. */
const FPS_APP_PSEUDO_ID = 'fps.app';

const SPARK_SAMPLES = 60;
const CHART_SAMPLES = 300;
// >= 8 distinct, color-blind-friendlier colors (docs/SPEC.md step5-polish "Charts"); a chart
// never reuses one of these within itself, and only adds a dash pattern past 8 series.
const SERIES_COLORS = [
  '--series-1', '--series-2', '--series-3', '--series-4',
  '--series-5', '--series-6', '--series-7', '--series-8',
];
const SERIES_DASH_PATTERNS = [[], [6, 3], [2, 2], [8, 3, 2, 3]];

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function sensorInfo(sensorId, ctx) {
  const sensor = ctx.meta.sensors.find((s) => s.id === sensorId);
  const role = ctx.meta.roles.find((r) => r.sensorId === sensorId);
  const label = ctx.meta.labels?.[sensorId];
  const display = displaySensorValue(null, sensor?.type, ctx.temperatureUnit);
  const temperature = sensor?.type === 'Temperature';
  const rawThreshold = ctx.thresholds?.[sensorId] ?? null;
  const threshold = rawThreshold && temperature
    ? {
        ...rawThreshold,
        watch: convertTemperature(rawThreshold.watch, ctx.temperatureUnit),
        critical: convertTemperature(rawThreshold.critical, ctx.temperatureUnit),
      }
    : rawThreshold;
  return {
    title: label?.title ?? sensor?.name ?? sensorId,
    subtitle: label?.subtitle ?? null,
    name: sensor?.name ?? sensorId,
    hardwareId: sensor?.hardwareId,
    unit: display.unit,
    decimals: display.decimals,
    sensorType: sensor?.type,
    role: role?.role ?? null,
    threshold,
  };
}

function latestValue(ctx, sensorId, info = sensorInfo(sensorId, ctx)) {
  const value = ctx.snapshot?.values?.[sensorId];
  if (typeof value !== 'number') return null;
  return info.sensorType === 'Temperature' ? convertTemperature(value, ctx.temperatureUnit) : value;
}

function history(ctx, sensorId, info = sensorInfo(sensorId, ctx)) {
  const values = ctx.snapshot?.history?.[sensorId] ?? [];
  return info.sensorType === 'Temperature'
    ? values.map((value) => convertTemperature(value, ctx.temperatureUnit))
    : values;
}

// ---- Tile -----------------------------------------------------------------------------

function buildFpsAppTile(ctx) {
  const tile = el('div', { className: 'tile fps-app-tile', attrs: { 'data-sensor-id': FPS_APP_PSEUDO_ID } });
  const label = el('div', { className: 'tile-label', text: 'Tracked app' });
  const value = el('div', { className: 'tile-value' });
  const status = el('div', { className: 'tile-status' });
  const actions = el('div', { className: 'tile-actions' });
  const notTrackingBtn = el('button', {
    className: 'btn',
    text: 'Not tracking?',
    attrs: { type: 'button' },
  });
  const pinBtn = el('button', { className: 'btn', text: 'Pin this app', attrs: { type: 'button' } });
  actions.append(notTrackingBtn, pinBtn);
  tile.append(label, value, status, actions);
  updateFpsAppTile(tile, ctx);
  return tile;
}

function updateFpsAppTile(tile, ctx) {
  const fps = ctx.fps ?? null;
  const valueNode = tile.querySelector('.tile-value');
  clear(valueNode);
  valueNode.append(document.createTextNode(''));
  setSanitizedText(valueNode, fps?.app || '–');

  const statusNode = tile.querySelector('.tile-status');
  statusNode.textContent = describeFpsStatus(fps?.status);
  statusNode.className = `tile-status${fps?.status && fps.status !== 'tracking' ? ' level-watch' : ''}`;

  const [notTrackingBtn, pinBtn] = tile.querySelectorAll('.tile-actions button');
  const hasApp = Boolean(fps?.app);
  const actionsNode = tile.querySelector('.tile-actions');
  actionsNode.hidden = !hasApp;
  if (notTrackingBtn) {
    notTrackingBtn.onclick = () => ctx.onFpsDenylistAdd?.(fps.app);
  }
  if (pinBtn) {
    pinBtn.onclick = () => ctx.onFpsPin?.(fps.app);
  }
}

function buildSingleTile(sensorId, ctx) {
  if (sensorId === FPS_APP_PSEUDO_ID) {
    return buildFpsAppTile(ctx);
  }

  const info = sensorInfo(sensorId, ctx);
  const tile = el('div', { className: 'tile', attrs: { 'data-sensor-id': sensorId } });
  const label = el('div', { className: 'tile-label' });
  setSanitizedText(label, info.title);
  const subtitle = el('div', { className: 'tile-subtitle' });
  const value = el('div', { className: 'tile-value' });
  const status = el('div', { className: 'tile-status' });
  const spark = el('canvas', { className: 'tile-spark', attrs: { width: 160, height: 26, 'aria-hidden': 'true' } });
  const peak = el('div', { className: 'tile-peak' });
  const bar = el('div', { className: 'tile-bar' });

  tile.append(label, subtitle, value, status, spark, peak, bar);
  updateSingleTile(tile, sensorId, ctx);
  return tile;
}

function updateSingleTile(tile, sensorId, ctx) {
  if (sensorId === FPS_APP_PSEUDO_ID) {
    updateFpsAppTile(tile, ctx);
    return;
  }

  const info = sensorInfo(sensorId, ctx);
  const value = latestValue(ctx, sensorId, info);
  const level = resolveSensorLevel(sensorId, value, info.threshold, ctx.health?.sensorLevels);
  const hist = history(ctx, sensorId, info);
  const disconnected = isLikelyDisconnected(hist);

  tile.classList.toggle('level-watch', level === 'watch');
  tile.classList.toggle('level-critical', level === 'critical');
  tile.classList.toggle('disconnected', disconnected);

  const subtitleNode = tile.querySelector('.tile-subtitle');
  setSanitizedText(subtitleNode, info.subtitle ?? '');
  subtitleNode.hidden = !info.subtitle;

  const valueNode = tile.querySelector('.tile-value');
  clear(valueNode);
  valueNode.append(document.createTextNode(formatValue(value, null, info.decimals)));
  if (info.unit) {
    // Same separator formatValue() puts between a value and its unit (e.g. the peak line
    // below), just split across two nodes so the unit can keep its muted, smaller style
    // (docs/SPEC.md step5-polish "Tiles consistency": value/unit spacing must be consistent).
    valueNode.append(document.createTextNode(VALUE_UNIT_SEPARATOR));
    valueNode.append(el('span', { className: 'unit', text: info.unit }));
  }

  // Tiles without a resolved threshold (most non-temperature sensors) never get a level, so
  // the status line stays empty rather than showing a redundant "OK" -- but the line itself
  // is always present (docs/SPEC.md step5-polish "Tiles consistency": no ragged tile heights).
  const statusNode = tile.querySelector('.tile-status');
  statusNode.className = `tile-status${level ? ` level-${level}` : ''}`;
  statusNode.textContent = disconnected ? 'not connected' : level && level !== 'ok' ? level.toUpperCase() : '';

  const peakNode = tile.querySelector('.tile-peak');
  const finiteHistory = hist.filter((sample) => Number.isFinite(sample));
  const peak = finiteHistory.length ? Math.max(...finiteHistory) : null;
  peakNode.textContent = peak == null ? '' : `peak ${formatValue(peak, info.unit, info.decimals)}`;

  if (info.threshold) {
    tile.setAttribute(
      'title',
      `Watch ${Math.round(info.threshold.watch)} ${info.unit}; critical ${Math.round(info.threshold.critical)} ${info.unit}`,
    );
  } else {
    tile.removeAttribute('title');
  }

  const barNode = tile.querySelector('.tile-bar');
  const barColor = level ? `var(--${level === 'ok' ? 'ok' : level})` : 'var(--ok)';
  setVar(barNode, 'background', barColor);
  const ratio = info.threshold ? Math.min(1, Math.max(0, (value ?? 0) / info.threshold.critical)) : 0;
  setVar(barNode, 'width', `${Math.round(ratio * 100)}%`);

  drawSparkline(tile.querySelector('canvas.tile-spark'), hist, level);
}

function drawSparkline(canvas, hist, level) {
  const ctx2d = canvas.getContext('2d');
  const w = canvas.width;
  const h = canvas.height;
  ctx2d.clearRect(0, 0, w, h);

  const samples = hist.slice(-SPARK_SAMPLES).filter((v) => v != null);
  if (samples.length < 2) {
    return;
  }

  const min = Math.min(...samples);
  const max = Math.max(...samples);
  const range = max - min || 1;
  ctx2d.strokeStyle = cssVar(level ? `--${level}` : '--accent');
  ctx2d.lineWidth = 1.5;
  ctx2d.beginPath();
  samples.forEach((value, index) => {
    const x = (index / (samples.length - 1)) * w;
    const y = h - 2 - ((value - min) / range) * (h - 4);
    index === 0 ? ctx2d.moveTo(x, y) : ctx2d.lineTo(x, y);
  });
  ctx2d.stroke();

  // No overlaid min/max numbers here on purpose: at this size they collide with each other
  // and the line itself, and the tile already shows the current value plus a "peak" line
  // (docs/SPEC.md step5-polish "Tiles consistency").
}

// One tile widget always covers exactly one sensor instance now -- app.js's render plan
// splits a multi-instance ref (e.g. every DIMM's dimm.temp) into one widget-grid entry per
// sensor id (docs/SPEC.md step5-polish "One tile per instance") rather than stacking several
// readings inside a single grid cell, so there is no multi-tile wrapper to build here.
function buildTileBody(body, entry, ctx) {
  const id = entry.ids?.[0];
  if (id) {
    body.append(buildSingleTile(id, ctx));
  }
}

function updateTileBody(body, entry, ctx) {
  const tileNode = body.querySelector('.tile[data-sensor-id]');
  if (tileNode) {
    updateSingleTile(tileNode, tileNode.getAttribute('data-sensor-id'), ctx);
  }
}

// ---- Chart ------------------------------------------------------------------------------

function buildChartBody(body, entry, ctx) {
  const title = el('h3', { className: 'chart-title' });
  title.append(document.createTextNode(''), el('span', { className: 'chart-note' }));
  const legend = el('div', { className: 'chart-legend' });
  const canvas = el('canvas', { className: 'chart-canvas' });
  const hiddenNote = el('p', { className: 'chart-hidden-note', attrs: { hidden: true } });
  body.append(title, legend, canvas, hiddenNote);
  updateChartBody(body, entry, ctx);
}

function updateChartBody(body, entry, ctx) {
  const allSeries = (entry.series ?? []).map((sensorId) => ({
    sensorId,
    info: sensorInfo(sensorId, ctx),
    data: history(ctx, sensorId).slice(-CHART_SAMPLES),
  }));

  // Fans/pumps that read 0 or null for the whole visible window are excluded rather than
  // drawn as a flat, meaningless line (docs/SPEC.md step5-polish "Charts") -- scoped to RPM
  // series specifically, since e.g. a GPU at true idle can legitimately show 0% load for the
  // whole window without being "not connected".
  const connected = allSeries.filter((s) => s.info.unit !== 'RPM' || isChartSeriesConnected(s.data));
  const hiddenCount = allSeries.length - connected.length;
  const series = connected.map((s, index) => ({
    ...s,
    color: SERIES_COLORS[index % SERIES_COLORS.length],
    dash: SERIES_DASH_PATTERNS[Math.floor(index / SERIES_COLORS.length) % SERIES_DASH_PATTERNS.length],
  }));

  // Titled from every configured series, not just the ones currently drawn -- a chart whose
  // fans are all momentarily idle should still read "Fans (RPM)", not "No data yet".
  const { heading, note } = describeChart(allSeries.map((s) => s.info));
  const titleNode = body.querySelector('.chart-title');
  setSanitizedText(titleNode.firstChild, heading);
  const noteNode = titleNode.querySelector('.chart-note');
  setSanitizedText(noteNode, note ? ` · ${note}` : '');

  const legendNode = body.querySelector('.chart-legend');
  clear(legendNode);
  for (const s of series) {
    const swatch = el('span', { className: 'legend-swatch' });
    setVar(swatch, 'background', cssVar(s.color));
    const item = el('span', { className: 'legend-item' });
    const nameNode = document.createElement('span');
    setSanitizedText(nameNode, s.info.title);
    item.append(swatch, nameNode);
    if (s.info.subtitle) {
      const subtitleNode = el('span', { className: 'legend-subtitle' });
      setSanitizedText(subtitleNode, `(${s.info.subtitle})`);
      item.append(subtitleNode);
    }
    legendNode.append(item);
  }

  const hiddenNoteNode = body.querySelector('.chart-hidden-note');
  hiddenNoteNode.hidden = hiddenCount === 0;
  if (hiddenCount > 0) {
    hiddenNoteNode.textContent = `${hiddenCount} not connected (hidden)`;
  }

  drawChart(body.querySelector('canvas.chart-canvas'), series, entry.widget?.max);
}

function drawChart(canvas, series, fixedMax) {
  const dpr = window.devicePixelRatio || 1;
  const cssWidth = canvas.clientWidth || 300;
  const cssHeight = canvas.clientHeight || 180;
  canvas.width = cssWidth * dpr;
  canvas.height = cssHeight * dpr;
  const ctx2d = canvas.getContext('2d');
  ctx2d.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx2d.clearRect(0, 0, cssWidth, cssHeight);

  const allValues = series.flatMap((s) => s.data.filter((v) => v != null));
  if (allValues.length === 0) {
    ctx2d.fillStyle = cssVar('--muted');
    ctx2d.font = '12px system-ui';
    ctx2d.fillText('No data yet', 8, cssHeight / 2);
    return;
  }

  // A fixed-max chart (e.g. load, 0-100%) always spans exactly that range with even ticks --
  // no data-dependent padding, which used to push the axis top past the real max (e.g. 105
  // instead of 100).
  let min;
  let max;
  if (fixedMax != null) {
    min = 0;
    max = fixedMax;
  } else {
    min = Math.floor(Math.min(...allValues) * 0.9);
    max = Math.ceil(Math.max(...allValues) * 1.05) || 1;
  }

  const padLeft = 34;
  const padBottom = 14;
  const w = cssWidth - padLeft - 6;
  const h = cssHeight - padBottom - 6;

  ctx2d.strokeStyle = cssVar('--line');
  ctx2d.fillStyle = cssVar('--muted');
  ctx2d.font = '11px system-ui';
  for (let i = 0; i <= 4; i++) {
    const y = 6 + (h * i) / 4;
    const v = max - ((max - min) * i) / 4;
    ctx2d.beginPath();
    ctx2d.moveTo(padLeft, y);
    ctx2d.lineTo(padLeft + w, y);
    ctx2d.stroke();
    const temperatureAxis = series.length > 0 && series.every((item) => item.info.unit === '°C' || item.info.unit === '°F');
    ctx2d.fillText(temperatureAxis ? v.toFixed(1) : Math.round(v).toString(), 2, y + 4);
  }

  for (const s of series) {
    ctx2d.strokeStyle = cssVar(s.color);
    ctx2d.lineWidth = 1.8;
    ctx2d.setLineDash(s.dash ?? []);
    ctx2d.beginPath();
    let started = false;
    const n = s.data.length;
    s.data.forEach((value, index) => {
      if (value == null) {
        started = false;
        return;
      }

      const x = padLeft + (index / Math.max(1, n - 1)) * w;
      const y = 6 + h - ((value - min) / (max - min || 1)) * h;
      started ? ctx2d.lineTo(x, y) : ctx2d.moveTo(x, y);
      started = true;
    });
    ctx2d.stroke();
  }
  ctx2d.setLineDash([]);
}

// ---- Gauge ------------------------------------------------------------------------------

function buildGaugeBody(body, entry, ctx) {
  const wrap = el('div', { className: 'gauge-wrap' });
  const label = el('div', { className: 'tile-label' });
  const canvas = el('canvas', { attrs: { width: 120, height: 70, 'aria-hidden': 'true' } });
  const value = el('div', { className: 'gauge-value' });
  const scale = el('div', { className: 'gauge-scale' });
  wrap.append(label, canvas, value, scale);
  body.append(wrap);
  updateGaugeBody(body, entry, ctx);
}

function updateGaugeBody(body, entry, ctx) {
  const sensorId = entry.ids?.[0];
  const info = sensorId ? sensorInfo(sensorId, ctx) : null;
  const value = sensorId ? latestValue(ctx, sensorId, info) : null;
  const label = body.querySelector('.tile-label');
  setSanitizedText(label, info?.title ?? 'Unavailable');

  const valueNode = body.querySelector('.gauge-value');
  clear(valueNode);
  valueNode.append(document.createTextNode(formatValue(value, info?.unit, info?.decimals ?? 0)));

  const max = info?.threshold?.critical ?? (info?.unit === '%' ? 100 : null);
  const ratio = max ? Math.min(1, Math.max(0, (value ?? 0) / max)) : 0;
  const level = sensorId
    ? resolveSensorLevel(sensorId, value, info?.threshold, ctx.health?.sensorLevels)
    : thresholdLevel(value, info?.threshold);
  const scaleNode = body.querySelector('.gauge-scale');
  scaleNode.textContent = max == null ? '' : `0 – ${Math.round(max)} ${info?.unit ?? ''}`.trim();
  drawGauge(body.querySelector('canvas'), ratio, level);
}

function drawGauge(canvas, ratio, level) {
  const ctx2d = canvas.getContext('2d');
  const w = canvas.width;
  const h = canvas.height;
  ctx2d.clearRect(0, 0, w, h);
  const cx = w / 2;
  const cy = h - 6;
  const radius = Math.min(w / 2, h) - 8;
  const start = Math.PI;
  const end = Math.PI * 2;

  ctx2d.lineWidth = 8;
  ctx2d.strokeStyle = cssVar('--line');
  ctx2d.beginPath();
  ctx2d.arc(cx, cy, radius, start, end);
  ctx2d.stroke();

  ctx2d.strokeStyle = cssVar(level ? `--${level}` : '--accent');
  ctx2d.beginPath();
  ctx2d.arc(cx, cy, radius, start, start + (end - start) * ratio);
  ctx2d.stroke();
}

// ---- Analysis ---------------------------------------------------------------------------

function buildAnalysisBody(body, entry, ctx) {
  const heading = el('h2', { className: 'analysis-title', text: 'Health analysis' });
  const badge = el('span', { className: 'status-badge UNKNOWN', text: '...' });
  const headerRow = el('div', { className: 'analysis-header-row' });
  headerRow.append(heading, badge);
  const headline = el('p', { className: 'analysis-headline' });
  const spinner = el('span', { className: 'spinner', attrs: { hidden: true, 'aria-hidden': 'true' } });
  headline.append(spinner, document.createTextNode(''));

  const columns = el('div', { className: 'analysis-columns' });
  const concernsCol = el('div');
  concernsCol.append(el('h3', { text: 'Concerns' }), el('ul', { className: 'concern-list' }));
  const trendsCol = el('div');
  trendsCol.append(el('h3', { text: 'Trends' }), el('ul', { className: 'trend-list' }));
  columns.append(concernsCol, trendsCol);

  body.append(headerRow, headline, columns);
  updateAnalysisBody(body, entry, ctx);
}

function updateAnalysisBody(body, entry, ctx) {
  const health = ctx.health;
  const badge = body.querySelector('.status-badge');
  const headline = body.querySelector('.analysis-headline');
  const spinner = headline.querySelector('.spinner');
  const concernList = body.querySelector('.concern-list');
  const trendList = body.querySelector('.trend-list');

  const status = health?.status ?? 'UNKNOWN';
  badge.className = `status-badge ${status}`;
  badge.textContent = status;

  const starting = health?.reason === 'starting';
  spinner.hidden = !starting;

  clear(headline);
  headline.append(spinner);
  const text = starting
    ? 'Starting up – reading your hardware…'
    : health?.headline ?? health?.reason ?? 'Waiting for the sampler…';
  headline.append(document.createTextNode(text));

  clear(concernList);
  const concerns = orderConcernsForFocus(health?.concerns ?? [], ctx.focus);
  if (concerns.length === 0) {
    const none = el('li', { text: starting || !health ? 'Waiting for data.' : 'No concerns right now.' });
    concernList.append(none);
  } else {
    for (const concern of concerns) {
      const item = el('li', { attrs: { 'data-level': concern.level } });
      const msg = document.createElement('span');
      setSanitizedText(msg, concern.message);
      item.append(msg, el('span', { className: 'level-text', text: concern.level }));
      concernList.append(item);
    }
  }

  clear(trendList);
  const trends = health?.trends ?? [];
  if (trends.length === 0) {
    trendList.append(el('li', { text: 'Nothing trending.' }));
  } else {
    for (const trend of trends) {
      const info = sensorInfo(trend.sensorId, ctx);
      // The server's role title (docs/SPEC.md step5-polish "Ambiguous labels": trends must
      // not fall back to a bare raw sensor name like "CPU"); the meta-derived title covers
      // the rare case of an older server without trend.label.
      const title = trend.label || info.title;
      const item = el('li');
      const nameNode = document.createElement('span');
      const suffix = info.subtitle ? ` (${info.subtitle})` : '';
      const trendUnit = typeof trend.unit === 'string' && trend.unit.endsWith('/min')
        ? trend.unit.slice(0, -4)
        : info.unit;
      const text = formatTrendText(trend, trendUnit, latestValue(ctx, trend.sensorId, info), info.threshold);
      setSanitizedText(nameNode, `${title}${suffix}: ${text}`);
      item.append(nameNode);
      trendList.append(item);
    }
  }
}

// ---- Notes ------------------------------------------------------------------------------

function buildNotesBody(body, entry, ctx) {
  const header = el('div', { className: 'notes-header' });
  header.append(el('h3', { className: 'notes-heading', text: 'Notes' }), el('span', { className: 'notes-age' }));
  const lines = el('div', { className: 'notes-lines' });
  body.append(header, lines);
  updateNotesBody(body, entry, ctx);
}

function updateNotesBody(body, entry, ctx) {
  const notes = ctx.notes;
  const widget = body.closest('.widget');
  const age = notes ? ageMinutesLabel(notes.ageMinutes, notes.source) : { dimmed: false, text: '' };
  widget?.classList.toggle('dimmed', age.dimmed);

  const ageNode = body.querySelector('.notes-age');
  ageNode.textContent = age.text ? `· ${age.text}` : notes?.source ? `· from ${notes.source}` : '';

  const lines = body.querySelector('.notes-lines');
  clear(lines);
  for (const line of notes?.lines ?? []) {
    const p = document.createElement('p');
    setSanitizedText(p, line);
    lines.append(p);
  }
}

// ---- Public API ---------------------------------------------------------------------------

/** Returns null for a notes widget with no notes: the caller must render no element at all
 * (docs/SPEC.md Invariant 5 / Component 7), not a placeholder or an empty widget shell. */
export function createWidgetElement(entry, ctx) {
  if (entry.widget.kind === 'notes' && !ctx.notes) {
    return null;
  }

  const node = el('div', {
    className: 'widget',
    attrs: { role: 'listitem', 'data-kind': entry.widget.kind, 'data-size': entry.widget.size, tabindex: '0' },
  });
  const body = el('div', { className: 'widget-body' });
  node.append(body);

  switch (entry.widget.kind) {
    case 'tile':
      buildTileBody(body, entry, ctx);
      break;
    case 'chart':
      buildChartBody(body, entry, ctx);
      break;
    case 'gauge':
      buildGaugeBody(body, entry, ctx);
      break;
    case 'analysis':
      buildAnalysisBody(body, entry, ctx);
      node.classList.add('analysis-widget');
      break;
    case 'notes':
      buildNotesBody(body, entry, ctx);
      node.classList.add('notes-widget');
      break;
    default:
      break;
  }

  return node;
}

export function updateWidgetElement(node, entry, ctx) {
  const body = node.querySelector('.widget-body');
  switch (entry.widget.kind) {
    case 'tile':
      updateTileBody(body, entry, ctx);
      break;
    case 'chart':
      updateChartBody(body, entry, ctx);
      break;
    case 'gauge':
      updateGaugeBody(body, entry, ctx);
      break;
    case 'analysis':
      updateAnalysisBody(body, entry, ctx);
      break;
    case 'notes':
      updateNotesBody(body, entry, ctx);
      break;
    default:
      break;
  }
}
