import { initNavigation } from './navigation.js';
import { setupLoadingHooks } from './loading.js';
function bootstrap() {
    const body = document.body;
    if (!body) {
        return;
    }

    if (window.__softNavInitialized) {
        console.warn('Soft navigation is already initialized.');
        return;
    }

    const root = document.querySelector('[data-soft-root]');
    const app = document.getElementById('app');
    const toastsHost = document.getElementById('toastsHost');
    const scriptHost = document.getElementById('pageScripts');

    const controller = initNavigation({ body, root, app, toastsHost, scriptHost });
    if (!controller) {
        return;
    }

    window.__softNavInitialized = true;

    setupLoadingHooks();

    controller.showApp();
    controller.updateAdminNavActive(window.location.href);
    history.replaceState({ url: window.location.href }, '', window.location.href);

    document.addEventListener('click', controller.onLinkClick);
    document.addEventListener('submit', controller.onFormSubmit);
    window.addEventListener('popstate', controller.onPopState);
}

bootstrap();
