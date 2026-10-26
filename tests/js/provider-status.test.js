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
      setAttribute(name, value) { this[name] = value; },
      click() { if (this.listeners.click) return this.listeners.click(); },
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
    Codex: { live: 'Live', noToken: 'Not signed in', signedOut: 'Access unavailable', noNetwork: 'Unreachable', error: 'Unavailable' }
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

test('Codex uses only typed reasons to distinguish local expiry and endpoint access rejection', () => {
  for (const [reason, httpStatusCode] of [['usageUnauthorized', 401], ['usageForbidden', 403]]) {
    const model = status.derive('Codex', input('signedOut', null, { reason, httpStatusCode }));
    assert.equal(model.label, 'Access unavailable');
    assert.equal(model.explanation, 'Usage endpoint rejected access (HTTP ' + httpStatusCode + '). CU cannot determine whether this is a sign-in, account entitlement or temporary access problem.');
    assert.doesNotMatch(model.explanation, /sign in again|subscription expired/);
  }
  const expired = status.derive('Codex', input('signedOut', null, { reason: 'localTokenExpired' }));
  assert.equal(expired.label, 'Sign-in needed');
  assert.match(expired.explanation, /CU reads the token without changing it; Codex owns sign-in refresh/);
  const generic = status.derive('Codex', input('signedOut', null, { detail: 'local token expired; HTTP 401' }));
  assert.equal(generic.label, 'Access unavailable');
  assert.doesNotMatch(generic.explanation, /HTTP 401|token.*expired/);
  const other = status.derive('Codex', input('error', null, { httpStatusCode: 502 }));
  assert.equal(other.label, 'Unavailable');
  assert.match(other.explanation, /HTTP 502/);
  assert.equal(status.derive('Codex', input('noToken', null, { reason: 'noLocalCredentials' })).label, 'Not signed in');
});

