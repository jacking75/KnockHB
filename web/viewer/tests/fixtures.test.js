import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseSignal } from '../connection.js';
const fixtures = JSON.parse(readFileSync(new URL('../../../tests/fixtures/signals.json', import.meta.url)));
for (const fixture of fixtures) test(`golden protocol ${fixture.name}`, () => {
  if (fixture.valid) assert.deepEqual(parseSignal(JSON.stringify(fixture.message)), fixture.message);
  else assert.throws(() => parseSignal(JSON.stringify(fixture.message)));
});
