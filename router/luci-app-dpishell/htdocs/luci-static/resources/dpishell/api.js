'use strict';
'require baseclass';
'require rpc';
'require ui';
'require uci';

// Общее для страниц DpiShell: вызовы ubus-объекта dpishell (docs/dev/router-api.md §4–5),
// тексты ошибок, язык страниц, мелкие элементы вёрстки, общие диалоги (подбор, защищённый DNS), «Сайт не открывается».

// Язык (docs/dev/architecture.md «Язык страниц»): файлы dpishell/lang/<код>.json в формате окна ПК — плоские
// «ключ → текст», {имя} — подстановки, формы чисел объектом one/few/many/other, _meta — о файле, ключи с «_» — не тексты.
// Свой перевод — положить <код>.json рядом: пакет его не перезапишет. Встроены ru и en; en — запасной для любого ключа.
var LANG = {}, BASE = {}, LANG_CODE = 'en', LANG_LOADED = null, LANG_PROBLEMS = [];
var BASE_CODE = 'en', BUILTIN = ['ru', 'en'];
// Как окно ПК по языку Windows: эти языки — русский, если своего файла под них нет.
var TO_RU = ['ru', 'uk', 'be', 'kk'];

function fetchLang(code) {
	return fetch(L.resource('dpishell/lang/' + code + '.json'), { cache: 'no-cache' }).then(function(r) {
		if (!r.ok)
			throw { missing: true };
		return r.text();
	}).then(function(text) {
		try { return JSON.parse(text); }
		catch (e) { throw { problem: code + '.json: ' + e.message }; }
	});
}

function placeholders(v) {
	var text = typeof v == 'object' && v ? Object.keys(v).map(function(k) { return v[k]; }).join(' ') : String(v);
	return (text.match(/\{\w+\}/g) || []).filter(function(x, i, a) { return a.indexOf(x) == i; }).sort().join(',');
}

// Свой перевод: строка с другим набором подстановок, чем в en, не берётся (в отчёт), «_»-ключи — не тексты.
function cleanLang(code, d) {
	if (!d || typeof d != 'object' || !d._meta || !d._meta.language || !d._meta.code)
		throw { problem: code + '.json: _meta (language, code)' };
	if (BUILTIN.indexOf(code) >= 0)
		return d;
	var out = { _meta: d._meta }, bad = [];
	Object.keys(d).forEach(function(k) {
		if (k.charAt(0) == '_')
			return;
		if (BASE[k] != null && placeholders(BASE[k]) != placeholders(d[k]))
			return bad.push(k);
		out[k] = d[k];
	});
	if (bad.length)
		LANG_PROBLEMS.push(code + '.json: ' + bad.join(', '));
	return out;
}

// Порядок выбора: явный язык LuCI (luci.main.lang) или, при «auto», языки браузера. Для каждого — свой файл
// <код>.json, затем ru для ru/uk/be/kk; в конце en. (LuCI при «auto» берёт браузерный язык, только если стоит
// luci-i18n-base-<язык>, — без него страница была бы английской и у русского браузера.)
function langCandidates() {
	return uci.load('luci').then(function() {
		return uci.get('luci', 'main', 'lang') || 'auto';
	}, function() { return 'auto'; }).then(function(set) {
		var list = [];
		var push = function(c) { if (c && list.indexOf(c) < 0) list.push(c); };
		var add = function(c) {
			if (!c)
				return;
			c = String(c).toLowerCase().replace('_', '-');
			var base = c.split('-')[0];
			push(c);
			push(base);
			if (TO_RU.indexOf(base) >= 0)
				push('ru');
		};
		if (set != 'auto')
			add(set);
		else
			(navigator.languages || [navigator.language]).forEach(add);
		push(BASE_CODE);
		return list;
	});
}

// Вкладки DpiShell: заголовки в menu.d — по-английски (LuCI переводит их своим каталогом, нашего там нет), здесь — на языке
// страниц. Меню тема рисует не сразу — несколько попыток.
var TAB_KEYS = { main: 'common.nav.home', select: 'common.nav.select', lists: 'common.nav.sites', dns: 'common.nav.dns',
	diag: 'common.nav.diag', about: 'common.nav.about' };

