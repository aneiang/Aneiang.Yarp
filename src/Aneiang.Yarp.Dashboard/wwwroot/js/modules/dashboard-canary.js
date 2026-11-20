/**
 * Canary rollouts — start a staged rollout, watch it, stop it.
 *
 * Kept out of dashboard-clusters.js on purpose: that module is already ~1.8k lines of table
 * rendering, and the rollout panel is an independent concern that polls on its own cadence.
 *
 * The panel is the only place in the dashboard that shows a *decision* rather than a state, so it
 * shows the numbers the gate used (requests, 5xx rate on both sides) instead of just "healthy".
 */
(function () {
    'use strict';

    var API = '/api/canary';
    var POLL_MS = 5000;
    var FINISHED_VISIBLE_MS = 30 * 60 * 1000;
    var DISMISS_KEY = 'dashboard.canary.dismissed';

    var Canary = {
        _timer: null,
        _data: null,
        _signature: '',
        _busy: false,

        t: function (key, params) {
            return window.__('canary.' + key, params);
        },

        esc: function (value) {
            var text = value === null || value === undefined ? '' : String(value);
            return window.DashboardUtils && DashboardUtils.escapeHtml
                ? DashboardUtils.escapeHtml(text)
                : text;
        },

        init: function () {
            this.refresh();
            this._startPolling();
        },

        _startPolling: function () {
            var self = this;
            if (this._timer) clearInterval(this._timer);
            this._timer = setInterval(function () {
                if (document.hidden) return;
                self.refresh();
            }, POLL_MS);
        },

        refresh: async function () {
            if (this._busy) return;
            try {
                var data = await window.DashboardApi.request(API + '/rollouts', { silent: true });
                // Skip the re-render when nothing moved: polling every 5s must not reset focus or
                // scroll position on a page the operator is actively using.
                var signature = JSON.stringify(data.rollouts || []);
                this._data = data;
                if (signature === this._signature) {
                    this._updateCountdown();
                    return;
                }
                this._signature = signature;
                this.render();
            } catch (e) {
                // A failed poll must never break the page. Leave the last known state on screen.
            }
        },

        _updateCountdown: function () {
            var el = document.getElementById('canary-countdown');
            if (!el) return;
            var active = this._active();
            if (!active || active.phase !== 'Observing') return;

            var remaining = el.getAttribute('data-remaining');
            var seconds = remaining === null ? NaN : parseInt(remaining, 10);
            if (isNaN(seconds)) return;

            if (seconds > 0) {
                el.setAttribute('data-remaining', String(seconds - 1));
                el.textContent = this.t('remaining', { seconds: seconds });
            }
        },

        _active: function () {
            var list = (this._data && this._data.rollouts) || [];
            return list.filter(function (r) { return r.isActive; })[0] || null;
        },

        _dismissed: function () {
            try { return window.localStorage.getItem(DISMISS_KEY) || ''; } catch (e) { return ''; }
        },

        dismiss: function (id) {
            try { window.localStorage.setItem(DISMISS_KEY, id); } catch (e) { /* private mode */ }
            this._signature = '';
            this.render();
        },

        render: function () {
            var host = document.getElementById('canary-panel');
            if (!host) return;

            var list = (this._data && this._data.rollouts) || [];
            var active = this._active();
            var latest = list[0];

            var showLatest = !active && latest && latest.endedAt
                && (Date.now() - new Date(latest.endedAt).getTime()) < FINISHED_VISIBLE_MS
                && this._dismissed() !== latest.id;

            if (!active && !showLatest) {
                host.style.display = 'none';
                host.innerHTML = '';
                return;
            }

            var rollout = active || latest;
            host.style.display = '';
            host.innerHTML = rollout.isActive ? this._renderActive(rollout) : this._renderFinished(rollout);
        },

        _phaseKey: function (phase) {
            return 'phase' + phase;
        },

        _phaseColor: function (phase) {
            switch (phase) {
                case 'Observing': return { fg: '#0369a1', bg: '#e0f2fe', border: '#bae6fd' };
                case 'Paused': return { fg: '#b45309', bg: '#fef3c7', border: '#fde68a' };
                case 'Completed': return { fg: '#15803d', bg: '#dcfce7', border: '#bbf7d0' };
                case 'RolledBack': return { fg: '#b91c1c', bg: '#fee2e2', border: '#fecaca' };
                default: return { fg: '#475569', bg: '#f1f5f9', border: '#e2e8f0' };
            }
        },

        _codeLabel: function (code) {
            if (!code) return '';
            var known = {
                healthy: 1, insufficientSamples: 1, noCanaryTraffic: 1, baselineDegraded: 1,
                canaryDegraded: 1, canaryDegradedRestoreFailed: 1, restarted: 1, resumed: 1,
                awaitingPromotion: 1, completed: 1, aborted: 1, advanced: 1, planFailed: 1,
                applyFailed: 1, abortedRestoreFailed: 1
            };
            var normalized = String(code).replace(/-/g, '');
            if (!known[normalized]) return '';
            return this.t('code.' + normalized);
        },

        _renderSteps: function (rollout) {
            var self = this;
            return (rollout.steps || []).map(function (percent, index) {
                var state = index < rollout.stepIndex ? 'done'
                    : index === rollout.stepIndex ? 'current' : 'pending';
                var colors = state === 'current'
                    ? 'background:#2563eb;color:#fff;border-color:#2563eb;'
                    : state === 'done'
                        ? 'background:#def7ec;color:#046c4e;border-color:#bcf0da;'
                        : 'background:#f8fafc;color:#94a3b8;border-color:#e2e8f0;';
                var icon = state === 'done' ? '<i class="bi bi-check2" style="font-size:10px;"></i>' : '';
                return '<span style="display:inline-flex;align-items:center;gap:3px;padding:2px 8px;border:1px solid;' +
                    'border-radius:999px;font-size:11px;font-weight:600;' + colors + '">' + icon + percent + '%</span>';
            }).join('');
        },

        _errorCell: function (requests, failures, rate) {
            if (!requests) {
                return '<span style="color: var(--text-faint);">' + this.esc(this.t('noTraffic')) + '</span>';
            }
            var percent = (rate === null || rate === undefined) ? 0 : (rate * 100);
            var color = percent >= 20 ? '#dc2626' : percent >= 5 ? '#d97706' : '#15803d';
            return '<strong style="color:' + color + ';">' + percent.toFixed(1) + '%</strong>' +
                '<span style="color: var(--text-faint);margin-left:6px;">(' + failures + '/' + requests + ')</span>';
        },

        _liveSection: function (rollout) {
            var live = rollout.live;
            var canaryCount = (rollout.canaryDestinations || []).length;
            var canaryShare = this._share(rollout);
            var rows = [
                {
                    label: this.t('canarySide'),
                    detail: (rollout.canaryDestinations || []).join(', '),
                    share: canaryShare,
                    dot: '#2563eb'
                },
                {
                    label: this.t('baselineSide'),
                    detail: this.t('baselineCount', { count: countBaseline(rollout) }),
                    share: canaryShare === null ? null : (100 - canaryShare),
                    dot: '#94a3b8'
                }
            ];

            var html = '<div style="display:grid;grid-template-columns:1fr 1fr;gap:10px;">';
            rows.forEach(function (row, index) {
                var requests = 0, failures = 0, rate = null;
                if (live) {
                    requests = index === 0 ? live.canaryRequests : live.baselineRequests;
                    failures = index === 0 ? live.canaryFailures : live.baselineFailures;
                    rate = index === 0 ? live.canaryErrorRate : live.baselineErrorRate;
                }
                html += '<div style="border:1px solid var(--border-color);border-radius:8px;padding:10px 12px;background: var(--bg-secondary,var(--surface-2));">' +
                    '<div style="display:flex;align-items:center;gap:6px;margin-bottom:6px;">' +
                    '<span style="width:8px;height:8px;border-radius:50%;background:' + row.dot + ';"></span>' +
                    '<span style="font-size:12px;font-weight:600;color:var(--text-primary);">' + Canary.esc(row.label) + '</span>' +
                    (row.share === null ? '' : '<span style="margin-left:auto;font-size:11px;color: var(--text-muted);">' + Canary.esc(Canary.t('share')) + ' ' + row.share + '%</span>') +
                    '</div>' +
                    '<div style="font-size:11px;color: var(--text-faint);margin-bottom:6px;word-break:break-all;">' + Canary.esc(row.detail) + '</div>' +
                    '<div style="font-size:12px;color:var(--text-secondary);">' +
                    Canary.esc(Canary.t('requests')) + ' <strong>' + requests + '</strong>' +
                    '<span style="color: var(--text-secondary);margin:0 6px;">|</span>' +
                    Canary.esc(Canary.t('errorRate')) + ' ' + Canary._errorCell(requests, failures, rate) +
                    '</div></div>';
            }, this);
            html += '</div>';

            function countBaseline(r) {
                var total = Object.keys(r.currentWeights || {}).length;
                return Math.max(0, total - canaryCount);
            }

            return html;
        },

        _share: function (rollout) {
            var weights = rollout.currentWeights || {};
            var canary = rollout.canaryDestinations || [];
            var total = 0, part = 0;
            Object.keys(weights).forEach(function (id) {
                var weight = parseInt(weights[id], 10);
                if (isNaN(weight) || weight < 0) return;
                total += weight;
                if (canary.indexOf(id) >= 0) part += weight;
            });
            if (total <= 0) return null;
            return Math.round(part / total * 100);
        },

        _renderActive: function (rollout) {
            var colors = this._phaseColor(rollout.phase);
            var isObserving = rollout.phase === 'Observing';
            var codeLabel = this._codeLabel(rollout.lastCode);

            var html = '<div class="card-header" style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">' +
                '<span style="display:inline-flex;align-items:center;justify-content:center;width:28px;height:28px;' +
                'border-radius:8px;background:linear-gradient(135deg,#0ea5e9,#22d3ee);color:#fff;font-size:13px;">' +
                '<i class="bi bi-signpost-split"></i></span>' +
                '<strong style="font-size:14px;">' + this.esc(this.t('panelTitle')) + '</strong>' +
                '<code style="font-size:12px;color:var(--text-secondary);">' + this.esc(rollout.clusterId) + '</code>' +
                '<span style="padding:1px 8px;border-radius:999px;font-size:11px;font-weight:600;color:' + colors.fg +
                ';background:' + colors.bg + ';border:1px solid ' + colors.border + ';">' +
                this.esc(this.t(this._phaseKey(rollout.phase))) + '</span>' +
                (isObserving
                    ? '<span id="canary-countdown" data-remaining="' + rollout.secondsRemaining + '" ' +
                      'style="font-size:12px;color: var(--text-muted);">' + this.esc(this.t('remaining', { seconds: rollout.secondsRemaining })) + '</span>'
                    : '') +
                '<span style="margin-left:auto;font-size:12px;color: var(--text-muted);">' +
                this.esc(this.t('step', { current: rollout.stepIndex + 1, total: rollout.stepCount })) + '</span>' +
                '</div>';

            html += '<div style="padding:14px 16px;display:flex;flex-direction:column;gap:12px;">';

            // Step ramp
            html += '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">' +
                this._renderSteps(rollout) +
                '<span style="margin-left:auto;font-size:11px;color: var(--text-faint);">' +
                this.esc(this.t('threshold')) + ' ' + Math.round(rollout.errorRateThreshold * 100) + '% · ' +
                this.esc(this.t('minSamples')) + ' ' + rollout.minSamples + ' · ' +
                this.esc(this.t('extensions')) + ' ' + rollout.windowExtensions + '</span>' +
                '</div>';

            // Live split
            html += this._liveSection(rollout);

            // Last verdict
            if (rollout.lastReason) {
                var verdict = rollout.lastVerdict || '';
                var dot = verdict === 'Rollback' ? '#dc2626'
                    : verdict === 'Pause' ? '#d97706'
                        : verdict === 'Advance' ? '#15803d' : '#64748b';
                html += '<div style="border-left:3px solid ' + dot + ';background:#fbfdff;padding:8px 12px;border-radius:6px;">' +
                    '<div style="font-size:12px;color:var(--text-primary);">' +
                    '<span style="display:inline-block;width:6px;height:6px;border-radius:50%;background:' + dot + ';margin-right:6px;"></span>' +
                    (codeLabel ? this.esc(codeLabel) : this.esc(verdict)) + '</div>' +
                    '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;font-family:monospace;">' +
                    this.esc(rollout.lastReason) + '</div></div>';
            }

            // Actions
            html += '<div style="display:flex;gap:8px;flex-wrap:wrap;">';
            if (rollout.phase === 'Paused') {
                html += '<button class="btn btn-sm btn-primary" onclick="CanaryModule.resume(\'' + rollout.id + '\')">' +
                    '<i class="bi bi-play-fill me-1"></i>' + this.esc(this.t('resume')) + '</button>';
            }
            html += '<button class="btn btn-sm btn-outline-primary" onclick="CanaryModule.promote(\'' + rollout.id + '\')">' +
                '<i class="bi bi-skip-forward-fill me-1"></i>' + this.esc(this.t('promote')) + '</button>';
            html += '<button class="btn btn-sm btn-outline-danger" onclick="CanaryModule.abort(\'' + rollout.id + '\')">' +
                '<i class="bi bi-arrow-counterclockwise me-1"></i>' + this.esc(this.t('abort')) + '</button>';
            html += '</div>';

            html += this._renderHistory(rollout);

            html += '</div>';
            return html;
        },

        _renderFinished: function (rollout) {
            var colors = this._phaseColor(rollout.phase);
            var codeLabel = this._codeLabel(rollout.lastCode);

            var html = '<div class="card-header" style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">' +
                '<span style="display:inline-flex;align-items:center;justify-content:center;width:28px;height:28px;' +
                'border-radius:8px;background:' + colors.bg + ';color:' + colors.fg + ';font-size:13px;">' +
                '<i class="bi bi-signpost-split"></i></span>' +
                '<strong style="font-size:14px;">' + this.esc(this.t(this._phaseKey(rollout.phase))) + '</strong>' +
                '<code style="font-size:12px;color:var(--text-secondary);">' + this.esc(rollout.clusterId) + '</code>' +
                '<span style="margin-left:auto;">' +
                '<button class="btn btn-sm btn-outline-secondary" onclick="CanaryModule.dismiss(\'' + rollout.id + '\')">' +
                '<i class="bi bi-x-lg"></i></button></span></div>';

            html += '<div style="padding:14px 16px;display:flex;flex-direction:column;gap:10px;">';
            if (codeLabel) {
                html += '<div style="font-size:12px;color:var(--text-primary);">' + this.esc(codeLabel) + '</div>';
            }
            if (rollout.lastReason) {
                html += '<div style="font-size:11px;color: var(--text-faint);font-family:monospace;">' + this.esc(rollout.lastReason) + '</div>';
            }
            html += this._renderHistory(rollout);
            html += '</div>';
            return html;
        },

        _renderHistory: function (rollout) {
            var history = rollout.history || [];
            if (!history.length) return '';

            var self = this;
            var rows = history.slice(-6).map(function (record) {
                var verdictColor = record.verdict === 'Rollback' ? '#dc2626'
                    : record.verdict === 'Pause' ? '#d97706'
                        : record.verdict === 'Advance' ? '#15803d' : '#64748b';
                var canaryRate = record.canaryErrorRate === null ? '-' : (record.canaryErrorRate * 100).toFixed(1) + '%';
                var baselineRate = record.baselineErrorRate === null ? '-' : (record.baselineErrorRate * 100).toFixed(1) + '%';
                return '<tr>' +
                    '<td style="padding:4px 8px;color: var(--text-muted);">' + record.canaryPercent + '%</td>' +
                    '<td style="padding:4px 8px;color:var(--text-secondary);">' + record.canaryRequests + '</td>' +
                    '<td style="padding:4px 8px;color:' + verdictColor + ';font-weight:600;">' + canaryRate + '</td>' +
                    '<td style="padding:4px 8px;color: var(--text-faint);">' + baselineRate + '</td>' +
                    '<td style="padding:4px 8px;color:' + verdictColor + ';">' + self.esc(self.t('verdict' + record.verdict)) + '</td>' +
                    '<td style="padding:4px 8px;color:#94a3b8;font-family:monospace;font-size:10px;max-width:420px;' +
                    'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;" title="' + self.esc(record.reason) + '">' +
                    self.esc(record.reason) + '</td></tr>';
            }).join('');

            return '<div>' +
                '<div style="font-size:11px;color: var(--text-faint);margin-bottom:4px;">' + this.esc(this.t('history')) + '</div>' +
                '<table style="width:100%;border-collapse:collapse;font-size:11px;">' +
                '<thead><tr style="text-align:left;color: var(--text-faint);">' +
                '<th style="padding:2px 8px;font-weight:500;">' + this.esc(this.t('colStep')) + '</th>' +
                '<th style="padding:2px 8px;font-weight:500;">' + this.esc(this.t('colCanaryReq')) + '</th>' +
                '<th style="padding:2px 8px;font-weight:500;">' + this.esc(this.t('colCanary5xx')) + '</th>' +
                '<th style="padding:2px 8px;font-weight:500;">' + this.esc(this.t('colBaseline5xx')) + '</th>' +
                '<th style="padding:2px 8px;font-weight:500;">' + this.esc(this.t('colVerdict')) + '</th>' +
                '<th style="padding:2px 8px;font-weight:500;">' + this.esc(this.t('colReason')) + '</th>' +
                '</tr></thead><tbody>' + rows + '</tbody></table></div>';
        },

        /* ---------------- actions ---------------- */

        promote: async function (id) {
            await this._act(API + '/rollouts/' + encodeURIComponent(id) + '/promote', null, 'promoted');
        },

        resume: async function (id) {
            await this._act(API + '/rollouts/' + encodeURIComponent(id) + '/resume', null, 'resumed');
        },

        abort: function (id) {
            var self = this;
            window.showConfirm(this.t('abortConfirm'), function () {
                self._act(API + '/rollouts/' + encodeURIComponent(id) + '/abort', { reason: 'manual abort' }, 'aborted');
            });
        },

        _act: async function (url, body, messageKey) {
            if (this._busy) return;
            this._busy = true;
            try {
                var result = await window.DashboardApi.request(url, { method: 'POST', body: body || {} });
                if (window.showSuccess) window.showSuccess(this.t(messageKey));
                if (result && result.currentPercent !== undefined) {
                    // Keep the version token fresh: every step write bumps it, and a stale token
                    // would turn the operator's next cluster edit into a spurious 409.
                    window.DashboardApi.loadConfigVersion();
                }
                this._signature = '';
                await this.refresh();
                if (window.DashboardApp?.modules?.clusters?.loadClusters) {
                    window.DashboardApp.modules.clusters.loadClusters();
                }
            } catch (e) {
                if (window.showError) window.showError(e.message || this.t('actionFailed'));
            } finally {
                this._busy = false;
            }
        },

        /* ---------------- start dialog ---------------- */

        openStartDialog: async function (clusterId) {
            var self = this;
            var info;
            try {
                info = await window.DashboardApi.request(API + '/candidates/' + encodeURIComponent(clusterId));
            } catch (e) {
                if (window.showError) window.showError(e.message || this.t('actionFailed'));
                return;
            }

            if (!info.canStart) {
                if (window.showWarning) window.showWarning(info.blockedReason || this.t('cannotStart'));
                else if (window.showToast) window.showToast(info.blockedReason || this.t('cannotStart'), 'warning');
                return;
            }

            var defaults = (this._data && this._data.defaults) || {};
            var steps = (defaults.steps || [10, 30, 50, 100]).join(',');
            var windowSeconds = defaults.observationWindowSeconds || 120;
            var thresholdPercent = Math.round((defaults.errorRateThreshold || 0.2) * 100);
            var minSamples = defaults.minSamples || 50;

            var modalId = 'canary-start-modal';
            var existing = document.getElementById(modalId);
            if (existing) existing.remove();

            var destRows = info.destinations.map(function (dest) {
                var checked = dest.isCanary ? ' checked' : '';
                return '<label style="display:flex;align-items:center;gap:8px;padding:6px 8px;border:1px solid var(--border-color);' +
                    'border-radius:8px;cursor:pointer;font-size:12px;">' +
                    '<input type="checkbox" class="form-check-input mt-0 canary-dest" value="' + self.esc(dest.id) + '"' + checked + '>' +
                    '<code style="font-size:12px;">' + self.esc(dest.id) + '</code>' +
                    '<span style="color: var(--text-faint);flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + self.esc(dest.address || '') + '</span>' +
                    (dest.tag ? '<span style="font-size:10px;padding:1px 6px;border-radius:999px;background: var(--info-bg);color: var(--info-color);">' + self.esc(dest.tag) + '</span>' : '') +
                    '<span style="font-size:11px;color: var(--text-muted);">w=' + dest.weight + '</span>' +
                    '</label>';
            }).join('');

            var html =
                '<div class="modal fade" id="' + modalId + '" tabindex="-1" data-bs-backdrop="static">' +
                '<div class="modal-dialog modal-dialog-centered modal-lg"><div class="modal-content" ' +
                'style="border-radius:16px;border:none;box-shadow:0 25px 50px rgba(0,0,0,0.25);overflow:hidden;">' +
                '<div class="modal-header" style="background: linear-gradient(135deg,var(--surface-2),var(--surface-4));border-bottom: 1px solid var(--border-color);padding:18px 24px;">' +
                '<h5 class="modal-title" style="font-weight:600;font-size:16px;display:flex;align-items:center;gap:10px;">' +
                '<span style="display:inline-flex;align-items:center;justify-content:center;width:32px;height:32px;border-radius:8px;' +
                'background:linear-gradient(135deg,#0ea5e9,#22d3ee);color:#fff;font-size:16px;"><i class="bi bi-signpost-split"></i></span>' +
                self.esc(self.t('startTitle')) + '</h5>' +
                '<button type="button" class="btn-close" data-bs-dismiss="modal"></button></div>' +
                '<div class="modal-body" style="padding:20px 24px;display:flex;flex-direction:column;gap:14px;">' +
                '<div style="font-size:12px;color: var(--text-muted);background: var(--surface-2);border: 1px solid var(--border-color);border-radius:8px;padding:8px 12px;">' +
                '<i class="bi bi-info-circle me-1"></i>' + self.esc(self.t('startHelp')) + '</div>' +
                '<div><div style="font-size:12px;font-weight:600;margin-bottom:6px;">' + self.esc(self.t('canaryDestinations')) + '</div>' +
                '<div style="display:grid;grid-template-columns:1fr 1fr;gap:6px;max-height:200px;overflow:auto;">' + destRows + '</div></div>' +
                '<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px;">' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('stepsLabel')) + '</label>' +
                '<input class="form-control form-control-sm" id="canary-steps" value="' + steps + '">' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;">' + self.esc(self.t('stepsHelp')) + '</div></div>' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('windowLabel')) + '</label>' +
                '<input type="number" class="form-control form-control-sm" id="canary-window" min="10" value="' + windowSeconds + '"></div>' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('thresholdLabel')) + '</label>' +
                '<input type="number" class="form-control form-control-sm" id="canary-threshold" min="1" max="100" value="' + thresholdPercent + '">' +
                '<div style="font-size:11px;color: var(--text-faint);margin-top:2px;">' + self.esc(self.t('baselineTrusted')) + '</div></div>' +
                '<div><label class="form-label" style="font-size:12px;font-weight:600;">' + self.esc(self.t('minSamplesLabel')) + '</label>' +
                '<input type="number" class="form-control form-control-sm" id="canary-minsamples" min="1" value="' + minSamples + '"></div>' +
                '</div></div>' +
                '<div class="modal-footer" style="background: var(--surface-2);border-top: 1px solid var(--border-color);padding:14px 24px;justify-content:flex-end;gap:8px;">' +
                '<button type="button" class="btn btn-secondary btn-sm" data-bs-dismiss="modal">' + self.esc(self.t('cancelBtn')) + '</button>' +
                '<button type="button" class="btn btn-primary btn-sm" id="canary-start-confirm">' +
                '<i class="bi bi-play-fill me-1"></i>' + self.esc(self.t('startBtn')) + '</button>' +
                '</div></div></div></div>';

            document.body.insertAdjacentHTML('beforeend', html);
            var modalEl = document.getElementById(modalId);
            var modal = new bootstrap.Modal(modalEl, { backdrop: 'static' });
            modal.show();

            modalEl.addEventListener('hidden.bs.modal', function () { modalEl.remove(); });

            document.getElementById('canary-start-confirm').addEventListener('click', async function () {
                var canary = Array.prototype.slice
                    .call(modalEl.querySelectorAll('.canary-dest:checked'))
                    .map(function (el) { return el.value; });

                if (canary.length === 0) {
                    if (window.showWarning) window.showWarning(self.t('needCanaryDestination'));
                    return;
                }

                var stepsValue = (document.getElementById('canary-steps').value || '')
                    .split(',').map(function (s) { return parseInt(s.trim(), 10); })
                    .filter(function (n) { return !isNaN(n); });

                var payload = {
                    clusterId: info.clusterId,
                    canaryDestinations: canary,
                    steps: stepsValue,
                    observationWindowSeconds: parseInt(document.getElementById('canary-window').value, 10) || undefined,
                    errorRateThreshold: (parseInt(document.getElementById('canary-threshold').value, 10) || 20) / 100,
                    minSamples: parseInt(document.getElementById('canary-minsamples').value, 10) || undefined,
                    autoAdvance: true
                };

                var button = this;
                button.disabled = true;
                try {
                    var result = await window.DashboardApi.request(API + '/rollouts', { method: 'POST', body: payload });
                    modal.hide();
                    if (window.showSuccess) {
                        window.showSuccess(self.t('started', { percent: (result.steps || [])[0] }));
                    }
                    window.DashboardApi.loadConfigVersion();
                    self._signature = '';
                    await self.refresh();
                    if (window.DashboardApp?.modules?.clusters?.loadClusters) {
                        window.DashboardApp.modules.clusters.loadClusters();
                    }
                } catch (e) {
                    if (window.showError) window.showError(e.message || self.t('actionFailed'));
                } finally {
                    button.disabled = false;
                }
            });
        }
    };

    window.CanaryModule = Canary;
})();
