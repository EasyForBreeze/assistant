export function parseTimeToMs(time) {
    if (!time) {
        return 0;
    }
    const trimmed = time.trim();
    if (trimmed.endsWith('ms')) {
        return parseFloat(trimmed) || 0;
    }
    if (trimmed.endsWith('s')) {
        const value = parseFloat(trimmed);
        return Number.isNaN(value) ? 0 : value * 1000;
    }
    const value = parseFloat(trimmed);
    return Number.isNaN(value) ? 0 : value;
}

export function getTransitionTimeout(element) {
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

export function executeSoftScripts(container) {
    if (!container) {
        return;
    }
    container.querySelectorAll('script[data-soft-nav]').forEach(script => {
        const clone = document.createElement('script');
        for (const attr of script.attributes) {
            if (attr.name === 'data-soft-nav') {
                continue;
            }
            clone.setAttribute(attr.name, attr.value);
        }
        clone.textContent = script.textContent;
        script.replaceWith(clone);
    });
}

export function refreshScriptHost(doc, host) {
    if (!host) {
        return;
    }
    host.innerHTML = '';
    const incoming = doc.getElementById('pageScripts');
    if (!incoming) {
        return;
    }
    incoming.querySelectorAll('script[data-soft-nav]').forEach(script => {
        const clone = document.createElement('script');
        for (const attr of script.attributes) {
            if (attr.name === 'data-soft-nav') {
                continue;
            }
            clone.setAttribute(attr.name, attr.value);
        }
        clone.textContent = script.textContent;
        host.appendChild(clone);
    });
}

export function refreshToasts(doc, host) {
    if (!host) {
        return;
    }
    const incoming = doc.getElementById('toastsHost');
    if (!incoming) {
        host.innerHTML = '';
        return;
    }
    host.innerHTML = incoming.innerHTML;
    executeSoftScripts(host);
}

export function sameOrigin(url) {
    try {
        const target = new URL(url, window.location.href);
        return target.origin === window.location.origin;
    } catch (_) {
        return false;
    }
}
