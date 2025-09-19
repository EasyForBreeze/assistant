(function () {
    const body = document.body;
    if (!body) {
        return;
    }

    function showApp() {
        requestAnimationFrame(() => {
            body.classList.remove('page-transitioning');
            body.classList.add('page-loaded');
        });
    }

    function hideApp() {
        body.classList.add('page-transitioning');
        body.classList.remove('page-loaded');
        if (!app) {
            return Promise.resolve();
        }
        return waitForTransition(app, 'opacity');
    }

    showApp();

    const root = document.querySelector('[data-soft-root]');
    let app = document.getElementById('app');
    const toastsHost = document.getElementById('toastsHost');
    const scriptHost = document.getElementById('pageScripts');
    const ADMIN_ACTIVE_CLASSES = ['bg-white/10', 'text-white', 'shadow-[0_0_0_1px_rgba(255,255,255,0.08)]'];
    const ADMIN_INACTIVE_CLASSES = ['text-slate-300', 'hover:bg-white/5'];
    if (!root || !app) {
        return;
    }

    const LOADABLE_SELECTOR = '.btn-primary, .btn-danger, .btn-subtle';
    const buttonLoadingCounts = new WeakMap();

    function resolveLoadableElement(element) {
        if (!element || !(element instanceof HTMLElement)) {
            return null;
        }
        if (element.matches(LOADABLE_SELECTOR)) {
            return element;
        }
        return element.closest(LOADABLE_SELECTOR);
    }

    function startButtonLoading(element) {
        const target = resolveLoadableElement(element);
        if (!target) {
            return null;
        }
        const count = buttonLoadingCounts.get(target) || 0;
        if (count === 0) {
            target.setAttribute('data-loading', 'true');
            target.setAttribute('aria-busy', 'true');
            if (target instanceof HTMLButtonElement || target instanceof HTMLInputElement) {
                target.dataset.prevDisabled = target.disabled ? 'true' : 'false';
                target.disabled = true;
            } else if (target instanceof HTMLAnchorElement) {
                const pointerState = target.style.pointerEvents ? target.style.pointerEvents : '__unset__';
                target.dataset.prevPointerEvents = pointerState;
                if (target.hasAttribute('tabindex')) {
                    target.dataset.prevTabindex = target.getAttribute('tabindex') || '';
                } else {
                    target.dataset.prevTabindex = '__unset__';
                }
                target.setAttribute('aria-disabled', 'true');
                target.style.pointerEvents = 'none';
                target.setAttribute('tabindex', '-1');
            }
        }
        buttonLoadingCounts.set(target, count + 1);
        return target;
    }

    function stopButtonLoading(element) {
        const target = resolveLoadableElement(element);
        if (!target) {
            return;
        }
        const count = buttonLoadingCounts.get(target);
        if (!count) {
            return;
        }
        if (count > 1) {
            buttonLoadingCounts.set(target, count - 1);
            return;
        }
        buttonLoadingCounts.delete(target);
        target.removeAttribute('aria-busy');
        target.removeAttribute('data-loading');
        if (target instanceof HTMLButtonElement || target instanceof HTMLInputElement) {
            if (target.dataset.prevDisabled === 'false') {
                target.disabled = false;
            }
            delete target.dataset.prevDisabled;
        } else if (target instanceof HTMLAnchorElement) {
            if (target.dataset.prevPointerEvents) {
                if (target.dataset.prevPointerEvents === '__unset__') {
                    target.style.removeProperty('pointer-events');
                } else {
                    target.style.pointerEvents = target.dataset.prevPointerEvents;
                }
            } else {
                target.style.removeProperty('pointer-events');
            }
            if (target.dataset.prevTabindex) {
                if (target.dataset.prevTabindex === '__unset__') {
                    target.removeAttribute('tabindex');
                } else {
                    target.setAttribute('tabindex', target.dataset.prevTabindex);
                }
            } else {
                target.removeAttribute('tabindex');
            }
            target.removeAttribute('aria-disabled');
            delete target.dataset.prevPointerEvents;
            delete target.dataset.prevTabindex;
        }
    }

    function updateAdminNavActive(url) {
        const nav = document.querySelector('[data-admin-nav]');
        if (!nav) {
            return;
        }

        let targetPath = window.location.pathname;
        if (url) {
            try {
                targetPath = new URL(url, window.location.href).pathname;
            } catch (_) {
                // Fallback to current location if URL parsing fails.
            }
        }

        nav.querySelectorAll('a[href]').forEach(anchor => {
            let anchorPath = '';
            try {
                anchorPath = new URL(anchor.href, window.location.href).pathname;
            } catch (_) {
                anchorPath = anchor.getAttribute('href') || '';
            }

            const isActive = anchorPath === targetPath;
            anchor.classList.remove(...ADMIN_ACTIVE_CLASSES, ...ADMIN_INACTIVE_CLASSES);
            if (isActive) {
                anchor.classList.add(...ADMIN_ACTIVE_CLASSES);
            } else {
                anchor.classList.add(...ADMIN_INACTIVE_CLASSES);
            }
        });
    }

    function parseTimeToMs(time) {
        if (!time) {
            return 0;
        }
        const trimmed = time.trim();
        if (trimmed.endsWith('ms')) {
            return parseFloat(trimmed) || 0;
        }
        if (trimmed.endsWith('s')) {
            const value = parseFloat(trimmed);
            return isNaN(value) ? 0 : value * 1000;
        }
        const value = parseFloat(trimmed);
        return isNaN(value) ? 0 : value;
    }

    function getTransitionTimeout(element) {
        const style = window.getComputedStyle(element);
        const durations = style.transitionDuration.split(',').map(parseTimeToMs);
        const delays = style.transitionDelay.split(',').map(parseTimeToMs);
        let max = 0;
        for (let i = 0; i < durations.length; i++) {
            const duration = durations[i] || 0;
            const delay = delays[i] !== undefined ? delays[i] : (delays.length > 0 ? delays[delays.length - 1] : 0);
            max = Math.max(max, duration + delay);
        }
        return max;
    }

    function waitForTransition(element, property) {
        const timeout = getTransitionTimeout(element);
        if (timeout <= 0) {
            return Promise.resolve();
        }
        return new Promise(resolve => {
            let resolved = false;
            const cleanup = () => {
                if (resolved) {
                    return;
                }
                resolved = true;
                element.removeEventListener('transitionend', onTransitionEnd);
                resolve();
            };
            const onTransitionEnd = (event) => {
                if (event.target !== element) {
                    return;
                }
                if (property && event.propertyName !== property) {
                    return;
                }
                cleanup();
            };
            element.addEventListener('transitionend', onTransitionEnd);
            setTimeout(cleanup, timeout + 50);
        });
    }

    function beginPending() { }

    function endPending() { }

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

    function resolveSubmitter(event) {
        if (!event) {
            return null;
        }
        const submitter = event.submitter;
        if (submitter && submitter instanceof HTMLElement) {
            return submitter;
        }
        const form = event.target;
        if (!form || !(form instanceof HTMLFormElement)) {
            return null;
        }
        return form.querySelector('button[type="submit"], input[type="submit"]');
    }

    function buildGetUrl(form, submitter) {
        const action = form.getAttribute('action') || window.location.href;
        const url = new URL(action, window.location.href);
        const formData = new FormData(form);
        if (submitter && submitter.name) {
            const submitValue = submitter.value != null ? submitter.value : '';
            formData.append(submitter.name, submitValue);
        }
        for (const [key, value] of formData.entries()) {
            if (typeof value === 'string') {
                url.searchParams.set(key, value);
            }
        }
        return url.toString();
    }

    async function fetchAndSwap(url, options, hidePromise) {
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
            return false;
        } finally {
            endPending();
        }

        if (!response || response.status === 204) {
            window.location.href = requestUrl;
            return false;
        }

        const contentType = response.headers.get('Content-Type') || '';
        if (!contentType.includes('text/html')) {
            window.location.href = response.url || requestUrl;
            return false;
        }

        const text = await response.text();
        const parser = new DOMParser();
        const doc = parser.parseFromString(text, 'text/html');
        const newMain = doc.getElementById('app');
        if (!newMain) {
            window.location.href = response.url || requestUrl;
            return false;
        }

        const importedMain = document.importNode(newMain, true);
        if (hidePromise) {
            try {
                await hidePromise;
            } catch (_) {
                // Ignore transition wait failures and continue swapping.
            }
        }
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

        updateAdminNavActive(finalUrl);

        return true;
    }

    async function handleNavigation(url, opts) {
        const options = Object.assign({ method: 'GET', pushState: true }, opts || {});
        const trigger = options.trigger;
        delete options.trigger;
        const loadingTarget = trigger ? startButtonLoading(trigger) : null;
        if (!sameOrigin(url)) {
            if (loadingTarget) {
                stopButtonLoading(loadingTarget);
            }
            hideApp();
            window.location.href = url;
            return;
        }
        const hidePromise = hideApp();
        let success;
        try {
            success = await fetchAndSwap(url, options, hidePromise);
            return success;
        } finally {
            showApp();
            if (loadingTarget) {
                stopButtonLoading(loadingTarget);
            }
        }
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
        handleNavigation(anchor.href, { method: 'GET', pushState: true, trigger: anchor });
    }

    function onFormSubmit(event) {
        const form = event.target;
        if (!shouldHandleForm(form)) {
            return;
        }
        event.preventDefault();
        const submitter = resolveSubmitter(event);
        const method = (form.method || 'GET').toUpperCase();
        if (method === 'GET') {
            const url = buildGetUrl(form, submitter);
            handleNavigation(url, { method: 'GET', pushState: true, trigger: submitter });
            return;
        }
        const formData = new FormData(form);
        if (submitter && submitter.name) {
            const submitValue = submitter.value != null ? submitter.value : '';
            formData.append(submitter.name, submitValue);
        }
        const action = form.getAttribute('action') || window.location.href;
        handleNavigation(action, { method, body: formData, pushState: true, trigger: submitter });
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

    updateAdminNavActive(window.location.href);

    document.addEventListener('click', onLinkClick);
    document.addEventListener('submit', onFormSubmit);
    window.addEventListener('popstate', onPopState);
})();
