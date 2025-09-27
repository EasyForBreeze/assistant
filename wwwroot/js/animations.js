import { waitForTransition } from './transitions.js';

const animationStates = new WeakMap();
const activeAnimationStates = new Set();

const CLASSNAMES = {
    base: 'app-visibility',
    visible: 'app-visible',
    animating: 'app-animating',
    showing: 'app-showing',
    hiding: 'app-hiding'
};

const PREFERS_REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';
const motionPreference = typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(PREFERS_REDUCED_MOTION_QUERY)
    : null;

function onMotionPreferenceChange(event) {
    if (!event.matches) {
        return;
    }
    activeAnimationStates.forEach(state => {
        try {
            state.finish();
        } catch (_) {
            // Ignore finish errors.
        }
    });
}

if (motionPreference && typeof motionPreference.addEventListener === 'function') {
    motionPreference.addEventListener('change', onMotionPreferenceChange);
} else if (motionPreference && typeof motionPreference.addListener === 'function') {
    motionPreference.addListener(onMotionPreferenceChange);
}

function ensureBaseClass(target) {
    if (!target || !target.classList) {
        return;
    }
    target.classList.add(CLASSNAMES.base);
}

function applyVisibility(target, shouldShow) {
    if (!target || !target.classList) {
        return;
    }
    target.classList.toggle(CLASSNAMES.visible, Boolean(shouldShow));
}

function clearAnimationClasses(target) {
    if (!target || !target.classList) {
        return;
    }
    target.classList.remove(CLASSNAMES.animating, CLASSNAMES.showing, CLASSNAMES.hiding);
}

function resetToHidden(target) {
    if (!target || !target.classList) {
        return;
    }
    ensureBaseClass(target);
    clearAnimationClasses(target);
    target.classList.remove(CLASSNAMES.visible);
}

export function seedAppVisibility(target) {
    resetToHidden(target);
}

export function cancelAppAnimation(target) {
    if (!target) {
        return;
    }
    const state = animationStates.get(target);
    if (!state) {
        return;
    }
    try {
        state.cancel();
    } finally {
        animationStates.delete(target);
        activeAnimationStates.delete(state);
    }
}

export function animateAppVisibility(target, shouldShow) {
    if (!target) {
        return null;
    }

    ensureBaseClass(target);
    cancelAppAnimation(target);
    clearAnimationClasses(target);

    const prefersReducedMotion = motionPreference && motionPreference.matches;
    const supportsCssAnimations = typeof target.getAnimations === 'function';

    if (!supportsCssAnimations || prefersReducedMotion) {
        applyVisibility(target, shouldShow);
        return null;
    }

    target.classList.add(CLASSNAMES.animating);
    if (shouldShow) {
        target.classList.add(CLASSNAMES.showing);
    } else {
        target.classList.add(CLASSNAMES.hiding);
    }
        return null;
    }

    ensureBaseClass(target);
    cancelAppAnimation(target);
    clearAnimationClasses(target);

    const prefersReducedMotion = motionPreference && motionPreference.matches;
    const supportsCssAnimations = typeof target.getAnimations === 'function';

    if (!supportsCssAnimations || prefersReducedMotion) {
        applyVisibility(target, shouldShow);
        return null;
    }

    target.classList.add(CLASSNAMES.animating);
    if (shouldShow) {
        target.classList.remove(CLASSNAMES.visible);
        target.classList.add(CLASSNAMES.showing);
    } else {
        target.classList.add(CLASSNAMES.hiding);
    }
    const expectedName = shouldShow ? 'app-visibility-show' : 'app-visibility-hide';
    let animations = [];
    try {
        animations = target.getAnimations({ subtree: false });
    } catch (_) {
        animations = target.getAnimations();
    }

    animations = animations.filter(animation => animation.animationName === expectedName);

    if (animations.length === 0) {
        clearAnimationClasses(target);
        applyVisibility(target, shouldShow);
        return null;
    }

    let state = null;
    let resolved = false;
    let resolvePromise = () => {};

    const finalize = () => {
        if (resolved) {
            return;
        }
        resolved = true;
        clearAnimationClasses(target);
        applyVisibility(target, shouldShow);
        if (state) {
            animationStates.delete(target);
            activeAnimationStates.delete(state);
        }
    };

    const promise = new Promise(resolve => {
        resolvePromise = () => {
            resolve();
            resolvePromise = () => {};
        };
        const finishOnce = () => {
            finalize();
            resolvePromise();
        };
        animations.forEach(animation => {
            animation.addEventListener('finish', finishOnce, { once: true });
            animation.addEventListener('cancel', finishOnce, { once: true });
        });
    });

    state = {
        target,
        shouldShow,
        cancel() {
            animations.forEach(animation => {
                try {
                    animation.cancel();
                } catch (_) {
                    // Ignore cancellation errors
                }
            });
            finalize();
            resolvePromise();
        },
        finish() {
            animations.forEach(animation => {
                try {
                    if (typeof animation.finish === 'function') {
                        animation.finish();
                    } else {
                        animation.cancel();
                    }
                } catch (_) {
                    try {
                        animation.cancel();
                    } catch (_) {
                        // Ignore finish errors.
                    }
                }
            });
            finalize();
            resolvePromise();
        }
    };

    animationStates.set(target, state);
    activeAnimationStates.add(state);

    return promise;
}

export function showApp(body, app) {
    animateAppVisibility(app, true);
    if (!body) {
        return;
    }
    requestAnimationFrame(() => {
        body.classList.remove('page-transitioning');
        body.classList.add('page-loaded');
    });
}

export function hideApp(body, app) {
    if (body) {
        body.classList.add('page-transitioning');
        body.classList.remove('page-loaded');
    }
    if (!app) {
        return Promise.resolve();
    }
    const animationPromise = animateAppVisibility(app, false);
    return animationPromise || waitForTransition(app, 'opacity');
}
