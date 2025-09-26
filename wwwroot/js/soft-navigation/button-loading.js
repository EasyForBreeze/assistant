const DEFAULT_SELECTOR = '.btn-primary, .btn-danger, .btn-subtle';

function resolveLoadableElement(element, selector) {
    if (!element || !(element instanceof HTMLElement)) {
        return null;
    }
    if (element.matches(selector)) {
        return element;
    }
    return element.closest(selector);
}

function restorePointerState(target) {
    if (target.dataset.prevPointerEvents) {
        if (target.dataset.prevPointerEvents === '__unset__') {
            target.style.removeProperty('pointer-events');
        } else {
            target.style.pointerEvents = target.dataset.prevPointerEvents;
        }
    } else {
        target.style.removeProperty('pointer-events');
    }
}

function restoreTabState(target) {
    if (target.dataset.prevTabindex) {
        if (target.dataset.prevTabindex === '__unset__') {
            target.removeAttribute('tabindex');
        } else {
            target.setAttribute('tabindex', target.dataset.prevTabindex);
        }
    } else {
        target.removeAttribute('tabindex');
    }
}

export function createButtonLoadingManager(selector = DEFAULT_SELECTOR) {
    const loadingCounts = new WeakMap();

    function start(element) {
        const target = resolveLoadableElement(element, selector);
        if (!target) {
            return null;
        }

        const count = loadingCounts.get(target) || 0;
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

        loadingCounts.set(target, count + 1);
        return target;
    }

    function stop(element) {
        const target = resolveLoadableElement(element, selector);
        if (!target) {
            return;
        }

        const count = loadingCounts.get(target);
        if (!count) {
            return;
        }

        if (count > 1) {
            loadingCounts.set(target, count - 1);
            return;
        }

        loadingCounts.delete(target);
        target.removeAttribute('aria-busy');
        target.removeAttribute('data-loading');

        if (target instanceof HTMLButtonElement || target instanceof HTMLInputElement) {
            if (target.dataset.prevDisabled === 'false') {
                target.disabled = false;
            }
            delete target.dataset.prevDisabled;
        } else if (target instanceof HTMLAnchorElement) {
            restorePointerState(target);
            restoreTabState(target);
            target.removeAttribute('aria-disabled');
            delete target.dataset.prevPointerEvents;
            delete target.dataset.prevTabindex;
        }
    }

    return { start, stop };
}
