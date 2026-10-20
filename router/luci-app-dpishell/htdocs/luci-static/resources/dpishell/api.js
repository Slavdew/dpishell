'use strict';
'require baseclass';
'require rpc';
'require ui';

// Общее для страниц DpiShell: вызовы ubus-объекта dpishell (docs/dev/router-api.md §4–5),
// тексты ошибок, мелкие элементы вёрстки, общие диалоги (подбор, защищённый DNS).

var UBUS_TEXT = {
	2: 'Роутер не принял запрос',
	4: 'Пакет DpiShell не установлен или устарел',
	5: 'Роутер не ответил',
	6: 'Нет доступа',
	7: 'Роутер не ответил вовремя'
};

var PARAMS = {
	status: [], strategies: [], select_progress: [], targets_get: [], diag: [], report: [],
	dns_status: [], site_check_result: [],
	lists_get: ['name'],
	enable: ['confirm_s'], confirm: [], disable: [], check_now: [], select_cancel: [], dns_analyze: [], dns_enable: [],
	dns_restore: [], core_install: [],
	mode_set: ['mode'],
	site_check: ['url'],
	strategy_set: ['id', 'confirm_s'],
	select_start: ['groups', 'mode', 'depth'],
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
	advanced_set: ['enabled', 'others', 'rows', 'confirm_s']
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

	// Вызов метода. Резолвится объектом ответа; отклоняется Error с готовым текстом (.message),
	// кодом (.code: busy|not_installed|invalid|conflict|core_mismatch|failed|ubus|network) и полем (.field).
	call: function(method, args) {
		var fn = declare(method), names = PARAMS[method] || [];
		var vals = names.map(function(n) { return args ? args[n] : undefined; });
		return fn.apply(null, vals).then(function(ret) {
			if (typeof ret == 'number') {
				if (ret === 0)
					return {};
				throw makeError(UBUS_TEXT[ret] || ('Ошибка роутера (код %d)'.format(ret)), 'ubus');
			}
			if (ret == null)
				return {};
			if (ret.error) {
				var e = ret.error;
				throw makeError(e.text || e.code || 'Ошибка', e.code, e.field);
			}
			return ret;
		}, function(err) {
			throw makeError('Нет связи с роутером', 'network');
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
		var i = E('span', { 'class': 'dps-info', 'title': 'Пояснение', 'click': function(ev) {
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
			return sec + ' с';
		return Math.floor(sec / 60) + ' мин ' + (sec % 60) + ' с';
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
	selectDialog: function(status, onStarted) {
		var self = this;
		var groups = {
			youtube: E('input', { type: 'checkbox', checked: '' }),
			discord: E('input', { type: 'checkbox', checked: '' }),
			user: E('input', { type: 'checkbox', checked: '' })
		};
		var modeFull = E('input', { type: 'radio', name: 'dps-sel-mode', value: 'full', checked: '' });
		var modeQuick = E('input', { type: 'radio', name: 'dps-sel-mode', value: 'quick' });
		var depthMain = E('input', { type: 'radio', name: 'dps-sel-depth', value: 'main', checked: '' });
		var depthDeep = E('input', { type: 'radio', name: 'dps-sel-depth', value: 'deep' });
		var errBox = E('div', { 'class': 'dps-error' });

		var warn = (status && status.enabled)
			? E('div', { 'class': 'alert-message warning' },
				'На время подбора оптимизация для всей сети переключается между стратегиями, сайты на устройствах могут открываться с перебоями.')
			: E('div', { 'class': 'alert-message notice' },
				'Подбор обычно занимает несколько минут (глубокий поиск — от 5 до 30 минут). Сайты в сети в это время могут открываться с перебоями.');

		ui.showModal('Подбор стратегии', [
			warn,
			E('p', {}, 'Что проверять'),
			E('label', { 'class': 'dps-check' }, [groups.youtube, 'YouTube']),
			E('label', { 'class': 'dps-check' }, [groups.discord, 'Discord']),
			E('label', { 'class': 'dps-check' }, [groups.user, 'Свои адреса']),
			E('p', {}, ['Режим', self.info('Полный проверяет все стратегии и показывает таблицу — так видно самую надёжную. Быстрый останавливается на первой, у которой всё открылось. Закончившийся подбор сам включает лучшую.')]),
			E('label', { 'class': 'dps-radio' }, [modeFull, 'Полный — все стратегии, обычно несколько минут']),
			E('label', { 'class': 'dps-radio' }, [modeQuick, 'Быстрый — до первой подходящей']),
			E('p', {}, ['Глубина', self.info('Основные — готовые стратегии. Глубокий поиск дополнительно придумывает новые, когда готовые не помогли; это долго — около 10 минут.')]),
			E('label', { 'class': 'dps-radio' }, [depthMain, 'Основные стратегии']),
			E('label', { 'class': 'dps-radio' }, [depthDeep, 'Глубокий поиск — от 5 до 30 минут']),
			errBox,
			E('div', { 'class': 'right' }, [
				E('button', { 'class': 'btn', 'click': ui.hideModal }, 'Отмена'), ' ',
				E('button', { 'class': 'btn cbi-button cbi-button-positive important', 'click': ui.createHandlerFn(self, function() {
					var g = [];
					for (var k in groups)
						if (groups[k].checked)
							g.push(k);
					if (!g.length) {
						errBox.textContent = 'Выберите хотя бы одну группу';
						return;
					}
					return self.call('select_start', {
						groups: g,
						mode: modeQuick.checked ? 'quick' : 'full',
						depth: depthDeep.checked ? 'deep' : 'main'
					}).then(function() {
						ui.hideModal();
						if (onStarted)
							onStarted();
					}, function(e) {
						errBox.textContent = self.text(e);
					});
				}) }, 'Начать подбор')
			])
		]);
	},

	// Последствия защищённого DNS — показываются до нажатия (architecture.md «DNS для всей сети»).
	dnsConsequences: function() {
		return E('ul', { 'class': 'dps-list' }, [
			E('li', {}, 'Запросы DNS уйдут к Google и Cloudflare в зашифрованном виде: провайдер не сможет их подменять, но может заблокировать сам защищённый DNS — тогда роутер вернёт DNS провайдера.'),
			E('li', {}, 'Перестанут действовать фильтры провайдера (например, родительский контроль у провайдера).'),
			E('li', {}, 'Сайты могут открываться чуть медленнее.'),
			E('li', {}, 'Если оба защищённых сервера станут недоступны, роутер временно вернёт DNS провайдера и предупредит.'),
			E('li', {}, 'Устройства с жёстко прописанным DNS (часть ТВ и приставок) идут мимо роутера — на них это не повлияет.'),
			E('li', {}, 'Имена устройств домашней сети обычно продолжают работать.')
		]);
	},

	dnsEnableDialog: function(onDone) {
		var self = this;
		var errBox = E('div', { 'class': 'dps-error' });
		ui.showModal('Включить защищённый DNS', [
			E('p', {}, 'Для всей домашней сети. Что изменится:'),
			self.dnsConsequences(),
			E('p', {}, 'Прежние настройки DNS сохраняются — «Вернуть как было» вернёт их. Включение обычно занимает около минуты: роутер ставит пакет и проверяет, что DNS отвечает.'),
			errBox,
			E('div', { 'class': 'right' }, [
				E('button', { 'class': 'btn', 'click': ui.hideModal }, 'Отмена'), ' ',
				E('button', { 'class': 'btn cbi-button cbi-button-positive important', 'click': ui.createHandlerFn(self, function() {
					return self.call('dns_enable').then(function() {
						ui.hideModal();
						if (onDone)
							onDone();
					}, function(e) {
						errBox.textContent = self.text(e);
					});
				}) }, 'Включить')
			])
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
