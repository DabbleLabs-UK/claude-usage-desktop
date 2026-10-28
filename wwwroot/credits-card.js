(function(root, factory) {
  'use strict';
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.CreditsCard = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function() {
  'use strict';

  function escape(value) {
    return String(value).replace(/[&<>"']/g, function(c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function number(value) {
    return typeof value === 'number' && Number.isFinite(value) && value >= 0;
  }

  function format(value) {
    return value.toLocaleString('en-GB', { maximumFractionDigits: 2 });
  }

  function validDate(value) {
    if (typeof value !== 'string' || !value.trim()) return null;
    const date = new Date(value);
    return Number.isFinite(date.getTime()) ? date : null;
  }

  function horizon(label, rate, stale, balance, unlimited) {
    let detail = 'Collecting history';
    let pace = '';
    if (stale) detail = 'Waiting for a fresh balance';
    else if (unlimited) detail = 'Unlimited credits; no depletion estimate';
    else if (!number(balance)) detail = 'Balance unavailable';
    else if (balance === 0) detail = 'No credits remaining';
    else if (rate && rate.status === 'uncertain') detail = 'Balance changed; estimate unavailable';
    else if (rate && rate.status === 'noSpend') detail = 'No credit spending observed';
    else if (rate && rate.status === 'estimated' && number(rate.creditsPerDay)
        && rate.creditsPerDay > 0 && number(rate.estimatedDaysLeft)) {
      pace = '<span class="credit-history-pace">' + escape(format(rate.creditsPerDay)) + ' credits/day</span>';
      detail = rate.estimatedDaysLeft < 0.1 ? 'Less than 0.1 days left' : 'About ' + format(rate.estimatedDaysLeft) + ' days left';
    } else if (rate && rate.status === 'collecting' && number(rate.observedHours)) {
      detail += ' (' + format(rate.observedHours) + ' hours observed)';
    }
    return '<div class="credit-history-horizon"><span class="credit-history-label">' + escape(label)
      + '</span><strong>' + escape(detail) + '</strong>' + pace + '</div>';
  }

  function chart(daily, fetchedAt, now) {
    if (!fetchedAt) return '<p class="credit-history-note">Daily history unavailable</p>';
    const entries = new Map();
    if (Array.isArray(daily)) daily.forEach(function(day) {
      if (!day || typeof day.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(day.date)) return;
      const date = validDate(day.date + 'T00:00:00Z');
      if (!date || date.toISOString().slice(0, 10) !== day.date) return;
      entries.set(day.date, entries.has(day.date) ? null : (number(day.creditsSpent) ? day.creditsSpent : null));
    });
    const end = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
    const readingDay = fetchedAt.toISOString().slice(0, 10);
    const days = [];
    for (let i = 6; i >= 0; i--) {
      const date = new Date(end - i * 86400000);
      const key = date.toISOString().slice(0, 10);
      days.push({ date: key, label: date.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' }), spent: entries.get(key) });
    }
    const max = Math.max(1, ...days.filter(d => number(d.spent)).map(d => d.spent));
    return '<div class="credit-history-chart-title">Estimated daily credit spending (UTC)</div>'
      + '<div class="credit-history-chart" role="list" aria-label="Observed credit spending for today and the previous six UTC days">'
      + days.map(function(day) {
        const known = number(day.spent);
        const amount = known ? format(day.spent) : '-';
        const description = day.date + ': ' + (known ? amount + ' credits spent'
          + (day.date === readingDay ? ' (so far as of last reading)' : '') : 'incomplete or uncertain history');
        const height = known ? Math.max(day.spent > 0 ? 2 : 0, day.spent / max * 48) : 0;
        return '<div class="credit-history-day" role="listitem" aria-label="' + escape(description) + '">'
          + '<span class="credit-history-value" aria-hidden="true">' + escape(amount) + '</span>'
          + '<span class="credit-history-track" aria-hidden="true"><span class="credit-history-fill" style="height:' + height.toFixed(2) + 'px"></span></span>'
          + '<span class="credit-history-date" aria-hidden="true">' + escape(day.label) + '</span></div>';
      }).join('') + '</div><p class="credit-history-note">Today shows spending so far, as of the last reading. - means incomplete or uncertain history.</p>';
  }

  function render(data, now) {
    if (!data || typeof data !== 'object') return '';
    const history = data.creditsHistory && typeof data.creditsHistory === 'object' ? data.creditsHistory : null;
    const balance = data.creditsBalance;
    const unlimited = data.creditsUnlimited === true;
    if (!number(balance) && !history && !unlimited) return '';
    const stale = data.isStale === true;
    const fetchedAt = validDate(data.fetchedAt);
    const stamp = fetchedAt ? '<time datetime="' + escape(fetchedAt.toISOString()) + '">'
      + escape(fetchedAt.toLocaleString('en-GB')) + '</time>' : 'time unavailable';
    return '<article class="card credit-history-card' + (stale ? ' credit-history-stale' : '') + '" aria-label="Codex credits">'
      + '<div class="credit-history-heading"><h2>Codex credits</h2><strong>'
      + (unlimited ? 'Unlimited' : number(balance) ? escape(format(balance)) + ' left' : 'Balance unavailable') + '</strong></div>'
      + '<div class="credit-history-horizons">'
      + horizon('At last 24h pace', history && history.last24Hours, stale, balance, unlimited)
      + horizon('At last 7 days pace', history && history.last7Days, stale, balance, unlimited) + '</div>'
      + chart(history && history.daily, fetchedAt, now || new Date())
      + '<p class="credit-history-note">Estimates use observed balance changes; grants, expiry and corrections can affect them.</p>'
      + '<p class="credit-history-freshness">' + (stale ? 'Stale balance; estimates paused. Last updated ' : 'Balance updated ') + stamp + '</p></article>';
  }

  return { render: render };
});
