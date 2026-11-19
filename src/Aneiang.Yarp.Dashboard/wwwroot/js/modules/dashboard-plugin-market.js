/**
 * Plugin market - what is installed, where it came from, how to add one.
 *
 * There is no remote registry behind this page, and pretending otherwise would be
 * the one thing a self-hosted gateway must not do: the dashboard cannot install
 * code it has no way to trust. So "market" here means exactly three real things:
 *
 *   1. everything the host currently has, with provenance (built-in / external)
 *   2. install / upgrade / uninstall of an external plugin from a local directory
 *   3. the contract an external plugin has to meet, stated where it is needed
 *
 * The grid is cards rather than the table used on the other plugin pages because
 * the question here is "what does this thing do", and a two-line sentence does not
 * survive a table column. Binding and resource details stay where they belong.
 */
(function () {
    'use strict';

    var state = {
        plugins: [],
        source: 'all',
        enabledOnly: false,
        query: ''
    };

    /* Presentation only - the tab icons in PluginCenter.REGISTRY are a different
       surface with its own colour coding, so this is not the same list duplicated
       for state, just for glyphs. Unknown ids fall back to a puzzle piece. */
    var ICONS = {
        'rate-limit': 'bi-speedometer2',
        'request-retry': 'bi-arrow-repeat',
        'compression': 'bi-file-zip',
        'waf': 'bi-shield-lock',
        'response-cache': 'bi-hdd-network',
        'circuit-breaker': 'bi-lightning-charge',
        'service-discovery': 'bi-binoculars',
        'fault-injection': 'bi-bug',
        'proxy-log': 'bi-journal-text',
        'traffic-metrics': 'bi-graph-up-arrow',
        'cluster-metrics': 'bi-diagram-3-fill',
        'ai': 'bi-stars'
    };

    function t(key, fallback) {
        if (typeof window.__ === 'function') {
            var v = window.__(key);
            if (v && v !== key) return v;
        }
        return fallback || key;
    }

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function basePath() {
        var cfg = window.__dashboard || {};
        return cfg.basePath || '';
    }

    /* PluginScope arrives as 0/1 (no global enum converter) and may arrive as a
       string if that ever changes - accept both instead of guessing. */
    function isClusterScope(scope) {
        return scope === 1 || scope === 'Cluster';
    }

    function scopeLabel(scope) {
        return isClusterScope(scope) ? t('pc.scope.cluster', '集群') : t('pc.scope.route', '路由');
    }

    function iconFor(id) {
        return ICONS[id] || 'bi-puzzle';
    }

    /* ------------------------------------------------------------------ data */

    function listOf(data) {
        if (Array.isArray(data)) return data;
        if (data && Array.isArray(data.plugins)) return data.plugins;
        if (data && Array.isArray(data.items)) return data.items;
        return [];
    }

    async function load() {
        var grid = document.getElementById('mk-grid');
        try {
            state.plugins = listOf(await window.DashboardApi.getPlugins());
            renderStats();
            renderGrid();
        } catch (error) {
            if (grid) {
                grid.innerHTML = '<div class="alert alert-danger" style="grid-column:1/-1;">' +
                    esc(t('common.loadFailed', '加载失败')) + ': ' + esc(error.message || error) + '</div>';
            }
        }
    }

    /* --------------------------------------------------------------- overview */

    function renderStats() {
        var host = document.getElementById('mk-stats');
        if (!host) return;

        var builtin = state.plugins.filter(function (p) { return p.isBuiltIn; }).length;
        var external = state.plugins.length - builtin;
        var enabled = state.plugins.filter(function (p) { return p.enabled; }).length;
        var pending = state.plugins.filter(function (p) {
            return p.enabled && !(p.bindingCount > 0);
        }).length;

        function stat(value, labelKey, label, cls) {
            return '<div class="mk-stat ' + (cls || '') + '">' +
                '<span class="mk-stat__num">' + value + '</span>' +
                '<span class="mk-stat__label" data-i18n="' + labelKey + '">' + esc(label) + '</span></div>';
        }

        host.innerHTML =
            stat(builtin, 'pm.stat.builtin', t('pm.stat.builtin', '内置')) +
            stat(external, 'pm.stat.external', t('pm.stat.external', '外部')) +
            stat(enabled, 'pm.stat.enabled', t('pm.stat.enabled', '已启用'), 'mk-stat--live') +
            (pending > 0
                ? stat(pending, 'pm.stat.pending', t('pm.stat.pending', '待绑定'), 'mk-stat--warn')
                : '');

        /* Filter counts ride on the segmented control so "how many external plugins
           do I have" is answered without switching the filter first. */
        var counts = { all: state.plugins.length, builtin: builtin, external: external };
        document.querySelectorAll('#mk-source [data-n]').forEach(function (el) {
            var n = counts[el.getAttribute('data-n')];
            el.textContent = n == null ? '' : n;
        });
    }

    /* ------------------------------------------------------------------ grid */

    function matches(plugin) {
        if (state.source === 'builtin' && !plugin.isBuiltIn) return false;
        if (state.source === 'external' && plugin.isBuiltIn) return false;
        if (state.enabledOnly && !plugin.enabled) return false;
        if (!state.query) return true;

        var q = state.query.toLowerCase();
        var hay = [
            plugin.pluginId, plugin.displayName, plugin.description
        ].join(' ').toLowerCase();
        return hay.indexOf(q) >= 0;
    }

    function cardHtml(plugin) {
        var scopes = Array.isArray(plugin.scopes) ? plugin.scopes : [];
        var chips = scopes.map(function (s) {
            return '<span class="mk-chip' + (isClusterScope(s) ? ' mk-chip--cluster' : '') + '">' +
                esc(scopeLabel(s)) + '</span>';
        }).join('');

        var binds = plugin.bindingCount > 0
            ? '<span class="mk-bind' + (plugin.enabled ? ' mk-bind--live' : '') + '">' +
                esc(t('pm.bound', '绑定') + ' ' + plugin.bindingCount) + '</span>'
            : '<span class="mk-bind">' + esc(t('pm.unbound', '未绑定')) + '</span>';

        var warnings = '';
        if (Array.isArray(plugin.missingDependencies) && plugin.missingDependencies.length) {
            warnings += warn(t('pm.warn.dependency', '缺少依赖') + ': ' + plugin.missingDependencies.join(', '));
        }
        if (plugin.registrationStatus && plugin.registrationStatus !== 'Registered' && plugin.registrationError) {
            warnings += warn(plugin.registrationError);
        }

        /* Config lives in Plugin Configuration, one click away, addressed by hash -
           the market never becomes a second editing surface. */
        var actions = '<a href="' + esc(basePath() + '/plugin-center#' + encodeURIComponent(plugin.pluginId)) + '" ' +
            'title="' + esc(t('pm.action.configure', '配置')) + '">' +
            '<i class="bi bi-sliders"></i></a>';

        if (!plugin.isBuiltIn) {
            actions += '<button type="button" data-act="upgrade" data-id="' + esc(plugin.pluginId) + '" ' +
                'data-requires-admin title="' + esc(t('pm.action.upgrade', '升级')) + '">' +
                '<i class="bi bi-arrow-up-circle"></i></button>' +
                '<button type="button" class="mk-danger" data-act="uninstall" data-id="' + esc(plugin.pluginId) + '" ' +
                'data-requires-admin title="' + esc(t('pm.action.uninstall', '卸载')) + '">' +
                '<i class="bi bi-trash3"></i></button>';
        }

        return '<article class="mk-card' + (plugin.enabled ? '' : ' is-off') + '" data-plugin="' + esc(plugin.pluginId) + '">' +
            '<header class="mk-card__head">' +
                '<span class="mk-card__icon"><i class="bi ' + iconFor(plugin.pluginId) + '"></i></span>' +
                '<div class="mk-card__title">' +
                    '<div class="mk-card__name"><span>' + esc(plugin.displayName || plugin.pluginId) + '</span></div>' +
                    '<div class="mk-card__sub">' +
                        '<code>' + esc(plugin.pluginId) + '</code>' +
                        '<span>v' + esc(plugin.version || '—') + '</span>' +
                        '<span class="mk-src mk-src--' + (plugin.isBuiltIn ? 'builtin' : 'external') + '">' +
                            esc(plugin.isBuiltIn ? t('pm.badge.builtin', '内置') : t('pm.badge.external', '外部')) +
                        '</span>' +
                    '</div>' +
                '</div>' +
                '<label class="mk-psw" data-requires-write title="' +
                    esc(plugin.enabled ? t('pm.action.disable', '停用') : t('pm.action.enable', '启用')) + '">' +
                    '<input type="checkbox" data-act="toggle" data-id="' + esc(plugin.pluginId) + '"' +
                        (plugin.enabled ? ' checked' : '') + '>' +
                    '<span></span>' +
                '</label>' +
            '</header>' +
            '<p class="mk-card__about">' + esc(plugin.description || '') + '</p>' +
            warnings +
            '<footer class="mk-card__foot">' + chips + binds +
                '<span class="mk-card__act">' + actions + '</span>' +
            '</footer>' +
        '</article>';
    }

    function warn(text) {
        return '<div class="mk-card__warn"><i class="bi bi-exclamation-triangle-fill"></i><span>' +
            esc(text) + '</span></div>';
    }

    function renderGrid() {
        var grid = document.getElementById('mk-grid');
        var empty = document.getElementById('mk-empty');
        if (!grid || !empty) return;

        var visible = state.plugins.filter(matches);
        empty.innerHTML = '';
        empty.style.display = 'none';

        if (!visible.length) {
            grid.innerHTML = '';
            grid.style.display = 'none';
            empty.style.display = '';
            empty.innerHTML = emptyHtml();
            bindEmptyActions();
            return;
        }

        grid.style.display = '';
        grid.innerHTML = visible.map(cardHtml).join('');
    }

    function emptyHtml() {
        /* Three different "nothing here", three different next steps. Collapsing
           them into one message is how a user concludes the page is broken. */
        if (!state.plugins.length) {
            return '<div class="mk-empty"><i class="bi bi-inbox"></i>' +
                '<div class="mk-empty__title">' + esc(t('pm.empty.none', '没有读到任何插件')) + '</div>' +
                '<div class="mk-empty__hint">' +
                    esc(t('pm.empty.noneHint', '插件注册表为空，通常是宿主启动时插件加载失败，先看网关启动日志。')) +
                '</div></div>';
        }

        var filtered = state.query || state.source !== 'all' || state.enabledOnly;
        if (!filtered && state.source === 'all') {
            return '';
        }

        if (state.source === 'external' && !state.query && !state.enabledOnly) {
            return '<div class="mk-empty"><i class="bi bi-box"></i>' +
                '<div class="mk-empty__title">' + esc(t('pm.empty.external', '还没有安装外部插件')) + '</div>' +
                '<div class="mk-empty__hint">' +
                    esc(t('pm.empty.externalHint', '内置插件已随宿主一起发布。外部插件用模板生成、发布成一个目录，再把目录路径填进「安装外部插件」。')) +
                '</div>' +
                '<pre>' + esc([
                    'dotnet new aneiang-yarp-plugin -n MyPlugin --PluginId my-plugin',
                    'dotnet publish -c Release -o out',
                    t('pm.empty.cmdHint', '# 把 out 目录的绝对路径填进上面的安装对话框')
                ].join('\n')) + '</pre></div>';
        }

        return '<div class="mk-empty"><i class="bi bi-search"></i>' +
            '<div class="mk-empty__title">' + esc(t('pm.empty.filtered', '没有符合条件的插件')) + '</div>' +
            '<div class="mk-empty__hint">' + esc(t('pm.empty.filteredHint', '换一个关键词，或把筛选条件清空。')) + '</div>' +
            '<button type="button" class="btn btn-sm btn-outline-secondary mt-3" data-act="reset">' +
                esc(t('pm.clearFilters', '清空筛选')) + '</button></div>';
    }

    function bindEmptyActions() {
        var btn = document.querySelector('#mk-empty [data-act="reset"]');
        if (!btn) return;
        btn.addEventListener('click', function () {
            state.query = '';
            state.source = 'all';
            state.enabledOnly = false;
            var search = document.getElementById('mk-search');
            if (search) search.value = '';
            var only = document.getElementById('mk-enabled-only');
            if (only) only.checked = false;
            syncSegmented();
            renderGrid();
        });
    }

    function syncSegmented() {
        document.querySelectorAll('#mk-source button').forEach(function (btn) {
            btn.classList.toggle('is-on', btn.getAttribute('data-source') === state.source);
        });
    }

    /* -------------------------------------------------------------- mutations */

    async function setEnabled(pluginId, enabled, input) {
        input.disabled = true;
        try {
            await window.DashboardApi.togglePlugin(pluginId, enabled);
            await load();
        } catch (error) {
            input.checked = !enabled;
            input.disabled = false;
            fail(t('common.operationFailed', '操作失败'), error);
        }
    }

    function uninstall(pluginId) {
        window.DashboardModals.showConfirm(
            t('pm.uninstall.confirm', '卸载插件') + ' 「' + pluginId + '」？' +
                t('pm.uninstall.confirmHint', '只能卸载已停用且未绑定任何目标的插件。'),
            async function () {
                try {
                    await window.DashboardApi.uninstallPlugin(pluginId);
                    window.DashboardModals.showSuccess(t('pm.uninstall.ok', '插件已卸载'));
                    await load();
                } catch (error) {
                    fail(t('pm.uninstall.failed', '卸载失败'), error);
                }
            },
            null,
            { title: t('pm.action.uninstall', '卸载'), danger: true }
        );
    }

    /* Install and upgrade differ by one field (target id), so they share a dialog -
       two dialogs with the same body would only be two places to fix. */
    function openInstaller(target) {
        var isUpgrade = !!target;
        var modal = document.createElement('div');
        modal.className = 'modal fade';
        modal.tabIndex = -1;
        modal.dataset.bsBackdrop = 'static';
        modal.innerHTML =
            '<div class="modal-dialog modal-dialog-centered">' +
            '<div class="modal-content">' +
                '<div class="modal-header">' +
                    '<h5 class="modal-title"><i class="bi ' + (isUpgrade ? 'bi-arrow-up-circle' : 'bi-box-arrow-in-down') + ' me-2"></i>' +
                        esc(isUpgrade ? t('pm.upgrade.title', '升级插件') + ' · ' + target.pluginId
                                      : t('pm.install.title', '安装外部插件')) +
                    '</h5>' +
                    '<button type="button" class="btn-close" data-bs-dismiss="modal"></button>' +
                '</div>' +
                '<div class="modal-body">' +
                    '<label class="form-label fw-semibold small">' +
                        esc(t('pm.install.dir', '插件目录（服务器上的绝对路径）')) + '</label>' +
                    '<input type="text" class="form-control font-monospace" data-role="dir" ' +
                        'placeholder="' + esc(isUpgrade ? '/opt/plugins/my-plugin-2.0' : '/opt/plugins/my-plugin') + '">' +
                    '<div class="form-text">' +
                        esc(t('pm.install.dirHint', '该目录必须已经含有 plugin.json 与插件程序集，通常是 dotnet publish 的输出目录。')) +
                    '</div>' +
                    '<div class="alert alert-secondary py-2 px-3 mt-3 mb-0 small">' + esc(t('pm.install.note',
                        '安装只做复制与登记，不会重启网关，也不会自动启用插件。启用与绑定在「插件配置」里完成。')) + '</div>' +
                    '<div data-role="error" class="text-danger small mt-2"></div>' +
                '</div>' +
                '<div class="modal-footer">' +
                    '<button type="button" class="btn btn-secondary btn-sm" data-bs-dismiss="modal">' +
                        esc(t('common.cancel', '取消')) + '</button>' +
                    '<button type="button" class="btn btn-primary btn-sm" data-role="submit">' +
                        '<i class="bi bi-check-lg me-1"></i>' + esc(t('common.save', '保存')) + '</button>' +
                '</div>' +
            '</div></div>';

        document.body.appendChild(modal);
        var instance = new bootstrap.Modal(modal);
        var input = modal.querySelector('[data-role="dir"]');
        var submit = modal.querySelector('[data-role="submit"]');
        var errorBox = modal.querySelector('[data-role="error"]');

        submit.addEventListener('click', async function () {
            var dir = (input.value || '').trim();
            if (!dir) {
                errorBox.textContent = t('pm.install.dirRequired', '请填写插件目录。');
                return;
            }
            var original = submit.innerHTML;
            submit.disabled = true;
            submit.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>' +
                esc(t('common.saving', '保存中...'));
            errorBox.textContent = '';
            try {
                if (isUpgrade) await window.DashboardApi.upgradePlugin(target.pluginId, dir);
                else await window.DashboardApi.installPlugin(dir);
                instance.hide();
                window.DashboardModals.showSuccess(isUpgrade
                    ? t('pm.upgrade.ok', '插件已升级，重启网关后生效')
                    : t('pm.install.ok', '插件已安装，可在列表中启用并绑定'));
                await load();
            } catch (error) {
                submit.disabled = false;
                submit.innerHTML = original;
                /* Stay open: the server's message is the only thing that says what
                   is wrong with the directory (missing manifest, wrong TFM, …). */
                errorBox.textContent = error.message || String(error);
            }
        });

        modal.addEventListener('hidden.bs.modal', function () { modal.remove(); });
        instance.show();
        setTimeout(function () { input.focus(); }, 200);
    }

    function fail(title, error) {
        if (window.DashboardModals) {
            window.DashboardModals.showError(title + ': ' + (error.message || error));
        }
    }

    /* ------------------------------------------------------------------- boot */

    function bindEvents() {
        var search = document.getElementById('mk-search');
        if (search) {
            search.addEventListener('input', function () {
                state.query = (search.value || '').trim();
                renderGrid();
            });
        }

        var source = document.getElementById('mk-source');
        if (source) {
            source.addEventListener('click', function (event) {
                var btn = event.target.closest ? event.target.closest('button[data-source]') : null;
                if (!btn) return;
                state.source = btn.getAttribute('data-source');
                syncSegmented();
                renderGrid();
            });
        }

        var only = document.getElementById('mk-enabled-only');
        if (only) {
            only.addEventListener('change', function () {
                state.enabledOnly = only.checked;
                renderGrid();
            });
        }

        var installBtn = document.getElementById('mk-install-btn');
        if (installBtn) installBtn.addEventListener('click', function () { openInstaller(null); });

        /* One delegated handler for the whole grid: cards are re-rendered on every
           refresh, so per-card listeners would be re-bound (and dropped) endlessly. */
        var grid = document.getElementById('mk-grid');
        if (grid) {
            grid.addEventListener('click', function (event) {
                var trigger = event.target.closest ? event.target.closest('[data-act]') : null;
                if (!trigger || trigger.tagName === 'INPUT') return;
                var act = trigger.getAttribute('data-act');
                var id = trigger.getAttribute('data-id');
                if (act === 'uninstall') uninstall(id);
                else if (act === 'upgrade') {
                    var plugin = state.plugins.filter(function (p) { return p.pluginId === id; })[0];
                    openInstaller(plugin || { pluginId: id });
                }
            });
            grid.addEventListener('change', function (event) {
                var input = event.target;
                if (!input || input.getAttribute('data-act') !== 'toggle') return;
                setEnabled(input.getAttribute('data-id'), input.checked, input);
            });
        }
    }

    window.PluginMarket = {
        init: async function () {
            bindEvents();
            await load();
            /* Enabling a plugin changes its binding state elsewhere; refresh on return. */
            document.addEventListener('dashboard:visibilityResume', load);
        },
        load: load
    };
})();
