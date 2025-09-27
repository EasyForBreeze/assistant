const LOADABLE_SELECTOR = '.btn-primary, .btn-danger, .btn-subtle';
const buttonLoadingCounts = new WeakMap();
let hooksInstalled = false;

function resolveLoadableElement(element) {
    if (!element || !(element instanceof HTMLElement)) {
        return null;
    }
    if (element.matches(LOADABLE_SELECTOR)) {
        return element;
    }
    return element.closest(LOADABLE_SELECTOR);
}

export function startButtonLoading(element) {
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

export function stopButtonLoading(element) {
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

export function beginPending() { }

export function endPending() { }

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

export function setupLoadingHooks() {
    if (hooksInstalled) {
        return;
    }
    hooksInstalled = true;
    hookXmlHttpRequest();
    hookFetch();
}
