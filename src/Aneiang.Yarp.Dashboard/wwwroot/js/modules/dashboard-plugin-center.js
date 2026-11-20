/**
 * Plugin Center - unified plugin configuration workspace.
 *
 * All 11 built-in plugins live here, grouped by what they do rather than by how
 * they are bound, so the first thing a user reads is "this plugin is for ___":
 *
 *   traffic   流量控制  rate-limit / request-retry / compression
 *   security  安全防护  waf / response-cache
 *   stability 稳定性    circuit-breaker / service-discovery
 *   observe   可观测    proxy-log / traffic-metrics / cluster-metrics
 *
 * Every panel opens with the same header block, which answers the four questions
 * a user actually has before touching the binding table:
 *
 *   1. 作用域   - scope badge, always visible (route or cluster)
 *   2. 一句话作用 - what changes once it runs, not a restatement of its name
 *   3. 生效条件 + 当前绑定数 - why "enabled" alone does nothing
 *   4. 配置 / 监控双入口 - where to configure it, where to see its output
 */
(function() {
    'use strict';

    function t(key, fallback) {
        if (typeof window.__ === 'function') {
            var v = window.__(key);
            if (v && v !== key) return v;
        }
        return fallback || key;
    }

    function esc(s) {
        if (window.DashboardUtils && typeof window.DashboardUtils.esc === 'function') {
            return window.DashboardUtils.esc(s);
        }
        var d = document.createElement('div');
        d.textContent = s || '';
        return d.innerHTML;
    }

    function basePath() {
        var cfg = window.__dashboard || {};
        return cfg.basePath || '';
    }

    /* ── Tab groups ──
       Ordered from "changes request behaviour" to "observes it", plus a last group
       for plugins this file does not know about (external ones, and any built-in
       added later than the registry). */
    var GROUPS = [
        { id: 'traffic', labelKey: 'pc.group.traffic', label: '流量控制' },
        { id: 'security', labelKey: 'pc.group.security', label: '安全防护' },
        { id: 'stability', labelKey: 'pc.group.stability', label: '稳定性' },
        { id: 'observe', labelKey: 'pc.group.observe', label: '可观测' },
        { id: 'extend', labelKey: 'pc.group.extend', label: '扩展插件' }
    ];

    /* ── Plugin registry ──
       id        = manifest plugin id, also the URL hash (data-source bars deep-link here)
       scope     = 'Route' | 'Cluster', mirrors PluginManifest.Scopes
       group     = tab group id
       about     = one sentence: what changes once this plugin runs
       monitor   = dashboard page that visualises this plugin's output (optional)
       config    = PluginBindingManager.create() arguments */
    var REGISTRY = [
        { id: 'rate-limit', group: 'traffic', scope: 'Route', icon: 'bi-speedometer2', color: 'text-warning',
          about: '限制每条路由的请求速率，超出配额的请求直接返回 429。',
          config: { pluginId: 'rate-limit', scope: 'Route', containerId: 'pc-rate-limit-content', refreshTimeId: 'pc-rate-limit-refresh-time', moduleName: 'RateLimitModule', icon: 'bi-speedometer2', color: 'text-warning' } },

        { id: 'request-retry', group: 'traffic', scope: 'Route', icon: 'bi-arrow-repeat', color: 'text-primary',
          about: '后端偶发失败时自动重发，把瞬时抖动挡在调用方之外。',
          config: { pluginId: 'request-retry', scope: 'Route', containerId: 'pc-request-retry-content', refreshTimeId: 'pc-request-retry-refresh-time', moduleName: 'RetryModule', icon: 'bi-arrow-repeat', color: 'text-primary' } },

        { id: 'compression', group: 'traffic', scope: 'Route', icon: 'bi-file-zip', color: 'text-success',
          about: '按 MIME 类型压缩响应体，减少传输字节。',
          config: { pluginId: 'compression', scope: 'Route', containerId: 'pc-compression-content', refreshTimeId: 'pc-compression-refresh-time', moduleName: 'CompressionModule', icon: 'bi-file-zip', color: 'text-success' } },

        { id: 'waf', group: 'security', scope: 'Route', icon: 'bi-shield-lock', color: 'text-danger',
          about: '按条件（路径 / 方法 / 请求头 / IP…）决定拦截、放行、打标签或仅记录，可先用 monitor 观察命中。',
          config: { pluginId: 'waf', scope: 'Route', containerId: 'pc-waf-content', refreshTimeId: 'pc-waf-refresh-time', moduleName: 'WafModule', icon: 'bi-shield-lock', color: 'text-danger' } },

        { id: 'response-cache', group: 'security', scope: 'Route', icon: 'bi-hdd-network', color: 'text-success',
          about: '缓存后端响应，重复请求直接命中缓存，减轻后端压力。',
          config: { pluginId: 'response-cache', scope: 'Route', containerId: 'pc-cache-content', refreshTimeId: 'pc-cache-refresh-time', moduleName: 'ResponseCacheModule', icon: 'bi-hdd-network', color: 'text-success' } },

        { id: 'circuit-breaker', group: 'stability', scope: 'Cluster', icon: 'bi-lightning-charge', color: 'text-danger', monitor: 'circuits',
          about: '后端连续失败时快速切断该目标，避免故障扩散到整条链路。',
          config: { pluginId: 'circuit-breaker', scope: 'Cluster', containerId: 'pc-circuit-breaker-content', refreshTimeId: 'pc-circuit-breaker-refresh-time', moduleName: 'CircuitBreakerConfigModule', icon: 'bi-lightning-charge', color: 'text-danger' } },

        { id: 'service-discovery', group: 'stability', scope: 'Cluster', icon: 'bi-binoculars', color: 'text-primary',
          about: '让集群的后端地址从注册中心自动同步，实例上下线无需改配置。',
          config: { pluginId: 'service-discovery', scope: 'Cluster', containerId: 'pc-service-discovery-content', refreshTimeId: 'pc-service-discovery-refresh-time', moduleName: 'ServiceDiscoveryModule', icon: 'bi-binoculars', color: 'text-primary' } },

        { id: 'fault-injection', group: 'stability', scope: 'Route', icon: 'bi-bug', color: 'text-danger',
          about: '故意让某条路由变慢或失败，用来验证重试、熔断、告警与灰度回滚是不是真的会生效。',
          config: { pluginId: 'fault-injection', scope: 'Route', containerId: 'pc-fault-injection-content', refreshTimeId: 'pc-fault-injection-refresh-time', moduleName: 'FaultInjectionModule', icon: 'bi-bug', color: 'text-danger' } },

        { id: 'proxy-log', group: 'observe', scope: 'Route', icon: 'bi-journal-text', color: 'text-info', monitor: 'logs',
          about: '记录每条请求与响应的细节，用于排障和审计。',
          config: { pluginId: 'proxy-log', scope: 'Route', containerId: 'pc-proxy-log-content', refreshTimeId: 'pc-proxy-log-refresh-time', moduleName: 'ProxyLogConfigModule', icon: 'bi-journal-text', color: 'text-info' } },

        { id: 'traffic-metrics', group: 'observe', scope: 'Route', icon: 'bi-graph-up-arrow', color: 'text-info', monitor: 'traffic',
          about: '按路由统计请求数、错误率、延迟与字节，供流量监控页展示。',
          config: { pluginId: 'traffic-metrics', scope: 'Route', containerId: 'pc-traffic-metrics-content', refreshTimeId: 'pc-traffic-metrics-refresh-time', moduleName: 'TrafficMetricsConfigModule', icon: 'bi-graph-up-arrow', color: 'text-info' } },

        { id: 'cluster-metrics', group: 'observe', scope: 'Cluster', icon: 'bi-diagram-3-fill', color: 'text-info', monitor: 'cluster-metrics',
          about: '采集各集群的节点、延迟与负载分布，供集群指标页展示。',
          config: { pluginId: 'cluster-metrics', scope: 'Cluster', containerId: 'pc-cluster-metrics-content', refreshTimeId: 'pc-cluster-metrics-refresh-time', moduleName: 'ClusterMetricsConfigModule', icon: 'bi-diagram-3-fill', color: 'text-info' } }
    ];

    var configModules = {};  /* plugin id -> PluginBindingManager instance */
    var activeTab = null;

    /* ── Tab bar budget ──
       The built-in groups are a closed set (11 today), so they can be laid out flat.
       Extensions are not: every installed plugin would add a tab, and the bar has no
       upper bound. So an extension earns a tab only when it is actually in use -
       bound to at least one target, or opened by hand from the picker. The bar then
       reflects "what is running here" instead of "what happens to be installed". */
    var bindCounts = {};        /* pluginId -> binding count */
    var pinnedExtensions = {};  /* pluginId -> true, opened from the picker this session */
    var pickerEl = null;
    var pickerAnchor = null;

    /* ── What the page actually renders ──
       REGISTRY only carries the hand-written bits (icon, colour, tab group, one-line
       purpose) for the built-in plugins. Everything else - external plugins, and any
       built-in added after this file was last touched - is merged in from /api/plugins,
       otherwise those plugins would install and bind fine from the market page but
       never show a tab here. */
    var entries = REGISTRY.slice();
    var discovered = false;

    function findEntry(id) {
        for (var i = 0; i < entries.length; i++) { if (entries[i].id === id) return entries[i]; }
        return null;
    }

    /* An external plugin declares `scopes` (array). This page gives each plugin one
       tab, so pick the scope that carries the request pipeline when both are offered. */
    function pickScope(plugin) {
        var scopes = Array.isArray(plugin.scopes) ? plugin.scopes : [];
        if (!scopes.length) return 'Route';
        return scopes.indexOf('Route') >= 0 ? 'Route' : scopes[0];
    }

    function extensionEntry(plugin) {
        var id = plugin.pluginId;
        var scope = pickScope(plugin);
        var description = plugin.description || '';
        return {
            id: id,
            group: 'extend',
            scope: scope,
            icon: 'bi-puzzle',
            color: 'text-secondary',
            name: plugin.displayName || id,
            about: description,
            external: !plugin.isBuiltIn,
            config: {
                pluginId: id,
                scope: scope,
                containerId: 'pc-' + id + '-content',
                refreshTimeId: 'pc-' + id + '-refresh-time',
                moduleName: 'ExtensionModule',
                icon: 'bi-puzzle',
                color: 'text-secondary',
                descText: description
            }
        };
    }

    async function discoverPlugins() {
        if (discovered) return;
        discovered = true;
        if (!window.DashboardApi || !window.DashboardApi.getPlugins) return;
        try {
            var plugins = await window.DashboardApi.getPlugins();
            (Array.isArray(plugins) ? plugins : []).forEach(function (plugin) {
                if (!plugin || !plugin.pluginId || findEntry(plugin.pluginId)) return;
                entries.push(extensionEntry(plugin));
            });
        } catch (error) {
            /* The registry is still enough to render the built-in tabs; a failed
               discovery must not take the whole page down with it. */
            discovered = false;
            console.warn('[PluginCenter] plugin discovery failed', error);
        }
    }

    /* One request for every binding, grouped by plugin - cheaper (and more reliable)
       than asking each plugin's panel before the tab bar can be painted. */
    async function loadBindingCounts() {
        if (!window.DashboardApi || !window.DashboardApi.getBindings) return;
        try {
            var bindings = await window.DashboardApi.getBindings();
            var counts = {};
            (Array.isArray(bindings) ? bindings : []).forEach(function (b) {
                if (!b || !b.pluginId) return;
                counts[b.pluginId] = (counts[b.pluginId] || 0) + 1;
            });
            bindCounts = counts;
        } catch (error) {
            /* Without counts every extension only shows up via the picker, which is
               degraded but still usable - a failed count must not empty the page. */
            console.warn('[PluginCenter] binding counts unavailable', error);
        }
    }

    function isTabVisible(entry) {
        if (entry.group !== 'extend') return true;
        return !!pinnedExtensions[entry.id] || (bindCounts[entry.id] || 0) > 0;
    }

    /* Extensions that exist but have no tab - the picker's "not shown yet" set. */
    function hiddenExtensions() {
        return entries.filter(function (e) { return e.group === 'extend' && !isTabVisible(e); });
    }

    function extensionEntries() {
        return entries.filter(function (e) { return e.group === 'extend'; });
    }

    function scopeShort(scope) {
        return scope === 'Cluster' ? t('pc.scope.cluster', '集群') : t('pc.scope.route', '路由');
    }

    function aboutOf(entry) {
        return t('pc.about.' + entry.id, entry.about);
    }

    /* ── Tab bar ── */
    function tabButton(entry) {
        var html = '<button type="button" class="pc-tab-btn' + (activeTab === entry.id ? ' active' : '') + '"';
        html += ' data-plugin="' + entry.id + '" title="' + esc(scopeShort(entry.scope)) + ' · ' + esc(aboutOf(entry)) + '"';
        html += ' onclick="PluginCenter.activate(\'' + entry.id + '\')">';
        html += '<i class="bi ' + entry.icon + ' ' + entry.color + '"></i>';
        html += '<span>' + esc(t('plugin.name.' + entry.id, entry.name || entry.id)) + '</span>';
        html += '<span class="pc-scope pc-scope--' + entry.scope.toLowerCase() + '">' + esc(scopeShort(entry.scope)) + '</span>';
        html += '</button>';
        return html;
    }

    /* Trailing entry of the extensions group. Always rendered while extensions are
       installed - once everything is pinned the label becomes "all extensions", so
       the list stays reachable instead of vanishing together with its last item. */
    function moreButton(hiddenCount, totalCount) {
        var label = hiddenCount > 0
            ? t('pc.more', '更多扩展')
            : t('pc.more.all', '全部扩展');
        var badge = hiddenCount > 0 ? hiddenCount : totalCount;
        return '<button type="button" class="pc-tab-more" data-act="more-plugins" ' +
            'title="' + esc(t('pc.more.hint', '已安装的扩展插件，选中后固定到标签栏')) + '">' +
            '<i class="bi bi-three-dots"></i>' +
            '<span>' + esc(label) + '</span>' +
            '<span class="pc-tab-more__count">' + badge + '</span>' +
            '</button>';
    }

    function renderTabBar(container) {
        /* Every button is about to be replaced, including the picker's trigger - an
           open panel would end up anchored to a detached node. */
        if (pickerEl) closePicker();

        var html = '<div class="pc-tabs-bar">';
        GROUPS.forEach(function (group) {
            var items = entries.filter(function (e) { return e.group === group.id && isTabVisible(e); });
            var isExtend = group.id === 'extend';
            var pending = isExtend ? hiddenExtensions().length : 0;
            var extTotal = isExtend ? extensionEntries().length : 0;
            /* The extensions group stays present as long as anything is installed:
               when every extension has a tab, the picker becomes its "all" entry. */
            if (!items.length && !extTotal) return;

            /* In-use first: it keeps the busiest extension next to the divider instead
               of wherever it happened to be installed. */
            if (isExtend) {
                items.sort(function (a, b) {
                    var diff = (bindCounts[b.id] || 0) - (bindCounts[a.id] || 0);
                    if (diff) return diff;
                    return t('plugin.name.' + a.id, a.name || a.id)
                        .localeCompare(t('plugin.name.' + b.id, b.name || b.id));
                });
            }

            html += '<div class="pc-tab-group">';
            html += '<span class="pc-tab-group-label">' + esc(t(group.labelKey, group.label)) + '</span>';
            html += '<div class="pc-tab-group-items">';
            items.forEach(function (e) { html += tabButton(e); });
            if (extTotal) html += moreButton(pending, extTotal);
            html += '</div>';
            html += '</div>';
        });
        html += '</div>';
        container.innerHTML = html;
    }

    /* ── Extension picker ──
       A searchable overlay rather than more tabs: with 50 installed extensions the
       question is "where is the one I want", not "show me all 50". */
    function positionPicker(anchor) {
        if (!pickerEl || !anchor) return;
        /* Wide enough to read a name and its one-liner, never wider than the viewport. */
        var width = Math.min(368, window.innerWidth - 24);
        pickerEl.style.width = width + 'px';

        var rect = anchor.getBoundingClientRect();
        var left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12));
        var top = rect.bottom + 6;
        pickerEl.style.left = left + 'px';
        pickerEl.style.top = top + 'px';

        /* Not enough room below (short window or mobile): open upwards instead. */
        if (top + pickerEl.offsetHeight > window.innerHeight - 12) {
            var above = rect.top - pickerEl.offsetHeight - 6;
            if (above > 12) pickerEl.style.top = above + 'px';
        }
    }

    function onPickerResize() {
        positionPicker(pickerAnchor);
    }

    function togglePicker(anchor) {
        if (pickerEl) { closePicker(); return; }

        pickerEl = document.createElement('div');
        pickerEl.className = 'pc-picker';
        pickerEl.innerHTML =
            '<div class="pc-picker__head">' +
                '<i class="bi bi-search"></i>' +
                '<input type="text" class="pc-picker__search" ' +
                    'placeholder="' + esc(t('pc.more.search', '搜索插件名称或 ID')) + '" />' +
            '</div>' +
            '<div class="pc-picker__list"></div>' +
            '<div class="pc-picker__foot">' +
                esc(t('pc.more.foot', '已安装的全部扩展插件。标有「已绑定」或「已固定」的已经在标签栏上。')) +
            '</div>';
        document.body.appendChild(pickerEl);

        pickerAnchor = anchor;
        positionPicker(anchor);

        var input = pickerEl.querySelector('.pc-picker__search');
        renderPickerList();
        input.addEventListener('input', function () {
            renderPickerList(input.value);
        });
        input.focus();

        /* Deferred so the click that opened the picker cannot immediately close it. */
        setTimeout(function () {
            document.addEventListener('mousedown', onPickerOutside);
            document.addEventListener('keydown', onPickerKey);
            window.addEventListener('resize', onPickerResize);
        }, 0);
    }

    /* Lists every extension, not only the ones without a tab: the ones already on the
       bar stay visible (with the reason why) so the list never looks like plugins
       went missing after being pinned. */
    function renderPickerList(keyword) {
        if (!pickerEl) return;
        var list = pickerEl.querySelector('.pc-picker__list');
        if (!list) return;

        var kw = (keyword || '').trim().toLowerCase();
        /* Same order as the tab bar: bound first, then pinned, then by name - so the
           plugins the user is actually running are never buried at the bottom. */
        var all = extensionEntries().sort(function (a, b) {
            var diff = (bindCounts[b.id] || 0) - (bindCounts[a.id] || 0);
            if (diff) return diff;
            if (!!pinnedExtensions[a.id] !== !!pinnedExtensions[b.id]) {
                return pinnedExtensions[a.id] ? -1 : 1;
            }
            return t('plugin.name.' + a.id, a.name || a.id)
                .localeCompare(t('plugin.name.' + b.id, b.name || b.id));
        });
        var shown = all.filter(function (e) {
            if (!kw) return true;
            var name = t('plugin.name.' + e.id, e.name || e.id).toLowerCase();
            return name.indexOf(kw) >= 0 ||
                e.id.toLowerCase().indexOf(kw) >= 0 ||
                String(e.about || '').toLowerCase().indexOf(kw) >= 0;
        });

        if (!shown.length) {
            list.innerHTML = '<div class="pc-picker__empty">' +
                esc(all.length
                    ? t('pc.more.empty', '没有匹配的插件')
                    : t('pc.more.none', '还没有安装扩展插件')) +
                '</div>';
            return;
        }

        list.innerHTML = shown.map(function (e) {
            var bound = bindCounts[e.id] || 0;
            var mark = bound > 0
                ? '<span class="pc-picker__flag">' + esc(t('pc.more.bound', '已绑定')) + ' ' + bound + '</span>'
                : (pinnedExtensions[e.id] ? '<span class="pc-picker__flag">' + esc(t('pc.more.pinned', '已固定')) + '</span>' : '');
            return '<button type="button" class="pc-picker__item" data-pick="' + esc(e.id) + '">' +
                '<i class="bi ' + e.icon + ' ' + e.color + '"></i>' +
                '<span class="pc-picker__meta">' +
                    '<span class="pc-picker__name">' +
                        esc(t('plugin.name.' + e.id, e.name || e.id)) + '</span>' +
                    '<span class="pc-picker__desc">' + esc(e.about || e.id) + '</span>' +
                '</span>' +
                mark +
                '<span class="pc-scope pc-scope--' + e.scope.toLowerCase() + '">' +
                    esc(scopeShort(e.scope)) + '</span>' +
                '</button>';
        }).join('');

        Array.prototype.forEach.call(list.querySelectorAll('[data-pick]'), function (btn) {
            btn.addEventListener('click', function () {
                pickExtension(btn.getAttribute('data-pick'));
            });
        });
    }

    async function pickExtension(id) {
        pinnedExtensions[id] = true;
        closePicker();
        var container = document.getElementById('pc-tab-bar');
        if (container) renderTabBar(container);
        await activate(id);
    }

    function onPickerOutside(event) {
        if (!pickerEl || pickerEl.contains(event.target)) return;
        /* Clicking the trigger is handled by its own click listener - closing here
           would just reopen on the click that follows. */
        if (event.target.closest && event.target.closest('[data-act="more-plugins"]')) return;
        closePicker();
    }

    function onPickerKey(event) {
        if (event.key === 'Escape') closePicker();
    }

    function closePicker() {
        if (!pickerEl) return;
        document.removeEventListener('mousedown', onPickerOutside);
        document.removeEventListener('keydown', onPickerKey);
        window.removeEventListener('resize', onPickerResize);
        pickerEl.remove();
        pickerEl = null;
        pickerAnchor = null;
    }

    /* ── Panel header ──
       Scope badge, one-line purpose, activation condition + live binding count,
       and the two entry points. Rendered twice: skeleton on first paint, real
       values once plugin + bindings arrive. */
    function headerShell(entry) {
        return '<section class="pc-head" id="pc-head-' + entry.id + '">' +
            '<span class="pc-head__icon ' + entry.color + '"><i class="bi ' + entry.icon + '"></i></span>' +
            '<div class="pc-head__body">' +
                '<div class="pc-head__title">' +
                    '<span class="pc-head__name">' + esc(t('plugin.name.' + entry.id, entry.name || entry.id)) + '</span>' +
                    '<span class="pc-scope pc-scope--' + entry.scope.toLowerCase() + '">● ' + esc(scopeShort(entry.scope)) + '</span>' +
                    '<code class="pc-head__id">' + esc(entry.id) + '</code>' +
                '</div>' +
                '<p class="pc-head__about">' + esc(aboutOf(entry)) + '</p>' +
                '<div class="pc-head__meta">' +
                    '<span class="pc-head__cond"><i class="bi bi-funnel"></i>' +
                        esc(t('pc.cond.label', '生效条件')) + '：' +
                        esc(entry.scope === 'Cluster' ? t('pc.cond.cluster', '需启用 + 绑定到至少 1 个集群')
                                                      : t('pc.cond.route', '需启用 + 绑定到至少 1 个路由')) +
                    '</span>' +
                    '<span class="pc-head__state is-wait">' +
                        '<span class="loading-spinner"></span>' + esc(t('common.loading', '加载中...')) +
                    '</span>' +
                '</div>' +
            '</div>' +
            '<div class="pc-head__act"></div>' +
        '</section>';
    }

    function renderHeader(entry, state) {
        var host = document.getElementById('pc-head-' + entry.id);
        if (!host) return;

        var plugin = (state && state.plugin) || {};
        var bindings = (state && state.bindings) || [];
        var enabled = !!plugin.enabled;
        var total = bindings.length;
        var live = bindings.filter(function (b) { return b.enabled; }).length;

        /* Promote an extension the moment it gets its first binding, so the tab bar
           does not disagree with the header right above it. Demotion is deliberately
           left to the next page load: removing the last binding should not make the
           tab you are working in disappear underneath you. */
        if (entry.group === 'extend') {
            var had = bindCounts[entry.id] || 0;
            bindCounts[entry.id] = total;
            if (had === 0 && total > 0) {
                var bar = document.getElementById('pc-tab-bar');
                if (bar && !bar.querySelector('.pc-tab-btn[data-plugin="' + entry.id + '"]')) {
                    renderTabBar(bar);
                }
            }
        }

        /* ③ activation state - the line that makes "enabled but unbound" obvious */
        var stateEl = host.querySelector('.pc-head__state');
        var stateText, stateClass;
        if (!enabled) {
            stateText = t('pc.state.off', '当前：未启用');
            stateClass = 'is-off';
        } else if (total === 0) {
            stateText = t('pc.state.unbound', '当前：未绑定任何目标');
            stateClass = 'is-warn';
        } else {
            stateText = t('pc.state.bound', '当前：已绑定') + ' ' + total + ' ' + t('pc.state.unit', '个').trim();
            stateText = stateText.replace(/\s+/g, ' ').trim();
            if (live === 0) {
                stateText += ' · ' + t('pc.state.allOff', '全部已停用');
                stateClass = 'is-warn';
            } else {
                if (live < total) {
                    stateText += ' · ' + live + ' ' + t('pc.state.liveUnit', '个生效中');
                }
                stateClass = 'is-live';
            }
        }
        if (stateEl) {
            stateEl.className = 'pc-head__state ' + stateClass;
            stateEl.innerHTML = '<i class="bi ' + (stateClass === 'is-live' ? 'bi-check-circle-fill' : 'bi-exclamation-circle-fill') + '"></i>' + esc(stateText);
        }

        /* ④ dual entry points: configure here, observe there */
        var act = host.querySelector('.pc-head__act');
        if (!act) return;
        var html = '';
        if (!enabled) {
            html += '<button type="button" class="btn btn-sm btn-primary" data-act="enable">' +
                '<i class="bi bi-power me-1"></i>' + esc(t('pc.act.enable', '启用插件')) + '</button>';
        } else if (total === 0) {
            html += '<button type="button" class="btn btn-sm btn-primary" data-act="bind">' +
                '<i class="bi bi-plus-lg me-1"></i>' + esc(t('pc.act.bind', '添加绑定')) + '</button>';
        } else if (live === 0) {
            html += '<button type="button" class="btn btn-sm btn-outline-warning" data-act="focus">' +
                '<i class="bi bi-toggle-off me-1"></i>' + esc(t('pc.act.focus', '检查绑定状态')) + '</button>';
        }
        if (entry.monitor) {
            var monHref = basePath() + '/' + entry.monitor;
            if (live > 0) {
                html += '<a class="btn btn-sm btn-outline-primary" href="' + esc(monHref) + '">' +
                    '<i class="bi bi-graph-up-arrow me-1"></i>' + esc(t('pc.act.monitor', '查看监控')) + '</a>';
            } else {
                /* Nothing is collecting yet, so the page would be empty - say why
                   instead of letting the user land on a blank chart. */
                html += '<span class="pc-head__locked" title="' + esc(t('pc.monitor.locked', '插件生效后才会有数据')) + '">' +
                    '<i class="bi bi-graph-up-arrow"></i>' + esc(t('pc.act.monitor', '查看监控')) + '</span>';
            }
        }
        /* Every plugin has a runtime cost row on the resources page, so this hop is
           always available - unlike the monitor link, which only exists for the
           plugins whose output is actually rendered somewhere. */
        html += '<a class="pc-head__aux" href="' + esc(basePath() + '/plugin-resources#' + entry.id) + '" ' +
            'title="' + esc(t('pc.act.resources', '查看该插件的运行开销')) + '" ' +
            'aria-label="' + esc(t('pc.act.resources', '查看该插件的运行开销')) + '">' +
            '<i class="bi bi-cpu"></i></a>';

        act.innerHTML = html;

        if (!act.__bound) {
            act.__bound = true;
            act.addEventListener('click', function (event) {
                var trigger = event.target && event.target.closest ? event.target.closest('[data-act]') : null;
                if (!trigger || !act.contains(trigger)) return;
                var action = trigger.getAttribute('data-act');
                if (action === 'enable') enablePlugin(entry, trigger);
                else if (action === 'bind') openAddModal(entry);
                else if (action === 'focus') focusBindings(entry);
            });
        }
    }

    /* Scroll to the bindings table so the user can switch one back on. */
    function focusBindings(entry) {
        var box = document.getElementById(entry.config.containerId);
        if (box && box.scrollIntoView) {
            box.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    }

    async function enablePlugin(entry, button) {
        if (!window.DashboardApi || !window.DashboardApi.togglePlugin) return;
        var original = button.innerHTML;
        button.disabled = true;
        button.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>' + esc(t('common.loading', '加载中...'));
        try {
            await window.DashboardApi.togglePlugin(entry.id, true);
            if (window.DashboardModals) {
                window.DashboardModals.showSuccess(t('pc.enabledToast', '插件已启用，接下来请绑定目标'));
            }
            await reload(entry.id);
        } catch (error) {
            button.disabled = false;
            button.innerHTML = original;
            if (window.DashboardModals) {
                window.DashboardModals.showError(t('common.operationFailed', '操作失败') + ': ' + (error.message || error));
            }
        }
    }

    function openAddModal(entry) {
        var module = configModules[entry.id];
        if (module && module.openAddModal) module.openAddModal();
    }

    /* ── Panel scaffolding ── */
    /* Panels are materialised from REGISTRY rather than authored in the view. The view
       used to carry one div per plugin; when `fault-injection` was added to this registry
       the div was not, and the only symptom was a tab that opened onto a blank page -
       a silent failure with no error anywhere. Now the registry is the single source. */
    function ensurePanel(entry) {
        var panel = document.querySelector('.pc-tab-panel[data-plugin="' + entry.id + '"]');
        if (panel) return panel;

        var host = document.getElementById('pc-panels');
        if (!host) return null;

        panel = document.createElement('div');
        panel.className = 'pc-tab-panel';
        panel.setAttribute('data-plugin', entry.id);
        panel.style.display = 'none';
        host.appendChild(panel);
        return panel;
    }

    function renderConfigPanel(entry) {
        var panel = ensurePanel(entry);
        if (!panel) return false;
        panel.innerHTML = headerShell(entry) +
            '<div id="' + entry.config.containerId + '">' +
                '<div class="loading-state"><div class="loading-spinner"></div>' +
                '<div class="loading-text">' + esc(t('common.loading', '加载中...')) + '</div></div>' +
            '</div>';
        return true;
    }

    /* ── Activate a tab ── */
    async function activate(id) {
        var entry = findEntry(id);
        if (!entry) return;

        /* The panel has to exist before visibility is toggled below, otherwise the tab
           would be selected while every panel - including this one - stays hidden. */
        ensurePanel(entry);

        document.querySelectorAll('.pc-tab-btn').forEach(function (btn) {
            btn.classList.toggle('active', btn.getAttribute('data-plugin') === id);
        });
        document.querySelectorAll('.pc-tab-panel').forEach(function (p) {
            p.style.display = p.getAttribute('data-plugin') === id ? '' : 'none';
        });

        activeTab = id;
        if (window.location.hash !== '#' + id) {
            history.replaceState(null, '', '#' + id);
        }

        await initConfigTab(entry);
    }

    /* ── Initialize (lazy create + load) ── */
    async function initConfigTab(entry) {
        if (!configModules[entry.id]) {
            if (!renderConfigPanel(entry)) {
                /* Nowhere to render: say so instead of leaving an empty tab. */
                var tab = document.querySelector('.pc-tab-btn[data-plugin="' + entry.id + '"]');
                if (tab && tab.parentElement) {
                    tab.parentElement.insertAdjacentHTML('beforeend',
                        '<span class="pc-tab-error" title="' +
                        esc(t('pc.panelMissing', '面板容器缺失，无法渲染该插件的配置')) + '">!</span>');
                }
                return;
            }
            if (window.PluginBindingManager) {
                var cfg = Object.assign({}, entry.config);
                /* The header already states the purpose and the activation rule;
                   the table below only needs the data and its own actions. */
                cfg.onLoaded = function (state) { renderHeader(entry, state); };
                configModules[entry.id] = window.PluginBindingManager.create(cfg);
            }
        }
        if (configModules[entry.id]) {
            await configModules[entry.id].load();
        }
    }

    async function reload(id) {
        var entry = findEntry(id);
        if (!entry) return;
        if (configModules[id]) {
            await configModules[id].load();
        } else {
            await initConfigTab(entry);
        }
    }

    async function init() {
        var tabBarContainer = document.getElementById('pc-tab-bar');
        if (!tabBarContainer) return;

        /* Discovery has to finish before the tab bar is painted: the initial tab is
           the first entry, and a deep link to #<external-id> only resolves once that
           plugin is in `entries`. Binding counts come next, because they decide which
           extensions get a tab. */
        await discoverPlugins();
        await loadBindingCounts();

        var hash = decodeURIComponent(window.location.hash.replace('#', ''));
        var target = findEntry(hash);

        /* A deep link to an unbound extension would otherwise land on a plugin that
           has no tab - pin it so the bar always reflects the panel on screen. */
        if (target && target.group === 'extend' && !isTabVisible(target)) {
            pinnedExtensions[target.id] = true;
        }

        renderTabBar(tabBarContainer);

        /* Delegated once: renderTabBar replaces the children, not the container. */
        tabBarContainer.addEventListener('click', function (event) {
            var trigger = event.target.closest ? event.target.closest('[data-act="more-plugins"]') : null;
            if (trigger) togglePicker(trigger);
        });

        var initial = target ? hash : entries[0].id;
        await activate(initial);

        window.addEventListener('hashchange', function () {
            var next = decodeURIComponent(window.location.hash.replace('#', ''));
            if (findEntry(next) && next !== activeTab) activate(next);
        });
    }

    window.PluginCenter = {
        init: init,
        activate: activate,
        reload: reload,
        REGISTRY: REGISTRY,
        GROUPS: GROUPS
    };
})();
