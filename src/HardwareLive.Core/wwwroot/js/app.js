// Bootstraps the dashboard: polling, presets, layout editing, the widget grid, quick filters
// and the sensor picker. All persisted writes are same-origin and token guarded.

import { readToken, getJson, writeJson, Poller } from './api.js';
import { el, clear } from './dom.js';
import { createWidgetElement, updateWidgetElement } from './widgets.js';
import { attachReordering } from './dragdrop.js';
import { createSensorPicker } from './picker.js';
import {
  resolveWidgetRef,
  groupForRole,
  moveIndex,
  sortByHeadroom,
  forkPresetName,
  temperatureUnitForLanguage,
} from './logic.js';

const TOKEN = readToken();
const QUICK_FILTER_GROUPS = ['CPU', 'GPU', 'Board', 'Memory', 'Storage', 'Fans', 'Analysis'];
const LOCAL_STORAGE_KEY = 'hardwareLive.hiddenGroups.v1';
const MAX_IMPORT_BYTES = 256 * 1024;

// The server normally supplies this compiled-in preset. Keeping a local copy makes the page
// useful during a transient /api/layouts failure without creating or overwriting anything.
const FALLBACK_LAYOUT = {
  id: 'builtin-overview',
  name: 'Overview',
  builtin: true,
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
  layouts: [cloneLayout(FALLBACK_LAYOUT)],
  activePresetId: FALLBACK_LAYOUT.id,
  layout: cloneLayout(FALLBACK_LAYOUT),
  temperatureUnit: 'C',
  editMode: false,
  hiddenGroups: loadHiddenGroups(),
};

function cloneLayout(layout) {
  return JSON.parse(JSON.stringify(layout));
}

// `builtin` is response metadata, not part of the strict layout write schema.
function layoutPayload(layout) {
  const payload = {
    id: layout.id,
    name: layout.name,
    widgets: cloneLayout(layout.widgets ?? []),
  };
  if (layout.focus != null) payload.focus = layout.focus;
  if (layout.sort != null) payload.sort = layout.sort;
  return payload;
}

function activeLayoutRecord() {
  return state.layouts.find((layout) => layout.id === state.activePresetId) ?? state.layout;
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
    // Per-window convenience only. Blocked storage simply resets quick filters next visit.
  }
}

// ---- Render plan ---------------------------------------------------------------------

function widgetGroup(widget) {
  if (widget.kind === 'analysis') return 'Analysis';
  if (widget.kind === 'notes') return null;
  const role = widget.ref?.role ?? (widget.series?.length === 1 ? widget.series[0].role : null);
  return groupForRole(role);
}

function sortRenderPlan(plan) {
  if (state.layout.sort !== 'headroom') {
    return plan;
  }

  return sortByHeadroom(
    plan,
    (entry) => state.values[entry.ids?.[0]],
    (entry) => state.meta.thresholds?.[entry.ids?.[0]]?.critical,
  );
}

