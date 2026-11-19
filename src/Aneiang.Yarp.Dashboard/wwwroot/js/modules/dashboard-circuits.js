/**
 * Circuit Breaker Module - Status viewer and management
 */
(function() {
    'use strict';

    var CircuitModule = {
        name: 'circuit',
        initialized: false,
        autoRefreshInterval: null,

        init: function() {
            if (this.initialized) return;
            this.setupEvents();
            this.initialized = true;
        },

        setupEvents: function() {
            var self = this;
            document.addEventListener('dashboard:ready', function() {
                if (self.autoRefreshInterval) clearInterval(self.autoRefreshInterval);
                self.autoRefreshInterval = setInterval(function() {
                    self.load();
                }, 15000);
            });
            document.addEventListener('dashboard:localeChange', function() { self.load(); });
        },

        destroy: function() {
            if (this.autoRefreshInterval) {
                clearInterval(this.autoRefreshInterval);
                this.autoRefreshInterval = null;
            }
            this.initialized = false;
        },

        load: async function() {
            try {
                var container = document.getElementById('circuit-content');
                if (!container) return;

                /* Status and thresholds live behind two endpoints on purpose: /status only
                   reports circuits that have actually been evaluated, so a cluster that is
                   fully configured but has never carried traffic would silently look
                   unconfigured. /config is the declared policy; joining them is what lets
                   the page answer "what is configured" and "what is happening" apart. */
                var results = await Promise.all([
                    window.DashboardApi.getCircuitBreakerStatus(),
                    /* Thresholds are supporting detail — a failure here must not blank the
                       live status table, so it degrades to "unknown". */
                    window.DashboardApi.getCircuitBreakerConfig().catch(function(error) {
                        console.warn('[Circuit] Threshold load failed:', error);
                        return null;
                    })
                ]);
                this.render(results[0], results[1], container);
                this.updateRefreshTime();
            } catch (error) {
                console.error('[Circuit] Load failed:', error);
                var container = document.getElementById('circuit-content');
                if (container) {
                    container.innerHTML = '<div class="alert alert-danger">' + __('circuit.loadFailed') + '</div>';
                }
            }
        },

        render: function(data, config, container) {
            window.DashboardDOM.clear(container);

            // API returns an array of CircuitStateInfo objects
            var entries = Array.isArray(data) ? data : [];

            var closedCount = 0, openCount = 0, halfOpenCount = 0;
            entries.forEach(function(circuit) {
                var state = circuit.status;
                if (state === 'Closed') closedCount++;
                else if (state === 'Open') openCount++;
                else if (state === 'HalfOpen') halfOpenCount++;
            });

            // Compact meta line: closed · open · half-open
            var summaryHtml =
                '<div class="pc-meta-line">' +
                    '<span class="pc-meta-item pc-meta-item--ok"><i class="bi bi-check-circle"></i><span class="pc-meta-num">' + closedCount + '</span><span class="pc-meta-label">' + __('circuit.healthy') + '</span></span>' +
                    '<span class="pc-meta-item' + (openCount > 0 ? ' pc-meta-item--danger' : '') + '"><i class="bi bi-x-circle"></i><span class="pc-meta-num">' + openCount + '</span><span class="pc-meta-label">' + __('circuit.tripped') + '</span></span>' +
                    '<span class="pc-meta-item' + (halfOpenCount > 0 ? ' pc-meta-item--warn' : '') + '"><i class="bi bi-arrow-repeat"></i><span class="pc-meta-num">' + halfOpenCount + '</span><span class="pc-meta-label">' + __('circuit.recovering') + '</span></span>' +
                '</div>';

            var html = summaryHtml;

            if (entries.length === 0) {
                html += '<div class="text-center py-5">' +
                            '<i class="bi bi-lightning-charge text-muted" style="font-size:48px;"></i>' +
                            '<p class="text-muted mt-3">' + __('circuit.empty') + '</p>' +
                            '<p class="text-muted small">' + __('circuit.emptyHelp') + '</p>' +
                        '</div>';
            } else {
                var self = this;
                var rows = entries.map(function(circuit) {
                    var clusterName = circuit.clusterName || circuit.clusterKeySnapshot || circuit.key || '-';
                    var destinationId = circuit.destinationKeySnapshot && circuit.destinationKeySnapshot !== 'any'
                        ? circuit.destinationKeySnapshot
                        : null;

                    var statusClass = circuit.status === 'Closed' ? 'bg-success' :
                                     circuit.status === 'Open' ? 'bg-danger' : 'bg-warning';
                    var statusText = circuit.status === 'Closed' ? __('circuit.status.closed') :
                                     circuit.status === 'Open' ? __('circuit.status.open') : __('circuit.status.halfOpen');
                    var stateIcon = circuit.status === 'Closed' ? 'bi-check-circle-fill text-success' :
                                   circuit.status === 'Open' ? 'bi-x-circle-fill text-danger' : 'bi-arrow-repeat text-warning';

                    var openedAt = circuit.openedAt
                        ? window.DashboardI18n.formatDate(circuit.openedAt)
                        : '-';
                    var lastAccessed = circuit.lastAccessedAt
                        ? window.DashboardI18n.formatDate(circuit.lastAccessedAt)
                        : '-';
                    var recoverySec = self.formatRecoveryTimeout(circuit);

                    return '<tr class="align-middle">' +
                        '<td><i class="bi ' + stateIcon + ' me-2"></i><strong>' + window.DashboardUtils.escapeHtml(clusterName) + '</strong>' +
                            (circuit.clusterKeySnapshot && circuit.clusterKeySnapshot !== clusterName
                                ? '<br><small class="text-muted">ID: ' + window.DashboardUtils.escapeHtml(circuit.clusterKeySnapshot) + '</small>'
                                : '') +
                        '</td>' +
                        '<td><code>' + (destinationId ? window.DashboardUtils.escapeHtml(destinationId) : '-') + '</code></td>' +
                        '<td><span class="badge ' + statusClass + '">' + statusText + '</span></td>' +
                        '<td>' + circuit.consecutiveFailures + ' / ' + circuit.failureThreshold + '</td>' +
                        '<td>' + recoverySec + '</td>' +
                        '<td class="text-muted small">' + openedAt + '</td>' +
                        '<td class="text-muted small">' + lastAccessed + '</td>' +
                    '</tr>';
                }).join('');

                html += '<div class="table-responsive">' +
                            '<table class="table table-hover align-middle">' +
                                '<thead>' +
                                    '<tr>' +
                                        '<th>' + __('circuit.cluster') + '</th>' +
                                        '<th>' + __('circuit.destination') + '</th>' +
                                        '<th>' + __('circuit.state') + '</th>' +
                                        '<th>' + __('circuit.failures') + '</th>' +
                                        '<th>' + __('circuit.recoveryTimeout') + '</th>' +
                                        '<th>' + __('circuit.openedAt') + '</th>' +
                                        '<th>' + __('circuit.lastAccessed') + '</th>' +
                                    '</tr>' +
                                '</thead>' +
                                '<tbody>' + rows + '</tbody>' +
                            '</table>' +
                        '</div>';
            }

            html += this.renderThresholds(entries, config);

            container.innerHTML = html;
        },

        /* Declared thresholds, joined with live state where one exists.
         *
         * This is the half of the picture the status table cannot show: a cluster whose
         * circuit has never been evaluated (no traffic since boot) has no entry in /status,
         * so before this section existed the only way to confirm "the threshold I set is
         * actually in effect" was to read the binding configuration of the plugin. */
        renderThresholds: function(entries, config) {
            var map = (config && typeof config === 'object') ? config : {};
            var clusterIds = Object.keys(map);

            var head = '<div class="mt-4 pt-3" style="border-top:1px solid var(--border-light,#f1f5f9);">' +
                '<div class="d-flex align-items-center justify-content-between mb-2 flex-wrap gap-2">' +
                    '<div style="font-weight:600;font-size:13px;"><i class="bi bi-sliders2 me-2"></i>' +
                        __('circuit.thresholds.title') + '</div>' +
                    '<div class="small text-muted">' + __('circuit.thresholds.hint') + '</div>' +
                '</div>';

            if (!clusterIds.length) {
                return head + '<div class="text-muted small py-3">' + __('circuit.thresholds.empty') + '</div></div>';
            }

            /* Live state is keyed by whatever the status payload happened to record; match
               on every alias so a cluster that is both configured and evaluated shows once. */
            var live = {};
            entries.forEach(function(c) {
                [c.clusterKeySnapshot, c.clusterName, c.key].forEach(function(k) {
                    if (k) live[k] = c;
                });
            });

            var self = this;
            var rows = clusterIds.sort().map(function(clusterId) {
                var cfg = map[clusterId] || {};
                var state = live[clusterId];
                var enabled = cfg.enabled !== false;

                var stateBadge = state
                    ? '<span class="badge ' + (state.status === 'Closed' ? 'bg-success' :
                        state.status === 'Open' ? 'bg-danger' : 'bg-warning') + '">' +
                      (state.status === 'Closed' ? __('circuit.status.closed') :
                       state.status === 'Open' ? __('circuit.status.open') : __('circuit.status.halfOpen')) +
                      '</span>'
                    : '<span class="badge bg-light text-muted" title="' + __('circuit.thresholds.notTriggeredHint') + '">' +
                      __('circuit.thresholds.notTriggered') + '</span>';

                var ratio = typeof cfg.failureRatio === 'number' ? Math.round(cfg.failureRatio * 100) : null;
                var trigger = ratio === null
                    ? '-'
                    : __('circuit.thresholds.triggerValue')
                        .replace('{ratio}', String(ratio))
                        .replace('{min}', String(cfg.minimumThroughput != null ? cfg.minimumThroughput : '-'))
                        .replace('{window}', String(cfg.samplingDurationSeconds != null ? cfg.samplingDurationSeconds : '-'));

                var codes = Array.isArray(cfg.failureStatusCodes) && cfg.failureStatusCodes.length
                    ? cfg.failureStatusCodes.join(' ')
                    : '-';

                return '<tr class="align-middle' + (enabled ? '' : ' text-muted') + '">' +
                    '<td><strong>' + window.DashboardUtils.escapeHtml(clusterId) + '</strong>' +
                        (enabled ? '' : ' <span class="badge bg-secondary">' + __('circuit.thresholds.disabled') + '</span>') +
                    '</td>' +
                    '<td>' + stateBadge + '</td>' +
                    '<td>' + (cfg.failureThreshold != null ? cfg.failureThreshold : '-') + '</td>' +
                    '<td>' + (cfg.recoveryTimeoutSeconds != null ? cfg.recoveryTimeoutSeconds + 's' : '-') + '</td>' +
                    '<td>' + (cfg.halfOpenMaxAttempts != null ? cfg.halfOpenMaxAttempts : '-') + '</td>' +
                    '<td class="small">' + trigger + '</td>' +
                    '<td class="small text-muted">' + codes + '</td>' +
                '</tr>';
            }).join('');

            return head +
                '<div class="table-responsive">' +
                    '<table class="table table-sm align-middle mb-0">' +
                        '<thead><tr>' +
                            '<th>' + __('circuit.cluster') + '</th>' +
                            '<th>' + __('circuit.state') + '</th>' +
                            '<th>' + __('circuit.thresholds.failureThreshold') + '</th>' +
                            '<th>' + __('circuit.thresholds.recovery') + '</th>' +
                            '<th>' + __('circuit.thresholds.halfOpen') + '</th>' +
                            '<th>' + __('circuit.thresholds.trigger') + '</th>' +
                            '<th>' + __('circuit.thresholds.statusCodes') + '</th>' +
                        '</tr></thead>' +
                        '<tbody>' + rows + '</tbody>' +
                    '</table>' +
                '</div>' +
            '</div>';
        },

        formatRecoveryTimeout: function(circuit) {
            if (typeof circuit.recoveryTimeoutSeconds === 'number') {
                return circuit.recoveryTimeoutSeconds + 's';
            }

            if (typeof circuit.recoveryTimeout === 'number') {
                return Math.round(circuit.recoveryTimeout / 1000) + 's';
            }

            if (typeof circuit.recoveryTimeout === 'string') {
                var parts = circuit.recoveryTimeout.split(':');
                if (parts.length === 3) {
                    var seconds = Math.round((parseFloat(parts[0]) || 0) * 3600 + (parseFloat(parts[1]) || 0) * 60 + (parseFloat(parts[2]) || 0));
                    return seconds + 's';
                }
                return circuit.recoveryTimeout;
            }

            return '-';
        },

        resetAll: function() {
            var self = this;
            window.DashboardModals.showConfirm(__('circuit.resetConfirm'), async function() {
                try {
                    await window.DashboardApi.resetCircuitBreakers();
                    window.DashboardModals.showSuccess(__('circuit.resetSuccess'));
                    await self.load();
                } catch (error) {
                    console.error('[Circuit] Reset failed:', error);
                    window.DashboardModals.showError(__('circuit.resetFailed'));
                }
            }, null, { danger: true });
        },

        updateRefreshTime: function() {
            var el = document.getElementById('circuit-refresh-time');
            if (el) {
                el.textContent = window.DashboardI18n.formatDate(new Date());
            }
        }
    };

    if (window.DashboardApp) {
        window.DashboardApp.registerModule('circuit', CircuitModule);
    }
    window.CircuitModule = CircuitModule;
})();
