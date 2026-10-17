(function (root) {
  'use strict';

  // Presentation only: typed backend state is authoritative. Diagnostic text is never classified.
  function derive(provider, input) {
    input = input || {};
    const data = input.data;
    const status = input.status;
    const state = status && status.state;
    const stale = !!(data && data.isStale);
    let label = 'Checking';
    let explanation = 'Waiting for a usage status from the desktop app.';
    const descriptions = {
      noNetwork: ['Unreachable', 'The desktop app cannot reach ' + provider + ' usage right now. It will retry automatically.'],
      rateLimited: ['Waiting', 'Claude usage requests are paused while the service limits requests or the sign-in refresh is pending.'],
      authError: ['Unavailable', 'A fresh Claude reading could not be fetched. This can be a sign-in, access or service problem.'],
      sourcesUnavailable: ['Unavailable', 'None of the configured Claude usage sources supplied a fresh reading. Check the source details below.'],
      error: ['Unavailable', 'A fresh Codex reading could not be fetched. The desktop app will retry automatically.'],
      noToken: ['Not signed in', 'No Codex sign-in was found on this computer. Open Codex or run codex login to sign in.'],
      signedOut: provider === 'Claude'
        ? ['Sign-in needed', 'The Claude sign-in on this computer needs attention. Open Claude Code and sign in, or run /login there. Remote sources have their own sign-ins.']
        : ['Sign-in/access needed', 'Codex could not use the saved sign-in or access the usage endpoint. Check Codex access and sign in again if needed.']
    };
    if (state && state !== 'live') {
      const description = descriptions[state] || ['Unavailable', 'The desktop app reported an unrecognized usage status.'];
      label = description[0];
      explanation = description[1];
    } else if (stale) {
      label = 'Stale';
      explanation = 'The last reading is available, but fresh usage has not been confirmed.';
    } else if (data && (state === 'live' || provider === 'Claude')) {
      const windows = data.windows || {};
      const hasQuota = Object.values(windows).some(value => value != null);
      label = hasQuota ? 'Live' : 'No quota reported';
      explanation = hasQuota ? 'The latest usage reading was successful.'
        : 'The latest reading returned no quota windows. This does not establish subscription or plan availability.';
    }
    const dateText = value => {
      const date = new Date(value);
      return value && Number.isFinite(date.getTime()) ? date.toLocaleString() : null;
    };
    const fetched = dateText(data && data.fetchedAt);
    const freshness = fetched ? 'Last successful reading: ' + fetched + (data.source ? ' (from ' + data.source + ')' : '') + '.'
      : 'No successful reading is available in this view yet.';
    const problem = label !== 'Live' && label !== 'No quota reported';
    const retryAt = dateText(status && status.nextAttemptAt);
    const automatic = provider === 'Claude' && problem && state && state !== 'live'
      ? (retryAt ? 'Next automatic attempt scheduled for ' + retryAt + '.' : 'The desktop app will check again automatically.')
      : '';
    return {
      provider, label, problem, explanation, freshness,
      retained: problem && data ? 'Last-known values remain available. Saved history is still available in Statistics.' : '',
      diagnostic: status && status.detail || '',
      automatic,
      settings: provider === 'Claude' && problem
        ? 'Automatic local login refresh is ' + (input.autoRefreshLogin === false ? 'off' : 'on') + '. This setting does not renew sign-ins on remote computers.' : '',
      canRetry: provider === 'Claude' && !!state && state !== 'live'
    };
  }

  function attach(container, actions) {
    actions = actions || {};
    const doc = container.ownerDocument;
    const entries = {};
    let latestModels = [], suppressed = false;
    function visibility() {
      const open = Object.values(entries).some(entry => entry.details.open);
      container.classList.toggle('hidden', suppressed || (!open && latestModels.length > 0 && latestModels.every(model => !model.problem)));
    }
    container.replaceChildren();
    function node(tag, className, text) {
      const element = doc.createElement(tag);
      if (className) element.className = className;
      if (text) element.textContent = text;
      return element;
    }
    for (const provider of ['Claude', 'Codex']) {
      const details = node('details', 'provider-status-entry');
      const summary = node('summary', 'provider-status-summary', provider + ': Checking');
      const body = node('div', 'provider-status-body');
      details.append(summary, body);
      details.addEventListener('toggle', visibility);
      const fields = {};
      for (const name of ['explanation', 'freshness', 'retained', 'diagnostic', 'automatic', 'settings']) {
        fields[name] = node('p', 'provider-status-' + name);
        body.append(fields[name]);
      }
      const controls = node('div', 'provider-status-controls');
      const refresh = node('button', 'provider-status-button', 'Refresh status');
      refresh.type = 'button';
      refresh.title = 'Read the latest status cached by the desktop app';
      refresh.addEventListener('click', () => { if (actions.refresh) actions.refresh(); });
      controls.append(refresh);
      let retry = null;
      if (provider === 'Claude') {
        retry = node('button', 'provider-status-button', 'Retry now');
        retry.type = 'button';
        retry.hidden = true;
        retry.addEventListener('click', () => { if (actions.retry) actions.retry(retry); });
        controls.append(retry);
      }
      body.append(controls);
      container.append(details);
      entries[provider] = { details, summary, fields, retry };
    }
    function text(element, value) {
      if (element.textContent !== value) element.textContent = value;
    }
    return {
      update(models, suppress) {
        latestModels = models;
        suppressed = !!suppress;
        for (const model of models) {
          const entry = entries[model.provider];
          if (!entry) continue;
          text(entry.summary, model.provider + ': ' + model.label);
          entry.details.classList.toggle('provider-status-problem', model.problem);
          for (const [name, element] of Object.entries(entry.fields)) {
            const value = model[name] || '';
            text(element, value);
            element.hidden = !value;
          }
          if (entry.retry) entry.retry.hidden = !model.canRetry;
        }
        // Keep an open disclosure in place during recovery so reading/focus are not interrupted.
        visibility();
      }
    };
  }

  const api = { derive, attach };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.ProviderStatus = api;
})(typeof window !== 'undefined' ? window : globalThis);
