/**
 * Environment comparison and promotion.
 *
 * Two requests, on purpose:
 *   prepare → shows the diff and hands back a single-use token
 *   apply   → writes the target store, and publishes when the target is the runtime environment
 *
 * The split is the feature, not an implementation detail. "Copy staging to prod" as one button is a
 * loaded gun: the operator cannot see what it will change until it has changed it. Here the diff is
 * the thing being approved, and if either side moves between the two calls the server refuses and
 * asks for a fresh look.
 *
 * The UI never pretends a promotion is reversible: it says what will happen to live traffic when
 * the target is the runtime environment, in those words.
 */
(function () {
    'use strict';

    var API = '/api/environments';

    function t(key, params) {
        var text = window.__(key, params);
        return text && text !== key ? text : null;
    }

    function say(key, fallback, params) {
        return t(key, params) || fallback;
    }

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function toast(message, type) {
        if (window.DashboardModals && window.DashboardModals.showToast) {
            window.DashboardModals.showToast(message, type);
        }
    }

    var Promote = {
        _state: null,
        _prepared: null,
        _overlay: null,
        _source: null,
        _target: null,

        init: function () {
            var button = document.getElementById('env-promote-btn');
            if (!button) return;

            button.addEventListener('click', function () { Promote.open(); });

            // The switcher only renders itself for multi-environment deployments; the promote
            // button inherits that, so there is nothing to hide here.
        },

        open: function () {
            var self = this;
            if (self._overlay) return;

            window.DashboardApi.request(API, { silent: true })
                .then(function (payload) {
                    var data = payload && payload.data ? payload.data : payload;
                    if (!data || !data.environments || data.environments.length < 2) {
                        toast(say('envp.needTwo', 'Only one environment is configured.'), 'warning');
                        return;
                    }
                    self._state = data;
                    self._source = data.current;
                    self._target = data.runtime === data.current
                        ? (data.environments.filter(function (e) { return e.name !== data.current; })[0] || {}).name
                        : data.runtime;

                    self._render();
                })
                .catch(function (err) {
                    toast((err && err.message) || say('envp.loadFailed', 'Could not read environments'), 'error');
                });
        },

        _render: function () {
            var self = this;
            var overlay = document.createElement('div');
            overlay.className = 'envp-overlay';
            overlay.innerHTML =
                '<div class="envp-modal" role="dialog" aria-modal="true">' +
                    '<div class="envp-head">' +
                        '<span class="envp-title">' + esc(say('envp.title', 'Compare and promote')) + '</span>' +
                        '<button type="button" class="envp-close" aria-label="close"><i class="bi bi-x-lg"></i></button>' +
                    '</div>' +
                    '<div class="envp-picker">' +
                        '<label>' + esc(say('envp.source', 'From')) +
                            '<select class="envp-from"></select></label>' +
                        '<i class="bi bi-arrow-right"></i>' +
                        '<label>' + esc(say('envp.target', 'To')) +
                            '<select class="envp-to"></select></label>' +
                        '<button type="button" class="envp-diff-btn">' + esc(say('envp.showDiff', 'Show changes')) + '</button>' +
                    '</div>' +
                    '<div class="envp-body"></div>' +
                    '<div class="envp-foot">' +
                        '<button type="button" class="envp-cancel">' + esc(say('envp.cancel', 'Close')) + '</button>' +
                        '<button type="button" class="envp-apply" disabled>' + esc(say('envp.apply', 'Promote')) + '</button>' +
                    '</div>' +
                '</div>';

            document.body.appendChild(overlay);
            self._overlay = overlay;

            var from = overlay.querySelector('.envp-from');
            var to = overlay.querySelector('.envp-to');

            (self._state.environments || []).forEach(function (env) {
                from.appendChild(new Option(self._label(env), env.name));
                to.appendChild(new Option(self._label(env), env.name));
            });

            from.value = self._source;
            to.value = self._target;

            overlay.querySelector('.envp-close').addEventListener('click', function () { self.close(); });
            overlay.querySelector('.envp-cancel').addEventListener('click', function () { self.close(); });
            overlay.addEventListener('click', function (event) {
                if (event.target === overlay) self.close();
            });

            from.addEventListener('change', function () {
                self._source = from.value;
                self._resetBody();
            });

            to.addEventListener('change', function () {
                self._target = to.value;
                self._resetBody();
            });

            overlay.querySelector('.envp-diff-btn').addEventListener('click', function () { self._loadDiff(); });
            overlay.querySelector('.envp-apply').addEventListener('click', function () { self._prepare(); });

            self._loadDiff();
        },

        _label: function (env) {
            return env.isRuntime ? env.name + ' · ' + say('envp.serving', 'serving') : env.name;
        },

        _resetBody: function () {
            this._prepared = null;
            var body = this._overlay && this._overlay.querySelector('.envp-body');
            var apply = this._overlay && this._overlay.querySelector('.envp-apply');
            if (body) body.innerHTML = '';
            if (apply) {
                apply.disabled = true;
                apply.textContent = say('envp.apply', 'Promote');
            }
        },

        _loadDiff: function () {
            var self = this;
            if (!self._overlay) return;

            if (self._source === self._target) {
                self._resetBody();
                self._body('<div class="envp-note">' + esc(say('envp.sameEnv', 'Pick two different environments.')) + '</div>');
                return;
            }

            self._body('<div class="envp-note">' + esc(say('envp.loading', 'Reading both environments…')) + '</div>');

            window.DashboardApi.request(API + '/diff?source=' + encodeURIComponent(self._source) +
                '&target=' + encodeURIComponent(self._target), { silent: true })
                .then(function (payload) {
                    var diff = payload && payload.data ? payload.data : payload;
                    self._renderDiff(diff);
                })
                .catch(function (err) {
                    self._body('<div class="envp-note envp-note--bad">' +
                        esc((err && err.message) || say('envp.diffFailed', 'Could not read the difference')) + '</div>');
                });
        },

        _renderDiff: function (diff) {
            var self = this;
            if (!self._overlay) return;

            if (!diff) return;
            if (diff.identical) {
                self._body('<div class="envp-note">' + esc(say('envp.identical', 'The two environments already match.')) + '</div>');
                return;
            }

            var html = '<div class="envp-chips">' +
                self._chip('envp.routesAdded', 'routes added', diff.routesAdded, 'add') +
                self._chip('envp.routesRemoved', 'removed', diff.routesRemoved, 'del') +
                self._chip('envp.routesChanged', 'changed', diff.routesChanged, 'mod') +
                self._chip('envp.clustersAdded', 'clusters added', diff.clustersAdded, 'add') +
                self._chip('envp.clustersRemoved', 'removed', diff.clustersRemoved, 'del') +
                self._chip('envp.clustersChanged', 'changed', diff.clustersChanged, 'mod') +
                '</div><div class="envp-list">';

            (diff.routeChanges || []).forEach(function (entry) {
                html += self._routeRow(entry);
            });

            (diff.clusterChanges || []).forEach(function (entry) {
                html += self._clusterRow(entry);
            });

            html += '</div>';

            self._body(html);

            var apply = self._overlay.querySelector('.envp-apply');
            apply.disabled = false;
            apply.textContent = say('envp.applyTo', 'Promote to {env}', { env: self._target });
        },

        _chip: function (key, fallback, count, tone) {
            if (!count) return '';
            return '<span class="envp-chip envp-chip--' + tone + '">' +
                esc(say(key, fallback)) + ' ' + count + '</span>';
        },

        _routeRow: function (entry) {
            var tone = entry.kind === 'added' ? 'add' : entry.kind === 'removed' ? 'del' : 'mod';
            var icon = entry.kind === 'added' ? 'bi-plus-circle' : entry.kind === 'removed' ? 'bi-dash-circle' : 'bi-pencil';
            var html = '<div class="envp-row envp-row--' + tone + '">' +
                '<div class="envp-row__head"><i class="bi ' + icon + '"></i>' +
                '<code>' + esc(entry.routeId) + '</code></div>';

            if (entry.changes && entry.changes.length) {
                html += '<div class="envp-row__changes">';
                entry.changes.forEach(function (change) {
                    html += '<div class="envp-change">' + esc(change.field) + '：' +
                        '<span class="envp-before">' + esc(change.before || '∅') + '</span> → ' +
                        '<span class="envp-after">' + esc(change.after || '∅') + '</span></div>';
                });
                html += '</div>';
            }

            return html + '</div>';
        },

        _clusterRow: function (entry) {
            var tone = entry.kind === 'added' ? 'add' : entry.kind === 'removed' ? 'del' : 'mod';
            var html = '<div class="envp-row envp-row--' + tone + '">' +
                '<div class="envp-row__head"><i class="bi bi-hdd-network"></i>' +
                '<code>' + esc(entry.clusterId) + '</code></div><div class="envp-row__changes">';

            (entry.changes || []).forEach(function (change) {
                html += '<div class="envp-change">' + esc(change.field) + '：' +
                    '<span class="envp-before">' + esc(change.before || '∅') + '</span> → ' +
                    '<span class="envp-after">' + esc(change.after || '∅') + '</span></div>';
            });

            (entry.destinationsAdded || []).forEach(function (name) {
                html += '<div class="envp-change envp-change--add">+ ' + esc(name) + '</div>';
            });

            (entry.destinationsRemoved || []).forEach(function (name) {
                html += '<div class="envp-change envp-change--del">− ' + esc(name) + '</div>';
            });

            (entry.destinationsChanged || []).forEach(function (change) {
                html += '<div class="envp-change">' + esc(change.field) + '：' +
                    '<span class="envp-before">' + esc(change.before) + '</span> → ' +
                    '<span class="envp-after">' + esc(change.after) + '</span></div>';
            });

            return html + '</div></div>';
        },

        _prepare: function () {
            var self = this;
            var apply = self._overlay.querySelector('.envp-apply');
            apply.disabled = true;

            window.DashboardApi.request(API + '/promote/prepare', {
                method: 'POST',
                body: { source: self._source, target: self._target }
            })
                .then(function (payload) {
                    var prepared = payload && payload.data ? payload.data : payload;
                    self._prepared = prepared;

                    if (!prepared.canApply) {
                        self._showProblems(prepared.problems || []);
                        return;
                    }

                    self._confirm(prepared);
                })
                .catch(function (err) {
                    apply.disabled = false;
                    toast((err && err.message) || say('envp.prepareFailed', 'Could not prepare the promotion'), 'error');
                });
        },

        _showProblems: function (problems) {
            var html = '<div class="envp-note envp-note--bad">' +
                esc(say('envp.blocked', 'This promotion is blocked:')) + '</div><ul class="envp-problems">';

            problems.forEach(function (problem) { html += '<li>' + esc(problem) + '</li>'; });
            html += '</ul>';

            this._body(html);

            var apply = this._overlay && this._overlay.querySelector('.envp-apply');
            if (apply) apply.disabled = true;
        },

        _confirm: function (prepared) {
            var self = this;
            var diff = prepared.diff || {};
            var total = (diff.routeChanges || []).length + (diff.clusterChanges || []).length;

            var message = say('envp.confirm',
                'Write {n} change(s) into "{target}"?', { n: total, target: self._target });

            if (prepared.targetIsRuntime) {
                message += ' ' + say('envp.confirmLive',
                    'That environment is what the gateway is serving: the change affects live traffic immediately.',
                    { env: self._target });
            }

            var run = function () {
                window.DashboardApi.request(API + '/promote/apply', {
                    method: 'POST',
                    body: { token: prepared.token }
                })
                    .then(function (payload) {
                        var result = payload && payload.data ? payload.data : payload;
                        self.close();
                        toast(say('envp.applied', 'Promoted {routes} route(s) and {clusters} cluster(s) into {target}',
                            { routes: result.routes, clusters: result.clusters, target: result.target }), 'success');

                        // Every panel on the page now shows the target environment; patching it
                        // piecemeal would leave half the page on the old one.
                        setTimeout(function () { window.location.reload(); }, 600);
                    })
                    .catch(function (err) {
                        var apply = self._overlay && self._overlay.querySelector('.envp-apply');
                        if (apply) apply.disabled = false;
                        toast((err && err.message) || say('envp.applyFailed', 'Promotion failed'), 'error');
                    });
            };

            if (window.DashboardModals && window.DashboardModals.showConfirm) {
                window.DashboardModals.showConfirm(message, run, function () {
                    var apply = self._overlay && self._overlay.querySelector('.envp-apply');
                    if (apply) apply.disabled = false;
                }, {
                    title: say('envp.confirmTitle', 'Promote configuration'),
                    confirmText: say('envp.apply', 'Promote')
                });
            } else {
                run();
            }
        },

        _body: function (html) {
            if (!this._overlay) return;
            this._overlay.querySelector('.envp-body').innerHTML = html;
        },

        close: function () {
            if (!this._overlay) return;
            this._overlay.remove();
            this._overlay = null;
            this._prepared = null;
        }
    };

    window.DashboardEnvPromote = Promote;

    document.addEventListener('DOMContentLoaded', function () {
        Promote.init();
    });
})();
