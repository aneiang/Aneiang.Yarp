/**
 * Plugin Workspace - the flow glue between the three plugin pages.
 *
 * The three pages (overview / configuration / resources) are one lifecycle, so
 * this module answers the one question that made them feel disjointed:
 * "where am I and what is still unfinished?"
 *
 * It renders a single status line into #plugin-todo, shared by all three pages:
 *
 *   ● N 个生效中      enabled and bound - these actually run
 *   ● N 个待绑定      enabled but bound to nothing - the classic trap
 *   ● N 个未启用      off
 *
 * The "待绑定" chip is a link into the configuration page for the first plugin
 * that needs it, so the dead end ("enabled, yet nothing happens") always has an
 * obvious next click.
 */
(function () {
    'use strict';

    var REFRESH_MS = 60000;
    var _timer = null;
    var _state = null;

    function t(key, params, fallback) {
        if (typeof window.__ === 'function') {
            var v = params ? window.__(key, params) : window.__(key);
            if (v && v !== key) return v;
        }
        return fallback || key;
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    /* Normalise the two payload shapes the API has used. */
    function pluginList(data) {
        return Array.isArray(data) ? data : (data && data.plugins) || [];
    }

    function summarise(plugins) {
        var live = 0, pending = 0, off = 0, firstPending = null;
        plugins.forEach(function (p) {
            if (!p || !p.enabled) { off++; return; }
            if ((p.bindingCount || 0) > 0) { live++; }
            else { pending++; if (!firstPending) firstPending = p.pluginId; }
        });
        return { total: plugins.length, live: live, pending: pending, off: off, firstPending: firstPending };
    }

    function render(summary) {
        var host = document.getElementById('plugin-todo');
        if (!host) return;
        if (!summary || summary.total === 0) {
            host.innerHTML = '';
            return;
        }

        var prefix = host.getAttribute('data-prefix') || '';
        var html = '';

        /* Live count is context, not a task - no link. */
        if (summary.live > 0) {
            html += '<span class="pst pst--live"><i class="bi bi-check-circle-fill"></i>' +
                esc(t('pw.live', { count: summary.live }, summary.live + ' 个生效中')) + '</span>';
        }

        /* Pending is the actionable one: link straight to the first offender. */
        if (summary.pending > 0) {
            var href = prefix + '/plugin-center#' + encodeURIComponent(summary.firstPending);
            html += '<a class="pst pst--todo" href="' + esc(href) + '" title="' +
                esc(t('pw.pendingHint', null, '已启用但未绑定，插件不会生效')) + '">' +
                '<i class="bi bi-exclamation-triangle-fill"></i>' +
                esc(t('pw.pending', { count: summary.pending }, summary.pending + ' 个待绑定')) +
                '<i class="bi bi-arrow-right"></i></a>';
        }

        if (summary.off > 0 && summary.live === 0 && summary.pending === 0) {
            html += '<span class="pst pst--off"><i class="bi bi-power"></i>' +
                esc(t('pw.allOff', null, '插件均未启用')) + '</span>';
        }

        if (summary.live > 0 && summary.pending === 0 && summary.off === 0) {
            html += '<span class="pst pst--live"><i class="bi bi-shield-check"></i>' +
                esc(t('pw.allGood', null, '全部就绪')) + '</span>';
        }

        host.innerHTML = html;
    }

    /* This script is emitted by the shared partial, which renders before the core
       bundle at the end of the layout - so wait for DashboardApi instead of assuming
       it is there. Bounded retry: give up silently rather than poll forever. */
    var _waits = 0;

    async function load() {
        var host = document.getElementById('plugin-todo');
        if (!host) return;
        if (!window.DashboardApi || typeof window.DashboardApi.getPlugins !== 'function') {
            if (++_waits > 40) return;
            setTimeout(load, 150);
            return;
        }
        _waits = 0;
        try {
            var data = await window.DashboardApi.getPlugins();
            _state = summarise(pluginList(data));
            render(_state);
        } catch (error) {
            /* A failed status line must never block the page itself. */
            host.innerHTML = '';
        }
    }

    function start() {
        load();
        if (_timer) clearInterval(_timer);
        _timer = setInterval(function () {
            if (!document.hidden) load();
        }, REFRESH_MS);
    }

    /* Pages call this after toggling a plugin, so the counter never goes stale. */
    function refresh() { return load(); }

    window.PluginWorkspace = {
        refresh: refresh,
        state: function () { return _state; },
        /* Shared helper: the monitor page that owns a plugin's output. */
        MONITOR_PAGES: {
            'circuit-breaker': 'circuits',
            'proxy-log': 'logs',
            'traffic-metrics': 'traffic',
            'cluster-metrics': 'cluster-metrics'
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
