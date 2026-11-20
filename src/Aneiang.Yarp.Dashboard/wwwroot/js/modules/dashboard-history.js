/**
 * Dashboard History Module - configuration timeline, diff and rollback.
 */
(function() {
    'use strict';

    const HistoryModule = {
        name: 'history',
        initialized: false,
        entries: [],
        filtered: [],
        selectedVersionId: null,
        filters: {
            query: '',
            type: '',
            sort: 'desc'
        },

        init: async function() {
            if (this.initialized) return;
            this.setupEvents();
            this.initialized = true;
        },

        loadHistory: async function() {
            const container = this.el('history-content');
            try {
                if (!container) return;
                container.innerHTML = this.renderLoading();

                const [data, queueMetrics] = await Promise.all([
                    window.DashboardApi.endpoints.getHistory(),
                    /* Supporting diagnostic only: a failure here must never blank the timeline. */
                    window.DashboardApi.get('/api/config/snapshot/metrics', null, { silent: true })
                        .catch(() => null)
                ]);
                this.entries = Array.isArray(data) ? data.map((entry, index) => this.normalizeEntry(entry, index)) : [];
                this.applyFilters();
                this.renderStats();
                this.renderQueueMetrics(queueMetrics);
                this.renderHistory();
                this.selectInitialEntry();
            } catch (error) {
                console.error('[History] Load failed:', error);
                if (container) container.innerHTML = this.renderError(error);
            }
        },

        /* Snapshots are enqueued and written in the background. When that queue backs up or
         * drops an item, the only symptom in the timeline is a version that never appears —
         * indistinguishable from "nobody changed anything". This bar is where the queue
         * reports itself, and it is deliberately always visible: zero failures is also an
         * answer worth having after a busy day. */
        renderQueueMetrics: function(metrics) {
            const host = this.el('history-queue');
            if (!host) return;
            if (!metrics) { host.classList.add('d-none'); return; }

            const queued = Number(metrics.queueLength || 0);
            const processed = Number(metrics.processedCount || 0);
            const failed = Number(metrics.failedCount || 0);
            const dropped = Number(metrics.droppedCount || 0);
            const capacity = Number(metrics.snapshotQueueCapacity || 0);
            const saturated = capacity > 0 && queued >= capacity;
            const degraded = failed > 0 || dropped > 0 || saturated;

            const item = (icon, label, value, bad) =>
                '<span class="hq-item"><i class="bi ' + icon + '"></i>' + this.escape(label) +
                ' <span class="hq-num' + (bad ? ' bad' : '') + '">' + value + '</span></span>';

            host.innerHTML =
                item('bi-list-ol', __('history.queue.queued'), queued, saturated) +
                item('bi-check2-circle', __('history.queue.processed'), processed, false) +
                item('bi-exclamation-triangle', __('history.queue.failed'), failed, failed > 0) +
                item('bi-slash-circle', __('history.queue.dropped'), dropped, dropped > 0) +
                item('bi-inboxes', __('history.queue.capacity'), capacity || '-', false) +
                (degraded
                    ? '<span class="hq-item ms-auto"><i class="bi bi-info-circle"></i>' +
                      this.escape(__('history.queue.degraded')) + '</span>'
                    : '');

            host.classList.remove('d-none');
            host.classList.toggle('degraded', degraded);
        },

        normalizeEntry: function(entry, index) {
            const routeCount = Number(entry.routeCount ?? entry.RouteCount ?? 0);
            const clusterCount = Number(entry.clusterCount ?? entry.ClusterCount ?? 0);
            const versionId = entry.versionId || entry.VersionId || '';
            const description = entry.description || entry.Description || '';
            const clientIp = entry.clientIp || entry.ClientIp || '';
            const timestamp = entry.timestamp || entry.Timestamp || null;
            const changeType = entry.changeType || entry.ChangeType || this.inferType(description);
            return {
                versionId,
                shortId: versionId ? versionId.substring(0, 10) : '-',
                timestamp,
                timestampMs: timestamp ? new Date(timestamp).getTime() : 0,
                description,
                clientIp,
                routeCount,
                clusterCount,
                totalItems: Number(entry.totalItems ?? entry.TotalItems ?? routeCount + clusterCount),
                configSize: Number(entry.configSize ?? entry.ConfigSize ?? 0),
                isLatest: Boolean(entry.isLatest ?? entry.IsLatest ?? index === 0),
                changeType
            };
        },

        applyFilters: function() {
            const query = this.filters.query.trim().toLowerCase();
            let rows = this.entries.filter(entry => {
                const matchesType = !this.filters.type || entry.changeType === this.filters.type;
                const searchable = [entry.versionId, entry.description, entry.clientIp, entry.changeType].join(' ').toLowerCase();
                const matchesQuery = !query || searchable.includes(query);
                return matchesType && matchesQuery;
            });

            rows = rows.sort((a, b) => this.filters.sort === 'asc'
                ? a.timestampMs - b.timestampMs
                : b.timestampMs - a.timestampMs);

            this.filtered = rows;
        },

        renderStats: function() {
            const latest = this.entries.slice().sort((a, b) => b.timestampMs - a.timestampMs)[0];
            this.setText('history-stat-total', String(this.entries.length));
            this.setText('history-stat-latest', latest ? this.relativeTime(latest.timestamp) : '--');
            this.setText('history-stat-routes', latest ? String(latest.routeCount) : '--');
            this.setText('history-stat-clusters', latest ? String(latest.clusterCount) : '--');
        },

        renderHistory: function() {
            const container = this.el('history-content');
            if (!container) return;

            if (this.entries.length === 0) {
                container.innerHTML = this.renderEmpty(__('history.noSnapshot'), __('history.noSnapshotHint'));
                this.renderDetail(null);
                return;
            }

            if (this.filtered.length === 0) {
                container.innerHTML = this.renderEmpty(__('history.noMatch'), __('history.noMatchHint'));
                this.renderDetail(null);
                return;
            }

            container.innerHTML = '<div class="history-timeline">' + this.filtered.map(entry => this.renderEntry(entry)).join('') + '</div>';
        },

        renderEntry: function(entry) {
            const active = entry.versionId === this.selectedVersionId ? ' active' : '';
            return `
                <div class="history-item" data-version="${this.escape(entry.versionId)}">
                    <div class="history-dot ${this.escape(entry.changeType)}"><i class="bi ${this.iconForType(entry.changeType)}"></i></div>
                    <div class="history-entry-card${active}" data-action="select" data-version="${this.escape(entry.versionId)}">
                        <div class="d-flex justify-content-between gap-3 align-items-start mb-2">
                            <div class="min-w-0">
                                <div class="d-flex flex-wrap gap-2 align-items-center mb-1">
                                    ${this.badgeForType(entry.changeType)}
                                    ${entry.isLatest ? '<span class="badge bg-success">' + __('history.currentLatest') + '</span>' : ''}
                                </div>
                                <h6 class="mb-1">${this.escape(entry.description || __('history.unnamedSnapshot'))}</h6>
                                <div class="history-version">${this.escape(entry.versionId)}</div>
                            </div>
                            <div class="text-end text-muted small text-nowrap">
                                <div>${this.formatDate(entry.timestamp)}</div>
                                <div>${this.relativeTime(entry.timestamp)}</div>
                            </div>
                        </div>
                        <div class="history-meta mb-3">
                            <span><i class="bi bi-signpost-split me-1"></i>${entry.routeCount} ${__('history.routes')}</span>
                            <span><i class="bi bi-hdd-network me-1"></i>${entry.clusterCount} ${__('history.clusters')}</span>
                            <span><i class="bi bi-pc-display me-1"></i>${this.escape(entry.clientIp || '-')}</span>
                            <span><i class="bi bi-database me-1"></i>${this.formatBytes(entry.configSize)}</span>
                        </div>
                        <div class="history-action-bar">
                            <button class="btn btn-sm btn-outline-secondary" data-action="copy" data-version="${this.escape(entry.versionId)}"><i class="bi bi-copy me-1"></i>${__('history.copyVersionId')}</button>
                            <button class="btn btn-sm btn-outline-info" data-action="diff" data-version="${this.escape(entry.versionId)}"><i class="bi bi-file-diff me-1"></i>${__('history.diff')}</button>
                            ${entry.isLatest ? '' : '<button class="btn btn-sm btn-outline-warning" data-action="rollback" data-version="' + this.escape(entry.versionId) + '"><i class="bi bi-arrow-counterclockwise me-1"></i>' + __('history.rollbackShort') + '</button>'}
                        </div>
                    </div>
                </div>`;
        },

        renderDetail: function(entry) {
            const detail = this.el('history-detail');
            if (!detail) return;

            if (!entry) {
                detail.innerHTML = `
                    <div class="history-detail-header">
                        <i class="bi bi-info-circle me-1 text-muted"></i><span>${__('history.detail.select')}</span>
                    </div>
                    <div class="history-detail-body text-muted">${__('history.detail.empty')}</div>`;
                return;
            }

            detail.innerHTML = `
                <div class="history-detail-header">
                    <div class="d-flex justify-content-between gap-2 align-items-center">
                        <span class="text-truncate">${this.escape(entry.description || __('history.unnamedSnapshot'))}</span>
                        ${entry.isLatest ? '<span class="badge bg-success">' + __('history.latest') + '</span>' : ''}
                    </div>
                </div>
                <div class="history-detail-body">
                    <div class="mb-3">
                        <div class="text-muted small mb-1">${__('history.versionId')}</div>
                        <code class="d-block p-2 rounded" style="background: var(--surface-2);word-break:break-all;">${this.escape(entry.versionId)}</code>
                    </div>
                    <div class="row g-2 mb-3">
                        <div class="col-6"><div class="border rounded p-2"><div class="text-muted small">${__('history.routes')}</div><div class="fw-bold">${entry.routeCount}</div></div></div>
                        <div class="col-6"><div class="border rounded p-2"><div class="text-muted small">${__('history.clusters')}</div><div class="fw-bold">${entry.clusterCount}</div></div></div>
                    </div>
                    <div class="small mb-3">
                        <div class="d-flex justify-content-between py-2 border-bottom"><span class="text-muted">${__('history.createdTime')}</span><span>${this.formatDate(entry.timestamp)}</span></div>
                        <div class="d-flex justify-content-between py-2 border-bottom"><span class="text-muted">${__('history.sourceIp')}</span><span>${this.escape(entry.clientIp || '-')}</span></div>
                        <div class="d-flex justify-content-between py-2 border-bottom"><span class="text-muted">${__('history.configSize')}</span><span>${this.formatBytes(entry.configSize)}</span></div>
                    </div>
                    <div class="history-action-bar">
                        <button class="btn btn-sm btn-outline-secondary" data-action="copy" data-version="${this.escape(entry.versionId)}"><i class="bi bi-copy me-1"></i>${__('history.copyVersionId')}</button>
                        <button class="btn btn-sm btn-outline-info" data-action="diff" data-version="${this.escape(entry.versionId)}"><i class="bi bi-file-diff me-1"></i>${__('history.viewDiff')}</button>
                        ${entry.isLatest ? '' : '<button class="btn btn-sm btn-warning" data-action="rollback" data-version="' + this.escape(entry.versionId) + '"><i class="bi bi-arrow-counterclockwise me-1"></i>' + __('history.rollbackToVersion') + '</button>'}
                    </div>
                </div>`;
        },

        selectInitialEntry: function() {
            const selected = this.entries.find(e => e.versionId === this.selectedVersionId) || this.filtered[0] || this.entries[0];
            this.selectedVersionId = selected ? selected.versionId : null;
            this.renderDetail(selected || null);
            this.markActive();
        },

        selectEntry: function(versionId) {
            this.selectedVersionId = versionId;
            const entry = this.entries.find(e => e.versionId === versionId);
            this.renderDetail(entry || null);
            this.markActive();
        },

        markActive: function() {
            document.querySelectorAll('.history-entry-card').forEach(card => {
                card.classList.toggle('active', card.getAttribute('data-version') === this.selectedVersionId);
            });
        },

        rollback: async function(versionId) {
            const entry = this.entries.find(e => e.versionId === versionId);
            const label = entry ? `${entry.shortId} - ${entry.description || ''}` : versionId;
            window.DashboardModals.showConfirm(__('history.rollbackConfirm').replace('{id}', label), async function() {
                try {
                    await window.DashboardApi.endpoints.rollbackConfig(entry.versionId);
                    window.DashboardModals.showSuccess(__('history.rollbackSuccess'));
                    setTimeout(() => HistoryModule.loadHistory(), 500);
                } catch (e) { window.DashboardModals.showError(__('history.rollbackFailed')); }
            }, null, { danger: true, confirmText: __('history.rollback') });
        },

        createSnapshot: async function() {
            const description = prompt(__('history.snapshotPrompt'), __('history.snapshotPrompt') + ': ' + new Date().toLocaleString());
            if (description === null) return;

            try {
                await window.DashboardApi.endpoints.createSnapshot(description || 'Manual snapshot');
                this.toast(__('history.snapshotCreated'), 'success');
                this.loadHistory();
            } catch (error) {
                console.error('[History] Snapshot failed:', error);
                this.toast(__('history.snapshotFailed') + ': ' + (error.message || ''), 'error');
            }
        },

        clearHistory: async function() {
            if (this.entries.length === 0) {
                this.toast(__('history.noRecordsToClear'), 'info');
                return;
            }

            window.DashboardModals.showConfirm(__('history.clearAllConfirm'), async function() {
                try {
                    await window.DashboardApi.endpoints.clearConfigHistory();
                    window.DashboardModals.showSuccess(__('history.clearAllSuccess'));
                    setTimeout(() => HistoryModule.loadHistory(), 500);
                } catch (e) { window.DashboardModals.showError(__('history.clearAllFailed')); }
            }, null, { danger: true });
        },

        copyVersionId: function(versionId) {
            if (window.DashboardUtils?.copyToClipboard) {
                window.DashboardUtils.copyToClipboard(versionId).then(success => {
                    if (success) this.toast(__('history.versionIdCopied'), 'success');
                });
                return;
            }
            navigator.clipboard?.writeText(versionId);
            this.toast(__('history.versionIdCopied'), 'success');
        },

        showDiff: async function(versionId) {
            try {
                const data = await window.DashboardApi.endpoints.configDiff(versionId);
                if (window.DashboardDiffPanel) {
                    window.DashboardDiffPanel.showStructured(data, {
                        title: __('history.configDiff') + ' (' + versionId.substring(0, 10) + ')',
                        summary: data.summary
                    });
                } else {
                    this.toast(__('history.diffPanelNotLoaded'), 'error');
                }
            } catch (error) {
                console.error('[History] Diff failed:', error);
                this.toast(__('history.diffLoadFailed') + ': ' + (error.message || ''), 'error');
            }
        },

        /* Whole-config integrity check — GET /api/config/integrity.
         *
         * The endpoint landed with the pre-flight work, but no screen ever called it: the
         * report was reachable only with curl. It answers a different question than the
         * timeline does — not "what changed" but "is what is live right now internally
         * consistent". Dangling cluster references, clusters with no targets and duplicate
         * match paths are exactly the states that accumulate over many single edits, and
         * each of them fails only at request time. */
        checkIntegrity: async function() {
            const body = this.el('integrity-body');
            const modalEl = this.el('integrity-modal');
            if (!body || !modalEl) return;

            /* bootstrap is optional on this page; fall back to an inline panel so the
               report never becomes unreachable just because the modal plugin is missing. */
            if (window.bootstrap?.Modal) {
                if (!this._integrityModal) this._integrityModal = new window.bootstrap.Modal(modalEl);
                this._integrityModal.show();
            } else {
                modalEl.classList.add('show');
                modalEl.style.display = 'block';
            }

            await this._runIntegrity(body);
        },

        _runIntegrity: async function(body) {
            body.innerHTML = '<div class="loading-state"><div class="loading-spinner"></div>' +
                '<div class="loading-text">' + this.escape(__('history.integrity.running')) + '</div></div>';
            try {
                /* silent: the modal has its own progress state; the global bar would keep
                   flashing behind an already-open dialog. */
                const report = await window.DashboardApi.get('/api/config/integrity', null, { silent: true });
                this.renderIntegrity(report, body);
            } catch (error) {
                console.error('[History] Integrity check failed:', error);
                body.innerHTML = '<div class="alert alert-danger mb-0">' +
                    '<i class="bi bi-exclamation-triangle me-1"></i>' +
                    this.escape(__('history.integrity.failed')) + ': ' + this.escape(error.message || '') +
                    '</div>';
            }
        },

        /* Field paths are prefixed with the owning entity by the validator
           (Routes.<id>.ClusterId, Clusters.<id>.Destinations...). Extracting the owner is
           what turns "some route is broken" into a link the operator can click. */
        _integrityOwner: function(field) {
            const match = /^(Routes|Clusters)\.([^.]+)/.exec(field || '');
            if (!match) return null;
            return {
                kind: match[1] === 'Routes' ? 'route' : 'cluster',
                id: match[2]
            };
        },

        renderIntegrity: function(report, body) {
            const errors = (report && report.errors) || [];
            const warnings = (report && report.warnings) || [];
            const ok = Boolean(report && report.ok);
            const total = errors.length + warnings.length;

            let html = '';

            if (total === 0) {
                html += '<div class="integ-summary ok">' +
                    '<i class="bi bi-check-circle-fill" style="color: var(--success-color);font-size:22px;"></i>' +
                    '<div><div class="integ-summary-num" style="color: var(--success-color);">0</div>' +
                    '<div class="small text-muted">' + this.escape(__('history.integrity.clean')) + '</div></div>' +
                    '</div>' +
                    '<div class="integ-empty"><i class="bi bi-shield-check" style="font-size:34px;opacity:.35;"></i>' +
                    '<p class="mt-2 mb-0">' + this.escape(__('history.integrity.cleanHelp')) + '</p></div>';
                body.innerHTML = html;
                return;
            }

            const tone = ok ? 'warn' : 'bad';
            html += '<div class="integ-summary ' + tone + '">' +
                '<i class="bi ' + (ok ? 'bi-exclamation-triangle-fill' : 'bi-x-octagon-fill') + '" ' +
                'style="color:' + (ok ? '#f59e0b' : '#ef4444') + ';font-size:22px;"></i>' +
                '<div><div class="integ-summary-num" style="color:' + (ok ? '#b45309' : '#b91c1c') + ';">' + total + '</div>' +
                '<div class="small text-muted">' + this.escape(ok
                    ? __('history.integrity.warnOnly')
                    : __('history.integrity.hasErrors')) + '</div></div>' +
                '<div class="ms-auto align-self-center small text-muted">' +
                this.escape(__('history.integrity.scale')
                    .replace('{errors}', errors.length)
                    .replace('{warnings}', warnings.length)) +
                '</div>' +
                '</div>';

            if (errors.length) {
                html += '<div class="integ-group-title"><i class="bi bi-x-circle me-1" style="color: var(--danger-color);"></i>' +
                    this.escape(__('history.integrity.errors')) + ' · ' + errors.length + '</div>';
                html += errors.map(item => this._integrityRow(item, 'error')).join('');
            }

            if (warnings.length) {
                html += '<div class="integ-group-title"><i class="bi bi-exclamation-triangle me-1" style="color: var(--warning-color);"></i>' +
                    this.escape(__('history.integrity.warnings')) + ' · ' + warnings.length + '</div>';
                html += warnings.map(item => this._integrityRow(item, 'warning')).join('');
            }

            body.innerHTML = html;
        },

        _integrityRow: function(item, tone) {
            const owner = this._integrityOwner(item && item.field);
            const base = (window.__dashboard && window.__dashboard.basePath) || '';
            const icon = tone === 'error' ? 'bi-x-circle-fill' : 'bi-exclamation-triangle-fill';
            const color = tone === 'error' ? '#ef4444' : '#f59e0b';
            let action = '';
            if (owner) {
                const href = base + '/' + (owner.kind === 'route' ? 'routes' : 'clusters') +
                    '?focus=' + encodeURIComponent(owner.id);
                action = '<a class="btn btn-outline-secondary" href="' + this.escape(href) + '">' +
                    '<i class="bi bi-box-arrow-up-right me-1"></i>' +
                    this.escape(__('history.integrity.open')) + '</a>';
            }
            return '<div class="integ-item ' + tone + '">' +
                '<i class="bi ' + icon + '" style="color:' + color + ';"></i>' +
                '<div>' +
                    '<div class="integ-item-msg">' + this.escape(item && item.message) + '</div>' +
                    (item && item.field ? '<div class="integ-item-field">' + this.escape(item.field) + '</div>' : '') +
                    (item && item.hint ? '<div class="integ-item-hint"><i class="bi bi-lightbulb me-1"></i>' + this.escape(item.hint) + '</div>' : '') +
                '</div>' +
                '<div class="integ-item-actions">' + action + '</div>' +
            '</div>';
        },

        setupEvents: function() {
            document.addEventListener('dashboard:shortcut:refresh', () => this.loadHistory());

            const integrityBtn = this.el('history-integrity-btn');
            if (integrityBtn) integrityBtn.addEventListener('click', () => this.checkIntegrity());
            const integrityRerun = this.el('integrity-rerun');
            if (integrityRerun) integrityRerun.addEventListener('click', () => {
                const body = this.el('integrity-body');
                if (body) this._runIntegrity(body);
            });

            document.addEventListener('click', (event) => {
                const target = event.target.closest('[data-action]');
                if (!target) return;
                const action = target.getAttribute('data-action');
                const versionId = target.getAttribute('data-version');

                if (action === 'select' && versionId) this.selectEntry(versionId);
                if (action === 'copy' && versionId) this.copyVersionId(versionId);
                if (action === 'diff' && versionId) this.showDiff(versionId);
                if (action === 'rollback' && versionId) this.rollback(versionId);
            });

            this.bindInput('history-search', value => { this.filters.query = value; this.refreshView(); });
            this.bindInput('history-type-filter', value => { this.filters.type = value; this.refreshView(); });
            this.bindInput('history-sort', value => { this.filters.sort = value || 'desc'; this.refreshView(); });

            const refresh = this.el('history-refresh');
            if (refresh) refresh.addEventListener('click', () => this.loadHistory());

            const create = this.el('history-create-snapshot');
            if (create) create.addEventListener('click', () => this.createSnapshot());

            const clearAll = this.el('history-clear-all');
            if (clearAll) clearAll.addEventListener('click', () => this.clearHistory());

            const clear = this.el('history-clear-filter');
            if (clear) clear.addEventListener('click', () => {
                this.filters = { query: '', type: '', sort: 'desc' };
                this.setValue('history-search', '');
                this.setValue('history-type-filter', '');
                this.setValue('history-sort', 'desc');
                this.refreshView();
            });
        },

        refreshView: function() {
            this.applyFilters();
            this.renderHistory();
            this.selectInitialEntry();
        },

        bindInput: function(id, handler) {
            const el = this.el(id);
            if (!el) return;
            const eventName = el.tagName === 'SELECT' ? 'change' : 'input';
            el.addEventListener(eventName, () => handler(el.value));
        },

        renderLoading: function() {
            return `<div class="history-empty-state"><div class="spinner-border text-primary mb-3"></div><div class="text-muted">${__('history.loadingMsg')}</div></div>`;
        },

        renderError: function(error) {
            return `<div class="history-empty-state"><i class="bi bi-exclamation-triangle text-danger" style="font-size:42px;"></i><h5 class="mt-3">${__('loading.loadFailed')}</h5><p class="text-muted">${this.escape(error.message || 'Unknown error')}</p><button class="btn btn-outline-primary" onclick="HistoryModule.loadHistory()">${__('loading.retry')}</button></div>`;
        },

        renderEmpty: function(title, message) {
            return `<div class="history-empty-state"><i class="bi bi-clock-history text-muted" style="font-size:52px;"></i><h5 class="mt-3">${this.escape(title)}</h5><p class="text-muted mb-0">${this.escape(message)}</p></div>`;
        },

        inferType: function(description) {
            const text = String(description || '').toLowerCase();
            if (text.includes('rollback')) return 'rollback';
            if (text.includes('import')) return 'import';
            if (text.includes('deleted') || text.includes('remove')) return 'delete';
            if (text.includes('renamed')) return 'rename';
            if (text.includes('saved') || text.includes('update')) return 'update';
            return 'manual';
        },

        iconForType: function(type) {
            return ({ update: 'bi-pencil-square', delete: 'bi-trash3', rollback: 'bi-arrow-counterclockwise', import: 'bi-box-arrow-in-down', rename: 'bi-input-cursor-text', manual: 'bi-camera' })[type] || 'bi-camera';
        },

        typeLabel: function(type) {
            const labels = {
                update: __('history.type.update'),
                delete: __('history.type.delete'),
                rollback: __('history.type.rollback'),
                import: __('history.type.import'),
                rename: __('history.type.rename'),
                manual: __('history.type.manual')
            };
            return labels[type] || __('history.type.manual');
        },

        badgeForType: function(type) {
            const classes = { update: 'bg-primary', delete: 'bg-danger', rollback: 'bg-warning text-dark', import: 'bg-info text-dark', rename: 'bg-secondary', manual: 'bg-success' };
            return `<span class="badge ${classes[type] || 'bg-secondary'}">${this.escape(this.typeLabel(type))}</span>`;
        },

        formatDate: function(value) {
            if (!value) return '-';
            if (window.DashboardI18n?.formatDate) return window.DashboardI18n.formatDate(value);
            return new Date(value).toLocaleString();
        },

        relativeTime: function(value) {
            if (!value) return '--';
            const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
            if (seconds < 60) return seconds + __('index.time.secondsAgo');
            const minutes = Math.floor(seconds / 60);
            if (minutes < 60) return minutes + __('index.time.minutesAgo');
            const hours = Math.floor(minutes / 60);
            if (hours < 24) return hours + __('index.time.hoursAgo');
            return Math.floor(hours / 24) + __('index.time.daysAgo');
        },

        formatBytes: function(bytes) {
            if (!bytes) return '0 B';
            if (bytes < 1024) return bytes + ' B';
            if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
            return (bytes / 1024 / 1024).toFixed(1) + ' MB';
        },

        toast: function(message, type) {
            if (window.DashboardModals) {
                if (type === 'success') return window.DashboardModals.showSuccess(message);
                if (type === 'error') return window.DashboardModals.showError(message);
                return window.DashboardModals.showInfo(message);
            }
        },

        el: function(id) { return document.getElementById(id); },
        setText: function(id, value) { const el = this.el(id); if (el) el.textContent = value; },
        setValue: function(id, value) { const el = this.el(id); if (el) el.value = value; },
        escape: function(value) {
            if (window.DashboardUtils?.escapeHtml) return window.DashboardUtils.escapeHtml(value == null ? '' : String(value));
            return String(value == null ? '' : value).replace(/[&<>"']/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[m]));
        }
    };

    if (window.DashboardApp) {
        window.DashboardApp.registerModule('history', HistoryModule);
    }
    window.HistoryModule = HistoryModule;
})();
