import { AppVisibilityController } from './app-visibility.js';
import { createButtonLoadingManager } from './button-loading.js';
import { executeSoftScripts, refreshScriptHost, refreshToasts, sameOrigin, waitForTransition } from './dom-helpers.js';
import { updateAdminNavActive } from './admin-nav.js';
import { createScopedTransition } from './transitions.js';
import { buildGetUrl, resolveSubmitter, resolveTransitionTarget, shouldHandleForm, shouldHandleLink } from './interaction-utils.js';
import { hookFetch, hookXmlHttpRequest } from './network-hooks.js';

const body = document.body;
if (!body) {
    return;
}

const appVisibility = new AppVisibilityController(body, waitForTransition);
const root = document.querySelector('[data-soft-root]');
let app = document.getElementById('app');
const toastsHost = document.getElementById('toastsHost');
const scriptHost = document.getElementById('pageScripts');
const buttonLoadingManager = createButtonLoadingManager();

if (!root || !app) {
    return;
}

appVisibility.setApp(app);
appVisibility.show();

executeSoftScripts(app);
refreshToasts(document, toastsHost);
if (scriptHost) {
    executeSoftScripts(scriptHost);
}

function beginPending() { }
function endPending() { }

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
    if (transition && typeof transition.prepare === 'function') {
        transition.prepare(importedMain);
    }

    let hidePromise = null;
    if (transition && typeof transition.hide === 'function') {
        try {
            hidePromise = transition.hide();
        } catch (_) {
            hidePromise = null;
        }
    } else {
        hidePromise = appVisibility.hide();
    }

    if (hidePromise) {
        try {
            await hidePromise;
        } catch (_) {
            // Ignore transition wait failures and continue swapping.
        }
    }

    appVisibility.cancelAnimation();
    app.replaceWith(importedMain);
    app = importedMain;
    appVisibility.setApp(app);
    executeSoftScripts(app);

    if (transition && typeof transition.show === 'function') {
        try {
            await transition.show();
        } catch (_) {
            // Ignore scoped transition failures and continue.
        }
    }

    refreshToasts(doc, toastsHost);
    refreshScriptHost(doc, scriptHost);

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
    const options = Object.assign({ method: 'GET', pushState: true }, opts || {});
    const transitionSelector = options.transition;
    delete options.transition;
    const transition = transitionSelector ? createScopedTransition(app, transitionSelector) : null;
    const trigger = options.trigger;
    delete options.trigger;

    const loadingTarget = trigger ? buttonLoadingManager.start(trigger) : null;

    if (!sameOrigin(url)) {
        if (loadingTarget) {
            buttonLoadingManager.stop(loadingTarget);
        }
        appVisibility.hide();
        window.location.href = url;
        return;
    }

    let success;
    try {
        success = await fetchAndSwap(url, options, transition);
        return success;
    } finally {
        appVisibility.show();
        if (loadingTarget) {
            buttonLoadingManager.stop(loadingTarget);
        }
    }
}

function onLinkClick(event) {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
        return;
    }
    const anchor = event.target.closest('a');
    if (!anchor || !shouldHandleLink(anchor, root)) {
        return;
    }
    event.preventDefault();
    handleNavigation(anchor.href, { method: 'GET', pushState: true, trigger: anchor });
}

function onFormSubmit(event) {
    const form = event.target;
    if (!shouldHandleForm(form, root)) {
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
    const url = event.state && event.state.url ? event.state.url : window.location.href;
    handleNavigation(url, { method: 'GET', pushState: false, replaceState: true, scroll: false });
}

hookXmlHttpRequest(beginPending, endPending);
hookFetch(beginPending, endPending);

history.replaceState({ url: window.location.href }, '', window.location.href);
updateAdminNavActive(window.location.href);

document.addEventListener('click', onLinkClick);
document.addEventListener('submit', onFormSubmit);
window.addEventListener('popstate', onPopState);
