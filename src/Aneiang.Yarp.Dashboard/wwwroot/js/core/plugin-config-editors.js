/**
 * Plugin Config Editors - per-plugin replacement for the generic schema form.
 *
 * The generic renderer (dashboard-capabilities.js) walks the JSON schema and emits
 * one control per property. That is the right default, but it degrades badly for
 * plugins whose schema contains nested arrays of objects: WAF's `rules[] ->
 * conditions[]` comes out as "Item 1" inside "Item 1", fourteen fields deep, with
 * no hint about which fields matter and which are mutually exclusive.
 *
 * This module lets a plugin ship its own editor. Everything generic stays generic;
 * only the plugins that need it opt in.
 *
 * Contract - an editor is a function (host, schema, config, pluginId) that renders
 * into `host` and attaches `host.__readConfig = function () { return config | null }`.
 * Returning null means "invalid"; the caller then refuses to save and the editor
 * is responsible for saying what is wrong. Attaching the reader to the host keeps
 * both call sites (capability modal and plugin binding modal) unchanged - they
 * both go through renderGroupedFields / readSchemaForm.
 */
(function () {
    'use strict';

    /* ── shared helpers ─────────────────────────────────────────────────── */

    function t(key, fallback, params) {
        var text = typeof window.__ === 'function' ? window.__(key, params) : null;
        if (!text || text === key) text = fallback || key;
        /* window.__ already substituted the parameters on the translated path,
           but the fallback strings carry the same placeholders and go through
           neither - so substitute again here, over every occurrence. */
        if (params && text) {
            Object.keys(params).forEach(function (name) {
                text = text.split('{' + name + '}').join(params[name]);
            });
        }
        return text;
    }

    function esc(value) {
        return window.DashboardUtils && window.DashboardUtils.escapeHtml
            ? window.DashboardUtils.escapeHtml(value == null ? '' : String(value))
            : String(value == null ? '' : value);
    }

    function node(tag, className, text) {
        var element = document.createElement(tag);
        if (className) element.className = className;
        if (text != null) element.textContent = text;
        return element;
    }

    function clear(element) {
        while (element.firstChild) element.removeChild(element.firstChild);
    }

    function icon(name, extra) {
        return node('i', 'bi ' + name + (extra ? ' ' + extra : ''));
    }

    /* Numeric config fields must stay numbers: the server deserialises them as
       integers, and a string "1024" fails silently back to the default. */
    function toInt(value, fallback) {
        var parsed = parseInt(value, 10);
        return isNaN(parsed) ? fallback : parsed;
    }

    function clone(value) {
        return value == null ? value : JSON.parse(JSON.stringify(value));
    }

    function bytesLabel(bytes) {
        if (bytes == null || isNaN(bytes)) return '';
        if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(bytes % (1024 * 1024) ? 1 : 0) + ' MB';
        if (bytes >= 1024) return Math.round(bytes / 1024) + ' KB';
        return bytes + ' B';
    }

    /* Plain address, CIDR, or last-octet wildcard - the three forms the server's
       IpMatcher accepts. The form must not be narrower than the server. */
    function looksLikeAddress(value) {
        if (!value) return false;
        return /^(\d{1,3}\.){3}(\d{1,3}|\*)(\/\d{1,2})?$/.test(String(value).trim()) ||
            /^[0-9a-fA-F:]{2,}:(\/[0-9]{1,3})?$/.test(String(value).trim());
    }

    /* Same durations the plugin accepts: a unit is mandatory, because a bare
       number is read as 300 *days* by TimeSpan and the server rejects it. */
    function looksLikeDuration(value) {
        if (!value) return false;
        var text = String(value).trim();
        return /^\d+(\.\d+)?[smhd]$/i.test(text) || /^\d+:\d{2}(:\d{2})?$/.test(text);
    }

    /* ── registry ───────────────────────────────────────────────────────── */

    var editors = {};

    window.PluginConfigEditors = {
        get: function (pluginId) { return editors[pluginId] || null; },
        has: function (pluginId) { return !!editors[pluginId]; },
        register: function (pluginId, editor) { editors[pluginId] = editor; },
        /* Optional: the one-line text a binding row shows. Returning '' falls
           back to the generic "key: value, key: value" summary. */
        summarize: function (pluginId, config) { return summarizeFor(pluginId, config) || ''; }
    };

    /* ══ WAF ══════════════════════════════════════════════════════════════
     *
     * Several choices here are about what the form *hides*, which is the whole
     * point of replacing the generic renderer:
     *
     *  1. A rule is a card whose title is a sentence, not an index. "拦截 SQL
     *     注入（路径 前缀 /api/）" can be read without expanding anything; the
     *     old "Item 1 / Item 1" told you nothing and appeared at two levels.
     *
     *  2. Four fields are visible on a fresh rule: detect, compare against
     *     what, and what to do. The other ten appear only when they can affect
     *     behaviour - `tag` when the action is tag, `statusCode` when it blocks,
     *     `matchMode` only with two or more conditions, `name` only for headers
     *     and cookies. Showing `tag` on a blocking rule is how people end up
     *     filling a field that is thrown away.
     *
     *  3. The operator list is filtered by source. Offering `cidr` on a path
     *     condition is offering a combination that can never match.
     *
     *  4. Validation is stricter than the server, deliberately. The compiler
     *     drops an invalid condition with a log warning and keeps the rest, so
     *     an empty value does not fail the save - it silently disappears, and a
     *     rule scoped to /admin/ becomes unscoped. The form must refuse what the
     *     compiler would drop, because the user cannot see the log.
     */

    var ACTION_META = {
        block: { icon: 'bi-slash-circle-fill', tone: 'block' },
        allow: { icon: 'bi-check-circle-fill', tone: 'allow' },
        log: { icon: 'bi-eye-fill', tone: 'log' },
        tag: { icon: 'bi-tag-fill', tone: 'tag' },
        'rate-limit': { icon: 'bi-speedometer2', tone: 'limit' }
    };

    var SOURCES = ['path', 'method', 'query', 'body', 'header', 'cookie', 'userAgent', 'ip', 'contentType', 'bodySize'];
    var ACTIONS = ['block', 'allow', 'log', 'tag', 'rate-limit'];
    var PARTITIONS = ['ip', 'ip-path', 'path', 'global'];
    var BUILTINS = ['', 'sql-injection', 'xss', 'path-traversal'];
    var METHODS = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD', 'OPTIONS'];

    /* Operators that can actually produce a match for each source, most useful
       first. `cidr` only makes sense against an address; `greaterThan` only
       against a number. */
    var OPS_BY_SOURCE = {
        path: ['prefix', 'equals', 'contains', 'suffix', 'regex'],
        method: ['equals', 'prefix', 'contains', 'regex'],
        query: ['contains', 'regex', 'equals', 'prefix', 'suffix'],
        body: ['contains', 'regex', 'equals', 'prefix', 'suffix'],
        header: ['equals', 'contains', 'prefix', 'suffix', 'regex'],
        cookie: ['equals', 'contains', 'prefix', 'suffix', 'regex'],
        userAgent: ['contains', 'prefix', 'equals', 'regex'],
        ip: ['cidr', 'equals', 'contains', 'prefix', 'regex'],
        contentType: ['prefix', 'equals', 'contains', 'suffix', 'regex'],
        bodySize: ['greaterThan', 'lessThan', 'equals']
    };

    /* The rules people actually write. Starting from a filled card beats
       starting from fourteen empty fields. */
    var TEMPLATES = [
        { key: 'sqli', builtin: 'sql-injection', action: 'block', path: '/api/' },
        { key: 'xss', builtin: 'xss', action: 'block' },
        { key: 'traversal', builtin: 'path-traversal', action: 'block' },
        { key: 'ip', builtin: '', action: 'block', source: 'ip', op: 'cidr', value: '203.0.113.0/24' },
        { key: 'throttle', builtin: '', action: 'rate-limit', path: '/login', rateLimit: { limit: 5, window: '1m', partition: 'ip' } },
        { key: 'observe', builtin: 'sql-injection', action: 'log' }
    ];

    var BUILTIN_SWITCHES = [
        { key: 'enablePathTraversalDetection', builtin: 'path-traversal' },
        { key: 'enableSqlInjectionDetection', builtin: 'sql-injection' },
        { key: 'enableXssDetection', builtin: 'xss' }
    ];

    function sourceLabel(source) { return t('schema.waf.source.' + source, source); }
    function opLabel(op) { return t('schema.waf.op.' + op, op); }
    function actionLabel(action) { return t('schema.waf.action.' + action, action); }
    function builtinLabel(builtin) { return builtin ? t('schema.waf.builtin.' + builtin, builtin) : ''; }
    function opsFor(source) { return OPS_BY_SOURCE[source] || OPS_BY_SOURCE.path; }

    /* One condition, abbreviated, for the card title. */
    function conditionSummary(condition) {
        var source = condition.source || 'path';
        var value = String(condition.value == null ? '' : condition.value);
        var prefix = (source === 'header' || source === 'cookie') && condition.name
            ? condition.name + ' '
            : '';

        if (source === 'bodySize') {
            var number = Number(value);
            return prefix + (condition.op === 'lessThan' ? '< ' : '> ') +
                (isFinite(number) ? bytesLabel(number) : value);
        }

        var shown = value.length > 24 ? value.slice(0, 24) + '…' : value;
        return prefix + sourceLabel(source) + ' ' + opLabel(condition.op || 'contains') + ' ' + shown;
    }

    /* The sentence on the card header: a rule should be readable without
       expanding it. */
    function describeRule(rule) {
        var action = rule.action || 'block';
        var meta = ACTION_META[action] || ACTION_META.block;
        var conditions = Array.isArray(rule.conditions) ? rule.conditions : [];

        /* A throttle with no conditions is not an empty rule — it is a route-wide cap, which is
           a legitimate thing to ask for and the only action where "matches everything" is sane. */
        var unscoped = action === 'rate-limit'
            ? t('wafcfg.rule.wholeRoute', '全部请求')
            : t('wafcfg.rule.empty', '空规则');

        var head = rule.builtin
            ? builtinLabel(rule.builtin)
            : (conditions.length ? t('wafcfg.rule.custom', '自定义条件') : unscoped);

        var title;
        if (action === 'tag') {
            title = t('wafcfg.title.tag', '打标签 {tag}', { tag: rule.tag || '?' });
        } else if (action === 'rate-limit') {
            var quota = rule.rateLimit || {};
            title = t('wafcfg.title.rateLimit', '限流 {limit} 次 / {window}', {
                limit: quota.limit || '?',
                window: quota.window || '?'
            }) + ' ' + head;
        } else {
            title = actionLabel(action) + ' ' + head;
        }

        if (conditions.length) {
            var joiner = rule.matchMode === 'any' ? t('wafcfg.cond.or', ' 或 ') : t('wafcfg.cond.and', ' 且 ');
            var parts = conditions.slice(0, 2).map(conditionSummary).join(joiner);
            if (conditions.length > 2) parts += t('wafcfg.cond.more', ' 等 {n} 个条件', { n: conditions.length });
            title += '（' + parts + '）';
        }

        return { title: title, icon: meta.icon, tone: meta.tone };
    }

    /* ── small controls ─────────────────────────────────────────────────── */

    function select(options, value, onChange, className) {
        var element = node('select', className || 'form-select form-select-sm');
        options.forEach(function (option) {
            element.appendChild(new Option(option.label, option.value, false, String(option.value) === String(value)));
        });
        element.addEventListener('change', function () { onChange(element.value); });
        return element;
    }

    function toggle(checked, onChange, title) {
        var wrapper = node('label', 'wafcfg-switch');
        if (title) wrapper.title = title;
        var input = node('input', 'form-check-input');
        input.type = 'checkbox';
        input.checked = !!checked;
        input.addEventListener('change', function () { onChange(input.checked); });
        wrapper.appendChild(input);
        return wrapper;
    }

    function inlineSwitch(checked, onChange, label, disabled) {
        var row = inlineRow([toggle(checked, onChange), node('span', 'wafcfg-inline-label', label)]);
        if (disabled) {
            row.classList.add('is-muted');
            row.querySelector('input').disabled = true;
        }
        return row;
    }

    function field(label, control, hint) {
        var wrapper = node('div', 'wafcfg-field');
        if (label) wrapper.appendChild(node('label', 'wafcfg-field__label', label));
        wrapper.appendChild(control);
        var hintEl = hint ? node('div', 'wafcfg-field__hint', hint) : null;
        if (hintEl) wrapper.appendChild(hintEl);
        wrapper.__hint = hintEl;
        return wrapper;
    }

    function inlineRow(children, className) {
        var row = node('div', 'wafcfg-row' + (className ? ' ' + className : ''));
        children.forEach(function (child) { if (child) row.appendChild(child); });
        return row;
    }

    function textInput(className, placeholder, mono) {
        var input = node('input', 'form-control form-control-sm' + (mono ? ' font-monospace' : '') +
            (className ? ' ' + className : ''));
        if (placeholder) input.placeholder = placeholder;
        return input;
    }

    function numberInput(className, min, max, step, placeholder) {
        var input = textInput(className);
        input.type = 'number';
        if (min != null) input.min = String(min);
        if (max != null) input.max = String(max);
        if (step != null) input.step = String(step);
        if (placeholder) input.placeholder = placeholder;
        return input;
    }

    var methodListReady = false;
    function ensureMethodList() {
        if (methodListReady) return;
        methodListReady = true;
        var list = node('datalist');
        list.id = 'wafcfg-method-list';
        METHODS.forEach(function (method) { list.appendChild(new Option(method, method)); });
        document.body.appendChild(list);
    }

    /* Free-form list editor (IP lists, scanned content types). A textarea of one
       entry per line works, but it hides how many entries there are and cannot
       validate a single entry - a typo in a CIDR mask is invisible. Chips show
       both. */
    function chipList(values, onChange, options) {
        options = options || {};
        var wrapper = node('div', 'wafcfg-chips');
        var entries = (Array.isArray(values) ? values : []).slice();

        function commit(next) {
            entries = next;
            onChange(entries.slice());
            paint();
        }

        function paint() {
            clear(wrapper);

            entries.forEach(function (entry, index) {
                var chip = node('span', 'wafcfg-chip');
                if (options.validate && !options.validate(entry)) chip.classList.add('is-bad');
                chip.appendChild(node('span', 'wafcfg-chip__text', entry));
                var remove = node('button', 'wafcfg-chip__x');
                remove.type = 'button';
                remove.innerHTML = '&times;';
                remove.setAttribute('aria-label', t('wafcfg.chip.remove', '移除'));
                remove.addEventListener('click', function () {
                    var next = entries.slice();
                    next.splice(index, 1);
                    commit(next);
                });
                chip.appendChild(remove);
                wrapper.appendChild(chip);
            });

            var input = textInput('wafcfg-chip-input__field', options.placeholder || t('wafcfg.chip.placeholder', '输入后回车添加'), options.mono);

            function add() {
                var raw = input.value.trim();
                if (!raw) return;
                if (options.validate && !options.validate(raw)) {
                    input.classList.add('is-invalid');
                    return;
                }
                input.classList.remove('is-invalid');
                input.value = '';
                if (entries.indexOf(raw) < 0) commit(entries.concat([raw]));
            }

            input.addEventListener('keydown', function (event) {
                if (event.key === 'Enter') { event.preventDefault(); add(); }
                else if (event.key === 'Backspace' && !input.value && entries.length) {
                    commit(entries.slice(0, entries.length - 1));
                }
            });
            input.addEventListener('blur', add);
            wrapper.appendChild(input);
        }

        paint();
        return wrapper;
    }

    /* A collapsible block whose header doubles as a summary line. Used for the
       sections that matter but are configured once: body scanning, block
       response, IP/size preamble. */
    function section(title, summary, body, open) {
        var wrapper = node('section', 'wafcfg-section');
        var header = node('button', 'wafcfg-section__head');
        header.type = 'button';
        header.setAttribute('aria-expanded', open ? 'true' : 'false');
        header.appendChild(icon('bi-chevron-right', 'wafcfg-section__caret'));
        header.appendChild(node('span', 'wafcfg-section__title', title));

        var summaryEl = node('span', 'wafcfg-section__summary', summary || '');
        if (!summary) summaryEl.classList.add('d-none');
        header.appendChild(summaryEl);

        var content = node('div', 'wafcfg-section__body');
        if (!open) content.classList.add('d-none');
        (Array.isArray(body) ? body : [body]).forEach(function (child) {
            if (child) content.appendChild(child);
        });

        header.addEventListener('click', function () {
            var collapsed = content.classList.toggle('d-none');
            wrapper.classList.toggle('is-open', !collapsed);
            header.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
        });

        wrapper.classList.toggle('is-open', !!open);
        wrapper.append(header, content);
        wrapper.__setSummary = function (text) {
            summaryEl.textContent = text || '';
            summaryEl.classList.toggle('d-none', !text);
        };
        return wrapper;
    }

    /* ── condition row ────────────────────────────────────────────────────
       One condition is one row: [where] [how] [name?] [value] [Aa] [x].
       The name control only exists for header/cookie, the only sources whose
       value is ambiguous without it. */
    function conditionRow(condition, options) {
        var row = node('div', 'wafcfg-cond');
        var source = condition.source || 'path';
        var op = condition.op || 'contains';
        if (opsFor(source).indexOf(op) < 0) op = opsFor(source)[0];

        var sourceEl = select(SOURCES.map(function (item) {
            return { value: item, label: sourceLabel(item) };
        }), source, function (next) {
            condition.source = next;
            if (opsFor(next).indexOf(condition.op) < 0) condition.op = opsFor(next)[0];
            /* A header name means nothing once the source stops being a header. */
            if (next !== 'header' && next !== 'cookie') delete condition.name;
            options.onStructuralChange();
        }, 'form-select form-select-sm wafcfg-cond__source');

        var opEl = select(opsFor(source).map(function (item) {
            return { value: item, label: opLabel(item) };
        }), op, function (next) {
            condition.op = next;
            /* The value control changes shape with the operator (regex vs
               numeric vs plain text), so the row is rebuilt. */
            options.onStructuralChange();
        }, 'form-select form-select-sm wafcfg-cond__op');

        var nameEl = null;
        if (source === 'header' || source === 'cookie') {
            nameEl = textInput('wafcfg-cond__name', source === 'cookie'
                ? t('wafcfg.cond.cookieName', 'Cookie 名')
                : t('wafcfg.cond.headerName', 'Header 名'));
            nameEl.value = condition.name || '';
            nameEl.addEventListener('input', function () {
                condition.name = nameEl.value.trim();
                options.onChange();
            });
        }

        var valueEl = valueControl(condition, options);
        var caseEl = null;

        /* Case sensitivity is not a property of numbers or of a CIDR mask. */
        if (['bodySize', 'cidr', 'greaterThan', 'lessThan'].indexOf(condition.op) < 0 && source !== 'bodySize') {
            caseEl = node('button', 'wafcfg-case');
            caseEl.type = 'button';
            caseEl.textContent = 'Aa';
            caseEl.title = t('schema.waf.ignoreCase', '忽略大小写');
            caseEl.classList.toggle('is-on', condition.ignoreCase !== false);
            caseEl.addEventListener('click', function () {
                condition.ignoreCase = condition.ignoreCase === false;
                caseEl.classList.toggle('is-on', condition.ignoreCase !== false);
                options.onChange();
            });
        }

        var remove = node('button', 'wafcfg-cond__remove');
        remove.type = 'button';
        remove.innerHTML = '<i class="bi bi-x-lg"></i>';
        remove.title = t('wafcfg.cond.remove', '移除条件');
        remove.addEventListener('click', options.onRemove);

        row.append(sourceEl, opEl, nameEl, valueEl, caseEl, remove);
        return row;
    }

    /* The value control follows the source and operator: a method is picked from
       a list, a size is a number in bytes, an address is validated as one. A
       single text box for all ten sources is what made the old form hard to get
       right. */
    function valueControl(condition, options) {
        var source = condition.source || 'path';
        var op = condition.op || 'contains';
        var raw = condition.value == null ? '' : condition.value;

        if (source === 'bodySize') {
            ensureMethodList();
            var sizeInput = numberInput('wafcfg-cond__value', 0, null, 1, '1048576');
            sizeInput.value = raw;
            var unit = node('span', 'wafcfg-cond__unit', bytesLabel(toInt(raw, 0)));
            sizeInput.addEventListener('input', function () {
                condition.value = sizeInput.value;
                unit.textContent = bytesLabel(toInt(sizeInput.value, 0));
                sizeInput.classList.remove('is-invalid');
                options.onChange();
            });
            var wrap = node('div', 'wafcfg-cond__valwrap');
            wrap.append(sizeInput, unit);
            return wrap;
        }

        var input;
        if (source === 'method') {
            ensureMethodList();
            input = textInput('wafcfg-cond__value', 'GET');
            input.setAttribute('list', 'wafcfg-method-list');
        } else if (op === 'regex') {
            input = textInput('wafcfg-cond__value', '^/api/v\\d+/', true);
        } else if (source === 'ip' && op === 'cidr') {
            input = textInput('wafcfg-cond__value', '10.0.0.0/8', true);
        } else {
            input = textInput('wafcfg-cond__value', placeholderFor(source));
        }

        input.value = raw;
        input.addEventListener('input', function () {
            condition.value = input.value;
            input.classList.remove('is-invalid');
            options.onChange();
        });
        return input;
    }

    function placeholderFor(source) {
        switch (source) {
            case 'path': return '/api/';
            case 'query': return 'token=';
            case 'contentType': return 'application/json';
            case 'userAgent': return 'curl';
            case 'ip': return '203.0.113.7';
            default: return '';
        }
    }

    var RAIL_TONES = ['block', 'allow', 'log', 'tag'];

    /* ── rule card ────────────────────────────────────────────────────────
       Three independently-repainted regions so that typing never rebuilds the
       whole card (which would steal focus and reset scroll):

         head       sentence, icon, tone  <- any field that changes the summary
         conditions list                  <- add / remove / change source or op
         action slot                      <- action switch

       Everything else is static once built. */
    function ruleCard(rule, context) {
        var card = node('article', 'wafcfg-rule');
        var head = node('div', 'wafcfg-rule__head');
        var titleEl = node('span', 'wafcfg-rule__title');
        var badgeEl = node('span', 'wafcfg-rule__badge');
        var iconEl = icon('bi-slash-circle-fill', 'wafcfg-rule__icon');

        var rail = node('div', 'wafcfg-rule__rail');
        rail.appendChild(toggle(rule.enabled !== false, function (checked) {
            rule.enabled = checked;
            card.classList.toggle('is-off', !checked);
            context.onDirty();
        }, t('wafcfg.rule.enable', '这条规则是否参与匹配')));
        rail.appendChild(railButton('bi-arrow-up', t('wafcfg.rule.up', '上移'), context.moveUp, context.isFirst));
        rail.appendChild(railButton('bi-arrow-down', t('wafcfg.rule.down', '下移'), context.moveDown, context.isLast));
        rail.appendChild(railButton('bi-files', t('wafcfg.rule.duplicate', '复制'), context.duplicate, false));
        rail.appendChild(railButton('bi-trash', t('wafcfg.rule.remove', '删除'), context.remove, false, 'danger'));

        head.append(iconEl, titleEl, badgeEl, rail);
        card.appendChild(head);
        card.classList.toggle('is-off', rule.enabled === false);

        var body = node('div', 'wafcfg-rule__body');
        card.appendChild(body);

        function paintHead() {
            var described = describeRule(rule);
            titleEl.textContent = described.title;
            iconEl.className = 'bi ' + described.icon + ' wafcfg-rule__icon';
            RAIL_TONES.forEach(function (tone) { card.classList.remove('wafcfg-rule--' + tone); });
            card.classList.add('wafcfg-rule--' + described.tone);
            badgeEl.textContent = rule.id || '';
            badgeEl.classList.toggle('d-none', !rule.id);
        }

        /* ── detector ── */
        var detectorEl = select(BUILTINS.map(function (builtin) {
            return {
                value: builtin,
                label: builtin ? builtinLabel(builtin) : t('wafcfg.builtin.none', '不检测（仅按条件匹配）')
            };
        }), rule.builtin || '', function (next) {
            if (next) rule.builtin = next; else delete rule.builtin;
            paintHead();
            context.onDirty();
        }, 'form-select form-select-sm wafcfg-rule__detector');

        body.appendChild(inlineRow([
            node('span', 'wafcfg-rule__label', t('wafcfg.rule.detect', '检测')),
            detectorEl
        ]));

        /* ── conditions ── */
        var conditions = Array.isArray(rule.conditions) ? rule.conditions : (rule.conditions = []);
        var condBox = node('div', 'wafcfg-rule__conds');
        body.appendChild(condBox);

        function paintConditions() {
            clear(condBox);

            conditions.forEach(function (condition, index) {
                condBox.appendChild(conditionRow(condition, {
                    onStructuralChange: function () { paintConditions(); paintHead(); context.onDirty(); },
                    onChange: function () { paintHead(); context.onDirty(); },
                    onRemove: function () {
                        conditions.splice(index, 1);
                        paintConditions();
                        paintHead();
                        context.onDirty();
                    }
                }));
            });

            /* Two conditions are where all/any becomes a decision worth making;
               with one it is a no-op, so it stays hidden. */
            if (conditions.length > 1) {
                condBox.appendChild(inlineRow([
                    node('span', 'wafcfg-rule__label', t('wafcfg.rule.combine', '条件组合')),
                    select([
                        { value: 'all', label: t('schema.waf.matchMode.all', '全部满足') },
                        { value: 'any', label: t('schema.waf.matchMode.any', '任一满足') }
                    ], rule.matchMode || 'all', function (next) {
                        rule.matchMode = next;
                        paintHead();
                        context.onDirty();
                    }, 'form-select form-select-sm wafcfg-rule__mode')
                ], 'wafcfg-row--sub'));
            }

            var addCondition = node('button', 'wafcfg-add-cond');
            addCondition.type = 'button';
            addCondition.innerHTML = '<i class="bi bi-plus-lg"></i>' + esc(t('wafcfg.cond.add', '添加条件'));
            addCondition.addEventListener('click', function () {
                conditions.push({ source: 'path', op: 'prefix', value: '', ignoreCase: true });
                paintConditions();
                paintHead();
                context.onDirty();
            });
            condBox.appendChild(addCondition);

            if (!conditions.length) {
                var note = node('div', 'wafcfg-rule__note');
                if (rule.builtin || rule.action === 'rate-limit') {
                    note.textContent = t('wafcfg.note.unscoped', '没有条件：对所有请求生效');
                } else {
                    note.classList.add('wafcfg-rule__note--bad');
                    note.textContent = t('wafcfg.note.emptyRule', '既没有检测器也没有条件，这条规则会被忽略');
                }
                condBox.appendChild(note);
            }
        }

        /* ── action, and only the field that action actually uses ── */
        var actionRow = node('div', 'wafcfg-rule__action');
        body.appendChild(actionRow);

        function paintAction() {
            clear(actionRow);
            actionRow.appendChild(node('span', 'wafcfg-rule__label', t('wafcfg.rule.then', '则')));
            actionRow.appendChild(select(ACTIONS.map(function (action) {
                return { value: action, label: actionLabel(action) };
            }), rule.action || 'block', function (next) {
                rule.action = next;
                if (next !== 'tag') delete rule.tag;
                /* statusCode belongs to the two actions that produce a rejection. */
                if (next !== 'block' && next !== 'rate-limit') delete rule.statusCode;
                /* An empty quota block is worse than none: the compiler drops the rule, so the
                   card would look configured and do nothing. Seed it instead. */
                if (next === 'rate-limit' && !rule.rateLimit) {
                    rule.rateLimit = { limit: 5, window: '1m', partition: 'ip' };
                }
                if (next !== 'rate-limit') delete rule.rateLimit;
                paintAction();
                paintHead();
                context.onDirty();
            }, 'form-select form-select-sm wafcfg-rule__actionselect'));

            if (rule.action === 'tag') {
                var tagInput = textInput('wafcfg-rule__extra', 'partner', true);
                tagInput.value = rule.tag || '';
                tagInput.addEventListener('input', function () {
                    rule.tag = tagInput.value.trim();
                    paintHead();
                    context.onDirty();
                });
                actionRow.appendChild(tagInput);
            } else if (rule.action === 'block') {
                var codeInput = numberInput('wafcfg-rule__extra wafcfg-rule__code', 400, 599, 1,
                    String(context.defaultStatus || 403));
                codeInput.value = rule.statusCode == null ? '' : rule.statusCode;
                codeInput.title = t('wafcfg.rule.statusInherit', '留空则使用拦截响应里的全局状态码');
                codeInput.addEventListener('input', function () {
                    var raw = codeInput.value.trim();
                    if (!raw) delete rule.statusCode; else rule.statusCode = toInt(raw, null);
                    context.onDirty();
                });
                actionRow.appendChild(codeInput);
            } else if (rule.action === 'rate-limit') {
                paintQuota(actionRow);
            }
        }

        /* ── quota: three numbers that only mean something together, so they
              share one line instead of becoming three labelled rows ── */
        function paintQuota(host) {
            var quota = rule.rateLimit || (rule.rateLimit = { limit: 5, window: '1m', partition: 'ip' });

            var limitInput = numberInput('wafcfg-rule__extra wafcfg-rule__limit', 1, 1000000, 1, '5');
            limitInput.value = quota.limit == null ? '' : quota.limit;
            limitInput.addEventListener('input', function () {
                var parsed = toInt(limitInput.value, null);
                if (parsed == null) delete quota.limit; else quota.limit = parsed;
                paintHead();
                context.onDirty();
            });
            host.appendChild(limitInput);

            host.appendChild(node('span', 'wafcfg-rule__unit', t('wafcfg.rule.timesPer', '次 /')));

            var windowInput = textInput('wafcfg-rule__extra wafcfg-rule__window', '1m', true);
            windowInput.value = quota.window || '';
            windowInput.title = t('wafcfg.rule.windowHint', '窗口长度，例如 30s、1m、1h');
            windowInput.addEventListener('input', function () {
                quota.window = windowInput.value.trim();
                paintHead();
                context.onDirty();
            });
            host.appendChild(windowInput);

            host.appendChild(node('span', 'wafcfg-rule__unit', t('wafcfg.rule.per', '按')));

            host.appendChild(select(PARTITIONS.map(function (partition) {
                return { value: partition, label: t('schema.waf.partition.' + partition, partition) };
            }), quota.partition || 'ip', function (next) {
                quota.partition = next;
                context.onDirty();
            }, 'form-select form-select-sm wafcfg-rule__partition'));

            var codeInput = numberInput('wafcfg-rule__extra wafcfg-rule__code', 400, 599, 1, '429');
            codeInput.value = rule.statusCode == null ? '' : rule.statusCode;
            codeInput.title = t('wafcfg.rule.statusInheritRate', '留空则返回 429');
            codeInput.addEventListener('input', function () {
                var raw = codeInput.value.trim();
                if (!raw) delete rule.statusCode; else rule.statusCode = toInt(raw, null);
                context.onDirty();
            });
            host.appendChild(codeInput);
        }

        /* ── details: identity, folded away because nobody needs to name a rule
              before it works ── */
        var details = node('details', 'wafcfg-rule__details');
        var detailsSummary = node('summary', 'wafcfg-rule__detailssum', t('wafcfg.rule.details', '标识与名称'));
        details.appendChild(detailsSummary);

        var detailsBody = node('div', 'wafcfg-rule__detailsbody');
        var idInput = textInput(null, t('wafcfg.rule.idPlaceholder', '留空自动生成'), true);
        idInput.value = rule.id || '';
        idInput.addEventListener('input', function () {
            rule.id = idInput.value.trim();
            paintHead();
            context.onDirty();
        });
        detailsBody.appendChild(field(t('schema.waf.id', '规则标识'), idInput,
            t('schema.waf.id.desc', '出现在日志与 X-Waf-Rule 响应头里，方便定位是哪条规则拦下的')));

        var nameInput = textInput(null, t('wafcfg.rule.namePlaceholder', '覆盖卡片的自动描述'));
        nameInput.value = rule.name || '';
        nameInput.addEventListener('input', function () {
            rule.name = nameInput.value.trim();
            context.onDirty();
        });
        detailsBody.appendChild(field(t('schema.waf.name', '名称'), nameInput));
        details.appendChild(detailsBody);
        body.appendChild(details);

        paintConditions();
        paintAction();
        paintHead();
        return card;
    }

    function railButton(iconName, title, onClick, disabled, tone) {
        var button = node('button', 'wafcfg-rail' + (tone ? ' wafcfg-rail--' + tone : ''));
        button.type = 'button';
        button.innerHTML = '<i class="bi ' + iconName + '"></i>';
        button.title = title;
        button.disabled = !!disabled;
        button.addEventListener('click', onClick);
        return button;
    }

    /* ── editor entry point ──────────────────────────────────────────────── */

    function mountWaf(host, schema, config) {
        var working = clone(config) || {};
        var errors = [];

        /* The server parses the mode case-insensitively, so a stored "Monitor"
           would leave the select showing "enforce" while the gateway is in fact
           only logging. Normalise once, here, so what the form says is what runs. */
        working.mode = String(working.mode || '').trim().toLowerCase() === 'monitor' ? 'monitor' : 'enforce';

        ensureMethodList();

        /* The rule list needs more width than the default dialog: a condition row
           alone is four controls, and wrapping it makes the card unreadable. The
           host is already in the document at this point (both callers append the
           modal first), so widening is a class on the enclosing dialog. */
        var dialog = host.closest ? host.closest('.modal-dialog') : null;
        if (dialog) dialog.classList.add('wafcfg-dialog');

        function rules() {
            if (!Array.isArray(working.rules)) working.rules = [];
            return working.rules;
        }

        /* ── ① mode banner ──
           Monitor is the only safe way to try a new rule against live traffic,
           so the switch is at the top and its consequence is spelled out. */
        var banner = node('div', 'wafcfg-banner');
        var bannerText = node('span', 'wafcfg-banner__text');
        var modeSelect = select([
            { value: 'enforce', label: t('schema.waf.mode.enforce', '拦截（enforce）') },
            { value: 'monitor', label: t('schema.waf.mode.monitor', '仅记录（monitor）') }
        ], working.mode || 'enforce', function (next) {
            working.mode = next;
            paintBanner();
        }, 'form-select form-select-sm wafcfg-banner__select');
        banner.append(modeSelect, bannerText);

        function paintBanner() {
            var monitor = working.mode === 'monitor';
            banner.classList.toggle('is-monitor', monitor);
            bannerText.textContent = monitor
                ? t('wafcfg.banner.monitor', '规则照常匹配并写日志，但不会拦截任何请求。用于正式启用前观察误报。')
                : t('wafcfg.banner.enforce', '命中的请求会被拦截，返回"拦截响应"里配置的状态码。');
        }
        paintBanner();
        host.appendChild(banner);

        /* ── ② rules ── */
        var ruleCount = node('span', 'wafcfg-rules__count');
        var ruleList = node('div', 'wafcfg-rules__list');

        var addButton = node('button', 'btn btn-sm btn-primary');
        addButton.type = 'button';
        addButton.innerHTML = '<i class="bi bi-plus-lg"></i>' + esc(t('wafcfg.rule.add', '添加规则'));
        addButton.addEventListener('click', function () { addRule(null); });

        var toolbar = node('div', 'wafcfg-rules__bar');
        toolbar.appendChild(node('span', 'wafcfg-rules__title', t('schema.waf.rules', '自定义规则')));
        toolbar.appendChild(ruleCount);
        var toolbarActions = node('div', 'wafcfg-rules__actions');
        toolbarActions.appendChild(addButton);
        toolbar.appendChild(toolbarActions);

        var templateBar = node('div', 'wafcfg-templates');
        templateBar.appendChild(node('span', 'wafcfg-templates__label', t('wafcfg.template.label', '快速添加')));
        TEMPLATES.forEach(function (template) {
            var chip = node('button', 'wafcfg-template');
            chip.type = 'button';
            chip.textContent = t('wafcfg.template.' + template.key, template.key);
            chip.addEventListener('click', function () { addRule(template); });
            templateBar.appendChild(chip);
        });

        function addRule(seed) {
            var rule = { enabled: true, action: 'block', matchMode: 'all', conditions: [] };
            if (seed) {
                if (seed.builtin) rule.builtin = seed.builtin;
                rule.action = seed.action || 'block';
                if (seed.path) rule.conditions.push({ source: 'path', op: 'prefix', value: seed.path, ignoreCase: true });
                if (seed.source) rule.conditions.push({ source: seed.source, op: seed.op, value: seed.value, ignoreCase: true });
                if (seed.rateLimit) rule.rateLimit = Object.assign({}, seed.rateLimit);
            }
            rules().push(rule);
            paintRules();
            var last = ruleList.lastElementChild;
            if (last && last.scrollIntoView) last.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }

        function moveRule(index, delta) {
            var list = rules();
            var target = index + delta;
            if (target < 0 || target >= list.length) return;
            list.splice(target, 0, list.splice(index, 1)[0]);
            paintRules();
        }

        function paintRules() {
            clear(ruleList);
            var list = rules();
            ruleCount.textContent = list.length ? t('wafcfg.rules.count', '{n} 条', { n: list.length }) : '';

            if (!list.length) {
                var empty = node('div', 'wafcfg-rules__empty');
                empty.appendChild(icon('bi-shield-slash'));
                empty.appendChild(node('p', null, t('wafcfg.rules.empty', '还没有规则。用上面的模板起一条，或添加空白规则。')));
                ruleList.appendChild(empty);
            }

            list.forEach(function (rule, index) {
                ruleList.appendChild(ruleCard(rule, {
                    isFirst: index === 0,
                    isLast: index === list.length - 1,
                    defaultStatus: (working.response && working.response.statusCode) || 403,
                    moveUp: function () { moveRule(index, -1); },
                    moveDown: function () { moveRule(index, 1); },
                    duplicate: function () {
                        var copy = clone(rule);
                        /* A copied rule must not share an id: the id is what the
                           log and the X-Waf-Rule header use to tell them apart. */
                        delete copy.id;
                        rules().splice(index + 1, 0, copy);
                        paintRules();
                    },
                    remove: function () {
                        rules().splice(index, 1);
                        paintRules();
                    },
                    onDirty: function () {
                        errors = [];
                        paintErrorBar();
                        paintBuiltin();
                    }
                }));
            });
        }

        var rulesBox = node('div', 'wafcfg-rules');
        rulesBox.append(toolbar, templateBar, ruleList);
        host.appendChild(rulesBox);
        paintRules();

        /* ── ③ built-in switches ──
           These three only participate when `rules` is empty; the compiler
           synthesises the pipeline from them. Showing them as live controls
           beside a non-empty list is what made the old form dishonest - the
           switches stayed on while having no effect at all. */
        var builtinBox = node('div', 'wafcfg-builtin');
        var builtinTitle = node('div', 'wafcfg-builtin__title');
        builtinTitle.innerHTML = '<i class="bi bi-toggles"></i>' + esc(t('wafcfg.builtin.title', '内置防护开关'));
        var builtinNote = node('div', 'wafcfg-builtin__note');
        var builtinRows = node('div', 'wafcfg-builtin__rows');

        function paintBuiltin() {
            clear(builtinRows);
            var overridden = rules().length > 0;

            BUILTIN_SWITCHES.forEach(function (entry) {
                var row = node('div', 'wafcfg-builtin__row');
                var sw = toggle(working[entry.key] !== false, function (checked) {
                    working[entry.key] = checked;
                    paintBuiltinNote();
                });
                if (overridden) {
                    row.classList.add('is-muted');
                    sw.querySelector('input').disabled = true;
                }
                row.appendChild(sw);
                row.appendChild(node('span', 'wafcfg-builtin__name', builtinLabel(entry.builtin)));
                row.appendChild(node('span', 'wafcfg-builtin__desc', t('wafcfg.builtin.' + entry.builtin, '')));
                builtinRows.appendChild(row);
            });

            paintBuiltinNote();
        }

        function paintBuiltinNote() {
            clear(builtinNote);

            if (rules().length > 0) {
                builtinNote.className = 'wafcfg-builtin__note is-warn';
                builtinNote.appendChild(icon('bi-exclamation-triangle-fill'));
                builtinNote.appendChild(node('span', null,
                    t('wafcfg.builtin.overridden', '规则列表非空，上面三个开关不参与检测。删掉所有规则后它们才会重新生效。')));
                return;
            }

            builtinNote.className = 'wafcfg-builtin__note';
            builtinNote.appendChild(icon('bi-info-circle'));
            builtinNote.appendChild(node('span', null,
                t('wafcfg.builtin.hint', '规则列表为空时，这三个开关会各合成一条内置规则。')));

            var convert = node('button', 'wafcfg-builtin__convert');
            convert.type = 'button';
            convert.textContent = t('wafcfg.builtin.convert', '转成可编辑规则');
            convert.title = t('wafcfg.builtin.convertHint', '行为不变，只是变成可以看到和调整的规则');
            convert.addEventListener('click', convertBuiltins);
            builtinNote.appendChild(convert);
        }

        /* Converting keeps behaviour identical: the compiler synthesises exactly
           these three rules, in exactly this order. */
        function convertBuiltins() {
            BUILTIN_SWITCHES.forEach(function (entry) {
                if (working[entry.key] === false) return;
                rules().push({
                    id: entry.builtin,
                    builtin: entry.builtin,
                    action: 'block',
                    matchMode: 'all',
                    conditions: []
                });
            });
            paintRules();
            paintBuiltin();
        }

        builtinBox.append(builtinTitle, builtinNote, builtinRows);
        host.appendChild(builtinBox);
        paintBuiltin();

        /* ── ④ body scanning ── */
        var bodyInsp = working.bodyInspection || (working.bodyInspection = {
            enabled: true,
            maxBytes: 65536,
            contentTypes: ['text/', 'application/json', 'application/x-www-form-urlencoded', 'application/xml', 'application/graphql'],
            inspectUnknownContentType: false
        });
        if (!Array.isArray(bodyInsp.contentTypes)) bodyInsp.contentTypes = [];

        var bodyMaxInput = numberInput(null, 0, null, 1024, '65536');
        bodyMaxInput.value = bodyInsp.maxBytes == null ? 65536 : bodyInsp.maxBytes;
        var bodyMaxHint = node('div', 'wafcfg-field__hint');

        function paintBodyMaxHint() {
            var value = toInt(bodyMaxInput.value, 0);
            bodyMaxHint.textContent = value > 0
                ? bytesLabel(value) + ' · ' + t('wafcfg.body.maxHint', '超过该大小的请求体整段跳过，不做截断扫描')
                : t('wafcfg.body.disabled', '为 0：不扫描请求体');
        }
        bodyMaxInput.addEventListener('input', function () {
            bodyInsp.maxBytes = toInt(bodyMaxInput.value, 0);
            paintBodyMaxHint();
            paintBodySummary();
        });
        paintBodyMaxHint();

        var sectionBody = section(
            t('schema.waf.bodyInspection', '请求体扫描'),
            '',
            [
                inlineSwitch(bodyInsp.enabled !== false, function (checked) {
                    bodyInsp.enabled = checked;
                    paintBodySummary();
                }, t('wafcfg.body.enabled', '扫描请求体')),
                field(t('schema.waf.maxBytes', '最大扫描字节数'), bodyMaxInput),
                bodyMaxHint,
                field(t('schema.waf.contentTypes', '扫描的内容类型'),
                    chipList(bodyInsp.contentTypes, function (next) {
                        bodyInsp.contentTypes = next;
                        paintBodySummary();
                    }, {
                        mono: true,
                        placeholder: 'text/   application/json',
                        validate: function (value) {
                            return /^[\w.+-]+\/(?:[\w.+-]+|\*)?$/.test(value);
                        }
                    }),
                    t('schema.waf.contentTypes.desc', '按前缀匹配，text/ 覆盖所有文本类型。默认不含 multipart：上传信封是传输元数据，不是业务载荷')),
                inlineSwitch(bodyInsp.inspectUnknownContentType === true, function (checked) {
                    bodyInsp.inspectUnknownContentType = checked;
                }, t('schema.waf.inspectUnknownContentType', '扫描无 Content-Type 的请求'))
            ],
            false
        );
        host.appendChild(sectionBody);

        function paintBodySummary() {
            sectionBody.__setSummary(bodyInsp.enabled === false
                ? t('wafcfg.body.off', '已关闭 · 检查请求体的条件都不会命中')
                : bytesLabel(bodyInsp.maxBytes || 0) + ' · ' + bodyInsp.contentTypes.length + ' ' +
                  t('wafcfg.body.types', '种内容类型'));
        }
        paintBodySummary();

        /* ── ⑤ block response ── */
        var response = working.response || (working.response = {
            statusCode: 403,
            contentType: 'application/json; charset=utf-8',
            includeRuleHeader: true
        });

        var statusInput = numberInput(null, 400, 599, 1, '403');
        statusInput.value = response.statusCode == null ? 403 : response.statusCode;
        statusInput.addEventListener('input', function () {
            response.statusCode = toInt(statusInput.value, 403);
        });

        var contentTypeInput = textInput(null, 'application/json; charset=utf-8', true);
        contentTypeInput.value = response.contentType || 'application/json; charset=utf-8';
        contentTypeInput.addEventListener('input', function () {
            response.contentType = contentTypeInput.value.trim();
        });

        var responseBodyInput = node('textarea', 'form-control form-control-sm font-monospace');
        responseBodyInput.rows = 3;
        responseBodyInput.placeholder = '{"error":"blocked"}';
        responseBodyInput.value = response.body || '';
        responseBodyInput.addEventListener('input', function () {
            if (responseBodyInput.value.trim()) response.body = responseBodyInput.value;
            else delete response.body;
        });

        var headerRows = node('div', 'wafcfg-kv');
        var headerEntries = [];

        function syncHeaders() {
            var result = {};
            headerEntries.forEach(function (entry) {
                var key = entry.key.value.trim();
                if (key) result[key] = entry.value.value.trim();
            });
            if (Object.keys(result).length) response.headers = result;
            else delete response.headers;
        }

        function addHeaderRow(key, value) {
            var row = node('div', 'wafcfg-kv__row');
            var keyInput = textInput(null, 'X-Reason', true);
            keyInput.value = key || '';
            var valueInput = textInput(null, 'blocked', true);
            valueInput.value = value == null ? '' : value;
            var remove = node('button', 'wafcfg-kv__remove');
            remove.type = 'button';
            remove.innerHTML = '<i class="bi bi-x-lg"></i>';
            var entry = { key: keyInput, value: valueInput, row: row };
            remove.addEventListener('click', function () {
                var index = headerEntries.indexOf(entry);
                if (index >= 0) headerEntries.splice(index, 1);
                row.remove();
                syncHeaders();
            });
            keyInput.addEventListener('input', syncHeaders);
            valueInput.addEventListener('input', syncHeaders);
            row.append(keyInput, valueInput, remove);
            headerEntries.push(entry);
            headerRows.appendChild(row);
        }

        var configuredHeaders = response.headers || {};
        Object.keys(configuredHeaders).forEach(function (key) { addHeaderRow(key, configuredHeaders[key]); });
        if (!Object.keys(configuredHeaders).length) addHeaderRow('', '');

        var addHeader = node('button', 'wafcfg-add-cond');
        addHeader.type = 'button';
        addHeader.innerHTML = '<i class="bi bi-plus-lg"></i>' + esc(t('wafcfg.response.addHeader', '添加响应头'));
        addHeader.addEventListener('click', function () { addHeaderRow('', ''); });

        var sectionResponse = section(
            t('schema.waf.response', '拦截响应'),
            t('wafcfg.response.summary', '{code} · {type}', {
                code: response.statusCode || 403,
                type: (response.contentType || 'application/json').split(';')[0]
            }),
            [
                inlineRow([
                    field(t('schema.waf.statusCode', '状态码'), statusInput),
                    field(t('schema.waf.contentType', '响应 Content-Type'), contentTypeInput)
                ], 'wafcfg-row--half'),
                field(t('schema.waf.body', '自定义响应体'), responseBodyInput,
                    t('schema.waf.body.desc', '留空则返回一段 JSON，写明是哪条规则命中的')),
                field(t('schema.waf.headers', '附加响应头'), headerRows),
                addHeader,
                inlineSwitch(response.includeRuleHeader !== false, function (checked) {
                    response.includeRuleHeader = checked;
                }, t('schema.waf.includeRuleHeader', '返回命中规则名'))
            ],
            false
        );
        host.appendChild(sectionResponse);

        /* ── ⑥ preamble: runs before every rule, cannot be waived ──
           Placed last on purpose. It is set once and never touched again, while
           the rules above are what people come back to change. The note keeps
           the execution order visible so nobody assumes rules run first. */
        var preambleNote = node('div', 'wafcfg-preamble__note');
        preambleNote.innerHTML = '<i class="bi bi-shield-exclamation"></i>' +
            esc(t('wafcfg.preamble.note', '这些检查在任何规则之前执行，"放行"规则也无法绕过它们。'));

        var sizeFields = [
            { key: 'maxRequestBodySize', label: t('schema.waf.maxRequestBodySize', '最大请求体大小（字节）'), bytes: true, step: 1048576 },
            { key: 'maxHeaderSize', label: t('schema.waf.maxHeaderSize', '最大 Header 大小（字节）'), bytes: true, step: 1024 },
            { key: 'maxHeaderCount', label: t('schema.waf.maxHeaderCount', '最大 Header 数量'), bytes: false, step: 1 },
            { key: 'maxUrlLength', label: t('schema.waf.maxUrlLength', '最大 URL 长度'), bytes: false, step: 256 }
        ].map(function (entry) {
            var input = numberInput(null, 0, null, entry.step,
                String(working[entry.key] == null ? '' : working[entry.key]));
            input.value = working[entry.key] == null ? '' : working[entry.key];
            var wrapper = field(entry.label, input);
            var hint = wrapper.__hint;

            if (entry.bytes) {
                hint = node('div', 'wafcfg-field__hint', bytesLabel(toInt(input.value, 0)));
                wrapper.appendChild(hint);
            }

            input.addEventListener('input', function () {
                var value = toInt(input.value, 0);
                working[entry.key] = value;
                if (entry.bytes && hint) hint.textContent = bytesLabel(value);
                paintPreambleSummary();
            });
            return wrapper;
        });

        var sectionPreamble = section(
            t('wafcfg.preamble.title', '前导检查（IP 与大小）'),
            '',
            [
                preambleNote,
                inlineSwitch(working.enableIpCheck !== false, function (checked) {
                    working.enableIpCheck = checked;
                    paintPreambleSummary();
                }, t('schema.waf.enableIpCheck', 'IP 检查')),
                field(t('schema.waf.ipWhitelist', 'IP 白名单'),
                    chipList(working.ipWhitelist, function (next) {
                        working.ipWhitelist = next;
                        paintPreambleSummary();
                    }, { mono: true, placeholder: '203.0.113.0/24', validate: looksLikeAddress }),
                    t('schema.waf.ipWhitelist.desc', '只有白名单内的 IP 能通过，留空表示不限制')),
                field(t('schema.waf.ipBlacklist', 'IP 黑名单'),
                    chipList(working.ipBlacklist, function (next) {
                        working.ipBlacklist = next;
                        paintPreambleSummary();
                    }, { mono: true, placeholder: '198.51.100.7', validate: looksLikeAddress }),
                    t('schema.waf.ipBlacklist.desc', '黑名单内的 IP 直接拒绝')),
                inlineSwitch(working.enableRequestSizeValidation !== false, function (checked) {
                    working.enableRequestSizeValidation = checked;
                    paintPreambleSummary();
                }, t('schema.waf.enableRequestSizeValidation', '请求大小校验')),
                inlineRow(sizeFields, 'wafcfg-row--grid')
            ],
            false
        );
        host.appendChild(sectionPreamble);

        function paintPreambleSummary() {
            var parts = [];
            if (working.enableIpCheck !== false) {
                parts.push(t('wafcfg.preamble.ip', 'IP 白名单 {w} / 黑名单 {b}', {
                    w: (working.ipWhitelist || []).length,
                    b: (working.ipBlacklist || []).length
                }));
            } else {
                parts.push(t('wafcfg.preamble.ipOff', 'IP 检查已关闭'));
            }
            if (working.enableRequestSizeValidation !== false) {
                parts.push(t('wafcfg.preamble.size', '请求体上限 {size}', {
                    size: bytesLabel(working.maxRequestBodySize || 0)
                }));
            }
            sectionPreamble.__setSummary(parts.join(' · '));
        }
        paintPreambleSummary();

        /* ── error bar ── */
        var errorBar = node('div', 'wafcfg-errors d-none');
        host.insertBefore(errorBar, host.firstChild);

        function paintErrorBar() {
            clear(errorBar);
            if (!errors.length) {
                errorBar.classList.add('d-none');
                return;
            }
            errorBar.classList.remove('d-none');
            var head = node('div', 'wafcfg-errors__head');
            head.innerHTML = '<i class="bi bi-exclamation-triangle-fill"></i>' +
                esc(t('wafcfg.errors.title', '有 {n} 处需要修正', { n: errors.length }));
            errorBar.appendChild(head);
            var list = node('ul', 'wafcfg-errors__list');
            errors.forEach(function (message) { list.appendChild(node('li', null, message)); });
            errorBar.appendChild(list);
        }

        /* Validation mirrors the compiler, but refuses instead of dropping.
           A condition the compiler would discard must never reach the server:
           dropping it silently widens the rule it belongs to. */
        function collect() {
            var found = [];
            var seenIds = {};

            rules().forEach(function (rule, index) {
                /* A disabled rule cannot fire, so an incomplete one is not an
                   error - it is a draft. Blocking the save on it would trap the
                   user into deleting work they meant to keep. */
                if (rule.enabled === false) return;

                var label = rule.id || (t('wafcfg.rule.n', '第 {n} 条规则', { n: index + 1 }));
                var conditions = Array.isArray(rule.conditions) ? rule.conditions : [];

                if (!rule.builtin && !conditions.length && rule.action !== 'rate-limit') {
                    found.push(t('wafcfg.err.emptyRule', '{name}：没有检测器也没有条件，保存后会被忽略', { name: label }));
                }
                if (rule.action === 'tag' && !rule.tag) {
                    found.push(t('wafcfg.err.tagMissing', '{name}：动作是打标签但没填标签', { name: label }));
                }
                if ((rule.action === 'block' || rule.action === 'rate-limit') && rule.statusCode != null &&
                    (rule.statusCode < 400 || rule.statusCode > 599)) {
                    found.push(t('wafcfg.err.statusRange', '{name}：状态码要在 400-599 之间', { name: label }));
                }
                if (rule.action === 'rate-limit') {
                    /* The compiler drops a throttle with an unreadable quota, so the card would
                       survive the save and do nothing. Refusing here is the only place the user
                       can see the problem. */
                    var quota = rule.rateLimit || {};
                    if (!(quota.limit > 0)) {
                        found.push(t('wafcfg.err.quotaLimit', '{name}：限流的次数要大于 0', { name: label }));
                    }
                    if (!looksLikeDuration(quota.window)) {
                        found.push(t('wafcfg.err.quotaWindow', '{name}：窗口长度要写成 30s、1m、1h 这样的形式', { name: label }));
                    }
                    if (PARTITIONS.indexOf(quota.partition) < 0) {
                        found.push(t('wafcfg.err.quotaPartition', '{name}：限流的统计维度无效', { name: label }));
                    }
                }
                if (rule.id) {
                    var key = rule.id.toLowerCase();
                    if (seenIds[key]) {
                        found.push(t('wafcfg.err.dupId', '规则标识 {id} 重复，日志里分不清是哪条', { id: rule.id }));
                    }
                    seenIds[key] = true;
                }

                conditions.forEach(function (condition, condIndex) {
                    var where = t('wafcfg.err.condN', '{name} 第 {n} 个条件', { name: label, n: condIndex + 1 });

                    if (!condition.value) {
                        found.push(where + '：' + t('wafcfg.err.valueMissing', '没有填匹配值'));
                        return;
                    }
                    if ((condition.source === 'header' || condition.source === 'cookie') && !condition.name) {
                        found.push(where + '：' + t('wafcfg.err.nameMissing', '需要指定 Header 或 Cookie 名'));
                    }
                    if ((condition.source === 'bodySize' ||
                         condition.op === 'greaterThan' || condition.op === 'lessThan') &&
                        !isFinite(Number(condition.value))) {
                        found.push(where + '：' + t('wafcfg.err.number', '需要数字'));
                    }
                    if (condition.op === 'cidr' && !looksLikeAddress(condition.value)) {
                        found.push(where + '：' + t('wafcfg.err.cidr', '不是合法的 IP 或网段'));
                    }
                    if (condition.op === 'regex') {
                        try { new RegExp(condition.value); }
                        catch (_) { found.push(where + '：' + t('wafcfg.err.regex', '正则表达式写法有误')); }
                    }
                });
            });

            if (response.statusCode != null && (response.statusCode < 400 || response.statusCode > 599)) {
                found.push(t('wafcfg.err.globalStatus', '拦截响应的状态码要在 400-599 之间'));
            }
            if (bodyInsp.enabled !== false && !(bodyInsp.maxBytes > 0)) {
                found.push(t('wafcfg.err.bodyMax', '请求体扫描开着但上限为 0，所有请求体都会被跳过'));
            }

            return found;
        }

        /* Strip defaults and empty shells so the saved JSON says only what the
           form actually says. Every removal here matches the C# default. */
        function normalise() {
            rules().forEach(function (rule) {
                if (rule.matchMode === 'all') delete rule.matchMode;
                if (rule.conditions) {
                    rule.conditions = rule.conditions.filter(function (condition) { return !!condition.value || condition.name; });
                    if (!rule.conditions.length) delete rule.conditions;
                }
                if (rule.statusCode == null) delete rule.statusCode;
                if (!rule.tag) delete rule.tag;
                if (!rule.id) delete rule.id;
                if (!rule.name) delete rule.name;
                if (!rule.builtin) delete rule.builtin;
                if (rule.enabled !== false) delete rule.enabled;
                if ((rule.action || 'block') === 'block') delete rule.action;

                /* A quota left behind by a previous action would be sent to the server, which
                   ignores it — but the next reader of the JSON would not know that. */
                if (rule.action === 'rate-limit') {
                    var quota = rule.rateLimit || {};
                    if (quota.limit == null) delete quota.limit;
                    if (!quota.window) delete quota.window;
                    if (!quota.partition) delete quota.partition;
                } else {
                    delete rule.rateLimit;
                }
            });

            if (response.body == null || !String(response.body).trim()) delete response.body;
            if (!Object.keys(response.headers || {}).length) delete response.headers;
            if (response.statusCode === 403) delete response.statusCode;
            if (response.contentType === 'application/json; charset=utf-8') delete response.contentType;
            if (response.includeRuleHeader !== false) delete response.includeRuleHeader;

            if (!(working.ipWhitelist || []).length) delete working.ipWhitelist;
            if (!(working.ipBlacklist || []).length) delete working.ipBlacklist;

            /* An empty list is not "no rules configured", it is the absence of the
               field: a legacy binding that never had rules must not come back with
               `rules: []`, which reads as "the operator cleared them" in an audit. */
            if (Array.isArray(working.rules) && !working.rules.length) delete working.rules;
        }

        host.__readConfig = function () {
            errors = collect();
            paintErrorBar();
            if (errors.length) {
                if (errorBar.scrollIntoView) errorBar.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
                return null;
            }

            normalise();
            /* mode survives normalisation on purpose: 'enforce' is a decision the
               user made, not an absent field, and dropping it would silently turn
               a monitor binding back into a blocking one. */
            var result = clone(working);
            result.mode = working.mode === 'monitor' ? 'monitor' : 'enforce';
            return result;
        };

        return true;
    }

    editors['waf'] = mountWaf;

    /* ── summary text ────────────────────────────────────────────────────
       A binding table has room for one line, and "rules: [4]" answers nothing.
       What decides whether a WAF binding does what you think it does is: is it
       actually blocking, and is it driven by rules or by the three switches. */
    function summarizeFor(pluginId, config) {
        if (pluginId !== 'waf' || !config) return '';

        var parts = [String(config.mode || '').toLowerCase() === 'monitor'
            ? t('wafcfg.summary.monitor', '仅记录')
            : t('wafcfg.summary.enforce', '拦截')];

        var rules = Array.isArray(config.rules)
            ? config.rules.filter(function (rule) { return rule && rule.enabled !== false; })
            : [];

        if (rules.length) {
            parts.push(t('wafcfg.summary.rules', '{n} 条规则', { n: rules.length }));
        } else {
            var builtin = BUILTIN_SWITCHES.filter(function (entry) { return config[entry.key] !== false; }).length;
            parts.push(builtin
                ? t('wafcfg.summary.builtin', '内置 {n} 项', { n: builtin })
                : t('wafcfg.summary.bare', '未配置检测'));
        }

        var listed = (config.ipWhitelist || []).length + (config.ipBlacklist || []).length;
        if (listed) parts.push(t('wafcfg.summary.ip', '{n} 个 IP 名单项', { n: listed }));

        return parts.join(' · ');
    }
})();
