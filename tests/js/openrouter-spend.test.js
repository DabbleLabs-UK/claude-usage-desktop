'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/index.html'), 'utf8');
function source(name) {
  const match = html.match(new RegExp('  function ' + name + '\\([^]*?\\n  \\}'));
  assert.ok(match, 'Missing function: ' + name);
  return match[0];
}
const body = { innerHTML: '' };
const context = vm.createContext({ document: { getElementById: () => body }, currencySymbol: () => '$', SPEND_MONTHLY_CEILING: 200,
  _agentSpendData: null, _openRouterSpendStatus: 'notConfigured', renderAgentSpend: () => {} });
for (const name of ['_spendEscape', '_validOpenRouterSpend', 'acceptOpenRouterSpend', '_spendBar', '_relativeAge', 'renderAgentSpendCard', '_spendAvgCostPerRun', '_spendStatRow', '_renderAgentSpendDetail']) vm.runInContext(source(name), context);
const period = { totalCost: 1.972726, pricedRuns: 172, unpricedRuns: 0 };
const data = { currency: 'USD', generatedAt: new Date().toISOString(), timezone: 'Europe/London',
  rolling24h: period, rolling7d: { ...period, dailyAverage: period.totalCost / 7 }, monthToDate: period,
  calendarWeek: { ...period, startsAt: '2026-10-03T23:00:00Z' },
  runRates: { monthlyAt24hPace: period.totalCost * 30, monthlyAt7dPace: period.totalCost / 7 * 30 },
  breakdownByModel: [], breakdownByProvider: [], dailyHistory: [] };
let card = context.renderAgentSpendCard(data);
assert.match(card, /OpenRouter Account Spend/);
assert.match(card, /THIS WEEK \(SUN\)/);
assert.match(card, /\$1\.97 &middot; 172 requests/);
assert.match(card, /7d total \$1\.97/);
assert.doesNotMatch(card, /Launcher|Stale/);
for (const invalid of [null, {}, { ...data, rolling24h: {} }, { ...data, monthToDate: { ...period, totalCost: NaN } }, { ...data, calendarWeek: null }]) {
  card = context.renderAgentSpendCard(invalid);
  assert.match(card, /Spending unavailable/);
  assert.doesNotMatch(card, /\$0\.00/);
}
const zero = { ...data, rolling24h: { ...period, totalCost: 0, pricedRuns: 0 }, calendarWeek: { ...period, totalCost: 0, pricedRuns: 0 } };
assert.match(context.renderAgentSpendCard(zero), /\$0\.00 &middot; 0 requests/);
assert.match(context.renderAgentSpendCard({ ...data, isStale: true }), /Stale - last successful update/);
assert.match(context.renderAgentSpendCard({ ...data, generatedAt: new Date(Date.now() - 31 * 60000).toISOString() }), /Stale - last successful update/);
assert.match(context.renderAgentSpendCard({ ...data, generatedAt: new Date(Date.now() + 6 * 60000).toISOString() }), /Stale - last successful update/);
context.acceptOpenRouterSpend({ status: 'live', data });
assert.equal(context._agentSpendData, data);
context.acceptOpenRouterSpend({ status: 'stale', data });
assert.equal(context._agentSpendData.isStale, true);
context.acceptOpenRouterSpend({ status: 'unavailable', data: null });
assert.equal(context._agentSpendData, null); // Never keep another account's data after backend clears it.
context.acceptOpenRouterSpend({ status: 'live', data: {} });
assert.equal(context._openRouterSpendStatus, 'unavailable');
context._agentSpendData = { ...data, timezone: '<img onerror=x>', breakdownByModel: [{ model: '<img onerror=x>', provider: '<script>', cost: 1, pricedRuns: 1, unpricedRuns: 0 }], breakdownByProvider: [{ provider: '<script>', cost: 1, pricedRuns: 1, unpricedRuns: 0 }], dailyHistory: [{ date: '<script>', cost: 1 }] };
context._renderAgentSpendDetail();
assert.doesNotMatch(body.innerHTML, /<script>|<img onerror/);
assert.match(body.innerHTML, /&lt;img onerror=x&gt;/);
assert.match(body.innerHTML, /across all API keys in the connected account/);
assert.doesNotMatch(html, /fetch\('\/api\/agent-spend'\)|conn\.on\('agentSpendUpdated'/);
assert.equal((html.match(/fetch\('\/api\/openrouter-spend'\)/g) || []).length, 4);
assert.match(html, /conn\.on\('openRouterSpendUpdated'/);
for (const script of html.matchAll(/<script\b[^>]*>([^]*?)<\/script>/gi)) if (script[1].trim()) new vm.Script(script[1]);
console.log('OpenRouter spend rendering, freshness, scope and feed tests passed');
