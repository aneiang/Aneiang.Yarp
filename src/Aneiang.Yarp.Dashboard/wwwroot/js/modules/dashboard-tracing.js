/**
 * Distributed tracing page.
 *
 * Three areas, in the order a person actually needs them:
 *
 *   1. 状态带  - is it running, where does the data go, how much is sampled.
 *                These are the only three answers that explain an empty list, so
 *                each gets its own column instead of sharing one wrapped row with
 *                five other numbers.
 *   2. 链路列表 - one grid template for the header and every row, so durations
 *                line up and can be compared without reading digits. Each row has
 *                two lines: the request, then the route → cluster → destination hop
 *                chain - the thing you correlate with the rest of the dashboard.
 *   3. 瀑布图   - a time axis with quarter lines, labelled columns and a colour
 *                legend. Before this, bars floated in an unlabelled box.
 *
 * The list shows sampled traces only, from the same processor the exporter uses, so
 * "not here" really does mean "not traced" rather than "the UI lost it".
 */
(function () {
    'use strict';

    var POLL_MS = 5000;
    var state = {
        data: null,
        details: {},
        expanded: {},
        timer: null,
        // Last export probe, kept outside `data` so the five-second refresh does not wipe the
        // result the moment it arrives.
        probe: null
    };

    function t(key, fallback) {
        var value = typeof window.__ === 'function' ? window.__(key) : null;
        return value && value !== key ? value : (fallback || key);
    }

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function fmtMs(ms) {
        if (ms == null || isNaN(ms)) return '–';
        if (ms >= 1000) return (ms / 1000).toFixed(2) + ' s';
        if (ms >= 10) return Math.round(ms) + ' ms';
        return ms.toFixed(1) + ' ms';
    }

    function statusClass(code, status) {
        if (status === 'error' || (code && code >= 500)) return 'is-error';
        if (code && code >= 400) return 'is-warn';
        if (status === 'ok' || (code && code < 400)) return 'is-ok';
        return 'is-idle';
    }

    /* ------------------------------------------------------------------ status */

    function renderStatus() {
        var el = document.getElementById('trace-status');
        if (!el || !state.data) return;

        var s = state.data.status || {};

        var badge, badgeClass;
        if (!s.enabled) {
            badge = t('trace.status.disabled', '未启用');
            badgeClass = 'tr-badge--off';
        } else if (!s.running) {
            badge = t('trace.status.failed', '启动失败');
            badgeClass = 'tr-badge--fail';
        } else if (s.restartRequired) {
            /* Configured on, not yet in effect: the one state where "on" and
               "working" genuinely differ, so it gets its own label. */
            badge = t('trace.band.pendingRestart', '等重启生效');
            badgeClass = 'tr-badge--muted';
        } else {
            badge = t('trace.status.running', '运行中');
            badgeClass = 'tr-badge--on';
        }

        var exportText = s.exportEnabled
            ? '<span class="tr-endpoint">' + esc((s.protocol || 'otlp') + ' → ' + (s.endpoint || '-')) + '</span>'
            : '<span class="tr-badge tr-badge--muted"><i class="bi bi-hdd"></i>' +
                esc(t('trace.status.noExport', '仅内存')) + '</span>' +
              '<span class="text-muted small">' + esc(t('trace.band.noExportHint', '没有导出到 Jaeger / Tempo')) + '</span>';

        /* The list below is fed by the provider's processor, not by the exporter, so a broken
           endpoint leaves the page looking healthy. This button is the only way to find out from
           here; it is admin-only because it exercises the export credential. */
        var canProbe = s.exportEnabled && window.DashboardPermissions &&
            typeof window.DashboardPermissions.canAdminister === 'function' &&
            window.DashboardPermissions.canAdminister();

        var probeBtn = canProbe
            ? '<button type="button" class="tr-probe-btn" data-role="probe-export" title="' +
                  esc(t('trace.probe.title', '向该地址发送一个空批次，确认地址与凭据是否可用')) + '">' +
                  '<i class="bi bi-send-check"></i>' + esc(t('trace.probe.run', '测试')) +
              '</button>'
            : '';

        var buffer = (s.bufferCount || 0) + ' / ' + (s.bufferCapacity || 0);
        var spans = (s.spansCaptured || 0) +
            (s.spansDropped ? ' · ' + t('trace.status.dropped', '丢弃') + ' ' + s.spansDropped : '');

        var percent = Math.round((s.samplingRatio || 0) * 100);

        /* Authentication belongs in the export column: "where does the data go" and "does the
           collector accept us" are the same question when the answer is a 401 the exporter
           swallows. */
        var authHtml = renderAuthLine(s);

        el.innerHTML =
            '<div class="tr-band__seg">' +
                '<div class="tr-band__label" data-i18n="trace.band.state">' + esc(t('trace.band.state', '运行状态')) + '</div>' +
                '<div class="tr-band__value">' +
                    '<span class="tr-badge ' + badgeClass + '"><i class="bi ' +
                        (badgeClass === 'tr-badge--on' ? 'bi-record-circle' : 'bi-exclamation-circle') + '"></i>' +
                        esc(badge) + '</span>' +
                    '<span class="tr-svc">' + esc(s.serviceName || '-') + '</span>' +
                '</div>' +
                '<div class="tr-band__note"><code>' + esc(s.sectionName || '') + '</code></div>' +
            '</div>' +

            '<div class="tr-band__seg">' +
                '<div class="tr-band__label" data-i18n="trace.band.export">' + esc(t('trace.band.export', '数据出口')) + '</div>' +
                '<div class="tr-band__value">' + exportText + probeBtn + '</div>' +
                authHtml +
                '<div class="tr-probe" id="trace-probe"></div>' +
                '<div class="tr-band__note">' +
                    esc(t('trace.status.buffer', '内存缓冲')) + ' ' + esc(buffer) +
                    ' · ' + esc(t('trace.status.spans', '已捕获 Span')) + ' ' + esc(spans) +
                '</div>' +
            '</div>' +

            '<div class="tr-band__seg tr-band__sampling">' +
                '<div class="tr-band__label" data-i18n="trace.band.sampling">' + esc(t('trace.band.sampling', '采样率')) + '</div>' +
                '<div class="tr-band__value">' +
                    '<input type="range" class="form-range" id="trace-sampling" min="0" max="100" step="1" value="' + percent + '">' +
                    '<span class="tr-pct" id="trace-sampling-value">' + percent + '%</span>' +
                    '<button class="btn btn-sm btn-outline-primary" id="trace-sampling-apply">' +
                        esc(t('trace.sampling.apply', '应用')) + '</button>' +
                '</div>' +
                '<div class="tr-band__note">' +
                    esc(t('trace.band.samplingHint', '只影响新请求，无需重启')) +
                    ' · ' + esc(t('trace.band.filtered', '已判定 / 已丢弃')) + ' ' +
                    (s.sampledTraces || 0) + ' / ' + (s.droppedTraces || 0) +
                '</div>' +
            '</div>';

        var slider = document.getElementById('trace-sampling');
        var label = document.getElementById('trace-sampling-value');
        if (slider && label) {
            slider.addEventListener('input', function () { label.textContent = slider.value + '%'; });
        }
        var apply = document.getElementById('trace-sampling-apply');
        if (apply) {
            apply.addEventListener('click', function () {
                if (slider) applySampling(parseInt(slider.value, 10) / 100);
            });
        }

        var authManage = el.querySelector('[data-role="manage-auth"]');
        if (authManage) authManage.addEventListener('click', openAuthDialog);

        var probeBtnEl = el.querySelector('[data-role="probe-export"]');
        if (probeBtnEl) probeBtnEl.addEventListener('click', runProbe);

        paintProbe();
    }

    /* ------------------------------------------------------- export connectivity */

    /* The trace list is populated by the provider's processor, which keeps working while the
       exporter is dropping every batch. Until now the only way to notice a wrong endpoint was to
       go and look in the backend for a service name that never arrived. */

    async function runProbe() {
        if (state.probe && state.probe.loading) return;

        state.probe = { loading: true };
        paintProbe();

        try {
            var result = await DashboardApi.request('/api/tracing/verify', {
                method: 'POST',
                silent: true
            });
            state.probe = { result: result || {} };
        } catch (e) {
            // The probe itself failed (403 for a non-admin, network error from the browser, …).
            state.probe = { result: { ok: false, phase: 'unknown', detail: e.message } };
        }

        paintProbe();
    }

    function paintProbe() {
        var el = document.getElementById('trace-probe');
        if (el) el.innerHTML = probeHtml();
    }

    function probeHtml() {
        var p = state.probe;
        if (!p) return '';

        if (p.loading) {
            return '<span class="tr-probe__state is-busy">' +
                '<span class="spinner-border spinner-border-sm"></span>' +
                esc(t('trace.probe.running', '正在向该地址发送空批次…')) +
            '</span>';
        }

        var r = p.result || {};
        var ok = !!r.ok;
        var head = ok
            ? 'HTTP ' + (r.statusCode || 200) + ' OK'
            : phaseLabel(r.phase) + (r.statusCode ? ' · HTTP ' + r.statusCode : '');

        var meta = [];
        if (r.elapsedMs != null) meta.push(r.elapsedMs + ' ms');
        if (r.headerCount) meta.push(r.headerCount + ' ' + t('trace.probe.headers', '个鉴权头'));

        var html = '<span class="tr-probe__state ' + (ok ? 'is-ok' : 'is-fail') + '">' +
            '<i class="bi ' + (ok ? 'bi-check-circle-fill' : 'bi-x-circle-fill') + '"></i>' +
            esc(head) +
            (meta.length ? '<span class="tr-probe__meta">' + esc(meta.join(' · ')) + '</span>' : '') +
        '</span>';

        // Which URL was actually called matters as much as the verdict: the exporter adds
        // /v1/traces and gRPC drops the path, so the URL here can differ from what was configured.
        if (r.url) html += '<div class="tr-probe__url">' + esc(r.url) + '</div>';
        if (r.detail) html += '<div class="tr-probe__detail">' + esc(r.detail) + '</div>';
        if (r.snippet) html += '<code class="tr-probe__snippet">' + esc(r.snippet) + '</code>';

        // A bare 401 means "rejected"; the challenge says "rejected because you used the wrong
        // credential type", which is a different fix.
        if (r.challenge) {
            html += '<div class="tr-probe__challenge">' +
                esc(t('trace.probe.challengeLabel', '服务端要求')) + ' <code>' + esc(r.challenge) + '</code>' +
            '</div>';
        }

        if (r.note) html += '<div class="tr-probe__note">' + esc(noteLabel(r.note)) + '</div>';

        html += '<div class="tr-probe__note">' + esc(ok
            ? t('trace.probe.caveat', '这只证明地址可达、凭据被接受，不证明 span 已入库。')
            : t('trace.probe.caveatFail', '导出器仍会照常尝试并静默丢弃，直到地址恢复。')) + '</div>';

        return html;
    }

    function phaseLabel(phase) {
        switch (phase) {
            case 'config': return t('trace.probe.phase.config', '配置不可用');
            case 'dns': return t('trace.probe.phase.dns', '域名解析失败');
            case 'connect': return t('trace.probe.phase.connect', '连接失败');
            case 'tls': return t('trace.probe.phase.tls', 'TLS 握手失败');
            case 'protocol': return t('trace.probe.phase.protocol', '协议不匹配');
            case 'timeout': return t('trace.probe.phase.timeout', '超时无响应');
            case 'http': return t('trace.probe.phase.http', '后端拒绝');
            default: return t('trace.probe.phase.unknown', '探测失败');
        }
    }

    function noteLabel(note) {
        if (note === 'path-appended') return t('trace.probe.note.pathAppended', '已自动补上 /v1/traces');
        if (note === 'grpc-path-ignored') return t('trace.probe.note.grpcPathIgnored', 'gRPC 不使用 Endpoint 中的路径');
        if (note === 'grpc-no-h2c') return t('trace.probe.note.grpcNoH2c', '该端口已连通但不接受明文 HTTP/2；gRPC 通常走另一个端口');
        if (note === 'auth-challenge') return t('trace.probe.note.authChallenge', '该地址不接受当前的认证方式：请看下面的 WWW-Authenticate，凭据本身可能是对的');
        return note;
    }

    async function applySampling(ratio) {
        try {
            await DashboardApi.request('/api/tracing/sampling', {
                method: 'POST',
                body: { ratio: ratio },
                silent: true
            });
            toast(t('trace.sampling.applied', '采样率已更新'));
            await load(true);
        } catch (e) {
            toast(t('trace.sampling.failed', '采样率更新失败') + ': ' + e.message, true);
        }
    }

    /* ------------------------------------------------------- export authentication */

    /* Shows whether exports carry credentials. The server only ever sends a masked
       value, so nothing on this line can be copied into a curl command — which is
       the point. */
    function renderAuthLine(s) {
        var auth = s.auth || {};
        var headers = auth.headers || [];
        var canManage = window.DashboardPermissions &&
            typeof window.DashboardPermissions.canAdminister === 'function' &&
            window.DashboardPermissions.canAdminister();

        var stateHtml;
        if (auth.configured) {
            var names = headers.map(function (h) { return h.name; }).join(', ');
            stateHtml =
                '<span class="tr-auth-state tr-auth-state--on"><i class="bi bi-shield-lock"></i>' +
                    esc(t('trace.auth.configured', '已鉴权')) + ' ' + esc(auth.count || headers.length) + ' ' +
                    esc(t('trace.auth.items', '项')) +
                '</span>' +
                '<span class="tr-auth-names">' + esc(names) + '</span>';
        } else {
            stateHtml =
                '<span class="tr-auth-state tr-auth-state--off"><i class="bi bi-shield-slash"></i>' +
                    esc(t('trace.auth.notConfigured', '未配置鉴权')) +
                '</span>';
        }

        var sourceHint = '';
        if (headers.length === 1) {
            sourceHint = '<span class="tr-auth-source">' + esc(sourceLabel(headers[0].source)) + '</span>';
        }

        var manage = canManage
            ? '<button type="button" class="tr-auth-link" data-role="manage-auth">' +
                  esc(t('trace.auth.manage', '管理')) +
              '</button>'
            : '';

        return '<div class="tr-auth-line">' + stateHtml + sourceHint + manage + '</div>';
    }

    function sourceLabel(source) {
        if (source === 'config') return t('trace.auth.source.config', '配置文件');
        if (source === 'panel') return t('trace.auth.source.panel', '面板');
        return t('trace.auth.source.env', '环境变量');
    }

    function authRowHtml(header) {
        // A header that comes from appsettings.json wins over anything the panel writes, so letting
        // it be edited here would produce a save that reports success and changes nothing. The row
        // stays visible to explain where the value comes from, and is locked.
        var fromConfig = header.source === 'config';

        var placeholder = fromConfig
            ? t('trace.auth.fromConfigHint', '由配置文件提供，请修改 appsettings.json')
            : header.masked
                ? t('trace.auth.valueKeep', '留空保持不变') + ' · ' + header.masked
                : t('trace.auth.valuePlaceholder', '例如 Basic dXNlcjpwYXNzd29yZA==');

        var source = header.source
            ? '<span class="tr-auth-source">' + esc(sourceLabel(header.source)) + '</span>'
            : '<span class="tr-auth-source tr-auth-source--empty"></span>';

        var locked = fromConfig ? ' disabled' : '';

        return '<div class="tr-auth-row"' + (fromConfig ? ' data-locked="1"' : '') + '>' +
            '<input type="text" class="form-control form-control-sm" data-role="auth-name"' + locked + ' ' +
                'placeholder="' + esc(t('trace.auth.namePlaceholder', 'Authorization')) + '" ' +
                'value="' + esc(header.name || '') + '">' +
            '<input type="password" class="form-control form-control-sm" data-role="auth-value"' + locked + ' ' +
                'placeholder="' + esc(placeholder) + '" value="" autocomplete="new-password">' +
            source +
            '<button type="button" class="btn btn-sm btn-outline-danger" data-role="remove-auth-row"' + locked + ' ' +
                'title="' + esc(t('common.delete', '删除')) + '"><i class="bi bi-x-lg"></i></button>' +
        '</div>';
    }

    function openAuthDialog() {
        var existing = document.getElementById('trace-auth-modal');
        if (existing) existing.remove();

        var auth = (state.data && state.data.status && state.data.status.auth) || {};
        var headers = (auth.headers || []).map(function (h) {
            return { name: h.name, masked: h.maskedValue, source: h.source };
        });
        if (headers.length === 0)
            headers.push({ name: 'Authorization', masked: '', source: '' });

        var envHint = auth.envVariableName
            ? '<i class="bi bi-info-circle me-1"></i>' +
              esc(t('trace.auth.envDetected', '检测到环境变量')) + ' <code>' + esc(auth.envVariableName) + '</code>'
            : '';

        var html =
            '<div class="modal fade" id="trace-auth-modal" tabindex="-1" aria-hidden="true">' +
              '<div class="modal-dialog modal-lg modal-dialog-centered">' +
                '<div class="modal-content">' +
                  '<div class="modal-header">' +
                    '<h5 class="modal-title"><i class="bi bi-shield-lock me-2"></i>' +
                        esc(t('trace.auth.title', '导出鉴权')) + '</h5>' +
                    '<button type="button" class="btn-close" data-bs-dismiss="modal" ' +
                        'aria-label="' + esc(t('common.close', '关闭')) + '"></button>' +
                  '</div>' +
                  '<div class="modal-body">' +
                    '<p class="tr-auth-help">' +
                        esc(t('trace.auth.help', '这些请求头会附加到每一次 OTLP 导出。已有值不会被回显，' +
                            '留空表示不使用面板覆盖、沿用配置文件或环境变量。')) +
                    '</p>' +
                    '<div class="tr-auth-rows" id="trace-auth-rows">' +
                        headers.map(authRowHtml).join('') +
                    '</div>' +
                    '<button type="button" class="btn btn-sm btn-outline-secondary mt-2" id="trace-auth-add">' +
                        '<i class="bi bi-plus-lg me-1"></i>' + esc(t('trace.auth.addRow', '添加请求头')) +
                    '</button>' +
                    '<div class="tr-auth-file mt-3">' +
                        '<i class="bi bi-file-earmark-lock me-1"></i>' +
                        esc(t('trace.auth.fileHint', '写入')) + ' <code>' +
                        esc(auth.overrideFilePath || 'Data/tracing-auth.json') + '</code>' +
                    '</div>' +
                    (envHint ? '<div class="tr-auth-file mt-1">' + envHint + '</div>' : '') +
                    '<div class="tr-auth-restart mt-2">' +
                        '<i class="bi bi-arrow-clockwise me-1"></i>' +
                        esc(t('trace.auth.restartHint', '保存后需重启网关，导出器才会使用新的凭据。')) +
                    '</div>' +
                  '</div>' +
                  '<div class="modal-footer">' +
                    '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">' +
                        esc(t('common.cancel', '取消')) + '</button>' +
                    '<button type="button" class="btn btn-primary" id="trace-auth-save">' +
                        esc(t('common.save', '保存')) + '</button>' +
                  '</div>' +
                '</div>' +
              '</div>' +
            '</div>';

        document.body.insertAdjacentHTML('beforeend', html);

        var modalEl = document.getElementById('trace-auth-modal');
        var modal = new bootstrap.Modal(modalEl);

        modalEl.querySelector('#trace-auth-add').addEventListener('click', function () {
            modalEl.querySelector('#trace-auth-rows')
                .insertAdjacentHTML('beforeend', authRowHtml({ name: '', masked: '', source: '' }));
        });

        // Delegated: rows are added and removed after this point.
        modalEl.addEventListener('click', function (event) {
            var remove = event.target.closest('[data-role="remove-auth-row"]');
            if (remove) {
                var row = remove.closest('.tr-auth-row');
                if (row) row.remove();
            }
        });

        modalEl.querySelector('#trace-auth-save').addEventListener('click', function () {
            var payload = [];
            modalEl.querySelectorAll('.tr-auth-row').forEach(function (row) {
                // Locked rows come from appsettings.json. Re-submitting them would be a no-op at
                // best and a misleading "saved" at worst.
                if (row.getAttribute('data-locked') === '1') return;

                var name = row.querySelector('[data-role="auth-name"]').value.trim();
                var value = row.querySelector('[data-role="auth-value"]').value;
                if (!name) return;              // blank row = nothing to save
                payload.push({ name: name, value: value });
            });
            saveAuth(modal, payload);
        });

        modalEl.addEventListener('hidden.bs.modal', function () { modalEl.remove(); });
        modal.show();
    }

    async function saveAuth(modal, headers) {
        try {
            await DashboardApi.request('/api/tracing/auth', {
                method: 'POST',
                body: { headers: headers },
                silent: true
            });
            modal.hide();
            toast(t('trace.auth.saved', '已保存，重启网关后生效'));
            await load(true);
        } catch (e) {
            toast(t('trace.auth.failed', '保存失败') + ': ' + e.message, true);
        }
    }

    /* ----------------------------------------------------------------- notices */

    function renderNotice() {
        var el = document.getElementById('trace-notice');
        if (!el || !state.data) return;

        var s = state.data.status || {};
        var html = '';

        if (!s.enabled) {
            html = notice('info', t('trace.notice.disabled', '链路追踪未启用'),
                t('trace.notice.disabledHint', '在配置中打开后重启网关即可开始记录。') +
                '<pre class="trace-snippet">' + esc(
                    '"Gateway": { "Dashboard": { "Tracing": {\n' +
                    '  "Enabled": true,\n' +
                    '  "Endpoint": "http://localhost:4317",\n' +
                    '  "SamplingRatio": 1.0\n} } }') + '</pre>');
        } else if (!s.running) {
            html = notice('danger', t('trace.notice.failed', '追踪已配置但启动失败'),
                esc(s.startError || '') + '<br><span class="small">' +
                esc(t('trace.notice.failedHint', '网关仍在正常转发流量，只是没有记录链路。检查导出地址后重启。')) +
                '</span>');
        } else if (!s.exportEnabled) {
            html = notice('warning', t('trace.notice.inMemory', '仅内存模式'),
                t('trace.notice.inMemoryHint', '链路只保留在下面的缓冲区里，没有导出到 Jaeger / Tempo。'));
        }

        el.innerHTML = html;
    }

    function notice(kind, title, body) {
        var icon = kind === 'danger' ? 'bi-exclamation-octagon'
            : kind === 'warning' ? 'bi-exclamation-triangle' : 'bi-info-circle';
        return '<div class="alert alert-' + kind + ' py-2 px-3 mb-3">' +
            '<div class="fw-semibold mb-1"><i class="bi ' + icon + '"></i> ' + esc(title) + '</div>' +
            '<div class="small">' + body + '</div></div>';
    }

    /* -------------------------------------------------------------------- list */

    async function renderList() {
        var el = document.getElementById('trace-list');
        var count = document.getElementById('trace-count');
        if (!el || !state.data) return;

        var traces = state.data.traces || [];
        var capacity = (state.data.status || {}).bufferCapacity || 0;
        if (count) {
            count.textContent = traces.length
                ? t('trace.list.count', '列出的条数') + ' ' + traces.length +
                  (capacity ? ' / ' + t('trace.list.capacity', '缓冲') + ' ' + capacity : '')
                : '';
        }

        if (!traces.length) {
            var s = state.data.status || {};
            var hint = !s.enabled
                ? t('trace.list.emptyDisabled', '追踪未启用，所以没有链路。')
                : (s.samplingRatio < 1
                    ? t('trace.list.emptySampled', '当前采样率较低，请求还没被抽中。把采样率调到 100% 再试。')
                    : t('trace.list.emptyHelp', '发一个请求后这里就会出现链路。'));
            el.innerHTML = '<div class="tr-empty">' +
                '<i class="bi bi-bezier2"></i>' +
                '<div class="tr-empty__title">' + esc(t('trace.list.empty', '还没有链路')) + '</div>' +
                '<div class="tr-empty__hint">' + esc(hint) + '</div></div>';
            return;
        }

        /* Relative bar scale. A shared maximum makes "which one is slow" a glance
           instead of a comparison of numbers across rows. */
        var max = 0;
        traces.forEach(function (tr) { if (tr.durationMs > max) max = tr.durationMs; });
        if (max <= 0) max = 1;

        el.innerHTML = traces.map(function (tr) { return rowHtml(tr, max); }).join('');
        el.querySelectorAll('.tr-row').forEach(function (row) {
            row.addEventListener('click', function () {
                toggleTrace(row.parentElement.getAttribute('data-trace'));
            });
            row.addEventListener('keydown', function (event) {
                if (event.key !== 'Enter' && event.key !== ' ') return;
                event.preventDefault();
                toggleTrace(row.parentElement.getAttribute('data-trace'));
            });
        });

        // Re-open whatever was open before the refresh, so a 5s poll does not collapse the
        // waterfall the operator is in the middle of reading.
        Object.keys(state.expanded).forEach(function (id) {
            if (!state.expanded[id]) return;
            var item = el.querySelector('.tr-item[data-trace="' + id + '"]');
            if (item) {
                item.classList.add('is-open');
                var body = item.querySelector('.tr-body');
                if (body) body.style.display = '';
                if (state.details[id]) renderDetail(id);
            }
        });
    }

    function rowHtml(trace, maxDuration) {
        var cls = statusClass(trace.statusCode, trace.status);
        var code = trace.statusCode
            ? '<span class="tr-code ' + cls + '">' + trace.statusCode + '</span>'
            : '<span class="tr-code ' + cls + '">' + esc(trace.status || '?') + '</span>';

        /* Hop chain on its own line: route → cluster → destination. Missing hops are
           dropped rather than rendered as "未到目标" chips, which is what made a
           short-circuited trace look like three broken values. */
        var hops = [];
        if (trace.routeId) hops.push('<span class="tr-hop tr-hop--route">' + esc(trace.routeId) + '</span>');
        if (trace.clusterId) hops.push('<span class="tr-hop tr-hop--cluster">' + esc(trace.clusterId) + '</span>');
        if (trace.destinationId) hops.push('<span class="tr-hop tr-hop--dest">' + esc(trace.destinationId) + '</span>');

        var path = hops.length
            ? hops.join('<i class="bi bi-chevron-right tr-arrow"></i>')
            : '<span class="tr-hop">' + esc(t('trace.noRoute', '未匹配路由')) + '</span>';

        if (trace.shortCircuited) {
            path += '<span class="tr-flag tr-flag--short">' +
                esc(t('trace.badge.shortCircuit', '插件拦截')) + '</span>';
        }
        if (trace.truncated) {
            path += '<span class="tr-flag tr-flag--trunc">' +
                esc(t('trace.badge.truncated', '已截断')) + '</span>';
        }

        var pct = Math.max(2, Math.min(100, Math.round((trace.durationMs / maxDuration) * 100)));
        var barCls = cls === 'is-error' ? 'is-err' : (pct >= 70 ? 'is-slow' : '');

        return '<div class="tr-item" data-trace="' + esc(trace.traceId) + '">' +
            '<div class="tr-row" role="button" tabindex="0">' +
                '<i class="bi bi-chevron-right tr-row__caret"></i>' +
                code +
                '<div class="tr-row__main">' +
                    '<div class="tr-row__name"><em>' + esc(trace.rootName || trace.traceId) + '</em></div>' +
                    '<div class="tr-row__path">' + path + '</div>' +
                '</div>' +
                '<div class="tr-row__dur">' +
                    '<b>' + esc(fmtMs(trace.durationMs)) + '</b>' +
                    '<div class="tr-durbar"><i class="' + barCls + '" style="width:' + pct + '%"></i></div>' +
                '</div>' +
                '<span class="tr-row__spans">' + (trace.spanCount || 0) + '</span>' +
                '<span class="tr-row__time">' + esc(new Date(trace.startedAt).toLocaleTimeString()) + '</span>' +
            '</div>' +
            '<div class="tr-body" style="display:none;"></div>' +
        '</div>';
    }

    async function toggleTrace(traceId) {
        var item = document.querySelector('.tr-item[data-trace="' + traceId + '"]');
        if (!item) return;

        var opening = !state.expanded[traceId];
        state.expanded[traceId] = opening;
        item.classList.toggle('is-open', opening);

        var body = item.querySelector('.tr-body');
        if (body) body.style.display = opening ? '' : 'none';
        if (!opening) return;

        if (state.details[traceId]) {
            renderDetail(traceId);
            return;
        }

        if (body) {
            body.innerHTML = '<div class="text-muted small p-2">' + esc(t('trace.loading', '加载中...')) + '</div>';
        }
        try {
            var detail = await DashboardApi.request('/api/tracing/traces/' + encodeURIComponent(traceId), { silent: true });
            state.details[traceId] = detail;
            renderDetail(traceId);
        } catch (e) {
            if (body) {
                body.innerHTML = '<div class="text-danger small p-2">' +
                    esc(t('trace.loadFailed', '链路加载失败')) + ': ' + esc(e.message) + '</div>';
            }
        }
    }

    /* ---------------------------------------------------------------- waterfall */

    function renderDetail(traceId) {
        var detail = state.details[traceId];
        var item = document.querySelector('.tr-item[data-trace="' + traceId + '"]');
        if (!detail || !item) return;

        var body = item.querySelector('.tr-body');
        if (!body) return;

        var summary = detail.summary || {};
        var spans = detail.spans || [];
        var total = Math.max(summary.durationMs || 0, 0.1);

        var meta = '<div class="tr-detail__meta">' +
            '<span>' + esc(t('trace.detail.spans', 'Span 数')) + ' ' + spans.length + '</span>' +
            '<span>' + esc(t('trace.detail.total', '总耗时')) + ' <b>' + esc(fmtMs(total)) + '</b></span>' +
            '<code class="tr-id" data-copy="' + esc(summary.traceId) + '" title="' +
                esc(t('trace.detail.copyHint', '点击复制 trace id')) + '">' + esc(summary.traceId) + '</code>' +
            '</div>';

        /* Ruler anchored to the same grid as the rows: the labels mark 0 / ¼ / ½ / ¾ / total
           of the bar column, so a bar's position can be read as a time, not a proportion. */
        var scale = '<div class="tr-wf__scale">' +
            '<span style="left:0;">0</span>' +
            '<span style="left:25%;transform:translateX(-50%);">' + esc(fmtMs(total * 0.25)) + '</span>' +
            '<span style="left:50%;transform:translateX(-50%);">' + esc(fmtMs(total * 0.5)) + '</span>' +
            '<span style="left:75%;transform:translateX(-50%);">' + esc(fmtMs(total * 0.75)) + '</span>' +
            '<span style="left:100%;transform:translateX(-100%);">' + esc(fmtMs(total)) + '</span>' +
            '</div>';

        var rows = spans.map(function (span) {
            var left = Math.min(100, (span.startOffsetMs / total) * 100);
            var width = Math.max(0.6, Math.min(100 - left, (span.durationMs / total) * 100));
            var cls = span.status === 'error' ? 'is-error'
                : span.status === 'ok' ? 'is-ok' : 'is-idle';
            var indent = Math.min(span.depth || 0, 6) * 11;

            var tagRows = Object.keys(span.tags || {}).map(function (k) {
                return '<div class="tr-tag"><k>' + esc(k) + '</k><v>' + esc(span.tags[k]) + '</v></div>';
            }).join('');

            return '<div class="tr-wf__row" data-span="' + esc(span.spanId) + '">' +
                '<div class="tr-wf__label" style="padding-left:' + indent + 'px" title="' + esc(span.name) + '">' +
                    '<i class="bi bi-chevron-right"></i>' +
                    '<span>' + esc(span.name) + '</span>' +
                    (span.source ? '<span class="tr-wf__src">' + esc(span.source) + '</span>' : '') +
                    (span.status === 'error'
                        ? '<i class="bi bi-exclamation-triangle-fill text-danger" title="' +
                            esc(span.statusMessage || '') + '"></i>'
                        : '') +
                '</div>' +
                '<div class="tr-wf__track">' +
                    '<div class="tr-wf__bar ' + cls + '" style="left:' + left.toFixed(2) +
                        '%;width:' + width.toFixed(2) + '%"></div>' +
                '</div>' +
                '<div class="tr-wf__dur">' + esc(fmtMs(span.durationMs)) + '</div>' +
                '<div class="tr-wf__tags" style="display:none;">' +
                    (tagRows || '<div class="text-muted small">' + esc(t('trace.detail.noTags', '没有属性')) + '</div>') +
                '</div>' +
            '</div>';
        }).join('');

        body.innerHTML = meta +
            '<div class="tr-wf">' +
                '<div class="tr-wf__ruler">' +
                    '<span>' + esc(t('trace.wf.span', 'Span')) + '</span>' +
                    scale +
                    '<span>' + esc(t('trace.wf.duration', '耗时')) + '</span>' +
                '</div>' +
                rows +
            '</div>' +
            '<div class="tr-legend">' +
                '<span><i style="background:#34d399"></i>' + esc(t('trace.legend.ok', '正常')) + '</span>' +
                '<span><i style="background:#f87171"></i>' + esc(t('trace.legend.error', '错误')) + '</span>' +
                '<span><i style="background:#94a3b8"></i>' + esc(t('trace.legend.unset', '未标注')) + '</span>' +
                '<span>' + esc(t('trace.wf.hint', '点一行展开该 span 的属性')) + '</span>' +
            '</div>' +
            (summary.truncated
                ? '<div class="text-muted small mt-2"><i class="bi bi-scissors"></i> ' +
                    esc(t('trace.detail.truncated', 'Span 数超过上限，其余未记录。')) + '</div>'
                : '');

        body.querySelectorAll('.tr-wf__row').forEach(function (row) {
            row.addEventListener('click', function (event) {
                event.stopPropagation();
                var tags = row.querySelector('.tr-wf__tags');
                var caret = row.querySelector('.tr-wf__label i');
                if (!tags) return;
                var open = tags.style.display === 'none';
                tags.style.display = open ? '' : 'none';
                if (caret) {
                    caret.classList.toggle('bi-chevron-right', !open);
                    caret.classList.toggle('bi-chevron-down', open);
                }
            });
        });

        body.querySelectorAll('[data-copy]').forEach(function (node) {
            node.addEventListener('click', function (event) {
                event.stopPropagation();
                var value = node.getAttribute('data-copy');
                if (navigator.clipboard) {
                    navigator.clipboard.writeText(value).then(function () {
                        toast(t('trace.detail.copied', '已复制 trace id'));
                    });
                }
            });
        });
    }

    /* -------------------------------------------------------------------- misc */

    function toast(message, isError) {
        if (window.DashboardToast && window.DashboardToast.show) {
            window.DashboardToast.show(message, isError ? 'error' : 'success');
            return;
        }
        // Fallback: the page has no toast host of its own, and silently swallowing the result of
        // a button press is worse than a browser dialog.
        if (isError) console.warn(message); else console.info(message);
    }

    async function load(silent) {
        try {
            state.data = await DashboardApi.request('/api/tracing?limit=50', { silent: silent !== false });
            renderNotice();
            renderStatus();
            await renderList();
        } catch (e) {
            var el = document.getElementById('trace-list');
            if (el) {
                el.innerHTML = '<div class="tr-empty"><div class="text-danger small">' +
                    esc(t('trace.loadFailed', '链路加载失败')) + ': ' + esc(e.message) + '</div></div>';
            }
        }
    }

    function startPolling() {
        stopPolling();
        state.timer = setInterval(function () {
            var auto = document.getElementById('trace-auto');
            if (auto && !auto.checked) return;
            if (document.hidden) return;
            load(true);
        }, POLL_MS);
    }

    function stopPolling() {
        if (state.timer) clearInterval(state.timer);
        state.timer = null;
    }

    var Tracing = {
        init: async function () {
            if (typeof DashboardApi === 'undefined') return;

            var refresh = document.getElementById('trace-refresh');
            if (refresh) {
                refresh.addEventListener('click', function () {
                    // Drop cached details so an explicit refresh really re-reads them.
                    state.details = {};
                    load(false);
                });
            }

            var clear = document.getElementById('trace-clear');
            if (clear) {
                clear.addEventListener('click', async function () {
                    if (!confirm(t('trace.clearConfirm', '清空内存中的链路？已导出的不受影响。'))) return;
                    try {
                        await DashboardApi.request('/api/tracing/clear', { method: 'POST', silent: true });
                        state.details = {};
                        state.expanded = {};
                        toast(t('trace.cleared', '已清空'));
                        await load(true);
                    } catch (e) {
                        toast(e.message, true);
                    }
                });
            }

            await load(true);
            startPolling();
        },

        stop: stopPolling
    };

    window.TracingModule = Tracing;
})();
