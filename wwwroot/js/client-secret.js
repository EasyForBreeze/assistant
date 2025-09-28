(function (global, factory) {
  if (typeof module === 'object' && typeof module.exports === 'object') {
    module.exports = factory(global);
  } else {
    const exports = factory(global);
    global.ClientSecret = exports;
  }
})(typeof globalThis !== 'undefined' ? globalThis : (typeof window !== 'undefined' ? window : this), function (global) {
  const DEFAULT_TOAST_TIMEOUT = 5000;

  function resolveHost(host) {
    if (host && typeof host === 'object' && host.nodeType === 1) {
      return host;
    }

    if (!global || !global.document) {
      return null;
    }

    if (typeof host === 'string' && host) {
      const target = global.document.querySelector(host);
      return target instanceof global.HTMLElement ? target : null;
    }

    const candidate = global.document.getElementById('toastsHost');
    return candidate instanceof global.HTMLElement ? candidate : null;
  }

  function showErrorToast(message, options = {}) {
    const { host, timeout = DEFAULT_TOAST_TIMEOUT } = options;
    const targetHost = resolveHost(host);

    if (!targetHost || !global || !global.document) {
      console.error('Unable to display error toast:', message);
      return null;
    }

    const toast = global.document.createElement('div');
    toast.className = 'kc-toast kc-toast-error';
    toast.setAttribute('role', 'alert');

    const icon = global.document.createElement('div');
    icon.className = 'kc-toast-icon';
    icon.textContent = '!';
    toast.appendChild(icon);

    const body = global.document.createElement('div');
    body.className = 'kc-toast-body';
    const title = global.document.createElement('div');
    title.className = 'kc-toast-title';
    title.textContent = message || '';
    body.appendChild(title);
    toast.appendChild(body);

    const close = global.document.createElement('button');
    close.type = 'button';
    close.className = 'kc-toast-close';
    close.setAttribute('aria-label', 'Закрыть');
    close.textContent = '×';
    close.addEventListener('click', () => toast.remove());
    toast.appendChild(close);

    targetHost.appendChild(toast);

    if (Number.isFinite(timeout) && timeout > 0 && typeof global.setTimeout === 'function') {
      global.setTimeout(() => toast.remove(), timeout);
    }

    return toast;
  }

  async function fetchClientSecret(options = {}) {
    const { url, method = 'GET', fetchImpl } = options;

    if (!url || typeof url !== 'string') {
      throw new Error('Client secret endpoint URL is required.');
    }

    const fetchFn = typeof fetchImpl === 'function'
      ? fetchImpl
      : typeof global.fetch === 'function'
        ? global.fetch
        : null;

    if (!fetchFn) {
      throw new Error('A fetch implementation is required to request the client secret.');
    }

    let response;
    try {
      response = await fetchFn(url, { method });
    } catch (error) {
      const err = new Error('Не удалось запросить секрет клиента.');
      err.cause = error;
      throw err;
    }

    if (!response || typeof response.ok !== 'boolean') {
      throw new Error('Некорректный ответ при получении секрета клиента.');
    }

    if (!response.ok) {
      const status = typeof response.status === 'number' ? response.status : '';
      const statusText = response.statusText || '';
      const statusPart = `${status} ${statusText}`.trim();
      throw new Error(`Не удалось получить секрет клиента${statusPart ? `: ${statusPart}` : '.'}`);
    }

    let data;
    try {
      data = await response.json();
    } catch (error) {
      const err = new Error('Не удалось разобрать ответ с секретом клиента.');
      err.cause = error;
      throw err;
    }

    if (!data || typeof data.secret !== 'string') {
      throw new Error('Ответ не содержит секрет клиента.');
    }

    return data.secret;
  }

  return {
    showErrorToast,
    fetchClientSecret
  };
});
