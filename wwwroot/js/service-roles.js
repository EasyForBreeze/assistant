const DEFAULT_MIN_QUERY = 3;
const DEFAULT_PAGE_SIZE = 50;
const CACHE_CLIENTS_LIMIT = 30;
const CACHE_ROLES_LIMIT = 30;

function cacheGet(cache, key) {
    if (!cache.has(key)) {
        return undefined;
    }
    const value = cache.get(key);
    cache.delete(key);
    cache.set(key, value);
    return value;
}

function cacheSet(cache, key, value, limit) {
    if (cache.has(key)) {
        cache.delete(key);
    }
    cache.set(key, value);
    if (cache.size > limit) {
        const oldestKey = cache.keys().next().value;
        if (oldestKey !== undefined) {
            cache.delete(oldestKey);
        }
    }
}

function createElement(tag, className, html) {
    const node = document.createElement(tag);
    if (className) {
        node.className = className;
    }
    if (html != null) {
        node.innerHTML = html;
    }
    return node;
}

function safe(fn) {
    return (...args) => {
        try {
            return fn(...args);
        } catch (error) {
            console.error('[ServiceRolesUI]', error);
            return undefined;
        }
    };
}

function isAbortSignal(value) {
    return typeof AbortSignal !== 'undefined' && value instanceof AbortSignal;
}

function normalizeController(options) {
    if (!options) {
        return { controller: null, signal: null };
    }

    if (options.abortController && typeof options.abortController.abort === 'function') {
        return { controller: options.abortController, signal: options.abortController.signal };
    }

    if (isAbortSignal(options.signal)) {
        return { controller: null, signal: options.signal };
    }

    if (options.signal && typeof options.signal.abort === 'function') {
        const ctrl = options.signal;
        return { controller: ctrl, signal: ctrl.signal };
    }

    const controller = new AbortController();
    return { controller, signal: controller.signal };
}

