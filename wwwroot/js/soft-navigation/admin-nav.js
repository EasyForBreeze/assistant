const DEFAULT_ACTIVE_CLASSES = ['bg-white/10', 'text-white', 'shadow-[0_0_0_1px_rgba(255,255,255,0.08)]'];
const DEFAULT_INACTIVE_CLASSES = ['text-slate-300', 'hover:bg-white/5'];

export function updateAdminNavActive(url, options = {}) {
    const nav = document.querySelector('[data-admin-nav]');
    if (!nav) {
        return;
    }

    const { activeClasses = DEFAULT_ACTIVE_CLASSES, inactiveClasses = DEFAULT_INACTIVE_CLASSES } = options;

    let targetPath = window.location.pathname;
    if (url) {
        try {
            targetPath = new URL(url, window.location.href).pathname;
        } catch (_) {
            // Fallback to current location if URL parsing fails.
        }
    }

    nav.querySelectorAll('a[href]').forEach(anchor => {
        let anchorPath = '';
        try {
            anchorPath = new URL(anchor.href, window.location.href).pathname;
        } catch (_) {
            anchorPath = anchor.getAttribute('href') || '';
        }

        const isActive = anchorPath === targetPath;
        anchor.classList.remove(...activeClasses, ...inactiveClasses);
        if (isActive) {
            anchor.classList.add(...activeClasses);
        } else {
            anchor.classList.add(...inactiveClasses);
        }
    });
}
