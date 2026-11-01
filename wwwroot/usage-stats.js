(function(root, factory) {
  'use strict';
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.UsageStats = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function() {
  'use strict';

  const RANGE_MS = Object.freeze({ '1d': 86400000, '7d': 604800000, '30d': 2592000000 });
  const COLORS = ['var(--accent)', '#2dd4bf', '#fbbf24', '#60a5fa', '#f472b6'];
  const GAP_MS = 15 * 60 * 1000;
  const PROVIDERS = [
    { id: 'claude', endpoint: '/api/usage-log', host: 'statsChart' },
    { id: 'codex', endpoint: '/api/codex/usage-log', host: 'codexStatsChart' }
  ];

  function escape(value) {
    return String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
  function timestamp(value) {
    return typeof value === 'string' && value.trim() ? Date.parse(value) : NaN;
  }
  function format(value) {
    return value.toLocaleString(undefined, { maximumFractionDigits: 1 });
  }
  function axisMaximum(observedMaximum) {
    // Keep two equal intervals, using steps readable at the label's precision.
    const requiredStep = Math.max(0.1, observedMaximum / 2);
    const magnitude = Math.pow(10, Math.floor(Math.log10(requiredStep)));
    const step = [1, 2, 2.5, 5, 10].map(value => value * magnitude)
      .find(value => value >= requiredStep && value >= 0.1
        && (value >= 1 || Math.abs(value * 10 - Math.round(value * 10)) < 0.000001));
    // Only the numeric representation limit can prevent a rounded upper bound.
    return Math.min(Number.MAX_VALUE, step * 2);
  }
  function period(from, to, latest) {
    const options = { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', timeZoneName: 'short' };
    return (latest ? 'Latest: ' : 'Historical: ') + new Date(from).toLocaleString(undefined, options)
      + ' - ' + new Date(to).toLocaleString(undefined, options);
  }

  function renderChart(data, options) {
    options = options || {};
    const note = text => '<div class="chart-note">' + escape(text) + '</div>';
    if (options.error) return note('Usage history could not be loaded. Change period or choose Latest to retry.');
    if (!data) return note('Loading usage history...');
    const from = timestamp(data.from), to = timestamp(data.to);
    if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from) return note('Usage history returned an invalid period.');
    const series = (Array.isArray(data.series) ? data.series : []).map((s, index) => {
      const points = (s && Array.isArray(s.points) ? s.points : [])
        .filter(p => p && typeof p.v === 'number' && Number.isFinite(p.v) && p.v >= 0)
        .map(p => ({ ms: timestamp(p.t), v: p.v, breakBefore: p.breakBefore === true }))
        .filter(p => Number.isFinite(p.ms) && p.ms >= from && p.ms <= to)
        .sort((a, b) => a.ms - b.ms);
      return { label: s && typeof s.label === 'string' ? s.label : 'Usage window', points,
        max: axisMaximum(points.reduce((max, p) => Math.max(max, p.v), 0)), color: COLORS[index % COLORS.length] };
    });
    if (!series.some(s => s.points.length)) {
      if (!options.latest) return note('No recorded data in this period.');
      return note(options.provider === 'codex'
        ? 'Collecting Codex quota history from this update for the current account. Earlier usage cannot be reconstructed; only reported quota windows are recorded.'
        : 'No recorded usage yet. History is collected while the app receives usage updates.');
    }

    const W = 380, H = 200, left = 43, right = 43, top = 12, bottom = 26;
    const width = W - left - right, height = H - top - bottom;
    const x = ms => left + (ms - from) / (to - from) * width;
    const y = (value, max) => top + height - (max > 0 ? value / max : 0) * height;
    const text = (xx, yy, value, color, anchor) => '<text x="' + xx.toFixed(1) + '" y="' + yy.toFixed(1)
      + '" text-anchor="' + anchor + '" fill="' + color + '" font-size="8" font-family="system-ui,sans-serif">' + escape(value) + '</text>';
    let svg = '<svg viewBox="0 0 380 200" role="img" aria-label="Observed cumulative usage; independently scaled lines" xmlns="http://www.w3.org/2000/svg">';
    [0, 0.5, 1].forEach(f => {
      const yy = top + height - f * height;
      svg += '<line x1="' + left + '" y1="' + yy + '" x2="' + (W - right) + '" y2="' + yy + '" stroke="rgba(255,255,255,0.07)"/>';
      if (series.length <= 2) {
        svg += text(left - 6, yy + 3, format(f * series[0].max), series[0].color, 'end');
        if (series[1]) svg += text(W - right + 6, yy + 3, format(f * series[1].max), series[1].color, 'start');
      } else svg += text(left - 6, yy + 3, f === 0 ? '0' : f === 1 ? 'max' : '1/2 max', 'var(--muted)', 'end');
      const date = new Date(from + f * (to - from));
      const label = to - from <= RANGE_MS['1d'] ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
        : date.toLocaleDateString([], { month: 'short', day: 'numeric' });
      svg += text(left + f * width, H - 8, label, 'var(--muted)', f === 0 ? 'start' : f === 1 ? 'end' : 'middle');
    });
    svg += '<rect x="' + left + '" y="' + top + '" width="' + width + '" height="' + height + '" fill="none" stroke="var(--border)"/>';
    let connected = false;
    series.forEach((s, index) => {
      const segments = [];
      s.points.forEach((p, i) => {
        const previous = s.points[i - 1];
        if (!previous || p.breakBefore || p.ms - previous.ms > GAP_MS || p.v < previous.v) segments.push([]);
        segments[segments.length - 1].push(p);
      });
      segments.forEach(points => {
        if (points.length === 1) {
          svg += '<circle cx="' + x(points[0].ms).toFixed(1) + '" cy="' + y(points[0].v, s.max).toFixed(1) + '" r="2.5" fill="' + s.color + '"/>';
          return;
        }
        connected = true;
        const coords = [x(points[0].ms).toFixed(1) + ',' + y(points[0].v, s.max).toFixed(1)];
        for (let i = 1; i < points.length; i++) {
          coords.push(x(points[i].ms).toFixed(1) + ',' + y(points[i - 1].v, s.max).toFixed(1));
          coords.push(x(points[i].ms).toFixed(1) + ',' + y(points[i].v, s.max).toFixed(1));
        }
        svg += '<polyline fill="none" stroke="' + s.color + '" stroke-width="2" stroke-linejoin="round"'
          + (index % 2 ? ' stroke-dasharray="4,3"' : '') + ' points="' + coords.join(' ') + '"/>';
      });
    });
    svg += '</svg>';
    const legend = '<div class="chart-legend">' + series.map((s, index) => '<span class="legend-item"><svg width="20" height="8" aria-hidden="true"><line x1="0" y1="4" x2="20" y2="4" stroke="' + s.color + '" stroke-width="2"'
      + (index % 2 ? ' stroke-dasharray="4,3"' : '') + '/></svg><span>' + escape(s.label) + ': '
      + (s.points.length ? escape(format(s.points[s.points.length - 1].v)) + ' pp' : 'no samples')
      + (series.length <= 2 ? (index === 0 ? ' (left axis)' : ' (right axis)') : '') + '</span></span>').join('') + '</div>';
    return svg + legend + (!connected ? note('Isolated samples only; not enough continuous data to draw a line.') : '')
      + (series.length > 2 ? note('Axis fractions refer to each line\'s own rounded axis maximum. Legend totals are percentage points (pp).') : '');
  }

  function createController(options) {
    const doc = options.document, fetcher = options.fetch, now = options.now || Date.now;
    let range = '1d', ceiling = now(), end = ceiling, generation = 0;
    let records = {};
    const element = id => doc.getElementById(id);
    function snapshot() {
      const from = end - RANGE_MS[range];
      const allKnown = PROVIDERS.every(p => records[p.id] && records[p.id].status === 'ready');
      const earliest = PROVIDERS.map(p => records[p.id] && timestamp(records[p.id].data && records[p.id].data.earliest)).filter(Number.isFinite);
      return { range, from, to: end, ceiling, latest: end === ceiling,
        previousDisabled: allKnown && (!earliest.length || from <= Math.min(...earliest)) };
    }
    function controls() {
      const state = snapshot();
      doc.querySelectorAll('#rangeChips .chip').forEach(c => {
        const active = c.dataset.range === range;
        c.classList.toggle('active', active);
        c.setAttribute('aria-pressed', String(active));
      });
      element('statsPrevious').disabled = state.previousDisabled;
      element('statsNext').disabled = state.latest;
      element('statsLatest').disabled = state.latest && !PROVIDERS.some(p => records[p.id] && records[p.id].status === 'error');
      element('statsPeriod').textContent = period(state.from, state.to, state.latest);
    }
    async function load(reconciled) {
      const current = ++generation, state = snapshot();
      // Retain known coverage during loading so a boundary cannot briefly reopen.
      controls();
      await Promise.all(PROVIDERS.map(async provider => {
        const host = element(provider.host);
        host.innerHTML = renderChart(null);
        host.setAttribute('aria-busy', 'true');
        try {
          const response = await fetcher(provider.endpoint + '?range=' + range + '&end=' + encodeURIComponent(new Date(state.to).toISOString()));
          if (!response.ok) throw new Error('History request failed');
          const data = await response.json();
          if (generation !== current) return;
          const actualTo = timestamp(data && data.to), actualFrom = timestamp(data && data.from);
          // A remote server can clamp a phone's clock that is ahead, including
          // a paged request made before its first response. Adopt that ceiling
          // once, then request both providers again with the same end.
          if (!reconciled && Number.isFinite(actualTo) && Number.isFinite(actualFrom)
              && actualTo - actualFrom === RANGE_MS[state.range] && actualTo < state.to) {
            end = ceiling = actualTo;
            return load(true);
          }
          records[provider.id] = { status: 'ready', data };
          host.innerHTML = renderChart(data, { latest: state.latest, provider: provider.id });
        } catch (error) {
          if (generation !== current) return;
          records[provider.id] = { status: 'error' };
          host.innerHTML = renderChart(null, { error: true });
        }
        if (generation !== current) return;
        host.setAttribute('aria-busy', 'false');
        controls();
        if (options.onRender) options.onRender();
      }));
    }
    return {
      snapshot,
      open() { if (end === ceiling) end = ceiling = now(); return load(); },
      setRange(value) { if (!RANGE_MS[value]) return Promise.resolve(); range = value; return load(); },
      previous() { if (snapshot().previousDisabled) return Promise.resolve(); end -= RANGE_MS[range]; return load(); },
      next() { end = Math.min(ceiling, end + RANGE_MS[range]); return load(); },
      latest() { end = ceiling = now(); return load(); }
    };
  }
  return { RANGE_MS, renderChart, createController, period };
});