function buildRenderPlan(layout = state.layout) {
  const plan = [];
  let unavailable = 0;

  for (const widget of layout.widgets ?? []) {
    if (widget.kind === 'tile' || widget.kind === 'gauge') {
      const ids = resolveWidgetRef(widget.ref, state.meta);
      if (ids.length === 0) {
        unavailable++;
        continue;
      }
      const group = widgetGroup(widget);
      for (const id of ids) {
        plan.push({ widget, ids: [id], group });
      }
    } else if (widget.kind === 'chart') {
      const refs = widget.series?.length ? widget.series : widget.ref ? [widget.ref] : [];
      const series = [];
      for (const ref of refs) {
        for (const id of resolveWidgetRef(ref, state.meta)) {
          if (!series.includes(id)) series.push(id);
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

  return { plan: layout === state.layout ? sortRenderPlan(plan) : plan, unavailable };
}

function countUnavailableWidgets(layout) {
  return buildRenderPlan(layout).unavailable;
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
const presetSelectEl = document.getElementById('preset-select');
const presetMenuToggleBtn = document.getElementById('preset-menu-toggle');
const presetMenuEl = document.getElementById('preset-menu');
const presetStatusEl = document.getElementById('preset-status');
const temperatureCBtn = document.getElementById('temperature-c');
const temperatureFBtn = document.getElementById('temperature-f');

let renderedNodes = [];
let lastPlanSignature = '';
let statusTimer = null;

function ctx() {
  return {
    meta: state.meta,
    thresholds: state.meta.thresholds ?? {},
    snapshot: { values: state.values, history: state.history },
    health: state.health,
    notes: state.notes,
    temperatureUnit: state.temperatureUnit,
    focus: state.layout.focus ?? null,
  };
}

function planSignature(plan, editMode) {
  return JSON.stringify({
    activePresetId: state.activePresetId,
    editMode,
    temperatureUnit: state.temperatureUnit,
    focus: state.layout.focus ?? null,
    sort: state.layout.sort ?? null,
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

function showPresetStatus(message) {
  if (statusTimer) window.clearTimeout(statusTimer);
  presetStatusEl.textContent = message;
  presetStatusEl.hidden = !message;
  if (message) {
    statusTimer = window.setTimeout(() => {
      presetStatusEl.hidden = true;
    }, 8000);
  }
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
      syncActiveLayoutRecord();
      renderGrid(true);
      persistLayoutSoon();
    });
    bar.append(btn);
  }

  const removeBtn = el('button', { className: 'btn', text: 'Remove', attrs: { type: 'button' } });
  removeBtn.addEventListener('click', () => {
    state.layout.widgets.splice(index, 1);
    syncActiveLayoutRecord();
    renderGrid(true);
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

  visiblePlan.forEach((entry) => {
    const node = createWidgetElement(entry, ctx());
    if (!node) return;

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
  const context = ctx();
  for (const { node, entry } of renderedNodes) {
    updateWidgetElement(node, entry, context);
  }
}

// ---- Quick filters -------------------------------------------------------------------

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
      if (state.hiddenGroups.has(group)) state.hiddenGroups.delete(group);
      else state.hiddenGroups.add(group);
      saveHiddenGroups();
      renderQuickFilterBar();
      renderGrid(true);
    });
    quickFilterBarEl.append(chip);
  }
}

// ---- Presets -------------------------------------------------------------------------

function uniquePresetId(name) {
  let base = String(name ?? '')
    .normalize('NFKD')
    .replace(/[^A-Za-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 48)
    .toLowerCase();
  if (!base || base === 'import' || base.startsWith('builtin-')) base = 'preset';

  const used = new Set(state.layouts.map((layout) => layout.id.toLowerCase()));
  let candidate = base;
  let suffix = 2;
  while (used.has(candidate.toLowerCase())) {
    const tail = `-${suffix++}`;
    candidate = `${base.slice(0, 64 - tail.length)}${tail}`;
  }
  return candidate;
}

function syncActiveLayoutRecord() {
  const index = state.layouts.findIndex((layout) => layout.id === state.activePresetId);
  if (index >= 0 && !state.layouts[index].builtin) {
    state.layouts[index] = { ...cloneLayout(state.layout), builtin: false };
  }
}

function setEditMode(enabled) {
  state.editMode = enabled;
  editToggleBtn.setAttribute('aria-pressed', enabled ? 'true' : 'false');
  editToggleBtn.textContent = enabled ? 'Done editing' : 'Edit layout';
  addWidgetBtn.hidden = !enabled;
  renderGrid(true);
}

function renderPresetSelect() {
  clear(presetSelectEl);
  const builtIns = document.createElement('optgroup');
  builtIns.label = 'Built-in';
  const custom = document.createElement('optgroup');
  custom.label = 'Custom';

  for (const layout of state.layouts) {
    const option = document.createElement('option');
    option.value = layout.id;
    option.textContent = layout.name;
    (layout.builtin ? builtIns : custom).append(option);
  }
  if (builtIns.children.length) presetSelectEl.append(builtIns);
  if (custom.children.length) presetSelectEl.append(custom);
  presetSelectEl.value = state.activePresetId;
  updatePresetMenuState();
}

function updatePresetMenuState() {
  const builtin = Boolean(activeLayoutRecord()?.builtin);
  document.getElementById('preset-rename').disabled = builtin;
  document.getElementById('preset-delete').disabled = builtin;
}

let settingsQueue = Promise.resolve();
function writeSettings(update) {
  const request = settingsQueue.then(
    () => writeJson(TOKEN, 'PUT', '/api/settings', update),
    () => writeJson(TOKEN, 'PUT', '/api/settings', update),
  );
  settingsQueue = request.catch(() => {});
  return request;
}

async function persistActivePreset() {
  await writeSettings({ activePresetId: state.activePresetId });
}

async function switchPreset(id, persist = true) {
  const selected = state.layouts.find((layout) => layout.id === id);
  if (!selected) return false;

  if (selected.id !== state.activePresetId) {
    await flushPendingLayout();
  }

  state.activePresetId = selected.id;
  state.layout = cloneLayout(selected);
  setEditMode(false);
  renderPresetSelect();
  renderGrid(true);

  if (persist) {
    try {
      await persistActivePreset();
    } catch {
      showPresetStatus('The preset changed for this window, but could not be remembered.');
    }
  }
  return true;
}

async function createCustomPreset(source, name) {
  const payload = layoutPayload(source);
  payload.id = uniquePresetId(name);
  payload.name = name;
  await writeJson(TOKEN, 'POST', '/api/layouts', payload);
  state.layouts.push({ ...cloneLayout(payload), builtin: false });
  await switchPreset(payload.id, true);
  return payload;
}

async function forkBuiltInForEditing() {
  const source = activeLayoutRecord();
  if (!source?.builtin) return true;
  try {
    await createCustomPreset(source, forkPresetName(source.name));
    showPresetStatus(`Created ${state.layout.name}.`);
    return true;
  } catch {
    showPresetStatus('The built-in preset could not be copied for editing.');
    return false;
  }
}

function openNameDialog(title, initialValue) {
  const dialog = document.getElementById('preset-name-dialog');
  const form = dialog.querySelector('form');
  const titleEl = document.getElementById('preset-name-title');
  const input = document.getElementById('preset-name-input');
  const cancel = document.getElementById('preset-name-cancel');
  titleEl.textContent = title;
  input.value = initialValue;

  return new Promise((resolve) => {
    const finish = (value) => {
      form.removeEventListener('submit', submit);
      cancel.removeEventListener('click', cancelDialog);
      dialog.removeEventListener('cancel', cancelDialog);
      if (dialog.open) dialog.close();
      resolve(value);
    };
    const submit = (event) => {
      event.preventDefault();
      const value = input.value.trim();
      if (value) finish(value.slice(0, 100));
    };
    const cancelDialog = (event) => {
      event?.preventDefault();
      finish(null);
    };
    form.addEventListener('submit', submit);
    cancel.addEventListener('click', cancelDialog);
    dialog.addEventListener('cancel', cancelDialog);
    dialog.showModal();
    input.select();
  });
}

function confirmPresetDelete(name) {
  const dialog = document.getElementById('preset-delete-dialog');
  const form = dialog.querySelector('form');
  const message = document.getElementById('preset-delete-message');
  const cancel = document.getElementById('preset-delete-cancel');
  message.textContent = `Delete “${name}”? This cannot be undone.`;

  return new Promise((resolve) => {
    const finish = (value) => {
      form.removeEventListener('submit', submit);
      cancel.removeEventListener('click', cancelDialog);
      dialog.removeEventListener('cancel', cancelDialog);
      if (dialog.open) dialog.close();
      resolve(value);
    };
    const submit = (event) => {
      event.preventDefault();
      finish(true);
    };
    const cancelDialog = (event) => {
      event?.preventDefault();
      finish(false);
    };
    form.addEventListener('submit', submit);
    cancel.addEventListener('click', cancelDialog);
    dialog.addEventListener('cancel', cancelDialog);
    dialog.showModal();
    cancel.focus();
  });
}

async function saveAsNew() {
  const name = await openNameDialog('Save as new preset', `${state.layout.name} copy`);
  if (!name) return;
  try {
    await createCustomPreset(state.layout, name);
    showPresetStatus(`Saved ${name}.`);
  } catch {
    showPresetStatus('The new preset could not be saved.');
  }
}

async function renameActivePreset() {
  const current = activeLayoutRecord();
  if (!current || current.builtin) return;
  const name = await openNameDialog('Rename preset', current.name);
  if (!name || name === current.name) return;

  const payload = layoutPayload({ ...state.layout, name });
  try {
    await writeJson(TOKEN, 'PUT', `/api/layouts/${encodeURIComponent(payload.id)}`, payload);
    state.layout.name = name;
    syncActiveLayoutRecord();
    renderPresetSelect();
    showPresetStatus(`Renamed preset to ${name}.`);
  } catch {
    showPresetStatus('The preset could not be renamed.');
  }
}

async function duplicateActivePreset() {
  const name = await openNameDialog('Duplicate preset', `${state.layout.name} copy`);
  if (!name) return;
  try {
    await createCustomPreset(state.layout, name);
    showPresetStatus(`Created ${name}.`);
  } catch {
    showPresetStatus('The preset could not be duplicated.');
  }
}

async function deleteActivePreset() {
  const current = activeLayoutRecord();
  if (!current || current.builtin || !(await confirmPresetDelete(current.name))) return;

  try {
    cancelPendingLayout();
    await persistQueue;
    await writeJson(TOKEN, 'DELETE', `/api/layouts/${encodeURIComponent(current.id)}`);
    state.layouts = state.layouts.filter((layout) => layout.id !== current.id);
    const fallback = state.layouts.find((layout) => layout.id === 'builtin-overview') ?? state.layouts[0];
    if (fallback) await switchPreset(fallback.id, true);
    showPresetStatus(`Deleted ${current.name}.`);
  } catch {
    persistLayoutSoon();
    showPresetStatus('The preset could not be deleted.');
  }
}

function safeFileName(name) {
  const base = String(name ?? 'preset').replace(/[<>:"/\\|?*\x00-\x1f]/g, '-').trim();
  return (base || 'preset').slice(0, 80);
}

function exportActivePreset() {
  const current = activeLayoutRecord();
  if (!current) return;
  const payload = layoutPayload(current);
  // Built-in IDs are intentionally non-importable. Give an exported built-in a custom-safe
  // ID so the file can still round-trip through the public import endpoint.
  if (current.builtin) payload.id = uniquePresetId(`${current.name}-preset`);
  const blob = new Blob([`${JSON.stringify({ layouts: [payload] }, null, 2)}\n`], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `${safeFileName(current.name)}.hardware-live.json`;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
  showPresetStatus(`Exported ${current.name}.`);
}

async function refreshLayouts() {
  const layouts = await getJson('/api/layouts');
  if (Array.isArray(layouts) && layouts.length) {
    state.layouts = layouts;
    renderPresetSelect();
  }
}

async function importPresetFile(file) {
  if (!file) return;
  if (file.size > MAX_IMPORT_BYTES) {
    showPresetStatus('Import files must be 256 KB or smaller.');
    return;
  }

  try {
    const parsed = JSON.parse(await file.text());
    const document = Array.isArray(parsed?.layouts) ? parsed : { layouts: [parsed] };
    const previousIds = new Set(state.layouts.map((layout) => layout.id));
    const result = await writeJson(TOKEN, 'POST', '/api/layouts/import', document);
    await refreshLayouts();
    const importedLayouts = state.layouts.filter((layout) => !layout.builtin && !previousIds.has(layout.id));
    const unavailable = importedLayouts.reduce((sum, layout) => sum + countUnavailableWidgets(layout), 0);
    const renamed = result?.renamed ?? [];
    const details = [];
    if (renamed.length) {
      details.push(`${renamed.length} renamed (${renamed.map((item) => `${item.from} → ${item.to}`).join(', ')})`);
    }
    if (unavailable) {
      details.push(`${unavailable} widget${unavailable === 1 ? '' : 's'} unavailable on this PC`);
    }
    if (importedLayouts[0]) await switchPreset(importedLayouts[0].id, true);
    showPresetStatus(`${result?.imported ?? 0} preset${result?.imported === 1 ? '' : 's'} imported${details.length ? `; ${details.join('; ')}` : ''}.`);
  } catch {
    showPresetStatus('The preset file is invalid or could not be imported.');
  }
}

presetSelectEl.addEventListener('change', () => void switchPreset(presetSelectEl.value, true));
presetMenuToggleBtn.addEventListener('click', () => {
  const open = presetMenuEl.hidden;
  presetMenuEl.hidden = !open;
  presetMenuToggleBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
});
document.getElementById('preset-save-new').addEventListener('click', () => void saveAsNew());
document.getElementById('preset-rename').addEventListener('click', () => void renameActivePreset());
document.getElementById('preset-duplicate').addEventListener('click', () => void duplicateActivePreset());
document.getElementById('preset-delete').addEventListener('click', () => void deleteActivePreset());
document.getElementById('preset-export').addEventListener('click', exportActivePreset);
const importFileEl = document.getElementById('preset-import-file');
document.getElementById('preset-import').addEventListener('click', () => importFileEl.click());
importFileEl.addEventListener('change', () => {
  const [file] = importFileEl.files ?? [];
  importFileEl.value = '';
  void importPresetFile(file);
});

// ---- Unit setting --------------------------------------------------------------------

function renderTemperatureToggle() {
  temperatureCBtn.setAttribute('aria-pressed', state.temperatureUnit === 'C' ? 'true' : 'false');
  temperatureFBtn.setAttribute('aria-pressed', state.temperatureUnit === 'F' ? 'true' : 'false');
}

async function setTemperatureUnit(unit, persist = true) {
  if (unit !== 'C' && unit !== 'F') return;
  const previousUnit = state.temperatureUnit;
  state.temperatureUnit = unit;
  if (persist) state.health = null;
  renderTemperatureToggle();
  renderGrid(true);
  picker.refresh();

  if (!persist) return;
  try {
    await writeSettings({ temperatureUnit: unit });
    const health = await getJson('/api/health');
    if (state.temperatureUnit === unit) {
      state.health = health;
      updateGridValues();
    }
  } catch {
    if (state.temperatureUnit === unit) {
      state.temperatureUnit = previousUnit;
      renderTemperatureToggle();
      renderGrid(true);
    }
    showPresetStatus(`The °${unit} setting could not be saved.`);
  }
}

temperatureCBtn.addEventListener('click', () => void setTemperatureUnit('C'));
temperatureFBtn.addEventListener('click', () => void setTemperatureUnit('F'));

// ---- Edit mode and reordering ---------------------------------------------------------

let persistTimer = null;
let persistQueue = Promise.resolve();
function cancelPendingLayout() {
  if (persistTimer) {
    window.clearTimeout(persistTimer);
    persistTimer = null;
  }
}

function persistLayoutSoon() {
  cancelPendingLayout();
  persistTimer = window.setTimeout(() => {
    persistTimer = null;
    void persistLayout();
  }, 400);
}

async function flushPendingLayout() {
  const needsSave = Boolean(persistTimer);
  cancelPendingLayout();
  await persistQueue;
  if (needsSave) await persistLayout();
}

async function persistLayout() {
  cancelPendingLayout();
  if (activeLayoutRecord()?.builtin) return;
  const payload = layoutPayload(state.layout);
  persistQueue = persistQueue.then(async () => {
    try {
      await writeJson(TOKEN, 'PUT', `/api/layouts/${encodeURIComponent(payload.id)}`, payload);
    } catch {
      showPresetStatus('Layout changes could not be saved.');
    }
  });
  await persistQueue;
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
    if (!fromWidget || !toWidget) return;

    const fullFrom = state.layout.widgets.indexOf(fromWidget);
    const fullTo = state.layout.widgets.indexOf(toWidget);
    state.layout.widgets = moveIndex(state.layout.widgets, fullFrom, fullTo);
    syncActiveLayoutRecord();
    renderGrid(true);
    persistLayoutSoon();
  },
});

editToggleBtn.addEventListener('click', async () => {
  if (!state.editMode) {
    if (!(await forkBuiltInForEditing())) return;
    setEditMode(true);
    return;
  }

  setEditMode(false);
  await persistLayout();
});

// ---- Sensor picker -------------------------------------------------------------------

const picker = createSensorPicker({
  dialogEl: document.getElementById('sensor-picker'),
  getMeta: () => state.meta,
  getLatestValue: (id) => state.values[id],
  getTemperatureUnit: () => state.temperatureUnit,
  onAdd({ kind, ref }) {
    state.layout.widgets.push({ kind, size: kind === 'chart' ? 'M' : 'S', ref });
    syncActiveLayoutRecord();
    renderGrid(true);
    persistLayoutSoon();
  },
});

addWidgetBtn.addEventListener('click', () => picker.open());

// ---- Polling -------------------------------------------------------------------------

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
    picker.refresh();
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
    if (!data) return;
    const values = {};
    for (const sensor of data.sensors ?? []) values[sensor.id] = sensor.value;
    state.values = values;
    for (const [id, series] of Object.entries(data.history ?? {})) state.history[id] = series;
    if (state.layout.sort === 'headroom') renderGrid();
    else updateGridValues();
  },
  onStatus: setConnectionState,
});

// ---- Startup -------------------------------------------------------------------------

async function loadInitialState() {
  let layouts = null;
  let settings = null;
  try {
    [layouts, settings] = await Promise.all([getJson('/api/layouts'), getJson('/api/settings')]);
  } catch {
    showPresetStatus('Saved presets could not be loaded; showing Overview while reconnecting.');
  }

  if (Array.isArray(layouts) && layouts.length) state.layouts = layouts;
  const requestedId = settings?.activePresetId;
  const active = state.layouts.find((layout) => layout.id === requestedId)
    ?? state.layouts.find((layout) => layout.id === 'builtin-overview')
    ?? state.layouts[0];
  if (active) {
    state.activePresetId = active.id;
    state.layout = cloneLayout(active);
  }

  const storedUnit = settings?.temperatureUnit;
  state.temperatureUnit = storedUnit === 'C' || storedUnit === 'F'
    ? storedUnit
    : temperatureUnitForLanguage(navigator.language);
  renderTemperatureToggle();
  renderPresetSelect();

  const initialSettings = {};
  if (!requestedId || requestedId !== state.activePresetId) initialSettings.activePresetId = state.activePresetId;
  if (storedUnit !== 'C' && storedUnit !== 'F') initialSettings.temperatureUnit = state.temperatureUnit;
  if (Object.keys(initialSettings).length) {
    try {
      await writeSettings(initialSettings);
    } catch {
      showPresetStatus('Initial preset preferences could not be saved.');
    }
  }
}

async function start() {
  renderQuickFilterBar();
  await loadInitialState();
  renderGrid(true);
  metaPoller.start();
  notesPoller.start();
  healthPoller.start();
  snapshotPoller.start();
}

start().catch((error) => {
  console.error('[hardware-live] startup failed', error);
});
