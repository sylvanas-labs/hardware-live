// The sensor picker dialog (docs/SPEC.md step5 feature 5): search + hardware/sensor-type
// filter chips + "only sensors with a role" toggle, results grouped by hardware, then a
// kind step (tile/chart/gauge). Never fetches its own snapshot -- the app already polls
// /api/snapshot every second, so "current value" is read straight from that cache.

import { el, clear, setSanitizedText } from './dom.js';
import {
  matchesSensorSearch,
  hardwareKindFromType,
  sensorTypeCategory,
  unitForSensorType,
  formatValue,
  sanitizeDisplayText,
} from './logic.js';

const HARDWARE_KINDS = ['CPU', 'GPU', 'Motherboard', 'Memory', 'Storage', 'Battery', 'Other'];
const SENSOR_TYPES = ['Temperature', 'Power', 'Clock', 'Load', 'Fan', 'Voltage', 'Other'];
const WIDGET_KINDS = ['tile', 'chart', 'gauge'];

export function createSensorPicker({ dialogEl, getMeta, getLatestValue, onAdd }) {
  const searchInput = dialogEl.querySelector('#picker-search');
  const roleOnlyBtn = dialogEl.querySelector('#picker-role-only');
  const hwChipsEl = dialogEl.querySelector('#picker-hardware-chips');
  const typeChipsEl = dialogEl.querySelector('#picker-type-chips');
  const resultsEl = dialogEl.querySelector('#picker-results');
  const kindStepEl = dialogEl.querySelector('#picker-kind-step');
  const kindChoicesEl = dialogEl.querySelector('#picker-kind-choices');
  const cancelBtn = dialogEl.querySelector('#picker-cancel');

  const state = { search: '', hwKind: null, sensorType: null, roleOnly: false, selectedSensor: null };

  function chip(label, active, onClick) {
    const button = el('button', {
      className: 'chip',
      text: label,
      attrs: { type: 'button', 'aria-pressed': active ? 'true' : 'false' },
    });
    button.addEventListener('click', onClick);
    return button;
  }

  function renderChips() {
    clear(hwChipsEl);
    for (const kind of HARDWARE_KINDS) {
      hwChipsEl.append(
        chip(kind, state.hwKind === kind, () => {
          state.hwKind = state.hwKind === kind ? null : kind;
          renderChips();
          renderResults();
        }),
      );
    }

    clear(typeChipsEl);
    for (const type of SENSOR_TYPES) {
      typeChipsEl.append(
        chip(type, state.sensorType === type, () => {
          state.sensorType = state.sensorType === type ? null : type;
          renderChips();
          renderResults();
        }),
      );
    }
  }

  function candidateRows() {
    const meta = getMeta();
    const sensors = meta?.sensors ?? [];
    const hardware = meta?.hardware ?? [];
    const roles = meta?.roles ?? [];
    const labels = meta?.labels ?? {};
    const hardwareById = new Map(hardware.map((hw) => [hw.id, hw]));
    const roleBySensorId = new Map(roles.map((role) => [role.sensorId, role.role]));

    return sensors.map((sensor) => {
      const hw = hardwareById.get(sensor.hardwareId);
      const label = labels[sensor.id];
      return {
        id: sensor.id,
        name: sensor.name,
        title: label?.title ?? sensor.name,
        subtitle: label?.subtitle ?? null,
        type: sensor.type,
        hardwareId: sensor.hardwareId,
        hardwareName: hw?.name ?? '',
        hardwareKind: hardwareKindFromType(hw?.type),
        role: roleBySensorId.get(sensor.id) ?? null,
      };
    });
  }

  function filteredRows() {
    return candidateRows().filter((row) => {
      if (state.hwKind && row.hardwareKind !== state.hwKind) return false;
      if (state.sensorType && sensorTypeCategory(row.type) !== state.sensorType) return false;
      if (state.roleOnly && !row.role) return false;
      return matchesSensorSearch(state.search, row);
    });
  }

  function renderResults() {
    clear(resultsEl);
    const rows = filteredRows();
    if (rows.length === 0) {
      resultsEl.append(el('p', { className: 'picker-empty', text: 'No sensors match.' }));
      return;
    }

    const byHardware = new Map();
    for (const row of rows) {
      const key = row.hardwareId ?? '';
      if (!byHardware.has(key)) {
        byHardware.set(key, { name: row.hardwareName || 'Unknown hardware', rows: [] });
      }
      byHardware.get(key).rows.push(row);
    }

    for (const group of byHardware.values()) {
      const heading = el('div', { className: 'picker-group-heading' });
      setSanitizedText(heading, group.name);
      resultsEl.append(heading);

      for (const row of group.rows) {
        const value = getLatestValue(row.id);
        const unit = unitForSensorType(row.type);
        const button = el('button', {
          className: 'picker-row',
          attrs: { type: 'button', role: 'option', 'aria-selected': 'false' },
        });
        const nameNode = document.createElement('span');
        setSanitizedText(nameNode, sanitizeDisplayText(row.title) || row.id);
        if (row.subtitle) {
          const subtitleNode = el('span', { className: 'muted picker-subtitle' });
          setSanitizedText(subtitleNode, `(${row.subtitle})`);
          nameNode.append(document.createTextNode(' '), subtitleNode);
        }
        const valueNode = el('span', {
          className: 'muted',
          text: formatValue(value, unit, unit === '°C' ? 1 : 0),
        });
        button.append(nameNode, valueNode);
        if (row.role) {
          const badge = el('span', { className: 'role-badge', text: row.role });
          button.append(badge);
        }

        button.addEventListener('click', () => selectSensor(row));
        resultsEl.append(button);
      }
    }
  }

  function selectSensor(row) {
    state.selectedSensor = row;
    for (const button of resultsEl.querySelectorAll('.picker-row')) {
      button.setAttribute('aria-selected', 'false');
    }
    renderKindStep();
  }

  function renderKindStep() {
    clear(kindChoicesEl);
    kindStepEl.hidden = false;
    for (const kind of WIDGET_KINDS) {
      const button = el('button', { className: 'chip', text: kind, attrs: { type: 'button' } });
      button.addEventListener('click', () => {
        const row = state.selectedSensor;
        if (!row) {
          return;
        }

        onAdd({ kind, ref: { id: row.id, hw: row.hardwareName || row.hardwareId || 'unknown' } });
        dialogEl.close();
      });
      kindChoicesEl.append(button);
    }
  }

  searchInput.addEventListener('input', () => {
    state.search = searchInput.value;
    renderResults();
  });

  roleOnlyBtn.addEventListener('click', () => {
    state.roleOnly = !state.roleOnly;
    roleOnlyBtn.setAttribute('aria-pressed', state.roleOnly ? 'true' : 'false');
    renderResults();
  });

  cancelBtn.addEventListener('click', () => dialogEl.close());
  dialogEl.addEventListener('cancel', () => {
    // <dialog>'s native Escape-to-close already fires this; nothing extra to clean up since
    // all picker state is reset on the next open().
  });

  function open() {
    state.search = '';
    state.hwKind = null;
    state.sensorType = null;
    state.roleOnly = false;
    state.selectedSensor = null;
    searchInput.value = '';
    roleOnlyBtn.setAttribute('aria-pressed', 'false');
    kindStepEl.hidden = true;
    renderChips();
    renderResults();
    dialogEl.showModal();
    searchInput.focus();
  }

  return { open };
}
