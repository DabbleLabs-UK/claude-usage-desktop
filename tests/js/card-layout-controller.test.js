'use strict';

const assert = require('node:assert/strict');
const layout = require('../../wwwroot/card-layout.js');

// Minimal DOM for controller behavior. Removing focused descendants clears focus,
// as replacing live browser nodes does; tests must restore it through the controller.
class Node {
  constructor(tag = 'div', dataset = {}, classes = []) {
    this.tag = tag;
    this.dataset = dataset;
    this.children = [];
    this.parentNode = null;
    this.listeners = {};
    this.attributes = {};
    this.style = {};
    this.hidden = false;
    this.disabled = false;
    this.textContent = '';
    const names = new Set(classes);
    this.classList = {
      contains: name => names.has(name),
      add: name => names.add(name),
      remove: name => names.delete(name),
      toggle(name, on) { on = on === undefined ? !names.has(name) : on; if (on) names.add(name); else names.delete(name); return on; }
    };
  }
  matches(selector) {
    if (selector.startsWith('.')) return this.classList.contains(selector.slice(1));
    const match = selector.match(/^\[data-([a-z-]+)(?:="([^"]*)")?\]$/);
    if (match) {
      const key = match[1].replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
      return this.dataset[key] !== undefined && (match[2] === undefined || this.dataset[key] === match[2]);
    }
    return this.tag === selector;
  }
  closest(selector) { return this.matches(selector) ? this : this.parentNode ? this.parentNode.closest(selector) : null; }
  querySelector(selector) {
    for (const node of this.children) {
      if (node.matches(selector)) return node;
      const found = node.querySelector(selector);
      if (found) return found;
    }
    return null;
  }
  contains(node) { return node === this || this.children.some(child => child.contains(node)); }
  detach(node) {
    if (node.contains(document.activeElement)) document.activeElement = document.body;
    this.children.splice(this.children.indexOf(node), 1);
    node.parentNode = null;
  }
  appendChild(node) {
    if (node.parentNode) node.parentNode.detach(node);
    node.parentNode = this;
    this.children.push(node);
    return node;
  }
  replaceChildren() { for (const child of this.children.slice()) this.detach(child); }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, handler) { (this.listeners[name] ||= []).push(handler); }
  emit(name, target = this, extra = {}) {
    const event = Object.assign({ target, prevented: false, stopped: false,
      preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }
    }, extra);
    for (const listener of this.listeners[name] || []) listener(event);
    return event;
  }
  focus() { if (!this.disabled) document.activeElement = this; }
  scrollIntoView() { this.scrolled = true; }
}

function card(id) {
  const node = new Node('article', { layoutId: id });
  const controls = node.appendChild(new Node('div', {}, ['position-controls']));
  controls.appendChild(new Node('button', {}, ['drag-handle']));
  controls.appendChild(new Node('button', { move: 'up' }));
  controls.appendChild(new Node('button', { move: 'down' }));
  return node;
}

const settle = () => new Promise(resolve => setImmediate(resolve));
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }

function setup(saved) {
  const body = new Node('body');
  global.document = { body, activeElement: body, querySelector: selector => body.querySelector(selector) };
  global.window = new Node('window');
  const toolbar = body.appendChild(new Node('div', {}, ['toolbar', 'toolbar-hidden']));
  const toggle = toolbar.appendChild(new Node('button'));
  toggle.appendChild(new Node('svg'));
  toggle.appendChild(new Node('span'));
  const board = body.appendChild(new Node('main'));
  const status = new Node();
  const posts = [];
  let respond = () => Promise.resolve({ ok: true });
  let gets = 0;
  global.fetch = async (url, options) => {
    assert.equal(url, '/api/card-order');
    if (!options) { gets++; return { ok: true, json: async () => saved }; }
    assert.equal(options.method, 'POST');
    assert.equal(options.headers['Content-Type'], 'application/json');
    const order = JSON.parse(options.body).order;
    posts.push(order);
    return respond(order);
  };
  const controller = layout.attach(board, toggle, status);
  return {
    board, toggle, status, controller, posts,
    respond: callback => { respond = callback; },
    gets: () => gets,
    order: () => board.children.filter(node => node.dataset.layoutId).map(node => node.dataset.layoutId),
    current: id => board.children.find(node => node.dataset.layoutId === id),
    update(source, ids) { const container = new Node(); for (const id of ids) container.appendChild(card(id)); controller.update(source, container); },
    arrow(id, direction) { const target = this.current(id).querySelector('[data-move="' + direction + '"]'); target.focus(); return board.emit('click', target); }
  };
}

let failures = 0;
async function test(name, fn) {
  try { await fn(); console.log('  ok - ' + name); }
  catch (error) { failures++; console.error('FAIL - ' + name + '\n' + error.stack); }
}

