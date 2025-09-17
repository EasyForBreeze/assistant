(function () {
    const root = document.querySelector('[data-soft-root]');
    let app = document.getElementById('app');
    const spinner = document.getElementById('globalSpinner');
    const toastsHost = document.getElementById('toastsHost');
    const scriptHost = document.getElementById('pageScripts');
    if (!root || !app) {
        return;
    }

    let pending = 0;

    function updateSpinner() {
        if (!spinner) {
            return;
        }
        if (pending > 0) {
            spinner.classList.remove('hidden');
        } else {
            spinner.classList.add('hidden');
        }
    }

    function beginPending() {
        pending += 1;
        updateSpinner();
    }

    function endPending() {
        pending = Math.max(0, pending - 1);
        updateSpinner();
    }

    function executeSoftScripts(container) {
        if (!container) {
            return;
        }
        container.querySelectorAll('script[data-soft-nav]').forEach(script => {
            const clone = document.createElement('script');
            for (const attr of script.attributes) {
                if (attr.name === 'data-soft-nav') {
                    continue;
                }
                clone.setAttribute(attr.name, attr.value);
            }
            clone.textContent = script.textContent;
            script.replaceWith(clone);
        });
    }

    function refreshScriptHost(doc) {
        if (!scriptHost) {
            return;
        }
        scriptHost.innerHTML = '';
        const incoming = doc.getElementById('pageScripts');
        if (!incoming) {
            return;
        }
        incoming.querySelectorAll('script[data-soft-nav]').forEach(script => {
            const clone = document.createElement('script');
            for (const attr of script.attributes) {
                if (attr.name === 'data-soft-nav') {
                    continue;
                }
                clone.setAttribute(attr.name, attr.value);
            }
            clone.textContent = script.textContent;
            scriptHost.appendChild(clone);
        });
    }

    function refreshToasts(doc) {
        if (!toastsHost) {
            return;
        }
        const incoming = doc.getElementById('toastsHost');
        if (!incoming) {
            toastsHost.innerHTML = '';
            return;
        }
        toastsHost.innerHTML = incoming.innerHTML;
        executeSoftScripts(toastsHost);
    }

    function sameOrigin(url) {
        try {
            const target = new URL(url, window.location.href);
            return target.origin === window.location.origin;
        } catch (_) {
            return false;
        }
    }

    function shouldHandleLink(anchor) {
        if (!anchor || anchor.target && anchor.target !== '_self') {
            return false;
        }
        if (anchor.hasAttribute('download')) {
            return false;
        }
        const href = anchor.getAttribute('href');
        if (!href || href.startsWith('#') || href.startsWith('javascript:')) {
            return false;
        }
        if (anchor.closest('[data-soft-ignore]')) {
            return false;
        }
        if (!sameOrigin(anchor.href)) {
            return false;
        }
        return root.contains(anchor);
    }

    function shouldHandleForm(form) {
        if (!form || !(form instanceof HTMLFormElement)) {
            return false;
        }
        if (form.target && form.target !== '_self') {
            return false;
        }
        if (form.hasAttribute('data-soft-ignore') || form.closest('[data-soft-ignore]')) {
            return false;
        }
        return root.contains(form);
    }

    function buildGetUrl(form) {
        const action = form.getAttribute('action') || window.location.href;
        const url = new URL(action, window.location.href);
        const formData = new FormData(form);
        for (const [key, value] of formData.entries()) {
            if (typeof value === 'string') {
                url.searchParams.set(key, value);
            }
        }
        return url.toString();
    }

    async function fetchAndSwap(url, options) {
        const requestUrl = url;
        const method = (options.method || 'GET').toUpperCase();
        const fetchInit = {
            method,
            credentials: 'include',
            headers: {
                'X-Requested-With': 'XMLHttpRequest',
                'Accept': 'text/html,application/xhtml+xml',
                'X-Soft-Nav': '1'
            }
        };
        if (options.body) {
            fetchInit.body = options.body;
        }
        if (options.headers) {
            Object.assign(fetchInit.headers, options.headers);
        }

        beginPending();
        let response;
        try {
            response = await fetch(requestUrl, fetchInit);
        } catch (_) {
            window.location.href = requestUrl;
            return;
        } finally {
            endPending();
        }

        if (!response || response.status === 204) {
            window.location.href = requestUrl;
            return;
        }

        const contentType = response.headers.get('Content-Type') || '';
        if (!contentType.includes('text/html')) {
            window.location.href = response.url || requestUrl;
            return;
        }

        const text = await response.text();
        const parser = new DOMParser();
        const doc = parser.parseFromString(text, 'text/html');
        const newMain = doc.getElementById('app');
        if (!newMain) {
            window.location.href = response.url || requestUrl;
            return;
        }

        const importedMain = document.importNode(newMain, true);
        app.replaceWith(importedMain);
        app = importedMain;
        executeSoftScripts(app);

        refreshToasts(doc);
        refreshScriptHost(doc);

        const newTitle = doc.querySelector('title');
        if (newTitle) {
            document.title = newTitle.textContent || document.title;
        }

        if (options.scroll !== false) {
            window.scrollTo({ top: 0, behavior: 'auto' });
        }

        const finalUrl = response.url || requestUrl;
        if (options.pushState) {
            history.pushState({ url: finalUrl }, '', finalUrl);
        } else if (options.replaceState) {
            history.replaceState({ url: finalUrl }, '', finalUrl);
        }
    }

    async function handleNavigation(url, opts) {
        const options = Object.assign({ method: 'GET', pushState: true }, opts || {});
        if (!sameOrigin(url)) {
            window.location.href = url;
            return;
        }
        await fetchAndSwap(url, options);
    }

    function onLinkClick(event) {
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return;
        }
        const anchor = event.target.closest('a');
        if (!anchor || !shouldHandleLink(anchor)) {
            return;
        }
        event.preventDefault();
        handleNavigation(anchor.href, { method: 'GET', pushState: true });
    }

    function onFormSubmit(event) {
        const form = event.target;
        if (!shouldHandleForm(form)) {
            return;
        }
        event.preventDefault();
        const method = (form.method || 'GET').toUpperCase();
        if (method === 'GET') {
            const url = buildGetUrl(form);
            handleNavigation(url, { method: 'GET', pushState: true });
            return;
        }
        const formData = new FormData(form);
        const action = form.getAttribute('action') || window.location.href;
        handleNavigation(action, { method, body: formData, pushState: true });
    }

    function onPopState(event) {
        const url = event.state && event.state.url ? event.state.url : window.location.href;
        handleNavigation(url, { method: 'GET', pushState: false, replaceState: true, scroll: false });
    }

    function hookXmlHttpRequest() {
        if (!window.XMLHttpRequest) {
            return;
        }
        const origOpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function () {
            this.addEventListener('loadstart', beginPending);
            this.addEventListener('loadend', endPending);
            return origOpen.apply(this, arguments);
        };
    }

    function hookFetch() {
        if (!window.fetch) {
            return;
        }
        const original = window.fetch.bind(window);
        window.fetch = function (input, init) {
            let headers = init && init.headers;
            let skip = false;
            if (headers) {
                if (headers instanceof Headers) {
                    skip = headers.get('X-Soft-Nav') === '1';
                } else if (Array.isArray(headers)) {
                    skip = headers.some(([name, value]) => name === 'X-Soft-Nav' && value === '1');
                } else if (typeof headers === 'object') {
                    skip = headers['X-Soft-Nav'] === '1';
                }
            }

            if (!skip) {
                beginPending();
            }

            const promise = original(input, init);
            if (promise && typeof promise.finally === 'function') {
                return promise.finally(() => {
                    if (!skip) {
                        endPending();
                    }
                });
            }

            promise.then(() => {
                if (!skip) {
                    endPending();
                }
            }, () => {
                if (!skip) {
                    endPending();
                }
            });
            return promise;
        };
    }

    hookXmlHttpRequest();
    hookFetch();
    history.replaceState({ url: window.location.href }, '', window.location.href);

    document.addEventListener('click', onLinkClick);
    document.addEventListener('submit', onFormSubmit);
    window.addEventListener('popstate', onPopState);
})();
