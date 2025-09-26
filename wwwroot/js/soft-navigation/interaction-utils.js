import { sameOrigin } from './dom-helpers.js';

export function shouldHandleLink(anchor, root) {
    if (!anchor || (anchor.target && anchor.target !== '_self')) {
        return false;
    }
    if (anchor.hasAttribute('download')) {
        return false;
    }
    const href = anchor.getAttribute('href');
    if (!href || href.startsWith('#') || href.startsWith('javascript:')) {
        return false;
    }
    if (anchor.closest('[data-soft-ignore]')) {
        return false;
    }
    if (!sameOrigin(anchor.href)) {
        return false;
    }
    return root ? root.contains(anchor) : true;
}

export function shouldHandleForm(form, root) {
    if (!form || !(form instanceof HTMLFormElement)) {
        return false;
    }
    if (form.target && form.target !== '_self') {
        return false;
    }
    if (form.hasAttribute('data-soft-ignore') || form.closest('[data-soft-ignore]')) {
        return false;
    }
    return root ? root.contains(form) : true;
}

export function resolveSubmitter(event) {
    if (!event) {
        return null;
    }
    const submitter = event.submitter;
    if (submitter && submitter instanceof HTMLElement) {
        return submitter;
    }
    const form = event.target;
    if (!form || !(form instanceof HTMLFormElement)) {
        return null;
    }
    return form.querySelector('button[type="submit"], input[type="submit"]');
}

export function resolveTransitionTarget(form, submitter) {
    if (form && form.dataset && form.dataset.softTransition) {
        return form.dataset.softTransition || null;
    }
    if (submitter && submitter.dataset && submitter.dataset.softTransition) {
        return submitter.dataset.softTransition || null;
    }
    if (form instanceof HTMLElement) {
        const formContainer = form.closest('[data-soft-transition]');
        if (formContainer && formContainer.dataset && formContainer.dataset.softTransition) {
            return formContainer.dataset.softTransition || null;
        }
    }
    const container = submitter instanceof HTMLElement ? submitter.closest('[data-soft-transition]') : null;
    if (container && container.dataset && container.dataset.softTransition) {
        return container.dataset.softTransition || null;
    }
    return null;
}

export function buildGetUrl(form, submitter) {
    const action = form.getAttribute('action') || window.location.href;
    const url = new URL(action, window.location.href);
    const formData = new FormData(form);
    if (submitter && submitter.name) {
        const submitValue = submitter.value != null ? submitter.value : '';
        formData.append(submitter.name, submitValue);
    }
    for (const [key, value] of formData.entries()) {
        if (typeof value === 'string') {
            url.searchParams.set(key, value);
        }
    }
    return url.toString();
}