(async () => {
  await test('restores cross-source order and keeps focus when live cards are replaced', async () => {
    const env = setup(['codex:credits', 'claude:week', 'codex:week']);
    env.update('claude', ['claude:week']);
    env.update('codex', ['codex:week', 'codex:credits']);
    await settle();
    assert.deepEqual(env.order(), ['codex:credits', 'claude:week', 'codex:week']);
    env.toggle.emit('click');
    const old = env.current('claude:week').querySelector('[data-move="up"]');
    old.focus();
    env.update('claude', ['claude:week']);
    assert.notEqual(document.activeElement, old);
    assert.equal(document.activeElement, env.current('claude:week').querySelector('[data-move="up"]'));
    assert.deepEqual(env.order(), ['codex:credits', 'claude:week', 'codex:week']);
    env.current('codex:credits').querySelector('.drag-handle').focus();
    env.update('codex', ['codex:week', 'codex:credits']);
    assert.equal(document.activeElement, env.current('codex:credits').querySelector('.drag-handle'));
  });

  await test('arrow moves update boundaries, focus and persisted positions', async () => {
    const env = setup(['a', 'b', 'c']);
    env.update('claude', ['a', 'b']); env.update('codex', ['c']); await settle();
    assert.equal(env.current('a').querySelector('.position-controls').hidden, true);
    env.arrow('a', 'down');
    assert.equal(env.posts.length, 0);
    env.toggle.emit('click');
    assert.equal(env.current('a').querySelector('.position-controls').hidden, false);
    assert.equal(document.querySelector('.toolbar').classList.contains('toolbar-hidden'), false);
    assert.equal(env.current('a').querySelector('[data-move="up"]').disabled, true);
    env.arrow('b', 'up'); await settle();
    assert.deepEqual(env.order(), ['b', 'a', 'c']);
    assert.deepEqual(env.posts, [['b', 'a', 'c']]);
    assert.equal(document.activeElement, env.current('b').querySelector('.drag-handle'));
    assert.equal(env.current('b').scrolled, true);
    assert.equal(env.status.textContent, 'Positions saved');
    env.toggle.emit('click');
    assert.equal(env.toggle.attributes['aria-pressed'], 'false');
  });

  await test('moves during an in-flight save persist the newest order afterward', async () => {
    const env = setup(['a', 'b', 'c']); env.update('claude', ['a', 'b', 'c']); await settle();
    const first = deferred(), second = deferred();
    env.respond(() => env.posts.length === 1 ? first.promise : second.promise);
    env.toggle.emit('click'); env.arrow('a', 'down'); env.arrow('a', 'down');
    assert.deepEqual(env.order(), ['b', 'c', 'a']);
    assert.deepEqual(env.posts, [['b', 'a', 'c']]);
    first.resolve({ ok: true }); await settle();
    assert.deepEqual(env.posts, [['b', 'a', 'c'], ['b', 'c', 'a']]);
    second.resolve({ ok: true }); await settle();
    assert.equal(env.status.textContent, 'Positions saved');
    env.toggle.emit('click'); assert.equal(env.toggle.attributes['aria-pressed'], 'false');
  });

  await test('failed saves retain the layout and Done retries before leaving edit mode', async () => {
    const env = setup(['a', 'b']); env.update('claude', ['a', 'b']); await settle();
    env.respond(async () => ({ ok: env.posts.length > 1 }));
    env.toggle.emit('click'); env.arrow('b', 'up'); await settle();
    assert.equal(env.status.textContent, 'Positions not saved. Press Done to retry.');
    assert.deepEqual(env.order(), ['b', 'a']);
    env.toggle.emit('click'); await settle();
    assert.deepEqual(env.posts, [['b', 'a'], ['b', 'a']]);
    assert.equal(env.status.textContent, 'Positions saved');
    env.toggle.emit('click'); assert.equal(env.toggle.attributes['aria-pressed'], 'false');
  });

  await test('drag handles reorder cards and defer live refresh until drop', async () => {
    const env = setup(['a', 'b', 'c']); env.update('claude', ['a', 'b']); env.update('codex', ['c']); await settle();
    const dataTransfer = { setData(type, value) { this.type = type; this.value = value; } };
    assert.equal(env.board.emit('dragstart', env.current('c'), { dataTransfer }).prevented, true);
    env.toggle.emit('click');
    assert.equal(env.board.emit('dragstart', env.current('c'), { dataTransfer }).prevented, true);
    const old = env.current('c');
    env.board.emit('dragstart', old.querySelector('.drag-handle'), { dataTransfer });
    assert.equal(dataTransfer.value, 'c');
    env.update('codex', ['c']);
    assert.equal(env.current('c'), old);
    assert.equal(env.board.emit('dragover', env.current('a'), { dataTransfer }).prevented, true);
    env.board.emit('drop', env.current('a'), { dataTransfer }); await settle();
    assert.deepEqual(env.order(), ['c', 'a', 'b']);
    assert.notEqual(env.current('c'), old);
    assert.deepEqual(env.posts, [['c', 'a', 'b']]);
    env.board.emit('dragend');
    assert.deepEqual(env.order(), ['c', 'a', 'b']);
  });

  await test('temporarily absent sources regain their saved positions after other cards move', async () => {
    const env = setup(['claude:week', 'codex:credits', 'claude:five', 'codex:week']);
    env.update('claude', ['claude:five', 'claude:week']); env.update('codex', ['codex:week', 'codex:credits']); await settle();
    env.update('codex', []);
    assert.deepEqual(env.order(), ['claude:week', 'claude:five']);
    env.toggle.emit('click'); env.arrow('claude:five', 'up'); await settle();
    assert.deepEqual(env.posts[0], ['claude:five', 'claude:week', 'codex:credits', 'codex:week']);
    env.update('codex', ['codex:week', 'codex:credits']);
    assert.deepEqual(env.order(), ['claude:five', 'claude:week', 'codex:credits', 'codex:week']);
  });

  await test('focus moves to the handle when a source change disables the focused arrow', async () => {
    const env = setup(['a', 'b']); env.update('claude', ['a']); env.update('codex', ['b']); await settle();
    env.toggle.emit('click'); env.current('b').querySelector('[data-move="up"]').focus();
    env.update('claude', []);
    assert.equal(document.activeElement, env.current('b').querySelector('.drag-handle'));
  });

  delete global.document; delete global.window; delete global.fetch;
  if (failures) process.exitCode = 1;
  else console.log('All card layout controller tests passed.');
})();
