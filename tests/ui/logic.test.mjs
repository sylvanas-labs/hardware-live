// Run with: node --test tests/ui/
// Plain Node test runner, no npm packages (docs/SPEC.md step5 "Tests"). Covers the pure
// logic functions that back the dashboard UI: ref resolution, unit mapping, threshold level
// calc, keyboard reorder index math, and name sanitizing.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { test } from 'node:test';

import {
  unitForSensorType,
  hardwareKindFromType,
  sensorTypeCategory,
  thresholdLevel,
  resolveWidgetRef,
  matchesSensorSearch,
  sanitizeDisplayText,
  formatValue,
  moveIndex,
  computeKeyboardMoveTarget,
  ageMinutesLabel,
  isLikelyDisconnected,
  groupForRole,
} from '../../src/HardwareLive.Core/wwwroot/js/logic.js';

test('unitForSensorType maps every documented sensor type', () => {
  assert.equal(unitForSensorType('Temperature'), 'C');
  assert.equal(unitForSensorType('Power'), 'W');
  assert.equal(unitForSensorType('Clock'), 'MHz');
  assert.equal(unitForSensorType('Load'), '%');
  assert.equal(unitForSensorType('Level'), '%');
  assert.equal(unitForSensorType('Control'), '%');
  assert.equal(unitForSensorType('Fan'), 'RPM');
  assert.equal(unitForSensorType('Voltage'), 'V');
  assert.equal(unitForSensorType('Data'), 'GB');
  assert.equal(unitForSensorType('SmallData'), 'MB');
  assert.equal(unitForSensorType('Throughput'), 'MB/s');
  assert.equal(unitForSensorType('Factor'), '');
  assert.equal(unitForSensorType('SomethingUnknown'), '');
});

test('hardwareKindFromType buckets GPUs and board-adjacent types', () => {
  assert.equal(hardwareKindFromType('Cpu'), 'CPU');
  assert.equal(hardwareKindFromType('GpuNvidia'), 'GPU');
  assert.equal(hardwareKindFromType('GpuAmd'), 'GPU');
  assert.equal(hardwareKindFromType('GpuIntel'), 'GPU');
  assert.equal(hardwareKindFromType('Motherboard'), 'Motherboard');
  assert.equal(hardwareKindFromType('SuperIO'), 'Motherboard');
  assert.equal(hardwareKindFromType('EmbeddedController'), 'Motherboard');
  assert.equal(hardwareKindFromType('Memory'), 'Memory');
  assert.equal(hardwareKindFromType('Storage'), 'Storage');
  assert.equal(hardwareKindFromType('Battery'), 'Battery');
  assert.equal(hardwareKindFromType('Nic'), 'Other');
});

test('sensorTypeCategory buckets Load-ish and Fan-ish types', () => {
  assert.equal(sensorTypeCategory('Load'), 'Load');
  assert.equal(sensorTypeCategory('Level'), 'Load');
  assert.equal(sensorTypeCategory('Control'), 'Load');
  assert.equal(sensorTypeCategory('Fan'), 'Fan');
  assert.equal(sensorTypeCategory('Voltage'), 'Voltage');
  assert.equal(sensorTypeCategory('Data'), 'Other');
});

test('thresholdLevel classifies ok/watch/critical and handles absence', () => {
  const threshold = { watch: 80, critical: 90 };
  assert.equal(thresholdLevel(50, threshold), 'ok');
  assert.equal(thresholdLevel(80, threshold), 'watch');
  assert.equal(thresholdLevel(90, threshold), 'critical');
  assert.equal(thresholdLevel(null, threshold), null);
  assert.equal(thresholdLevel(NaN, threshold), null);
  assert.equal(thresholdLevel(50, null), null);
});

test('resolveWidgetRef expands a bare role to every matching sensor in identifier order', () => {
  const meta = {
    sensors: [{ id: '/cpu/0/clock/1' }, { id: '/cpu/0/clock/0' }],
    roles: [
      { sensorId: '/cpu/0/clock/1', role: 'cpu.clock.core' },
      { sensorId: '/cpu/0/clock/0', role: 'cpu.clock.core' },
    ],
  };

  assert.deepEqual(resolveWidgetRef({ role: 'cpu.clock.core' }, meta), [
    '/cpu/0/clock/0',
    '/cpu/0/clock/1',
  ]);
});

test('resolveWidgetRef resolves an exact id when present on this PC', () => {
  const meta = { sensors: [{ id: '/cpu/0/temp/1' }], roles: [] };
  assert.deepEqual(resolveWidgetRef({ id: '/cpu/0/temp/1', hw: 'CPU' }, meta), ['/cpu/0/temp/1']);
});

test('resolveWidgetRef falls back to the role when the exact id is missing', () => {
  const meta = {
    sensors: [{ id: '/cpu/0/temp/2' }],
    roles: [{ sensorId: '/cpu/0/temp/2', role: 'cpu.temp.control' }],
  };

  assert.deepEqual(
    resolveWidgetRef({ id: '/cpu/0/temp/9', hw: 'CPU', role: 'cpu.temp.control' }, meta),
    ['/cpu/0/temp/2'],
  );
});

test('resolveWidgetRef resolves to nothing when neither id nor role match', () => {
  const meta = { sensors: [], roles: [] };
  assert.deepEqual(resolveWidgetRef({ id: '/cpu/0/temp/9', hw: 'CPU' }, meta), []);
  assert.deepEqual(resolveWidgetRef(null, meta), []);
  assert.deepEqual(resolveWidgetRef({}, meta), []);
});