test('every nonlive Codex status explains schedule and session-only freshness', () => {
  for (const state of ['noToken', 'signedOut', 'noNetwork', 'error', null]) {
    const model = status.derive('Codex', input(state, null, { checkedAt: '2026-10-26T13:39:04Z', nextAttemptAt: '2026-10-26T13:42:04Z' }));
    assert.match(model.automatic, /automatically about every 3 minutes/);
    if (state) {
      assert.match(model.automatic, /Last check:/);
      assert.match(model.automatic, /Next check expected around/);
    }
    assert.equal(model.freshness, 'No successful reading in this app session. Saved history may still be available in Statistics.');
    assert.equal(model.retained, '');
  }
  assert.equal(live('Codex').automatic, '');
  assert.match(status.derive('Codex', input('signedOut')).retained, /Last-known values remain available/);
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
  const match = html.match(new RegExp('  (?:async )?function ' + name + '\\([^]*?\\n  \\}'));
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

test('refresh acknowledges pending, success and failure independently while preserving disclosure and focus', async () => {
  const document = mockDocument();
  const container = document.createElement('div');
  const pending = {};
  const calls = [];
  const view = status.attach(container, { refresh: provider => {
    calls.push(provider);
    return new Promise((resolve, reject) => { pending[provider] = { resolve, reject }; });
  } });
  const [claude, codex] = container.children;
  const claudeButton = controls(claude).children[0];
  const codexButton = controls(codex).children[0];
  const feedback = entry => body(entry).children[7];
  claude.open = true;
  claudeButton.focus();
  const checkingClaude = claudeButton.click();
  assert.equal(claudeButton.disabled, true);
  assert.equal(claudeButton.textContent, 'Checking...');
  assert.match(feedback(claude).textContent, /Checking cached Claude status/);
  assert.equal(feedback(claude).role, 'status');
  assert.equal(feedback(claude)['aria-live'], 'polite');
  await claudeButton.click();
  assert.deepEqual(calls, ['Claude']);
  const checkingCodex = codexButton.click();
  assert.deepEqual(calls, ['Claude', 'Codex']);
  view.update([live('Claude'), live('Codex')]);
  assert.equal(claude.open, true);
  assert.equal(document.activeElement, claudeButton);
  assert.equal(claudeButton.textContent, 'Checking...');
  pending.Claude.resolve({ ok: true, message: 'Checked cached Claude status. No fresh provider request was made.' });
  await checkingClaude;
  assert.equal(claudeButton.disabled, false);
  assert.equal(claudeButton.textContent, 'Refresh status');
  assert.match(feedback(claude).textContent, /Checked cached Claude status. No fresh provider request/);
  pending.Codex.resolve({ ok: false, message: 'Partially checked cached Codex status; one local response failed.' });
  await checkingCodex;
  assert.match(feedback(codex).textContent, /Partially checked/);
  const rejected = codexButton.click();
  pending.Codex.reject(new Error('raw diagnostic should not be shown'));
  await rejected;
  assert.match(feedback(codex).textContent, /Could not check cached Codex status/);
  assert.doesNotMatch(feedback(codex).textContent, /raw diagnostic/);
  assert.equal(codexButton.disabled, false);
});

function refreshIntegration(fetch) {
  const { context } = integration();
  const timers = [];
  context.AbortController = AbortController;
  context.fetch = fetch;
  context.setTimeout = (callback, delay) => { const timer = { callback, delay, cleared: false }; timers.push(timer); return timer; };
  context.clearTimeout = timer => { timer.cleared = true; };
  context.contacts = 0;
  context._markServerContact = () => { context.contacts++; };
  vm.runInContext(source('renderCodex'), context);
  vm.runInContext(source('refreshProviderStatus'), context);
  return { context, timers };
}

test('refresh adapter checks only selected local cached endpoints and accepts null Claude backoff', async () => {
  for (const provider of ['Claude', 'Codex']) {
    const calls = [];
    const { context, timers } = refreshIntegration(async (url, options) => {
      calls.push({ url, options });
      return { ok: true, status: 200, json: async () => url.endsWith('/usage')
        ? provider === 'Claude' ? sample : { ...sample, windows: [{ usedPercent: 15, limitWindowSeconds: 18000 }] }
        : provider === 'Claude' ? null : { state: 'live' } };
    });
    context._backoffState = { state: 'sourcesUnavailable' };
    const result = await context.refreshProviderStatus(provider);
    assert.equal(result.ok, true);
    assert.match(result.message, /Checked cached.*No fresh provider request was made/);
    assert.deepEqual(calls.map(call => call.url), provider === 'Claude' ? ['/api/backoff', '/api/usage'] : ['/api/codex/status', '/api/codex/usage']);
    for (const call of calls) {
      assert.deepEqual(Object.keys(call.options), ['signal']);
      assert.equal(call.options.signal.aborted, false);
    }
    assert.equal(context.contacts, 2);
    assert.equal(timers[0].delay, 8000);
    assert.equal(timers[0].cleared, true);
    if (provider === 'Claude') assert.equal(context._backoffState, null);
    else assert.equal(context._backoffState.state, 'sourcesUnavailable');
  }
});

test('refresh adapter applies independent successes, distinguishes absent snapshots and retains usage', async () => {
  const { context } = refreshIntegration(async url => {
    if (url.endsWith('/status')) return { ok: true, status: 200, json: async () => ({ state: 'signedOut', reason: 'usageUnauthorized' }) };
    throw new Error('network failed');
  });
  const before = context._codexData;
  let result = await context.refreshProviderStatus('Codex');
  assert.equal(result.ok, false);
  assert.match(result.message, /Partially checked cached Codex status/);
  assert.equal(context._codexStatus.reason, 'usageUnauthorized');
  assert.equal(context._codexData, before);
  assert.equal(context.contacts, 1);
  context.fetch = async url => url.endsWith('/status')
    ? { ok: true, status: 200, json: async () => ({ state: 'signedOut', reason: 'usageUnauthorized' }) }
    : { ok: false, status: 404 };
  result = await context.refreshProviderStatus('Codex');
  assert.equal(result.ok, true);
  assert.match(result.message, /No snapshot.*existing values were kept/);
  assert.equal(context._codexData, before);
  assert.equal(context.contacts, 3); // An absent cached snapshot still proves local contact.
  context.fetch = async () => ({ ok: false, status: 404 });
  result = await context.refreshProviderStatus('Codex');
  assert.equal(result.ok, false); // A missing status route is a failure, not an absent usage snapshot.
  assert.match(result.message, /Partially checked cached Codex status/);
  assert.match(result.message, /No snapshot/);
  assert.equal(context._codexData, before);
  assert.equal(context.contacts, 5);
  context.fetch = async () => ({ ok: false, status: 503 });
  result = await context.refreshProviderStatus('Codex');
  assert.equal(result.ok, false);
  assert.match(result.message, /Could not check cached Codex status/);
  assert.doesNotMatch(result.message, /No snapshot/);
  context.fetch = async () => ({ ok: true, status: 200, json: async () => { throw new Error('malformed JSON'); } });
  result = await context.refreshProviderStatus('Codex');
  assert.equal(result.ok, false);
  assert.equal(context._codexData, before);
});

test('refresh adapter retains each failed status while applying its successful usage response', async () => {
  for (const provider of ['Claude', 'Codex']) {
    const usage = provider === 'Claude' ? sample : { ...sample, windows: [] };
    const { context } = refreshIntegration(async url => ({
      ok: url.endsWith('/usage'), status: url.endsWith('/usage') ? 200 : 500,
      json: async () => usage
    }));
    context._backoffState = { state: 'authError' };
    context._codexStatus = { state: 'error' };
    const result = await context.refreshProviderStatus(provider);
    assert.equal(result.ok, false);
    assert.match(result.message, /Partially checked/);
    assert.equal(context._backoffState.state, 'authError');
    assert.equal(context._codexStatus.state, 'error');
    assert.equal(provider === 'Claude' ? context.lastData : context._codexData, usage);
    assert.equal(context.contacts, 1);
  }
});

test('refresh adapter times out and aborts both local reads without changing watchdog recovery', async () => {
  const signals = [];
  const { context, timers } = refreshIntegration((url, options) => {
    signals.push(options.signal);
    return new Promise(() => {});
  });
  const checking = context.refreshProviderStatus('Codex');
  timers[0].callback();
  const result = await checking;
  assert.equal(result.ok, false);
  assert.match(result.message, /Could not check cached Codex status/);
  assert.equal(signals.length, 2);
  assert.equal(signals.every(signal => signal.aborted), true);
  assert.equal(timers[0].cleared, true);
  assert.equal(context.contacts, 0);
  assert.doesNotMatch(source('refreshProviderStatus'), /forceRecovery|fetchInstanceId|_recoveryFailStreak|POST|usage\/retry/);
  assert.match(source('renderProviderStatus'), /refresh: provider => refreshProviderStatus\(provider\)/);
  assert.match(source('_refreshUpdatedEl'), /'Claude updated '/);
  assert.match(html, /id="staleEl">Claude stale<\/span>/);
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
