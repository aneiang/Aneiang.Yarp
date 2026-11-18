/**
 * Environment switcher — points the control plane at prod / staging / dev.
 *
 * Two things this must never conflate, and the UI says so in two different ways:
 *
 *   - The **runtime** environment is what the gateway is forwarding. It cannot be changed from
 *     here at all; it is fixed at process start.
 *   - The **editing view** is what every page reads and writes. Switching it changes all the data
 *     on screen and nothing about live traffic.
 *
 * When those two are equal the switcher is quiet and green. When they are not, it turns amber and
 * spells out what is still being served — because the alternative is an operator editing staging
 * while believing they are looking at production, which is how a "small change" lands in the wrong
 * place at 3am.
 *
 * Hidden entirely for single-environment deployments, so nothing about the existing deployment
 * changes apart from one extra request at page load.
 */
(function () {
    'use strict';

    var API = '/api/environments';

    function t(key, params) {
        var text = window.__(key, params);
        return text && text !== key ? text : null;
    }

    function toast(message, type) {
        if (window.DashboardModals && window.DashboardModals.showToast) {
            window.DashboardModals.showToast(message, type);
        }
    }

    var Environments = {
        _state: null,
        _busy: false,

        init: function () {
            this._load();
        },

        _load: function () {
            var self = this;

            return window.DashboardApi.request(API, { silent: true })
                .then(function (payload) {
                    var data = payload && payload.data ? payload.data : payload;
                    if (!data) return;

                    self._state = data;

                    // Single environment: pretend this feature does not exist.
                    if (!data.multiEnvironment) return;

                    self._render(data);
                    self.announcePendingSwitch();
                })
                .catch(function () {
                    // The switcher is an aid, not a dependency: if the endpoint is unavailable the
                    // rest of the dashboard still works against the runtime environment.
                });
        },

        _render: function (data) {
            var wrap = document.getElementById('env-switch');
            var select = document.getElementById('env-select');
            var serving = document.getElementById('env-serving');
            var warn = document.getElementById('env-warn');
            if (!wrap || !select) return;

            select.innerHTML = '';
            (data.environments || []).forEach(function (env) {
                var option = document.createElement('option');
                option.value = env.name;
                option.textContent = env.isRuntime
                    ? env.name + ' · ' + (t('env.servingShort') || 'serving')
                    : env.name;
                option.selected = env.isCurrent;
                select.appendChild(option);
            });

            var editingRuntime = !!data.isEditingRuntime;

            wrap.hidden = false;
            wrap.className = 'env-switch ' + (editingRuntime ? 'is-editing-runtime' : 'is-not-runtime');

            if (warn) warn.hidden = editingRuntime;

            if (serving) {
                serving.hidden = false;
                serving.textContent = editingRuntime
                    ? (t('env.runtimeBadge') || 'LIVE')
                    : (t('env.servingOther', { env: data.runtime }) || ('serving ' + data.runtime));
                serving.style.background = editingRuntime ? '' : 'rgba(245, 158, 11, 0.15)';
                serving.style.color = editingRuntime ? '' : '#b45309';
            }

            wrap.title = editingRuntime
                ? (t('env.tipRuntime') || '')
                : (t('env.tipOther', { env: data.runtime }) || '');

            select.onchange = function () {
                var target = select.value;
                if (target === data.current) return;
                window.DashboardEnvironments.switchTo(target);
            };
        },

        switchTo: function (environment) {
            var self = this;
            if (self._busy || !self._state) return;

            var data = self._state;
            var targetIsRuntime = environment === data.runtime;

            var run = function () {
                self._busy = true;
                var select = document.getElementById('env-select');
                if (select) select.disabled = true;

                window.DashboardApi.request(API + '/switch', {
                    method: 'POST',
                    body: { environment: environment },
                    silent: true
                })
                    .then(function (payload) {
                        var result = payload && payload.data ? payload.data : payload;

                        // Every module on the page is holding data from the previous environment
                        // (lists, meters, timers). Reloading is the only honest way to show the new
                        // one — patching piece by piece would leave stale panels behind.
                        sessionStorage.setItem('env.switched', result && result.current ? result.current : environment);
                        window.location.reload();
                    })
                    .catch(function (err) {
                        self._busy = false;
                        if (select) {
                            select.disabled = false;
                            select.value = data.current;
                        }
                        var message = err && err.message ? err.message : String(err);
                        toast((t('env.switchFailed') || 'Environment switch failed') + ': ' + message, 'error');
                    });
            };

            var confirmText = targetIsRuntime
                ? null
                : (t('env.confirmOther', { env: environment, runtime: data.runtime })
                    || ('Switch the editing view to "' + environment + '"? Every page will show that environment\'s configuration. Live traffic keeps using "' + data.runtime + '".'));

            if (!confirmText) {
                run();
                return;
            }

            if (window.DashboardModals && window.DashboardModals.showConfirm) {
                var select = document.getElementById('env-select');
                window.DashboardModals.showConfirm(confirmText, run, function () {
                    if (select) select.value = data.current;
                }, {
                    title: t('env.switchTitle') || 'Switch environment',
                    confirmText: t('env.switchBtn') || 'Switch'
                });
            } else {
                run();
            }
        },

        /** Shows a one-shot hint after a reload caused by switching, then clears it. */
        announcePendingSwitch: function () {
            var switched = sessionStorage.getItem('env.switched');
            if (!switched) return;
            sessionStorage.removeItem('env.switched');

            var message = t('env.switchedTo', { env: switched }) || ('Editing ' + switched);
            toast(message, switched === (this._state && this._state.runtime) ? 'success' : 'warning');
        }
    };

    window.DashboardEnvironments = Environments;

    document.addEventListener('DOMContentLoaded', function () {
        Environments.init();
    });
})();
