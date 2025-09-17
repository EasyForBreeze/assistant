// Smooth page transitions
const TRANSITION_MS = 300;

document.addEventListener('DOMContentLoaded', () => {
    // Fade in content on initial load
    requestAnimationFrame(() => {
        document.body.classList.add('page-loaded');
    });

    // Handle link clicks for fade-out
    document.querySelectorAll('a[href]').forEach(anchor => {
        const href = anchor.getAttribute('href');
        if (!href ||
            anchor.target ||
            href.startsWith('#') ||
            href.startsWith('javascript:') ||
            anchor.hasAttribute('download') ||
            anchor.dataset.noTransition !== undefined) {
            return;
        }

        anchor.addEventListener('click', ev => {
            if (anchor.dataset.noTransition !== undefined) {
                return;
            }
            const url = anchor.href;
            if (url && anchor.origin === window.location.origin) {
                ev.preventDefault();
                document.body.classList.remove('page-loaded');
                setTimeout(() => {
                    window.location.href = url;
                }, TRANSITION_MS);
            }
        });
    });

    // Handle form submissions for fade-out
    document.querySelectorAll('form').forEach(form => {
        if (form.target && form.target !== '_self') {
            return;
        }

        if (form.dataset.noTransition !== undefined) {
            return;
        }

        form.addEventListener('submit', ev => {
            if (ev.defaultPrevented) {
                return;
            }

            const actionUrl = form.getAttribute('action') || window.location.href;
            let url;
            try {
                url = new URL(actionUrl, window.location.href);
            } catch (_) {
                return;
            }

            if (url.origin !== window.location.origin) {
                return;
            }

            ev.preventDefault();
            document.body.classList.remove('page-loaded');
            setTimeout(() => {
                form.submit();
            }, TRANSITION_MS);
        });
    });
});

