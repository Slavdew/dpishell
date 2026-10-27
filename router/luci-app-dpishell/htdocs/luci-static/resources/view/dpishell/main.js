'use strict';
'require view';
'require ui';
'require poll';
'require dpishell.api as api';

// Строки — только текстом: свой E вместо LuCI (api.js, tools/xss-check.py).
var E = api.E;

// Вкладка «Главная» (architecture.md «Страница роутера»; названия — как в окне ПК, матрица ПК×роутер).

var HOURS = [[0.5, '30m'], [1, '1h'], [3, '3h'], [6, '6h'], [12, '12h'], [24, '24h']];
// Сколько роутер ждёт «Да, оставить» после включения со страницы (router-api.md confirm_s).
var CONFIRM_S = 180;
var GROUP_KEYS = { user: 'select.group.user' };

var WARN_CLASS = {
	switched_backup: 'notice', auto_selected: 'notice', dns_fallback: 'warning'
};

// Порядок стратегий «по имени» — естественный: ALT2, ALT3 … ALT10.
function byName(a, b) {
	return String(a.title).localeCompare(String(b.title), undefined, { numeric: true, sensitivity: 'base' });
}

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return api.lang().then(function() {
			// select_progress — только чтобы знать, есть ли отчёт для «Итогов подбора».
			return Promise.all([api.safe('status'), api.safe('strategies'), api.safe('select_progress')]);
		});
	},

	render: function(data) {
		var self = this;
		this.status = data[0];
		this.strategies = data[1].__error ? { items: [] } : data[1];
		this.progress = null;
		this.tickNo = 0;
		this.lastWarnJson = null;
		this.order = 'result';
		try { this.order = window.localStorage.getItem('dpishell-order') || 'result'; } catch (e) {}

		if (this.status.__error) {
			return E('div', {}, [
				E('h2', {}, 'DpiShell'),
				api.card(null, [api.errorLine(this.status.__error),
					api.line(api.t('web.main.just_installed'), 'dps-muted')])
			]);
		}

		this.el = {
			// Панель состояния (ux-audit-web п.1): точка, крупное состояние, одна строка пояснения, одна главная кнопка.
			dot: E('span', { 'class': 'dps-dot' }),
			state: E('div', { 'class': 'dps-hero-title' }),
			stateSub: E('div', { 'class': 'dps-hero-sub' }),
			toggle: api.btn(api.t('router.enable'), function() { return self.toggle(); }, 'cbi-button-positive dps-big'),
			select: api.btn(api.t('router.select'), function() { return self.openSelect(); }, 'cbi-button-neutral'),
			check: api.btn(api.t('home.check_now'), function() { return self.checkNow(); }, 'cbi-button-neutral'),
			// «Итоги подбора» — последний отчёт (роутер хранит его до нового подбора), то же место, что на главной окна ПК.
			report: api.btn(api.t('home.report'), function() { api.go('select'); }, 'cbi-button-neutral'),
			err: E('div', { 'class': 'dps-error' }),
			// Стратегия — не сразу: оптимизация для всей сети перезапускается (UX-аудит п. 50).
			strategy: E('select', { 'class': 'cbi-input-select', 'style': 'width:100%;max-width:36em', 'change': function(ev) { self.pend(ev.target.value); } }),
			order: E('select', { 'class': 'cbi-input-select', 'style': 'width:auto', 'change': function(ev) { self.setOrder(ev.target.value); } }, [
				E('option', { value: 'result', title: api.t('home.order.by_result.tip') }, api.t('home.order.by_result')),
				E('option', { value: 'name', title: api.t('home.order.by_name.tip') }, api.t('home.order.by_name'))
			]),
			apply: E('div', { 'class': 'dps-actions', 'style': 'display:none' }, [
				api.btn(api.t('web.apply'), function() { return self.applyPending(); }, 'cbi-button-apply'),
				api.btn(api.t('common.cancel'), function() { self.cancelPending(); }, 'cbi-button-neutral'),
				E('span', { 'class': 'dps-muted' }, api.t('web.main.restart_note'))
			]),
			params: E('div'),
			ts: E('div', { 'class': 'dps-line', 'style': 'display:none' }, [
				api.t('web.main.ts_need'),
				api.info(api.t('web.main.ts_info'))
			]),
			warnings: E('div'),
			// Включение на пробу (architecture.md «Аварийный выключатель»): без «Да, оставить» роутер сам вернёт прежнее.
			confirmText: E('span'),
			confirm: E('div', { 'class': 'alert-message notice', 'style': 'display:none' }),
			update: E('div'),
			lastCheck: E('div', { 'class': 'dps-line' }),
			schedOn: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedHours: E('select', { 'class': 'cbi-input-select', change: function() { self.saveSchedule(); } },
				HOURS.map(function(h) { return E('option', { value: String(h[0]) }, api.t('router.schedule.opt.' + h[1])); })),
			schedAuto: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedWatch: E('input', { type: 'checkbox', change: function() { self.saveSchedule(); } }),
			schedMsg: E('div', { 'class': 'dps-line dps-muted' })
		};
		this.el.order.value = this.order;
		this.pending = null;

		// Порядок — как во вкладке «Роутер» окна ПК (UX-аудит, макет страницы роутера).
		var view = E('div', {}, [
			E('h2', {}, api.t('web.main.title')),
			this.el.update,
			api.card(null, [
				E('div', { 'class': 'dps-hero' }, [
					this.el.dot,
					E('div', { 'class': 'dps-hero-text' }, [this.el.state, this.el.stateSub]),
					this.el.toggle
				]),
				this.el.confirm,
				this.el.warnings,
				this.el.lastCheck,
				E('div', { 'class': 'dps-actions' }, [this.el.check, this.el.select, this.el.report]),
				this.el.err,
				this.el.ts
			]),
			api.card(null, [
				api.row([api.t('router.strategy'), api.info(api.t('web.main.strategy_info'))], this.el.strategy),
				api.row(api.t('home.order.label'), this.el.order),
				this.el.params,
				this.el.apply
			]),
			api.siteCheckCard('main'),
			api.card(api.t('router.schedule.title'), [
				E('label', { 'class': 'dps-check' }, [this.el.schedOn, api.t('router.schedule.enabled') + ' ', this.el.schedHours]),
				E('label', { 'class': 'dps-check' }, [this.el.schedAuto, api.t('router.schedule.reselect'),
					api.info(api.t('web.main.reselect_info'))]),
				E('label', { 'class': 'dps-check' }, [this.el.schedWatch, api.t('router.watchdog.check'),
					api.info(api.t('web.main.watchdog_info'))]),
				E('p', { 'class': 'dps-muted' }, api.t('web.main.reset_note')),
				this.el.schedMsg
			])
		]);

		this.setReport(data[2]);
		this.fillStrategies();
		this.fillSchedule();
		this.update();

		poll.add(L.bind(this.tick, this), 1);
		return view;
	},

	// Опрос: в покое раз в 10 с, во время подбора/проверки и ожидания подтверждения — раз в 1 с; скрытая вкладка —
	// не опрашивается (status нагружает CPU роутера), после возврата — сразу.
	tick: function() {
		var self = this;
		if (document.hidden) {
			this.wasHidden = true;
			return Promise.resolve();
		}
		this.tickNo++;
		this.renderConfirm();
		var fast = (this.status.jobs && (this.status.jobs.select || this.status.jobs.check)) || this.status.pending_confirm;
		if (!fast && !this.wasHidden && (this.tickNo % 10) != 0)
			return Promise.resolve();
		this.wasHidden = false;
		return api.safe('status').then(function(s) {
			if (s.__error) {
				api.fail(self.el.err, s.__error);
				return;
			}
			var wasSelecting = self.status.jobs && self.status.jobs.select;
			self.status = s;
			if (s.jobs.select)
				// Здесь нужны только index/total/depth: роутер, умеющий since (rows_total в ответе), строк не шлёт.
				return api.safe('select_progress', self.progress && self.progress.rows_total != null ? { since: 1000000 } : null).then(function(p) {
					self.progress = p.__error ? null : p;
					self.update();
				});
			self.progress = null;
			self.update();
			if (wasSelecting)
				return Promise.all([api.safe('strategies'), api.safe('select_progress', { since: 1000000 })]).then(function(d) {
					if (!d[0].__error) {
						self.strategies = d[0];
						self.fillStrategies();
					}
					self.setReport(d[1]);
				});
		});
	},

	// «Итоги подбора»: отчёта нет (подбора не было или идёт новый) — кнопка серая, в подсказке почему (не скрыта).
	setReport: function(p) {
		var has = !!(p && !p.__error && !p.running && p.finished_at);
		this.el.report.disabled = !has;
		this.el.report.title = has ? api.t('web.main.report_tip', { time: api.time(p.finished_at) }) : api.t('web.main.report_none');
	},

	// Состояние для панели — как на главной окна ПК: kind — ok | off | err | busy (цвет точки).
	stateText: function() {
		var s = this.status, j = s.jobs || {};
		if (j.select) {
			var p = this.progress;
			return { kind: 'busy', title: api.t(p && p.depth == 'deep' ? 'home.status.deep' : 'home.status.select'),
				sub: p && p.total ? api.t('router.select.progress', { index: Math.min(p.index || 1, p.total), total: p.total }) : api.t('web.main.select_sub') };
		}
		if (j.core_install)
			return { kind: 'busy', title: api.t('web.core.loading') };
		if (!s.core.installed)
			return { kind: 'err', title: api.t('web.core.missing'), sub: api.t('web.core.missing_hint') };
		if (s.core.hash_ok === false)
			return { kind: 'err', title: api.t('web.core.broken'), sub: api.t('web.core.broken_hint') };
		var title = this.strategyTitle(s.strategy);
		if (j.check)
			return { kind: 'busy', title: api.t('home.check.running'), sub: s.enabled ? title : '' };
		if (!s.enabled)
			return { kind: 'off', title: api.t('home.status.off'), sub: s.strategy ? title : api.t('web.main.off_no_strategy') };
		var lc = s.last_check;
		// Итог старше часа — справочно, с возрастом: не «Работает частично», а «проверено 5 ч назад — открывалось не всё».
		if (lc && lc.at && api.stale(lc.at))
			return { kind: 'ok', title: api.t('home.status.running'),
				sub: title + ' · ' + api.t(lc.ok === false ? 'router.home.checked_bad_ago' : 'router.home.checked_ago', { ago: api.ago(lc.at) }) };
		var when = lc && lc.at ? api.t('router.home.checked', { time: api.time(lc.at) }) : api.t('router.wd.not_checked');
		if (lc && lc.ok === false)
			return { kind: 'err', title: api.t('home.status.partial'), sub: title + ' · ' + when };
		return { kind: 'ok', title: api.t('home.status.running'), sub: title + ' · ' + when };
	},

	strategyTitle: function(id) {
		if (!id)
			return api.t('home.strategy.none');
		var items = this.strategies.items || [];
		for (var i = 0; i < items.length; i++)
			if (items[i].id == id)
				return api.t('home.strategy', { name: api.stratTitle(items[i].title) });
		return api.t('home.strategy', { name: id });
	},

	// Список стратегий: «по результату» — порядок роутера (по статистике подборов), «по имени» — на странице.
	fillStrategies: function() {
		var sel = this.el.strategy, items = (this.strategies.items || []).slice();
		if (this.order == 'name')
			items.sort(byName);
		api.content(sel, []);
		if (!this.status.strategy)
			sel.appendChild(E('option', { value: '' }, api.t('web.main.not_selected')));
		items.forEach(function(it) {
			var label = it.runs > 0 ? api.t(it.passed > 0 ? 'web.main.opt_stats' : 'web.main.opt_stats_none', { name: api.stratTitle(it.title), passed: it.passed, runs: it.runs }) : api.stratTitle(it.title);
			sel.appendChild(E('option', { value: it.id }, label));
		});
		sel.value = this.pending || this.status.strategy || '';
		this.renderParams();
	},

	// «Параметры» выбранной стратегии — роли и аргументы nfqws (strategies.items[].roles), как на главной окна ПК (матрица п. 8).
	renderParams: function() {
		var self = this, id = this.pending || this.status.strategy;
		var it = (this.strategies.items || []).filter(function(x) { return x.id == id; })[0];
		var roles = it && it.roles ? Object.keys(it.roles) : [];
		if (!roles.length) {
			api.content(this.el.params, []);
			return;
		}
		var text = roles.map(function(r) { return r + ': ' + it.roles[r]; }).join('\n');
		var msg = E('span', { 'class': 'dps-muted' });
		var det = E('details', { 'class': 'dps-details', open: this.paramsOpen ? '' : null,
			toggle: function() { self.paramsOpen = det.open; } }, [
			E('summary', { title: api.t('home.params.tip') }, api.t('home.params.show')),
			E('div', { 'class': 'dps-muted' }, api.t('router.strategy.roles')),
			E('pre', { 'class': 'dps-pre' }, text),
			E('div', { 'class': 'dps-actions' }, [api.btn(api.t('home.params.copy'), function() {
				api.copy(text);
				msg.textContent = api.t('router.strategy.copied', { name: api.stratTitle(it.title) });
			}), msg])
		]);
		api.content(this.el.params, det);
	},

	setOrder: function(v) {
		this.order = v;
		try { window.localStorage.setItem('dpishell-order', v); } catch (e) {}
		this.fillStrategies();
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
		var st = this.stateText();
		this.el.state.textContent = st.title;
		this.el.stateSub.textContent = st.sub || '';
		this.el.dot.className = 'dps-dot dps-dot-' + st.kind;
		this.el.toggle.textContent = api.t(s.enabled ? 'web.main.disable' : 'router.enable');
		this.el.toggle.className = 'btn cbi-button dps-big ' + (s.enabled ? 'cbi-button-neutral' : 'cbi-button-positive');
		this.el.check.title = s.enabled ? '' : api.t('web.main.check_needs_on');
		this.el.toggle.disabled = busy || !s.core.installed;
		this.el.select.disabled = busy || !s.core.installed;
		this.el.check.disabled = busy || !s.enabled || j.check;
		this.el.strategy.disabled = busy;
		if (document.activeElement != this.el.strategy && !this.pending && this.el.strategy.value != (s.strategy || ''))
			this.fillStrategies();
		this.el.ts.style.display = (s.enabled && s.needs_tcp_timestamps) ? '' : 'none';

		var lc = s.last_check;
		if (lc && lc.at) {
			var groups = lc.groups || [];
			var okN = groups.filter(function(g) { return g.ok; }).length;
			var src = { manual: 'web.src.manual', timer: 'web.src.timer', app: 'web.src.app' }[lc.source];
			// Старый итог (больше часа) — серым и с возрастом: справочно, не текущая ошибка; «Проверить сейчас» — рядом.
			var old = api.stale(lc.at);
			// Итог проверки значками (ux-audit-web п.3) и строкой «Открывается N из M», как на ПК (матрица п. 4).
			api.content(this.el.lastCheck, [
				E('div', { 'class': 'dps-verdicts' + (old ? ' dps-muted' : '') }, groups.map(function(g) {
					return E('span', { 'class': old ? '' : g.ok ? 'dps-v-ok' : 'dps-v-bad' }, (g.ok ? '✓ ' : '✗ ') +
						(GROUP_KEYS[g.id] ? api.t(GROUP_KEYS[g.id]) : ({ youtube: 'YouTube', discord: 'Discord' }[g.id] || g.id)));
				})),
				E('div', { 'class': 'dps-muted' }, (groups.length ? api.t('home.check.summary', { ok: okN, total: groups.length }) + ' · ' : '') +
					api.t('web.main.checked', { time: old ? api.ago(lc.at) : api.time(lc.at) }) + (src ? ' ' + api.t(src) : ''))
			]);
		} else
			api.content(this.el.lastCheck, []);

		this.renderUpdate(s.update);
		// Отсчёт — от left_s, не от until: часы роутера и браузера могут расходиться.
		this.confirmDeadline = s.pending_confirm ? Date.now() + (s.pending_confirm.left_s || 0) * 1000 : 0;
		this.renderConfirm();

		// strategy_failing держится на last_check (ручная проверка не снимает его до следующей): итог старше часа — не текущая ошибка.
		var lcOld = !!(lc && lc.at && api.stale(lc.at));
		var warns = (s.warnings || []).filter(function(w) { return !(w.code == 'strategy_failing' && lcOld); });
		var wj = JSON.stringify(warns);
		if (wj != this.lastWarnJson) {
			this.lastWarnJson = wj;
			this.renderWarnings(warns);
		}
	},

	renderConfirm: function() {
		var self = this, pc = this.status.pending_confirm;
		var left = pc ? Math.max(0, Math.round((this.confirmDeadline - Date.now()) / 1000)) : 0;
		if (!pc || !left) {
			this.el.confirm.style.display = 'none';
			return;
		}
		if (!this.el.confirm.firstChild)
			api.content(this.el.confirm, [this.el.confirmText, ' ',
				api.btn(api.t('router.trial.keep'), function() { return self.act('confirm'); }, 'cbi-button-positive')]);
		this.el.confirmText.textContent = api.t('web.main.trial_ask', { time: '%d:%02d'.format(Math.floor(left / 60), left % 60) });
		this.el.confirm.style.display = '';
	},

	// Плашка «Доступно обновление» (router-api.md §5): обновление — на вкладке «Информация и обновления».
	renderUpdate: function(u) {
		var key = JSON.stringify([u && u.available, u && u.version]);
		if (key == this.lastUpdateKey)
			return;
		this.lastUpdateKey = key;
		api.content(this.el.update, !u || !u.available ? [] : E('div', { 'class': 'alert-message notice' }, [
			E('div', { 'class': 'dps-warn' }, [
				E('b', {}, api.t('router.update.available', { version: (u.version || '').replace(/-r\d+$/, '') })),
				api.btn(api.t('router.details'), function() { api.go('about'); }, 'cbi-button-apply')
			])
		]));
	},

	renderWarnings: function(list) {
		var self = this;
		api.content(this.el.warnings, list.map(function(w) {
			var action = null;
			switch (w.code) {
			case 'dns_spoofed':
				action = api.btn(api.t('router.dns.enable'), function() { api.dnsEnableDialog(function() { api.go('dns'); }); }, 'cbi-button-apply');
				break;
			case 'conflict':
				// Останавливает по-настоящему (службы); пакет — только удалить, процесс — разбор в «Диагностике» (п. 49).
				action = api.btn(api.t('router.diag.stop'), function() { return self.stopConflicts(); }, 'cbi-button-apply');
				break;
			case 'core_missing':
				action = api.btn(api.t('web.core.download'), function() { return self.coreInstall(); }, 'cbi-button-apply');
				break;
			case 'auto_select_failed':
			case 'strategy_failing':
			case 'watchdog_select_failed':
				action = api.btn(api.t('router.select'), function() { return self.openSelect(); }, 'cbi-button-apply');
				break;
			case 'flow_offload':
				action = api.btn(api.t('router.warn.fix'), function() { return self.act('flow_offload_fix'); }, 'cbi-button-apply');
				break;
			case 'ipv6':
				action = api.btn(api.t('router.warn.fix'), function() { return self.act('ipv6_set', { enabled: true }); }, 'cbi-button-apply');
				break;
			case 'dns_fallback':
				action = api.btn(api.t('router.details'), function() { api.go('dns'); }, 'cbi-button-neutral');
				break;
			case 'watchdog_off':
			case 'button_off':
			case 'confirm_reverted':
				// «Включить» — главная кнопка панели выше; здесь только другой путь.
				action = api.btn(api.t('web.main.select_other'), function() { return self.openSelect(); }, 'cbi-button-neutral');
				break;
			}
			return E('div', { 'class': 'alert-message ' + (WARN_CLASS[w.code] || 'warning') },
				E('div', { 'class': 'dps-warn' }, [E('span', {}, api.msg(w) || w.code), action || '']));
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
			api.fail(self.el.err, e);
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
		if (!window.confirm(api.t('web.main.stop_confirm')))
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
						return api.t(c.kind == 'package' ? 'web.main.remove_pkg' : 'web.main.conflict_more', { name: c.name });
					}).join(' ');
				return self.act('status');
			});
		}).catch(function(e) { api.fail(self.el.err, e); });
	},

	checkNow: function() {
		return this.act('check_now').catch(function() {});
	},

	openSelect: function() {
		api.selectDialog(this.status, function() { api.go('select'); });
		return Promise.resolve();
	},

	// Выбранная, но не применённая стратегия (кнопка «Применить»).
	pend: function(value) {
		this.pending = (!value || value == (this.status.strategy || '')) ? null : value;
		this.el.apply.style.display = this.pending ? '' : 'none';
		this.renderParams();
	},

	cancelPending: function() {
		this.pending = null;
		this.el.apply.style.display = 'none';
		this.fillStrategies();
	},

	applyPending: function() {
		var self = this, id = this.pending;
		if (!id)
			return Promise.resolve();
		return this.act('strategy_set', { id: id, confirm_s: CONFIRM_S }).then(function() { self.cancelPending(); }, function() { self.cancelPending(); });
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
			self.el.schedMsg.textContent = api.t('web.saved');
			setTimeout(function() { self.el.schedMsg.textContent = ''; }, 2000);
		}, function(e) {
			api.fail(self.el.schedMsg, e);
			self.fillSchedule();
		});
	},

	coreInstall: function() {
		return this.act('core_install').catch(function() {});
	}
});
