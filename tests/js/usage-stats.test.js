'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const stats = require('../../wwwroot/usage-stats.js');

const ANCHOR = Date.parse('2026-10-16T12:00:00Z');
const iso = ms => new Date(ms).toISOString();
const point = (ms, v, extra) => Object.assign({ t: iso(ms), v }, extra);
function data(from, to, series) {
  return { from: iso(from), to: iso(to), earliest: iso(ANCHOR - 200 * stats.RANGE_MS['1d']), series: series || [] };
}
function mockDocument() {
  const nodes = {};
  function node(id) {
    if (!nodes[id]) nodes[id] = {
      innerHTML: '', textContent: '', disabled: false, attributes: {}, dataset: {},
      setAttribute(key, value) { this.attributes[key] = value; },
      classList: { values: new Set(), toggle(key, value) { value ? this.values.add(key) : this.values.delete(key); },
        add(key) { this.values.add(key); }, remove(key) { this.values.delete(key); }, contains(key) { return this.values.has(key); } }
    };
    return nodes[id];
  }
  const chips = Object.keys(stats.RANGE_MS).map(range => Object.assign(node(range), { dataset: { range } }));
  return { getElementById: node, querySelectorAll: () => chips, nodes };
}
function harness(customFetch) {
  let currentTime = ANCHOR;
  const calls = [], document = mockDocument();
  const fetch = async url => {
    calls.push(url);
    if (customFetch) return customFetch(url);
    const query = new URL(url, 'http://localhost').searchParams;
    const to = Date.parse(query.get('end'));
    return { ok: true, json: async () => data(to - stats.RANGE_MS[query.get('range')], to) };
  };
  const controller = stats.createController({ document, fetch, now: () => currentTime });
  return { controller, document, calls, setTime(value) { currentTime = value; } };
}

test('all ranges page exact adjacent windows against a frozen ceiling and return to the present', async () => {
  for (const [range, width] of Object.entries(stats.RANGE_MS)) {
    const h = harness();
    await h.controller.open();
    await h.controller.setRange(range);
    const latest = h.controller.snapshot();
    assert.equal(latest.to - latest.from, width);
    assert.equal(h.document.nodes.statsNext.disabled, true);
    assert.equal(h.document.nodes.statsLatest.disabled, true);
    h.setTime(ANCHOR + 100000);
    await h.controller.previous();
    const previous = h.controller.snapshot();
    assert.equal(previous.to, latest.from);
    assert.equal(previous.to - previous.from, width);
    await h.controller.previous();
    assert.equal(h.controller.snapshot().to, previous.from);
    await h.controller.next();
    assert.equal(h.controller.snapshot().to, previous.to);
    await h.controller.next();
    assert.equal(h.controller.snapshot().to, ANCHOR);
    await h.controller.next();
    assert.equal(h.controller.snapshot().to, ANCHOR);
    await h.controller.latest();
    assert.equal(h.controller.snapshot().to, ANCHOR + 100000);
    assert.equal(h.controller.snapshot().to - h.controller.snapshot().from, width);
    for (let i = 0; i < h.calls.length; i += 2) {
      assert.equal(h.calls[i].split('?')[1], h.calls[i + 1].split('?')[1], 'provider dates stay synchronized');
    }
  }
});

test('range changes preserve the historic end, next clamps to ceiling, open refreshes only latest', async () => {
  const h = harness();
  await h.controller.open();
  await h.controller.previous();
  const oldEnd = h.controller.snapshot().to;
  await h.controller.setRange('7d');
  assert.equal(h.controller.snapshot().to, oldEnd);
  assert.equal(h.controller.snapshot().from, oldEnd - stats.RANGE_MS['7d']);
  h.setTime(ANCHOR + 5000);
  await h.controller.open();
  assert.equal(h.controller.snapshot().to, oldEnd);
  await h.controller.next();
  assert.equal(h.controller.snapshot().to, ANCHOR);
  await h.controller.open();
  assert.equal(h.controller.snapshot().to, ANCHOR + 5000);
  const callCount = h.calls.length;
  await h.controller.setRange('unexpected');
  assert.equal(h.calls.length, callCount);
  assert.equal(h.document.nodes['7d'].attributes['aria-pressed'], 'true');
  assert.match(h.document.nodes.statsPeriod.textContent, /Latest:.*2026/);
});

