/**
 * Role-aware UI.
 *
 * The server is the only enforcement point — DashboardRolePolicy answers 403 with a reason code
 * and the API client turns that into text. This module exists so a viewer is told *before* they
 * click, not after: an interface that offers a button which cannot work is a lie, and the failure
 * arrives as a red toast that reads like a bug.
 *
 * Contract for page code:
 *   - add `data-requires-write` to any control that mutates state  → hidden for viewers
 *   - add `data-requires-admin` to control-plane controls          → hidden for viewers and operators
 *   - or call DashboardPermissions.canWrite() when the decision is inside a script
 *
 * Hiding is done in CSS off a single attribute on <html>, so it also covers markup that pages
 * render later — no observers, no re-application after every refresh.
 */
(function() {
    'use strict';

    var state = {
        loaded: false,
        role: 'admin',
        username: '',
        // Defaults assume full rights so a failed status call never locks a working deployment
        // into a read-only view. The server still refuses what the account may not do.
        canWrite: true,
        canAdminister: true,
        implicit: true
    };

    function translate(key, params) {
        var dict = (window.__dashboard && window.__dashboard.I18N) || {};
        var text = dict[key];
        if (text === undefined || text === null || text === '') return null;
        if (params) {
            Object.keys(params).forEach(function(name) {
                text = text.replace('{' + name + '}', params[name]);
            });
        }
        return text;
    }

    function apply() {
        var html = document.documentElement;
        html.setAttribute('data-dashboard-role', state.role);
        html.setAttribute('data-dashboard-writes', state.canWrite ? 'allow' : 'deny');
        renderTopbar();
        renderBanner();
        document.dispatchEvent(new CustomEvent('dashboard:permissions', { detail: snapshot() }));
    }

    function snapshot() {
        return {
            role: state.role,
            username: state.username,
            canWrite: state.canWrite,
            canAdminister: state.canAdminister,
            implicit: state.implicit,
            loaded: state.loaded
        };
    }

    /** Replaces the "online" caption with the role, which is the fact a user needs here. */
    function renderTopbar() {
        var roleEl = document.querySelector('.topbar-user-role');
        if (!roleEl) return;
        var label = translate('auth.role.' + state.role);
        if (label) roleEl.textContent = label;
        if (state.username) {
            var nameEl = document.getElementById('topbar-username');
            var dropdownNameEl = document.getElementById('topbar-dropdown-username');
            if (nameEl) nameEl.textContent = state.username;
            if (dropdownNameEl) dropdownNameEl.textContent = state.username;
        }
    }

    /**
     * A persistent one-line notice. A reader who wonders why a button is missing looks at the top of
     * the page; a toast they dismissed five minutes ago is not there any more.
     */
    function renderBanner() {
        var host = document.getElementById('role-notice');
        if (!host) return;

        // Nothing to say when the role is unrestricted or was never really assigned.
        if (state.canWrite || state.implicit) {
            host.hidden = true;
            host.innerHTML = '';
            return;
        }

        var params = { role: translate('auth.role.' + state.role) || state.role };
        var title = translate('auth.notice.viewer.title', params) || 'Read-only account';
        var text = translate('auth.notice.viewer.body', params)
            || 'You can browse everything on this page, but saves, deletions and environment switches will be refused.';

        host.innerHTML = '<i class="bi bi-eye"></i><div><strong></strong><span></span></div>';
        host.querySelector('strong').textContent = title;
        host.querySelector('span').textContent = text;
        host.hidden = false;
    }

    function load() {
        return window.DashboardApi.get('/api/auth/status', null, { silent: true })
            .then(function(data) {
                if (!data) return snapshot();
                state.loaded = true;
                state.role = data.Role || data.role || 'admin';
                state.username = data.Username || data.username || '';
                state.canWrite = data.CanWrite !== false && data.canWrite !== false;
                state.canAdminister = data.CanAdminister !== false && data.canAdminister !== false;
                state.implicit = data.IsImplicitRole !== false && data.isImplicitRole !== false;
                apply();
                return snapshot();
            })
            .catch(function() {
                // Keep the permissive defaults: the server is the gate, and a status call that
                // failed says nothing about this account's rights.
                state.loaded = false;
                return snapshot();
            });
    }

    window.DashboardPermissions = {
        load: load,
        snapshot: snapshot,
        role: function() { return state.role; },
        canWrite: function() { return state.canWrite; },
        canAdminister: function() { return state.canAdminister; },

        /**
         * Explains a refused write. Returns null when the account is allowed to write, so callers
         * can `if (msg) { warn(msg); return; }` without branching on the role themselves.
         */
        writeDeniedMessage: function() {
            if (state.canWrite) return null;
            var role = translate('auth.role.' + state.role) || state.role;
            return translate('auth.denied.readOnly', { role: role }) || 'This account is read-only.';
        },

        adminDeniedMessage: function() {
            if (state.canAdminister) return null;
            var role = translate('auth.role.' + state.role) || state.role;
            return translate('auth.denied.adminRequired', { role: role }) || 'This operation requires the admin role.';
        },

        /** Re-applies attributes after a locale change swapped the labels. */
        refresh: apply
    };

    /* Deferred to DOMContentLoaded on purpose: every page calls DashboardApi.init() in its own
       inline script, and until that runs there is no base path to prefix the status call with. */
    function boot() {
        if (window.DashboardApi) window.DashboardPermissions.load();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
