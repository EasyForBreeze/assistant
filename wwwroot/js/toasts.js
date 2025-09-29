// Initializes auto-dismiss behavior for flash toasts (CSP-safe external script)
(function () {
    const selector = '.kc-toast[data-timeout]';
    const processedAttr = 'data-timeout-initialized';

    function applyTimeout(toast) {
        if (!toast || toast.hasAttribute(processedAttr)) return;
        toast.setAttribute(processedAttr, 'true');
        const timeout = parseInt(toast.getAttribute('data-timeout'), 10);
        if (!Number.isFinite(timeout) || timeout <= 0) return;
        window.setTimeout(() => {
            try { toast.remove(); } catch (_) { }
        }, timeout);
    }

    function processAll() {
        document.querySelectorAll(selector).forEach(applyTimeout);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', processAll);
    } else {
        processAll();
    }
})();


