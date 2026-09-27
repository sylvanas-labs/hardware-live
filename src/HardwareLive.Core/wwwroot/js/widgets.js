// Renders the five widget kinds (docs/SPEC.md step5 feature 2). Every hardware/sensor name
// reaches the DOM through setSanitizedText (textContent under the hood) -- never innerHTML.
// Canvas drawing reads colors via CSS custom properties so charts/sparklines follow the
// active theme (dark default, light via prefers-color-scheme) automatically.

import { el, setSanitizedText, setVar, clear } from './dom.js';
import {
  formatValue,
  thresholdLevel,
  unitForSensorType,
  isLikelyDisconnected,
  ageMinutesLabel,
} from './logic.js';

const SPARK_SAMPLES = 60;
const CHART_SAMPLES = 300;
const SERIES_COLORS = ['--accent', '--critical', '--ok', '--accent2', '--watch'];

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function sensorInfo(sensorId, ctx) {
  const sensor = ctx.meta.sensors.find((s) => s.id === sensorId);
  const role = ctx.meta.roles.find((r) => r.sensorId === sensorId);
  const unit = unitForSensorType(sensor?.type);
  const decimals = unit === 'C' || unit === '%' || unit === 'W' || unit === 'V' ? 1 : 0;
  return {
    name: sensor?.name ?? sensorId,
    hardwareId: sensor?.hardwareId,
    unit,
    decimals,
    role: role?.role ?? null,
    threshold: ctx.thresholds?.[sensorId] ?? null,
  };
}

function latestValue(ctx, sensorId) {
  const value = ctx.snapshot?.values?.[sensorId];
  return typeof value === 'number' ? value : null;
}

function history(ctx, sensorId) {
  return ctx.snapshot?.history?.[sensorId] ?? [];
}

// ---- Tile -----------------------------------------------------------------------------

function buildSingleTile(sensorId, ctx) {
  const info = sensorInfo(sensorId, ctx);
  const tile = el('div', { className: 'tile', attrs: { 'data-sensor-id': sensorId } });
  const label = el('div', { className: 'tile-label' });
  setSanitizedText(label, info.name);
  const value = el('div', { className: 'tile-value' });
  const peak = el('div', { className: 'tile-peak' });
  const status = el('div', { className: 'tile-status' });
  const spark = el('canvas', { className: 'tile-spark', attrs: { width: 160, height: 26, 'aria-hidden': 'true' } });
  const bar = el('div', { className: 'tile-bar' });

  tile.append(label, value, peak, status, spark, bar);
  updateSingleTile(tile, sensorId, ctx);
  return tile;
}

function updateSingleTile(tile, sensorId, ctx) {
  const info = sensorInfo(sensorId, ctx);
  const value = latestValue(ctx, sensorId);
  const level = thresholdLevel(value, info.threshold);
  const hist = history(ctx, sensorId);
  const disconnected = isLikelyDisconnected(hist);

  tile.classList.toggle('level-watch', level === 'watch');
  tile.classList.toggle('level-critical', level === 'critical');
  tile.classList.toggle('disconnected', disconnected);

  const valueNode = tile.querySelector('.tile-value');
  clear(valueNode);
  valueNode.append(document.createTextNode(formatValue(value, null, info.decimals)));
  if (info.unit) {
    valueNode.append(el('span', { className: 'unit', text: info.unit }));
  }

  const statusNode = tile.querySelector('.tile-status');
  statusNode.className = `tile-status${level ? ` level-${level}` : ''}`;
  statusNode.textContent = disconnected ? 'not connected?' : level ? level.toUpperCase() : '';

  const peakNode = tile.querySelector('.tile-peak');
  peakNode.textContent = '';

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
}

function buildTileBody(body, entry, ctx) {
  const ids = entry.ids ?? [];
  const wrap = el('div', { className: ids.length > 1 ? 'tile-multi' : '' });
  for (const id of ids) {
    wrap.append(buildSingleTile(id, ctx));
  }
  body.append(wrap);
}

