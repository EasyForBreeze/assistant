const test = require('node:test');
const assert = require('node:assert/strict');
const { JSDOM } = require('jsdom');

const { fetchClientSecret, showErrorToast } = require('../wwwroot/js/client-secret.js');

test('fetchClientSecret throws on non-ok response', async () => {
  const fetchImpl = async () => ({
    ok: false,
    status: 503,
    statusText: 'Service Unavailable'
  });
  await assert.rejects(
    () => fetchClientSecret({ url: 'https://example.com/secret', method: 'GET', fetchImpl }),
    /503/,
    'Should throw when the response status is not ok'
  );
});

test('fetchClientSecret throws when parsing fails', async () => {
  const fetchImpl = async () => ({
    ok: true,
    status: 200,
    json: () => { throw new Error('boom'); }
  });
  await assert.rejects(
    () => fetchClientSecret({ url: 'https://example.com/secret', fetchImpl }),
    /разобрать/, 
    'Should throw when parsing fails'
  );
});

test('showErrorToast appends error toast to host', () => {
  const dom = new JSDOM('<!doctype html><html><body><div id="toastsHost"></div></body></html>', { pretendToBeVisual: true });
  global.window = dom.window;
  global.document = dom.window.document;
  global.HTMLElement = dom.window.HTMLElement;

  try {
    const host = document.getElementById('toastsHost');
    const toast = showErrorToast('Ошибка при получении секрета', { host, timeout: 0 });
    assert.ok(toast, 'Toast element should be returned');
    assert.equal(host.children.length, 1, 'Toast host should contain the toast');
    const title = toast.querySelector('.kc-toast-title');
    assert.ok(title, 'Toast should include title element');
    assert.equal(title.textContent, 'Ошибка при получении секрета');
  } finally {
    delete global.window;
    delete global.document;
    delete global.HTMLElement;
  }
});