function translateTabs() {
	var api = { t: function(k) { return LANG[k] != null ? LANG[k] : BASE[k]; } };
	var run = function() {
		document.querySelectorAll('a[href*="/admin/services/dpishell/"]').forEach(function(a) {
			var m = /\/admin\/services\/dpishell\/(\w+)/.exec(a.getAttribute('href') || '');
			var text = m && TAB_KEYS[m[1]] && api.t(TAB_KEYS[m[1]]);
			if (text && a.textContent != text)
				a.textContent = text;
		});
	};
	run();
	[100, 500, 1500].forEach(function(ms) { window.setTimeout(run, ms); });
}

var PARAMS = {
	status: [], strategies: [], select_progress: [], targets_get: [], diag: [], report: [],
	dns_status: [], site_check_result: [],
	lists_get: ['name'],
	enable: ['confirm_s'], confirm: [], disable: [], check_now: [], select_cancel: [], dns_analyze: [], dns_enable: [],
	dns_restore: [], core_install: [],
	mode_set: ['mode'],
	site_check: ['url'],
	strategy_set: ['id', 'confirm_s'],
	select_start: ['groups', 'mode', 'depth', 'ids'],
	apply: ['id', 'backups', 'confirm_s'],
	schedule_set: ['enabled', 'hours', 'auto_select', 'watchdog'],
	dns_set: ['preset', 'custom'], dns_presets: [],
	targets_set: ['items'],
	lists_set: ['name', 'lines'],
	strategies_user_set: ['items'],
	conflict_stop: ['name'],
	flow_offload_fix: [],
	uninstall: ['keep_settings'],
	update_install: ['from'],
	update_check: [],
	ipv6_set: ['enabled'],
	advanced_get: [],
	advanced_set: ['enabled', 'others', 'rows', 'confirm_s'],
	advanced_revert: [],
	dns_compare: []
};

var declared = {};

function declare(method) {
	if (!declared[method])
		declared[method] = rpc.declare({ object: 'dpishell', method: method, params: PARAMS[method] || [] });
	return declared[method];
}

function makeError(text, code, field) {
	var e = new Error(text);
	e.code = code || 'failed';
	if (field)
		e.field = field;
	return e;
}

