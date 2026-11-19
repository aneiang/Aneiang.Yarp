/**
 * Plugin Binding Manager - Shared CRUD UI for plugin pages
 * Provides: summary cards, bindings table, add/edit modal, toggle, delete
 */
(function() {
    'use strict';

    function esc(value) {
        return window.DashboardUtils ? window.DashboardUtils.escapeHtml(value == null ? '' : String(value)) : String(value == null ? '' : value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    /* Escape a value for use inside a double-quoted HTML attribute. */
    function attr(value) {
        return esc(value).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function t(key, fallback) {
        var text = window.__ ? window.__(key) : null;
        return (!text || text === key) ? (fallback || key) : text;
    }

    function getPluginSchema(plugin) {
        if (window.DashboardCapabilities && window.DashboardCapabilities.getPluginSchema) {
            return window.DashboardCapabilities.getPluginSchema(plugin);
        }
        return null;
    }
 
    function create(config) {
        var pluginId = config.pluginId;
        var scope = config.scope; // 'Route' or 'Cluster'
        var scopeLabel = config.scopeLabel || (scope === 'Cluster' ? t('pluginPage.cluster', 'Cluster') : t('pluginPage.route', 'Route'));
        var containerId = config.containerId;
        var refreshTimeId = config.refreshTimeId;
        var moduleName = config.moduleName;

        var state = {
            plugin: null,
            bindings: [],
            scopes: [],
            pluginSchema: null
        };

        /* Rows ticked for a bulk action, keyed by binding id.
           Kept outside the render so a reload (or a single-row toggle, which also reloads)
           does not silently drop the operator's selection. Cleared after every bulk call. */
        var selected = {};

        var module = {
            initialized: false,
            autoRefreshInterval: null,

            init: function() {
                if (this.initialized) return;
                this.initialized = true;
            },

            destroy: function() {
                if (this.autoRefreshInterval) {
                    clearInterval(this.autoRefreshInterval);
                    this.autoRefreshInterval = null;
                }
                this.initialized = false;
            },

            load: async function() {
                var container = document.getElementById(containerId);
                if (!container) return;
                try {
                    container.innerHTML = '<div class="loading-state"><div class="loading-spinner"></div><div class="loading-text">' + t('common.loading', 'Loading...') + '</div></div>';

                    var results = await Promise.all([
                        window.DashboardApi.getPlugin(pluginId),
                        window.DashboardApi.getPluginBindings(pluginId, scope),
                        scope === 'Cluster'
                            ? window.DashboardApi.getClusters()
                            : window.DashboardApi.getRoutes()
                    ]);

                    state.plugin = results[0];
                    state.bindings = Array.isArray(results[1]) ? results[1] : [];
                    state.scopes = Array.isArray(results[2]) ? results[2] : [];
                    state.pluginSchema = getPluginSchema(state.plugin);

                    this.render(container);
                    this.updateRefreshTime();
                    /* Host pages (e.g. Plugin Center) reuse this payload to render
                       their own status chrome instead of refetching the same data. */
                    if (typeof config.onLoaded === 'function') {
                        config.onLoaded(state);
                    }
                } catch (error) {
                    container.innerHTML = '<div class="alert alert-danger">' + t('common.loadFailed', 'Load failed') + ': ' + esc(error.message || error) + '</div>';
                }
            },

            render: function(container) {
                var plugin = state.plugin;
                var bindings = state.bindings;
                var bindingCount = bindings.length;
                var icon = config.icon || 'bi-puzzle';
                var color = config.color || 'text-primary';

                var html = '';

                // Slim meta line: enable-state and binding count are already shown by the
                // host's effect chain, repeating them here just adds noise. Keep provenance
                // (version / built-in) plus the actions at the right end.
                html += '<div class="pc-meta-line">';
                html += '<span class="pc-meta-item"><i class="bi bi-tag"></i><span class="pc-meta-num">v' + esc(plugin ? (plugin.version || '1.0') : '-') + '</span></span>';
                html += '<span class="pc-meta-item"><i class="bi bi-box"></i><span class="pc-meta-label">' + (plugin && plugin.isBuiltIn ? t('common.builtIn', 'Built-in') : t('common.external', 'External')) + '</span></span>';
                html += '<div class="pc-meta-actions">';
                html += '<span class="plg-plugin-id" style="font-size:11.5px;">' + scopeLabel + '</span>';
                html += '<span id="' + refreshTimeId + '" class="refresh-badge"></span>';
                html += '<button class="pr-refresh-btn" data-action="refresh" title="' + t('index.btn.refresh', '刷新') + '"><i class="bi bi-arrow-clockwise"></i></button>';
                /* With zero bindings the host's effect chain already offers the primary
                   "add" action and the empty state repeats it - a third button here
                   would just sit next to them doing the same thing. */
                if (bindingCount > 0) {
                    html += '<button class="btn btn-sm btn-success" data-action="add"><i class="bi bi-plus-lg me-1"></i>' + t('pluginPage.addBinding', 'Add Binding') + '</button>';
                }
                html += '</div>';
                html += '</div>';

                // Help text (single subtle line; description collapses into it)
                var descText = config.descKey ? t(config.descKey, '') : (config.descText || '');
                var helpText = config.helpKey ? t(config.helpKey, '') : (config.helpText || '');
                if (descText || helpText) {
                    html += '<div class="pc-help-line"><i class="bi bi-info-circle"></i><span>' + esc(helpText || descText) + '</span></div>';
                }

                // Bulk bar: appears once at least one row is ticked. Hidden rather than
                // disabled when nothing is selected — a permanently greyed toolbar reads
                // as "this feature is broken", not "pick something first".
                html += '<div class="plg-batch-bar d-none align-items-center gap-2 mb-2 px-2 py-2 border rounded-3 bg-light" data-role="batch-bar">';
                html += '<span class="small text-muted" data-role="batch-count"></span>';
                html += '<button class="btn btn-sm btn-outline-success" data-action="batch-enable" data-requires-write><i class="bi bi-toggle-on me-1"></i>' + t('pluginPage.batchEnable', 'Enable selected') + '</button>';
                html += '<button class="btn btn-sm btn-outline-warning" data-action="batch-disable" data-requires-write><i class="bi bi-toggle-off me-1"></i>' + t('pluginPage.batchDisable', 'Disable selected') + '</button>';
                html += '<button class="btn btn-sm btn-outline-danger ms-auto" data-action="batch-delete" data-requires-write><i class="bi bi-trash me-1"></i>' + t('pluginPage.batchDelete', 'Delete selected') + '</button>';
                html += '</div>';

                // Bindings table (shared plg-table style)
                html += '<div class="plg-table-wrap"><table class="plg-table"><thead><tr>';
                html += '<th style="width:34px;"><input type="checkbox" class="form-check-input" data-action="select-all" aria-label="' + attr(t('pluginPage.selectAll', 'Select all')) + '"></th>';
                html += '<th style="width:220px;">' + scopeLabel + ' ID</th>';
                html += '<th>' + t('pluginPage.configSummary', 'Config Summary') + '</th>';
                html += '<th style="width:110px;">' + t('common.status', 'Status') + '</th>';
                html += '<th class="plg-th-actions">' + t('common.actions', 'Actions') + '</th>';
                html += '</tr></thead><tbody>';

                if (bindingCount > 0) {
                    bindings.forEach(function(b) {
                        var bindingConfig = parseConfigSafe(b);
                        var summary = summarizeConfigSafe(bindingConfig, pluginId);
                        var isOn = b.enabled;
                        var healthColor = isOn ? '#10b981' : '#94a3b8';
                        html += '<tr' + (isOn ? '' : ' class="plg-row--off"') + '>';
                        html += '<td><input type="checkbox" class="form-check-input" data-role="row-select" data-id="' + attr(b.id) + '"' + (selected[b.id] ? ' checked' : '') + ' aria-label="' + attr(t('pluginPage.selectRow', 'Select this binding')) + '"></td>';
                        html += '<td><span class="plg-plugin-id" style="font-size:12px;">' + esc(b.scopeId || '-') + '</span></td>';
                        html += '<td><span class="plg-plugin-id" style="font-size:12px;">' + esc(summary) + '</span></td>';
                        html += '<td><span class="plg-health" style="color:' + healthColor + ';"><span class="plg-dot" style="background:' + healthColor + ';"></span>' + (isOn ? t('common.enabled', 'Enabled') : t('common.disabled', 'Disabled')) + '</span></td>';
                        html += '<td class="plg-actions">';
                        html += '<button class="btn btn-sm btn-outline-primary" title="' + t('pluginPage.editBinding', 'Edit') + '" data-action="edit" data-id="' + attr(b.id) + '"><i class="bi bi-pencil"></i></button>';
                        html += '<button class="btn btn-sm btn-outline-' + (isOn ? 'warning' : 'success') + '" title="' + (isOn ? t('pluginPage.disable', 'Disable') : t('pluginPage.enable', 'Enable')) + '" data-action="toggle" data-id="' + attr(b.id) + '"><i class="bi bi-' + (isOn ? 'toggle-on' : 'toggle-off') + '"></i></button>';
                        html += '<button class="btn btn-sm btn-outline-danger" title="' + t('common.delete', 'Delete') + '" data-action="delete" data-id="' + attr(b.id) + '"><i class="bi bi-trash"></i></button>';
                        html += '</td>';
                        html += '</tr>';
                    });
                } else {
                    html += '<tr><td colspan="5" style="white-space:normal;">';
                    html += '<div class="pr-empty">';
                    html += '<i class="bi bi-inbox"></i>';
                    html += '<p class="mb-1">' + t('pluginPage.noBindings', 'No bindings yet') + '</p>';
                    html += '<p class="small mb-2">' + t('pluginPage.noBindingsHint', 'This plugin stays inactive, and collects no data, until a target is bound.') + '</p>';
                    html += '<button class="btn btn-outline-primary btn-sm" data-action="add"><i class="bi bi-plus-lg me-1"></i>' + t('pluginPage.addBinding', 'Add Binding') + '</button>';
                    html += '</div>';
                    html += '</td></tr>';
                }

                html += '</tbody></table></div>';
                container.innerHTML = html;

                /* Row actions are delegated instead of inlined: binding ids are
                   strings, and embedding them in onclick attributes broke out of
                   the HTML attribute and produced truncated JS (SyntaxError). */
                if (!container.__pcActionBound) {
                    container.__pcActionBound = true;
                    container.addEventListener('click', function(event) {
                        var trigger = event.target && event.target.closest ? event.target.closest('[data-action]') : null;
                        if (!trigger || !container.contains(trigger)) return;
                        var action = trigger.getAttribute('data-action');
                        var id = trigger.getAttribute('data-id');
                        if (action === 'refresh') module.load();
                        else if (action === 'add') module.openAddModal();
                        else if (action === 'edit') module.openEditModal(id);
                        else if (action === 'toggle') module.toggleBinding(id);
                        else if (action === 'delete') module.deleteBinding(id);
                        else if (action === 'batch-enable') module.batchSetEnabled(true);
                        else if (action === 'batch-disable') module.batchSetEnabled(false);
                        else if (action === 'batch-delete') module.batchDelete();
                    });
                    /* Checkboxes are handled on 'change', not 'click': the click fires before
                       the checked state flips, so reading it there always gives the old value. */
                    container.addEventListener('change', function(event) {
                        var box = event.target;
                        if (!box || !box.matches) return;
                        if (box.matches('[data-role="row-select"]')) {
                            var rowId = box.getAttribute('data-id');
                            if (box.checked) selected[rowId] = true; else delete selected[rowId];
                            syncBatchBar(container);
                        } else if (box.matches('[data-action="select-all"]')) {
                            selected = {};
                            if (box.checked) {
                                state.bindings.forEach(function(b) { selected[b.id] = true; });
                            }
                            container.querySelectorAll('[data-role="row-select"]').forEach(function(item) {
                                item.checked = !!selected[item.getAttribute('data-id')];
                            });
                            syncBatchBar(container);
                        }
                    });
                }

                syncBatchBar(container);
            },

            openAddModal: function() {
                openEditor(null);
            },

            openEditModal: function(bindingId) {
                var binding = findBinding(bindingId);
                if (!binding) {
                    window.DashboardModals.showError(t('pluginPage.bindingNotFound', 'Binding not found'));
                    return;
                }
                openEditor(binding);
            },

            toggleBinding: async function(bindingId) {
                var binding = findBinding(bindingId);
                if (!binding) return;
                try {
                    var payload = Object.assign({}, binding, {
                        enabled: !binding.enabled,
                        scope: binding.scope === 1 ? 'Route' : (binding.scope === 2 ? 'Cluster' : binding.scope)
                    });
                    await window.DashboardApi.updateBinding(binding.id, payload);
                    window.DashboardModals.showSuccess(binding.enabled ? t('pluginPage.disabledSuccess', 'Disabled') : t('pluginPage.enabledSuccess', 'Enabled'));
                    await this.load();
                } catch (error) {
                    window.DashboardModals.showError(t('common.operationFailed', 'Operation failed') + ': ' + (error.message || error));
                }
            },

            deleteBinding: function(bindingId) {
                var binding = findBinding(bindingId);
                if (!binding) return;
                var self = this;
                window.DashboardModals.showConfirm(
                    t('pluginPage.deleteConfirm', 'Delete this binding?') + ' (' + (binding.scopeId || '') + ')',
                    async function() {
                        try {
                            await window.DashboardApi.deleteBinding(binding.id);
                            window.DashboardModals.showSuccess(t('pluginPage.deletedSuccess', 'Deleted'));
                            await self.load();
                        } catch (error) {
                            window.DashboardModals.showError(t('common.deleteFailed', 'Delete failed') + ': ' + (error.message || error));
                        }
                    },
                    null,
                    { title: t('common.delete', 'Delete'), danger: true }
                );
            },

            /* One atomic round-trip for N bindings: the server takes the same publish gate
               as a single edit, so "half applied" is not a state this can leave behind. */
            batchSetEnabled: async function(enabled) {
                var ids = Object.keys(selected);
                if (!ids.length) return;
                var self = this;
                try {
                    var resp = await window.DashboardApi.endpoints.batchSetBindingsEnabled(ids, enabled);
                    var result = (resp && resp.data) || resp || {};
                    var failed = Number(result.failed || 0);
                    if (failed > 0) {
                        window.DashboardModals.showWarning(
                            t('pluginPage.batchPartial', 'Some bindings were not updated') +
                            ': ' + failed + '/' + (result.total || ids.length));
                    } else {
                        window.DashboardModals.showSuccess(
                            (enabled ? t('pluginPage.batchEnabledSuccess', 'Selected bindings enabled')
                                     : t('pluginPage.batchDisabledSuccess', 'Selected bindings disabled')) +
                            ' (' + ids.length + ')');
                    }
                    selected = {};
                    await self.load();
                } catch (error) {
                    window.DashboardModals.showError(t('common.operationFailed', 'Operation failed') + ': ' + (error.message || error));
                }
            },

            batchDelete: function() {
                var ids = Object.keys(selected);
                if (!ids.length) return;
                var self = this;
                window.DashboardModals.showConfirm(
                    t('pluginPage.batchDeleteConfirm', 'Delete {count} selected bindings? This does not affect other bindings.')
                        .replace('{count}', String(ids.length)),
                    async function() {
                        try {
                            var resp = await window.DashboardApi.endpoints.batchDeleteBindings(ids);
                            var result = (resp && resp.data) || resp || {};
                            var failed = Number(result.failed || 0);
                            if (failed > 0) {
                                window.DashboardModals.showWarning(
                                    t('pluginPage.batchPartial', 'Some bindings were not updated') +
                                    ': ' + failed + '/' + (result.total || ids.length));
                            } else {
                                window.DashboardModals.showSuccess(t('pluginPage.batchDeletedSuccess', 'Selected bindings deleted') + ' (' + ids.length + ')');
                            }
                            selected = {};
                            await self.load();
                        } catch (error) {
                            window.DashboardModals.showError(t('common.deleteFailed', 'Delete failed') + ': ' + (error.message || error));
                        }
                    },
                    null,
                    { title: t('common.delete', 'Delete'), danger: true }
                );
            },

            updateRefreshTime: function() {
                var el = document.getElementById(refreshTimeId);
                if (el) el.textContent = new Date().toLocaleTimeString();
            }
        };

        function parseConfigSafe(binding) {
            try {
                return JSON.parse(binding.configJson || '{}');
            } catch (_) {
                return {};
            }
        }

        /* Ids come from data-* attributes as strings, so compare loosely. */
        function findBinding(bindingId) {
            return state.bindings.find(function(b) { return String(b.id) === String(bindingId); });
        }

        /* The bulk bar and the header checkbox both derive from `selected`; nothing else
           may write that state, so this is the single place that has to stay in sync. */
        function syncBatchBar(container) {
            var bar = container.querySelector('[data-role="batch-bar"]');
            var count = Object.keys(selected).length;
            if (bar) {
                bar.classList.toggle('d-none', count === 0);
                bar.classList.toggle('d-flex', count > 0);
                var label = bar.querySelector('[data-role="batch-count"]');
                if (label) label.textContent = t('pluginPage.batchSelected', 'Selected') + ': ' + count;
            }
            var all = container.querySelector('[data-action="select-all"]');
            if (all) {
                all.checked = count > 0 && count === state.bindings.length;
                /* Indeterminate is the only honest state for "some rows ticked"; without it
                   the header box looks unchecked while the bulk bar says otherwise. */
                all.indeterminate = count > 0 && count < state.bindings.length;
            }
        }

        function summarizeConfigSafe(config, pluginId) {
            /* Same contract as the capability page: a plugin that ships an editor
               also knows what its one-line summary should say. */
            var editors = window.PluginConfigEditors;
            if (editors && editors.summarize) {
                var text = editors.summarize(pluginId, config);
                if (text) return text;
            }
            var keys = Object.keys(config);
            if (!keys.length) return '{}';
            var Cap = window.DashboardCapabilities;
            return keys.slice(0, 4).map(function(key) {
                var value = config[key];
                if (value && typeof value === 'object') value = Array.isArray(value) ? '[' + value.length + ']' : '{...}';
                var label = (Cap && Cap.propertyLabel) ? Cap.propertyLabel(key, null, pluginId) : key;
                return label + ': ' + String(value);
            }).join(', ') + (keys.length > 4 ? ' ...' : '');
        }

        function openEditor(binding) {
            var isEdit = !!binding;
            var Cap = window.DashboardCapabilities;
            var schema = state.pluginSchema;
            var bindingConfig = isEdit ? parseConfigSafe(binding) : (Cap ? Cap.applyDefaults(schema, {}) : {});

            var modal = document.createElement('div');
            var modalId = 'plugin-binding-editor-' + Date.now();
            modal.className = 'modal fade';
            modal.id = modalId;
            modal.tabIndex = -1;
            modal.dataset.bsBackdrop = 'static';
            // New bindings support multi-select: pick several routes/clusters in one atomic batch.
            modal.innerHTML = '<div class="modal-dialog modal-dialog-centered modal-lg"><div class="modal-content">' +
                '<div class="modal-header"><h5 class="modal-title"><i class="bi ' + (isEdit ? 'bi-pencil-square' : 'bi-plus-square') + ' me-2"></i>' + (isEdit ? t('pluginPage.editBinding', 'Edit Binding') : t('pluginPage.addBinding', 'Add Binding')) + '</h5><button type="button" class="btn-close" data-bs-dismiss="modal"></button></div>' +
                '<div class="modal-body">' +
                (isEdit ? '' : '<div class="mb-3"><label class="form-label fw-semibold">' + scopeLabel + ' <span class="text-muted fw-normal small" data-role="scope-selected-count"></span></label>' +
                    '<select class="form-select" data-role="scope-select" multiple size="8" style="min-height:160px;"></select>' +
                    '<div class="form-text">' + esc(t('pluginPage.selectScopeHint', 'Multi-select: hold Ctrl/Shift')) + '</div>' +
                    '<div class="form-check form-switch mt-2"><input class="form-check-input" type="checkbox" data-role="overwrite" id="' + modalId + '-overwrite"><label class="form-check-label small" for="' + modalId + '-overwrite">' + esc(t('pluginPage.overwriteExisting', 'Overwrite existing bindings')) + '</label></div>' +
                    '</div>') +
                '<div class="form-check form-switch mb-3"><input class="form-check-input" type="checkbox" data-role="enabled" id="' + modalId + '-enabled"' + (isEdit ? (binding.enabled ? ' checked' : '') : ' checked') + '><label class="form-check-label" for="' + modalId + '-enabled">' + t('pluginPage.enableBinding', 'Enable this binding') + '</label></div>' +
                '<div data-role="schema-form"></div>' +
                '<div class="d-none" data-role="json-panel"><label class="form-label fw-semibold">' + t('pluginPage.jsonConfig', 'Advanced JSON Config') + '</label><textarea class="form-control font-monospace" rows="14" data-role="json"></textarea><div class="form-text">' + t('pluginPage.jsonHint', 'Edit JSON directly. Ensure valid format before switching back to form mode.') + '</div></div>' +
                '<div data-role="batch-result" class="mt-2"></div>' +
                '</div>' +
                '<div class="modal-footer justify-content-between">' +
                '<button type="button" class="btn btn-outline-secondary btn-sm" data-role="mode"><i class="bi bi-braces me-1"></i>JSON</button>' +
                '<div><button type="button" class="btn btn-secondary btn-sm me-2" data-bs-dismiss="modal">' + t('common.cancel', 'Cancel') + '</button><button type="button" class="btn btn-primary btn-sm" data-role="save"><i class="bi bi-check-lg me-1"></i>' + t('common.save', 'Save') + '</button></div>' +
                '</div></div></div>';
            document.body.appendChild(modal);

            var scopeSelect = modal.querySelector('[data-role="scope-select"]');
            var overwriteInput = modal.querySelector('[data-role="overwrite"]');
            var scopeCountEl = modal.querySelector('[data-role="scope-selected-count"]');
            var batchResult = modal.querySelector('[data-role="batch-result"]');
            var enabledInput = modal.querySelector('[data-role="enabled"]');
            var schemaForm = modal.querySelector('[data-role="schema-form"]');
            var jsonPanel = modal.querySelector('[data-role="json-panel"]');
            var jsonInput = modal.querySelector('[data-role="json"]');
            var modeButton = modal.querySelector('[data-role="mode"]');
            var saveButton = modal.querySelector('[data-role="save"]');
            var advanced = false;

            function updateScopeCount() {
                if (!scopeCountEl || !scopeSelect) return;
                var selected = Array.from(scopeSelect.selectedOptions).filter(function(o) { return !o.disabled; }).length;
                scopeCountEl.textContent = selected > 0 ? t('pluginPage.selectedCount', 'Selected') + ': ' + selected : '';
            }

            if (!isEdit && scopeSelect) {
                var boundScopeIds = state.bindings.map(function(b) { return b.scopeId; });
                state.scopes.forEach(function(item) {
                    var itemId = scope === 'Cluster' ? (item.clusterId || item.ClusterId) : (item.routeId || item.RouteId);
                    if (!itemId) return;
                    var isBound = boundScopeIds.indexOf(itemId) >= 0;
                    var opt = new Option(itemId + (isBound ? ' (' + t('pluginPage.alreadyBound', 'Bound') + ')' : ''), itemId, false, false);
                    opt.dataset.bound = isBound ? '1' : '0';
                    opt.disabled = isBound;
                    scopeSelect.appendChild(opt);
                });
                if (scopeSelect.options.length === 0) {
                    var alertDiv = document.createElement('div');
                    alertDiv.className = 'alert alert-warning';
                    alertDiv.innerHTML = '<i class="bi bi-exclamation-triangle me-1"></i>' + t('pluginPage.noScopes', 'No available') + ' ' + scopeLabel + '. ' + t('pluginPage.createScopeFirst', 'Please create a') + ' ' + scopeLabel + ' first.';
                    scopeSelect.parentElement.insertBefore(alertDiv, scopeSelect);
                }

                // Already-bound targets stay disabled unless the user opts into overwriting them.
                function syncScopeOptions() {
                    var allowBound = !!(overwriteInput && overwriteInput.checked);
                    Array.prototype.forEach.call(scopeSelect.options, function(opt) {
                        var isBound = opt.dataset.bound === '1';
                        opt.disabled = isBound && !allowBound;
                        if (opt.disabled) opt.selected = false;
                    });
                    updateScopeCount();
                }

                if (overwriteInput) overwriteInput.addEventListener('change', syncScopeOptions);
                scopeSelect.addEventListener('change', updateScopeCount);
                updateScopeCount();
            }

            function renderForm() {
                schemaForm.replaceChildren();
                if (!schema || !Object.keys(schema.properties || {}).length) {
                    var notice = document.createElement('div');
                    notice.className = 'alert alert-secondary py-2';
                    notice.textContent = t('pluginPage.noSchema', 'This plugin does not provide a visual configuration form. Use JSON mode instead.');
                    schemaForm.appendChild(notice);
                    return;
                }
                if (Cap && Cap.renderGroupedFields) {
                    Cap.renderGroupedFields(schemaForm, schema, bindingConfig, pluginId);
                } else {
                    var required = schema.required || [];
                    schemaForm.__schemaFields = Object.keys(schema.properties).filter(function(name) { return name !== 'enabled'; }).map(function(name) {
                        var field = Cap.schemaField(name, schema.properties[name], bindingConfig[name], required.indexOf(name) >= 0, pluginId);
                        schemaForm.appendChild(field);
                        return { name: name, field: field };
                    });
                }
            }

            function syncToJson() {
                jsonInput.value = JSON.stringify(bindingConfig, null, 2);
            }

            modeButton.addEventListener('click', function() {
                if (!advanced && schema) {
                    var formConfig = Cap.readSchemaForm(schemaForm, schema, bindingConfig);
                    if (formConfig === null) return;
                    bindingConfig = formConfig;
                    syncToJson();
                } else if (advanced) {
                    try {
                        bindingConfig = JSON.parse(jsonInput.value || '{}');
                    } catch (_) {
                        window.DashboardModals.showWarning(t('pluginPage.invalidJson', 'Invalid JSON format'));
                        return;
                    }
                    renderForm();
                }
                advanced = !advanced;
                schemaForm.classList.toggle('d-none', advanced);
                jsonPanel.classList.toggle('d-none', !advanced);
                modeButton.innerHTML = advanced ? '<i class="bi bi-ui-checks-grid me-1"></i>' + t('pluginPage.formMode', 'Form') : '<i class="bi bi-braces me-1"></i>JSON';
            });

            saveButton.addEventListener('click', async function() {
                // Edit always targets one binding; new bindings may target several in one atomic batch.
                var scopeIds = isEdit
                    ? [binding.scopeId]
                    : (scopeSelect ? Array.from(scopeSelect.selectedOptions)
                        .filter(function(o) { return o.value && !o.disabled; })
                        .map(function(o) { return o.value; }) : []);
                if (!isEdit && scopeIds.length === 0) {
                    window.DashboardModals.showWarning(t('pluginPage.scopeRequiredMulti', 'Please select at least one target'));
                    return;
                }
                if (batchResult) batchResult.innerHTML = '';

                if (advanced) {
                    try {
                        bindingConfig = JSON.parse(jsonInput.value || '{}');
                    } catch (_) {
                        window.DashboardModals.showWarning(t('pluginPage.invalidJson', 'Invalid JSON format'));
                        return;
                    }
                } else if (schema && Cap) {
                    var formConfig = Cap.readSchemaForm(schemaForm, schema, bindingConfig);
                    if (formConfig === null) {
                        window.DashboardModals.showWarning(t('pluginPage.validationError', 'Please check form fields'));
                        return;
                    }
                    bindingConfig = formConfig;
                }

                // Shared payload; single-create and batch-create add their own target fields.
                var payload = {
                    pluginId: pluginId,
                    scope: scope,
                    enabled: enabledInput.checked,
                    configJson: JSON.stringify(bindingConfig),
                    schemaVersion: (Cap && Cap.getPluginSchemaVersion)
                        ? Cap.getPluginSchemaVersion(state.plugin)
                        : 1,
                    order: Number((state.plugin && state.plugin.order) || 0)
                };

                function resetSaveButton() {
                    saveButton.disabled = false;
                    saveButton.innerHTML = '<i class="bi bi-check-lg me-1"></i>' + t('common.save', 'Save');
                }

                try {
                    saveButton.disabled = true;
                    saveButton.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>' + t('common.saving', 'Saving...');
                    if (isEdit) {
                        await window.DashboardApi.updateBinding(binding.id, Object.assign({}, payload, {
                            scopeId: binding.scopeId,
                            configVersion: (binding.configVersion || 0) + 1
                        }));
                        window.DashboardModals.showSuccess(t('pluginPage.saveSuccess', 'Saved'));
                    } else if (scopeIds.length > 1) {
                        var resp = await window.DashboardApi.endpoints.batchCreateBindings(Object.assign({}, payload, {
                            scopeIds: scopeIds,
                            overwriteExisting: !!(overwriteInput && overwriteInput.checked)
                        }));
                        var result = (resp && resp.data) || resp || {};
                        var succeeded = result.succeeded != null ? result.succeeded : 0;
                        var failed = result.failed != null ? result.failed : 0;
                        if (failed > 0) {
                            var failedList = (result.items || []).filter(function(it) { return !it.success; })
                                .map(function(it) { return '<li>' + esc(it.id) + ' — ' + esc(it.message) + '</li>'; }).join('');
                            if (batchResult) {
                                batchResult.innerHTML = '<div class="alert alert-warning py-2 mb-0"><i class="bi bi-exclamation-triangle"></i> '
                                    + esc(t('pluginPage.batchBindPartial', 'Some bindings failed') + ' (' + succeeded + '/' + (result.total || scopeIds.length) + ')')
                                    + (failedList ? '<ul class="mb-0 mt-1 small">' + failedList + '</ul>' : '') + '</div>';
                            }
                            await module.load();
                            resetSaveButton();
                            return; // keep the dialog open so per-target errors stay visible
                        }
                        window.DashboardModals.showSuccess(t('pluginPage.batchBindSuccess', 'Batch binding succeeded') + ' (' + succeeded + ')');
                    } else {
                        await window.DashboardApi.createBinding(Object.assign({}, payload, {
                            scopeId: scopeIds[0],
                            configVersion: 1
                        }));
                        window.DashboardModals.showSuccess(t('pluginPage.addSuccess', 'Added'));
                    }
                    bootstrap.Modal.getInstance(modal).hide();
                    await module.load();
                } catch (error) {
                    window.DashboardModals.showError(t('common.saveFailed', 'Save failed') + ': ' + (error.message || error));
                    resetSaveButton();
                }
            });

            modal.addEventListener('hidden.bs.modal', function() { modal.remove(); });

            renderForm();
            syncToJson();
            new bootstrap.Modal(modal).show();
        }

        window[moduleName] = module;
        return module;
    }

    window.PluginBindingManager = { create: create };
})();
