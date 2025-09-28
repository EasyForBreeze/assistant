import { createScopedTransition } from './transitions.js';
import { showApp as showAppAnimation, hideApp as hideAppAnimation, cancelAppAnimation } from './animations.js';
import { startButtonLoading, stopButtonLoading, beginPending, endPending } from './loading.js';

export function initNavigation({ body, root, app, toastsHost, scriptHost }) {
    if (!body || !root || !app) {
        return null;
    }

    const teardownCallbacks = new Set();

    function registerTeardown(entry) {
        if (!entry) {
            return () => {};
        }
        let callback = null;
        if (typeof entry === 'function') {
            callback = entry;
        } else if (entry && typeof entry.abort === 'function') {
            callback = () => {
                try {
                    entry.abort();
                } catch (error) {
                    console.error('Soft navigation teardown abort failed:', error);
                }
            };
        } else {
            return () => {};
        }
        teardownCallbacks.add(callback);
        return () => teardownCallbacks.delete(callback);
    }

    function runTeardowns() {
        if (!teardownCallbacks.size) {
            return;
        }
        const entries = Array.from(teardownCallbacks);
        teardownCallbacks.clear();
        for (const fn of entries) {
            try {
                fn();
            } catch (error) {
                console.error('Soft navigation teardown failed:', error);
            }
        }
    }

    if (typeof window !== 'undefined') {
        window.__softNavRegisterTeardown = registerTeardown;
    }

    let currentApp = app;
    const ADMIN_ACTIVE_CLASSES = ['bg-white/10', 'text-white', 'shadow-[0_0_0_1px_rgba(255,255,255,0.08)]'];
    const ADMIN_INACTIVE_CLASSES = ['text-slate-300', 'hover:bg-white/5'];

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
            toastsHost.replaceChildren();
            return;
        }
        const nodes = Array.from(incoming.childNodes, node => document.importNode(node, true));
        toastsHost.replaceChildren(...nodes);
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
        const processedKeys = new Set();
        for (const [key, value] of formData.entries()) {
            if (typeof value === 'string') {
                if (!processedKeys.has(key)) {
                    url.searchParams.delete(key);
                    processedKeys.add(key);
                }
                url.searchParams.append(key, value);
            }
        }
        return url.toString();
    }

    async function fetchAndSwap(url, options, transition) {
        const requestUrl = url;
        const method = (options.method || 'GET').toUpperCase();
        const shouldPreserveScroll = options && options.scroll === false;
        let hideStarted = false;
        let hidePromise = null;

        const startHide = () => {
            if (hideStarted) {
                return hidePromise;
            }
            hideStarted = true;
            if (transition && typeof transition.hide === 'function') {
                try {
                    hidePromise = Promise.resolve(transition.hide());
                } catch (_) {
                    hidePromise = Promise.resolve();
                }
            } else {
                const result = hideAppAnimation(body, currentApp);
                hidePromise = result ? Promise.resolve(result) : Promise.resolve();
            }
            return hidePromise;
        };

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

        const revertHide = () => {
            if (!hideStarted) {
                return;
            }
            try {
                showAppAnimation(body, currentApp);
            } catch (error) {
                console.error('Soft navigation revert failed:', error);
            }
        };

        beginPending();
        let response;
        try {
            hidePromise = startHide();
            response = await fetch(requestUrl, fetchInit);
        } catch (_) {
            revertHide();
            window.location.href = requestUrl;
            return false;
        } finally {
            endPending();
        }

        if (!response || response.status === 204) {
            revertHide();
            window.location.href = requestUrl;
            return false;
        }

        const contentType = response.headers.get('Content-Type') || '';
        if (!contentType.includes('text/html')) {
            revertHide();
            window.location.href = response.url || requestUrl;
            return false;
        }

        if (!hideStarted) {
            hidePromise = startHide();
        }

        const text = await response.text();
        const parser = new DOMParser();
        const doc = parser.parseFromString(text, 'text/html');
        const newMain = doc.getElementById('app');
        if (!newMain) {
            revertHide();
            window.location.href = response.url || requestUrl;
            return false;
        }

        let importedMain = null;
        if (typeof document.adoptNode === 'function') {
            try {
                importedMain = document.adoptNode(newMain);
            } catch (_) {
                importedMain = null;
            }
        }
        if (!importedMain) {
            importedMain = document.importNode(newMain, true);
        }
        if (transition && typeof transition.prepare === 'function') {
            transition.prepare(importedMain);
        }

        if (hidePromise) {
            try {
                await hidePromise;
            } catch (_) {
                // Ignore transition wait failures and continue swapping.
            }
        }
        runTeardowns();
        cancelAppAnimation(currentApp);
        try {
            currentApp.dispatchEvent(new CustomEvent('soft:teardown'));
        } catch (error) {
            console.error('Soft navigation teardown event failed:', error);
        }
        currentApp.replaceWith(importedMain);
        currentApp = importedMain;
        executeSoftScripts(currentApp);

        const hadTabIndex = currentApp.hasAttribute('tabindex');
        const previousTabIndex = currentApp.getAttribute('tabindex');
        currentApp.setAttribute('tabindex', '-1');
        try {
            if (shouldPreserveScroll) {
                currentApp.focus({ preventScroll: true });
            } else {
                currentApp.focus();
            }
        } catch (error) {
            console.error('Soft navigation focus failed:', error);
        } finally {
            if (hadTabIndex) {
                if (previousTabIndex === null) {
                    currentApp.removeAttribute('tabindex');
                } else {
                    currentApp.setAttribute('tabindex', previousTabIndex);
                }
            } else {
                currentApp.removeAttribute('tabindex');
            }
        }

        if (!shouldPreserveScroll) {
            try {
                window.scrollTo(0, 0);
            } catch (error) {
                console.error('Soft navigation scroll reset failed:', error);
            }
        }

        if (transition && typeof transition.show === 'function') {
            try {
                await transition.show();
            } catch (_) {
                // Ignore scoped transition failures and continue.
            }
        }

        refreshToasts(doc);
        refreshScriptHost(doc);

        const newTitle = doc.querySelector('title');
        if (newTitle) {
            document.title = newTitle.textContent || document.title;
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
        const options = Object.assign({ method: 'GET', pushState: true, scroll: true }, opts || {});
        const transitionSelector = options.transition;
        delete options.transition;
        const transition = transitionSelector ? createScopedTransition(currentApp, transitionSelector) : null;
        const trigger = options.trigger;
        delete options.trigger;
        const loadingTarget = trigger ? startButtonLoading(trigger) : null;
        if (!sameOrigin(url)) {
            if (loadingTarget) {
                stopButtonLoading(loadingTarget);
            }
            hideAppAnimation(body, currentApp);
            window.location.href = url;
            return;
        }
        let success;
        try {
            success = await fetchAndSwap(url, options, transition);
            return success;
        } finally {
            showAppAnimation(body, currentApp);
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
            const transitionTarget = resolveTransitionTarget(form, submitter);
            handleNavigation(url, { method: 'GET', pushState: true, trigger: submitter, transition: transitionTarget });
            return;
        }
        const formData = new FormData(form);
        if (submitter && submitter.name) {
            const submitValue = submitter.value != null ? submitter.value : '';
            formData.append(submitter.name, submitValue);
        }
        const action = form.getAttribute('action') || window.location.href;
        const transitionTarget = resolveTransitionTarget(form, submitter);
        handleNavigation(action, { method, body: formData, pushState: true, trigger: submitter, transition: transitionTarget });
    }

    function resolveTransitionTarget(form, submitter) {
        if (form && form.dataset && form.dataset.softTransition) {
            return form.dataset.softTransition || null;
        }
        if (submitter && submitter.dataset && submitter.dataset.softTransition) {
            return submitter.dataset.softTransition || null;
        }
        if (form instanceof HTMLElement) {
            const formContainer = form.closest('[data-soft-transition]');
            if (formContainer && formContainer.dataset && formContainer.dataset.softTransition) {
                return formContainer.dataset.softTransition || null;
            }
        }
        const container = submitter instanceof HTMLElement ? submitter.closest('[data-soft-transition]') : null;
        if (container && container.dataset && container.dataset.softTransition) {
            return container.dataset.softTransition || null;
        }
        return null;
    }

    function onPopState(event) {
        const url = event.state && event.state.url ? event.state.url : window.location.href;
        handleNavigation(url, { method: 'GET', pushState: false, replaceState: true, scroll: false });
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

    function showApp() {
        showAppAnimation(body, currentApp);
    }

    return {
        handleNavigation,
        onLinkClick,
        onFormSubmit,
        onPopState,
        showApp,
        updateAdminNavActive
    };
}