function injectStyle() {
	if (document.getElementById('dpishell-css'))
		return;
	document.head.appendChild(E('style', { id: 'dpishell-css' }, [
		'.dps-actions{display:flex;flex-wrap:wrap;gap:8px;margin:8px 0}',
		'.dps-actions .btn,.dps-actions button{margin:0}',
		'.dps-state{font-size:1.15em;margin:6px 0 10px}',
		'.dps-muted{color:var(--text-color-medium,#777)}',
		'.dps-info{display:inline-block;border:1px solid currentColor;border-radius:50%;width:1.2em;height:1.2em;line-height:1.1em;text-align:center;font-size:.85em;margin-left:.4em;cursor:pointer;user-select:none}',
		'.dps-info-text{display:none;font-size:.92em;margin:.4em 0 .6em;padding:.5em .8em;border-left:3px solid var(--primary-color-high,#36c);background:var(--background-color-low,#f6f6f6)}',
		'.dps-info-text.open{display:block}',
		'.dps-line{margin:.5em 0}',
		'.dps-error{color:var(--error-color-high,#c00);margin:.5em 0;white-space:pre-wrap}',
		'.dps-ok{color:var(--success-color-high,#080)}',
		'.dps-ok::before{content:"✓ "}',
		'.dps-warn{display:flex;flex-wrap:wrap;gap:8px;align-items:center;justify-content:space-between}',
		'.dps-warn .btn{margin:0}',
		'.dps-textarea{width:100%;max-width:100%;min-height:8em;font-family:monospace;box-sizing:border-box}',
		'.dps-target{display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin:4px 0}',
		'.dps-target input[type=text]{flex:1 1 200px;min-width:0}',
		// Строки «сайты → стратегия»: на телефоне сайты — во всю ширину, стратегия и «×» — одной строкой под ними.
		'.dps-adv-row{padding-bottom:6px;border-bottom:1px solid var(--border-color-low,rgba(128,128,128,.3))}',
		'@media (max-width:600px){.dps-adv-row input[type=text]{flex:1 1 100%}.dps-adv-row select{flex:1 1 0;width:auto!important;min-width:0}.dps-adv-row .cbi-button-remove{flex:0 0 auto;width:auto!important}}',
		'.dps-target.dps-bad input[type=text]{border-color:var(--error-color-high,#c00);outline:2px solid var(--error-color-high,#c00)}',
		'.dps-table{width:100%}',
		'.dps-table .td,.dps-table .th{word-break:break-word}',
		'.dps-radio{display:block;margin:.3em 0}',
		'.dps-radio input{margin-right:.5em}',
		'.dps-check{display:block;margin:.3em 0}',
		'.dps-check input{margin-right:.5em}',
		'.dps-list{margin:.3em 0 .3em 1.2em;padding:0}',
		// ux-audit-web: панель состояния, итог проверки значками, отступы, вкладки на телефоне, контраст тёмной темы.
		'.dps-hero{display:flex;gap:14px;align-items:center;flex-wrap:wrap;padding:14px 16px;border-radius:8px;border:1px solid var(--border-color-medium,#ccc);margin-bottom:10px}',
		'.dps-hero-text{flex:1 1 14em;min-width:0}',
		'.dps-hero-title{font-size:1.4em;font-weight:600;line-height:1.25}',
		'.dps-hero-sub{opacity:.8;margin-top:2px}',
		'.dps-dot{width:14px;height:14px;border-radius:50%;flex:none;background:#888}',
		'.dps-dot-ok{background:var(--success-color-high,#2a2)}.dps-dot-err{background:var(--error-color-high,#c33)}.dps-dot-busy{background:var(--warn-color-high,#d90)}',
		'.btn.dps-big{padding:.6em 1.8em;font-size:1.1em;margin:0}',
		// Главная кнопка — залитая: в тёмной теме LuCI cbi-button-positive только контур (ux-audit-web круг 2).
		'.btn.dps-big.cbi-button-positive{background:var(--success-color-high,#2a7);border-color:transparent;color:#fff}',
		'@media (max-width:600px){.btn.dps-big{flex:1 1 100%}}',
		'.dps-verdicts{display:flex;flex-wrap:wrap;gap:14px;font-weight:600}',
		'.dps-v-ok{color:var(--success-color-high,#080)}.dps-v-bad{color:var(--error-color-high,#c00)}',
		'.cbi-section{margin:0 0 20px}',
		'.cbi-section > h3{font-size:1.15em;margin:0 0 .6em}',
		'.dps-radio input[type=radio],.dps-check input[type=checkbox]{margin:0 .6em 0 0 !important;vertical-align:middle}',
		'.dps-check{display:block;margin:.4em 0}',
		'.dps-info-text{font-weight:400;text-align:left;white-space:normal}',
		'ul.tabs{display:flex;flex-wrap:nowrap;overflow-x:auto;white-space:nowrap;scrollbar-width:none}ul.tabs::-webkit-scrollbar{display:none}ul.tabs>li{flex:none}',
		'@media (prefers-color-scheme:dark){.dps-muted{color:inherit;opacity:.75}}',
		'[data-darkmode="true"] .dps-muted{color:inherit;opacity:.75}',
		// «Что мешает»: строка проверки — отметка, вывод, кнопка справа; сверху итог.
		'.dps-check-row{display:flex;gap:10px;align-items:center;flex-wrap:wrap;padding:6px 0;border-bottom:1px solid var(--border-color-low,rgba(128,128,128,.25))}',
		'.dps-check-row .btn{margin:0 0 0 auto}',
		'.dps-cr-mark{width:1.2em;text-align:center;font-weight:700;flex:none}',
		'.dps-cr-text{flex:1 1 16em;min-width:0}',
		'.dps-cr-ok .dps-cr-mark{color:var(--success-color-high,#080)}.dps-cr-bad .dps-cr-mark{color:var(--error-color-high,#c00)}',
		'.dps-cr-sum{font-size:1.1em;font-weight:600;margin:.3em 0 .5em}',
		'.dps-cr-sum-ok{color:var(--success-color-high,#080)}.dps-cr-sum-bad{color:var(--error-color-high,#c00)}',
		'.dps-xfer-row{margin:.2em 0}.dps-xfer-row details{margin-left:1.9em}',
		'.dps-danger{border:1px solid var(--error-color-high,#c33);border-radius:8px;padding:12px 16px}',
		// Индикатор опроса LuCI («Обновление» вверху справа): наши страницы опрашивают роутер постоянно, он горит всегда,
		// и его принимали за незаконченное обновление DpiShell. Ход заданий страницы показывают сами.
		'span[data-indicator="poll-status"]{display:none}',
		'.dps-preset{padding:.4em 0;border-bottom:1px solid var(--border-color-low,#e5e5e5)}',
		'.dps-preset .dps-radio{display:inline-block;font-weight:600;margin-right:.5em}',
		'.dps-preset .btn{padding:0 .6em;font-size:.9em}',
		'.dps-preset-info{margin-left:1.7em;font-size:.95em;overflow-wrap:anywhere}',
		'.dps-pre{white-space:pre-wrap;word-break:break-word;max-height:16em;overflow:auto;font-family:monospace;font-size:.9em}',
		'@media (max-width:480px){.dps-actions .btn,.dps-actions button{flex:1 1 auto}}',
		// Шесть вкладок на телефоне в строку не входят — переносом во вторую строку, без прокрутки вбок.
		'@media (max-width:700px){#tabmenu .tabs{display:flex;flex-wrap:wrap;overflow:visible;white-space:normal;height:auto}}'
	].join('\n')));
}

