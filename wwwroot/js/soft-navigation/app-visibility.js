const prefersReducedMotion = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

function safelyCancelAnimation(animation) {
    if (!animation) {
        return;
    }
    try {
        animation.cancel();
    } catch (_) {
        // Ignore animation cancellation errors.
    }
}

function safelyInvokeCleanup(cleanup) {
    if (typeof cleanup !== 'function') {
        return;
    }
    try {
        cleanup();
    } catch (_) {
        // Ignore cleanup errors.
    }
}

export class AppVisibilityController {
    constructor(body, waitForTransition) {
        this.body = body;
        this.waitForTransition = waitForTransition;
        this.app = null;
        this.animationState = null;
    }

    setApp(app) {
        this.app = app;
    }

    cancelAnimation() {
        if (!this.animationState) {
            return;
        }

        const { animation, cleanup } = this.animationState;
        this.animationState = null;
        safelyCancelAnimation(animation);
        safelyInvokeCleanup(cleanup);
    }

    animateVisibility(shouldShow) {
        if (!this.app || typeof this.app.animate !== 'function') {
            return null;
        }

        if (prefersReducedMotion()) {
            return null;
        }

        this.cancelAnimation();

        const target = this.app;
        const computed = window.getComputedStyle(target);
        const currentOpacity = parseFloat(computed.opacity);
        const startOpacity = Number.isNaN(currentOpacity) ? (shouldShow ? 0 : 1) : currentOpacity;
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
        this.animationState = state;

        const promise = new Promise(resolve => {
            let resolved = false;
            const finalize = () => {
                if (resolved) {
                    return;
                }
                resolved = true;
                if (this.animationState === state) {
                    this.animationState = null;
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

    show() {
        this.animateVisibility(true);
        requestAnimationFrame(() => {
            this.body.classList.remove('page-transitioning');
            this.body.classList.add('page-loaded');
        });
    }

    hide() {
        const animationPromise = this.app ? this.animateVisibility(false) : null;
        this.body.classList.add('page-transitioning');
        this.body.classList.remove('page-loaded');
        if (!this.app) {
            return Promise.resolve();
        }
        return animationPromise || this.waitForTransition(this.app, 'opacity');
    }
}
