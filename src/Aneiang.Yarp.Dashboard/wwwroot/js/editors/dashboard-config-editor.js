/**
 * Configuration Editor - Main controller for form/JSON dual-mode editing
 */
(function() {
    'use strict';

    window.DashboardConfigEditor = {
        currentMode: 'form', // 'form' | 'json'
        draftData: null,
        originalData: null,
        configType: null, // 'cluster' | 'route' | 'full'
        configId: null,
        containerId: null,

        /**
         * Initialize configuration editor
         */
        init: function(containerId, config) {
            this.containerId = containerId;
            this.configType = config.type || 'full';
            this.configId = config.id || null;
            this.currentMode = config.mode || 'form';
            this.draftData = config.data || {};
            this.originalData = JSON.parse(JSON.stringify(this.draftData));

            var self = this;
            
            window.DashboardSchemaService.load().then(function(schema) {
                self._renderEditor();
            }).catch(function(err) {
                console.error('[ConfigEditor] Failed to load schema:', err);
            });
        },

        /**
         * Render editor based on current mode
         */
        _renderEditor: function() {
            var container = document.getElementById(this.containerId);
            if (!container) return;

            container.innerHTML = '';

            var toolbar = this._createToolbar();
            container.appendChild(toolbar);

            var editorArea = document.createElement('div');
            editorArea.className = 'editor-area mt-3';
            editorArea.id = this.containerId + '-area';
            container.appendChild(editorArea);

            var validationPanel = document.createElement('div');
            validationPanel.id = 'validationPanel';
            validationPanel.style.display = 'none';
            container.appendChild(validationPanel);

            var actions = this._createActions();
            container.appendChild(actions);

            // Render current mode
            if (this.currentMode === 'form') {
                this._renderFormEditor();
            } else {
                this._renderJsonEditor();
            }
        },

        /**
         * Create toolbar with mode switch
         */
        _createToolbar: function() {
            var toolbar = document.createElement('div');
            toolbar.className = 'btn-group';
            toolbar.setAttribute('role', 'group');

            var formBtn = document.createElement('button');
            formBtn.type = 'button';
            formBtn.className = 'btn btn-sm ' + (this.currentMode === 'form' ? 'btn-primary' : 'btn-outline-primary');
            formBtn.textContent = this._t('modal.formMode', 'Form Mode');
            formBtn.onclick = function() { this.switchMode('form'); }.bind(this);

            var jsonBtn = document.createElement('button');
            jsonBtn.type = 'button';
            jsonBtn.className = 'btn btn-sm ' + (this.currentMode === 'json' ? 'btn-primary' : 'btn-outline-primary');
            jsonBtn.textContent = this._t('modal.jsonMode', 'JSON Mode');
            jsonBtn.onclick = function() { this.switchMode('json'); }.bind(this);

            toolbar.appendChild(formBtn);
            toolbar.appendChild(jsonBtn);

            return toolbar;
        },

        /**
         * Create action buttons
         */
        _createActions: function() {
            var actions = document.createElement('div');
            actions.className = 'mt-3 d-flex gap-2';

            var validateBtn = document.createElement('button');
            validateBtn.className = 'btn btn-warning';
            validateBtn.textContent = this._t('modal.validate', 'Validate');
            validateBtn.onclick = function() { this.validate(); }.bind(this);

            var diffBtn = document.createElement('button');
            diffBtn.className = 'btn btn-info';
            diffBtn.textContent = this._t('editor.previewChanges', 'Preview Changes');
            diffBtn.onclick = function() { this.showDiff(); }.bind(this);

            var saveBtn = document.createElement('button');
            saveBtn.className = 'btn btn-success';
            saveBtn.textContent = this._t('modal.save', 'Save');
            saveBtn.onclick = function() { this.save(); }.bind(this);

            var cancelBtn = document.createElement('button');
            cancelBtn.className = 'btn btn-secondary';
            cancelBtn.textContent = this._t('modal.cancel', 'Cancel');
            cancelBtn.onclick = function() { this.cancel(); }.bind(this);

            actions.appendChild(validateBtn);
            actions.appendChild(diffBtn);
            actions.appendChild(saveBtn);
            actions.appendChild(cancelBtn);

            return actions;
        },

        /**
         * i18n helper with fallback (returns fallback when key is missing,
         * since __() returns the key itself for missing translations).
         */
        _t: function(key, fallback) {
            if (window.__ && typeof window.__ === 'function') {
                var v = window.__(key);
                return (v && v !== key) ? v : fallback;
            }
            return fallback;
        },

        /**
         * Render form editor
         */
        _renderFormEditor: function() {
            var area = document.getElementById(this.containerId + '-area');
            if (!area) return;

            area.innerHTML = '';
            var formHost = document.createElement('div');
            formHost.id = this.containerId + '-form';
            area.appendChild(formHost);

            // Get schema for current config type
            var schema = window.DashboardSchemaService.getSchemaForType(this.configType);
            if (!schema) {
                formHost.innerHTML = '<div class="alert alert-warning mb-0">Schema unavailable for this config type.</div>';
                return;
            }

            window.DashboardFormBuilder.build(formHost.id, schema, this.draftData);
            window.DashboardFormBuilder.bindSync(formHost.id, function(data) {
                this.draftData = data;
            }.bind(this));
        },

        /**
         * Render JSON editor
         */
        _renderJsonEditor: function() {
            var area = document.getElementById(this.containerId + '-area');
            if (!area) return;

            var textareaId = this.containerId + '-json';
            var textarea = document.createElement('textarea');
            textarea.id = textareaId;
            textarea.style.display = 'none';
            area.appendChild(textarea);

            var editorContainer = document.createElement('div');
            editorContainer.id = textareaId + '-editor';
            editorContainer.className = 'monaco-editor-container';
            editorContainer.style.height = '400px';
            editorContainer.style.border = '1px solid #404040';
            editorContainer.style.borderRadius = '4px';
            area.appendChild(editorContainer);

            // Initialize Monaco Editor
            var jsonValue = JSON.stringify(this.draftData, null, 2);
            window.DashboardMonacoEditor.init(textareaId, {
                value: jsonValue,
                onChange: function(newValue) {
                    try {
                        this.draftData = window.DashboardUtils.parseJsonLenient(newValue);
                    } catch (e) {
                        // Invalid JSON, ignore
                    }
                }.bind(this)
            }).then(function() {
                window.DashboardMonacoEditor.setValue(textareaId, jsonValue);
            });
        },

        /**
         * Switch editor mode
         */
        switchMode: function(mode) {
            if (mode === this.currentMode) return;

            if (mode === 'json') {
                // Form -> JSON: serialize data
                this.draftData = window.DashboardFormBuilder.collectData(this.containerId + '-form');
            } else {
                // JSON -> Form: data already synced
            }

            this.currentMode = mode;
            this._renderEditor();
        },

        /**
         * Validate current data.
         *
         * Two layers, both required: the schema validator catches shape/type mistakes, and
         * the server-side pre-flight catches what a schema cannot see — a policy name that
         * is not registered, a cluster that does not exist, a destination without a scheme.
         * The second layer is what turns "saved fine, 500 at runtime" into "refused, here
         * is the field".
         */
        validate: async function() {
            var schemaOk = true;

            if (window.DashboardSchemaValidator && window.DashboardValidationPanel) {
                var schema = window.DashboardSchemaService.getSchemaForType(this.configType);
                var result = window.DashboardSchemaValidator.validate(this.draftData, schema);
                if (!result.valid) {
                    window.DashboardValidationPanel.show(result);
                    schemaOk = false;
                }
            }

            var report = await this._runServerValidation();
            this._renderValidationReport(report);

            return schemaOk && (!report || report.ok !== false);
        },

        /**
         * Ask the gateway to check the draft without saving it. Returns null when the
         * endpoint is unavailable, in which case we fall back to schema-only validation
         * rather than blocking the save.
         */
        _runServerValidation: async function() {
            if (this.configType !== 'route' && this.configType !== 'cluster') return null;

            var payload = { kind: this.configType, config: this.draftData };

            if (this.configType === 'route') {
                var destinations = this.draftData && (this.draftData.Destinations || this.draftData.destinations);
                payload.clusterWillBeCreated = !!(destinations && Object.keys(destinations).length);
            }

            try {
                return await window.DashboardApi.post('/api/config/validate', payload, { silent: true });
            } catch (err) {
                console.warn('[ConfigEditor] server-side validation unavailable:', err);
                return null;
            }
        },

        /** Render the pre-flight findings into the shared panel above the action buttons. */
        _renderValidationReport: function(report) {
            if (!report) return;

            var panel = document.getElementById('validationPanel');
            if (!panel) return;

            var errors = report.errors || [];
            var warnings = report.warnings || [];
            if (errors.length === 0 && warnings.length === 0) {
                panel.style.display = 'none';
                panel.innerHTML = '';
                return;
            }

            function esc(value) {
                return String(value == null ? '' : value)
                    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
            }

            var html = '<div class="card-panel" style="border-left:3px solid ' + (errors.length ? '#dc2626' : '#f59e0b') + ';">' +
                '<div style="font-weight:600;font-size:13px;margin-bottom:8px;">' +
                (errors.length ? '配置检查未通过（' + errors.length + ' 个错误）' : '配置检查通过，但有 ' + warnings.length + ' 条提醒') +
                '</div><ul style="margin:0;padding-left:18px;font-size:12.5px;line-height:1.7;">';

            errors.concat(warnings).forEach(function(issue) {
                var colour = issue.severity === 'Error' ? '#b91c1c' : '#b45309';
                html += '<li style="color:' + colour + ';">' +
                    '<code style="font-size:11.5px;background:#f1f5f9;padding:1px 5px;border-radius:4px;">' + esc(issue.field) + '</code> ' +
                    esc(issue.message) +
                    (issue.hint ? '<br><span style="color:#64748b;font-size:11.5px;">' + esc(issue.hint) + '</span>' : '') +
                    '</li>';
            });

            html += '</ul></div>';
            panel.innerHTML = html;
            panel.style.display = 'block';
        },

        /**
         * Destructive-change guard. Removing a destination silently drops that backend out
         * of rotation; a plain confirmation is enough to stop the accidental case.
         */
        _confirmDestructiveChange: function() {
            var notes = this._describeChanges();
            if (notes.length === 0) return true;

            var destructive = notes.some(function(note) {
                return note.indexOf('删除目标节点') === 0 || note.indexOf('删除') === 0;
            });
            if (!destructive) return true;

            return window.confirm(notes.join('\n') + '\n\n确认应用这些变更？');
        },

        /**
         * Turn a route/cluster diff into sentences a human can act on, instead of a
         * two-column JSON diff where "10.0.0.5:8080" disappearing is easy to miss.
         */
        _describeChanges: function() {
            var before = this.originalData || {};
            var after = this.draftData || {};
            var notes = [];

            function destinations(data) {
                var d = data && (data.Destinations || data.destinations);
                return d && typeof d === 'object' ? d : {};
            }

            if (this.configType === 'cluster') {
                var oldDests = destinations(before);
                var newDests = destinations(after);

                Object.keys(oldDests).forEach(function(name) {
                    if (!newDests[name]) {
                        notes.push('删除目标节点 ' + name + '：' + (oldDests[name].Address || oldDests[name].address || ''));
                    } else {
                        var oldAddress = oldDests[name].Address || oldDests[name].address;
                        var newAddress = newDests[name].Address || newDests[name].address;
                        if (oldAddress !== newAddress) {
                            notes.push('修改目标节点 ' + name + ' 地址：' + oldAddress + ' → ' + newAddress);
                        }
                    }
                });
                Object.keys(newDests).forEach(function(name) {
                    if (!oldDests[name]) {
                        notes.push('新增目标节点 ' + name + '：' + (newDests[name].Address || newDests[name].address || ''));
                    }
                });

                if (before.LoadBalancingPolicy !== after.LoadBalancingPolicy) {
                    notes.push('负载均衡策略：' + (before.LoadBalancingPolicy || '默认') + ' → ' + (after.LoadBalancingPolicy || '默认'));
                }

                var oldAffinity = before.SessionAffinity && before.SessionAffinity.Enabled;
                var newAffinity = after.SessionAffinity && after.SessionAffinity.Enabled;
                if (!!oldAffinity !== !!newAffinity) {
                    notes.push('会话亲和：' + (newAffinity ? '启用' : '关闭'));
                }
            }

            if (this.configType === 'route') {
                var oldPath = before.Match && before.Match.Path;
                var newPath = after.Match && after.Match.Path;
                if (oldPath !== newPath) {
                    notes.push('匹配路径：' + (oldPath || '（空）') + ' → ' + (newPath || '（空）'));
                }

                if (before.ClusterId !== after.ClusterId) {
                    notes.push('目标集群：' + (before.ClusterId || '（空）') + ' → ' + (after.ClusterId || '（空）'));
                }

                ['AuthorizationPolicy', 'CorsPolicy', 'RateLimiterPolicy', 'OutputCachePolicy'].forEach(function(field) {
                    if ((before[field] || '') !== (after[field] || '')) {
                        notes.push('策略 ' + field + '：' + (before[field] || '不启用') + ' → ' + (after[field] || '不启用'));
                    }
                });
            }

            return notes;
        },

        /**
         * Save configuration
         */
        save: async function() {
            if (!(await this.validate())) {
                return false;
            }

            if (!this._confirmDestructiveChange()) {
                return false;
            }

            try {
                var response;
                
                if (this.configType === 'cluster') {
                    response = await window.DashboardApi.endpoints.saveCluster(
                        this.configId, 
                        this.draftData
                    );
                } else if (this.configType === 'route') {
                    response = await window.DashboardApi.endpoints.saveRoute(
                        this.configId, 
                        this.draftData
                    );
                } else {
                    // Full config import
                    response = await window.DashboardApi.endpoints.importConfig(
                        this.draftData
                    );
                }

                if (response.code === 200) {
                    this.originalData = JSON.parse(JSON.stringify(this.draftData));
                    if (window.DashboardModals) window.DashboardModals.showSuccess('Saved successfully');
                    return true;
                } else {
                    if (window.DashboardModals) window.DashboardModals.showError('Save failed: ' + (response.message || 'Unknown error'));
                    return false;
                }
            } catch (error) {
                console.error('[ConfigEditor] Save failed:', error);
                /* The gateway refuses invalid config with 400 + a structured report, but the
                   API layer flattens the body into the message. Re-run the check so the
                   panel shows every finding instead of just the first. */
                var report = await this._runServerValidation();
                this._renderValidationReport(report);
                if (window.DashboardModals) window.DashboardModals.showError('Save failed: ' + error.message);
                return false;
            }
        },

        /**
         * Show diff preview with a plain-language summary above the field-level diff.
         */
        showDiff: function() {
            if (!window.DashboardDiffPanel) return;

            var notes = this._describeChanges();
            window.DashboardDiffPanel.show(this.originalData, this.draftData, {
                title: this._t('editor.previewChanges', 'Preview Changes'),
                summary: notes.length ? { description: notes.join(' · ') } : null
            });
        },

        /**
         * Import configuration from file
         */
        import: async function(file) {
            try {
                var text = await file.text();
                var config = window.DashboardUtils.parseJsonLenient(text);

                // Pre-validate
                var validation = window.DashboardSchemaValidator.validate(
                    config, 
                    window.ConfigurationSchema
                );

                if (!validation.valid) {
                    window.DashboardValidationPanel.show(validation);
                    return false;
                }

                // Call backend import
                var response = await window.DashboardApi.endpoints.importConfig(config);

                if (response.code === 200) {
                    var data = response.data;
                    var msg = response.message || 'Import successful';
                    if (data && (data.importedRoutes || data.importedClusters)) {
                        msg += ' (' + (data.importedRoutes || 0) + ' routes, ' + (data.importedClusters || 0) + ' clusters)';
                    }
                    if (window.DashboardModals) window.DashboardModals.showSuccess(msg);
                    window.location.reload();
                    return true;
                } else {
                    if (window.DashboardModals) window.DashboardModals.showError(response.message || 'Import failed');
                }

                return false;
            } catch (error) {
                console.error('[ConfigEditor] Import failed:', error);
                if (window.DashboardModals) window.DashboardModals.showError('Import failed: ' + error.message);
                return false;
            }
        },

        /**
         * Export configuration
         */
        export: async function() {
            try {
                var response = await window.DashboardApi.endpoints.exportConfig();

                if (response.code === 200) {
                    var blob = new Blob(
                        [JSON.stringify(response.data, null, 2)], 
                        { type: 'application/json' }
                    );
                    var url = URL.createObjectURL(blob);
                    var a = document.createElement('a');
                    a.href = url;
                    a.download = 'yarp-config-' + Date.now() + '.json';
                    a.click();
                    URL.revokeObjectURL(url);
                    return true;
                }

                return false;
            } catch (error) {
                console.error('[ConfigEditor] Export failed:', error);
                if (window.DashboardModals) window.DashboardModals.showError('Export failed: ' + error.message);
                return false;
            }
        },

        /**
         * Cancel editing
         */
        cancel: function() {
            // Close modal or revert changes
            var modal = document.getElementById(this.containerId)?.closest('.modal');
            if (modal) {
                var bsModal = bootstrap.Modal.getInstance(modal);
                if (bsModal) {
                    bsModal.hide();
                }
            }
        }
    };
})();