return baseclass.extend({
	__init__: function() {
		injectStyle();
	},

	// Загрузить язык страниц; вызывать в load() каждой страницы до первого t(). Один раз на страницу.
	lang: function() {
		if (LANG_LOADED)
			return LANG_LOADED;
		LANG_LOADED = fetchLang(BASE_CODE).then(function(d) { BASE = d; }, function() {}).then(langCandidates).then(function(list) {
			var next = function(i) {
				if (i >= list.length)
					return;
				if (list[i] == BASE_CODE) {
					LANG = BASE;
					LANG_CODE = BASE_CODE;
					return;
				}
				return fetchLang(list[i]).then(function(d) {
					LANG = cleanLang(list[i], d);
					LANG_CODE = list[i];
				}, function(e) {
					if (e && e.problem)
						LANG_PROBLEMS.push(e.problem);
					return next(i + 1);
				});
			};
			return next(0);
		}).then(function() {
			LANG_PROBLEMS.forEach(function(p) { console.warn('DpiShell, язык: ' + p); });
			translateTabs();
		});
		return LANG_LOADED;
	},

	// Свои переводы, которые не взяты целиком или частично, — для строки на «Информация и обновления».
	langProblems: function() {
		return LANG_PROBLEMS.slice();
	},

	has: function(key) {
		return LANG[key] != null || BASE[key] != null;
	},

	// Название стратегии на языке страницы: автоимена роутера (select.uc) — по разбору; имена каталога (general (ALT3)) и
	// свои — как есть (замечания владельца 2026-10-20, п. 3).
	stratTitle: function(t) {
		if (t == null)
			return '';
		t = String(t);
		var m = /^Новая стратегия (\d+) \(поиск ([^)]+)\)$/.exec(t);
		if (m)
			return this.t('web.strat.new', { n: m[1], when: m[2] });
		m = /^Найденная (\d+) \(([^)]+)\)$/.exec(t);
		if (m)
			return this.t('web.strat.found', { n: m[1], when: m[2] });
		if (/^Смешанная · /.test(t)) {
			var self = this;
			return t.replace(/^Смешанная/, this.t('web.strat.mixed'))
				.replace(/свои сайты/g, this.t('web.strat.own_sites')).replace(/(^|[ ·])свои(?= )/g, function(x, p) { return p + self.t('web.strat.own'); })
				.replace(/остальное/g, this.t('web.strat.rest')).replace(/ и /g, ' ' + this.t('web.strat.and') + ' ');
		}
		return t;
	},

	langCode: function() {
		return LANG_CODE;
	},

	// Сообщение роутера { key, params, text } (router-api.md §9, 2026-10-20): по ключу, если он есть в словаре, иначе text.
	// params сырые: time и *_at — unix-секунды, вложенная фраза — { key, params }.
	msg: function(m) {
		var self = this;
		if (m == null)
			return '';
		if (typeof m != 'object')
			return String(m);
		if (m.key && (LANG[m.key] != null || BASE[m.key] != null)) {
			var p = {};
			Object.keys(m.params || {}).forEach(function(k) {
				var v = m.params[k];
				if (v && typeof v == 'object' && v.key)
					p[k] = self.msg(v);
				else if ((k == 'time' || /_at$/.test(k)) && typeof v == 'number')
					p[k] = self.time(v);
				else
					p[k] = v;
			});
			return this.t(m.key, p);
		}
		return m.text || m.message || m.code || '';
	},

	// Текст по ключу: t('router.home.checked', { time: '12:00' }); формы чисел — по params.n.
	t: function(key, params) {
		var v = LANG[key];
		if (v == null)
			v = BASE[key];
		if (v == null)
			return key;
		if (typeof v == 'object') {
			var n = params ? params.n : 0, form = 'other';
			try { form = new Intl.PluralRules(LANG_CODE).select(n); } catch (e) {}
			v = v[form] != null ? v[form] : v.other;
		}
		if (params)
			v = v.replace(/\{(\w+)\}/g, function(m, k) { return params[k] != null ? String(params[k]) : m; });
		return v;
	},

	// Вызов метода. Резолвится объектом ответа; отклоняется Error с готовым текстом (.message),
	// кодом (.code: busy|not_installed|invalid|conflict|core_mismatch|failed|ubus|network) и полем (.field).
	call: function(method, args) {
		var self = this, fn = declare(method), names = PARAMS[method] || [];
		var vals = names.map(function(n) { return args ? args[n] : undefined; });
		return fn.apply(null, vals).then(function(ret) {
			if (typeof ret == 'number') {
				if (ret === 0)
					return {};
				var known = [2, 4, 5, 6, 7].indexOf(ret) >= 0;
				throw makeError(known ? self.t('web.err.ubus_' + ret) : self.t('web.err.ubus', { code: ret }), 'ubus');
			}
			if (ret == null)
				return {};
			if (ret.error) {
				var e = ret.error;
				throw makeError(self.msg(e) || self.t('web.err.generic'), e.code, e.field);
			}
			return ret;
		}, function(err) {
			throw makeError(self.t('web.err.network'), 'network');
		});
	},

	// Вызов, который не должен ронять страницу: в случае ошибки — { __error: Error }.
	safe: function(method, args) {
		return this.call(method, args).catch(function(e) { return { __error: e }; });
	},

	text: function(e) {
		if (!e)
			return '';
		return e.message || String(e);
	},

	// Элементы вёрстки
	card: function(title, children) {
		var kids = [];
		if (title)
			kids.push(E('h3', {}, title));
		return E('div', { 'class': 'cbi-section' }, kids.concat(children || []));
	},

	btn: function(label, handler, cls, ctx) {
		return E('button', {
			'class': 'btn cbi-button ' + (cls || 'cbi-button-neutral'),
			'click': ui.createHandlerFn(ctx || {}, function(ev) { return handler(ev); })
		}, label);
	},

	line: function(children, cls) {
		return E('div', { 'class': 'dps-line ' + (cls || '') }, children);
	},

	errorLine: function(e) {
		return E('div', { 'class': 'dps-error' }, this.text(e));
	},

	// Кнопка «(i)»: раскрывает пояснение под строкой. Один элемент — можно класть в любой список детей E().
	info: function(text) {
		var box = E('div', { 'class': 'dps-info-text' }, text);
		var i = E('span', { 'class': 'dps-info', 'title': this.t('common.info'), 'click': function(ev) {
			box.classList.toggle('open');
			ev.preventDefault();
		} }, 'i');
		return E('span', { 'class': 'dps-info-wrap' }, [i, box]);
	},

	// Строка «подпись — элемент» в стиле cbi.
	row: function(title, field, descr) {
		return E('div', { 'class': 'cbi-value' }, [
			E('label', { 'class': 'cbi-value-title' }, title),
			E('div', { 'class': 'cbi-value-field' }, [field, descr ? E('div', { 'class': 'cbi-value-description' }, descr) : ''])
		]);
	},

	radio: function(name, value, label, checked, onchange) {
		var input = E('input', { type: 'radio', name: name, value: value, checked: checked ? '' : null, change: onchange });
		return E('label', { 'class': 'dps-radio' }, [input, label]);
	},

	check: function(label, checked, onchange) {
		var input = E('input', { type: 'checkbox', checked: checked ? '' : null, change: onchange });
		return E('label', { 'class': 'dps-check' }, [input, label]);
	},

	// Время: сегодня — ЧЧ:ММ, иначе ДД.ММ ЧЧ:ММ. Принимает ISO 8601 (договор) и секунды эпохи (бэкенд).
	time: function(iso) {
		if (!iso)
			return '';
		var d = (typeof iso == 'number' || /^\d+$/.test(iso)) ? new Date(iso * 1000) : new Date(iso);
		if (isNaN(d.getTime()))
			return iso;
		var hm = '%02d:%02d'.format(d.getHours(), d.getMinutes());
		var now = new Date();
		if (d.toDateString() == now.toDateString())
			return hm;
		return '%02d.%02d %s'.format(d.getDate(), d.getMonth() + 1, hm);
	},

	duration: function(sec) {
		if (sec == null || isNaN(sec))
			return '';
		sec = Math.round(sec);
		if (sec < 60)
			return this.t('web.sec', { n: sec });
		return this.t('web.min_sec', { m: Math.floor(sec / 60), s: sec % 60 });
	},

	percent: function(share) {
		if (share == null || isNaN(share))
			return '—';
		return Math.round(share * 100) + ' %';
	},

	url: function(tab) {
		return L.url('admin/services/dpishell' + (tab ? '/' + tab : ''));
	},

	go: function(tab) {
		window.location.href = this.url(tab);
	},

	// Диалог запуска подбора (architecture.md «Подбор на роутере»). onStarted() — после успешного select_start.
	// Глубины здесь нет: «Искать глубже» — кнопкой после результата (матрица ПК×роутер, п. 25).
	selectDialog: function(status, onStarted) {
		var self = this;
		var groups = {
			youtube: E('input', { type: 'checkbox', checked: '' }),
			discord: E('input', { type: 'checkbox', checked: '' }),
			user: E('input', { type: 'checkbox', checked: '' })
		};
		var modeFull = E('input', { type: 'radio', name: 'dps-sel-mode', value: 'full', checked: '' });
		var modeQuick = E('input', { type: 'radio', name: 'dps-sel-mode', value: 'quick' });
		var errBox = E('div', { 'class': 'dps-error' });

		var warn = (status && status.enabled)
			? E('div', { 'class': 'alert-message warning' }, self.t('web.select.warn_on'))
			: E('div', { 'class': 'alert-message notice' }, self.t('web.select.warn_off'));

		ui.showModal(self.t('select.title'), [
			warn,
			E('p', {}, self.t('select.groups.title')),
			E('label', { 'class': 'dps-check' }, [groups.youtube, 'YouTube']),
			E('label', { 'class': 'dps-check' }, [groups.discord, 'Discord']),
			E('label', { 'class': 'dps-check' }, [groups.user, self.t('select.group.user')]),
			E('p', {}, [self.t('select.mode.title'), self.info(self.t('web.select.mode_info'))]),
			E('label', { 'class': 'dps-radio' }, [modeFull, self.t('select.mode.full') + self.t('select.mode.full_hint')]),
			E('label', { 'class': 'dps-radio' }, [modeQuick, self.t('select.mode.quick') + self.t('select.mode.quick_hint')]),
			errBox,
			E('div', { 'class': 'right' }, [
				E('button', { 'class': 'btn', 'click': ui.hideModal }, self.t('common.cancel')), ' ',
				E('button', { 'class': 'btn cbi-button cbi-button-positive important', 'click': ui.createHandlerFn(self, function() {
					var g = [];
					for (var k in groups)
						if (groups[k].checked)
							g.push(k);
					if (!g.length) {
						errBox.textContent = self.t('web.select.no_groups');
						return;
					}
					return self.call('select_start', {
						groups: g,
						mode: modeQuick.checked ? 'quick' : 'full'
					}).then(function() {
						ui.hideModal();
						if (onStarted)
							onStarted();
					}, function(e) {
						errBox.textContent = self.text(e);
					});
				}) }, self.t('select.start'))
			])
		]);
	},

	// Последствия защищённого DNS — показываются до нажатия (architecture.md «DNS для всей сети»).
	dnsConsequences: function() {
		var self = this;
		return E('ul', { 'class': 'dps-list' }, [1, 2, 3, 4, 5, 6].map(function(i) {
			return E('li', {}, self.t('web.dns.cons' + i));
		}));
	},

	dnsEnableDialog: function(onDone) {
		var self = this;
		var errBox = E('div', { 'class': 'dps-error' });
		ui.showModal(self.t('router.dns.enable'), [
			E('p', {}, self.t('web.dns.enable_what')),
			self.dnsConsequences(),
			E('p', {}, self.t('web.dns.enable_note')),
			errBox,
			E('div', { 'class': 'right' }, [
				E('button', { 'class': 'btn', 'click': ui.hideModal }, self.t('common.cancel')), ' ',
				E('button', { 'class': 'btn cbi-button cbi-button-positive important', 'click': ui.createHandlerFn(self, function() {
					return self.call('dns_enable').then(function() {
						ui.hideModal();
						if (onDone)
							onDone();
					}, function(e) {
						errBox.textContent = self.text(e);
					});
				}) }, self.t('router.enable'))
			])
		]);
	},

	// Копирование и по http: navigator.clipboard есть только в защищённом контексте, а LuCI обычно открыт по http.
	copy: function(text) {
		if (navigator.clipboard && window.isSecureContext)
			return navigator.clipboard.writeText(text);
		var ta = E('textarea', { style: 'position:fixed;left:-1000px;top:0' }, text);
		document.body.appendChild(ta);
		ta.select();
		try { document.execCommand('copy'); } catch (e) {}
		document.body.removeChild(ta);
	},

	// Кнопка «Скопировать …»: на 1,5 с — «Скопировано».
	copyBtn: function(label, getText) {
		var self = this;
		return this.btn(label, function(ev) {
			self.copy(getText());
			ev.target.textContent = self.t('web.copied');
			window.setTimeout(function() { ev.target.textContent = label; }, 1500);
		});
	},

	// Карточка «Сайт не открывается» (site_check): на «Сайтах» и копией на главной (матрица п. 11). Опрос итога — свой.
	// where — 'sites' | 'main'; onExcluded(host) — после добавления в исключения.
	siteCheckCard: function(where, onExcluded) {
		var self = this;
		var result = E('div', { 'class': 'dps-line' });
		var url = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'example.com', style: 'width:100%;max-width:28em',
			keydown: function(ev) { if (ev.key == 'Enter') check(); } });
		var timer = null;

		function hostOf(u) {
			return u.replace(/^https?:\/\//i, '').replace(/[:/].*$/, '');
		}

		function sideText(x) {
			if (!x)
				return self.t('router.site.side_none');
			if (x.ok)
				return self.t('router.site.side_ok');
			var d = self.msg(x.detail_msg || x.detail);
			return d ? self.t('router.site.side_fail_detail', { detail: d }) : self.t('router.site.side_fail');
		}

		function verdict(title, hint, cls, extra) {
			return E('div', {}, [E('div', { 'class': cls }, title), E('div', { 'class': 'dps-muted' }, hint)].concat(extra || []));
		}

		function show(kids) {
			while (result.firstChild)
				result.removeChild(result.firstChild);
			[].concat(kids).forEach(function(k) { result.appendChild(k); });
		}

		function render(r) {
			var kids = [E('div', {}, [E('b', {}, r.url || '')])];
			if (r.error) {
				kids.push(E('div', { 'class': 'dps-error' }, self.msg(r.error_msg || r.error)));
				show(kids);
				return;
			}
			kids.push(E('div', { 'class': 'dps-muted' }, self.t('router.site.detail', { 'with': sideText(r.with_bypass), without: sideText(r.without_bypass) })));
			var host = hostOf(r.url || '');
			if (r.suggest_exclude)
				kids.push(verdict(self.t('router.site.only_without'), self.t('router.site.only_without_hint'), 'dps-error', [
					E('div', { 'class': 'dps-actions' }, [self.btn(self.t('sites.check.add_exclude'), function() { return addExclude(host); }, 'cbi-button-apply')])
				]));
			else if (r.with_bypass && !r.with_bypass.ok)
				kids.push(verdict(self.t('router.site.closed'), self.t('router.site.closed_hint'), 'dps-error'));
			else
				kids.push(verdict(self.t('router.site.opens'), self.t('router.site.opens_hint'), 'dps-ok'));
			show(kids);
		}

		function addExclude(host) {
			return self.call('lists_get', { name: 'user-exclude' }).then(function(l) {
				var lines = l.lines || [];
				if (lines.indexOf(host) < 0)
					lines.push(host);
				return self.call('lists_set', { name: 'user-exclude', lines: lines });
			}).then(function() {
				show(verdict(self.t('router.site.excluded', { host: host }),
					self.t(where == 'sites' ? 'router.site.excluded_hint' : 'web.site.excluded_hint_main'), 'dps-ok'));
				if (onExcluded)
					onExcluded(host);
			}, function(e) {
				result.appendChild(self.errorLine(e));
			});
		}

		function check() {
			var u = (url.value || '').trim();
			if (!u) {
				show(E('div', {}, self.t('router.site.enter')));
				return Promise.resolve();
			}
			if (!/^https?:\/\//i.test(u))
				u = 'https://' + u;
			show(E('span', { 'class': 'spinning' }, self.t('router.site.checking', { host: hostOf(u) })));
			if (timer)
				window.clearInterval(timer);
			return self.call('site_check', { url: u }).then(function() {
				var ticks = 90;
				timer = window.setInterval(function() {
					self.safe('site_check_result').then(function(r) {
						ticks--;
						if (r.__error) {
							window.clearInterval(timer);
							show(self.errorLine(r.__error));
						} else if (!r.running) {
							window.clearInterval(timer);
							render(r);
						} else if (ticks <= 0) {
							window.clearInterval(timer);
							show(E('div', {}, self.t('router.site.pending')));
						}
					});
				}, 1000);
			}, function(e) {
				show(self.errorLine(e));
			});
		}

		return this.card(this.t('sites.check.title'), [
			this.line(this.t('web.site.hint'), 'dps-muted'),
			E('div', { 'class': 'dps-actions' }, [url, this.btn(this.t('sites.check.button'), check, 'cbi-button-action')]),
			result
		]);
	},

	// Скачать текст файлом в браузере.
	download: function(name, text) {
		var blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
		var a = E('a', { href: URL.createObjectURL(blob), download: name, style: 'display:none' });
		document.body.appendChild(a);
		a.click();
		setTimeout(function() { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
	}
});
