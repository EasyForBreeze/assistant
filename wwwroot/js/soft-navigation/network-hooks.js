export function hookXmlHttpRequest(beginPending, endPending) {
    if (!window.XMLHttpRequest) {
        return;
    }
    const originalOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (...args) {
        this.addEventListener('loadstart', beginPending);
        this.addEventListener('loadend', endPending);
        return originalOpen.apply(this, args);
    };
}

export function hookFetch(beginPending, endPending) {
    if (!window.fetch) {
        return;
    }
    const original = window.fetch.bind(window);
    window.fetch = function (input, init = {}) {
        let headers = init && init.headers;
        let skip = false;
        if (headers) {
            if (headers instanceof Headers) {
                skip = headers.get('X-Soft-Nav') === '1';
            } else if (Array.isArray(headers)) {
                skip = headers.some(([name, value]) => name === 'X-Soft-Nav' && value === '1');
            } else if (typeof headers === 'object') {
                skip = headers['X-Soft-Nav'] === '1';
            }
        }

        if (!skip) {
            beginPending();
        }

        const promise = original(input, init);
        if (promise && typeof promise.finally === 'function') {
            return promise.finally(() => {
                if (!skip) {
                    endPending();
                }
            });
        }

        promise.then(() => {
            if (!skip) {
                endPending();
            }
        }, () => {
            if (!skip) {
                endPending();
            }
        });
        return promise;
    };
}
