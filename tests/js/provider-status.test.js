'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const status = require('../../wwwroot/provider-status.js');
const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/index.html'), 'utf8');
const sample = Object.freeze({ windows: Object.freeze({ five_hour: { utilization: 15 } }), fetchedAt: '2026-10-17T10:00:00Z', source: 'Dell' });
const input = (state, data = sample, extra = {}) => ({ status: state ? { state, ...extra } : null, data });
const live = provider => status.derive(provider, input('live'));

function mockDocument() {
  const document = { activeElement: null };
  document.createElement = tag => {
    let text = '';
    const node = {
      tagName: tag, ownerDocument: document, children: [], hidden: false, open: false,
      writes: 0, listeners: {},
      append(...nodes) { this.children.push(...nodes); },
      replaceChildren(...nodes) { this.children = nodes; },
      addEventListener(name, callback) { this.listeners[name] = callback; },
      click() { if (this.listeners.click) this.listeners.click(); },
      focus() { document.activeElement = this; },
      classList: {
        values: new Set(),
        toggle(name, value) { value ? this.values.add(name) : this.values.delete(name); },
        add(name) { this.values.add(name); }, remove(name) { this.values.delete(name); },
        contains(name) { return this.values.has(name); }
      },
      get textContent() { return text; },
      set textContent(value) { this.writes++; text = value; },
      set innerHTML(value) {
        if (!this.allowHTML) throw new Error('Unsafe HTML assignment: ' + value);
        this.markup = value;
      }
    };
    return node;
  };
  return document;
}
const body = entry => entry.children[1];
const controls = entry => body(entry).children[6];

test('all typed provider states map conservatively, independent of diagnostics or failure count', () => {
  const states = {
    Claude: { live: 'Live', noNetwork: 'Unreachable', rateLimited: 'Waiting', authError: 'Unavailable', signedOut: 'Sign-in needed', sourcesUnavailable: 'Unavailable' },
    Codex: { live: 'Live', noToken: 'Not signed in', signedOut: 'Sign-in/access needed', noNetwork: 'Unreachable', error: 'Unavailable' }
  };
  for (const [provider, cases] of Object.entries(states)) {
    for (const [state, label] of Object.entries(cases)) {
      const model = status.derive(provider, input(state, sample, { consecutiveFailures: 999, detail: 'expired subscription; 403; malformed response' }));
      assert.equal(model.label, label, provider + ' ' + state);
      assert.doesNotMatch(model.label + model.explanation, /expired|no subscription|offline|fully lapsed/i);
      assert.equal(model.diagnostic, 'expired subscription; 403; malformed response');
    }
  }
});

test('unknown, stale and empty successful readings remain distinct without changing data', () => {
  for (const provider of ['Claude', 'Codex']) {
    assert.equal(status.derive(provider, {}).label, 'Checking');
    assert.equal(status.derive(provider, input('live', null)).label, 'Checking');
    assert.equal(status.derive(provider, input('futureState')).label, 'Unavailable');
    assert.equal(status.derive(provider, input('live', { ...sample, isStale: true })).label, 'Stale');
    const empty = status.derive(provider, input('live', { ...sample, windows: provider === 'Claude' ? { five_hour: null } : [] }));
    assert.equal(empty.label, 'No quota reported');
    assert.match(empty.explanation, /does not establish subscription/);
  }
  assert.equal(status.derive('Claude', input(null)).label, 'Live'); // Null backoff is the successful Claude contract.
  assert.equal(status.derive('Claude', input(null, { ...sample, isStale: true })).label, 'Stale');
  assert.equal(status.derive('Codex', input(null)).label, 'Checking');
  const stale = status.derive('Claude', input('sourcesUnavailable', { ...sample, isStale: true }, { nextAttemptAt: '2026-10-17T10:10:00Z' }));
  assert.match(stale.freshness, /Last successful reading:.*Dell/);
  assert.match(stale.retained, /Saved history is still available/);
  assert.match(stale.automatic, /Next automatic attempt scheduled/);
  assert.equal(sample.windows.five_hour.utilization, 15);
});

