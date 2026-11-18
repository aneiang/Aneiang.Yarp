/**
 * Form Builder - Dynamically generates forms from JSON Schema
 */
(function() {
    'use strict';

    window.DashboardFormBuilder = {
        /**
         * Build form from schema
         * @param {string} containerId - Container element ID
         * @param {object} schema - JSON Schema
         * @param {object} data - Initial data
         */
        build: function(containerId, schema, data) {
            var container = document.getElementById(containerId);
            if (!container) {
                console.warn('[FormBuilder] Container not found:', containerId);
                return;
            }

            container.innerHTML = '';
            this._buildFields(container, schema, data || {}, '', 0);
        },

        /**
         * Build top-level fields grouped into cards:
         * flat scalar fields -> one "Basic Info" card;
         * object/array fields -> one card each.
         */
        _buildFields: function(container, schema, data, path, depth) {
            if (!schema || !schema.properties) return;
            var self = this;
            var basicNames = [];
            var groupNames = [];
            Object.keys(schema.properties).forEach(function(propName) {
                var ps = schema.properties[propName];
                /* Classify through the normalizer: the schema wraps object/array in
                   `type: ['object','null']` or anyOf, which a raw `ps.type` test misses. */
                var t = self._normalize(ps).type;
                var isGroup = t === 'object' || t === 'array';
                (isGroup ? groupNames : basicNames).push(propName);
            });

            if (basicNames.length > 0) {
                var basicCard = self._createCard(self._t('editor.basicInfo', 'Basic Info'));
                basicNames.forEach(function(propName) {
                    var ps = schema.properties[propName];
                    var currentPath = path ? path + '.' + propName : propName;
                    var field = self._createField(propName, ps, data[propName], currentPath, depth);
                    if (field) basicCard.body.appendChild(field);
                });
                container.appendChild(basicCard.el);
            }

            groupNames.forEach(function(propName) {
                var ps = schema.properties[propName];
                var currentPath = path ? path + '.' + propName : propName;
                var value = data[propName];
                var card = self._createCard(self._fieldTitle(propName, ps));
                var input = self._createInput(propName, ps, value, currentPath, depth);
                if (input) card.body.appendChild(input);
                container.appendChild(card.el);
            });
        },

        /**
         * Flat rendering of an object's properties (used inside a card or nested object).
         */
        _buildFieldsFlat: function(container, schema, data, path, depth) {
            if (!schema || !schema.properties) return;
            var self = this;
            Object.keys(schema.properties).forEach(function(propName) {
                var ps = schema.properties[propName];
                var currentPath = path ? path + '.' + propName : propName;
                var field = self._createField(propName, ps, data[propName], currentPath, depth);
                if (field) container.appendChild(field);
            });
        },

        /**
         * Create a card container with title bar.
         */
        _createCard: function(title) {
            var el = document.createElement('div');
            el.className = 'fb-card';
            var titleEl = document.createElement('div');
            titleEl.className = 'fb-card-title';
            titleEl.innerHTML = '<i class="bi bi-three-dots-vertical"></i><span>' + (title || '') + '</span>';
            el.appendChild(titleEl);
            var body = document.createElement('div');
            body.className = 'fb-card-body';
            el.appendChild(body);
            return { el: el, body: body };
        },

        /**
         * Derive a human-friendly title for a field.
         */
        _fieldTitle: function(name, schema) {
            if (schema && schema.title) return schema.title;
            return name.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ');
        },

        /**
         * i18n helper with fallback.
         */
        _t: function(key, fallback) {
            if (window.__ && typeof window.__ === 'function') {
                var v = window.__(key);
                return (v && v !== key) ? v : fallback;
            }
            return fallback;
        },

        /**
         * Create a single form field (label + input).
         */
        _createField: function(name, schema, value, path, depth) {
            var wrapper = document.createElement('div');
            wrapper.className = 'fb-field';

            var label = document.createElement('label');
            label.className = 'fb-label';
            label.textContent = name;

            if (schema.description) {
                label.title = schema.description;
            }

            var input = this._createInput(name, schema, value, path, depth);
            if (!input) return null;

            wrapper.appendChild(label);
            wrapper.appendChild(input);

            return wrapper;
        },

        /**
         * Resolve a schema property into one effective descriptor.
         *
         * ConfigurationSchema.json describes almost every optional field in the
         * "anyOf / type array" style. A nullable bool looks like:
         *   { anyOf: [ { type: ['boolean','null'] }, { type: 'string', enum: ['true','false'] } ] }
         * Reading only `schema.type` misses the real type, so the field degraded into a
         * free-text box, submitted "" for a boolean, and the gateway rejected the whole
         * request with "Cannot convert string '' to boolean".
         *
         * @returns {{type: string|null, enum: Array|null, nullable: boolean}}
         */
        _normalize: function(schema) {
            var info = { type: null, enum: null, nullable: false };
            if (!schema || typeof schema !== 'object') return info;

            var branches = [];
            (function collect(node) {
                if (!node || typeof node !== 'object') return;
                /* Keep the node itself: some fields declare `type` and `anyOf` on the same
                   level (Match is `type: object` plus validation-only anyOf branches). */
                branches.push(node);
                if (Array.isArray(node.anyOf)) node.anyOf.forEach(collect);
                if (Array.isArray(node.oneOf)) node.oneOf.forEach(collect);
            })(schema);

            var types = [];
            branches.forEach(function(node) {
                var t = node.type;
                if (Array.isArray(t)) {
                    t.forEach(function(one) {
                        if (one === 'null') info.nullable = true;
                        else if (types.indexOf(one) < 0) types.push(one);
                    });
                } else if (typeof t === 'string') {
                    if (types.indexOf(t) < 0) types.push(t);
                }
            });

            /* "true"/"false" written as a string is a parsing convenience, not the field's
               type, so a concrete type always wins over 'string'. */
            var concrete = types.filter(function(t) { return t !== 'string'; });
            info.type = concrete.length ? concrete[0] : (types[0] || null);

            /* Some properties carry no `type` at all but are obviously a container. */
            if (!info.type && schema.items) info.type = 'array';
            if (!info.type && schema.properties) info.type = 'object';

            if (info.type === 'string') {
                for (var i = 0; i < branches.length; i++) {
                    if (Array.isArray(branches[i].enum) && branches[i].enum.length) {
                        info.enum = branches[i].enum.slice();
                        break;
                    }
                }
            }
            return info;
        },

        /**
         * Find the branch that actually carries a sub-schema (`properties`, `items`, ...).
         * With anyOf the sub-schema may sit one level down instead of on the root node.
         */
        _branchWith: function(schema, keys) {
            var found = null;
            (function collect(node) {
                if (!node || typeof node !== 'object' || found) return;
                if (Array.isArray(node.anyOf)) { node.anyOf.forEach(collect); return; }
                if (Array.isArray(node.oneOf)) { node.oneOf.forEach(collect); return; }
                for (var i = 0; i < keys.length; i++) {
                    if (node[keys[i]]) { found = node; return; }
                }
            })(schema);
            return found || schema || {};
        },

        /** Policy-reference fields, and which policy family each one resolves against. */
        POLICY_FIELD_KINDS: {
            AuthorizationPolicy: 'authorization',
            CorsPolicy: 'cors',
            RateLimiterPolicy: 'rateLimiter',
            OutputCachePolicy: 'outputCache'
        },

        _policyKindFor: function(fieldName) {
            return this.POLICY_FIELD_KINDS[fieldName] || null;
        },

        /**
         * Policy names used to be free-text boxes. Typing a name that was never registered
         * saved fine and then killed the route at request time, with no hint about the cause.
         * They are now a dropdown of what actually exists, plus an escape hatch for
         * host-registered policies the dashboard cannot enumerate.
         */
        _createPolicySelect: function(path, kind, value) {
            var self = this;
            var select = document.createElement('select');
            select.className = 'form-select';
            select.setAttribute('data-path', path);
            select.setAttribute('data-kind', 'policy');
            select.setAttribute('data-policy-kind', kind);
            /* Blank means "do not apply this policy", so it must not be written as "". */
            select.setAttribute('data-omit-empty', 'true');

            var loading = document.createElement('option');
            loading.value = value == null ? '' : String(value);
            loading.textContent = this._t('editor.policyLoading', '加载可用策略…');
            loading.selected = true;
            select.appendChild(loading);

            this._fillPolicySelect(select, kind, value);

            select.addEventListener('change', function() {
                if (select.value !== '__custom__') return;
                var entered = window.prompt(
                    self._t('editor.policyPrompt', '输入已在宿主代码中注册的策略名'), '');
                if (!entered) {
                    select.value = value == null ? '' : String(value);
                    return;
                }
                var option = document.createElement('option');
                option.value = entered;
                option.textContent = entered + self._t('editor.policyUnregistered', '（未注册，保存时会被拒绝）');
                option.selected = true;
                select.appendChild(option);
                value = entered;
            });

            return select;
        },

        /**
         * One catalog fetch per page: three forms could be open in sequence and the answer
         * never changes within a session.
         */
        _loadPolicyCatalog: function() {
            if (!this._policyCatalogPromise) {
                var self = this;
                this._policyCatalogPromise = window.DashboardApi.get('/api/config/policies')
                    .then(function(data) { return data || {}; })
                    .catch(function() {
                        self._policyCatalogPromise = null;
                        return {};
                    });
            }
            return this._policyCatalogPromise;
        },

        _fillPolicySelect: function(select, kind, value) {
            var self = this;
            var current = value == null ? '' : String(value);

            this._loadPolicyCatalog().then(function(catalog) {
                var list = catalog[kind] || [];
                select.innerHTML = '';

                var unset = document.createElement('option');
                unset.value = '';
                unset.textContent = self._t('editor.policyUnset', '未设置（不启用）');
                if (current === '') unset.selected = true;
                select.appendChild(unset);

                var matched = false;
                list.forEach(function(policy) {
                    var option = document.createElement('option');
                    option.value = policy.name;
                    option.textContent = policy.name;
                    if (policy.description) option.title = policy.description;
                    if (policy.name === current) { option.selected = true; matched = true; }
                    select.appendChild(option);
                });

                /* A value that is not in the catalog is either a host-registered policy or a
                   typo. Keep it and say so — silently swapping it for the first option would
                   rewrite config the user never touched. */
                if (current !== '' && !matched) {
                    var extra = document.createElement('option');
                    extra.value = current;
                    extra.textContent = current + self._t('editor.policyUnregistered', '（未注册，保存时会被拒绝）');
                    extra.selected = true;
                    select.appendChild(extra);
                }

                var custom = document.createElement('option');
                custom.value = '__custom__';
                custom.textContent = self._t('editor.policyCustom', '自定义名称…');
                select.appendChild(custom);
            });
        },

        /**
         * Build a select; the empty option represents "not set" and is omitted on save.
         */
        _createSelect: function(path, options, value, kind) {            var select = document.createElement('select');
            select.className = 'form-select';
            select.setAttribute('data-path', path);
            if (kind) select.setAttribute('data-kind', kind);

            var current = value == null ? '' : String(value);
            var matched = false;
            options.forEach(function(opt) {
                var el = document.createElement('option');
                el.value = opt.value;
                el.textContent = opt.label;
                if (opt.value === current) { el.selected = true; matched = true; }
                select.appendChild(el);
            });

            /* A stored value outside the enum (custom policy, newer option) must not be
               silently replaced by the first option. */
            if (!matched && current !== '') {
                var extra = document.createElement('option');
                extra.value = current;
                extra.textContent = current;
                extra.selected = true;
                select.appendChild(extra);
            }
            return select;
        },

        /**
         * Create input element based on schema type
         */
        _createInput: function(name, schema, value, path, depth) {
            var policyKind = this._policyKindFor(name);
            if (policyKind) return this._createPolicySelect(path, policyKind, value);

            var info = this._normalize(schema);
            var input;

            if (info.type === 'boolean') {
                if (!info.nullable) {
                    input = document.createElement('input');
                    input.type = 'checkbox';
                    input.className = 'form-check-input';
                    input.setAttribute('data-path', path);
                    input.setAttribute('data-kind', 'boolean');
                    if (value === true || value === 'true') input.checked = true;
                    return input;
                }
                /* A nullable bool has three meaningful states. A checkbox only has two,
                   and forcing "not set" to false writes a value the user never chose. */
                return this._createSelect(path, [
                    { value: '', label: this._t('editor.fieldUnset', '未设置（沿用默认）') },
                    { value: 'true', label: 'true' },
                    { value: 'false', label: 'false' }
                ], value, 'tristate');
            }

            if (info.enum) {
                var options = info.enum.map(function(v) { return { value: v, label: v }; });
                /* Without an empty option an unset nullable enum silently snaps to the
                   first value and the save changes config the user never touched. */
                if (info.nullable) options.unshift({ value: '', label: this._t('editor.fieldUnset', '未设置（沿用默认）') });
                return this._createSelect(path, options, value, 'enum');
            }

            if (info.type === 'number' || info.type === 'integer') {
                input = document.createElement('input');
                input.type = 'number';
                input.className = 'form-control';
                input.setAttribute('data-path', path);
                input.setAttribute('data-kind', 'number');
                if (value != null) input.value = value;
                return input;
            }

            if (info.type === 'object') {
                var shape = this._branchWith(schema, ['properties', 'patternProperties', 'additionalProperties']);
                var objContainer = document.createElement('div');
                objContainer.className = 'fb-object';
                if (shape.properties) {
                    this._buildFieldsFlat(objContainer, shape, value || {}, path, (depth || 0) + 1);
                    return objContainer;
                }
                if (shape.patternProperties) {
                    return this._createPatternObjectInput(name, shape, value, path);
                }
                if (shape.additionalProperties) {
                    return this._createMapInput(name, shape, value, path);
                }
                return objContainer;
            }

            if (info.type === 'array') {
                return this._createArrayInput(name, this._branchWith(schema, ['items']), value, path);
            }

            // String -> text input
            input = document.createElement('input');
            input.type = 'text';
            input.className = 'form-control';
            input.setAttribute('data-path', path);
            input.setAttribute('data-kind', 'string');
            if (info.nullable) input.setAttribute('data-omit-empty', 'true');
            if (value != null) input.value = value;
            return input;
        },

        /**
         * Key/value editor for free-form maps (`additionalProperties`), e.g. cluster
         * Metadata. These used to render as an empty card, so every save in form mode
         * silently dropped the existing entries.
         */
        _createMapInput: function(name, schema, value, path) {
            var self = this;
            var wrapper = document.createElement('div');
            wrapper.className = 'fb-map-object';

            var list = document.createElement('div');
            list.className = 'd-flex flex-column gap-2';
            wrapper.appendChild(list);

            function renderRow(entryKey, entryVal) {
                var row = document.createElement('div');
                row.className = 'd-flex gap-2 align-items-center';

                var keyInput = document.createElement('input');
                keyInput.type = 'text';
                keyInput.className = 'form-control';
                keyInput.placeholder = self._t('editor.map.key', 'Key');
                keyInput.setAttribute('data-map-key', path);
                keyInput.value = entryKey || '';

                var valInput = document.createElement('input');
                valInput.type = 'text';
                valInput.className = 'form-control';
                valInput.placeholder = self._t('editor.map.value', 'Value');
                valInput.setAttribute('data-map-value', path);
                valInput.value = entryVal == null ? '' : String(entryVal);

                var delBtn = document.createElement('button');
                delBtn.type = 'button';
                delBtn.className = 'btn btn-sm btn-outline-danger flex-shrink-0';
                delBtn.innerHTML = '<i class="bi bi-dash-lg"></i>';
                delBtn.onclick = function() { row.remove(); };

                row.appendChild(keyInput);
                row.appendChild(valInput);
                row.appendChild(delBtn);
                list.appendChild(row);
            }

            var dataObj = (value && typeof value === 'object' && !Array.isArray(value)) ? value : {};
            Object.keys(dataObj).forEach(function(entryKey) { renderRow(entryKey, dataObj[entryKey]); });

            var addBtn = document.createElement('button');
            addBtn.type = 'button';
            addBtn.className = 'btn btn-sm btn-outline-success mt-1';
            addBtn.innerHTML = '<i class="bi bi-plus-lg me-1"></i>' + this._t('editor.map.add', 'Add entry');
            addBtn.onclick = function() { renderRow('', ''); };
            wrapper.appendChild(addBtn);

            return wrapper;
        },

        /**
         * Create pattern-properties object input (e.g. Destinations).
         * Renders each existing key as an entry with the value schema's fields,
         * plus an add button. Entry names are fixed (rename via delete + add).
         */
        _createPatternObjectInput: function(name, schema, value, path) {
            var self = this;
            var wrapper = document.createElement('div');
            wrapper.className = 'fb-pattern-object';
            var list = document.createElement('div');
            list.className = 'd-flex flex-column gap-2';
            wrapper.appendChild(list);

            var valueSchema = null;
            if (schema.patternProperties) {
                for (var pk in schema.patternProperties) { valueSchema = schema.patternProperties[pk]; break; }
            }

            function t(key, fb) { return self._t(key, fb); }
            function escapeHtml(s) {
                return (window.DashboardUtils && window.DashboardUtils.escapeHtml) ? window.DashboardUtils.escapeHtml(s) : String(s);
            }

            function renderEntry(entryKey, entryVal) {
                var entry = document.createElement('div');
                entry.className = 'fb-pattern-entry';
                var head = document.createElement('div');
                head.className = 'fb-pattern-entry-head';
                var nameSpan = document.createElement('span');
                nameSpan.className = 'fb-pattern-entry-name';
                nameSpan.innerHTML = '<i class="bi bi-server me-1"></i>' + escapeHtml(entryKey);
                head.appendChild(nameSpan);
                var delBtn = document.createElement('button');
                delBtn.type = 'button';
                delBtn.className = 'btn btn-sm btn-outline-danger';
                delBtn.innerHTML = '<i class="bi bi-trash"></i>';
                delBtn.title = t('editor.array.removeItem', 'Remove');
                delBtn.onclick = function() { entry.remove(); };
                head.appendChild(delBtn);
                entry.appendChild(head);
                if (valueSchema && valueSchema.properties) {
                    self._buildFieldsFlat(entry, valueSchema, entryVal || {}, path + '.' + entryKey, 1);
                }
                list.appendChild(entry);
            }

            var dataObj = (value && typeof value === 'object' && !Array.isArray(value)) ? value : {};
            Object.keys(dataObj).forEach(function(entryKey) {
                renderEntry(entryKey, dataObj[entryKey]);
            });

            var addBtn = document.createElement('button');
            addBtn.type = 'button';
            addBtn.className = 'btn btn-sm btn-outline-success mt-2';
            addBtn.innerHTML = '<i class="bi bi-plus-lg me-1"></i>' + t('editor.pattern.add', 'Add Item');
            addBtn.onclick = function() {
                var entryKey = window.prompt(t('editor.pattern.promptName', 'Name:'), 'destination' + (list.children.length + 1));
                if (entryKey && entryKey.trim()) {
                    renderEntry(entryKey.trim(), {});
                }
            };
            wrapper.appendChild(addBtn);

            return wrapper;
        },

        /**
         * Create array input with add/remove items.
         * Primitive items (string/number) render as text inputs;
         * object/anyOf items (e.g. Transforms) render as per-item JSON textareas.
         */
        _createArrayInput: function(name, schema, value, path) {
            var self = this;
            var items = schema.items || {};
            var itemTypes = Array.isArray(items.type) ? items.type : (items.type ? [items.type] : []);
            var isObjectItem = Array.isArray(items.anyOf)
                || itemTypes.indexOf('object') >= 0
                || (!items.type && !!items.properties);

            var wrapper = document.createElement('div');
            wrapper.className = 'array-field';

            var list = document.createElement('div');
            list.className = 'd-flex flex-column gap-2';
            wrapper.appendChild(list);

            var values = Array.isArray(value) ? value.slice() : [];

            function t(key, fallback) {
                if (window.__ && typeof window.__ === 'function') {
                    var v = window.__(key);
                    return (v && v !== key) ? v : fallback;
                }
                return fallback;
            }

            function collectCurrent() {
                var cur = [];
                var nodes = list.querySelectorAll('[data-path]');
                for (var i = 0; i < nodes.length; i++) {
                    var node = nodes[i];
                    var seg = node.getAttribute('data-path').split('.');
                    var idx = parseInt(seg[seg.length - 1], 10);
                    var v;
                    if (node.getAttribute('data-item-json') === 'true') {
                        try { v = JSON.parse(node.value || '{}'); } catch (e) { v = {}; }
                    } else {
                        v = node.value;
                    }
                    cur.push({ idx: idx, val: v });
                }
                cur.sort(function(a, b) { return a.idx - b.idx; });
                return cur.map(function(x) { return x.val; });
            }

            function renderItem(itemVal, idx) {
                var row = document.createElement('div');
                row.className = 'd-flex gap-2 align-items-start';

                var field;
                if (isObjectItem) {
                    field = document.createElement('textarea');
                    field.className = 'form-control form-control-sm font-monospace';
                    field.rows = 3;
                    field.setAttribute('data-path', path + '.' + idx);
                    field.setAttribute('data-item-json', 'true');
                    field.value = JSON.stringify(itemVal == null ? {} : itemVal, null, 2);
                } else {
                    field = document.createElement('input');
                    field.type = 'text';
                    field.className = 'form-control form-control-sm';
                    field.setAttribute('data-path', path + '.' + idx);
                    field.value = itemVal == null ? '' : String(itemVal);
                }
                row.appendChild(field);

                var removeBtn = document.createElement('button');
                removeBtn.type = 'button';
                removeBtn.className = 'btn btn-sm btn-outline-danger flex-shrink-0';
                removeBtn.innerHTML = '<i class="bi bi-dash-lg"></i>';
                removeBtn.title = t('editor.array.removeItem', 'Remove');
                removeBtn.onclick = function() {
                    values = collectCurrent();
                    values.splice(idx, 1);
                    renderAll();
                };
                row.appendChild(removeBtn);

                return row;
            }

            function renderAll() {
                list.innerHTML = '';
                values.forEach(function(itemVal, idx) {
                    list.appendChild(renderItem(itemVal, idx));
                });
            }

            renderAll();

            var addBtn = document.createElement('button');
            addBtn.type = 'button';
            addBtn.className = 'btn btn-sm btn-outline-success mt-1';
            addBtn.innerHTML = '<i class="bi bi-plus-lg me-1"></i>' + t('editor.array.addItem', 'Add Item');
            addBtn.onclick = function() {
                values = collectCurrent();
                values.push(isObjectItem ? {} : '');
                renderAll();
            };
            wrapper.appendChild(addBtn);

            return wrapper;
        },

        /**
         * Collect data from form.
         *
         * A field the user left blank means "not specified", not "empty string": the
         * gateway deserializes these into nullable YARP fields where "" is invalid for
         * booleans, numbers, enums and TimeSpan. Emitting nothing lets the server apply
         * its own default instead of rejecting the whole request.
         */
        collectData: function(containerId) {
            var container = document.getElementById(containerId);
            if (!container) return {};

            var self = this;
            var data = {};

            /* Free-form maps (additionalProperties) are two inputs per row, so they are
               gathered separately instead of through the single-input path below. */
            var maps = {};
            container.querySelectorAll('[data-map-key]').forEach(function(keyInput) {
                var mapPath = keyInput.getAttribute('data-map-key');
                var entryKey = (keyInput.value || '').trim();
                if (!entryKey) return;
                var row = keyInput.parentElement;
                var valInput = row ? row.querySelector('[data-map-value]') : null;
                if (!maps[mapPath]) maps[mapPath] = {};
                maps[mapPath][entryKey] = valInput ? valInput.value : '';
            });

            var inputs = container.querySelectorAll('[data-path]');

            inputs.forEach(function(input) {
                var path = input.getAttribute('data-path');
                var kind = input.getAttribute('data-kind');
                var value;

                if (input.getAttribute('data-item-json') === 'true') {
                    // Per-item JSON (array of objects, e.g. Transforms)
                    try { value = JSON.parse(input.value || '{}'); }
                    catch (e) { value = {}; }
                } else if (input.type === 'checkbox') {
                    value = input.checked;
                } else if (kind === 'number') {
                    if (input.value === '') return;            // blank -> unset, not 0
                    value = Number(input.value);
                    if (!isFinite(value)) return;
                } else if (kind === 'tristate') {
                    if (input.value === '') return;            // blank -> unset, not false
                    value = input.value === 'true';
                } else if (kind === 'enum' && input.value === '') {
                    return;                                    // the "not set" option
                } else if (input.getAttribute('data-omit-empty') === 'true' && input.value.trim() === '') {
                    return;
                } else {
                    value = input.value;
                }

                self._setNestedValue(data, path, value);
            });

            Object.keys(maps).forEach(function(mapPath) {
                self._setNestedValue(data, mapPath, maps[mapPath]);
            });

            return this._pruneEmptyObjects(data);
        },

        /**
         * Drop branches the form left completely empty (e.g. an untouched HealthCheck
         * card). Writing `{}` for them stores meaningless objects in the config.
         */
        _pruneEmptyObjects: function(value) {
            var self = this;
            if (Array.isArray(value)) return value.map(function(item) { return self._pruneEmptyObjects(item); });
            if (!value || typeof value !== 'object') return value;

            var result = {};
            Object.keys(value).forEach(function(key) {
                var child = self._pruneEmptyObjects(value[key]);
                if (child && typeof child === 'object' && !Array.isArray(child) && Object.keys(child).length === 0) return;
                result[key] = child;
            });
            return result;
        },

        /**
         * Set nested value in object by path (numeric segments become array indices)
         */
        _setNestedValue: function(obj, path, value) {
            var parts = path.split('.');
            var current = obj;

            for (var i = 0; i < parts.length - 1; i++) {
                var part = parts[i];
                var nextIsIndex = /^\d+$/.test(parts[i + 1]);
                if (current[part] === undefined || current[part] === null) {
                    current[part] = nextIsIndex ? [] : {};
                }
                current = current[part];
            }

            var last = parts[parts.length - 1];
            if (/^\d+$/.test(last)) {
                current[parseInt(last, 10)] = value;
            } else {
                current[last] = value;
            }
        },

        /**
         * Bind change events for real-time sync
         */
        bindSync: function(containerId, onFormDataChange) {
            var container = document.getElementById(containerId);
            if (!container) return;

            container.addEventListener('input', function() {
                var data = this.collectData(containerId);
                if (onFormDataChange) {
                    onFormDataChange(data);
                }
            }.bind(this));

            container.addEventListener('change', function() {
                var data = this.collectData(containerId);
                if (onFormDataChange) {
                    onFormDataChange(data);
                }
            }.bind(this));
        }
    };
})();
