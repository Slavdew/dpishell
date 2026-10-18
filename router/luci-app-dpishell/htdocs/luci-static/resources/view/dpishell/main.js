'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «Обход» — главный экран (architecture.md «Страница роутера»).

var HOURS = [[0.5, '30 мин'], [1, '1 ч'], [3, '3 ч'], [6, '6 ч'], [12, '12 ч'], [24, '24 ч']];
// Сколько роутер ждёт «Да, оставить» после включения со страницы (router-api.md confirm_s).
var CONFIRM_S = 180;

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
			check: api.btn('Проверить', function() { return self.checkNow(); }, 'cbi-button-neutral'),
			err: E('div', { 'class': 'dps-error' }),
			// Стратегия и режим — не сразу: обход для всей сети перезапускается (UX-аудит п. 50).
			strategy: E('select', { 'class': 'cbi-input-select', 'style': 'width:100%;max-width:36em', 'change': function(ev) { self.pend('strategy', ev.target.value); } }),
			apply: E('div', { 'class': 'dps-actions', 'style': 'display:none' }, [
				api.btn('Применить', function() { return self.applyPending(); }, 'cbi-button-apply'),
				api.btn('Отмена', function() { self.cancelPending(); }, 'cbi-button-neutral'),
				E('span', { 'class': 'dps-muted' }, 'Обход для всей сети перезапустится — сайты моргнут на пару секунд.')
			]),
			modeAll: null, modeLists: null,
			ts: E('div', { 'class': 'dps-line', 'style': 'display:none' }, [
				'Компьютерам на Windows нужна настройка — без неё обход для них не работает',
				api.info('Эта стратегия требует, чтобы Windows отправляла метки времени в соединениях (TCP timestamps); по умолчанию они выключены. Приложение DpiShell на компьютере включает их само. Вручную — в командной строке от администратора: netsh interface tcp set global timestamps=enabled. Телефоны, ТВ и приставки настраивать не нужно.')
			]),
			warnings: E('div'),
			// Включение на пробу (architecture.md «Аварийный выключатель»): без «Да, оставить» роутер сам вернёт прежнее.
			confirmText: E('span'),
			confirm: E('div', { 'class': 'alert-message notice', 'style': 'display:none' }),
			update: E('div'),
			lastCheck: E('div', { 'class': 'dps-line dps-muted' }),
			schedOn: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedHours: E('select', { 'class': 'cbi-input-select', change: function() { self.saveSchedule(); } },
				HOURS.map(function(h) { return E('option', { value: String(h[0]) }, h[1]); })),
			schedAuto: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedWatch: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedMsg: E('div', { 'class': 'dps-line dps-muted' }),
			siteUrl: E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'example.com', style: 'width:100%;max-width:28em', keydown: function(ev) { if (ev.key == 'Enter') self.siteCheck(); } }),
			siteBtn: api.btn('Проверить', function() { return self.siteCheck(); }, 'cbi-button-action'),
			siteResult: E('div', { 'class': 'dps-line' })
		};
		this.el.modeAll = api.radio('dps-mode', 'all', 'Все сайты', true, function() { self.pend('mode', 'all'); });
		this.el.modeLists = api.radio('dps-mode', 'lists', 'Только из списков', false, function() { self.pend('mode', 'lists'); });
		this.pending = {};

		// Порядок — как во вкладке «Роутер» окна ПК (UX-аудит, макет страницы роутера).
		var view = E('div', {}, [
			E('h2', {}, 'DpiShell — обход блокировок'),
			this.el.update,
			api.card('Обход', [
				this.el.state,
				E('div', { 'class': 'dps-actions' }, [this.el.toggle, this.el.check, this.el.select]),
				this.el.err,
				this.el.lastCheck,
				this.el.confirm,
				this.el.warnings,
				this.el.ts,
				api.row(['Для каких сайтов', api.info('«Все сайты» — обход для любого сайта, кроме исключений; это основной режим. «Только из списков» — только для сайтов из списков (YouTube, Discord, свои). После смены роутер сам проверяет, что сайты открываются.')],
					E('div', {}, [this.el.modeAll, this.el.modeLists])),
				api.row(['Стратегия', api.info('Стратегия — способ, которым обход обманывает оборудование провайдера; у разных провайдеров работают разные. Если не знаете, какую выбрать, нажмите «Подобрать» — роутер проверит сам и включит лучшую.')], this.el.strategy),
				this.el.apply,
				E('h4', { 'style': 'margin-top:1.2em' }, 'Проверять сам'),
				E('label', { 'class': 'dps-check' }, [this.el.schedOn, 'Проверять сайты каждые ', this.el.schedHours]),
				E('label', { 'class': 'dps-check' }, [this.el.schedAuto, 'Если не помогло — подобрать заново',
					api.info('Если обход перестал работать и запасные стратегии не помогли, роутер сам запустит быстрый подбор и включит найденную — не чаще раза в 6 часов. Пока идёт подбор (2–3 минуты), сайты в сети могут открываться с перебоями в непредсказуемый момент, поэтому по умолчанию выключено.')]),
				E('label', { 'class': 'dps-check' }, [this.el.schedWatch, 'Выключать обход, если с ним пропал интернет',
					api.info('Раз в 5 минут роутер проверяет обычные сайты. Если с обходом они не открываются, а без него открываются — обход выключается, причина видна здесь. Включить снова — кнопкой «Включить». Если интернета нет и без обхода, это обрыв у провайдера: обход остаётся.')]),
				this.el.schedMsg
			]),
			api.card('Сайт не открывается', [
				api.line('Роутер проверит адрес с обходом и без. Если сайт открывается только без обхода — предложит добавить его в исключения.', 'dps-muted'),
				E('div', { 'class': 'dps-actions' }, [this.el.siteUrl, this.el.siteBtn]),
				this.el.siteResult
			]),
			this.about()
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
		this.renderConfirm();
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
			return 'Ошибка: файлы обхода повреждены — нажмите «Восстановить» в «Диагностике»';
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
		this.el.schedWatch.checked = sc.watchdog !== false;
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
		if (document.activeElement != this.el.strategy && !this.pending.strategy && this.el.strategy.value != (s.strategy || ''))
			this.fillStrategies();
		if (!this.pending.mode) {
			this.el.modeAll.firstChild.checked = s.mode != 'lists';
			this.el.modeLists.firstChild.checked = s.mode == 'lists';
		}
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

		this.renderUpdate(s.update);
		// Отсчёт — от left_s, не от until: часы роутера и браузера могут расходиться.
		this.confirmDeadline = s.pending_confirm ? Date.now() + (s.pending_confirm.left_s || 0) * 1000 : 0;
		this.renderConfirm();

		var wj = JSON.stringify(s.warnings || []);
		if (wj != this.lastWarnJson) {
			this.lastWarnJson = wj;
			this.renderWarnings(s.warnings || []);
		}
	},

	// «О программе»: версии пакета, договора с окном ПК и ядра; где код и инструкция.
	about: function() {
		var s = this.status, link = function(href, text) { return E('a', { href: href, target: '_blank', rel: 'noopener' }, text); };
		return api.card('О программе', [
			api.line('DpiShell ' + (s.version || '—') + ' · договор с приложением на ПК ' + (s.api || '—') +
				' · ядро zapret ' + ((s.core && s.core.version) || '—'), 'dps-muted'),
			api.line([link('https://github.com/Slavdew/dpishell', 'Исходный код на GitHub'), ' · ',
				link('https://github.com/Slavdew/dpishell/blob/master/docs/router/README.md', 'Инструкция для роутера')])
		]);
	},

	renderConfirm: function() {
		var self = this, pc = this.status.pending_confirm;
		var left = pc ? Math.max(0, Math.round((this.confirmDeadline - Date.now()) / 1000)) : 0;
		if (!pc || !left) {
			this.el.confirm.style.display = 'none';
			return;
		}
		if (!this.el.confirm.firstChild)
			dom.content(this.el.confirm, [this.el.confirmText, ' ',
				api.btn('Да, оставить', function() { return self.act('confirm'); }, 'cbi-button-positive')]);
		this.el.confirmText.textContent = 'Всё работает? Иначе через %d:%02d роутер вернёт как было.'.format(Math.floor(left / 60), left % 60);
		this.el.confirm.style.display = '';
	},

	// Плашка «Доступно обновление» (router-api.md §5): что нового — до трёх строк, кнопка «Обновить».
	renderUpdate: function(u) {
		var self = this;
		var key = JSON.stringify([u && u.available, u && u.version, this.updating]);
		if (key == this.lastUpdateKey)
			return;
		this.lastUpdateKey = key;
		if (this.updating) {
			this.el.updateStep = E('div', { 'class': 'dps-muted' });
			dom.content(this.el.update, E('div', { 'class': 'alert-message notice' }, [
				E('span', { 'class': 'spinning' }, 'Обновляю DpiShell… страница перезагрузится сама'), this.el.updateStep]));
			return;
		}
		if (!u || !u.available) {
			dom.content(this.el.update, []);
			return;
		}
		var notes = (u.notes || []).slice(0, 3);
		dom.content(this.el.update, E('div', { 'class': 'alert-message notice' }, [
			E('div', { 'class': 'dps-warn' }, [
				E('span', {}, [E('b', {}, 'Доступно обновление DpiShell ' + (u.version || '').replace(/-r\d+$/, '')),
					notes.length ? E('ul', { 'style': 'margin:.3em 0 0 1.2em' }, notes.map(function(n) { return E('li', {}, n); })) : '']),
				api.btn('Обновить', function() { return self.updateInstall(); }, 'cbi-button-apply')
			])
		]));
	},

	updateInstall: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('update_install', { from: 'feed' }).then(function() {
			self.updating = true;
			self.renderUpdate(self.status.update);
			poll.stop();
			// Пакет меняет сам себя и перезапускает rpcd: ошибки ответа во время обновления — пережидаем.
			var started = Date.now();
			var timer = window.setInterval(function() {
				api.safe('status').then(function(s) {
					var sec = Math.round((Date.now() - started) / 1000);
					var j = !s.__error && s.update && s.update.job;
					// Ход (update_job.step); пока rpcd перезапускается, ответа нет — это и есть перезапуск.
					if (self.el.updateStep)
						self.el.updateStep.textContent = (s.__error ? 'Перезапуск служб роутера' :
							({ download: 'Скачиваю пакеты', install: 'Устанавливаю' + (j && j.target ? ' ' + j.target : ''), restart: 'Перезапуск служб роутера' }[j && j.step] || 'Готовлюсь')) +
							' · ' + api.duration(sec);
					if (s.__error && Date.now() - started < 300000)
						return;
					if (!s.__error && s.jobs && s.jobs.update)
						return;
					window.clearInterval(timer);
					var job = !s.__error && s.update && s.update.job;
					if (job && job.ok === false) {
						self.updating = false;
						self.status = s;
						self.el.err.textContent = job.message;
						self.renderUpdate(s.update);
						poll.start();
						return;
					}
					window.location.reload();
				});
			}, 2000);
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
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
				// Останавливает по-настоящему (службы); пакет — только удалить, процесс — разбор в «Диагностике» (п. 49).
				action = api.btn('Остановить', function() { return self.stopConflicts(); }, 'cbi-button-apply');
				break;
			case 'core_missing':
				action = api.btn('Загрузить ядро', function() { return self.coreInstall(); }, 'cbi-button-apply');
				break;
			case 'auto_select_failed':
				action = api.btn('Подобрать', function() { return self.openSelect(); }, 'cbi-button-apply');
				break;
			case 'flow_offload':
				action = api.btn('Исправить', function() { return self.act('flow_offload_fix'); }, 'cbi-button-apply');
				break;
			case 'ipv6':
				action = api.btn('Исправить', function() { return self.act('ipv6_set', { enabled: true }); }, 'cbi-button-apply');
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
		return this.act('enable', { confirm_s: CONFIRM_S }).catch(function(e) {
			// Без стратегии и статистики «Включить» = подобрать (architecture.md «Принцип»).
			if (!self.status.strategy && e.code != 'busy' && e.code != 'not_installed')
				self.openSelect();
		});
	},

	stopConflicts: function() {
		var self = this;
		this.el.err.textContent = '';
		if (!window.confirm('Остановить другую программу обхода? Она выключится и не запустится после перезагрузки роутера; включить её снова можно в её настройках.'))
			return Promise.resolve();
		return api.call('diag').then(function(d) {
			var running = (d.conflicts || []).filter(function(c) { return c.running; });
			var services = running.filter(function(c) { return c.kind == 'service'; });
			var other = running.filter(function(c) { return c.kind != 'service'; });
			return services.reduce(function(p, c) {
				return p.then(function() { return api.call('conflict_stop', { name: c.name }); });
			}, Promise.resolve()).then(function() {
				if (other.length)
					self.el.err.textContent = other.map(function(c) {
						return c.kind == 'package' ? 'Удалите пакет ' + c.name + ' (apk del ' + c.name + ')' : 'Работает ' + c.name + ' — подробнее в «Диагностике»';
					}).join('. ');
				return self.act('status');
			});
		}).catch(function(e) { self.el.err.textContent = api.text(e); });
	},

	checkNow: function() {
		return this.act('check_now').catch(function() {});
	},

	openSelect: function() {
		var self = this;
		api.selectDialog(this.status, function() { api.go('select'); });
		return Promise.resolve();
	},

	// Выбранное, но не применённое: стратегия и режим (кнопка «Применить»).
	pend: function(what, value) {
		var cur = what == 'mode' ? (this.status.mode == 'lists' ? 'lists' : 'all') : (this.status.strategy || '');
		if (!value || value == cur)
			delete this.pending[what];
		else
			this.pending[what] = value;
		this.el.apply.style.display = Object.keys(this.pending).length ? '' : 'none';
	},

	cancelPending: function() {
		this.pending = {};
		this.el.apply.style.display = 'none';
		this.fillStrategies();
		this.el.modeAll.firstChild.checked = this.status.mode != 'lists';
		this.el.modeLists.firstChild.checked = this.status.mode == 'lists';
	},

	applyPending: function() {
		var self = this, p = this.pending;
		var chain = Promise.resolve();
		if (p.strategy)
			chain = chain.then(function() { return self.act('strategy_set', { id: p.strategy, confirm_s: CONFIRM_S }); });
		if (p.mode)
			chain = chain.then(function() { return self.act('mode_set', { mode: p.mode }); });
		return chain.then(function() { self.cancelPending(); }, function() { self.cancelPending(); });
	},

	saveSchedule: function() {
		var self = this;
		this.el.schedMsg.textContent = '';
		return api.call('schedule_set', {
			enabled: this.el.schedOn.checked,
			hours: parseFloat(this.el.schedHours.value),
			auto_select: this.el.schedAuto.checked,
			watchdog: this.el.schedWatch.checked
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
