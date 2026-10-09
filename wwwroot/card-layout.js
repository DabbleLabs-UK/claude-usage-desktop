(function (root) {
  'use strict';
  function normalize(order) {
    return Array.isArray(order) ? [...new Set(order.filter(id => typeof id === 'string' && /^[a-zA-Z0-9:_-]{1,160}$/.test(id)))].slice(0, 200) : [];
  }
  function resolve(order, available) {
    const saved = normalize(order);
    return saved.filter(id => available.includes(id)).concat(available.filter(id => !saved.includes(id)));
  }
  // Preserve positions for absent cards when a source temporarily disappears.
  function move(order, available, id, target) {
    const saved = normalize(order);
    const full = saved.concat(available.filter(key => !saved.includes(key)));
    if (id === target || !available.includes(id) || !available.includes(target)) return full;
    const from = full.indexOf(id), to = full.indexOf(target);
    full.splice(from, 1); full.splice(to, 0, id);
    return full;
  }
  function attach(board, toggle, status) {
    let order = [], editing = false, dragging = null, dirty = false, saving = false, loaded = false;
    const sources = new Map([['claude', []], ['codex', []], ['launcher', []]]);
    const cards = () => [...sources.values()].flatMap(nodes => nodes.filter(n => n.dataset.layoutId));
    const ids = () => cards().map(n => n.dataset.layoutId);
    const report = text => { status.textContent = text; };
    function render() {
      if (dragging) return;
      const active = document.activeElement;
      const focusedCard = active && active.closest && active.closest('[data-layout-id]');
      const focusId = focusedCard && focusedCard.dataset.layoutId;
      const direction = active && active.dataset && active.dataset.move;
      const wasHandle = active && active.classList && active.classList.contains('drag-handle');
      const nodes = [...sources.values()].flat();
      const ordered = resolve(order, ids());
      const notices = nodes.filter(n => !n.dataset.layoutId && !n.classList.contains('panel-head'));
      board.replaceChildren();
      for (const node of notices) board.appendChild(node);
      for (const id of ordered) {
        const node = cards().find(n => n.dataset.layoutId === id);
        const index = ordered.indexOf(id);
        const controls = node.querySelector('.position-controls');
        controls.hidden = !editing;
        controls.querySelector('[data-move="up"]').disabled = index === 0;
        controls.querySelector('[data-move="down"]').disabled = index === ordered.length - 1;
        board.appendChild(node);
      }
      board.classList.toggle('arranging', editing);
      if (focusId) {
        const replacement = cards().find(n => n.dataset.layoutId === focusId);
        const selector = direction ? '[data-move="' + direction + '"]' : wasHandle ? '.drag-handle' : null;
        const control = replacement && selector && replacement.querySelector(selector);
        if (control && !control.disabled) control.focus({ preventScroll: true });
        else if (replacement && editing) replacement.querySelector('.drag-handle').focus({ preventScroll: true });
      }
    }
    async function save() {
      if (saving || !dirty) return;
      saving = true;
      const sent = order.slice();
      report('Saving positions...');
      try {
        const response = await fetch('/api/card-order', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ order: sent }) });
        if (!response.ok) throw new Error('save failed');
        dirty = JSON.stringify(sent) !== JSON.stringify(order);
        report(dirty ? 'Saving positions...' : 'Positions saved');
      } catch (_) {
        report('Positions not saved. Press Done to retry.');
        saving = false;
        return;
      }
      saving = false;
      if (dirty) save();
    }
    function reposition(id, target, direction) {
      order = move(order, ids(), id, target);
      dirty = true; render();
      const node = cards().find(n => n.dataset.layoutId === id);
      const button = node && node.querySelector('[data-move="' + direction + '"]');
      if (button && !button.disabled) button.focus();
      else if (node) node.querySelector('.drag-handle').focus();
      if (node) node.scrollIntoView({ block: 'nearest' });
      save();
    }
    board.addEventListener('click', event => {
      if (!editing) return;
      event.preventDefault(); event.stopPropagation();
      const button = event.target.closest('[data-move]');
      if (!button || button.disabled) return;
      const id = button.closest('[data-layout-id]').dataset.layoutId;
      const ordered = resolve(order, ids());
      const target = ordered[ordered.indexOf(id) + (button.dataset.move === 'up' ? -1 : 1)];
      if (target) reposition(id, target, button.dataset.move);
    }, true);
    board.addEventListener('dragstart', event => {
      if (!editing || !event.target.closest('.drag-handle')) { event.preventDefault(); return; }
      dragging = event.target.closest('[data-layout-id]').dataset.layoutId;
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', dragging);
    });
    board.addEventListener('dragover', event => {
      if (editing && dragging && event.target.closest('[data-layout-id]')) { event.preventDefault(); event.dataTransfer.dropEffect = 'move'; }
    });
    board.addEventListener('drop', event => {
      if (!editing || !dragging) return;
      event.preventDefault();
      const target = event.target.closest('[data-layout-id]');
      const id = dragging; dragging = null;
      if (target) reposition(id, target.dataset.layoutId, 'up'); else render();
    });
    board.addEventListener('dragend', () => { dragging = null; render(); });
    toggle.addEventListener('click', () => {
      if (!loaded) { report('Loading saved positions. Try again shortly.'); load(); return; }
      if (editing && dirty) { save(); return; }
      editing = !editing;
      toggle.setAttribute('aria-pressed', String(editing));
      toggle.setAttribute('aria-label', editing ? 'Done rearranging cards' : 'Rearrange cards');
      toggle.title = editing ? 'Done rearranging cards' : 'Rearrange cards';
      toggle.querySelector('svg').style.display = editing ? 'none' : '';
      toggle.querySelector('span').hidden = !editing;
      document.body.classList.toggle('editing-layout', editing);
      if (editing) document.querySelector('.toolbar').classList.remove('toolbar-hidden');
      report(editing ? 'Drag a handle or use the arrows. Positions save automatically.' : '');
      render();
    });
    async function load() {
      try {
        const response = await fetch('/api/card-order');
        if (!response.ok) throw new Error('load failed');
        const saved = await response.json();
        if (!dirty && !editing) { order = normalize(saved); loaded = true; render(); }
      } catch (_) { report('Saved positions unavailable. Reconnect to rearrange.'); }
    }
    load();
    window.addEventListener('online', () => { if (dirty) save(); else load(); });
    return { update(source, container) { sources.set(source, Array.from(container.children)); render(); }, refresh: load };
  }
  const api = { normalize, resolve, move, attach };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.CardLayout = api;
})(typeof window !== 'undefined' ? window : globalThis);
