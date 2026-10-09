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
  // Arrow moves exchange grid cells without shifting the other column or hidden cards.
  function swap(order, available, id, target) {
    const saved = normalize(order);
    const full = saved.concat(available.filter(key => !saved.includes(key)));
    if (id === target || !available.includes(id) || !available.includes(target)) return full;
    const from = full.indexOf(id), to = full.indexOf(target);
    [full[from], full[to]] = [full[to], full[from]];
    return full;
  }
  function neighbour(index, count, columns, direction) {
    if (index < 0 || index >= count || columns < 1) return -1;
    const column = index % columns;
    const offset = { up: -columns, down: columns, left: -1, right: 1 }[direction];
    if (offset === undefined || (direction === 'left' && column === 0)
        || (direction === 'right' && column === columns - 1)) return -1;
    const target = index + offset;
    return target >= 0 && target < count ? target : -1;
  }
  function attach(board, toggle, status) {
    let order = [], editing = false, dragging = null, dirty = false, saving = false, loaded = false;
    const sources = new Map([['claude', []], ['codex', []], ['launcher', []]]);
    const cards = () => [...sources.values()].flatMap(nodes => nodes.filter(n => n.dataset.layoutId));
    const ids = () => cards().map(n => n.dataset.layoutId);
    const report = text => { status.textContent = text; };
    const columns = () => Math.max(1, window.getComputedStyle(board).gridTemplateColumns.split(/\s+/).filter(Boolean).length);
    function updateDirections() {
      const ordered = resolve(order, ids()), width = columns();
      for (const node of cards()) {
        for (const direction of ['up', 'down', 'left', 'right']) {
          const button = node.querySelector('[data-move="' + direction + '"]');
          const unavailable = neighbour(ordered.indexOf(node.dataset.layoutId), ordered.length, width, direction) < 0;
          button.disabled = unavailable;
          button.hidden = unavailable;
          if (unavailable && document.activeElement === button) node.querySelector('.drag-handle').focus({ preventScroll: true });
        }
      }
    }
    function render() {
      if (dragging) return;
      // Rebuilding the list briefly removes its height. Preserve the scroll viewport rather
      // than letting the browser anchor to whichever card happens to move under the finger.
      const viewport = board.parentNode;
      const scrollTop = viewport.scrollTop;
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
        const controls = node.querySelector('.position-controls');
        controls.hidden = !editing;
        board.appendChild(node);
      }
      board.classList.toggle('arranging', editing);
      updateDirections();
      if (focusId) {
        const replacement = cards().find(n => n.dataset.layoutId === focusId);
        const selector = direction ? '[data-move="' + direction + '"]' : wasHandle ? '.drag-handle' : null;
        const control = replacement && selector && replacement.querySelector(selector);
        if (control && !control.disabled) control.focus({ preventScroll: true });
        else if (replacement && editing) replacement.querySelector('.drag-handle').focus({ preventScroll: true });
      }
      viewport.scrollTop = scrollTop;
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
    function reposition(id, target, direction, exchange = false) {
      order = (exchange ? swap : move)(order, ids(), id, target);
      dirty = true; render();
      const node = cards().find(n => n.dataset.layoutId === id);
      const button = node && node.querySelector('[data-move="' + direction + '"]');
      const control = button && !button.disabled ? button : node && node.querySelector('.drag-handle');
      if (control) {
        control.focus({ preventScroll: true });
        const viewport = board.parentNode;
        const bounds = viewport.getBoundingClientRect();
        const toolbar = document.querySelector('.toolbar').getBoundingClientRect();
        const rect = control.getBoundingClientRect();
        const bottom = Math.min(bounds.bottom, toolbar.top);
        // Scroll just enough to retain access to the control, never to fit a tall card.
        if (rect.top < bounds.top) viewport.scrollTop -= bounds.top - rect.top;
        else if (rect.bottom > bottom) viewport.scrollTop += rect.bottom - bottom;
      }
      save();
    }
    board.addEventListener('click', event => {
      if (!editing) return;
      event.preventDefault(); event.stopPropagation();
      const button = event.target.closest('[data-move]');
      if (!button || button.disabled) return;
      const id = button.closest('[data-layout-id]').dataset.layoutId;
      const ordered = resolve(order, ids());
      const target = ordered[neighbour(ordered.indexOf(id), ordered.length, columns(), button.dataset.move)];
      if (target) reposition(id, target, button.dataset.move, true);
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
      report(editing ? 'Arrows swap neighbouring cards. Drag to reorder. Positions save automatically.' : '');
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
    window.addEventListener('resize', updateDirections);
    // Also follow container resizing without changing the dashboard's normal grid.
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(updateDirections).observe(board);
    window.addEventListener('online', () => { if (dirty) save(); else load(); });
    return { update(source, container) { sources.set(source, Array.from(container.children)); render(); }, refresh: load };
  }
  const api = { normalize, resolve, move, swap, neighbour, attach };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.CardLayout = api;
})(typeof window !== 'undefined' ? window : globalThis);