export function initServiceRoles(root, options = {}) {
    if (!root || !(root instanceof HTMLElement)) {
        return null;
    }

    const pageUrl = options.pageUrl || '';
    if (!pageUrl) {
        console.warn('[ServiceRolesUI] Missing pageUrl option.');
        return null;
    }

    const hiddenInput = options.hiddenInput instanceof HTMLElement ? options.hiddenInput : null;
    const realmInput = options.realmInput instanceof HTMLElement ? options.realmInput : null;
    const minQueryLength = typeof options.minQueryLength === 'number' ? Math.max(1, options.minQueryLength) : DEFAULT_MIN_QUERY;
    const pageSize = typeof options.pageSize === 'number' && options.pageSize > 0 ? options.pageSize : DEFAULT_PAGE_SIZE;

    const getRealm = typeof options.getRealm === 'function'
        ? () => {
            try {
                return (options.getRealm() || '').trim();
            } catch (error) {
                console.error('[ServiceRolesUI] getRealm failed', error);
                return '';
            }
        }
        : () => (typeof options.realm === 'string' ? options.realm.trim() : '');

    const { controller, signal } = normalizeController(options);
    if (controller && typeof window.__softNavRegisterTeardown === 'function') {
        window.__softNavRegisterTeardown(controller);
    }

    const addEvent = (target, type, handler, opts) => {
        if (!target || typeof target.addEventListener !== 'function') {
            return;
        }
        const finalHandler = safe(handler);
        if (signal) {
            const optionsWithSignal = Object.assign({}, opts || {}, { signal });
            target.addEventListener(type, finalHandler, optionsWithSignal);
        } else {
            target.addEventListener(type, finalHandler, opts);
        }
    };

    const query = (selector) => root.querySelector(selector);

    const svcList = query('#svcList');
    const svcSearchInput = query('#svcSearchInput');
    const svcSearchBtn = query('#svcSearchBtn');
    const svcSearchDd = query('#svcSearchDd');
    const svcChosen = query('#svcChosen');
    const svcChosenTag = query('#svcChosenTag');
    const svcChangeBtn = query('#svcChange');
    const svcRoleList = query('#svcRoleList');
    const btnMoreRoles = query('#btnMoreRoles');
    const svcErr = query('#svcErr');

    if (!svcList || !svcSearchInput || !svcSearchDd) {
        console.warn('[ServiceRolesUI] Required markup not found inside root.');
        return null;
    }

    const state = {
        chips: [],
        currentClient: null,
        page: 0,
        size: pageSize,
        more: false,
        cacheClients: new Map(),
        cacheClientRoles: new Map(),
        lastQuery: '',
        lastRealm: (getRealm() || ''),
        roleScanCursor: 0,
        roleScanHasMore: false,
        pendingEmpty: false
    };

    const persist = () => {
        if (hiddenInput) {
            hiddenInput.value = JSON.stringify(state.chips);
        }
        if (typeof options.onChange === 'function') {
            options.onChange([...state.chips]);
        }
    };

    const chipElements = [];

    const createChipElement = (chipValue) => {
        const chip = createElement('div', 'kc-chip');
        chip.textContent = chipValue;
        const closeBtn = createElement('button', 'kc-chip-x', '×');
        closeBtn.type = 'button';
        closeBtn.title = 'Удалить';
        addEvent(closeBtn, 'click', () => {
            const idx = chipElements.indexOf(chip);
            if (idx >= 0) {
                state.chips.splice(idx, 1);
                chipElements.splice(idx, 1);
                chip.remove();
                persist();
            }
        });
        chip.appendChild(closeBtn);
        return chip;
    };

    const renderChips = () => {
        if (!svcList) {
            return;
        }

        const startIdx = chipElements.length;
        if (startIdx >= state.chips.length) {
            return;
        }

        const fragment = document.createDocumentFragment();
        for (let i = startIdx; i < state.chips.length; i++) {
            const chipValue = state.chips[i];
            const chip = createChipElement(chipValue);
            chipElements.push(chip);
            fragment.appendChild(chip);
        }

        if (fragment.childNodes.length) {
            svcList.appendChild(fragment);
        }
    };

    const loadInitialChips = () => {
        const initial = Array.isArray(options.initialRoles) ? options.initialRoles : null;
        if (initial && initial.length) {
            for (const value of initial) {
                if (typeof value === 'string' && !state.chips.includes(value)) {
                    state.chips.push(value);
                }
            }
        } else if (hiddenInput && hiddenInput.value) {
            try {
                const parsed = JSON.parse(hiddenInput.value);
                if (Array.isArray(parsed)) {
                    for (const value of parsed) {
                        if (typeof value === 'string' && !state.chips.includes(value)) {
                            state.chips.push(value);
                        }
                    }
                }
            } catch (error) {
                console.error('[ServiceRolesUI] Failed to parse hidden input value', error);
            }
        }
        renderChips();
        persist();
    };

    const cacheClientsKey = (realm, queryText) => `${realm}::${queryText}`;
    const cacheClientRolesKey = (realm, clientId, page) => `${realm}::${clientId}::${page}`;

    const hideDd = () => {
        if (!svcSearchDd) {
            return;
        }
        svcSearchDd.classList.add('hidden');
        svcSearchDd.innerHTML = '';
        svcSearchDd.style.minHeight = '';
    };

    const showDd = () => {
        if (!svcSearchDd) {
            return;
        }
        svcSearchDd.classList.remove('hidden');
    };

    const showInfo = (message) => {
        if (!svcSearchDd) {
            return;
        }
        svcSearchDd.innerHTML = '';
        svcSearchDd.appendChild(createElement('div', 'px-2 py-1 text-slate-400', message));
        showDd();
    };

    const showError = (message, { append = false } = {}) => {
        if (!svcSearchDd) {
            return;
        }
        const node = createElement('div', 'px-2 py-1 text-rose-400', message);
        if (append) {
            svcSearchDd.appendChild(node);
        } else {
            svcSearchDd.innerHTML = '';
            svcSearchDd.appendChild(node);
        }
        showDd();
    };

    const showLoading = () => {
        if (!svcSearchDd) {
            return;
        }
        svcSearchDd.innerHTML = '<div class="px-3 py-2 text-slate-400">Ищем...</div>';
        svcSearchDd.style.minHeight = '56px';
        showDd();
    };

    const fetchJson = async (url, init) => {
        const finalInit = Object.assign({}, init || {});
        const headers = new Headers(finalInit.headers || {});
        if (!headers.has('Accept')) {
            headers.set('Accept', 'application/json');
        }
        finalInit.headers = headers;
        if (signal && !finalInit.signal) {
            finalInit.signal = signal;
        }
        const response = await fetch(url, finalInit);
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }
        return response.json();
    };

    const ensureMoreHitsButton = () => {
        if (!svcSearchDd) {
            return;
        }
        if (svcSearchDd.querySelector('#roleHitsMoreBtn')) {
            return;
        }
        const btn = createElement('button', 'w-full text-center px-3 py-2 mt-2 hover:bg-slate-700 rounded-md', 'Показать ещё совпадения');
        btn.type = 'button';
        btn.id = 'roleHitsMoreBtn';
        addEvent(btn, 'click', () => searchRolesAcrossClients(state.lastQuery, true, false));
        svcSearchDd.appendChild(btn);
    };

    const removeMoreHitsButton = () => {
        const btn = document.getElementById('roleHitsMoreBtn');
        if (btn) {
            btn.remove();
        }
    };

    const renderRoleHits = (hits, append) => {
        if (!svcSearchDd || !hits.length) {
            return;
        }
        let roleSection = svcSearchDd.querySelector('#roleHitsSection');
        if (!append) {
            if (roleSection) {
                roleSection.remove();
            }
            roleSection = createElement('div', null, '');
            roleSection.id = 'roleHitsSection';
            roleSection.appendChild(createElement('div', 'kc-mini text-slate-400 px-2 pb-1', 'Роли (совпадения)'));
            svcSearchDd.appendChild(roleSection);
        }
        for (const match of hits) {
            const line = createElement('button', 'w-full text-left px-3 py-2 hover:bg-slate-700 rounded-md');
            line.type = 'button';
            line.textContent = `${match.clientId}: ${match.role}`;
            line.title = 'Добавить роль';
            addEvent(line, 'click', () => {
                const value = `${match.clientId}: ${match.role}`;
                if (!state.chips.includes(value)) {
                    state.chips.push(value);
                    renderChips();
                    persist();
                }
                hideDd();
            });
            roleSection.appendChild(line);
        }
    };

    const renderClientList = (clients) => {
        if (!svcSearchDd) {
            return;
        }
        if (!clients.length) {
            showInfo('Совпадений не найдено');
            return;
        }
        const wrap = createElement('div', null, '');
        wrap.appendChild(createElement('div', 'kc-mini text-slate-400 px-2 pb-1', 'Клиенты'));
        clients.forEach(client => {
            const line = createElement('button', 'w-full text-left px-3 py-2 hover:bg-slate-700 rounded-md');
            line.type = 'button';
            line.textContent = client.clientId;
            addEvent(line, 'click', () => selectClient(client));
            wrap.appendChild(line);
        });
        svcSearchDd.innerHTML = '';
        svcSearchDd.appendChild(wrap);
        svcSearchDd.appendChild(createElement('div', 'divider my-2', ''));
        showDd();
    };

    const searchClients = async (queryText, realmValue) => {
        const key = cacheClientsKey(realmValue, queryText);
        const cachedClients = cacheGet(state.cacheClients, key);
        if (cachedClients) {
            return cachedClients;
        }
        const url = `${pageUrl}?handler=ClientsSearch&realm=${encodeURIComponent(realmValue)}&q=${encodeURIComponent(queryText)}&first=0&max=12`;
        const clients = await fetchJson(url);
        const finalClients = Array.isArray(clients) ? clients : [];
        cacheSet(state.cacheClients, key, finalClients, CACHE_CLIENTS_LIMIT);
        return finalClients;
    };

    const searchRolesAcrossClients = async (queryText, append = false, isFirst = false, realmOverride) => {
        const currentRealm = (realmOverride ?? state.lastRealm ?? getRealm() ?? '').trim();
        if (!currentRealm) {
            if (isFirst) {
                showInfo('Сначала выберите Realm.');
            }
            return;
        }
        const url = `${pageUrl}?handler=RoleLookup&realm=${encodeURIComponent(currentRealm)}`
            + `&q=${encodeURIComponent(queryText)}&clientFirst=${state.roleScanCursor}&clientsToScan=25&rolesPerClient=10`;
        let response;
        try {
            response = await fetchJson(url);
        } catch (error) {
            if (error && error.name === 'AbortError') {
                return;
            }
            console.error('[ServiceRolesUI] role lookup failed', error);
            if (isFirst) {
                const message = `Не удалось загрузить роли: ${error?.message ?? error}`;
                if (state.pendingEmpty) {
                    showError(message);
                } else {
                    showError(message, { append: true });
                }
                state.pendingEmpty = false;
            }
            return;
        }
        const hits = Array.isArray(response?.hits) ? response.hits : [];
        renderRoleHits(hits, append);

        if (typeof response?.nextClientFirst === 'number' && response.nextClientFirst >= 0) {
            state.roleScanCursor = response.nextClientFirst;
            state.roleScanHasMore = true;
            ensureMoreHitsButton();
        } else {
            state.roleScanHasMore = false;
            removeMoreHitsButton();
        }

        if (isFirst) {
            if (state.pendingEmpty && hits.length === 0) {
                svcSearchDd.innerHTML = '<div class="px-2 py-1 text-slate-400">Совпадений не найдено</div>';
                showDd();
            }
            state.pendingEmpty = false;
        }
    };

    const renderRoles = (roles, { append }) => {
        if (!svcRoleList) {
            return;
        }
        if (!append) {
            svcRoleList.innerHTML = '';
        }
        if (!roles.length) {
            if (!append) {
                svcRoleList.innerHTML = '<div class="px-2 py-1 text-slate-400">Ролей не найдено</div>';
            }
            return;
        }
        roles.forEach(roleName => {
            const item = createElement('button', 'kc-card px-3 py-2 hover:bg-slate-700 text-left');
            item.type = 'button';
            item.textContent = roleName;
            item.title = 'Добавить роль';
            addEvent(item, 'click', () => {
                if (!state.currentClient) {
                    return;
                }
                const value = `${state.currentClient.clientId}: ${roleName}`;
                if (!state.chips.includes(value)) {
                    state.chips.push(value);
                    renderChips();
                    persist();
                }
            });
            svcRoleList.appendChild(item);
        });
    };

    const loadRoles = async ({ append }) => {
        if (svcErr) {
            svcErr.classList.remove('show');
            svcErr.textContent = '';
        }
        if (!state.currentClient) {
            return;
        }
        const currentRealm = (state.currentClient.realm || getRealm() || '').trim();
        if (!currentRealm) {
            if (svcErr) {
                svcErr.classList.add('show');
                svcErr.textContent = 'Сначала выберите Realm.';
            }
            return;
        }
        const cacheKey = cacheClientRolesKey(currentRealm, state.currentClient.clientId, state.page);
        try {
            let roles = cacheGet(state.cacheClientRoles, cacheKey);
            if (!roles) {
                const url = `${pageUrl}?handler=ClientRoles&id=${encodeURIComponent(state.currentClient.id)}&realm=${encodeURIComponent(currentRealm)}&first=${state.page * state.size}&max=${state.size}`;
                roles = await fetchJson(url);
                roles = Array.isArray(roles) ? roles : [];
                cacheSet(state.cacheClientRoles, cacheKey, roles, CACHE_ROLES_LIMIT);
            }
            renderRoles(roles, { append });
            state.more = roles.length === state.size;
            if (btnMoreRoles) {
                btnMoreRoles.classList.toggle('hidden', !state.more);
            }
        } catch (error) {
            if (error && error.name === 'AbortError') {
                return;
            }
            if (svcErr) {
                svcErr.textContent = `Не удалось загрузить роли: ${error.message}`;
                svcErr.classList.add('show');
            }
        }
    };

    const selectClient = (client) => {
        const realmValue = state.lastRealm || getRealm();
        state.currentClient = { id: client.id, clientId: client.clientId, realm: realmValue };
        if (svcChosenTag) {
            svcChosenTag.textContent = client.clientId;
        }
        if (svcChosen) {
            svcChosen.classList.remove('hidden');
        }
        hideDd();
        if (svcSearchInput) {
            svcSearchInput.value = client.clientId;
        }
        state.page = 0;
        if (svcRoleList) {
            svcRoleList.innerHTML = '';
        }
        loadRoles({ append: false });
    };

    const unifiedSearch = async (rawQuery) => {
        const queryText = (rawQuery || '').trim();
        if (queryText.length < minQueryLength) {
            svcSearchDd.innerHTML = `<div class="px-2 py-1 text-slate-400">Введите минимум ${minQueryLength} символа</div>`;
            showDd();
            return;
        }
        const currentRealm = getRealm();
        if (!currentRealm) {
            showInfo('Сначала выберите Realm.');
            return;
        }
        state.lastQuery = queryText;
        state.lastRealm = currentRealm;
        state.roleScanCursor = 0;
        state.roleScanHasMore = false;
        state.pendingEmpty = true;
        removeMoreHitsButton();
        showLoading();
        let clients = [];
        try {
            clients = await searchClients(queryText, currentRealm);
        } catch (error) {
            if (error && error.name === 'AbortError') {
                return;
            }
            console.error('[ServiceRolesUI] client search failed', error);
            showError(`Не удалось выполнить поиск: ${error?.message ?? error}`);
            state.pendingEmpty = false;
            return;
        }
        if (clients.length) {
            renderClientList(clients);
            state.pendingEmpty = false;
        }
        searchRolesAcrossClients(queryText, false, true, currentRealm).catch(() => { /* ignored */ });
    };

    const updateBtnState = () => {
        if (!svcSearchBtn || !svcSearchInput) {
            return;
        }
        svcSearchBtn.disabled = svcSearchInput.value.trim().length < minQueryLength;
    };

    const resetForRealmChange = () => {
        state.cacheClients.clear();
        state.cacheClientRoles.clear();
        state.currentClient = null;
        state.page = 0;
        state.more = false;
        state.lastQuery = '';
        state.lastRealm = getRealm();
        state.roleScanCursor = 0;
        state.roleScanHasMore = false;
        state.pendingEmpty = false;
        hideDd();
        if (svcChosen) {
            svcChosen.classList.add('hidden');
        }
        if (svcRoleList) {
            svcRoleList.innerHTML = '';
        }
        if (svcErr) {
            svcErr.classList.remove('show');
            svcErr.textContent = '';
        }
        if (svcSearchInput) {
            svcSearchInput.value = '';
        }
        updateBtnState();
    };

    // Initial state and listeners
    loadInitialChips();

    updateBtnState();

    addEvent(svcSearchInput, 'input', updateBtnState);

    addEvent(svcSearchBtn, 'click', () => {
        const queryText = svcSearchInput ? svcSearchInput.value || '' : '';
        showLoading();
        requestAnimationFrame(() => {
            unifiedSearch(queryText);
        });
    });

    addEvent(svcSearchInput, 'keydown', (event) => {
        if (event.key === 'Enter') {
            event.preventDefault();
        }
    });

    addEvent(document, 'click', (event) => {
        if (!svcSearchDd) {
            return;
        }
        const target = event.target;
        if (!(target instanceof Node)) {
            return;
        }
        if (!svcSearchDd.contains(target) && target !== svcSearchInput && target !== svcSearchBtn) {
            hideDd();
        }
    });

    addEvent(btnMoreRoles, 'click', () => {
        state.page += 1;
        loadRoles({ append: true });
    });

    addEvent(svcChangeBtn, 'click', () => {
        state.currentClient = null;
        if (svcChosen) {
            svcChosen.classList.add('hidden');
        }
        if (svcSearchInput) {
            svcSearchInput.focus();
        }
    });

    if (realmInput) {
        addEvent(realmInput, 'change', resetForRealmChange);
    }

    const abortCleanup = () => {
        state.cacheClients.clear();
        state.cacheClientRoles.clear();
        removeMoreHitsButton();
        hideDd();
    };

    if (isAbortSignal(signal)) {
        signal.addEventListener('abort', abortCleanup, { once: true });
    } else if (controller && isAbortSignal(controller.signal)) {
        controller.signal.addEventListener('abort', abortCleanup, { once: true });
    }

    return {
        getRoles: () => [...state.chips],
        reset: resetForRealmChange,
        abort: () => {
            if (controller && typeof controller.abort === 'function') {
                controller.abort();
            }
        }
    };
}
