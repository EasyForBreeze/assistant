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

export function waitForTransition(element, property) {
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

function createTransitionForSelector(app, selector) {
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

export function createScopedTransition(app, selector) {
    if (!selector || !app) {
        return null;
    }
    const selectors = selector.split(',').map(part => part.trim()).filter(Boolean);
    if (selectors.length === 0) {
        return null;
    }
    const transitions = selectors.map(part => createTransitionForSelector(app, part)).filter(Boolean);
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
