/**
 * Plugin Management Module - Plugin list and toggle
 */
(function() {
    'use strict';

    var PluginModule = {

        /** Try i18n lookup, fall back to provided default if key not found */
        _t: function(key, fallback) {
            var text = window.DashboardI18n && window.DashboardI18n.translations ? window.DashboardI18n.translations[key] : null;
            return text || fallback || key;
        },

        name: 'plugin',
        initialized: false,
        autoRefreshInterval: null,

        /* plugin id -> dashboard page that visualises its output */
        MONITOR_PAGES: {
            'circuit-breaker': 'circuits',
            'proxy-log': 'logs',
            'traffic-metrics': 'traffic',
            'cluster-metrics': 'cluster-metrics'
        },

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
                }, 30000);
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
                var container = document.getElementById('plugin-content');
                if (!container) return;

                window.DashboardDOM.showLoading(container, __('plugin.loading'));

                var data = await window.DashboardApi.getPlugins();
                this.render(data, container);
                this.updateRefreshTime();
            } catch (error) {
                console.error('[Plugin] Load failed:', error);
                var container = document.getElementById('plugin-content');
                if (container) {
                    container.innerHTML = '<div class="alert alert-danger">' + __('plugin.loadFailed') + '</div>';
                }
            }
        },

        getPluginIcon: function(pluginId) {
            if (pluginId && pluginId.toLowerCase().includes('circuit')) return 'bi-lightning-charge';
            if (pluginId && pluginId.toLowerCase().includes('retry')) return 'bi-arrow-repeat';
            if (pluginId && pluginId.toLowerCase().includes('waf')) return 'bi-shield-lock';
            if (pluginId && pluginId.toLowerCase().includes('rate')) return 'bi-speedometer2';
            if (pluginId && pluginId.toLowerCase().includes('fault')) return 'bi-bug';
            return 'bi-puzzle';
        },

        getPluginColor: function(pluginId) {
            if (pluginId && pluginId.toLowerCase().includes('circuit')) return '#6366f1';
            if (pluginId && pluginId.toLowerCase().includes('retry')) return '#0ea5e9';
            if (pluginId && pluginId.toLowerCase().includes('waf')) return '#f59e0b';
            if (pluginId && pluginId.toLowerCase().includes('rate')) return '#0ea5e9';
            if (pluginId && pluginId.toLowerCase().includes('fault')) return '#dc2626';
            return '#64748b';
        },

        /** Binding scope chips: a plugin can only be bound to the target types it declares. */
        getScopeHtml: function(scopes) {
            var list = Array.isArray(scopes) ? scopes : [];
            if (!list.length) return '<span class="text-muted">-</span>';
            return list.map(function(scope) {
                var isCluster = String(scope).toLowerCase().indexOf('cluster') >= 0;
                return '<span class="plg-scope plg-scope--' + (isCluster ? 'cluster' : 'route') + '">' +
                    (isCluster ? __('pluginPage.cluster') : __('pluginPage.route')) + '</span>';
            }).join('');
        },

        /** Deep link into Plugin Center, which owns this plugin's configuration tab. */
        getConfigHref: function(pluginId) {
            var base = (window.__dashboard && window.__dashboard.basePath) || '';
            return base + '/plugin-center#' + encodeURIComponent(pluginId);
        },

        getPageHref: function(page) {
            var base = (window.__dashboard && window.__dashboard.basePath) || '';
            return base + '/' + page;
        },

        render: function(data, container) {
            window.DashboardDOM.clear(container);

            var self = this;
            var plugins = Array.isArray(data) ? data : (data && data.plugins) || [];
            this.plugins = plugins;
            var enabledCount = plugins.filter(function(p) { return p.enabled; }).length;

            var summaryHtml =
                '<div class="pc-meta-line">' +
                    '<span class="pc-meta-item"><i class="bi bi-grid-fill"></i><span class="pc-meta-num">' + plugins.length + '</span><span class="pc-meta-label">' + this._t('plugin.totalShort', 'Total') + '</span></span>' +
                    '<span class="pc-meta-item pc-meta-item--ok"><i class="bi bi-check-circle"></i><span class="pc-meta-num">' + enabledCount + '</span><span class="pc-meta-label">' + __('plugin.enabled') + '</span></span>' +
                    '<span class="pc-meta-item' + ((plugins.length - enabledCount) > 0 ? ' pc-meta-item--warn' : '') + '"><i class="bi bi-x-circle"></i><span class="pc-meta-num">' + (plugins.length - enabledCount) + '</span><span class="pc-meta-label">' + __('plugin.disabled') + '</span></span>' +
                '</div>';

            if (plugins.length === 0) {
                container.innerHTML = summaryHtml +
                    '<div class="text-center py-5">' +
                        '<i class="bi bi-puzzle text-muted" style="font-size:48px;"></i>' +
                        '<p class="text-muted mt-3">' + __('plugin.empty') + '</p>' +
                        '<p class="text-muted small">' + __('plugin.emptyHelp') + '</p>' +
                    '</div>';
                return;
            }

            var rows = plugins.map(function(plugin) {
                var icon = self.getPluginIcon(plugin.pluginId);
                var color = self.getPluginColor(plugin.pluginId);
                var localizedName = plugin.displayName || plugin.pluginId;
                var health = plugin.healthProbe?.status || plugin.health;
                var healthMessage = plugin.healthProbe?.message;

                // Row tooltip: description + health message + registration error + declared/runtime resources
                var tips = [plugin.description || localizedName];
                if (healthMessage) tips.push(healthMessage);
                if (plugin.registrationError) tips.push(plugin.registrationError);
                var declared = (plugin.declaredResources || []).map(function(r) {
                    return r.type + (r.capacity ? '(' + r.capacity + ')' : '');
                });
                if (declared.length) tips.push(self._t('plugin.declaredResources', 'Declared resources') + ': ' + declared.join(', '));
                (plugin.runtimeResources || []).forEach(function(resource) {
                    var status = resource.health || (resource.running ? 'Healthy' : 'Stopped');
                    var stats = Object.entries(resource.statistics || {}).map(function(e) { return e[0] + '=' + e[1]; }).join(', ');
                    tips.push(resource.resourceId + ' [' + status + ']' + (stats ? ' ' + stats : ''));
                });
                if (plugin.bindingTargets && plugin.bindingTargets.length) {
                    tips.push(self._t('plugin.boundTo', 'Bound to') + ': ' + plugin.bindingTargets.join(', '));
                }
                if (plugin.dependencies && plugin.dependencies.length) {
                    tips.push(self._t('plugin.dependencies', 'Dependencies') + ': ' + plugin.dependencies.join(', '));
                }
                var rowTitle = window.DashboardUtils.escapeHtml(tips.join(' · '));

                // Action buttons: configure (Plugin Center) + observe (monitor page) + toggle + external lifecycle
                // "Enabled but bound to nothing" is the one state where the next step is
                // unambiguous, so it gets a labelled primary button instead of an icon.
                var needsBinding = plugin.enabled && !(plugin.bindingCount > 0);
                var actions = '';
                if (needsBinding) {
                    actions += '<a class="btn btn-sm btn-primary plg-bind-cta" href="' + self.getConfigHref(plugin.pluginId) + '" ' +
                        'title="' + self._t('pw.pendingHint', '已启用但未绑定，插件不会生效') + '">' +
                        '<i class="bi bi-plus-circle me-1"></i>' + self._t('pw.act.bind', '去绑定') + '</a>';
                } else {
                    actions += '<a class="btn btn-sm btn-outline-primary" href="' + self.getConfigHref(plugin.pluginId) + '" ' +
                        'title="' + self._t('plugin.configure', '配置绑定') + '">' +
                        '<i class="bi bi-sliders"></i></a>';
                }
                /* Plugins that render output somewhere get a second entry point, so
                   "where do I configure it" and "where do I watch it" never collide. */
                var monitorPage = self.MONITOR_PAGES[plugin.pluginId];
                if (monitorPage) {
                    var monitorHint = self._t('pc.act.monitor', '查看监控');
                    if (!plugin.enabled || !(plugin.bindingCount > 0)) {
                        monitorHint += ' · ' + self._t('pc.monitor.locked', '插件生效后才会有数据');
                    }
                    actions += '<a class="btn btn-sm btn-outline-secondary" href="' + self.getPageHref(monitorPage) + '" ' +
                        'title="' + window.DashboardUtils.escapeHtml(monitorHint) + '">' +
                        '<i class="bi bi-graph-up-arrow"></i></a>';
                }
                var toggleClass = plugin.enabled ? 'btn-outline-danger' : 'btn-outline-success';
                var toggleIcon = plugin.enabled ? 'bi-toggle-on' : 'bi-toggle-off';
                var toggleLabel = plugin.enabled ? __('plugin.toggleOff') : __('plugin.toggleOn');
                actions += '<button class="btn btn-sm ' + toggleClass + '" ' +
                    'onclick="PluginModule.togglePlugin(\'' + window.DashboardUtils.escapeHtml(plugin.pluginId) + '\', ' + !plugin.enabled + ')" title="' + toggleLabel + '">' +
                    '<i class="bi ' + toggleIcon + '"></i></button>';
                if (plugin.registrationStatus) {
                    actions +=
                        '<button class="btn btn-sm btn-outline-warning" ' +
                        'onclick="PluginModule.showUpgradeDialog(\'' + window.DashboardUtils.escapeHtml(plugin.pluginId) + '\')" title="' + self._t('plugin.upgrade', 'Upgrade') + '" ' + (plugin.enabled ? 'disabled' : '') + '>' +
                        '<i class="bi bi-arrow-up-circle"></i></button>' +
                        '<button class="btn btn-sm btn-outline-danger" ' +
                        'onclick="PluginModule.uninstallPlugin(\'' + window.DashboardUtils.escapeHtml(plugin.pluginId) + '\')" title="' + self._t('plugin.uninstall', 'Uninstall') + '" ' + (plugin.enabled ? 'disabled' : '') + '>' +
                        '<i class="bi bi-trash"></i></button>';
                }

                // Registration badge for external plugins
                var regBadge = '';
                if (plugin.registrationStatus) {
                    var regStatus = plugin.registrationStatus;
                    var regBadgeClass = regStatus === 'Active' || regStatus === 'Discovered' ? 'plg-reg--ok'
                        : regStatus === 'InvalidManifest' || regStatus === 'LoadFailed' ? 'plg-reg--err'
                        : 'plg-reg--warn';
                    regBadge = '<span class="plg-reg ' + regBadgeClass + '" title="' + window.DashboardUtils.escapeHtml(plugin.registrationError || '') + '">' +
                        window.DashboardUtils.escapeHtml(regStatus) + '</span>';
                }

                var healthColor = health === 'Healthy' ? '#10b981'
                    : health === 'Disabled' || health === 'Stopped' ? '#94a3b8'
                    : health === 'Degraded' || health === 'Starting' || health === 'Stopping' ? '#f59e0b'
                    : health === 'Faulted' ? '#ef4444' : '#94a3b8';

                // Enabled but bound to nothing: the plugin is on yet does nothing.
                // Calling that out inline is the whole point of this column.
                var bindHint = plugin.bindingCount > 0
                    ? ' · <i class="bi bi-link"></i> ' + plugin.bindingCount
                    : (plugin.enabled ? ' · <span class="plg-unbound">' + self._t('plugin.unbound', '未绑定') + '</span>' : '');

                return '<tr' + (plugin.enabled ? '' : ' class="plg-row--off"') + ' title="' + rowTitle + '">' +
                    '<td><div class="plg-cell-plugin">' +
                        '<span class="plg-icon" style="background:' + color + '15;color:' + color + ';"><i class="bi ' + icon + '"></i></span>' +
                        '<div>' +
                            '<div class="plg-name">' + window.DashboardUtils.escapeHtml(localizedName) + ' ' + regBadge + '</div>' +
                            '<div class="plg-about">' + window.DashboardUtils.escapeHtml(self._t('pc.about.' + plugin.pluginId, '')) + '</div>' +
                            '<div class="plg-plugin-id">' + window.DashboardUtils.escapeHtml(plugin.pluginId) +
                                ' · v' + window.DashboardUtils.escapeHtml(plugin.version || '1.0') +
                                bindHint +
                            '</div>' +
                        '</div>' +
                    '</div></td>' +
                    '<td><span class="plg-health" style="color:' + healthColor + ';">' +
                        '<span class="plg-dot" style="background:' + healthColor + ';"></span>' +
                        window.DashboardUtils.escapeHtml(health || '-') + '</span></td>' +
                    '<td>' + self.getScopeHtml(plugin.scopes) + '</td>' +
                    '<td class="plg-actions">' + actions + '</td>' +
                '</tr>';
            }).join('');

            container.innerHTML = summaryHtml +
                '<div class="plg-table-wrap"><table class="plg-table"><thead><tr>' +
                    '<th><i class="bi bi-puzzle me-1"></i>' + __('plugin.title') + '</th>' +
                    '<th><i class="bi bi-activity me-1"></i>' + __('plugin.colHealth', '健康状态') + '</th>' +
                    '<th style="width:96px;"><i class="bi bi-signpost me-1"></i>' + __('plugin.colScope', '作用于') + '</th>' +
                    '<th class="plg-th-actions"></th>' +
                '</tr></thead><tbody>' + rows + '</tbody></table></div>';
        },

        togglePlugin: function(pluginId, enable) {
            var self = this;
            var plugin = this.plugins.find(function(item) { return item.pluginId === pluginId; });
            var localizedName = (plugin && plugin.displayName) || pluginId;
            var action = enable ? __('plugin.toggleOn') : __('plugin.toggleOff');
            var msg = __('plugin.toggleConfirm', { action: action, name: localizedName });
            window.DashboardModals.showConfirm(msg, async function() {
                try {
                    await window.DashboardApi.togglePlugin(pluginId, enable);
                    window.DashboardModals.showSuccess(enable ? __('plugin.enableSuccess') : __('plugin.disableSuccess'));
                    await self.load();
                    // keep the shared workspace status line in sync after any toggle
                    if (window.PluginWorkspace) window.PluginWorkspace.refresh();
                } catch (error) {
                    console.error('[Plugin] Toggle failed:', error);
                    window.DashboardModals.showError(__('plugin.toggleFailed'));
                }
            }, null, { danger: !enable });
        },

        resetAll: function() {
            var self = this;
            window.DashboardModals.showConfirm(__('plugin.resetConfirm'), async function() {
                try {
                    await window.DashboardApi.resetPlugins();
                    window.DashboardModals.showSuccess(__('plugin.resetSuccess'));
                    await self.load();
                    // keep the shared workspace status line in sync after any toggle
                    if (window.PluginWorkspace) window.PluginWorkspace.refresh();
                } catch (error) {
                    console.error('[Plugin] Reset failed:', error);
                    window.DashboardModals.showError(__('plugin.resetFailed'));
                }
            }, null, { danger: true });
        },

        showUpgradeDialog: function(pluginId) {
            var self = this;
            var plugin = this.plugins.find(function(item) { return item.pluginId === pluginId; });
            var localizedName = (plugin && plugin.displayName) || pluginId;

            var html =
                '<div class="mb-3">' +
                    '<p class="text-muted">' + this._t('plugin.upgradeHelp', 'Specify the new plugin version source directory. The plugin must be disabled first.') + '</p>' +
                    '<label class="form-label">' + this._t('plugin.upgradePath', 'New version source directory path') + '</label>' +
                    '<input type="text" class="form-control" id="upgrade-source-dir" placeholder="/path/to/new/version/directory" />' +
                '</div>';
            window.DashboardModals.showCustom({
                title: '<i class="bi bi-arrow-up-circle me-2"></i>' + this._t('plugin.upgrade', 'Upgrade') + ' · ' + window.DashboardUtils.escapeHtml(localizedName),
                body: html,
                confirmText: this._t('plugin.upgrade', 'Upgrade'),
                confirmClass: 'btn-warning',
                onConfirm: async function() {
                    var sourceDir = document.getElementById('upgrade-source-dir').value.trim();
                    if (!sourceDir) {
                        window.DashboardModals.showError(self._t('plugin.installPathRequired', 'Source directory path is required'));
                        return false;
                    }
                    try {
                        await window.DashboardApi.upgradePlugin(pluginId, sourceDir);
                        window.DashboardModals.showSuccess(self._t('plugin.upgradeSuccess', 'Plugin upgraded successfully'));
                        await self.load();
                    // keep the shared workspace status line in sync after any toggle
                    if (window.PluginWorkspace) window.PluginWorkspace.refresh();
                        return true;
                    } catch (error) {
                        console.error('[Plugin] Upgrade failed:', error);
                        var msg = (error && error.message) || self._t('plugin.upgradeFailed', 'Upgrade failed');
                        window.DashboardModals.showError(msg);
                        return false;
                    }
                }
            });
        },

        uninstallPlugin: function(pluginId) {
            var self = this;
            var plugin = this.plugins.find(function(item) { return item.pluginId === pluginId; });
            var localizedName = (plugin && plugin.displayName) || pluginId;
            var msg = this._t('plugin.uninstallConfirm', 'Are you sure you want to uninstall plugin "{name}"? This will permanently remove the plugin files.')
                .replace('{name}', localizedName);
            window.DashboardModals.showConfirm(msg, async function() {
                try {
                    await window.DashboardApi.uninstallPlugin(pluginId);
                    window.DashboardModals.showSuccess(self._t('plugin.uninstallSuccess', 'Plugin uninstalled successfully'));
                    await self.load();
                    // keep the shared workspace status line in sync after any toggle
                    if (window.PluginWorkspace) window.PluginWorkspace.refresh();
                } catch (error) {
                    console.error('[Plugin] Uninstall failed:', error);
                    var errMsg = (error && error.message) || self._t('plugin.uninstallFailed', 'Uninstall failed');
                    window.DashboardModals.showError(errMsg);
                }
            }, null, { danger: true });
        },

        updateRefreshTime: function() {
            var el = document.getElementById('plugin-refresh-time');
            if (el) {
                el.textContent = window.DashboardI18n.formatDate(new Date());
            }
        }
    };

    if (window.DashboardApp) {
        window.DashboardApp.registerModule('plugin', PluginModule);
    }
    window.PluginModule = PluginModule;
})();
