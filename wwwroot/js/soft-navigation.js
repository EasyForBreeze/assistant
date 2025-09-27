(function () {
    if (window.__softNavInitialized) {
        return;
    }
    window.__softNavInitialized = true;

    const body = document.body;
    if (!body) {
        return;
    }

    let app = null;
    let appAnimationState = null;
    let currentUrl = window.location.href;

    function cancelAppAnimation() {
        if (!appAnimationState) {
            return;
        }
        const { animation, cleanup } = appAnimationState;
        appAnimationState = null;
        if (animation) {
            try {
                animation.cancel();
            } catch (_) {
                // Ignore animation cancellation errors.
            }
        }
        if (typeof cleanup === 'function') {
            try {
                cleanup();
            } catch (_) {
                // Ignore cleanup errors.
            }
        }
    }

    function animateAppVisibility(shouldShow) {
        if (!app) {
            return null;
        }
        const prefersReducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (prefersReducedMotion || typeof app.animate !== 'function') {
            app.style.opacity = shouldShow ? '1' : '0';
            app.style.transform = shouldShow ? 'translateY(0px)' : 'translateY(12px)';
            return null;
        }

        if (appAnimationState) {
            cancelAppAnimation();
        }

        const target = app;
        const computed = window.getComputedStyle(target);
        const currentOpacity = parseFloat(computed.opacity);
        const startOpacity = isNaN(currentOpacity) ? (shouldShow ? 0 : 1) : currentOpacity;
        const startTransform = computed.transform && computed.transform !== 'none'
            ? computed.transform
            : (shouldShow ? 'translateY(12px)' : 'translateY(0px)');
        const endTransform = shouldShow ? 'translateY(0px)' : 'translateY(12px)';
        const endOpacity = shouldShow ? 1 : 0;

        const previousTransition = target.style.transition;
        target.style.transition = 'none';

        const previousWillChange = target.style.willChange;
        let willChangeOverridden = false;
        if (!previousWillChange) {
            target.style.willChange = 'opacity, transform';
            willChangeOverridden = true;
        }

        const midShowOpacity = Math.min(1, Math.max(startOpacity, 0.85));
        const midHideOpacity = Math.min(1, Math.max(endOpacity, startOpacity - 0.15));
        const keyframes = shouldShow
            ? [
                { opacity: startOpacity, transform: startTransform },
                { opacity: midShowOpacity, transform: 'translateY(-4px)', offset: 0.7 },
                { opacity: endOpacity, transform: endTransform }
            ]
            : [
                { opacity: startOpacity, transform: startTransform },
                { opacity: midHideOpacity, transform: 'translateY(6px)', offset: 0.35 },
                { opacity: endOpacity, transform: endTransform }
            ];

        let animation;
        try {
            animation = target.animate(keyframes, {
                duration: shouldShow ? 460 : 360,
                easing: shouldShow ? 'cubic-bezier(0.33, 1, 0.68, 1)' : 'cubic-bezier(0.4, 0, 0.2, 1)',
                fill: 'forwards'
            });
        } catch (_) {
            target.style.transition = previousTransition;
            if (willChangeOverridden) {
                target.style.willChange = previousWillChange;
            }
            return null;
        }

        let cleaned = false;
        const cleanup = () => {
            if (cleaned) {
                return;
            }
            cleaned = true;
            target.style.transition = previousTransition;
            if (willChangeOverridden) {
                target.style.willChange = previousWillChange;
            }
        };

        const state = { animation, cleanup };
        appAnimationState = state;

        const promise = new Promise(resolve => {
            let resolved = false;
            const finalize = () => {
                if (resolved) {
                    return;
                }
                resolved = true;
                if (appAnimationState === state) {
                    appAnimationState = null;
                }
                cleanup();
                resolve();
            };

            animation.addEventListener('finish', () => {
                if (typeof animation.commitStyles === 'function') {
                    try {
                        animation.commitStyles();
                    } catch (_) {
                        // Ignore browsers that throw for commitStyles.
                    }
                }
                animation.cancel();
                finalize();
            }, { once: true });

            animation.addEventListener('cancel', finalize, { once: true });
        });
        return promise;
    }

    function showApp() {
        animateAppVisibility(true);
        requestAnimationFrame(() => {
            body.classList.remove('page-transitioning');
            body.classList.add('page-loaded');
        });
    }

    function hideApp() {
        const animationPromise = app ? animateAppVisibility(false) : null;
        body.classList.add('page-transitioning');
        body.classList.remove('page-loaded');
        if (!app) {
            return Promise.resolve();
        }
        return animationPromise || waitForTransition(app, 'opacity');
    }

    showApp();

    const root = document.querySelector('[data-soft-root]');
    app = document.getElementById('app');
    const toastsHost = document.getElementById('toastsHost');
    const scriptHost = document.getElementById('pageScripts');
    const ADMIN_ACTIVE_CLASSES = ['bg-white/10', 'text-white', 'shadow-[0_0_0_1px_rgba(255,255,255,0.08)]'];
    const ADMIN_INACTIVE_CLASSES = ['text-slate-300', 'hover:bg-white/5'];
    if (!root || !app) {
        return;
    }

    const reduceMotionQuery = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
    if (reduceMotionQuery) {
        const onReduceMotionChange = () => {
            if (reduceMotionQuery.matches) {
                cancelAppAnimation();
                if (app) {
                    app.style.transition = '';
                    app.style.opacity = '1';
                    app.style.transform = 'translateY(0px)';
                }
            }
        };
        if (typeof reduceMotionQuery.addEventListener === 'function') {
            reduceMotionQuery.addEventListener('change', onReduceMotionChange);
        } else if (typeof reduceMotionQuery.addListener === 'function') {
            reduceMotionQuery.addListener(onReduceMotionChange);
        }
    }

    function parseBoolean(value) {
        if (value == null) {
            return false;
        }
        if (typeof value === 'boolean') {
            return value;
        }
        if (typeof value === 'number') {
            return value !== 0;
        }
        const normalized = String(value).trim().toLowerCase();
        return normalized === '1' || normalized === 'true' || normalized === 'yes' || normalized === '';
    }

    const debugEnabled = parseBoolean(root.dataset.softDebug || body.dataset.softDebug || window.__softNavDebug);

    function debugLog(message, ...details) {
        if (!debugEnabled) {
            return;
        }
        try {
            console.debug('[soft-nav]', message, ...details);
        } catch (_) {
            // Ignore console errors.
        }
    }

    function errorLog(message, error) {
        try {
            if (error && debugEnabled) {
                console.error('[soft-nav]', message, error);
            } else {
                console.error('[soft-nav]', message);
            }
        } catch (_) {
            // Ignore console errors.
        }
    }

    function fallbackToHardNavigation(targetUrl, reason, error) {
        const destination = targetUrl || window.location.href;
        errorLog(`Falling back to full navigation (${reason}) → ${destination}`, error);
        window.location.href = destination;
    }

    const LOADABLE_SELECTOR = '.btn-primary, .btn-danger, .btn-subtle';
    const buttonLoadingCounts = new Map();
    const activeLoadingTargets = new Set();
    const pendingState = { count: 0 };
    const scrollPositions = new Map();
    const scrollStoragePrefix = 'soft-nav:scroll:';
    const sessionStorageAvailable = (() => {
        try {
            const key = '__soft_nav_scroll__';
            sessionStorage.setItem(key, '1');
            sessionStorage.removeItem(key);
            return true;
        } catch (_) {
            return false;
        }
    })();

    if (typeof history.scrollRestoration === 'string') {
        history.scrollRestoration = 'manual';
    }

    function createTransitionForSelector(selector) {
        if (!selector || !app) {
            return null;
        }
        let currentElement;
        try {
            currentElement = app.querySelector(selector);
        } catch (_) {
            return null;
        }
        let nextElement = null;
        return {
            hide() {
                if (!currentElement) {
                    return Promise.resolve();
                }
                currentElement.classList.remove('fade-enter', 'fade-enter-active');
                currentElement.classList.add('fade-leave');
                void currentElement.offsetWidth;
                currentElement.classList.add('fade-leave-active');
                return waitForTransition(currentElement, 'opacity');
            },
            prepare(incomingRoot) {
                nextElement = null;
                if (!incomingRoot) {
                    return;
                }
                try {
                    nextElement = incomingRoot.querySelector(selector);
                } catch (_) {
                    nextElement = null;
                }
                if (!nextElement) {
                    return;
                }
                nextElement.classList.remove('fade-leave', 'fade-leave-active');
                nextElement.classList.add('fade-enter');
            },
            async show() {
                if (!nextElement) {
                    return;
                }
                nextElement.classList.remove('fade-leave', 'fade-leave-active');
                void nextElement.offsetWidth;
                requestAnimationFrame(() => {
                    nextElement.classList.add('fade-enter-active');
                });
                try {
                    await waitForTransition(nextElement, 'opacity');
                } finally {
                    nextElement.classList.remove('fade-enter', 'fade-enter-active');
                    nextElement = null;
                }
            }
        };
    }

    function createScopedTransition(selector) {
        if (!selector || !app) {
            return null;
        }
        const selectors = selector.split(',').map(part => part.trim()).filter(Boolean);
        if (selectors.length === 0) {
            return null;
        }
        const transitions = selectors.map(part => createTransitionForSelector(part)).filter(Boolean);
        if (transitions.length === 0) {
            return null;
        }
        if (transitions.length === 1) {
            return transitions[0];
        }
        return {
            hide() {
                return Promise.all(transitions.map(transition => Promise.resolve(transition.hide()))).then(() => undefined);
            },
            prepare(incomingRoot) {
                transitions.forEach(transition => transition.prepare(incomingRoot));
            },
            show() {
                return Promise.all(transitions.map(transition => Promise.resolve(transition.show()))).then(() => undefined);
            }
        };
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

    function resolveLoadableElement(element) {
        if (!element || !(element instanceof HTMLElement)) {
            return null;
        }
        if (element.matches(LOADABLE_SELECTOR)) {
            return element;
        }
        return element.closest(LOADABLE_SELECTOR);
    }

    function emitLoadingState(target, state) {
        if (!target) {
            return;
        }
        try {
            const event = new CustomEvent('soft:loading-state', { detail: { target, state } });
            document.dispatchEvent(event);
        } catch (error) {
            debugLog('Failed to dispatch loading state event', error);
        }
    }

    function resetAllLoadingStates(reason) {
        if (!activeLoadingTargets.size) {
            return;
        }
        debugLog('Resetting hanging loading states', reason);
        const targets = Array.from(activeLoadingTargets);
        targets.forEach(target => {
            try {
                stopButtonLoading(target);
            } catch (error) {
                errorLog('Failed to reset loading state', error);
            }
        });
    }

    function getScrollStorageKey(url) {
        return `${scrollStoragePrefix}${url}`;
    }

    function storeScrollPosition(url, position) {
        if (!url) {
            return;
        }
        const normalized = {
            x: Math.max(0, Math.round(position && typeof position.x === 'number' ? position.x : window.scrollX)),
            y: Math.max(0, Math.round(position && typeof position.y === 'number' ? position.y : window.scrollY))
        };
        scrollPositions.set(url, normalized);
        if (!sessionStorageAvailable) {
            return;
        }
        try {
            sessionStorage.setItem(getScrollStorageKey(url), JSON.stringify(normalized));
        } catch (error) {
            debugLog('Failed to persist scroll position', error);
        }
    }

    function readStoredScrollPosition(url) {
        if (!url) {
            return null;
        }
        if (scrollPositions.has(url)) {
            return scrollPositions.get(url);
        }
        if (!sessionStorageAvailable) {
            return null;
        }
        try {
            const raw = sessionStorage.getItem(getScrollStorageKey(url));
            if (!raw) {
                return null;
            }
            const parsed = JSON.parse(raw);
            if (parsed && typeof parsed === 'object') {
                scrollPositions.set(url, parsed);
                return parsed;
            }
        } catch (error) {
            debugLog('Failed to read stored scroll position', error);
        }
        return null;
    }

    function restoreScrollPosition(url) {
        const position = readStoredScrollPosition(url);
        if (!position) {
            return false;
        }
        window.scrollTo({ left: position.x || 0, top: position.y || 0, behavior: 'auto' });
        return true;
    }

    function applyScrollBehavior(options, finalUrl) {
        if (!finalUrl) {
            return;
        }
        if (options && options.scroll === false) {
            if (!restoreScrollPosition(finalUrl)) {
                window.scrollTo({ top: 0, left: 0, behavior: 'auto' });
            }
            storeScrollPosition(finalUrl);
            return;
        }

        if (options && options.scroll === 'preserve') {
            storeScrollPosition(finalUrl);
            return;
        }

        let target = null;
        const scrollTarget = options ? options.scrollTarget : null;
        if (scrollTarget) {
            try {
                target = typeof scrollTarget === 'string' ? app.querySelector(scrollTarget) : null;
            } catch (error) {
                debugLog('Invalid scroll target selector', error);
            }
        }
        if (!target) {
            target = app.querySelector('[data-soft-scroll-target]');
        }
        if (target instanceof HTMLElement) {
            try {
                target.scrollIntoView({ behavior: 'auto', block: 'start' });
            } catch (error) {
                debugLog('Failed to scroll to target element', error);
                window.scrollTo({ top: 0, left: 0, behavior: 'auto' });
            }
        } else {
            window.scrollTo({ top: 0, left: 0, behavior: 'auto' });
        }
        storeScrollPosition(finalUrl);
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
        activeLoadingTargets.add(target);
        emitLoadingState(target, 'start');
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
        activeLoadingTargets.delete(target);
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
        emitLoadingState(target, 'stop');
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

    function dispatchPendingEvent() {
        try {
            const event = new CustomEvent('soft:pending-state', { detail: { count: pendingState.count } });
            document.dispatchEvent(event);
        } catch (error) {
            debugLog('Failed to dispatch pending event', error);
        }
    }

    function beginPending(source) {
        pendingState.count += 1;
        if (pendingState.count === 1) {
            body.classList.add('soft-nav-pending');
        }
        debugLog('Pending started', { source, count: pendingState.count });
        dispatchPendingEvent();
    }

    function endPending(source) {
        if (pendingState.count <= 0) {
            pendingState.count = 0;
            return;
        }
        pendingState.count -= 1;
        if (pendingState.count === 0) {
            body.classList.remove('soft-nav-pending');
        }
        debugLog('Pending finished', { source, count: pendingState.count });
        dispatchPendingEvent();
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

    async function fetchAndSwap(url, options, transition) {
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

        beginPending('fetch');
        let response;
        try {
            response = await fetch(requestUrl, fetchInit);
        } catch (error) {
            debugLog('Soft navigation request failed', error);
            fallbackToHardNavigation(requestUrl, 'request-error', error);
            return { success: false, finalUrl: requestUrl };
        } finally {
            endPending('fetch');
        }

        if (!response) {
            debugLog('Soft navigation received empty response');
            fallbackToHardNavigation(requestUrl, 'empty-response');
            return { success: false, finalUrl: requestUrl };
        }

        if (response.status === 204) {
            debugLog('Soft navigation received 204 response');
            fallbackToHardNavigation(requestUrl, 'no-content');
            return { success: false, finalUrl: requestUrl };
        }

        const finalUrlFromResponse = response.url || requestUrl;
        const contentType = response.headers.get('Content-Type') || '';
        if (!contentType.includes('text/html')) {
            debugLog('Soft navigation received unsupported content type', contentType);
            fallbackToHardNavigation(finalUrlFromResponse, 'unsupported-content');
            return { success: false, finalUrl: finalUrlFromResponse };
        }

        let text;
        try {
            text = await response.text();
        } catch (error) {
            debugLog('Failed to read response text', error);
            fallbackToHardNavigation(finalUrlFromResponse, 'read-error', error);
            return { success: false, finalUrl: finalUrlFromResponse };
        }

        let doc;
        try {
            const parser = new DOMParser();
            doc = parser.parseFromString(text, 'text/html');
        } catch (error) {
            debugLog('DOMParser failed to parse response', error);
            fallbackToHardNavigation(finalUrlFromResponse, 'parse-error', error);
            return { success: false, finalUrl: finalUrlFromResponse };
        }

        if (!doc || doc.querySelector('parsererror')) {
            debugLog('Parsed document does not look valid');
            fallbackToHardNavigation(finalUrlFromResponse, 'invalid-document');
            return { success: false, finalUrl: finalUrlFromResponse };
        }

        const newMain = doc.getElementById('app');
        if (!newMain) {
            debugLog('Parsed document does not contain #app');
            fallbackToHardNavigation(finalUrlFromResponse, 'missing-app');
            return { success: false, finalUrl: finalUrlFromResponse };
        }

        const importedMain = document.importNode(newMain, true);
        if (transition && typeof transition.prepare === 'function') {
            try {
                transition.prepare(importedMain);
            } catch (error) {
                debugLog('Transition prepare failed', error);
            }
        }

        let hidePromise = null;
        if (transition && typeof transition.hide === 'function') {
            try {
                hidePromise = transition.hide();
            } catch (error) {
                debugLog('Transition hide threw synchronously', error);
                hidePromise = null;
            }
        } else {
            hidePromise = hideApp();
        }

        if (hidePromise) {
            try {
                await hidePromise;
            } catch (error) {
                debugLog('Transition hide promise rejected', error);
            }
        }
        cancelAppAnimation();
        app.replaceWith(importedMain);
        app = importedMain;
        executeSoftScripts(app);

        if (transition && typeof transition.show === 'function') {
            try {
                await transition.show();
            } catch (error) {
                debugLog('Transition show promise rejected', error);
            }
        }

        refreshToasts(doc);
        refreshScriptHost(doc);

        const newTitle = doc.querySelector('title');
        if (newTitle) {
            document.title = newTitle.textContent || document.title;
        }

        const finalUrl = finalUrlFromResponse;
        if (options.pushState) {
            history.pushState({ url: finalUrl }, '', finalUrl);
        } else if (options.replaceState) {
            history.replaceState({ url: finalUrl }, '', finalUrl);
        }

        applyScrollBehavior(options, finalUrl);
        updateAdminNavActive(finalUrl);
        debugLog('Soft navigation completed', { url: finalUrl, method });

        return { success: true, finalUrl };
    }

    async function handleNavigation(url, opts) {
        const options = Object.assign({ method: 'GET', pushState: true }, opts || {});
        const transitionSelector = options.transition;
        delete options.transition;
        const transition = transitionSelector ? createScopedTransition(transitionSelector) : null;
        const trigger = options.trigger;
        delete options.trigger;
        const loadingTarget = trigger ? startButtonLoading(trigger) : null;

        storeScrollPosition(currentUrl);

        if (!sameOrigin(url)) {
            resetAllLoadingStates('cross-origin');
            if (loadingTarget) {
                stopButtonLoading(loadingTarget);
            }
            hideApp();
            fallbackToHardNavigation(url, 'cross-origin');
            return false;
        }

        let result = { success: false, finalUrl: url };
        try {
            result = await fetchAndSwap(url, options, transition);
            if (result && result.success && result.finalUrl) {
                currentUrl = result.finalUrl;
            }
            return result ? !!result.success : false;
        } catch (error) {
            errorLog('Unexpected navigation error', error);
            fallbackToHardNavigation(url, 'unexpected-error', error);
            return false;
        } finally {
            showApp();
            if (loadingTarget) {
                stopButtonLoading(loadingTarget);
            }
            if (!result || !result.success) {
                resetAllLoadingStates('navigation-failed');
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

    function onPopState(event) {
        resetAllLoadingStates('popstate');
        const url = event.state && event.state.url ? event.state.url : window.location.href;
        handleNavigation(url, { method: 'GET', pushState: false, replaceState: true, scroll: false });
    }

    history.replaceState({ url: currentUrl }, '', currentUrl);
    storeScrollPosition(currentUrl);
    window.addEventListener('beforeunload', () => {
        storeScrollPosition(currentUrl);
    });

    updateAdminNavActive(window.location.href);

    document.addEventListener('click', onLinkClick);
    document.addEventListener('submit', onFormSubmit);
    window.addEventListener('popstate', onPopState);
})();