test('both providers use compact collapsed disclosures, with symmetric failure/live behavior', () => {
  const document = mockDocument();
  const container = document.createElement('div');
  let retries = 0, refreshes = 0;
  const view = status.attach(container, { retry: () => retries++, refresh: () => refreshes++ });
  const [claude, codex] = container.children;
  assert.equal(claude.tagName, 'details');
  assert.equal(codex.tagName, 'details');
  assert.equal(claude.open, false);
  assert.equal(codex.open, false);
  const broken = status.derive('Claude', input('sourcesUnavailable', sample, { detail: '<img src=x onerror=alert(1)>' }));
  view.update([broken, live('Codex')]);
  assert.equal(claude.children[0].textContent, 'Claude: Unavailable');
  assert.equal(codex.children[0].textContent, 'Codex: Live');
  assert.equal(body(claude).children[3].textContent, '<img src=x onerror=alert(1)>');
  assert.equal(controls(claude).children[1].hidden, false);
  assert.equal(retries, 0);
  assert.equal(refreshes, 0);
  assert.equal(container.classList.contains('hidden'), false);
  view.update([live('Claude'), status.derive('Codex', input('error'))]);
  assert.equal(claude.children[0].textContent, 'Claude: Live');
  assert.equal(codex.children[0].textContent, 'Codex: Unavailable');
  assert.equal(controls(claude).children[1].hidden, true);
  assert.equal(controls(codex).children.length, 1); // No invented Codex provider retry.
  controls(codex).children[0].click();
  assert.equal(refreshes, 1);
  view.update([broken, live('Codex')]);
  controls(claude).children[1].click();
  assert.equal(retries, 1);
});

test('updates retain expanded state, focused controls and unchanged diagnostic text nodes', () => {
  const document = mockDocument();
  const container = document.createElement('div');
  const view = status.attach(container);
  const model = status.derive('Claude', input('sourcesUnavailable', sample, { detail: 'Relay unreachable' }));
  view.update([model, live('Codex')]);
  const [claude] = container.children;
  const diagnostic = body(claude).children[3];
  const retry = controls(claude).children[1];
  claude.open = true;
  retry.focus();
  retry.disabled = true;
  retry.textContent = 'Retry requested';
  const writes = diagnostic.writes;
  for (let i = 0; i < 60; i++) view.update([model, live('Codex')]);
  assert.equal(container.children[0], claude);
  assert.equal(claude.open, true);
  assert.equal(document.activeElement, retry);
  assert.equal(retry.disabled, true);
  assert.equal(retry.textContent, 'Retry requested');
  assert.equal(diagnostic.writes, writes);
  view.update([live('Claude'), live('Codex')]);
  assert.equal(claude.open, true);
  assert.equal(container.classList.contains('hidden'), false);
  view.update([model, live('Codex')], true);
  assert.equal(container.classList.contains('hidden'), true); // Watchdog takes precedence even if open.
  assert.equal(claude.open, true);
  claude.open = false;
  view.update([live('Claude'), live('Codex')]);
  assert.equal(container.classList.contains('hidden'), true);
  claude.open = true;
  claude.listeners.toggle();
  assert.equal(container.classList.contains('hidden'), false);
  claude.open = false;
  claude.listeners.toggle();
  assert.equal(container.classList.contains('hidden'), true);
});

function source(name) {
  const match = html.match(new RegExp('  function ' + name + '\\([^]*?\\n  \\}'));
  assert.ok(match, 'Missing function: ' + name);
  return match[0];
}

function integration(moduleAvailable = true) {
  const document = mockDocument();
  const nodes = Object.fromEntries(['backoffBanner', 'mainView', 'claudeCol', 'codexCol', 'backendGoneBanner', 'backendGoneElapsed'].map(id => [id, document.createElement('div')]));
  let loading = false;
  for (const node of Object.values(nodes)) { node.querySelector = () => loading; node.allowHTML = true; }
  document.getElementById = id => nodes[id];
  const context = vm.createContext({ document, window: {}, Date,
    _providerStatusView: null, _uiConnLost: false, _backendGone: false,
    _backoffState: null, _lastGoodData: null, lastData: null, _codexData: { ...sample, windows: [{ usedPercent: 15, limitWindowSeconds: 18000 }] },
    _codexStatus: { state: 'live' }, _autoRefreshLogin: true, _lastFetchedAt: Date.now(),
    _lastServerContactAt: Date.now(), _setReconnecting() {}, _refreshReconnectingText() {},
    renderCalls: [], render(data) { context.lastData = data; context.renderCalls.push(data); loading = false; context.renderProviderStatus(); },
    dashboardLayout: { update() {} }, _codexCardDataMap: {}, _openCardKey: null,
    codexWindowName() { return { label: '5-hour', compact: '5h' }; }, card() { return 'Usage'; },
    dashboardCard(id, provider, content, stale) { return provider + ':' + content + ':' + stale; }, CreditsCard: { render() { return ''; } },
    _markServerContact() {},
    forceRecovery() { throw new Error('Presentation must not fetch automatically'); }
  });
  if (moduleAvailable) context.ProviderStatus = status;
  for (const name of ['renderProviderStatus', 'updateBackoffBanner', '_setUiConnLost', '_setBackendGone', '_refreshBackendGoneBanner', '_fmtElapsedSince']) {
    vm.runInContext(source(name), context);
  }
  return { context, nodes, setLoading(value) { loading = value; } };
}

