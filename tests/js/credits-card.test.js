'use strict';

const assert = require('assert');
const { render } = require('../../wwwroot/credits-card.js');

function data(overrides) {
  return Object.assign({
    creditsBalance: 500,
    fetchedAt: '2026-10-09T12:00:00Z',
    isStale: false,
    creditsHistory: {
      last24Hours: { status: 'estimated', creditsPerDay: 50, estimatedDaysLeft: 10 },
      last7Days: { status: 'estimated', creditsPerDay: 25, estimatedDaysLeft: 20 },
      daily: [{ date: '2026-10-08', creditsSpent: 50 }, { date: '2026-10-07', creditsSpent: 0 }]
    }
  }, overrides);
}

let failures = 0;
function test(name, fn) {
  try { fn(); console.log('  ok - ' + name); }
  catch (error) { failures++; console.error('FAIL - ' + name + ': ' + error.message); }
}

test('no balance or history hides the card, but an exhausted balance remains visible', () => {
  assert.strictEqual(render(null), '');
  assert.strictEqual(render({}), '');
  const html = render(data({ creditsBalance: 0 }));
  assert(html.includes('0 left'));
  assert(html.includes('No credits remaining'));
  assert(!html.includes('days left'));
});

test('two observed paces give independent forecasts', () => {
  const html = render(data());
  assert(html.includes('50 credits/day'));
  assert(html.includes('25 credits/day'));
  assert(html.includes('About 10 days left'));
  assert(html.includes('About 20 days left'));
});

test('stale balance suppresses both forecasts but keeps historical spending', () => {
  const html = render(data({ isStale: true }));
  assert(html.includes('Stale balance; estimates paused'));
  assert(!html.includes('days left'));
  assert(!html.includes('credits/day'));
  assert(html.includes('50 credits spent'));
});

test('collecting, no spend and uncertain states explain why there is no forecast', () => {
  for (const [status, expected] of [['collecting', 'Collecting history'], ['noSpend', 'No credit spending observed'], ['uncertain', 'Balance changed; estimate unavailable']]) {
    const html = render(data({ creditsHistory: { last24Hours: { status }, last7Days: { status } } }));
    assert(html.includes(expected));
    assert(!html.includes('days left'));
  }
});

test('seven completed UTC days distinguish zero spend from missing history', () => {
  const html = render(data());
  assert.strictEqual((html.match(/role="listitem"/g) || []).length, 7);
  assert(html.includes('2026-10-07: 0 credits spent'));
  assert(html.includes('2026-10-02: incomplete or uncertain history'));
  assert(!html.includes('2026-10-09:'));
});

test('duplicate days do not imply known spend', () => {
  const sample = data();
  sample.creditsHistory.daily.push({ date: '2026-10-08', creditsSpent: 20 });
  assert(render(sample).includes('2026-10-08: incomplete or uncertain history'));
});

test('unlimited or unavailable balance never produces a depletion forecast', () => {
  for (const overrides of [{ creditsUnlimited: true }, { creditsBalance: null }]) {
    const html = render(data(overrides));
    assert(!html.includes('days left'));
    assert(!html.includes('credits/day'));
  }
});

test('invalid numbers and untrusted strings cannot become forecasts or HTML', () => {
  const sample = data({ creditsBalance: '<img src=x onerror=alert(1)>', fetchedAt: '<script>alert(1)</script>' });
  sample.creditsHistory.last24Hours = { status: 'estimated', creditsPerDay: Infinity, estimatedDaysLeft: NaN };
  sample.creditsHistory.last7Days = { status: '<img>', creditsPerDay: -1, estimatedDaysLeft: -1 };
  sample.creditsHistory.daily = [{ date: '\"><img src=x>', creditsSpent: Infinity }];
  const html = render(sample);
  assert(!/<img|<script|Infinity|NaN/.test(html));
  assert(!html.includes('days left'));
  assert(html.includes('time unavailable'));
});

test('extreme finite spending cannot create an infinite bar', () => {
  const sample = data();
  sample.creditsHistory.daily = [{ date: '2026-10-08', creditsSpent: Number.MAX_VALUE }];
  const html = render(sample);
  assert(html.includes('height:48.00px'));
  assert(!html.includes('Infinity'));
});

if (failures) process.exitCode = 1;
else console.log('All credits card tests passed.');
