// Bootstraps the dashboard: polling, layout load/save, the widget grid, edit mode, the quick
// filter bar and the sensor picker. docs/SPEC.md step5 is the spec this file implements.

import { readToken, getJson, writeLayout, Poller } from './api.js';
import { el, clear, setSanitizedText } from './dom.js';
import { createWidgetElement, updateWidgetElement } from './widgets.js';
import { attachReordering } from './dragdrop.js';
import { createSensorPicker } from './picker.js';
import { resolveWidgetRef, groupForRole, moveIndex } from './logic.js';

const TOKEN = readToken();
const QUICK_FILTER_GROUPS = ['CPU', 'GPU', 'Board', 'Memory', 'Storage', 'Fans', 'Analysis'];
const LOCAL_STORAGE_KEY = 'hardwareLive.hiddenGroups.v1';

const DEFAULT_LAYOUT = {
  id: 'current',
  name: 'Overview',
  widgets: [
    { kind: 'analysis', size: 'L', ref: { role: 'analysis.health' } },
    { kind: 'notes', size: 'L', ref: { role: 'notes' } },
    { kind: 'tile', size: 'S', ref: { role: 'cpu.temp.control' } },
    { kind: 'tile', size: 'S', ref: { role: 'cpu.power.package' } },
    { kind: 'tile', size: 'S', ref: { role: 'cpu.load.total' } },
    { kind: 'tile', size: 'S', ref: { role: 'cpu.clock.effective.avg' } },
    { kind: 'tile', size: 'S', ref: { role: 'fan.cpu' } },
    { kind: 'tile', size: 'S', ref: { role: 'gpu.temp.core' } },
    { kind: 'tile', size: 'S', ref: { role: 'gpu.temp.mem' } },
    { kind: 'tile', size: 'S', ref: { role: 'gpu.power' } },
    { kind: 'tile', size: 'S', ref: { role: 'gpu.load.core' } },
    { kind: 'tile', size: 'S', ref: { role: 'gpu.clock.core' } },
    { kind: 'tile', size: 'S', ref: { role: 'dimm.temp' } },
    { kind: 'tile', size: 'S', ref: { role: 'storage.temp' } },
    { kind: 'tile', size: 'S', ref: { role: 'board.temp.vrm' } },
    {
      kind: 'chart',
      size: 'L',
      series: [
        { role: 'cpu.temp.control' },
        { role: 'gpu.temp.core' },
        { role: 'gpu.temp.mem' },
        { role: 'board.temp.vrm' },
      ],
    },
    { kind: 'chart', size: 'L', series: [{ role: 'cpu.power.package' }, { role: 'gpu.power' }] },
    {
      kind: 'chart',
      size: 'L',
      series: [{ role: 'cpu.load.total' }, { role: 'gpu.load.core' }, { role: 'ram.load' }],
      max: 100,
    },
    { kind: 'chart', size: 'L', series: [{ role: 'fan.cpu' }, { role: 'fan.pump' }, { role: 'gpu.fan' }] },
  ],
};

const state = {
  meta: { hardware: [], sensors: [], roles: [], thresholds: {}, profile: {} },
  values: {},
  history: {},
  health: null,
  notes: null,
  layout: cloneLayout(DEFAULT_LAYOUT),
  editMode: false,
  hiddenGroups: loadHiddenGroups(),
};

function cloneLayout(layout) {
  return JSON.parse(JSON.stringify(layout));
}

function loadHiddenGroups() {
  try {
    const raw = window.localStorage.getItem(LOCAL_STORAGE_KEY);
    const parsed = raw ? JSON.parse(raw) : [];
    return new Set(Array.isArray(parsed) ? parsed : []);
  } catch {
    return new Set();
  }
}

function saveHiddenGroups() {
  try {
    window.localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify([...state.hiddenGroups]));
  } catch {
    // Per-viewer convenience only; a private window or blocked storage just means filters
    // reset next visit.
  }
}

// ---- Render plan: resolve layout widgets against the current /api/meta -------------------

function widgetGroup(widget) {
  if (widget.kind === 'analysis') return 'Analysis';
  if (widget.kind === 'notes') return null;
  const role = widget.ref?.role ?? (widget.series?.length === 1 ? widget.series[0].role : null);
  return groupForRole(role);
}

function buildRenderPlan() {
  const plan = [];
  let unavailable = 0;

  for (const widget of state.layout.widgets) {
    if (widget.kind === 'tile' || widget.kind === 'gauge') {
      const ids = resolveWidgetRef(widget.ref, state.meta);
      if (ids.length === 0) {
        unavailable++;
        continue;
      }
      plan.push({ widget, ids, group: widgetGroup(widget) });
    } else if (widget.kind === 'chart') {
      const refs = widget.series?.length ? widget.series : widget.ref ? [widget.ref] : [];
      const series = [];
      for (const ref of refs) {
        for (const id of resolveWidgetRef(ref, state.meta)) {
          series.push(id);
        }
      }
      if (series.length === 0) {
        unavailable++;
        continue;
      }
      plan.push({ widget, series, group: null });
    } else {
      plan.push({ widget, group: widgetGroup(widget) });
    }
  }

  return { plan, unavailable };
}

