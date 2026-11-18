/**
 * DashboardDataSource - 数据来源诊断条
 *
 * 背景：集群指标 / 流量 / 熔断 / 日志四个页面的数据都由插件采集，
 * 插件没开时页面是空的，而旧提示条只有一句「未配置插件，数据可能为空」，
 * 既不写插件 ID，也覆盖不到「已启用但未绑定」「已绑定但无采集」两种空。
 *
 * 本模块把页面空白的成因收敛成五种状态，每种状态给出一个可点的下一步：
 *   missing  插件未安装
 *   disabled 插件未启用          -> 一键启用
 *   unbound  已启用但未绑定目标  -> 去插件中心绑定
 *   waiting  已绑定但没有采集结果 -> 立即刷新
 *   ready    运行中（收起为一行摘要，不占内容区）
 *
 * 状态全部由现有接口推导，后端无需改动：
 *   GET  /api/plugins/{id}         -> enabled
 *   GET  /api/plugin-bindings      -> 绑定数量
 *   POST /api/plugins/{id}/toggle  -> 一键启用
 */
(function () {
    'use strict';

    /** 状态枚举，顺序即「从缺到有」的排查顺序 */
    var STATE = {
        MISSING: 'missing',
        DISABLED: 'disabled',
        UNBOUND: 'unbound',
        WAITING: 'waiting',
        READY: 'ready'
    };

    /** 每个插件的运行时登记：状态、绑定数、宿主容器、页面回调 */
    var registry = Object.create(null);

    /** 等待数据态的观察定时器，避免数据到了条子还停在「等待」 */
    var watchers = Object.create(null);

    var WATCH_INTERVAL_MS = 15000;
    var WATCH_MAX_TICKS = 8;

    /** 状态 -> 图标 / 语义色 / 按钮行为 */
    var VIEW = {
        missing: { icon: 'bi-x-octagon-fill', tone: 'danger', action: 'center', labelKey: 'datasource.act.install', badgeKey: 'datasource.state.missing', descKey: 'datasource.desc.missing' },
        disabled: { icon: 'bi-exclamation-triangle-fill', tone: 'warn', action: 'enable', labelKey: 'datasource.act.enable', badgeKey: 'datasource.state.disabled', descKey: 'datasource.desc.disabled' },
        unbound: { icon: 'bi-link-45deg', tone: 'info', action: 'bind', labelKey: 'datasource.act.bind', badgeKey: 'datasource.state.unbound', descKey: 'datasource.desc.unbound' },
        waiting: { icon: 'bi-hourglass-split', tone: 'muted', action: 'refresh', labelKey: 'datasource.act.refresh', badgeKey: 'datasource.state.waiting', descKey: 'datasource.desc.waiting' },
        ready: { icon: 'bi-check-circle-fill', tone: 'ok', action: 'detail', labelKey: 'datasource.act.detail', badgeKey: 'datasource.state.ready', descKey: 'datasource.desc.ready' }
    };

    // ---------------------------------------------------------------------
    // 工具
    // ---------------------------------------------------------------------

    /** 读取翻译；第二个参数是插值对象（不是 fallback） */
    function t(key, params) {
        return window.__ ? window.__(key, params) : key;
    }

    function basePath() {
        var cfg = window.__dashboard || {};
        return cfg.basePath || '';
    }

    function pluginCenterUrl(pluginId) {
        return basePath() + '/plugin-center#' + encodeURIComponent(pluginId);
    }

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function pickState(element, key) {
        return element && element.getAttribute ? element.getAttribute(key) : null;
    }

    // ---------------------------------------------------------------------
    // 状态推导
    // ---------------------------------------------------------------------

    /**
     * 查询插件与绑定信息，推导当前状态。
     * 任一接口异常都不抛出，退化为 missing / waiting，避免拖垮页面初始化。
     */
    async function resolve(entry) {
        var result = { state: STATE.MISSING, bindingCount: 0, detail: null, error: null };

        if (!window.DashboardApi || !window.DashboardApi.getPlugin) {
            result.error = 'dashboard-api-missing';
            return result;
        }

        try {
            result.detail = await window.DashboardApi.getPlugin(entry.pluginId);
        } catch (err) {
            // 插件未注册时接口返回 404，这里视为「未安装」而不是报错
            result.error = err && err.message ? err.message : String(err);
            return result;
        }

        if (!result.detail) {
            return result;
        }

        if (!result.detail.enabled) {
            result.state = STATE.DISABLED;
            return result;
        }

        // 已启用：再看有没有采集目标
        var bindings = [];
        try {
            bindings = await window.DashboardApi.getPluginBindings(entry.pluginId, entry.scope || null);
        } catch (err) {
            bindings = [];
        }
        result.bindingCount = Array.isArray(bindings) ? bindings.length : 0;

        if (result.bindingCount === 0) {
            result.state = STATE.UNBOUND;
            return result;
        }

        result.state = readHasData(entry) ? STATE.READY : STATE.WAITING;
        return result;
    }

    /** 页面通过 hasData 回调告知是否已经有数据；未提供时按「等待数据」处理更诚实 */
    function readHasData(entry) {
        if (typeof entry.opts.hasData === 'function') {
            try {
                return !!entry.opts.hasData();
            } catch (err) {
                return false;
            }
        }
        return entry.opts.hasData === true;
    }

    // ---------------------------------------------------------------------
    // 渲染
    // ---------------------------------------------------------------------

    function render(host, entry) {
        var view = VIEW[entry.state] || VIEW.missing;
        var params = { plugin: entry.pluginId, count: String(entry.bindingCount) };
        var badge = t(view.badgeKey, params);
        var desc = entry.state === STATE.READY ? '' : t(view.descKey, params);

        // ready 态下「前往插件中心」与「插件详情」指向同一处，直接不渲染，避免出现两个同义按钮
        var centerHtml = entry.state === STATE.READY
            ? ''
            : '<button type="button" class="ds-btn ds-btn--ghost" data-ds-action="center">' +
                  esc(t('datasource.act.center')) +
              '</button>';

        // ready 态收成单行摘要，避免长期占位；其余状态带一行说明
        var html = '' +
            '<div class="ds-bar ds-bar--' + view.tone + '">' +
                '<div class="ds-row">' +
                    '<i class="bi ' + view.icon + ' ds-icon"></i>' +
                    '<span class="ds-label">' + esc(t('datasource.label')) + '</span>' +
                    '<code class="ds-chip">' + esc(entry.pluginId) + '</code>' +
                    '<span class="ds-badge"><i class="ds-dot"></i>' + esc(badge) + '</span>' +
                    '<div class="ds-actions">' +
                        centerHtml +
                        '<button type="button" class="ds-btn ds-btn--primary" data-ds-action="' + view.action + '">' +
                            esc(t(view.labelKey)) +
                        '</button>' +
                    '</div>' +
                '</div>' +
                (desc ? '<p class="ds-desc">' + esc(desc) + '</p>' : '') +
            '</div>';

        host.innerHTML = html;
    }

    /**
     * 未进入「运行中」时，把面板内容区的加载动画换成空状态。
     * 否则插件未启用时面板会一直转圈，比空白更让人困惑。
     * 只在 opts.emptyContainerId 指定且状态非 ready 时生效。
     */
    function paintEmpty(entry) {
        var containerId = entry.opts && entry.opts.emptyContainerId;
        if (!containerId || entry.state === STATE.READY) {
            return;
        }
        var box = document.getElementById(containerId);
        if (!box) {
            return;
        }
        box.innerHTML = '' +
            '<div class="ds-empty">' +
                '<div class="ds-empty__icon"><i class="bi bi-inboxes"></i></div>' +
                '<p class="ds-empty__title">' + esc(t('datasource.empty.title')) + '</p>' +
                '<p class="ds-empty__desc">' + esc(t('datasource.empty.desc')) + '</p>' +
            '</div>';
    }

    // ---------------------------------------------------------------------
    // 动作
    // ---------------------------------------------------------------------

    function toast(kind, message) {
        if (!window.DashboardModals) {
            return;
        }
        var fn = window.DashboardModals['show' + kind];
        if (typeof fn === 'function') {
            fn.call(window.DashboardModals, message);
        }
    }

    async function enablePlugin(entry, button) {
        if (!window.DashboardApi || !window.DashboardApi.togglePlugin) {
            return;
        }
        var original = button ? button.textContent : '';
        if (button) {
            button.disabled = true;
            button.textContent = t('datasource.enabling');
        }
        try {
            await window.DashboardApi.togglePlugin(entry.pluginId, true);
            toast('Success', t('datasource.toast.enabled', { plugin: entry.pluginId }));
            await refresh(entry.pluginId);
            // 页面监听该事件后重新拉取数据
            document.dispatchEvent(new CustomEvent('datasource:enabled', { detail: { pluginId: entry.pluginId } }));
        } catch (err) {
            toast('Error', t('datasource.toast.failed') + ': ' + (err && err.message ? err.message : err));
            if (button) {
                button.disabled = false;
                button.textContent = original;
            }
        }
    }

    function handleAction(action, entry, button) {
        switch (action) {
            case 'enable':
                enablePlugin(entry, button);
                break;
            case 'bind':
            case 'center':
                window.location.href = pluginCenterUrl(entry.pluginId);
                break;
            case 'refresh':
                refresh(entry.pluginId);
                if (typeof entry.opts.onRefresh === 'function') {
                    entry.opts.onRefresh();
                }
                break;
            case 'detail':
                window.location.href = pluginCenterUrl(entry.pluginId);
                break;
            default:
                break;
        }
    }

    function bindEvents(host, entry) {
        if (host.getAttribute('data-ds-bound') === '1') {
            return;
        }
        host.setAttribute('data-ds-bound', '1');
        host.addEventListener('click', function (event) {
            var button = event.target.closest('[data-ds-action]');
            if (!button || !host.contains(button)) {
                return;
            }
            event.preventDefault();
            handleAction(button.getAttribute('data-ds-action'), entry, button);
        });
    }

    // ---------------------------------------------------------------------
    // 等待数据态的自动推进
    // ---------------------------------------------------------------------

    /**
     * 处于「等待数据」时每 15 秒看一次页面是否已有数据，最多看 2 分钟。
     * 采集周期通常几十秒，超过 2 分钟还没数据就该由用户排查了，不再打扰。
     */
    function stopWatch(pluginId) {
        if (watchers[pluginId]) {
            clearInterval(watchers[pluginId]);
            delete watchers[pluginId];
        }
    }

    function startWatch(entry) {
        if (watchers[entry.pluginId]) {
            return;
        }
        var ticks = 0;
        watchers[entry.pluginId] = setInterval(function () {
            ticks++;
            if (readHasData(entry)) {
                stopWatch(entry.pluginId);
                refresh(entry.pluginId);
                return;
            }
            if (ticks >= WATCH_MAX_TICKS) {
                // 到上限就停下，不再重启观察，避免无限轮询
                stopWatch(entry.pluginId);
                refresh(entry.pluginId, true);
            }
        }, WATCH_INTERVAL_MS);
    }

    // ---------------------------------------------------------------------
    // 对外 API
    // ---------------------------------------------------------------------

    /**
     * 把状态条挂到容器上。
     * @param {Object} opts
     * @param {string} opts.containerId 宿主容器 id（partial 里的占位 div）
     * @param {string} opts.pluginId    插件 id，如 cluster-metrics
     * @param {string} [opts.scope]     Route / Cluster
     * @param {Function} [opts.hasData] 返回 true 表示该页已有数据
     * @param {Function} [opts.onRefresh] 点「立即刷新」时的页面回调
     */
    async function mount(opts) {
        opts = opts || {};
        var host = opts.containerId ? document.getElementById(opts.containerId) : null;
        if (!host) {
            return null;
        }

        var pluginId = opts.pluginId || pickState(host, 'data-plugin');
        if (!pluginId) {
            return null;
        }
        var scope = opts.scope || pickState(host, 'data-scope') || null;

        var entry = registry[pluginId];
        if (!entry) {
            entry = registry[pluginId] = { pluginId: pluginId, state: STATE.MISSING, bindingCount: 0 };
        }
        entry.opts = opts;
        entry.scope = scope;
        entry.hostId = opts.containerId;

        var resolved = await resolve(entry);
        entry.state = resolved.state;
        entry.bindingCount = resolved.bindingCount;
        entry.detail = resolved.detail;

        render(host, entry);
        bindEvents(host, entry);
        paintEmpty(entry);
        if (entry.state === STATE.WAITING) {
            startWatch(entry);
        } else {
            stopWatch(pluginId);
        }
        return entry;
    }

    /**
     * 重新推导并重绘（启用插件后、手动刷新后、观察定时器触发时调用）
     * @param {string} pluginId
     * @param {boolean} [fromWatcher] 由观察定时器触发时不再重启观察，防止无限轮询
     */
    async function refresh(pluginId, fromWatcher) {
        var entry = registry[pluginId];
        if (!entry || !entry.hostId) {
            return;
        }
        var host = document.getElementById(entry.hostId);
        if (!host) {
            return;
        }
        var resolved = await resolve(entry);
        entry.state = resolved.state;
        entry.bindingCount = resolved.bindingCount;
        entry.detail = resolved.detail;
        render(host, entry);
        paintEmpty(entry);

        if (entry.state === STATE.WAITING) {
            if (!fromWatcher) {
                startWatch(entry);
            }
        } else {
            stopWatch(pluginId);
        }
    }

    /** 页面拉到数据后调用，把 waiting 切成 ready */
    function notifyData(pluginId) {
        var entry = registry[pluginId];
        if (!entry) {
            return;
        }
        if (entry.state === STATE.WAITING || entry.state === STATE.READY) {
            refresh(pluginId);
        }
    }

    window.DashboardDataSource = {
        STATE: STATE,
        mount: mount,
        refresh: refresh,
        notifyData: notifyData
    };
})();
