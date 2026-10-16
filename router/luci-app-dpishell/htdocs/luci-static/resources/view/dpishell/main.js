'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «Обход» — главный экран (06-router-spec §3.1, §3.4, §3.5).

var HOURS = [[0.5, '30 мин'], [1, '1 ч'], [3, '3 ч'], [6, '6 ч'], [12, '12 ч'], [24, '24 ч']];

var WARN_CLASS = {
	switched_backup: 'notice', auto_selected: 'notice', dns_fallback: 'warning'
};

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return Promise.all([api.safe('status'), api.safe('strategies')]);
	},

	render: function(data) {
		var self = this;
		this.status = data[0];
		this.strategies = data[1].__error ? { items: [] } : data[1];
		this.progress = null;
		this.tickNo = 0;
		this.lastWarnJson = null;
		this.siteTicks = 0;

		if (this.status.__error) {
			return E('div', {}, [
				E('h2', {}, 'DpiShell'),
				api.card('Обход', [api.errorLine(this.status.__error),
					api.line('Если пакет только что установлен — обновите страницу через минуту.', 'dps-muted')])
			]);
		}

		this.el = {
			state: E('div', { 'class': 'dps-state' }),
			toggle: api.btn('Включить', function() { return self.toggle(); }, 'cbi-button-apply'),
			select: api.btn('Подобрать', function() { return self.openSelect(); }, 'cbi-button-action'),
			check: api.btn('Проверить сейчас', function() { return self.checkNow(); }, 'cbi-button-neutral'),
			err: E('div', { 'class': 'dps-error' }),
			strategy: E('select', { 'class': 'cbi-input-select', 'style': 'width:100%;max-width:36em', 'change': function(ev) { self.setStrategy(ev.target.value); } }),
			modeAll: null, modeLists: null,
			ts: E('div', { 'class': 'dps-line', 'style': 'display:none' }, [
				'Компьютерам на Windows нужна настройка — без неё обход для них не работает',
				api.info('Эта стратегия требует, чтобы Windows отправляла метки времени в соединениях (TCP timestamps); по умолчанию они выключены. Приложение DpiShell на компьютере включает их само. Вручную — в командной строке от администратора: netsh interface tcp set global timestamps=enabled. Телефоны, ТВ и приставки настраивать не нужно.')
			]),
			warnings: E('div'),
			lastCheck: E('div', { 'class': 'dps-line dps-muted' }),
			schedOn: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedHours: E('select', { 'class': 'cbi-input-select', change: function() { self.saveSchedule(); } },
				HOURS.map(function(h) { return E('option', { value: String(h[0]) }, h[1]); })),
			schedAuto: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedMsg: E('div', { 'class': 'dps-line dps-muted' }),
			siteUrl: E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'example.com', style: 'width:100%;max-width:28em', keydown: function(ev) { if (ev.key == 'Enter') self.siteCheck(); } }),
			siteBtn: api.btn('Проверить', function() { return self.siteCheck(); }, 'cbi-button-action'),
			siteResult: E('div', { 'class': 'dps-line' })
		};
		this.el.modeAll = api.radio('dps-mode', 'all', 'Все сайты, кроме исключений', true, function() { self.setMode('all'); });
		this.el.modeLists = api.radio('dps-mode', 'lists', 'По спискам', false, function() { self.setMode('lists'); });

		var view = E('div', {}, [
			E('h2', {}, 'DpiShell — обход блокировок'),
			api.card('Обход', [
				this.el.state,
				E('div', { 'class': 'dps-actions' }, [this.el.toggle, this.el.select, this.el.check]),
				this.el.err,
				this.el.lastCheck,
				this.el.warnings,
				this.el.ts,
				api.row(['Стратегия', api.info('Стратегия — способ, которым обход обманывает оборудование провайдера; у разных провайдеров работают разные. Если не знаете, какую выбрать, нажмите «Подобрать» — роутер проверит сам и включит лучшую. При смене стратегии обход перезапускается, открытые соединения на секунду прервутся.')], this.el.strategy),
				api.row(['Режим', api.info('«Все сайты, кроме исключений» — обход для любого сайта, кроме списка исключений; это основной режим. «По спискам» — только для сайтов из списков (YouTube, Discord, свои). После смены режима роутер сам проверяет, что сайты открываются.')],
					E('div', {}, [this.el.modeAll, this.el.modeLists]))
			]),
			api.card('Проверять автоматически', [
				E('label', { 'class': 'dps-check' }, [this.el.schedOn, 'Проверять, что обход работает, каждые ', this.el.schedHours]),
				E('label', { 'class': 'dps-check' }, [this.el.schedAuto, 'Подбирать автоматически',
					api.info('Если обход перестал работать и запасные стратегии не помогли, роутер сам запустит быстрый подбор и включит найденную — не чаще раза в 6 часов. Пока идёт подбор (2–3 минуты), сайты в сети могут открываться с перебоями в непредсказуемый момент, поэтому по умолчанию выключено.')]),
				this.el.schedMsg
			]),
			api.card('Сайт не открывается с обходом', [
				api.line('Роутер проверит адрес с обходом и без. Если сайт открывается только без обхода — предложит добавить его в исключения.', 'dps-muted'),
				E('div', { 'class': 'dps-actions' }, [this.el.siteUrl, this.el.siteBtn]),
				this.el.siteResult
			])
		]);

		this.fillStrategies();
		this.fillSchedule();
		this.update();

		poll.add(L.bind(this.tick, this), 1);
		return view;
	},

	// Опрос: раз в 3 с, во время подбора/проверки сайта — раз в 1 с.
	tick: function() {
		var self = this;
		this.tickNo++;
		var fast = this.status.jobs && (this.status.jobs.select || this.status.jobs.site_check || this.status.jobs.check);
		if (!fast && (this.tickNo % 3) != 0)
			return Promise.resolve();
		return api.safe('status').then(function(s) {
			if (s.__error) {
				self.el.err.textContent = api.text(s.__error);
				return;
			}
			var wasSelecting = self.status.jobs && self.status.jobs.select;
			self.status = s;
			if (s.jobs.select)
				return api.safe('select_progress').then(function(p) {
					self.progress = p.__error ? null : p;
					self.update();
				});
			self.progress = null;
			self.update();
			if (wasSelecting)
				return api.safe('strategies').then(function(st) {
					if (!st.__error) {
						self.strategies = st;
						self.fillStrategies();
					}
				});
			if (self.siteTicks > 0)
				return self.pollSite();
		});
	},

	stateText: function() {
		var s = this.status, j = s.jobs || {};
		if (j.select) {
			var p = this.progress;
			return 'Подбор… ' + (p && p.total ? '%d из %d'.format(Math.min(p.index || 1, p.total), p.total) : '') +
				(p && p.message ? ' — ' + p.message : '');
		}
		if (j.core_install)
			return 'Загрузка ядра обхода…';
		if (!s.core.installed)
			return 'Ошибка: ядро обхода не установлено';
		if (s.core.hash_ok === false)
			return 'Ошибка: файлы ядра изменены';
		var title = this.strategyTitle(s.strategy);
		if (j.check)
			return (s.enabled ? 'Работает — ' + title + ', ' : '') + 'идёт проверка…';
		if (!s.enabled)
			return 'Выключено';
		var lc = s.last_check;
		if (lc && lc.ok === false)
			return 'Ошибка: проверка в ' + api.time(lc.at) + ' не прошла — ' + title;
		return 'Работает — ' + title + (lc && lc.at ? ', проверено в ' + api.time(lc.at) : ', ещё не проверялось');
	},

	strategyTitle: function(id) {
		if (!id)
			return 'стратегия не выбрана';
		var items = this.strategies.items || [];
		for (var i = 0; i < items.length; i++)
			if (items[i].id == id)
				return 'стратегия ' + items[i].title;
		return 'стратегия ' + id;
	},

	fillStrategies: function() {
		var sel = this.el.strategy, items = this.strategies.items || [];
		dom.content(sel, []);
		if (!this.status.strategy)
			sel.appendChild(E('option', { value: '' }, 'Не выбрана — нажмите «Подобрать»'));
		items.forEach(function(it) {
			var label = it.title;
			if (it.runs > 0)
				label += ' — открыла всё в %d из %d подборов'.format(it.passed, it.runs);
			sel.appendChild(E('option', { value: it.id }, label));
		});
		sel.value = this.status.strategy || '';
	},

	fillSchedule: function() {
		var sc = this.status.schedule || {};
		this.el.schedOn.checked = !!sc.enabled;
		this.el.schedHours.value = String(sc.hours || 1);
		this.el.schedAuto.checked = !!sc.auto_select;
	},

	update: function() {
		var s = this.status, j = s.jobs || {};
		var busy = !!(j.select || j.core_install);
		this.el.state.textContent = this.stateText();
		this.el.toggle.textContent = s.enabled ? 'Выключить' : 'Включить';
		this.el.toggle.className = 'btn cbi-button ' + (s.enabled ? 'cbi-button-remove' : 'cbi-button-apply');
		this.el.toggle.disabled = busy || !s.core.installed;
		this.el.select.disabled = busy || !s.core.installed;
		this.el.check.disabled = busy || !s.enabled || j.check;
		this.el.strategy.disabled = busy;
		if (document.activeElement != this.el.strategy && this.el.strategy.value != (s.strategy || ''))
			this.fillStrategies();
		this.el.modeAll.firstChild.checked = s.mode != 'lists';
		this.el.modeLists.firstChild.checked = s.mode == 'lists';
		this.el.modeAll.firstChild.disabled = this.el.modeLists.firstChild.disabled = busy;
		this.el.ts.style.display = (s.enabled && s.needs_tcp_timestamps) ? '' : 'none';

		var lc = s.last_check;
		if (lc && lc.at) {
			var src = { manual: 'по кнопке', timer: 'по таймеру', app: 'из приложения' }[lc.source] || '';
			var groups = (lc.groups || []).map(function(g) {
				return ({ youtube: 'YouTube', discord: 'Discord', user: 'свои адреса' }[g.id] || g.id) + (g.ok ? ' — да' : ' — нет');
			}).join(', ');
			this.el.lastCheck.textContent = 'Последняя проверка ' + api.time(lc.at) + (src ? ' ' + src : '') + ': ' +
				(lc.ok ? 'всё открывается' : 'не всё открывается') + (groups ? ' (' + groups + ')' : '');
		} else
			this.el.lastCheck.textContent = '';

		var wj = JSON.stringify(s.warnings || []);
		if (wj != this.lastWarnJson) {
			this.lastWarnJson = wj;
			this.renderWarnings(s.warnings || []);
		}
	},

	renderWarnings: function(list) {
		var self = this;
		dom.content(this.el.warnings, list.map(function(w) {
			var action = null;
			switch (w.code) {
			case 'dns_spoofed':
				action = api.btn('Включить защищённый DNS', function() { api.dnsEnableDialog(function() { api.go('dns'); }); }, 'cbi-button-apply');
				break;
			case 'conflict':
				action = api.btn('Остановить', function() { api.go('diag'); }, 'cbi-button-apply');
				break;
			case 'core_missing':
				action = api.btn('Загрузить ядро', function() { return self.coreInstall(); }, 'cbi-button-apply');
				break;
			case 'auto_select_failed':
				action = api.btn('Подобрать', function() { return self.openSelect(); }, 'cbi-button-apply');
				break;
			case 'flow_offload':
				action = api.btn('Открыть настройки', function() { window.location.href = L.url('admin/network/firewall'); }, 'cbi-button-neutral');
				break;
			case 'dns_fallback':
				action = api.btn('Подробнее', function() { api.go('dns'); }, 'cbi-button-neutral');
				break;
			}
			return E('div', { 'class': 'alert-message ' + (WARN_CLASS[w.code] || 'warning') },
				E('div', { 'class': 'dps-warn' }, [E('span', {}, w.text || w.code), action || '']));
		}));
	},

	act: function(method, args) {
		var self = this;
		this.el.err.textContent = '';
		return api.call(method, args).then(function(r) {
			return api.safe('status').then(function(s) {
				if (!s.__error) {
					self.status = s;
					self.update();
				}
				return r;
			});
		}, function(e) {
			self.el.err.textContent = api.text(e);
			throw e;
		});
	},

	toggle: function() {
		var self = this;
		if (this.status.enabled)
			return this.act('disable').catch(function() {});
		return this.act('enable').catch(function(e) {
			// Без стратегии и статистики «Включить» = подобрать (06-router-spec §1).
			if (!self.status.strategy && e.code != 'busy' && e.code != 'not_installed')
				self.openSelect();
		});
	},

	checkNow: function() {
		return this.act('check_now').catch(function() {});
	},

	openSelect: function() {
		var self = this;
		api.selectDialog(this.status, function() { api.go('select'); });
		return Promise.resolve();
	},

	setStrategy: function(id) {
		var self = this;
		if (!id)
			return;
		this.el.strategy.disabled = true;
		return this.act('strategy_set', { id: id }).catch(function() {
			self.el.strategy.value = self.status.strategy || '';
		}).then(function() { self.el.strategy.disabled = false; });
	},

	setMode: function(mode) {
		var self = this;
		if (mode == this.status.mode)
			return;
		return this.act('mode_set', { mode: mode }).catch(function() {
			self.el.modeAll.firstChild.checked = self.status.mode != 'lists';
			self.el.modeLists.firstChild.checked = self.status.mode == 'lists';
		});
	},

	saveSchedule: function() {
		var self = this;
		this.el.schedMsg.textContent = '';
		return api.call('schedule_set', {
			enabled: this.el.schedOn.checked,
			hours: parseFloat(this.el.schedHours.value),
			auto_select: this.el.schedAuto.checked
		}).then(function() {
			self.el.schedMsg.textContent = 'Сохранено';
			setTimeout(function() { self.el.schedMsg.textContent = ''; }, 2000);
		}, function(e) {
			self.el.schedMsg.textContent = api.text(e);
			self.fillSchedule();
		});
	},

	coreInstall: function() {
		return this.act('core_install').catch(function() {});
	},

	// «Сайт не открывается с обходом»
	siteCheck: function() {
		var self = this;
		var url = (this.el.siteUrl.value || '').trim();
		if (!url) {
			this.el.siteResult.textContent = 'Введите адрес сайта';
			return Promise.resolve();
		}
		if (!/^https?:\/\//i.test(url))
			url = 'https://' + url;
		dom.content(this.el.siteResult, E('span', { 'class': 'spinning' }, 'Проверяем ' + url + '…'));
		return api.call('site_check', { url: url }).then(function() {
			self.siteTicks = 90;
			self.status.jobs.site_check = true;
		}, function(e) {
			self.el.siteResult.textContent = api.text(e);
		});
	},

	pollSite: function() {
		var self = this;
		this.siteTicks--;
		return api.safe('site_check_result').then(function(r) {
			if (r.__error) {
				self.siteTicks = 0;
				self.el.siteResult.textContent = api.text(r.__error);
				return;
			}
			if (r.running && self.siteTicks > 0)
				return;
			self.siteTicks = 0;
			if (r.running) {
				self.el.siteResult.textContent = 'Проверка затянулась — попробуйте ещё раз позже';
				return;
			}
			self.renderSiteResult(r);
		});
	},

	renderSiteResult: function(r) {
		var self = this;
		function part(label, x) {
			if (!x)
				return E('div', {}, label + ': нет данных');
			return E('div', { 'class': x.ok ? 'dps-ok' : 'dps-error' },
				label + ': ' + (x.ok ? 'открывается' : 'не открывается') + (x.detail ? ' (' + x.detail + ')' : ''));
		}
		var kids = [E('div', {}, [E('b', {}, r.url || '')])];
		if (r.error) {
			kids.push(E('div', { 'class': 'dps-error' }, r.error));
			dom.content(this.el.siteResult, kids);
			return;
		}
		kids.push(part('С обходом', r.with_bypass), part('Без обхода', r.without_bypass));
		if (r.suggest_exclude) {
			var host = (r.url || '').replace(/^https?:\/\//i, '').replace(/[:/].*$/, '');
			kids.push(E('div', { 'class': 'dps-actions' }, [
				api.btn('Добавить ' + host + ' в исключения', function() { return self.addExclude(host); }, 'cbi-button-apply')
			]));
		} else if (r.with_bypass && !r.with_bypass.ok)
			kids.push(E('div', { 'class': 'dps-muted' }, 'Сайт не открывается и без обхода — дело не в обходе.'));
		else
			kids.push(E('div', { 'class': 'dps-muted' }, 'С обходом сайт открывается — исключение не нужно.'));
		dom.content(this.el.siteResult, kids);
	},

	addExclude: function(host) {
		var self = this;
		return api.call('lists_get', { name: 'user-exclude' }).then(function(l) {
			var lines = l.lines || [];
			if (lines.indexOf(host) < 0)
				lines.push(host);
			return api.call('lists_set', { name: 'user-exclude', lines: lines });
		}).then(function() {
			dom.content(self.el.siteResult, E('div', { 'class': 'dps-ok' }, host + ' добавлен в исключения'));
		}, function(e) {
			self.el.siteResult.appendChild(api.errorLine(e));
		});
	}
});
