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
});