test('history boundaries use the earliest of both providers; empty provider does not hide older history', async () => {
  const h = harness(async url => {
    const q = new URL(url, 'http://localhost').searchParams;
    const to = Date.parse(q.get('end'));
    const result = data(to - stats.RANGE_MS['1d'], to);
    result.earliest = url.startsWith('/api/codex') ? null : iso(ANCHOR - 2 * stats.RANGE_MS['1d']);
    return { ok: true, json: async () => result };
  });
  await h.controller.open();
  assert.equal(h.document.nodes.statsPrevious.disabled, false);
  await h.controller.previous();
  assert.equal(h.document.nodes.statsPrevious.disabled, true);
  const count = h.calls.length;
  await h.controller.previous();
  assert.equal(h.calls.length, count);
  assert.equal(h.document.nodes.statsNext.disabled, false);
});

test('a server-clamped latest ceiling updates dates and refetches both providers in sync', async () => {
  const actualCeiling = ANCHOR - 3600000;
  const h = harness(async url => {
    const q = new URL(url, 'http://localhost').searchParams;
    const to = Math.min(actualCeiling, Date.parse(q.get('end')));
    return { ok: true, json: async () => data(to - stats.RANGE_MS[q.get('range')], to) };
  });
  await h.controller.open();
  assert.equal(h.controller.snapshot().to, actualCeiling);
  assert.equal(h.controller.snapshot().ceiling, actualCeiling);
  assert.equal(h.document.nodes.statsPeriod.textContent, stats.period(actualCeiling - 86400000, actualCeiling, true));
  assert.equal(h.calls.length, 4);
  assert.equal(h.calls[2].split('?')[1], h.calls[3].split('?')[1]);
  assert.equal(new URL(h.calls[2], 'http://localhost').searchParams.get('end'), iso(actualCeiling));
});

test('known oldest boundary stays disabled while a period refresh is pending', async () => {
  let hold = false;
  const pending = [];
  const h = harness(async () => {
    if (hold) await new Promise(resolve => pending.push(resolve));
    return { ok: true, json: async () => ({ ...data(ANCHOR - 86400000, ANCHOR), earliest: iso(ANCHOR - 1000) }) };
  });
  await h.controller.open();
  assert.equal(h.document.nodes.statsPrevious.disabled, true);
  hold = true;
  const refresh = h.controller.setRange('1d');
  assert.equal(h.document.nodes.statsPrevious.disabled, true);
  await h.controller.previous();
  assert.equal(h.calls.length, 4);
  pending.forEach(resolve => resolve());
  await refresh;
});

test('paging before initial load resolves still reconciles a future historical end against server time', async () => {
  const pending = [];
  const h = harness(url => new Promise(resolve => pending.push({ url, resolve })));
  h.setTime(ANCHOR + 3 * 86400000);
  const opening = h.controller.open();
  const paging = h.controller.previous();
  assert.equal(h.controller.snapshot().to, ANCHOR + 2 * 86400000);
  function reply(index, to) {
    pending[index].resolve({ ok: true, json: async () => data(to - 86400000, to) });
  }
  reply(2, ANCHOR);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.calls.length, 6);
  assert.equal(h.controller.snapshot().to, ANCHOR);
  assert.equal(h.controller.snapshot().ceiling, ANCHOR);
  assert.equal(h.calls[4].split('?')[1], h.calls[5].split('?')[1]);
  reply(4, ANCHOR);
  reply(5, ANCHOR);
  reply(3, ANCHOR + 1000);
  reply(0, ANCHOR + 2000);
  reply(1, ANCHOR + 2000);
  await Promise.all([opening, paging]);
  assert.equal(h.controller.snapshot().to, ANCHOR, 'stale responses cannot replace the corrected ceiling');
  assert.equal(h.document.nodes.statsPeriod.textContent, stats.period(ANCHOR - 86400000, ANCHOR, true));
});

