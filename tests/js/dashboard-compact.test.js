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
const nodes = Object.fromEntries(['usageSource', 'untrackedToggle', 'usageInfo'].map(id => [id, {
  textContent: '', hidden: true, attributes: {},
  setAttribute(key, value) { this.attributes[key] = value; }
}]));
const context = vm.createContext({
  document: { getElementById: id => nodes[id] },
  color: () => 'green', projColor: () => 'green', barRed: value => value,
  fmtPct: value => value + '%', timeUntilSec: () => '1h',
  effectiveWindowMs: value => value, barPeriod: () => 'this week',
  fmtTimeWithoutService: () => '1h', currencySymbol: () => '$'
});
for (const name of ['card', 'billingCard', 'dashboardCard', 'renderUsageInfo']) vm.runInContext(source(name), context);

const window = { utilization: 10, resetsAt: new Date(Date.now() + 3600000).toISOString() };
const heading = markup => markup.match(/<span class="label">([^<]*)<\/span>/)[1];
assert.equal(heading(context.card('7-day', window, 604800000, 'Week', true, false, null, 'Claude')), 'Claude Week');
const codex = context.card('Codex Week', window, 604800000, 'Week', true, false, null, 'Codex');
assert.equal(heading(codex), 'Codex Week');
assert.match(codex, /data-card="Codex Week"/); // Detail routing must not change.
assert.equal(heading(context.card('7-day', null, 604800000, 'Week', true, false, null, 'Claude')), 'Claude 7-day');
assert.equal(heading(context.card('Nimbus Quill', { utilization: 0 }, null, null, true, false, null, 'Claude')), 'Claude Nimbus Quill');
assert.equal(heading(context.card('Codex Week', { utilization: 0 }, null, null, true, false, null, 'Codex')), 'Codex Week');
assert.equal(heading(context.card('7-day', window, 604800000, null, false)), '7-day');
assert.equal(heading(context.billingCard(null)), 'Claude Monthly Billing');
assert.equal(heading(context.billingCard({ currency: 'USD', utilization: 20, usedCredits: 2, monthlyLimit: 10 })), 'Claude Monthly Billing');
const wrapper = context.dashboardCard('codex:week', 'Codex', codex, true);
assert.doesNotMatch(wrapper, /provider-label|panel-head/);
assert.match(wrapper, /data-provider="Codex"/); // Stale styling still uses provider identity.
assert.equal((wrapper.match(/>Codex Week</g) || []).length, 1);

context.renderUsageInfo({ source: 'Dell' }, 2, 0);
assert.equal(nodes.usageSource.textContent, 'Usage from: Dell');
assert.equal(nodes.untrackedToggle.textContent, 'Show 2 windows not tracked this period');
assert.equal(nodes.untrackedToggle.hidden, false);
assert.equal(nodes.untrackedToggle.attributes['aria-expanded'], 'false');
assert.equal(nodes.usageInfo.hidden, false);
context.renderUsageInfo({ source: 'Dell', isStale: true }, 0, 2);
assert.equal(nodes.usageSource.textContent, 'Last reading from: Dell');
assert.equal(nodes.untrackedToggle.textContent, 'Hide untracked windows');
assert.equal(nodes.untrackedToggle.attributes['aria-expanded'], 'true');
context.renderUsageInfo({ source: 'Dell' }, 0, 0);
assert.equal(nodes.untrackedToggle.hidden, true);
assert.equal(nodes.usageInfo.hidden, false);
context.renderUsageInfo({}, 1, 0);
assert.equal(nodes.untrackedToggle.textContent, 'Show 1 window not tracked this period');
assert.equal(nodes.usageInfo.hidden, false);
context.renderUsageInfo({}, 0, 0);
assert.equal(nodes.usageInfo.hidden, true);
assert.match(html, /id="usageInfo"[^]*?id="usageSource"[^]*?<button id="untrackedToggle"[^]*?<\/div>/);
assert.doesNotMatch(source('render'), /class="untracked-toggle"|class="panel-head"/);
assert.doesNotMatch(source('renderCodex'), /class="panel-head"/);
for (const script of html.matchAll(/<script\b[^>]*>([^]*?)<\/script>/gi)) {
  if (script[1].trim()) new vm.Script(script[1]);
}
console.log('Compact dashboard headings, source row and inline script tests passed');
