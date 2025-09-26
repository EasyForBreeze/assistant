import { waitForTransition } from './dom-helpers.js';

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

    const transitions = selectors
        .map(part => createTransitionForSelector(app, part))
        .filter(Boolean);

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
