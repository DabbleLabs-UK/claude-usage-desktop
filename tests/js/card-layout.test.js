'use strict';
const assert = require('node:assert/strict');
const layout = require('../../wwwroot/card-layout.js');
assert.deepEqual(layout.normalize(['claude:week', 'claude:week', '<script>', null]), ['claude:week']);
assert.deepEqual(layout.resolve(['codex:credits', 'missing'], ['claude:week', 'codex:credits', 'new']), ['codex:credits', 'claude:week', 'new']);
assert.deepEqual(layout.move(['a', 'hidden', 'b', 'c'], ['a', 'b', 'c'], 'c', 'a'), ['c', 'a', 'hidden', 'b']);
assert.deepEqual(layout.move(['a', 'b', 'c'], ['a', 'b', 'c'], 'a', 'c'), ['b', 'c', 'a']);
assert.deepEqual(layout.resolve(['c', 'a', 'hidden', 'b'], ['a', 'hidden', 'b', 'c']), ['c', 'a', 'hidden', 'b']);
assert.deepEqual(layout.move(['a'], ['a', 'b'], 'unknown', 'a'), ['a', 'b']);
assert.deepEqual(layout.swap(['a', 'hidden', 'b', 'c'], ['a', 'b', 'c'], 'a', 'c'), ['c', 'hidden', 'b', 'a']);
assert.deepEqual(layout.swap(['a'], ['a', 'b'], 'unknown', 'a'), ['a', 'b']);
// Every valid directional move has an inverse and never wraps across a row edge.
for (const columns of [1, 2, 3]) {
  for (let count = 1; count <= 10; count++) {
    for (let index = 0; index < count; index++) {
      for (const [direction, inverse] of [['up', 'down'], ['down', 'up'], ['left', 'right'], ['right', 'left']]) {
        const target = layout.neighbour(index, count, columns, direction);
        const row = Math.floor(index / columns), column = index % columns;
        const nextRow = row + (direction === 'up' ? -1 : direction === 'down' ? 1 : 0);
        const nextColumn = column + (direction === 'left' ? -1 : direction === 'right' ? 1 : 0);
        const expected = nextRow < 0 || nextColumn < 0 || nextColumn >= columns || nextRow * columns + nextColumn >= count ? -1 : nextRow * columns + nextColumn;
        assert.equal(target, expected);
        if (target >= 0) assert.equal(layout.neighbour(target, count, columns, inverse), index);
      }
    }
  }
}
console.log('Card layout ordering and grid-direction tests passed');
