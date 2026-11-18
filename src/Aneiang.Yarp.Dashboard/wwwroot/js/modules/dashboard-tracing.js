/**
 * Distributed tracing page.
 *
 * Renders two things from one endpoint: the tracing *configuration* (is it on, where does it
 * export, how much is sampled) and the *traces themselves*. Both belong on the same screen because
 * the only useful question in front of an empty list is "why is it empty" - and the answer is
 * always one of those two halves.
 *
 * The list deliberately shows sampled traces only. It is the same processor the exporter uses, so
 * "it is not here" really does mean "it was not traced", not "the UI lost it".
 */
(function () {
    'use strict';

    var POLL_MS = 5000;
    var state = {
        data: null,
        details: {},
        expanded: {},
        timer: null
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
        if (ms == null || isNaN(ms)) return '-';
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
            badgeClass = 'bg-secondary';
        } else if (!s.running) {
            badge = t('trace.status.failed', '启动失败');
            badgeClass = 'bg-danger';
        } else {
            badge = t('trace.status.running', '运行中');
            badgeClass = 'bg-success';
        }

        var meta = [
            [t('trace.status.service', '服务名'), s.serviceName || '-'],
            [t('trace.status.endpoint', '导出地址'),
                s.exportEnabled ? (s.protocol + ' → ' + (s.endpoint || '-')) : t('trace.status.noExport', '仅内存')],
            [t('trace.status.buffer', '内存缓冲'), (s.bufferCount || 0) + ' / ' + (s.bufferCapacity || 0)],
            [t('trace.status.spans', '已捕获 Span'), String(s.spansCaptured || 0) +
                (s.spansDropped ? ' (' + t('trace.status.dropped', '丢弃') + ' ' + s.spansDropped + ')' : '')],
            [t('trace.status.decisions', '采样判定 / 丢弃判定'), (s.sampledTraces || 0) + ' / ' + (s.droppedTraces || 0),
                t('trace.status.decisions.hint',
                    '判定次数包含控制面与健康检查请求：它们在采样之后就被过滤掉，不入缓冲也不导出，' +
                    '所以这个数会大于实际捕获的链路数。')]
        ];

        var metaHtml = meta.map(function (row) {
            return '<div class="trace-meta-item"' + (row.length > 2 ? ' title="' + esc(row[2]) + '"' : '') + '>' +
                '<div class="text-muted small">' + esc(row[0]) + '</div>' +
                '<div class="trace-meta-value">' + esc(row[1]) + '</div></div>';
        }).join('');

        var percent = Math.round((s.samplingRatio || 0) * 100);

        el.innerHTML =
            '<div class="d-flex justify-content-between align-items-center flex-wrap gap-2 mb-3">' +
                '<div class="d-flex align-items-center gap-2">' +
                    '<span class="badge ' + badgeClass + '">' + esc(badge) + '</span>' +
                    '<span class="text-muted small">' + esc(s.sectionName || '') + '</span>' +
                '</div>' +
                '<div class="d-flex align-items-center gap-2 trace-sampling">' +
                    '<label class="small text-muted mb-0" for="trace-sampling">' +
                        esc(t('trace.sampling.label', '采样率')) + ' <strong id="trace-sampling-value">' + percent + '%</strong>' +
                    '</label>' +
                    '<input type="range" class="form-range" id="trace-sampling" min="0" max="100" step="1" value="' + percent + '">' +
                    '<button class="btn btn-sm btn-outline-primary" id="trace-sampling-apply">' +
                        esc(t('trace.sampling.apply', '应用')) + '</button>' +
                '</div>' +
            '</div>' +
            '<div class="trace-meta">' + metaHtml + '</div>' +
            '<div class="text-muted small mt-2">' + esc(t('trace.sampling.hint',
                '采样率只影响新请求，无需重启。低采样率下这里和 Jaeger 里都只会看到一部分链路。')) + '</div>';

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

    /* ------------------------------------------------------------------ notices */

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
        return '<div class="alert alert-' + kind + ' py-2 px-3 mb-3 trace-notice">' +
            '<div class="fw-semibold mb-1"><i class="bi ' + icon + '"></i> ' + esc(title) + '</div>' +
            '<div class="small">' + body + '</div></div>';
    }

    /* -------------------------------------------------------------------- list */

    async function renderList() {
        var el = document.getElementById('trace-list');
        var count = document.getElementById('trace-count');
        if (!el || !state.data) return;

        var traces = state.data.traces || [];
        if (count) count.textContent = traces.length ? traces.length + ' / ' + (state.data.status.bufferCapacity || 0) : '';

        if (!traces.length) {
            var s = state.data.status || {};
            var hint = !s.enabled
                ? t('trace.list.emptyDisabled', '追踪未启用，所以没有链路。')
                : (s.samplingRatio < 1
                    ? t('trace.list.emptySampled', '当前采样率较低，请求还没被抽中。把采样率调到 100% 再试。')
                    : t('trace.list.emptyHelp', '发一个请求后这里就会出现链路。'));
            el.innerHTML = '<div class="text-center text-muted py-4">' +
                '<i class="bi bi-bezier2" style="font-size:1.6rem;"></i>' +
                '<div class="mt-2">' + esc(t('trace.list.empty', '还没有链路')) + '</div>' +
                '<div class="small mt-1">' + esc(hint) + '</div></div>';
            return;
        }

        el.innerHTML = traces.map(rowHtml).join('');
        el.querySelectorAll('.trace-row').forEach(function (row) {
            row.addEventListener('click', function () {
                toggleTrace(row.getAttribute('data-trace'));
            });
        });

        // Re-open whatever was open before the refresh, so a 5s poll does not collapse the
        // waterfall the operator is in the middle of reading.
        Object.keys(state.expanded).forEach(function (id) {
            if (!state.expanded[id]) return;
            var item = el.querySelector('.trace-item[data-trace="' + id + '"]');
            if (item) {
                item.classList.add('is-open');
                var body = item.querySelector('.trace-body');
                if (body) body.style.display = '';
                if (state.details[id]) renderDetail(id);
            }
        });
    }

    function rowHtml(trace) {
        var cls = statusClass(trace.statusCode, trace.status);
        var codeBadge = trace.statusCode
            ? '<span class="badge trace-code ' + cls + '">' + trace.statusCode + '</span>'
            : '<span class="badge trace-code ' + cls + '">' + esc(trace.status) + '</span>';

        var route = trace.routeId
            ? '<span class="trace-chip">' + esc(trace.routeId) + '</span>'
            : '';
        var cluster = trace.clusterId
            ? '<span class="trace-chip">' + esc(trace.clusterId) + '</span>'
            : '';
        var dest = trace.destinationId
            ? '<span class="trace-chip trace-chip-dest">' + esc(trace.destinationId) + '</span>'
            : '<span class="trace-chip trace-chip-dest">' +
                esc(t('trace.noDestination', '未到目标')) + '</span>';

        var flags = '';
        if (trace.shortCircuited) {
            flags += '<span class="badge bg-warning text-dark">' +
                esc(t('trace.badge.shortCircuit', '插件拦截')) + '</span>';
        }
        if (trace.truncated) {
            flags += '<span class="badge bg-secondary">' +
                esc(t('trace.badge.truncated', '已截断')) + '</span>';
        }

        return '<div class="trace-item" data-trace="' + esc(trace.traceId) + '">' +
            '<div class="trace-row" role="button" tabindex="0">' +
                '<i class="bi bi-chevron-right trace-caret"></i>' +
                codeBadge +
                '<span class="trace-name">' + esc(trace.rootName || trace.traceId) + '</span>' +
                route + cluster + dest + flags +
                '<span class="trace-duration ms-auto">' + esc(fmtMs(trace.durationMs)) + '</span>' +
                '<span class="trace-spans text-muted small">' + (trace.spanCount || 0) + ' ' +
                    esc(t('trace.spans', 'span')) + '</span>' +
                '<span class="trace-time text-muted small">' +
                    esc(new Date(trace.startedAt).toLocaleTimeString()) + '</span>' +
            '</div>' +
            '<div class="trace-body" style="display:none;"></div>' +
        '</div>';
    }

    async function toggleTrace(traceId) {
        var item = document.querySelector('.trace-item[data-trace="' + traceId + '"]');
        if (!item) return;

        var opening = !state.expanded[traceId];
        state.expanded[traceId] = opening;
        item.classList.toggle('is-open', opening);

        var body = item.querySelector('.trace-body');
        if (body) body.style.display = opening ? '' : 'none';
        if (!opening) return;

        if (state.details[traceId]) {
            renderDetail(traceId);
            return;
        }

        if (body) body.innerHTML = '<div class="text-muted small p-2">' + esc(t('trace.loading', '加载中...')) + '</div>';
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

    function renderDetail(traceId) {
        var detail = state.details[traceId];
        var item = document.querySelector('.trace-item[data-trace="' + traceId + '"]');
        if (!detail || !item) return;

        var body = item.querySelector('.trace-body');
        if (!body) return;

        var summary = detail.summary || {};
        var spans = detail.spans || [];
        var total = Math.max(summary.durationMs || 0, 0.1);

        var head = '<div class="trace-detail-head small text-muted">' +
            '<span class="text-muted">trace</span> <code class="trace-id" data-copy="' +
                esc(summary.traceId) + '">' + esc(summary.traceId) + '</code>' +
            '<span class="ms-3">' + esc(t('trace.detail.spans', 'Span 数')) + ': ' + spans.length + '</span>' +
            '</div>';

        var rows = spans.map(function (span) {
            var left = Math.min(100, (span.startOffsetMs / total) * 100);
            var width = Math.max(0.6, Math.min(100 - left, (span.durationMs / total) * 100));
            var cls = span.status === 'error' ? 'is-error'
                : span.status === 'ok' ? 'is-ok' : 'is-idle';
            var indent = Math.min(span.depth || 0, 6) * 14;
            var tagTitle = Object.keys(span.tags || {}).map(function (k) {
                return k + '=' + span.tags[k];
            }).join('\n');

            var tags = Object.keys(span.tags || {}).map(function (k) {
                return '<div class="trace-tag"><span class="trace-tag-key">' + esc(k) + '</span>' +
                    '<span class="trace-tag-val">' + esc(span.tags[k]) + '</span></div>';
            }).join('');

            return '<div class="trace-span" data-span="' + esc(span.spanId) + '">' +
                '<div class="trace-span-main">' +
                    '<div class="trace-span-name" style="padding-left:' + indent + 'px" title="' + esc(tagTitle) + '">' +
                        '<i class="bi bi-chevron-right trace-span-caret"></i>' +
                        '<span class="trace-span-title">' + esc(span.name) + '</span>' +
                        '<span class="trace-span-source">' + esc(span.source) + '</span>' +
                        (span.status === 'error'
                            ? '<i class="bi bi-exclamation-triangle-fill text-danger" title="' +
                                esc(span.statusMessage || '') + '"></i>'
                            : '') +
                    '</div>' +
                    '<div class="trace-span-bar">' +
                        '<div class="trace-bar ' + cls + '" style="left:' + left.toFixed(2) +
                            '%;width:' + width.toFixed(2) + '%"></div>' +
                    '</div>' +
                    '<div class="trace-span-duration">' + esc(fmtMs(span.durationMs)) + '</div>' +
                '</div>' +
                '<div class="trace-span-tags" style="display:none;">' +
                    (tags || '<div class="text-muted small">' + esc(t('trace.detail.noTags', '没有属性')) + '</div>') +
                '</div>' +
            '</div>';
        }).join('');

        body.innerHTML = head + '<div class="trace-waterfall">' + rows + '</div>' +
            (summary.truncated
                ? '<div class="text-muted small mt-2"><i class="bi bi-scissors"></i> ' +
                    esc(t('trace.detail.truncated', 'Span 数超过上限，其余未记录。')) + '</div>'
                : '');

        body.querySelectorAll('.trace-span-main').forEach(function (row) {
            row.addEventListener('click', function (event) {
                event.stopPropagation();
                var span = row.parentElement;
                if (!span) return;
                var tags = span.querySelector('.trace-span-tags');
                var caret = row.querySelector('.trace-span-caret');
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
            node.style.cursor = 'pointer';
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
                el.innerHTML = '<div class="text-danger small py-3">' +
                    esc(t('trace.loadFailed', '链路加载失败')) + ': ' + esc(e.message) + '</div>';
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