function visibleSensorIds(plan) {
  const ids = new Set();
  for (const entry of plan) {
    for (const id of entry.ids ?? []) ids.add(id);
    for (const id of entry.series ?? []) ids.add(id);
  }
  return [...ids];
}

// ---- DOM wiring ----------------------------------------------------------------------

const gridEl = document.getElementById('widget-grid');
const emptyStateEl = document.getElementById('empty-state');
const liveRegionEl = document.getElementById('reorder-live');
const editToggleBtn = document.getElementById('edit-toggle');
const addWidgetBtn = document.getElementById('add-widget-btn');
const connectionPillEl = document.getElementById('connection-pill');
const connectionBannerEl = document.getElementById('connection-banner');
const unavailableBannerEl = document.getElementById('unavailable-banner');
const quickFilterBarEl = document.getElementById('quick-filter-bar');

let renderedNodes = []; // parallel to plan: { node, entry }
let lastPlanSignature = '';

function ctx() {
  return {
    meta: state.meta,
    thresholds: state.meta.thresholds ?? {},
    snapshot: { values: state.values, history: state.history },
    health: state.health,
    notes: state.notes,
  };
}

function planSignature(plan, editMode) {
  return JSON.stringify({
    editMode,
    hidden: [...state.hiddenGroups].sort(),
    widgets: plan.map((entry) => ({
      kind: entry.widget.kind,
      size: entry.widget.size,
      ids: entry.ids,
      series: entry.series,
    })),
    notes: state.notes != null,
  });
}

function buildEditControls(index, widget) {
  const bar = el('div', { className: 'widget-edit-controls' });
  const handle = el('button', {
    className: 'btn drag-handle',
    text: '☰ Drag',
    attrs: { type: 'button', 'aria-label': 'Drag to reorder' },
  });
  bar.append(handle);

  for (const size of ['S', 'M', 'L']) {
    const btn = el('button', {
      className: 'btn',
      text: size,
      attrs: { type: 'button', 'aria-pressed': widget.size === size ? 'true' : 'false' },
    });
    btn.addEventListener('click', () => {
      widget.size = size;
      renderGrid();
      persistLayoutSoon();
    });
    bar.append(btn);
  }

  const removeBtn = el('button', { className: 'btn', text: 'Remove', attrs: { type: 'button' } });
  removeBtn.addEventListener('click', () => {
    state.layout.widgets.splice(index, 1);
    renderGrid();
    persistLayoutSoon();
  });
  bar.append(removeBtn);

  return bar;
}

function renderGrid(force = false) {
  const { plan, unavailable } = buildRenderPlan();
  const visiblePlan = plan.filter((entry) => !entry.group || !state.hiddenGroups.has(entry.group));

  unavailableBannerEl.hidden = unavailable === 0;
  if (unavailable > 0) {
    unavailableBannerEl.textContent = `${unavailable} widget${unavailable === 1 ? '' : 's'} unavailable on this PC.`;
  }

  const signature = planSignature(visiblePlan, state.editMode);
  if (!force && signature === lastPlanSignature) {
    updateGridValues();
    return;
  }
  lastPlanSignature = signature;

  clear(gridEl);
  renderedNodes = [];

  visiblePlan.forEach((entry, index) => {
    const node = createWidgetElement(entry, ctx());
    if (!node) {
      return; // Notes widget with nothing to show: no element, no placeholder, no gap.
    }

    if (state.editMode) {
      node.append(buildEditControls(state.layout.widgets.indexOf(entry.widget), entry.widget));
    }

    gridEl.append(node);
    renderedNodes.push({ node, entry });
  });

  emptyStateEl.hidden = renderedNodes.length > 0;
  reordering?.refreshPickedUpMarker();
}

function updateGridValues() {
  const c = ctx();
  for (const { node, entry } of renderedNodes) {
    updateWidgetElement(node, entry, c);
  }
}

// ---- Quick filter bar -----------------------------------------------------------------

function renderQuickFilterBar() {
  clear(quickFilterBarEl);
  for (const group of QUICK_FILTER_GROUPS) {
    const active = !state.hiddenGroups.has(group);
    const chip = el('button', {
      className: 'chip',
      text: group,
      attrs: { type: 'button', 'aria-pressed': active ? 'true' : 'false' },
    });
    chip.addEventListener('click', () => {
      if (state.hiddenGroups.has(group)) {
        state.hiddenGroups.delete(group);
      } else {
        state.hiddenGroups.add(group);
      }
      saveHiddenGroups();
      renderQuickFilterBar();
      renderGrid(true);
    });
    quickFilterBarEl.append(chip);
  }
}

