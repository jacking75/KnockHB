import test from 'node:test';
import assert from 'node:assert/strict';
import { videoPoint } from '../input-geometry.js';

test('contain excludes letterbox and preserves 0..1 independent of viewer DPI', () => {
  const rect = { left: 100, top: 80, width: 800, height: 600 };
  assert.equal(videoPoint(rect, 1920, 1080, 500, 100), null);
  assert.deepEqual(videoPoint(rect, 1920, 1080, 100, 155), { x: 0, y: 0 });
  assert.deepEqual(videoPoint(rect, 1920, 1080, 900, 605), { x: 1, y: 1 });
  assert.deepEqual(videoPoint(rect, 1920, 1080, 500, 380), { x: .5, y: .5 });
});
test('portrait uses side bars without applying a second rotation', () => {
  const rect = { left: 0, top: 0, width: 1920, height: 1080 };
  assert.equal(videoPoint(rect, 2160, 3840, 0, 540), null);
  assert.deepEqual(videoPoint(rect, 2160, 3840, 960, 540), { x: .5, y: .5 });
  assert.equal(videoPoint(rect, 2160, 3840, NaN, 0), null);
  assert.equal(videoPoint(rect, 0, 3840, 960, 540), null);
});
