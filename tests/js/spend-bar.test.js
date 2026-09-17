'use strict';

// Regression tests for the API Spend card's fixed-ceiling bar math in wwwroot/index.html.
//
// There's no JS test framework/build in this repo -- this is a plain Node script (no
// dependencies), run with:   node tests/js/spend-bar.test.js
//
// Rather than duplicating the formula here (which could silently drift from the real code), this
// extracts the ACTUAL SPEND_MONTHLY_CEILING constant and _spendBar() function source straight out
// of wwwroot/index.html and evaluates them in an isolated scope, then reads the fill width back
// out of the real HTML string _spendBar() returns. If someone edits the bar math in index.html
// without updating this file, the test still exercises the real code -- only the extraction regex
// needs to keep matching the function's signature.

const fs = require('fs');
const path = require('path');
const assert = require('assert');

const indexPath = path.join(__dirname, '..', '..', 'wwwroot', 'index.html');
const html = fs.readFileSync(indexPath, 'utf8');

const ceilingMatch = html.match(/const SPEND_MONTHLY_CEILING = (\d+);/);
assert(ceilingMatch, 'Could not find "const SPEND_MONTHLY_CEILING = <n>;" in wwwroot/index.html -- extraction regex is stale.');

const fnMatch = html.match(/function _spendBar\(monthlyValue, color\) \{[\s\S]*?\n  \}/);
assert(fnMatch, 'Could not find _spendBar(monthlyValue, color) in wwwroot/index.html -- extraction regex is stale.');

// Build a real function bound to the real SPEND_MONTHLY_CEILING constant, both extracted
// verbatim from index.html above, in their own isolated scope (via Function()).
const spendBarFactory = new Function(`${ceilingMatch[0]}\n${fnMatch[0]}\nreturn _spendBar;`);
const spendBar = spendBarFactory();

function fillPctOf(html) {
  const m = html.match(/width:([\d.]+)%/);
  assert(m, `No width:%N% found in returned HTML: ${html}`);
  return parseFloat(m[1]);
}

function assertPct(monthlyValue, expectedPct, label) {
  const pct = fillPctOf(spendBar(monthlyValue, 'red'));
  assert.strictEqual(pct, expectedPct, `${label}: expected ${expectedPct}%, got ${pct}% (monthlyValue=${monthlyValue})`);
}

let failures = 0;
function test(name, fn) {
  try {
    fn();
    console.log(`  ok  - ${name}`);
  } catch (e) {
    failures++;
    console.error(`FAIL  - ${name}`);
    console.error(`        ${e.message}`);
  }
}

console.log(`Testing _spendBar() against a fixed $${ceilingMatch[1]}/mo ceiling\n`);

test('ceiling constant is 200', () => {
  assert.strictEqual(ceilingMatch[1], '200');
});

test('$0/mo -> 0%', () => {
  assertPct(0, 0.0, '$0/mo');
});

test('$100/mo -> 50%', () => {
  assertPct(100, 50.0, '$100/mo');
});

test('$200/mo -> 100% (exactly at the ceiling)', () => {
  assertPct(200, 100.0, '$200/mo');
});

test('$300/mo -> capped at 100% (over the ceiling)', () => {
  assertPct(300, 100.0, '$300/mo');
});

test('$50/mo -> 25%', () => {
  assertPct(50, 25.0, '$50/mo');
});

test('negative/garbage input never produces a negative or NaN width', () => {
  assertPct(-10, 0.0, 'negative monthlyValue');
  assertPct(null, 0.0, 'null monthlyValue');
  assertPct(undefined, 0.0, 'undefined monthlyValue');
});

test('the two bars are independent -- a $200/mo 24h pace does not affect a $50/mo 7d pace', () => {
  assertPct(200, 100.0, '24h pace at ceiling');
  assertPct(50, 25.0, '7d pace well under ceiling, unaffected by the other bar');
});

console.log('');
if (failures > 0) {
  console.error(`${failures} test(s) FAILED`);
  process.exit(1);
} else {
  console.log('All tests passed.');
  process.exit(0);
}
