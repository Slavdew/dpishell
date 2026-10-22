'use strict';
'require view';
'require ui';
'require poll';
'require dpishell.api as api';
'require dpishell.transfer as xfer';

// Строки — только текстом: свой E вместо LuCI (api.js, tools/xss-check.py).
var E = api.E;

// Вкладка «Диагностика» (architecture.md «Диагностика роутера»): чужие обходы, flow offloading, IPv6, DNS, ядро, отчёт.
// Названия — как в окне ПК («Что мешает» на вкладке «Роутер»), матрица ПК×роутер п. 46–51.

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		var self = this;
		return api.lang().then(function() {
			return Promise.all([api.safe('diag'), api.safe('status')]);
		}).then(function(d) {
			return Promise.all([self.loadSecurity(d[1]), self.loadChanges(d[1])]).then(function(x) { return d.concat(x); });
		});
	},

	// «Изменения DpiShell» (router-api §5.2 changes_list, с 0.2.0-r16): старый пакет без метода — карточки нет.
	loadChanges: function(status) {
		if (!status || status.__error || (status.methods || []).indexOf('changes_list') < 0)
			return Promise.resolve(null);
		return api.safe('changes_list');
	},

	// «Безопасность роутера» (router-api §5.1 security_check, с 0.2.0-r13): старый пакет без метода — блока нет.
	loadSecurity: function(status) {
		if (!status || status.__error || (status.methods || []).indexOf('security_check') < 0)
			return Promise.resolve(null);
		return api.safe('security_check');
	},

	render: function(data) {
		var self = this;
		this.diag = data[0];
		this.status = data[1].__error ? null : data[1];
		this.security = data[2];
		this.changes = data[3];
		this.coreTicks = 0;

		this.el = {
			findings: E('div'),
			security: E('div'),
			changes: E('div'),
			core: E('div'),
			err: E('div', { 'class': 'dps-error' })
		};

		var view = E('div', {}, [
			E('h2', {}, api.t('diag.title')),
			api.card(api.t('router.diag.title'), [
				api.line([api.t('web.diag.intro'), api.info(api.t('web.diag.intro_info'))], 'dps-muted'),
				this.el.findings,
				this.el.err
			]),
			this.security ? api.card(api.t('router.security.title'), [
				api.line([api.t('router.security.intro'), api.info(api.t('router.security.intro_info'))], 'dps-muted'),
				this.el.security
			]) : '',
			this.changes ? api.card(api.t('settings.changes.title'), [
				api.line(api.t('router.changes.intro'), 'dps-muted'),
				this.el.changes
			]) : '',
			api.card(api.t('web.core.title'), [this.el.core]),
			api.card(api.t('web.diag.report'), [
				api.line(api.t('web.diag.report_descr'), 'dps-muted'),
				E('div', { 'class': 'dps-actions' }, [api.btn(api.t('router.diag.save_report'), function() { return self.report(); }, 'cbi-button-action'),
					E('a', { href: 'https://github.com/Slavdew/dpishell/issues', target: '_blank', rel: 'noopener', style: 'align-self:center' }, api.t('diag.report.issue'))])
			]),
			// Выгрузка и загрузка своих настроек (docs/settings-export.md, общий формат с окном ПК; там — «Настройки»,
			// здесь — рядом с отчётом: страницы «Настройки» у роутера нет).
			api.card(api.t('web.xfer.title'), [
				api.line([api.t('web.xfer.descr'), api.info(api.t('web.xfer.info'))], 'dps-muted'),
				E('div', { 'class': 'dps-actions' }, [
					api.btn(api.t('web.xfer.export'), function() { return xfer.exportDialog().catch(function(e) { api.fail(self.el.err, e); }); }, 'cbi-button-action'),
					api.btn(api.t('web.xfer.import'), function() { xfer.importDialog(function() { self.reload(); }); }, 'cbi-button-neutral')
				])
			]),
			this.uninstallSection()
		]);

		this.renderFindings();
		this.renderSecurity();
		this.renderChanges();
		this.renderCore();
		poll.add(L.bind(this.tick, this), 1);
		return view;
	},

	// Список «Изменения DpiShell»: пункт — название, когда, «Отменить» или почему нельзя; ⚠ — может на секунды оборвать связь.
	// Тексты — общие с окном ПК (settings.changes.*, router.changes.*). Итог последней отмены — по пунктам, как на ПК.
	renderChanges: function() {
		var self = this, c = this.changes, kids = [];
		if (!c)
			return;
		if (c.__error) {
			api.content(this.el.changes, api.errorLine(c.__error));
			return;
		}
		var running = !!(c.job && c.job.running) || this.undoing;
		var items = c.items || [];
		if (!items.length)
			kids.push(api.line(api.t('settings.changes.empty'), 'dps-muted'));
		items.forEach(function(it) {
			var title = api.msg({ key: it.key, params: it.params, text: it.text });
			var when = it.at ? api.time(it.at) : '';
			var note = it.undo == 'yes' ? (it.risk == 'link' ? api.t('router.changes.row.link') : '')
				: api.msg({ key: it.note_key, params: {}, text: it.note || '' });
			var btn = '';
			if (it.undo == 'yes') {
				btn = api.btn(api.t('settings.changes.row.undo'), function() { return self.undoChange(it, title); }, 'cbi-button-neutral');
				btn.disabled = running;
			}
			kids.push(E('div', { 'class': 'dps-check-row dps-cr-unknown' }, [
				E('span', { 'class': 'dps-cr-mark' }, it.risk == 'link' && it.undo == 'yes' ? '⚠' : '•'),
				E('span', { 'class': 'dps-cr-text' }, [E('b', {}, title), when ? E('span', { 'class': 'dps-muted' }, ' · ' + when) : '',
					note ? E('div', { 'class': 'dps-muted' }, note) : '']),
				btn
			]));
		});
		if (running)
			kids.push(E('div', { 'class': 'spinning' }, api.t('router.changes.busy')));
		else if (items.some(function(it) { return it.undo == 'yes'; })) {
			var all = api.btn(api.t('settings.changes.undo_all'), function() { return self.undoChange(null); }, 'cbi-button-negative');
			kids.push(E('div', { 'class': 'dps-actions' }, [all]));
		}
		var j = c.job;
		if (j && !j.running && this.showReport && (j.items || []).length + (j.error ? 1 : 0)) {
			var n = { restored: 0, same: 0, skipped: 0, failed: 0 };
			var rep = (j.items || []).map(function(r) {
				n[r.status] = (n[r.status] || 0) + 1;
				var st = { restored: 'settings.changes.status_restored', same: 'settings.changes.status_same', skipped: 'settings.changes.status_skipped' }[r.status] || 'settings.changes.status_failed';
				var why = r.note ? ' — ' + api.msg({ key: r.note_key, params: {}, text: r.note }) : '';
				return E('li', {}, api.msg({ key: r.key, params: r.params, text: r.text }) + ': ' + api.t(st) + why);
			});
			if (j.error)
				rep.push(E('li', {}, api.t('web.changes.failed', { message: j.error })));
			kids.push(E('div', { 'class': 'alert-message ' + (n.failed || j.error ? 'warning' : 'notice') }, [
				E('div', {}, rep.length ? api.t('settings.changes.summary', n) : api.t('settings.changes.summary_none')),
				E('ul', { 'class': 'dps-list' }, rep)
			]));
		}
		api.content(this.el.changes, kids);
	},

	// Отмена пункта (it) или всего (null): вопрос (с предупреждением о связи, где она может прерваться), задание в фоне, опрос итога.
	undoChange: function(it, title) {
		var self = this, items = (this.changes && this.changes.items) || [];
		var risky = it ? it.risk == 'link' : items.some(function(x) { return x.undo == 'yes' && x.risk == 'link'; });
		var q = (it ? api.t('router.changes.confirm_one', { title: title }) : api.t('router.changes.confirm_all')) +
			(risky ? '\n\n' + api.t('router.changes.link_warn') : '');
		if (!window.confirm(q))
			return Promise.resolve();
		this.el.err.textContent = '';
		this.undoing = true;
		this.showReport = true;
		this.renderChanges();
		var started = Date.now();
		return api.call(it ? 'changes_undo' : 'changes_undo_all', it ? { id: it.id } : {}).then(function() {
			return new Promise(function(resolve) {
				// Отмена DNS перезапускает dnsmasq, ускорения сети — межсетевой экран: ответ может на секунды пропасть — ждём дальше.
				var timer = window.setInterval(function() {
					api.safe('changes_list').then(function(c) {
						if (c.__error && Date.now() - started < 120000)
							return;
						if (!c.__error && c.job && c.job.running && Date.now() - started < 300000)
							return;
						window.clearInterval(timer);
						self.undoing = false;
						self.changes = c.__error ? self.changes : c;
						if (c.__error)
							api.fail(self.el.err, c.__error);
						self.renderChanges();
						self.reload();
						resolve();
					});
				}, 1500);
			});
		}, function(e) {
			self.undoing = false;
			api.fail(self.el.err, e);
			return self.reload();
		});
	},

	tick: function() {
		var self = this;
		// Отмену начали не отсюда (окно ПК, другая вкладка) — следить за ней, пока идёт.
		if (this.changes && this.changes.job && this.changes.job.running && !this.undoing && !document.hidden)
			return api.safe('changes_list').then(function(c) {
				if (c.__error)
					return;
				self.changes = c;
				self.showReport = !c.job || !c.job.running;
				self.renderChanges();
			});
		if (this.coreTicks <= 0)
			return Promise.resolve();
		this.coreTicks--;
		return api.safe('status').then(function(s) {
			if (s.__error)
				return;
			self.status = s;
			if (!s.jobs.core_install)
				self.coreTicks = 0;
			self.renderCore();
		});
	},

	reload: function() {
		var self = this;
		return Promise.all([api.safe('diag'), api.safe('status')]).then(function(d) {
			self.diag = d[0];
			if (!d[1].__error)
				self.status = d[1];
			self.renderFindings();
			self.renderCore();
			return Promise.all([
				self.security ? self.loadSecurity(self.status).then(function(sec) {
					self.security = sec || self.security;
					self.renderSecurity();
				}) : null,
				// Пока идёт отмена, список ведёт её опрос (undoChange).
				self.changes && !self.undoing ? self.loadChanges(self.status).then(function(c) {
					self.changes = c || self.changes;
					self.renderChanges();
				}) : null
			]);
		});
	},

	renderFindings: function() {
		var self = this, d = this.diag, kids = [];
		if (d.__error) {
			api.content(this.el.findings, api.errorLine(d.__error));
			return;
		}
		// Каждая проверка — строкой с оценкой ✓/⚠ и выводом словами («выключено — не мешает»), сверху итог (замечания
		// владельца 2026-10-20, п. 9: «выключено» без вывода читалось как проблема). Так же — «Что мешает» в окне ПК.
		var bad = [];
		function row(state, label, text, action, info) {
			if (state == 'bad')
				bad.push(label);
			return E('div', { 'class': 'dps-check-row dps-cr-' + state }, [
				E('span', { 'class': 'dps-cr-mark' }, state == 'ok' ? '✓' : state == 'bad' ? '⚠' : '•'),
				E('span', { 'class': 'dps-cr-text' }, [E('b', {}, label), ': ' + text, info ? api.info(info) : '']),
				action || ''
			]);
		}
		var rows = [];
		var running = (d.conflicts || []).filter(function(c) { return c.running; });
		if (!running.length)
			rows.push(row('ok', api.t('router.diag.conflicts'), api.t('web.diag.conflicts_none')));
		running.forEach(function(c) {
			if (c.kind == 'package')
				rows.push(row('bad', api.t('router.diag.conflicts'), api.t('web.diag.conflict_row', { name: c.name }), null, api.t('web.diag.package', { name: c.name })));
			else
				rows.push(row('bad', api.t('router.diag.conflicts'), api.t('web.diag.conflict_row', { name: c.name }),
					api.btn(api.t('router.diag.stop'), function() { return self.conflictStop(c.name); }, 'cbi-button-remove'), api.t('web.diag.conflict', { name: c.name })));
		});
		if (d.flow_offload)
			rows.push(row('bad', api.t('router.diag.flow'), api.t('web.diag.flow_on'),
				api.btn(api.t('router.warn.fix'), function() { return self.act('flow_offload_fix'); }, 'cbi-button-apply'), api.t('web.diag.flow')));
		else
			rows.push(row('ok', api.t('router.diag.flow'), api.t(d.flowoffload && d.flowoffload != 'none' ? 'web.diag.flow_handed' : 'web.diag.flow_off')));
		if (!d.ipv6_wan)
			rows.push(row('ok', api.t('router.diag.ipv6'), api.t('web.diag.ipv6_none')));
		else if (d.ipv6)
			rows.push(row('ok', api.t('router.diag.ipv6'), api.t('web.diag.ipv6_on')));
		else
			rows.push(row('bad', api.t('router.diag.ipv6'), api.t('web.diag.ipv6_bad'),
				api.btn(api.t('router.warn.fix'), function() { return self.act('ipv6_set', { enabled: true }); }, 'cbi-button-apply'), api.t('web.diag.ipv6')));
		var dn = d.dns || {}, when = dn.checked_at ? ' · ' + api.t('router.home.checked', { time: api.time(dn.checked_at) }) : '';
		if (dn.spoofed === true)
			rows.push(row('bad', api.t('web.diag.dns_label'), api.t('router.diag.dns_spoofed') + when,
				api.btn(api.t('router.dns.enable'), function() { api.dnsEnableDialog(function() { self.reload(); }); }, 'cbi-button-apply')));
		else if (dn.spoofed === false)
			rows.push(row('ok', api.t('web.diag.dns_label'), api.t('web.diag.dns_clean') + when));
		else
			rows.push(row('unknown', api.t('web.diag.dns_label'), api.t('router.diag.dns_not_checked'),
				api.btn(api.t('dns.check'), function() { api.go('dns'); }, 'cbi-button-neutral')));
		kids.push(bad.length
			? E('div', { 'class': 'dps-cr-sum dps-cr-sum-bad' }, '⚠ ' + api.t('web.diag.bad', { list: bad.filter(function(x, i, a) { return a.indexOf(x) == i; }).join(', ') }))
			: E('div', { 'class': 'dps-cr-sum dps-cr-sum-ok' }, '✓ ' + api.t('web.diag.none')));
		kids = kids.concat(rows);
		if (d.overlay_free_kb != null)
			kids.push(api.line(api.t('router.diag.space') + ': ' + (d.overlay_free_kb >= 1024 ? api.t('router.diag.space_value', { value: Math.round(d.overlay_free_kb / 1024) })
				: api.t('web.diag.space_kb', { value: d.overlay_free_kb })), 'dps-muted'));
		api.content(this.el.findings, kids);
	},

	// Справочно: ✓ защищено, ⚠ можно улучшить (с вариантами текстом, без кнопок — решение владельца 22.10), • не удалось
	// проверить. Тексты — общие с окном ПК (router.security.*); незнакомый id (роутер новее страницы) пропускается.
	renderSecurity: function() {
		var sec = this.security, kids = [], bad = [], unknown = 0, rows = [];
		if (!sec)
			return;
		if (sec.__error) {
			api.content(this.el.security, api.errorLine(sec.__error));
			return;
		}
		(sec.checks || []).forEach(function(c) {
			var base = 'router.security.' + c.id;
			if (!api.has(base))
				return;
			var state = c.state == 'ok' || c.state == 'warn' ? c.state : 'unknown';
			var key = base + '.' + (c.detail || state);
			var text = state == 'unknown' ? api.t('router.security.unknown')
				: api.has(key) ? api.t(key, c.params || {}) : api.t(base + '.' + state, c.params || {});
			var fix = state == 'warn' && api.has(base + '.fix') ? E('div', { 'class': 'dps-muted' }, api.t(base + '.fix')) : '';
			if (state == 'warn')
				bad.push(api.t(base));
			else if (state == 'unknown')
				unknown++;
			rows.push(E('div', { 'class': 'dps-check-row dps-cr-' + (state == 'warn' ? 'bad' : state) }, [
				E('span', { 'class': 'dps-cr-mark' }, state == 'ok' ? '✓' : state == 'warn' ? '⚠' : '•'),
				E('span', { 'class': 'dps-cr-text' }, [E('b', {}, api.t(base)), ': ' + text, fix])
			]));
		});
		// Ускорение сети — не про безопасность, но мешает оптимизации: оно в «Что мешает», здесь только ссылка.
		if (this.diag && !this.diag.__error && this.diag.flow_offload)
			rows.push(E('div', { 'class': 'dps-check-row dps-cr-unknown' }, [
				E('span', { 'class': 'dps-cr-mark' }, '•'),
				E('span', { 'class': 'dps-cr-text' }, [E('b', {}, api.t('router.diag.flow')), ': ' + api.t('router.security.flow_ref')])
			]));
		kids.push(bad.length
			? E('div', { 'class': 'dps-cr-sum dps-cr-sum-bad' }, '⚠ ' + api.t('router.security.bad', { list: bad.join(', ') }))
			: E('div', { 'class': 'dps-cr-sum dps-cr-sum-ok' }, '✓ ' + api.t(unknown ? 'router.security.ok_partial' : 'router.security.ok_all')));
		api.content(this.el.security, kids.concat(rows));
	},

	renderCore: function() {
		var self = this, s = this.status, kids = [];
		if (!s) {
			api.content(this.el.core, api.line(api.t('web.diag.unknown'), 'dps-muted'));
			return;
		}
		var c = s.core || {};
		if (s.jobs && s.jobs.core_install)
			kids.push(E('div', { 'class': 'spinning' }, api.t('web.core.loading')));
		else if (!c.installed)
			kids.push(api.line(api.t('web.core.missing_long'), 'dps-error'));
		else if (c.hash_ok === false)
			kids.push(api.line(api.t('web.core.broken_long'), 'dps-error'));
		else
			kids.push(api.line(api.t('web.core.ok', { version: c.version || '' }), 'dps-ok'));
		if (!(s.jobs && s.jobs.core_install) && (!c.installed || c.hash_ok === false))
			kids.push(E('div', { 'class': 'dps-actions' }, [
				api.btn(api.t(c.installed ? 'web.core.restore' : 'web.core.download'), function() { return self.coreInstall(); }, 'cbi-button-apply')
			]));
		api.content(this.el.core, kids);
	},

	coreInstall: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('core_install').then(function() {
			self.coreTicks = 600;
			self.status.jobs.core_install = true;
			self.renderCore();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	},

	act: function(method, args) {
		var self = this;
		this.el.err.textContent = '';
		return api.call(method, args).then(function() {
			return self.reload();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	},

	conflictStop: function(name) {
		var self = this;
		this.el.err.textContent = '';
		return api.call('conflict_stop', { name: name }).then(function() {
			return self.reload();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	},

	// Удаление (router-api.md §5, uninstall): внизу, свёрнуто, спокойная кнопка; подтверждение — словом «удалить».
	uninstallSection: function() {
		var self = this;
		var keep = E('input', { type: 'radio', name: 'dps-uninst', value: 'keep', checked: '' });
		var full = E('input', { type: 'radio', name: 'dps-uninst', value: 'full' });
		var word = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: api.t('web.uninst.word'), autocomplete: 'off', style: 'max-width:12em' });
		var btn = api.btn(api.t('web.uninst.button'), function() { return self.uninstall(!full.checked); }, 'cbi-button-negative');
		btn.disabled = true;
		word.addEventListener('input', function() { btn.disabled = word.value.trim().toLowerCase() != api.t('web.uninst.word').toLowerCase(); });
		this.el.uninstall = E('div', {}, [
			api.line(api.t('web.uninst.intro'), 'dps-muted'),
			E('label', { 'class': 'dps-radio' }, [keep, api.t('router.uninstall.keep')]),
			E('label', { 'class': 'dps-radio' }, [full, api.t('router.uninstall.full')]),
			E('div', { 'class': 'dps-actions' }, [E('span', {}, api.t('web.uninst.confirm', { word: api.t('web.uninst.word') }) + ' '), word, btn])
		]);
		// Опасное — отдельной красной карточкой, не рядом с «Сохранить отчёт» (ux-audit-web п.29).
		return E('div', { 'class': 'dps-danger', 'style': 'margin-top:2em' }, E('details', {}, [
			E('summary', { 'style': 'font-weight:600' }, api.t('router.uninstall.title')),
			this.el.uninstall
		]));
	},

	uninstall: function(keepSettings) {
		var self = this;
		return api.call('uninstall', { keep_settings: keepSettings }).then(function() {
			api.content(self.el.uninstall, E('div', { 'class': 'spinning' }, api.t('web.uninst.busy')));
			poll.stop();
			// Пакет и объект ubus исчезают по ходу удаления: пропали (или сброшена сессия) — значит, удалён.
			var tries = 0;
			var timer = window.setInterval(function() {
				tries++;
				api.safe('status').then(function(s) {
					if (s.__error || tries > 90) {
						window.clearInterval(timer);
						api.content(self.el.uninstall, api.line(s.__error
							? api.t(keepSettings ? 'web.uninst.done_kept' : 'web.uninst.done')
							: api.t('web.uninst.too_long'), s.__error ? 'dps-ok' : 'dps-muted'));
					}
				});
			}, 2000);
		}, function(e) {
			api.fail(self.el.err, e);
		});
	},

	report: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('report').then(function(r) {
			var d = new Date();
			var name = 'dpishell-report-%04d%02d%02d-%02d%02d.txt'.format(d.getFullYear(), d.getMonth() + 1, d.getDate(), d.getHours(), d.getMinutes());
			api.download(name, r.text || '');
		}, function(e) {
			api.fail(self.el.err, e);
		});
	}
});