test('matchesSensorSearch matches name, hardware, role and id case-insensitively', () => {
  const entry = { name: 'Tctl', hardwareName: 'AMD Ryzen 7 9800X3D', role: 'cpu.temp.control', id: '/amdcpu/0/temp/0' };
  assert.equal(matchesSensorSearch('', entry), true);
  assert.equal(matchesSensorSearch('tctl', entry), true);
  assert.equal(matchesSensorSearch('RYZEN', entry), true);
  assert.equal(matchesSensorSearch('cpu.temp', entry), true);
  assert.equal(matchesSensorSearch('amdcpu/0', entry), true);
  assert.equal(matchesSensorSearch('nvidia', entry), false);
});

test('sanitizeDisplayText strips C0 control characters from the real DIMM fixture name', () => {
  const fixturePath = path.join(
    path.dirname(fileURLToPath(import.meta.url)),
    '..',
    'fixtures',
    'amd-9800x3d_nvidia-5090_desktop.json',
  );
  const fixture = JSON.parse(readFileSync(fixturePath, 'utf8'));
  const dimm = fixture.hardware.find((hw) => hw.id === '/memory/dimm/3');

  assert.ok(dimm, 'expected the fixture to contain the DIMM with embedded control characters');
  assert.match(dimm.name, /[\x00-\x1f]/);

  const clean = sanitizeDisplayText(dimm.name);
  assert.doesNotMatch(clean, /[\x00-\x1f]/);
  assert.equal(clean, 'G Skill Intl - F5-6000J3636F32GAAA} (#3)');
});

test('sanitizeDisplayText strips a bare tab (still a C0 control) and passes plain text through', () => {
  assert.equal(sanitizeDisplayText('a\tb'), 'ab');
  assert.equal(sanitizeDisplayText(null), '');
  assert.equal(sanitizeDisplayText(undefined), '');
  assert.equal(sanitizeDisplayText('plain text'), 'plain text');
});

test('formatValue renders an en dash for missing/non-finite values', () => {
  assert.equal(formatValue(null, 'C'), '–');
  assert.equal(formatValue(NaN, 'C'), '–');
  assert.equal(formatValue(42.345, 'C', 1), '42.3 C');
  assert.equal(formatValue(0, '%', 0), '0 %');
  assert.equal(formatValue(5, ''), '5');
});

test('moveIndex reorders without mutating the input and is a no-op on bad indices', () => {
  const list = ['a', 'b', 'c', 'd'];
  assert.deepEqual(moveIndex(list, 0, 2), ['b', 'c', 'a', 'd']);
  assert.deepEqual(moveIndex(list, 3, 0), ['d', 'a', 'b', 'c']);
  assert.deepEqual(list, ['a', 'b', 'c', 'd'], 'original array must not be mutated');
  assert.deepEqual(moveIndex(list, 0, 0), list);
  assert.deepEqual(moveIndex(list, -1, 2), list);
  assert.deepEqual(moveIndex(list, 0, 99), list);
});

test('computeKeyboardMoveTarget moves across grid rows/columns and clamps at the edges', () => {
  // 3-column grid, 7 items (rows: [0,1,2] [3,4,5] [6]).
  assert.equal(computeKeyboardMoveTarget(4, 'ArrowLeft', 3, 7), 3);
  assert.equal(computeKeyboardMoveTarget(4, 'ArrowRight', 3, 7), 5);
  assert.equal(computeKeyboardMoveTarget(4, 'ArrowUp', 3, 7), 1);
  assert.equal(computeKeyboardMoveTarget(4, 'ArrowDown', 3, 7), null, 'index 7 is out of bounds');
  assert.equal(computeKeyboardMoveTarget(0, 'ArrowLeft', 3, 7), null, 'cannot move left of the grid');
  assert.equal(computeKeyboardMoveTarget(0, 'Escape', 3, 7), null, 'non-arrow keys are ignored');
  assert.equal(computeKeyboardMoveTarget(0, 'ArrowDown', 3, 0), null, 'empty grid never moves');
});

test('ageMinutesLabel hides the label under 30 minutes and includes source only when present', () => {
  assert.deepEqual(ageMinutesLabel(10, 'Claude Code'), { dimmed: false, text: '' });
  assert.deepEqual(ageMinutesLabel(31, undefined), { dimmed: true, text: '31 min ago' });
  assert.deepEqual(ageMinutesLabel(45.6, 'a script'), { dimmed: true, text: 'from a script, 46 min ago' });
});

test('groupForRole buckets dotted role prefixes into quick-filter groups', () => {
  assert.equal(groupForRole('cpu.temp.control'), 'CPU');
  assert.equal(groupForRole('gpu.temp.core'), 'GPU');
  assert.equal(groupForRole('igpu.temp.core'), 'GPU');
  assert.equal(groupForRole('board.temp.vrm'), 'Board');
  assert.equal(groupForRole('dimm.temp'), 'Memory');
  assert.equal(groupForRole('ram.load'), 'Memory');
  assert.equal(groupForRole('storage.temp'), 'Storage');
  assert.equal(groupForRole('fan.cpu'), 'Fans');
  assert.equal(groupForRole('battery.charge'), null);
  assert.equal(groupForRole(null), null);
});

test('isLikelyDisconnected flags an all-zero history but not an all-null or mixed one', () => {
  assert.equal(isLikelyDisconnected([0, 0, 0]), true);
  assert.equal(isLikelyDisconnected([null, null]), false);
  assert.equal(isLikelyDisconnected([0, null, 0]), false);
  assert.equal(isLikelyDisconnected([0, 1200, 0]), false);
  assert.equal(isLikelyDisconnected([]), false);
});