test('empty histories disable previous while a failed provider leaves history unknown', async () => {
  const h = harness(async () => ({ ok: true, json: async () => ({ ...data(ANCHOR - 86400000, ANCHOR), earliest: null }) }));
  await h.controller.open();
  assert.equal(h.document.nodes.statsPrevious.disabled, true);
  const failed = harness(async url => {
    if (url.startsWith('/api/codex')) throw new Error('<script>private details</script>');
    return { ok: true, json: async () => ({ ...data(ANCHOR - 86400000, ANCHOR), earliest: null }) };
  });
  await failed.controller.open();
  assert.equal(failed.document.nodes.statsPrevious.disabled, false);
  assert.match(failed.document.nodes.statsChart.innerHTML, /No recorded usage yet/);
  assert.match(failed.document.nodes.codexStatsChart.innerHTML, /could not be loaded/);
  assert.doesNotMatch(failed.document.nodes.codexStatsChart.innerHTML, /script|private details/);
  assert.equal(failed.document.nodes.statsLatest.disabled, false, 'Latest remains available to retry a failed current window');
});

test('one fetch may fail without hiding the other; stale requests cannot replace a navigated period', async () => {
  const pending = [];
  const h = harness(url => new Promise((resolve, reject) => pending.push({ url, resolve, reject })));
  const opening = h.controller.open();
  const navigating = h.controller.previous();
  function reply(request, label) {
    const q = new URL(request.url, 'http://localhost').searchParams;
    const to = Date.parse(q.get('end'));
    request.resolve({ ok: true, json: async () => data(to - 86400000, to, [{ label, points: [point(to - 1000, 9)] }]) });
  }
  reply(pending[2], 'Current Claude');
  pending[3].reject(new Error('offline'));
  await navigating;
  assert.match(h.document.nodes.statsChart.innerHTML, /Current Claude/);
  assert.match(h.document.nodes.codexStatsChart.innerHTML, /could not be loaded/);
  reply(pending[0], 'Obsolete Claude');
  reply(pending[1], 'Obsolete Codex');
  await opening;
  assert.doesNotMatch(h.document.nodes.statsChart.innerHTML, /Obsolete/);
  assert.doesNotMatch(h.document.nodes.codexStatsChart.innerHTML, /Obsolete/);
  assert.equal(h.document.nodes.statsChart.attributes['aria-busy'], 'false');
});

test('historic chart uses server bounds and actual sample positions without extending endpoints', () => {
  const from = ANCHOR - 86400000, to = ANCHOR;
  const input = data(from, to, [{ label: 'Session', points: [point(from + 43200000, 0), point(from + 43800000, 10)] }]);
  const first = stats.renderChart(input);
  const actualNow = Date.now;
  try {
    Date.now = () => ANCHOR + 200 * 86400000;
    assert.equal(stats.renderChart(input), first);
  } finally { Date.now = actualNow; }
  assert.match(first, /points="190.0,174.0 192.0,174.0 192.0,12.0"/);
  assert.match(first, /Session: 10 pp/);
});

test('single points, empty periods, reset boundaries and long gaps never fabricate a continuous line', () => {
  const from = ANCHOR - 86400000;
  assert.match(stats.renderChart(data(from, ANCHOR), { latest: false }), /No recorded data in this period/);
  assert.match(stats.renderChart(data(from, ANCHOR), { latest: true, provider: 'codex' }), /current account/);
  const single = stats.renderChart(data(from, ANCHOR, [{ label: 'Daily', points: [point(from + 1000, 4)] }]));
  assert.match(single, /<circle/);
  assert.match(single, /not enough continuous data/);
  assert.doesNotMatch(single, /<polyline/);
  const gaps = stats.renderChart(data(from, ANCHOR, [{ label: 'Window', points: [
    point(from, 0), point(from + 60000, 5, { breakBefore: true }),
    point(from + 120000, 1), point(from + 1200000, 8)
  ] }]));
  assert.equal((gaps.match(/<circle/g) || []).length, 4);
  assert.doesNotMatch(gaps, /<polyline/);
});