test('integration preserves fallback cards, independent watchdog warning and Codex no-token status', () => {
  const { context, nodes, setLoading } = integration();
  context._backoffState = { state: 'sourcesUnavailable' };
  context._lastGoodData = sample;
  setLoading(true);
  context.updateBackoffBanner();
  assert.deepEqual(context.renderCalls, [sample]);
  assert.equal(nodes.mainView.classList.contains('stale-mode'), true);
  assert.equal(nodes.codexCol.classList.contains('stale-mode'), false);
  assert.equal(nodes.backoffBanner.children[1].children[0].textContent, 'Codex: Live');
  context._setUiConnLost(true);
  assert.equal(nodes.backoffBanner.classList.contains('hidden'), true);
  context.updateBackoffBanner();
  assert.equal(nodes.backoffBanner.classList.contains('hidden'), true);
  context._setBackendGone(true);
  assert.equal(nodes.backendGoneBanner.classList.contains('hidden'), false);
  assert.equal(nodes.mainView.classList.contains('backend-gone'), true);
  context._setBackendGone(false);
  context._setUiConnLost(false);
  assert.equal(nodes.backendGoneBanner.classList.contains('hidden'), true);
  context._backoffState = null;
  context.updateBackoffBanner();
  assert.equal(nodes.mainView.classList.contains('stale-mode'), false);
  context._codexStatus = { state: 'noToken' };
  vm.runInContext(source('renderCodex'), context);
  context.renderCodex();
  assert.equal(nodes.backoffBanner.children[1].children[0].textContent, 'Codex: Not signed in');
  assert.match(nodes.codexCol.markup, /Codex:Usage:true/);
  assert.equal(nodes.codexCol.classList.contains('hidden'), false);
  context._codexData = null;
  context.renderCodex();
  assert.equal(nodes.codexCol.classList.contains('hidden'), true);
});

test('actual push handlers update both provider presentations and empty Claude loading clears safely', () => {
  const { context, nodes, setLoading } = integration();
  const handlers = {};
  context.conn = { on(name, callback) { handlers[name] = callback; } };
  vm.runInContext(source('renderCodex'), context);
  for (const event of ['backoffUpdated', 'usageUpdated', 'codexUsageUpdated', 'codexStatusUpdated']) {
    const match = html.match(new RegExp("  conn.on\\('" + event + "'[^]*?\\}\\);"));
    assert.ok(match, event);
    vm.runInContext(match[0], context);
  }
  setLoading(true);
  handlers.backoffUpdated({ state: 'authError' });
  assert.equal(nodes.claudeCol.markup, '');
  assert.equal(nodes.codexCol.markup, undefined);
  assert.equal(nodes.backoffBanner.children[0].children[0].textContent, 'Claude: Unavailable');
  handlers.usageUpdated({ ...sample, isStale: true });
  handlers.backoffUpdated(null);
  assert.equal(nodes.backoffBanner.children[0].children[0].textContent, 'Claude: Stale');
  handlers.usageUpdated(sample);
  assert.equal(nodes.backoffBanner.children[0].children[0].textContent, 'Claude: Live');
  handlers.codexStatusUpdated({ state: 'error' });
  assert.equal(nodes.backoffBanner.children[1].children[0].textContent, 'Codex: Unavailable');
  handlers.codexUsageUpdated({ ...sample, windows: [], isStale: false });
  handlers.codexStatusUpdated({ state: 'live' });
  assert.equal(nodes.backoffBanner.children[1].children[0].textContent, 'Codex: No quota reported');
});

test('missing presentation module degrades safely; Stats keeps history and hides dashboard-only metadata', () => {
  const { context, nodes } = integration(false);
  context.renderProviderStatus();
  context.updateBackoffBanner();
  assert.equal(nodes.backoffBanner.classList.contains('hidden'), false);
  context._uiConnLost = true;
  context.renderProviderStatus();
  assert.equal(nodes.backoffBanner.classList.contains('hidden'), true);
  assert.match(html, /<main id="mainView">\s*<div id="usageInfo"/);
  assert.match(html, /<details class="chart-note">\s*<summary>About these graphs<\/summary>/);
  assert.match(html, /Gaps while the app is off or samples are missing are unknown/);
  assert.match(html, /a weekly line requires the account to report a weekly window/);
  assert.match(html, /#statsView:not\(\.hidden\) ~ \.toolbar #updatedEl,\s*#statsView:not\(\.hidden\) ~ \.toolbar #staleEl \{ display: none; \}/);
  assert.match(html, /\.backoff-banner \{[^}]*max-height: 40dvh;[^}]*overflow-y: auto;/);
  assert.match(html, /@media \(max-width: 320px\) \{\s*\.backoff-banner \{ grid-template-columns: minmax\(0, 1fr\); \}/);
  assert.doesNotMatch(source('renderProviderStatus') + source('updateBackoffBanner'), /statsChart|codexStatsChart|statsController|fetch\(/);
  for (const script of html.matchAll(/<script\b[^>]*>([^]*?)<\/script>/gi)) if (script[1].trim()) new vm.Script(script[1]);
});
