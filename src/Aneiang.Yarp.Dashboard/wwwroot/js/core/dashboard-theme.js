/**
 * dashboard-theme.js —— 控制台配色模式（浅色 / 深色）
 *
 * 为什么单独一个模块：颜色本身写在 CSS 令牌里（styles/theme.css），这里只负责
 * 「决定用哪一套」并落到 <html data-theme="...">。所有需要跟着变的部件（图表、
 * Monaco）监听 aneiang:themechange 事件，不去读 localStorage。
 *
 * 持久化键：aneiang-theme = "light" | "dark"（缺省 light）。
 * 首屏防闪烁由 <head> 里的内联脚本负责，不等这个文件加载。
 *
 * 为什么没有「跟随系统」：这是运维盯盘的常驻页面，用户要的是「现在是什么就是
 * 什么」。跟随系统会让外观在傍晚自己变一次，正在对照日志的人得重新适应一套配色，
 * 而且系统偏好变化时整页表单控件的对比会跳变。锁定两态更可控。
 */
(function (window, document) {
    "use strict";

    var STORAGE_KEY = "aneiang-theme";
    var MODES = ["light", "dark"];

    var LABELS = {
        zh: {
            light: "浅色",
            dark: "深色",
            hint: "外观：{0}（点击切换到{1}）"
        },
        en: {
            light: "Light",
            dark: "Dark",
            hint: "Appearance: {0} (click for {1})"
        }
    };

    function lang() {
        var l = (document.documentElement.getAttribute("lang") || "zh-CN").toLowerCase();
        return l.indexOf("en") === 0 ? "en" : "zh";
    }

    function readMode() {
        try {
            var v = localStorage.getItem(STORAGE_KEY);
            // 历史值 "system"（旧的三态）统一归到浅色，避免老用户卡在无效值上
            return MODES.indexOf(v) >= 0 ? v : "light";
        } catch (e) {
            return "light";
        }
    }

    function apply(mode) {
        mode = MODES.indexOf(mode) >= 0 ? mode : "light";
        var theme = mode;
        var root = document.documentElement;

        root.setAttribute("data-theme", theme);
        root.setAttribute("data-theme-mode", mode);
        // Bootstrap 5.3 自带暗色变量，同步过去让 .modal / .table / .form-* 跟随
        root.setAttribute("data-bs-theme", theme);

        try {
            localStorage.setItem(STORAGE_KEY, mode);
        } catch (e) {
            /* 隐私模式下写不进去，忽略 */
        }

        syncButton(mode);
        themeCharts();
        document.dispatchEvent(
            new CustomEvent("aneiang:themechange", { detail: { mode: mode, theme: theme } })
        );
        return theme;
    }

    /**
     * 画布不认 CSS 变量：Chart.js 的刻度/网格颜色必须在变了主题之后重新赋值，
     * 否则深色下会残留一层黑网格 + 深灰刻度，等于没有图表。
     */
    function themeCharts() {
        var Chart = window.Chart;
        var isDark = document.documentElement.getAttribute("data-theme") === "dark";

        // Monaco 自带主题切换，全局设一次即可作用于已创建的编辑器
        if (window.monaco && window.monaco.editor && window.monaco.editor.setTheme) {
            try {
                window.monaco.editor.setTheme(isDark ? "vs-dark" : "vs");
            } catch (e) {
                /* 编辑器还没初始化完 */
            }
        }

        if (!Chart) return;
        var cs = getComputedStyle(document.documentElement);
        var tick = (cs.getPropertyValue("--chart-tick") || "").trim() || "#64748b";
        var grid = (cs.getPropertyValue("--chart-grid") || "").trim() || "rgba(15,23,42,0.07)";

        if (Chart.defaults) {
            Chart.defaults.color = tick;
            Chart.defaults.borderColor = grid;
        }

        var instances = Chart.instances || {};
        Object.keys(instances).forEach(function (k) {
            var c = instances[k];
            if (!c || !c.options || !c.options.scales) return;
            Object.keys(c.options.scales).forEach(function (axis) {
                var s = c.options.scales[axis];
                if (!s) return;
                if (s.ticks) s.ticks.color = tick;
                // 只有原本画了网格的轴才改颜色，display:false 的保持不画
                if (s.grid && s.grid.display !== false) s.grid.color = grid;
            });
            if (c.options.plugins && c.options.plugins.legend && c.options.plugins.legend.labels) {
                c.options.plugins.legend.labels.color = tick;
            }
            try {
                c.update("none");
            } catch (e) {
                /* 图表可能已销毁 */
            }
        });
    }

    function syncButton(mode) {
        var btn = document.getElementById("theme-toggle");
        if (!btn) return;
        var dict = LABELS[lang()];
        var icon = btn.querySelector("i");
        var isDark = mode === "dark";

        if (icon) icon.className = "bi " + (isDark ? "bi-moon-stars" : "bi-sun");

        var label = dict.hint.replace("{0}", dict[mode] || dict.light)
            .replace("{1}", isDark ? dict.light : dict.dark);
        btn.setAttribute("title", label);
        btn.setAttribute("aria-label", label);
        btn.setAttribute("aria-pressed", isDark ? "true" : "false");
    }

    function set(mode) {
        return apply(mode);
    }

    /** 点击切换：浅色 ⇄ 深色 */
    function cycle() {
        return apply(readMode() === "dark" ? "light" : "dark");
    }

    function bind() {
        var btn = document.getElementById("theme-toggle");
        if (btn && !btn.dataset.themeBound) {
            btn.dataset.themeBound = "1";
            btn.addEventListener("click", function (e) {
                e.preventDefault();
                cycle();
            });
        }
        // 语言切换后按钮文案要跟着变
        document.addEventListener("localechange", function () {
            syncButton(readMode());
        });
        apply(readMode());
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", bind);
    } else {
        bind();
    }

    window.AneiangTheme = {
        get: readMode,
        getTheme: readMode,
        set: set,
        cycle: cycle,
        apply: apply,
        themeCharts: themeCharts,
        chartColors: function () {
            var cs = getComputedStyle(document.documentElement);
            return {
                tick: (cs.getPropertyValue("--chart-tick") || "").trim() || "#64748b",
                grid: (cs.getPropertyValue("--chart-grid") || "").trim() || "rgba(15,23,42,0.07)"
            };
        },
        modes: MODES.slice()
    };
})(window, document);