function updateTileBody(body, entry, ctx) {
  for (const tileNode of body.querySelectorAll('.tile[data-sensor-id]')) {
    updateSingleTile(tileNode, tileNode.getAttribute('data-sensor-id'), ctx);
  }
}

// ---- Chart ------------------------------------------------------------------------------

function buildChartBody(body, entry, ctx) {
  const title = el('h3', { className: 'chart-title' });
  const legend = el('span');
  title.append(document.createTextNode(''), legend);
  const canvas = el('canvas', { className: 'chart-canvas' });
  body.append(title, canvas);
  updateChartBody(body, entry, ctx);
}

function updateChartBody(body, entry, ctx) {
  const series = (entry.series ?? []).map((sensorId, index) => ({
    sensorId,
    info: sensorInfo(sensorId, ctx),
    color: SERIES_COLORS[index % SERIES_COLORS.length],
    data: history(ctx, sensorId).slice(-CHART_SAMPLES),
  }));

  const title = body.querySelector('.chart-title');
  clear(title);
  const label = el('span');
  const unit = series[0]?.info.unit;
  setSanitizedText(label, unit ? `Last 5 min (${unit})` : 'Last 5 min');
  title.append(label);
  for (const s of series) {
    const swatch = el('span', { className: 'legend-swatch' });
    setVar(swatch, 'background', cssVar(s.color));
    const item = el('span');
    const nameNode = document.createElement('span');
    setSanitizedText(nameNode, s.info.name);
    item.append(swatch, nameNode);
    title.append(item);
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

  let min = Math.min(...allValues);
  let max = fixedMax ?? Math.max(...allValues);
  min = Math.floor(Math.min(min, max) * 0.9);
  max = Math.ceil(max * 1.05) || 1;

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
    ctx2d.fillText(Math.round(v).toString(), 2, y + 4);
  }

  for (const s of series) {
    ctx2d.strokeStyle = cssVar(s.color);
    ctx2d.lineWidth = 1.8;
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
}

// ---- Gauge ------------------------------------------------------------------------------

function buildGaugeBody(body, entry, ctx) {
  const wrap = el('div', { className: 'gauge-wrap' });
  const label = el('div', { className: 'tile-label' });
  const canvas = el('canvas', { attrs: { width: 120, height: 70, 'aria-hidden': 'true' } });
  const value = el('div', { className: 'gauge-value' });
  wrap.append(label, canvas, value);
  body.append(wrap);
  updateGaugeBody(body, entry, ctx);
}

function updateGaugeBody(body, entry, ctx) {
  const sensorId = entry.ids?.[0];
  const info = sensorId ? sensorInfo(sensorId, ctx) : null;
  const value = sensorId ? latestValue(ctx, sensorId) : null;
  const label = body.querySelector('.tile-label');
  setSanitizedText(label, info?.name ?? 'Unavailable');

  const valueNode = body.querySelector('.gauge-value');
  clear(valueNode);
  valueNode.append(document.createTextNode(formatValue(value, info?.unit, info?.decimals ?? 0)));

  const max = info?.threshold?.critical ?? (info?.unit === '%' ? 100 : null);
  const ratio = max ? Math.min(1, Math.max(0, (value ?? 0) / max)) : 0;
  const level = thresholdLevel(value, info?.threshold);
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
  const concerns = health?.concerns ?? [];
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
      const direction = trend.slopePerMin > 0 ? 'rising' : 'falling';
      const eta = trend.etaMinutes != null ? `, ~${Math.round(trend.etaMinutes)} min to limit` : '';
      const item = el('li');
      const nameNode = document.createElement('span');
      setSanitizedText(nameNode, `${info.name}: ${direction} ${Math.abs(trend.slopePerMin).toFixed(1)}/min${eta}`);
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
