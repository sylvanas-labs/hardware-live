// Fetch helpers and the polling scheduler (docs/SPEC.md step5 feature 1). Every request is
// same-origin, GET for reads and token-guarded POST/PUT for layout writes -- there is no
// cross-origin traffic of any kind (CSP's connect-src 'self' also enforces this).

export function readToken() {
  return document.querySelector('meta[name="hl-token"]')?.getAttribute('content') ?? '';
}

export async function getJson(path) {
  const response = await fetch(path, { cache: 'no-store' });
  if (response.status === 204) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`${path} failed: ${response.status}`);
  }

  return response.json();
}

export async function writeLayout(token, method, path, body) {
  const response = await fetch(path, {
    method,
    cache: 'no-store',
    headers: {
      'Content-Type': 'application/json',
      'X-HL-Token': token,
    },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(`${path} failed: ${response.status}`);
  }

  return response;
}

/**
 * Polls `fetchOne` every `intervalMs`. On a failed request it backs off to `backoffMs`
 * (min 5000ms per docs/SPEC.md step5 feature 1) and calls `onStatus('lost')`; the next
 * success restores the normal interval and calls `onStatus('recovered')`. Polling pauses
 * entirely while the tab is hidden and resumes (with an immediate fetch) when it becomes
 * visible again.
 */
export class Poller {
  #intervalMs;
  #backoffMs;
  #fetchOne;
  #onData;
  #onStatus;
  #timer = null;
  #currentDelay;
  #stopped = false;
  #lastOk = true;
  #everOk = false;
  #visibilityHandler;

  constructor({ intervalMs, backoffMs = 5000, fetchOne, onData, onStatus }) {
    this.#intervalMs = intervalMs;
    this.#backoffMs = Math.max(intervalMs, backoffMs);
    this.#fetchOne = fetchOne;
    this.#onData = onData;
    this.#onStatus = onStatus ?? (() => {});
    this.#currentDelay = intervalMs;
  }

  start() {
    this.#visibilityHandler = () => {
      if (document.hidden) {
        this.#clearTimer();
      } else {
        this.#tick();
      }
    };
    document.addEventListener('visibilitychange', this.#visibilityHandler);
    // Always fetch once, even when hidden: a dashboard window that opens behind other windows
    // at logon (Chromium reports occluded windows as hidden) must not sit blank. Continued
    // polling still pauses while hidden and resumes on visibilitychange.
    this.#tick(true);
  }

  stop() {
    this.#stopped = true;
    this.#clearTimer();
    if (this.#visibilityHandler) {
      document.removeEventListener('visibilitychange', this.#visibilityHandler);
    }
  }

  #clearTimer() {
    if (this.#timer !== null) {
      clearTimeout(this.#timer);
      this.#timer = null;
    }
  }

  async #tick(initial = false) {
    if (this.#stopped || (document.hidden && !initial)) {
      return;
    }

    let data;
    let fetched = false;
    try {
      data = await this.#fetchOne();
      fetched = true;
    } catch (error) {
      // A failed request means the connection is lost: back off and report it.
      console.warn('[hardware-live] request failed', error);
      if (this.#lastOk) {
        this.#lastOk = false;
        this.#onStatus('lost');
      }

      this.#currentDelay = this.#backoffMs;
    }

    if (fetched) {
      if (!this.#lastOk || !this.#everOk) {
        // Report the first success too, so the UI leaves its initial "connecting" state.
        this.#lastOk = true;
        this.#everOk = true;
        this.#currentDelay = this.#intervalMs;
        this.#onStatus('recovered');
      }

      try {
        this.#onData(data);
      } catch (error) {
        // A rendering bug is NOT a lost connection: never swallow it silently, and keep
        // polling at the normal rate so the next good payload can recover the view.
        console.error('[hardware-live] failed to render data', error);
      }
    }

    if (this.#stopped || document.hidden) {
      return;
    }

    this.#timer = window.setTimeout(() => this.#tick(), this.#currentDelay);
  }
}
