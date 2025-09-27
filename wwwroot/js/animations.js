import { waitForTransition } from './transitions.js';

const animationStates = new WeakMap();

export function cancelAppAnimation(target) {
    if (!target) {
        return;
    }
    const state = animationStates.get(target);
    if (!state) {
        return;
    }
    animationStates.delete(target);
    const { animation, cleanup } = state;
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

export function animateAppVisibility(target, shouldShow) {
    if (!target || typeof target.animate !== 'function') {
        return null;
    }
    if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        return null;
    }

    cancelAppAnimation(target);

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
    animationStates.set(target, state);

    const promise = new Promise(resolve => {
        let resolved = false;
        const finalize = () => {
            if (resolved) {
                return;
            }
            resolved = true;
            if (animationStates.get(target) === state) {
                animationStates.delete(target);
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
