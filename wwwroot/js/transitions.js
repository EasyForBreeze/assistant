(function () {
    const mainSelector = 'main#app';
    const toastsSelector = '#pageToasts';
    const scriptsSelector = '#pageScripts';
    const parser = new DOMParser();
    const reduceMotion = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)').matches : false;
    let currentController = null;
    let navigationToken = 0;
    let scrollSaveTimer = null;

    const body = document.body;
    const initialFocusAttr = 'data-soft-nav-root';

    window.pending = window.pending || 0;
    window.updateSpinner = window.updateSpinner || function () {
        const spinner = document.getElementById('globalSpinner');
        if (!spinner) {
            return;
        }
        if ((window.pending || 0) > 0) {
            spinner.classList.remove('hidden');
        } else {
            spinner.classList.add('hidden');
        }
    };

    const originalFetch = window.fetch.bind(window);
    window.fetch = function (...args) {
        window.pending = (window.pending || 0) + 1;
        window.updateSpinner();
        return originalFetch(...args).then(
            response => {
                window.pending = Math.max(0, (window.pending || 0) - 1);
                window.updateSpinner();
                return response;
            },
            error => {
                window.pending = Math.max(0, (window.pending || 0) - 1);
                window.updateSpinner();
                throw error;
            });
    };

    function startTransition() {
        if (!reduceMotion) {
            body.classList.remove('page-loaded');
        }
    }

    function endTransition() {
        if (reduceMotion) {
            body.classList.add('page-loaded');
            return;
        }
        requestAnimationFrame(() => body.classList.add('page-loaded'));
    }

    function executeScripts(root) {
        if (!root) {
            return;
        }
        const scripts = Array.from(root.querySelectorAll('script'));
        for (const oldScript of scripts) {
            const newScript = document.createElement('script');
            for (const attr of oldScript.attributes) {
                newScript.setAttribute(attr.name, attr.value);
            }
            newScript.textContent = oldScript.textContent;
            oldScript.replaceWith(newScript);
        }
    }

    function focusMain(main) {
        if (!main) {
            return;
        }
        const previousTabIndex = main.getAttribute('tabindex');
        if (previousTabIndex === null) {
            main.setAttribute('tabindex', '-1');
            main.addEventListener('blur', () => main.removeAttribute('tabindex'), { once: true });
        }
        try {
            main.focus({ preventScroll: true });
        } catch (_) {
            main.focus();
        }
    }

    function updateSections(doc) {
        const main = document.querySelector(mainSelector);
        const nextMain = doc.querySelector(mainSelector);
        if (!main || !nextMain) {
            return false;
        }

        main.innerHTML = nextMain.innerHTML;
        executeScripts(main);

        const toastsHost = document.querySelector(toastsSelector);
        const nextToasts = doc.querySelector(toastsSelector);
        if (toastsHost) {
            toastsHost.innerHTML = nextToasts ? nextToasts.innerHTML : '';
            executeScripts(toastsHost);
        }

        const scriptsHost = document.querySelector(scriptsSelector);
        const nextScripts = doc.querySelector(scriptsSelector);
        if (scriptsHost) {
            scriptsHost.innerHTML = nextScripts ? nextScripts.innerHTML : '';
            executeScripts(scriptsHost);
        }

        return true;
    }

    function restoreScrollFromState(scroll) {
        if (typeof scroll === 'number') {
            window.scrollTo(0, scroll);
        } else {
            window.scrollTo(0, 0);
        }
    }

    function swapDocument(doc, url, historyMode, scrollPosition) {
        if (!updateSections(doc)) {
            window.location.href = url;
            return;
        }

        document.title = doc.title || document.title;

        if (historyMode === 'push') {
            history.pushState({ url, scroll: 0 }, '', url);
        } else if (historyMode === 'replace') {
            history.replaceState({ url, scroll: scrollPosition || 0 }, '', url);
        }

        restoreScrollFromState(scrollPosition);

        const main = document.querySelector(mainSelector);
        focusMain(main);
        endTransition();
    }

    function storeScrollPosition() {
        const state = history.state || {};
        state.url = location.href;
        state.scroll = window.scrollY || window.pageYOffset || 0;
        history.replaceState(state, '', location.href);
    }

    function scheduleScrollSave() {
        if (scrollSaveTimer) {
            clearTimeout(scrollSaveTimer);
        }
        scrollSaveTimer = window.setTimeout(storeScrollPosition, 150);
    }

    function navigate(url, options = {}) {
        const historyMode = options.historyMode || 'push';
        const scrollPosition = options.scrollPosition;
        const normalizedUrl = typeof url === 'string' ? url : url.toString();

        if (!normalizedUrl) {
            return;
        }

        if (historyMode === 'push') {
            storeScrollPosition();
            if (normalizedUrl === window.location.href) {
                return;
            }
        }

        if (currentController) {
            currentController.abort();
        }

        const controller = new AbortController();
        currentController = controller;
        const token = ++navigationToken;

        startTransition();

        window.fetch(normalizedUrl, {
            credentials: 'same-origin',
            headers: {
                'X-Requested-With': 'XMLHttpRequest',
                'X-Soft-Navigation': '1'
            },
            signal: controller.signal
        }).then(response => {
            if (token !== navigationToken) {
                return;
            }
            if (!response.ok) {
                window.location.href = normalizedUrl;
                return;
            }
            if (response.redirected) {
                window.location.href = response.url;
                return;
            }
            const contentType = response.headers.get('content-type') || '';
            if (!contentType.includes('text/html')) {
                window.location.href = normalizedUrl;
                return;
            }
            return response.text().then(html => {
                if (token !== navigationToken) {
                    return;
                }
                const doc = parser.parseFromString(html, 'text/html');
                swapDocument(doc, normalizedUrl, historyMode, scrollPosition);
            });
        }).catch(error => {
            if (error && error.name === 'AbortError') {
                return;
            }
            console.error('Soft navigation failed, falling back to full reload.', error);
            window.location.href = normalizedUrl;
        }).finally(() => {
            if (token === navigationToken) {
                currentController = null;
            }
        });
    }

    function shouldHandleLink(event, anchor) {
        if (!anchor || anchor.target && anchor.target.toLowerCase() !== '_self') {
            return false;
        }
        if (anchor.hasAttribute('download')) {
            return false;
        }
        if (anchor.dataset.noTransition !== undefined) {
            return false;
        }
        const href = anchor.getAttribute('href');
        if (!href || href.startsWith('#') || href.startsWith('javascript:')) {
            return false;
        }
        const url = new URL(anchor.href, window.location.href);
        if (url.origin !== window.location.origin) {
            return false;
        }
        const samePath = url.pathname === window.location.pathname && url.search === window.location.search;
        if (samePath && url.hash) {
            return false;
        }
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0) {
            return false;
        }
        return true;
    }

    function handleLinkClick(event) {
        if (event.defaultPrevented) {
            return;
        }
        const anchor = event.target.closest('a');
        if (!shouldHandleLink(event, anchor)) {
            return;
        }
        const url = anchor.href;
        event.preventDefault();
        navigate(url, { historyMode: 'push' });
    }

    function shouldHandleForm(form) {
        if (!(form instanceof HTMLFormElement)) {
            return false;
        }
        if (form.target && form.target.toLowerCase() !== '_self') {
            return false;
        }
        if (form.dataset.noTransition !== undefined) {
            return false;
        }
        const method = (form.getAttribute('method') || 'GET').toUpperCase();
        if (method !== 'GET') {
            return false;
        }
        return true;
    }

    function handleFormSubmit(event) {
        if (event.defaultPrevented) {
            return;
        }
        const form = event.target;
        if (!shouldHandleForm(form)) {
            return;
        }
        const action = form.getAttribute('action') || window.location.href;
        let url;
        try {
            url = new URL(action, window.location.href);
        } catch (_) {
            return;
        }
        if (url.origin !== window.location.origin) {
            return;
        }
        const formData = new FormData(form);
        const params = new URLSearchParams(url.search);
        for (const [key, value] of formData.entries()) {
            if (typeof value === 'string') {
                params.append(key, value);
            }
        }
        url.search = params.toString();
        event.preventDefault();
        navigate(url.toString(), { historyMode: 'push' });
    }

    window.addEventListener('popstate', event => {
        const scroll = event.state && typeof event.state.scroll === 'number' ? event.state.scroll : 0;
        navigate(window.location.href, { historyMode: 'replace', scrollPosition: scroll });
    });

    document.addEventListener('click', handleLinkClick);
    document.addEventListener('submit', handleFormSubmit, true);
    window.addEventListener('scroll', scheduleScrollSave, { passive: true });

    document.addEventListener('DOMContentLoaded', () => {
        endTransition();
        storeScrollPosition();
        const main = document.querySelector(mainSelector);
        if (main && main.hasAttribute(initialFocusAttr)) {
            focusMain(main);
        }
    });
})();
