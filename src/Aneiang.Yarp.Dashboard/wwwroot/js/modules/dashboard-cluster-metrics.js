/**
 * Cluster Metrics Module - Per-cluster statistics
 */
(function() {
    'use strict';

    /* Escape dynamic text before injecting it into generated table markup. */
    function escText(value) {
        var text = String(value == null ? '' : value);
        return window.DashboardUtils && window.DashboardUtils.escapeHtml
            ? window.DashboardUtils.escapeHtml(text)
            : text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    var ClusterMetricsModule = {
        initialized: false,
        autoRefreshInterval: null,
        init: function() { if (this.initialized) return; this.initialized = true; },
        destroy: function() { if (this.autoRefreshInterval) { clearInterval(this.autoRefreshInterval); this.autoRefreshInterval = null; } this.initialized = false; },

        load: async function() {
            var container = document.getElementById('cm-content');
            if (!container) return;
            try {
                container.innerHTML = '<div class="loading-state"><div class="loading-spinner"></div><div class="loading-text">' + __('common.loading') + '</div></div>';
                var plugin = await window.DashboardApi.getPlugin('cluster-metrics');
                var clusters = await window.DashboardApi.getClusters().catch(function() { return []; });
                this.render(plugin, clusters, container);
                this.updateRefreshTime();
            } catch (error) {
                container.innerHTML = '<div class="alert alert-danger">' + __('common.loadFailed') + ': ' + (error.message || error) + '</div>';
            }
        },

        render: function(plugin, clusters, container) {
            var enabled = plugin && plugin.enabled;
            var clusterCount = Array.isArray(clusters) ? clusters.length : 0;
            var html = '';
            // Compact meta line: status · version · clusters · type
            html += '<div class="pc-meta-line">';
            html += '<span class="pc-meta-item' + (enabled ? ' pc-meta-item--ok' : '') + '"><i class="bi bi-' + (enabled ? 'check-circle' : 'x-circle') + '"></i><span class="pc-meta-num">' + (enabled ? __('common.enabled') : __('common.disabled')) + '</span></span>';
            html += '<span class="pc-meta-item"><i class="bi bi-tag"></i><span class="pc-meta-num">v' + (plugin ? plugin.version || '1.0' : '-') + '</span></span>';
            html += '<span class="pc-meta-item"><i class="bi bi-diagram-3"></i><span class="pc-meta-num">' + clusterCount + '</span><span class="pc-meta-label">' + __('clusterMetrics.totalClusters') + '</span></span>';
            html += '<span class="pc-meta-item"><i class="bi bi-box"></i><span class="pc-meta-label">' + (plugin && plugin.isBuiltIn ? __('common.builtIn') : __('common.external')) + '</span></span>';
            html += '</div>';
            html += '<div class="table-responsive"><table class="table table-hover"><thead><tr><th>' + __('clusterMetrics.clusterId') + '</th><th>' + __('clusterMetrics.destCount') + '</th><th>' + __('clusterMetrics.healthStatus') + '</th><th>' + __('clusterMetrics.loadBalancing') + '</th></tr></thead><tbody>';
            if (Array.isArray(clusters) && clusters.length > 0) {
                clusters.forEach(function(c) {
                    var destCount = c.destinations ? c.destinations.length : 0;
                    var healthy = Number(c.healthyCount) || 0;
                    var unknown = Number(c.unknownCount) || 0;
                    var unhealthy = Number(c.unhealthyCount) || 0;
                    // Any unhealthy destination marks the cluster unhealthy; healthy nodes must not mask partial failures.
                    var badgeCss, badgeIcon, statusKey;
                    if (unhealthy > 0) {
                        badgeCss = 'bg-danger'; badgeIcon = 'bi-x-circle-fill';
                        statusKey = 'index.cluster.health.unhealthy';
                    } else if (healthy > 0) {
                        badgeCss = 'bg-success'; badgeIcon = 'bi-check-circle-fill';
                        statusKey = 'index.cluster.health.healthy';
                    } else {
                        badgeCss = 'bg-secondary'; badgeIcon = 'bi-question-circle-fill';
                        statusKey = 'index.cluster.health.unknown';
                    }
                    var statusLabel = __(statusKey);
                    // Counts follow the "Healthy / Unknown / Unhealthy" order used elsewhere in the dashboard.
                    var countsHint = escText(__('index.stat.health'));
                    var healthHtml = '<span class="badge ' + badgeCss + '"><i class="bi ' + badgeIcon + '"></i> ' + escText(statusLabel) + '</span>'
                        + (destCount > 0 ? '<span class="text-muted small ms-1" title="' + countsHint + '">' + healthy + '/' + unknown + '/' + unhealthy + '</span>' : '');
                    var lb = c.loadBalancingPolicy || 'PowerOfTwoChoices';
                    html += '<tr><td><code>' + escText(c.clusterId || c.id || '-') + '</code></td>';
                    html += '<td>' + destCount + '</td>';
                    html += '<td>' + healthHtml + '</td>';
                    html += '<td>' + escText(lb) + '</td></tr>';
                });
            } else {
                html += '<tr><td colspan="4" class="text-center text-muted py-3">' + __('clusterMetrics.noData') + '</td></tr>';
            }
            html += '</tbody></table></div>';
            container.innerHTML = html;
        },

        updateRefreshTime: function() { var el = document.getElementById('cm-refresh-time'); if (el) el.textContent = new Date().toLocaleTimeString(); }
    };

    window.ClusterMetricsModule = ClusterMetricsModule;
})();