test('arbitrary reported windows get independent scales, safe labels and only finite in-period points', () => {
  const from = ANCHOR - 86400000;
  const series = [1, 10, 100].map((total, index) => ({ label: index === 0 ? '<img src=x onerror="alert(1)">' : (index + 1) + '-hour window', points: [
    point(from + 60000, 0), point(from + 120000, total), point(from - 1, 12345), point(ANCHOR + 1, 12345),
    { t: 'invalid', v: 12345 }, { t: iso(from), v: Infinity }, { t: iso(from), v: NaN }, { t: iso(from), v: '999' },
    { t: iso(from), v: -9 }
  ] }));
  const rendered = stats.renderChart(data(from, ANCHOR, series));
  assert.equal((rendered.match(/<polyline/g) || []).length, 3);
  assert.equal((rendered.match(/43.4,12.0/g) || []).length, 3, 'each independent maximum is at the top');
  assert.match(rendered, /&lt;img/);
  assert.doesNotMatch(rendered, /<img|NaN|Infinity|12345|999/);
  assert.match(rendered, /1 pp/);
  assert.match(rendered, /10 pp/);
  assert.match(rendered, /100 pp/);
  assert.match(rendered, /1\/2 max/);
  assert.match(stats.renderChart({ from: '<svg>', to: 'invalid' }), /invalid period/);
});

test('zero observed increases draw zero-valued samples without inventing missing boundaries', () => {
  const input = data(ANCHOR - 86400000, ANCHOR, [{ label: 'Weekly', points: [point(ANCHOR - 120000, 0), point(ANCHOR - 60000, 0)] }]);
  const rendered = stats.renderChart(input);
  assert.match(rendered, /Weekly: 0 pp/);
  assert.doesNotMatch(rendered, /NaN|Infinity/);
  assert.match(rendered, /points="336.6,174.0 336.8,174.0 336.8,174.0"/);
});

test('Stats adapter preserves main/back navigation and parses alongside all existing inline scripts', async () => {
  const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/index.html'), 'utf8');
  const scripts = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)].map(match => match[1]).filter(Boolean);
  scripts.forEach(source => new vm.Script(source));
  assert.ok(html.indexOf('src="usage-stats.js"') < html.indexOf('const _usageStats'));
  const source = html.slice(html.indexOf('  // ---- Stats ----'), html.indexOf('  // ---- Settings ----'));
  const document = mockDocument(), called = [];
  const controller = Object.fromEntries(['open', 'setRange', 'previous', 'next', 'latest'].map(name => [name, value => called.push([name, value])]));
  const history = { state: null, pushState(state) { this.state = state; called.push(['push']); }, replaceState(state) { this.state = state; called.push(['replace']); } };
  const context = { document, history, UsageStats: { createController: () => controller }, fetch: () => {},
    _hideAllViews: () => called.push(['hide']), _checkToolbarOverflow: () => called.push(['toolbar']) };
  context.window = context;
  vm.runInNewContext(source, context);
  context.openStats();
  assert.equal(history.state.view, 'stats');
  assert.equal(called[0][0], 'push');
  context.openStats();
  assert.ok(called.some(call => call[0] === 'replace'));
  context.setStatsRange('7d'); context.previousStatsWindow(); context.nextStatsWindow(); context.latestStatsWindow();
  assert.ok(called.some(call => call[0] === 'setRange' && call[1] === '7d'));
  context.closeStats();
  assert.equal(document.nodes.statsView.classList.contains('hidden'), true);
  assert.equal(document.nodes.mainView.classList.contains('hidden'), false);
  assert.ok(called.some(call => call[0] === 'toolbar'));
  assert.match(html, /id="statsView"[\s\S]*?onclick="history.back\(\)"/);
  delete context.UsageStats;
  const noModuleContext = { ...context, window: null };
  noModuleContext.window = noModuleContext;
  vm.runInNewContext(source, noModuleContext);
  noModuleContext.openStats();
  assert.match(document.nodes.statsChart.textContent, /Statistics could not load/);
  noModuleContext.closeStats();
});