// ---- Edit mode + reordering -------------------------------------------------------------

let persistTimer = null;
function persistLayoutSoon() {
  if (persistTimer) {
    window.clearTimeout(persistTimer);
  }
  persistTimer = window.setTimeout(persistLayout, 400);
}

async function persistLayout() {
  try {
    await writeLayout(TOKEN, 'PUT', '/api/layouts/current', state.layout);
  } catch {
    // Best-effort: the layout also gets a final save on edit-mode exit, and stays correct
    // in memory either way for the rest of this session.
  }
}

const reordering = attachReordering({
  gridEl,
  liveRegionEl,
  isEditMode: () => state.editMode,
  getItemCount: () => renderedNodes.length,
  onReorder(fromIndex, toIndex) {
    const visibleWidgets = renderedNodes.map(({ entry }) => entry.widget);
    const fromWidget = visibleWidgets[fromIndex];
    const toWidget = visibleWidgets[toIndex];
    if (!fromWidget || !toWidget) {
      return;
    }

    const fullFrom = state.layout.widgets.indexOf(fromWidget);
    const fullTo = state.layout.widgets.indexOf(toWidget);
    state.layout.widgets = moveIndex(state.layout.widgets, fullFrom, fullTo);
    renderGrid(true);
    persistLayoutSoon();
  },
});

editToggleBtn.addEventListener('click', () => {
  state.editMode = !state.editMode;
  editToggleBtn.setAttribute('aria-pressed', state.editMode ? 'true' : 'false');
  editToggleBtn.textContent = state.editMode ? 'Done editing' : 'Edit layout';
  addWidgetBtn.hidden = !state.editMode;
  renderGrid(true);

  if (!state.editMode) {
    void persistLayout();
  }
});

// ---- Sensor picker --------------------------------------------------------------------

const picker = createSensorPicker({
  dialogEl: document.getElementById('sensor-picker'),
  getMeta: () => state.meta,
  getLatestValue: (id) => state.values[id],
  onAdd({ kind, ref }) {
    state.layout.widgets.push({ kind, size: kind === 'chart' ? 'M' : 'S', ref });
    renderGrid(true);
    persistLayoutSoon();
  },
});

addWidgetBtn.addEventListener('click', () => picker.open());

// ---- Polling ------------------------------------------------------------------------

function setConnectionState(kind) {
  if (kind === 'lost') {
    connectionPillEl.className = 'pill lost';
    connectionPillEl.textContent = 'connection lost';
    connectionBannerEl.hidden = false;
    connectionBannerEl.textContent = 'Connection lost – retrying…';
  } else {
    connectionPillEl.className = 'pill live';
    connectionPillEl.textContent = 'live';
    connectionBannerEl.hidden = true;
  }
}

const metaPoller = new Poller({
  intervalMs: 30000,
  fetchOne: () => getJson('/api/meta'),
  onData(data) {
    state.meta = data ?? state.meta;
    renderGrid();
  },
});

const notesPoller = new Poller({
  intervalMs: 30000,
  fetchOne: () => getJson('/api/notes'),
  onData(data) {
    state.notes = data;
    renderGrid();
  },
});

const healthPoller = new Poller({
  intervalMs: 2000,
  fetchOne: () => getJson('/api/health'),
  onData(data) {
    state.health = data;
    updateGridValues();
  },
  onStatus: setConnectionState,
});

const snapshotPoller = new Poller({
  intervalMs: 1000,
  async fetchOne() {
    const { plan } = buildRenderPlan();
    const ids = visibleSensorIds(plan);
    const query = ids.length ? `?ids=${ids.map(encodeURIComponent).join(',')}` : '';
    return getJson(`/api/snapshot${query}`);
  },
  onData(data) {
    if (!data) {
      return;
    }

    const values = {};
    for (const sensor of data.sensors ?? []) {
      values[sensor.id] = sensor.value;
    }
    state.values = values;

    for (const [id, series] of Object.entries(data.history ?? {})) {
      state.history[id] = series;
    }

    updateGridValues();
  },
  onStatus: setConnectionState,
});

// ---- Startup ------------------------------------------------------------------------

async function loadInitialLayout() {
  try {
    const layouts = await getJson('/api/layouts');
    const current = Array.isArray(layouts) ? layouts.find((layout) => layout.id === 'current') : null;
    if (current && Array.isArray(current.widgets) && current.widgets.length > 0) {
      state.layout = current;
    }
  } catch {
    // Falls back to the built-in default layout below.
  }
}

async function start() {
  renderQuickFilterBar();
  await loadInitialLayout();
  renderGrid(true);

  metaPoller.start();
  notesPoller.start();
  healthPoller.start();
  snapshotPoller.start();
}

start().catch((error) => {
  // Never fail silently: an unhandled startup error would otherwise leave a blank page.
  console.error('[hardware-live] startup failed', error);
});
