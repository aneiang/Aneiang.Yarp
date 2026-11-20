/**
 * Metric alerts — threshold rules, live incidents, and the delivery warning.
 *
 * Lives on the Notifications page rather than in its own nav entry: the question this page answers
 * is "when am I told, and through which channel", and split across two pages the answer would be
 * spread across two screens.
 *
 * The panel leads with delivery state, not with the rule list. A rule that evaluates perfectly and
 * notifies nobody looks exactly like a healthy gateway, so "no platform subscribes to this event"
 * has to be the first thing on screen when it is true.
 */
(function () {
    'use strict';

    var API = '/api/alerts';
    var POLL_MS = 10000;

    var SEVERITY_COLOR = { Info: '#0ea5e9', Warning: '#f59e0b', Critical: '#ef4444' };
    var STATE_COLOR = { Pending: '#f59e0b', Firing: '#ef4444', OK: '#16a34a' };

    var Alerts = {
        _data: null,
        _timer: null,
        _loaded: false,
        _busy: false,

        t: function (key, params) {
            return window.__('malert.' + key, params);
        },

        esc: function (value) {
            var text = value === null || value === undefined ? '' : String(value);
            return window.DashboardUtils && DashboardUtils.escapeHtml
                ? DashboardUtils.escapeHtml(text)
                : text;
        },

        /* ---------------- lifecycle ---------------- */

        init: function () {
            var self = this;
            var tab = document.getElementById('tab-notif-alerts');
            if (!tab) return;

            tab.addEventListener('shown.bs.tab', function () {
                if (!self._loaded) {
                    self._loaded = true;
                    self.load();
                }
                self._startPolling();
            });

            tab.addEventListener('hidden.bs.tab', function () {
                self._stopPolling();
            });

            var refresh = document.getElementById('malert-refresh-btn');
            if (refresh) refresh.addEventListener('click', function () { self.load(); });

            var create = document.getElementById('malert-new-btn');
            if (create) create.addEventListener('click', function () { self.openEditor(null); });

            // Deep link: someone lands on #pane-notif-alerts directly.
            if (document.getElementById('pane-notif-alerts')?.classList.contains('active')) {
                this._loaded = true;
                this.load();
                this._startPolling();
            }
        },

        _startPolling: function () {
            var self = this;
            if (this._timer) return;
            this._timer = setInterval(function () {
                if (document.hidden) return;
                self.load(true);
            }, POLL_MS);
        },

        _stopPolling: function () {
            if (this._timer) {
                clearInterval(this._timer);
                this._timer = null;
            }
        },

        /* ---------------- data ---------------- */

        load: async function (silent) {
            if (this._busy) return;
            this._busy = true;
            try {
                var data = await window.DashboardApi.request(API, { silent: true });
                this._data = data;
                this.render();
            } catch (e) {
                if (!silent && window.showError) window.showError(e.message || this.t('loadFailed'));
                var body = document.getElementById('malert-rules-body');
                if (body) {
                    body.innerHTML = '<tr><td colspan="8" class="text-center text-danger py-4">' +
                        this.esc(e.message || this.t('loadFailed')) + '</td></tr>';
                }
            } finally {
                this._busy = false;
            }
        },

        _activeIncidents: function () {
            return ((this._data && this._data.incidents) || []).filter(function (i) { return i.isActive; });
        },

        _resolvedIncidents: function () {
            return ((this._data && this._data.incidents) || [])
                .filter(function (i) { return !i.isActive; })
                .slice(0, 5);
        },

        _incidentFor: function (ruleId) {
            var list = this._activeIncidents();
            for (var i = 0; i < list.length; i++) {
                if (list[i].ruleId === ruleId) return list[i];
            }
            return null;
        },

        /* ---------------- render ---------------- */

        render: function () {
            this._renderSummary();
            this._renderDelivery();
            this._renderActive();
            this._renderRules();
            this._renderResolved();
        },

        _renderSummary: function () {
            var el = document.getElementById('malert-summary');
            if (!el) return;
            var rules = (this._data.rules || []).length;
            var firing = this._activeIncidents().filter(function (i) {
                return i.state === 'Firing';
            }).length;
            el.textContent = this.t('summary.rules', { n: rules }) +
                (firing > 0 ? ' · ' + this.t('summary.firing', { n: firing }) : '');
            el.style.color = firing > 0 ? '#ef4444' : '';
        },

        _renderDelivery: function () {
            var host = document.getElementById('malert-delivery');
            if (!host) return;

            var delivery = this._data.delivery || {};
            var hasRules = (this._data.rules || []).length > 0;

            if (delivery.willDeliver) {
                host.innerHTML = hasRules
                    ? '<div style="display:flex;align-items:center;gap:8px;font-size:12px;color:#15803d;' +
                      'background:#f0fdf4;border:1px solid #bbf7d0;border-radius:8px;padding:8px 12px;margin-bottom:12px;">' +
                      '<i class="bi bi-check2-circle"></i>' +
                      this.esc(this.t('delivery.ok', { n: (delivery.subscribedPlatforms || []).length })) +
                      (delivery.subscribedPlatforms && delivery.subscribedPlatforms.length
                          ? ' <code style="font-size:11px;">' + this.esc(delivery.subscribedPlatforms.join(', ')) + '</code>'
                          : '') +
                      '</div>'
                    : '';
                return;
            }

            // Nothing is listening. Say so loudly — this is the difference between "quiet" and "dead".
            host.innerHTML =
                '<div style="display:flex;align-items:center;gap:10px;font-size:12px;color:#b45309;' +
                'background:#fffbeb;border:1px solid #fde68a;border-radius:8px;padding:8px 12px;margin-bottom:12px;">' +
                '<i class="bi bi-exclamation-triangle"></i>' +
                '<span style="flex:1;">' + this.esc(this.t('delivery.none')) + '</span>' +
                '<button type="button" class="btn btn-sm btn-outline-warning" id="malert-goto-events">' +
                this.esc(this.t('delivery.fix')) + '</button></div>';

            var jump = document.getElementById('malert-goto-events');
            if (jump) {
                jump.addEventListener('click', function () {
                    var target = document.getElementById('tab-notif-events');
                    if (target && window.bootstrap) new bootstrap.Tab(target).show();
                });
            }
        },

        _renderActive: function () {
            var host = document.getElementById('malert-active');
            if (!host) return;

            var incidents = this._activeIncidents();
            if (incidents.length === 0) {
                host.innerHTML = '<div style="font-size:12px;color: var(--text-faint);padding:6px 0 12px;">' +
                    '<i class="bi bi-shield-check me-1"></i>' + this.esc(this.t('active.empty')) + '</div>';
                return;
            }

            var self = this;
            var html = '<div style="font-size:12px;font-weight:600;color: var(--text-secondary);margin-bottom:8px;">' +
                this.esc(this.t('active.title')) + '</div>';

            incidents.forEach(function (incident) {
                var color = SEVERITY_COLOR[incident.severity] || '#64748b';
                var stateLabel = incident.isMuted
                    ? self.t('state.muted')
                    : self.t('state.' + incident.state);

                html +=
                    '<div style="display:flex;align-items:flex-start;gap:12px;padding:10px 14px;margin-bottom:8px;' +
                    'border:1px solid #fecaca;border-left:3px solid ' + color + ';border-radius:8px;background:#fff;">' +
                    '<div style="flex:1;min-width:0;">' +
                    '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">' +
                    '<span style="width:8px;height:8px;border-radius:50%;background:' + color + ';' +
                    (incident.state === 'Firing' ? 'box-shadow:0 0 0 4px ' + color + '22;' : '') + '"></span>' +
                    '<span style="font-weight:600;font-size:13px;">' + self.esc(incident.ruleName) + '</span>' +
                    '<span style="font-size:10px;padding:1px 6px;border-radius:999px;background: var(--surface-3);color: var(--text-secondary);">' +
                    self.esc(stateLabel) + '</span>' +
                    '<span style="font-size:10px;padding:1px 6px;border-radius:999px;background: var(--info-bg);color: var(--info-color);">' +
                    self.esc(self._scopeLabel(incident.scopeKind, incident.scope)) + '</span>' +
                    '</div>' +
                    '<div style="font-size:12px;color: var(--text-secondary);margin-top:4px;">' +
                    '<strong style="color:' + color + ';">' + self.esc(incident.lastValueLabel) + '</strong>' +
                    ' / ' + self.esc(self.t('col.threshold')) + ' ' + self.esc(incident.thresholdLabel) +
                    ' · ' + incident.lastSamples + ' ' + self.esc(self.t('samples')) +
                    ' · ' + self._duration(incident.elapsedSeconds) +
                    '</div>' +
                    '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;">' + self.esc(incident.lastReason || '') + '</div>' +
                    '</div>' +
                    '<div style="display:flex;gap:6px;flex-shrink:0;">' +
                    '<button type="button" class="btn btn-sm btn-outline-secondary malert-mute" data-id="' +
                    self.esc(incident.id) + '" data-name="' + self.esc(incident.ruleName) + '">' +
                    '<i class="bi bi-bell-slash"></i></button>' +
                    '<button type="button" class="btn btn-sm btn-outline-primary malert-edit" data-rule="' +
                    self.esc(incident.ruleId) + '"><i class="bi bi-pencil"></i></button>' +
                    '</div></div>';
            });

            host.innerHTML = html;

            host.querySelectorAll('.malert-mute').forEach(function (btn) {
                btn.addEventListener('click', function () { self.mute(btn.getAttribute('data-id')); });
            });
            host.querySelectorAll('.malert-edit').forEach(function (btn) {
                btn.addEventListener('click', function () { self.openEditor(btn.getAttribute('data-rule')); });
            });
        },

        _renderRules: function () {
            var body = document.getElementById('malert-rules-body');
            if (!body) return;

            var rules = this._data.rules || [];
            if (rules.length === 0) {
                body.innerHTML = '<tr><td colspan="8" class="text-center py-5">' +
                    '<div style="font-size:13px;font-weight:600;color: var(--text-muted);">' + this.esc(this.t('empty')) + '</div>' +
                    '<div style="font-size:12px;color: var(--text-faint);margin-top:4px;">' + this.esc(this.t('emptyHelp')) + '</div>' +
                    '</td></tr>';
                return;
            }

            var self = this;
            body.innerHTML = rules.map(function (rule) {
                var incident = self._incidentFor(rule.id);
                var stateLabel = !rule.enabled
                    ? self.t('state.disabled')
                    : incident
                        ? (incident.isMuted ? self.t('state.muted') : self.t('state.' + incident.state))
                        : self.t('state.ok');
                var stateColor = !rule.enabled
                    ? '#94a3b8'
                    : incident
                        ? (incident.isMuted ? '#94a3b8' : (STATE_COLOR[incident.state] || '#64748b'))
                        : STATE_COLOR.OK;

                var sevColor = SEVERITY_COLOR[rule.severity] || '#64748b';

                return '<tr>' +
                    '<td><div style="font-weight:600;font-size:13px;">' + self.esc(rule.name) + '</div>' +
                    '<div style="font-size:11px;color: var(--text-faint);">' + self.esc(rule.createdBy || '') + '</div></td>' +
                    '<td style="font-size:12px;">' + self.esc(self.t('kind.' + rule.kind)) + '</td>' +
                    '<td style="font-size:12px;color: var(--text-secondary);">' + self.esc(self._scopeLabel(rule.scopeKind, rule.scope)) + '</td>' +
                    '<td><code style="font-size:12px;">' + self.esc(rule.thresholdLabel) + '</code></td>' +
                    '<td style="font-size:11px;color: var(--text-muted);">' + rule.forSeconds + 's / ' +
                    rule.windowSeconds + 's / ' + rule.minSamples + '</td>' +
                    '<td><span style="font-size:11px;padding:1px 7px;border-radius:999px;background:' + sevColor +
                    '18;color:' + sevColor + ';">' + self.esc(self.t('severity.' + rule.severity)) + '</span></td>' +
                    '<td><span style="font-size:11px;padding:1px 7px;border-radius:999px;background:' + stateColor +
                    '18;color:' + stateColor + ';">' + self.esc(stateLabel) + '</span></td>' +
                    '<td class="text-end" style="white-space:nowrap;">' +
                    '<button type="button" class="btn btn-sm btn-outline-secondary malert-test" data-id="' +
                    self.esc(rule.id) + '" title="' + self.esc(self.t('action.test')) + '"><i class="bi bi-send"></i></button> ' +
                    '<button type="button" class="btn btn-sm btn-outline-primary malert-edit" data-rule="' +
                    self.esc(rule.id) + '" title="' + self.esc(self.t('action.edit')) + '"><i class="bi bi-pencil"></i></button> ' +
                    '<button type="button" class="btn btn-sm btn-outline-danger malert-delete" data-id="' +
                    self.esc(rule.id) + '" data-name="' + self.esc(rule.name) + '" title="' +
                    self.esc(self.t('action.delete')) + '"><i class="bi bi-trash3"></i></button>' +
                    '</td></tr>';
            }).join('');

            body.querySelectorAll('.malert-test').forEach(function (btn) {
                btn.addEventListener('click', function () { self.test(btn.getAttribute('data-id')); });
            });
            body.querySelectorAll('.malert-edit').forEach(function (btn) {
                btn.addEventListener('click', function () { self.openEditor(btn.getAttribute('data-rule')); });
            });
            body.querySelectorAll('.malert-delete').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    self.remove(btn.getAttribute('data-id'), btn.getAttribute('data-name'));
                });
            });
        },

        _renderResolved: function () {
            var host = document.getElementById('malert-resolved');
            if (!host) return;

            var resolved = this._resolvedIncidents();
            if (resolved.length === 0) {
                host.innerHTML = '';
                return;
            }

            var self = this;
            host.innerHTML =
                '<div style="font-size:12px;font-weight:600;color: var(--text-secondary);margin-bottom:6px;">' +
                this.esc(this.t('resolved.title')) + '</div>' +
                '<div style="font-size:12px;color: var(--text-muted);">' +
                resolved.map(function (incident) {
                    var reason = self.t('reason.' + (incident.resolveReason || 'recovered'));
                    return '<div style="padding:4px 0;border-bottom: 1px dashed var(--border-color);">' +
                        '<i class="bi bi-check2-circle me-1" style="color: var(--success-color);"></i>' +
                        self.esc(incident.ruleName) +
                        ' <span style="color: var(--text-faint);">· ' + self.esc(reason) + ' · ' +
                        self.esc(self._duration(incident.elapsedSeconds)) + '</span></div>';
                }).join('') + '</div>';
        },

        _scopeLabel: function (scopeKind, scope) {
            if (scopeKind === 'Global') return this.t('scope.Global');
            return scope + ' · ' + this.t('scope.' + scopeKind);
        },

        _duration: function (seconds) {
            var value = Number(seconds) || 0;
            if (value < 60) return value + 's';
            if (value < 3600) return Math.floor(value / 60) + 'm ' + (value % 60) + 's';
            return Math.floor(value / 3600) + 'h ' + Math.floor((value % 3600) / 60) + 'm';
        },

        /* ---------------- actions ---------------- */

        test: async function (id) {
            try {
                await window.DashboardApi.request(API + '/rules/' + encodeURIComponent(id) + '/test', {
                    method: 'POST'
                });
                if (window.showToast) window.showToast(this.t('testQueued'), 'success');
            } catch (e) {
                if (window.showError) window.showError(e.message);
            }
        },

        remove: async function (id, name) {
            if (!window.confirm(this.t('deleteConfirm', { name: name }))) return;
            try {
                await window.DashboardApi.request(API + '/rules/' + encodeURIComponent(id), {
                    method: 'DELETE'
                });
                if (window.showToast) window.showToast(this.t('deleteDone'), 'success');
                await this.load(true);
            } catch (e) {
                if (window.showError) window.showError(e.message);
            }
        },

        mute: async function (incidentId) {
            var raw = window.prompt(this.t('mutePrompt'), this.t('muteDefault'));
            if (raw === null) return;

            var minutes = parseInt(raw, 10);
            if (isNaN(minutes) || minutes < 1) return;

            try {
                await window.DashboardApi.request(
                    API + '/incidents/' + encodeURIComponent(incidentId) + '/mute',
                    { method: 'POST', body: { minutes: minutes } });
                if (window.showToast) window.showToast(this.t('muteDone', { n: minutes }), 'success');
                await this.load(true);
            } catch (e) {
                if (window.showError) window.showError(e.message);
            }
        },

        /* ---------------- editor ---------------- */

        _kindMeta: function (kind) {
            var kinds = (this._data && this._data.kinds) || [];
            for (var i = 0; i < kinds.length; i++) {
                if (kinds[i].kind === kind) return kinds[i];
            }
            return { kind: kind, unit: 'count', defaultThreshold: 1, supportsRouteScope: true };
        },

        openEditor: function (ruleId) {
            if (!this._data) return;

            var self = this;
            var rule = null;
            if (ruleId) {
                var rules = this._data.rules || [];
                for (var i = 0; i < rules.length; i++) {
                    if (rules[i].id === ruleId) { rule = rules[i]; break; }
                }
            }

            var defaults = this._data.defaults || {};
            var kind = rule ? rule.kind : 'ErrorRate';
            var meta = this._kindMeta(kind);

            var modalId = 'malert-editor';
            var existing = document.getElementById(modalId);
            if (existing) existing.remove();

            var kindOptions = (this._data.kinds || []).map(function (k) {
                return '<option value="' + k.kind + '"' + (k.kind === kind ? ' selected' : '') + '>' +
                    self.esc(self.t('kind.' + k.kind)) + '</option>';
            }).join('');

            var scopeKindOptions = ['Global', 'Cluster', 'Route'].map(function (sk) {
                var selected = rule ? rule.scopeKind === sk : sk === 'Global';
                return '<option value="' + sk + '"' + (selected ? ' selected' : '') + '>' +
                    self.esc(self.t('scope.' + sk)) + '</option>';
            }).join('');

            var severityOptions = ['Info', 'Warning', 'Critical'].map(function (sv) {
                var selected = rule ? rule.severity === sv : sv === 'Warning';
                return '<option value="' + sv + '"' + (selected ? ' selected' : '') + '>' +
                    self.esc(self.t('severity.' + sv)) + '</option>';
            }).join('');

            var thresholdInput = rule
                ? (meta.unit === 'ratio' ? (rule.threshold * 100) : rule.threshold)
                : (meta.unit === 'ratio' ? Math.round(meta.defaultThreshold * 100) : meta.defaultThreshold);

            var html =
                '<div class="modal fade" id="' + modalId + '" tabindex="-1">' +
                '<div class="modal-dialog modal-dialog-centered modal-lg"><div class="modal-content" ' +
                'style="border-radius:16px;border:none;box-shadow:0 25px 50px rgba(0,0,0,0.25);overflow:hidden;">' +
                '<div class="modal-header" style="background: linear-gradient(135deg,var(--surface-2),var(--surface-4));border-bottom: 1px solid var(--border-color);padding:18px 24px;">' +
                '<h5 class="modal-title" style="font-weight:600;font-size:16px;display:flex;align-items:center;gap:10px;">' +
                '<span style="display:inline-flex;align-items:center;justify-content:center;width:32px;height:32px;border-radius:8px;' +
                'background:linear-gradient(135deg,#f59e0b,#ef4444);color:#fff;font-size:16px;"><i class="bi bi-activity"></i></span>' +
                self.esc(rule ? self.t('form.titleEdit') : self.t('form.titleNew')) + '</h5>' +
                '<button type="button" class="btn-close" data-bs-dismiss="modal"></button></div>' +
                '<div class="modal-body" style="padding:20px 24px;display:flex;flex-direction:column;gap:14px;">' +

                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.name')) + '</label>' +
                '<input class="form-control form-control-sm" id="malert-name" maxlength="80" ' +
                'placeholder="' + self.esc(self.t('form.namePlaceholder')) + '" value="' +
                (rule ? self.esc(rule.name) : '') + '"></div>' +

                '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px;">' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.kind')) + '</label>' +
                '<select class="form-select form-select-sm" id="malert-kind">' + kindOptions + '</select></div>' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.scopeKind')) + '</label>' +
                '<select class="form-select form-select-sm" id="malert-scopekind">' + scopeKindOptions + '</select></div>' +
                '</div>' +

                '<div id="malert-scope-wrap"><label class="form-label" style="font-size:12px;font-weight:600;">' +
                self.esc(self.t('form.scope')) + '</label>' +
                '<select class="form-select form-select-sm" id="malert-scope"></select>' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;" id="malert-scope-hint"></div></div>' +

                '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px;">' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;" id="malert-threshold-label">' +
                self.esc(self.t('form.threshold')) + '</label>' +
                '<input type="number" step="any" class="form-control form-control-sm" id="malert-threshold" value="' +
                thresholdInput + '">' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;" id="malert-threshold-hint"></div></div>' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.severity')) + '</label>' +
                '<select class="form-select form-select-sm" id="malert-severity">' + severityOptions + '</select></div>' +
                '</div>' +

                '<div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:12px;">' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.window')) + '</label>' +
                '<input type="number" class="form-control form-control-sm" id="malert-window" min="10" max="3600" value="' +
                (rule ? rule.windowSeconds : (defaults.windowSeconds || 60)) + '">' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;">' + self.esc(self.t('form.window.hint')) + '</div></div>' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.for')) + '</label>' +
                '<input type="number" class="form-control form-control-sm" id="malert-for" min="0" max="3600" value="' +
                (rule ? rule.forSeconds : (defaults.forSeconds || 60)) + '">' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;">' + self.esc(self.t('form.for.hint')) + '</div></div>' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.minSamples')) + '</label>' +
                '<input type="number" class="form-control form-control-sm" id="malert-minsamples" min="1" value="' +
                (rule ? rule.minSamples : (defaults.minSamples || 20)) + '">' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;">' + self.esc(self.t('form.minSamples.hint')) + '</div></div>' +
                '</div>' +

                '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px;align-items:end;">' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('form.renotify')) + '</label>' +
                '<input type="number" class="form-control form-control-sm" id="malert-renotify" min="0" max="1440" value="' +
                (rule ? rule.renotifyMinutes : 30) + '">' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;">' + self.esc(self.t('form.renotify.hint')) + '</div></div>' +
                '<div style="display:flex;flex-direction:column;gap:6px;padding-bottom:4px;">' +
                '<label style="display:flex;align-items:center;gap:8px;font-size:12px;cursor:pointer;">' +
                '<input type="checkbox" class="form-check-input mt-0" id="malert-notify-resolve"' +
                (!rule || rule.notifyOnResolve ? ' checked' : '') + '>' + self.esc(self.t('form.notifyOnResolve')) + '</label>' +
                '<label style="display:flex;align-items:center;gap:8px;font-size:12px;cursor:pointer;">' +
                '<input type="checkbox" class="form-check-input mt-0" id="malert-enabled"' +
                (!rule || rule.enabled ? ' checked' : '') + '>' + self.esc(self.t('form.enabled')) + '</label>' +
                '</div></div>' +

                '<div style="font-size:11px;color: var(--text-muted);background: var(--surface-2);border: 1px solid var(--border-color);border-radius:8px;padding:8px 12px;">' +
                '<i class="bi bi-info-circle me-1"></i>' + self.esc(self.t('hint.pipeline')) + '</div>' +
                '</div>' +

                '<div class="modal-footer" style="background: var(--surface-2);border-top: 1px solid var(--border-color);padding:14px 24px;gap:8px;">' +
                '<button type="button" class="btn btn-secondary btn-sm" data-bs-dismiss="modal">' +
                self.esc(self.t('form.cancel')) + '</button>' +
                '<button type="button" class="btn btn-primary btn-sm" id="malert-save">' +
                '<i class="bi bi-check2 me-1"></i>' + self.esc(self.t('form.save')) + '</button>' +
                '</div></div></div></div>';

            document.body.insertAdjacentHTML('beforeend', html);

            var modalEl = document.getElementById(modalId);
            var modal = new bootstrap.Modal(modalEl);
            modal.show();
            modalEl.addEventListener('hidden.bs.modal', function () { modalEl.remove(); });

            // The threshold label carries the unit because the same input means a ratio, a number of
            // seconds or a count depending on the metric — typing 5 into a ratio field means 500%.
            function syncKind() {
                var selected = document.getElementById('malert-kind').value;
                var selectedMeta = self._kindMeta(selected);
                var labelKey = selectedMeta.unit === 'ratio'
                    ? 'form.threshold.ratio'
                    : selectedMeta.unit === 'seconds' ? 'form.threshold.seconds' : 'form.threshold.count';
                var hintKey = selectedMeta.unit === 'ratio'
                    ? 'form.threshold.hintRatio'
                    : selectedMeta.unit === 'seconds' ? 'form.threshold.hintSeconds' : 'form.threshold.hintCount';

                document.getElementById('malert-threshold-label').textContent = self.t(labelKey);
                document.getElementById('malert-threshold-hint').textContent = self.t(hintKey);

                // A route-scoped circuit rule matches nothing: there is no per-route breaker.
                var routeOption = document.querySelector('#malert-scopekind option[value="Route"]');
                if (routeOption) {
                    routeOption.disabled = !selectedMeta.supportsRouteScope;
                    if (!selectedMeta.supportsRouteScope && document.getElementById('malert-scopekind').value === 'Route') {
                        document.getElementById('malert-scopekind').value = 'Cluster';
                    }
                }
                document.getElementById('malert-scope-hint').textContent =
                    selectedMeta.supportsRouteScope ? '' : self.t('hint.scope');

                syncScope();
            }

            function syncScope() {
                var scopeKind = document.getElementById('malert-scopekind').value;
                var wrap = document.getElementById('malert-scope-wrap');
                var select = document.getElementById('malert-scope');
                var scopes = self._data.scopes || { clusters: [], routes: [] };

                if (scopeKind === 'Global') {
                    wrap.style.display = 'none';
                    return;
                }

                wrap.style.display = '';
                var list = scopeKind === 'Cluster' ? (scopes.clusters || []) : (scopes.routes || []);
                var current = rule ? rule.scope : null;

                if (list.length === 0) {
                    select.innerHTML = '<option value="">' + self.esc(self.t('form.scopePlaceholder')) + '</option>';
                    return;
                }

                select.innerHTML = list.map(function (id) {
                    return '<option value="' + self.esc(id) + '"' +
                        (current === id ? ' selected' : '') + '>' + self.esc(id) + '</option>';
                }).join('');
            }

            document.getElementById('malert-kind').addEventListener('change', syncKind);
            document.getElementById('malert-scopekind').addEventListener('change', syncScope);
            syncKind();

            document.getElementById('malert-save').addEventListener('click', async function () {
                var selectedKind = document.getElementById('malert-kind').value;
                var selectedMeta = self._kindMeta(selectedKind);
                var rawThreshold = parseFloat(document.getElementById('malert-threshold').value);

                if (isNaN(rawThreshold)) {
                    if (window.showWarning) window.showWarning(self.t('form.threshold'));
                    return;
                }

                var payload = {
                    id: rule ? rule.id : '',
                    name: document.getElementById('malert-name').value,
                    kind: selectedKind,
                    scopeKind: document.getElementById('malert-scopekind').value,
                    scope: document.getElementById('malert-scopekind').value === 'Global'
                        ? null
                        : document.getElementById('malert-scope').value,
                    // Percent in the form, ratio on the wire: only one of the two can be the truth.
                    threshold: selectedMeta.unit === 'ratio' ? rawThreshold / 100 : rawThreshold,
                    windowSeconds: parseInt(document.getElementById('malert-window').value, 10),
                    forSeconds: parseInt(document.getElementById('malert-for').value, 10),
                    minSamples: parseInt(document.getElementById('malert-minsamples').value, 10),
                    severity: document.getElementById('malert-severity').value,
                    renotifyMinutes: parseInt(document.getElementById('malert-renotify').value, 10) || 0,
                    notifyOnResolve: document.getElementById('malert-notify-resolve').checked,
                    enabled: document.getElementById('malert-enabled').checked
                };

                try {
                    await window.DashboardApi.request(API + '/rules', { method: 'POST', body: payload });
                    modal.hide();
                    if (window.showToast) window.showToast(self.t('saveDone'), 'success');
                    await self.load(true);
                } catch (e) {
                    // Validation errors come back joined by ';' — show them all at once.
                    if (window.showError) window.showError(e.message);
                }
            });
        }
    };

    window.AlertRulesModule = Alerts;

    // The page may load this script after DOMContentLoaded has already fired, depending on where
    // the tag lands, so cover both orders rather than relying on the host page to call init().
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { Alerts.init(); });
    } else {
        Alerts.init();
    }
})();
