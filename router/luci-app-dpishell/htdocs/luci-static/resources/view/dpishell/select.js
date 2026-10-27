'use strict';
'require view';
'require ui';
'require poll';
'require dpishell.api as api';

// Строки — только текстом: свой E вместо LuCI (api.js, tools/xss-check.py).
var E = api.E;

// Вкладка «Подбор» (architecture.md «Подбор на роутере»): запуск, ход, итог. Названия — как в окне ПК (матрица ПК×роутер).

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return api.lang().then(function() {
			return Promise.all([api.safe('status'), api.safe('select_progress')]);
		});
	},

	// Ход подбора: роутер с rows_total в ответе (0.2.2) отдаёт строки с номера since — к полученным досылаются новые,
	// а не весь select.json раз в секунду; старый роутер (без rows_total) — весь список, как раньше.
	fetchProgress: function() {
		var prev = this.progress, args = null;
		if (prev && !prev.__error && prev.rows_total != null && Array.isArray(prev.rows))
			args = { since: prev.rows.length, run: prev.run };
		return api.safe('select_progress', args).then(function(p) {
			if (args && !p.__error && p.rows_from > 0)
				p.rows = prev.rows.slice(0, p.rows_from).concat(p.rows || []);
			return p;
		});
	},

	render: function(data) {
		this.status = data[0].__error ? null : data[0];
		this.progress = data[1];
		this.tickNo = 0;
		this.lastJson = null;

		this.el = {
			head: E('div', { 'class': 'dps-state' }),
			actions: E('div', { 'class': 'dps-actions' }),
			err: E('div', { 'class': 'dps-error' }),
			body: E('div')
		};

		var view = E('div', {}, [
			E('h2', {}, api.t('select.title')),
			api.card(null, [
				api.line([api.t('web.select.intro'), api.info(api.t('web.select.intro_info'))], 'dps-muted'),
				this.el.head,
				this.el.actions,
				this.el.err,
				this.el.body
			])
		]);

		this.update();
		poll.add(L.bind(this.tick, this), 1);
		return view;
	},

	running: function() {
		return !!(this.progress && !this.progress.__error && this.progress.running);
	},

	tick: function() {
		var self = this;
		// Скрытая вкладка — без опроса; в покое — раз в 10 с, пока идёт подбор — раз в 1 с.
		if (document.hidden) {
			this.wasHidden = true;
			return Promise.resolve();
		}
		this.tickNo++;
		if (!this.running() && !this.wasHidden && (this.tickNo % 10) != 0)
			return Promise.resolve();
		this.wasHidden = false;
		return Promise.all([api.safe('status'), this.fetchProgress()]).then(function(d) {
			if (!d[0].__error)
				self.status = d[0];
			self.progress = d[1];
			self.update();
		});
	},

	startBtn: function() {
		var self = this;
		return api.btn(api.t('select.start'), function() { return self.start(); }, 'cbi-button-apply');
	},

	// «Искать глубже» — после результата, если готовые стратегии открыли не всё (матрица п. 25, как на ПК).
	deepBtn: function(p) {
		var self = this;
		var b = api.btn(api.t('select.deep'), function() { return self.startDeep(p); }, 'cbi-button-neutral');
		b.title = api.t('select.deep.tip');
		return b;
	},

	update: function() {
		var p = this.progress;
		var json = JSON.stringify([p, this.status && this.status.enabled]);
		if (json == this.lastJson)
			return;
		this.lastJson = json;
		var self = this;

		if (p.__error) {
			this.el.head.textContent = api.t('web.select.not_started');
			api.content(this.el.actions, [this.startBtn()]);
			api.content(this.el.body, api.errorLine(p.__error));
			return;
		}

		if (p.running) {
			var eta = p.eta_s != null ? ' · ' + (p.eta_s < 60 ? api.t('router.eta.under_minute') : api.t('router.eta.minutes', { n: Math.round(p.eta_s / 60) })) : '';
			var head = api.t(p.depth == 'deep' ? 'select.busy.deep' : 'select.busy.select');
			if (p.total)
				head += ' ' + api.t('router.select.progress', { index: Math.min(p.index || 1, p.total), total: p.total });
			this.el.head.textContent = head + eta;
			api.content(this.el.actions, [api.btn(api.t('select.stop'), function() { return self.cancel(); }, 'cbi-button-remove')]);
			var m = api.msg(p.message_msg || p.message);
			api.content(this.el.body, [
				m ? api.line(m, 'dps-muted') : '',
				this.table(p, false)
			]);
			return;
		}

		if (!p.finished_at && !(p.rows && p.rows.length)) {
			this.el.head.textContent = api.t('diag.results.none');
			api.content(this.el.actions, [this.startBtn()]);
			api.content(this.el.body, []);
			return;
		}

		var when = api.time(p.finished_at), headText, sub = null;
		var msg = api.msg(p.message_msg || p.message);
		if (p.cancelled) {
			headText = api.t('web.select.stopped', { time: when });
			sub = api.t('router.select.stopped_hint');
		} else if (p.ids && p.ids.length)
			headText = api.t('web.select.checked_one', { time: when, name: this.title(p, p.ids[0]) });
		else if (p.best)
			headText = api.t('web.select.done', { time: when }) + ' ' + api.t(p.applied ? 'web.select.result_applied' : 'router.select.result_best', { name: this.title(p, p.best) });
		else if (!(p.rows && p.rows.length) && msg)
			headText = api.t('web.select.done', { time: when }) + ' ' + msg;
		else
			headText = api.t('web.select.done', { time: when }) + ' ' + api.t('select.result.none');
		this.el.head.textContent = headText;

		// Готовые не открыли всё — «Искать глубже» (после глубокого поиска — не предлагать снова).
		var actions = [this.startBtn()];
		if (!p.cancelled && p.depth != 'deep' && !(p.ids && p.ids.length) && (p.rows || []).length && !this.allOpened(p))
			actions.push(this.deepBtn(p));
		api.content(this.el.actions, actions);

		// Стратегии не проверялись (блокировок нет, подбор прервался) — без пустой таблицы: «завершён» и «пока ни одна
		// не проверена» противоречили друг другу (ux-audit-web п.12).
		if (!(p.rows && p.rows.length) && !p.best) {
			api.content(this.el.body, sub ? api.line(sub, 'dps-muted') : []);
			return;
		}

		var kids = [];
		if (sub)
			kids.push(api.line(sub, 'dps-muted'));
		if (msg && !p.best && !p.cancelled)
			kids.push(api.line(msg, 'dps-muted'));
		if (p.backups && p.backups.length)
			kids.push(api.line(api.t('router.apply.backups', { list: p.backups.map(function(id) { return self.title(p, id); }).join(', ') }), 'dps-muted'));
		kids.push(this.table(p, !p.cancelled));
		api.content(this.el.body, kids);
	},

	// Лучшая открыла все группы — глубже искать незачем.
	allOpened: function(p) {
		var best = (p.rows || []).filter(function(r) { return r.id == p.best; })[0];
		if (!best)
			return false;
		return best.groups_total != null ? best.groups_ok == best.groups_total : best.group_share >= 1;
	},

	title: function(p, id) {
		var rows = p.rows || [];
		for (var i = 0; i < rows.length; i++)
			if (rows[i].id == id)
				return api.stratTitle(rows[i].title || id);
		return id;
	},

	// Глубокий поиск: кандидаты (candidate: true) — отдельным свёрнутым списком, во время поиска — последние 8.
	table: function(p, withApply) {
		var all = p.rows || [];
		this.ipBlocked = this.allTimeouts(all);
		var cands = all.filter(function(r) { return r.candidate; });
		if (!cands.length)
			return this.rowsTable(p, all, withApply);
		var full = all.filter(function(r) { return !r.candidate; });
		var parts = [];
		if (full.length)
			parts.push(this.rowsTable(p, full, withApply));
		if (p.running && !full.length)
			parts.push(this.rowsTable(p, cands.slice(-8), false));
		else
			parts.push(E('details', { 'class': 'dps-muted', 'style': 'margin-top:.5em' }, [
				E('summary', {}, api.t('web.select.candidates', { n: cands.length })),
				this.rowsTable(p, cands, false)
			]));
		return E('div', {}, parts);
	},

	// Адреса, что не ответили (таймаут на всех версиях TLS) ни в одной полной проверке, — хотя бы в двух: стратегией такое
	// обычно не лечится, похоже на блокировку по адресу. Кандидаты поиска исходов по адресам не отдают — судим по полным.
	allTimeouts: function(rows) {
		var full = rows.filter(function(r) { return !r.candidate && r.verdicts && r.verdicts.length; });
		var res = {};
		if (full.length < 2)
			return res;
		full[0].verdicts.forEach(function(v) {
			var dead = full.every(function(r) {
				return r.verdicts.some(function(x) { return x.url == v.url && !x.ok && x.outcome == 'timeout'; });
			});
			if (dead)
				res[v.url] = true;
		});
		return res;
	},

	host: function(url) {
		return (url.match(/^https?:\/\/([^\/]+)/) || [])[1] || url;
	},

	// «Что не так» — коротко: какие адреса не открылись (подробности — «Подробнее» в строке стратегии).
	problem: function(r) {
		var self = this;
		if (r.error)
			return E('span', { 'class': 'dps-error' }, api.msg(r.error_msg || r.error));
		var bad = (r.verdicts || []).filter(function(v) { return !v.ok; });
		if (!bad.length)
			return '';
		var names = bad.slice(0, 3).map(function(v) { return self.host(v.url); }).join(', ');
		return E('span', { 'class': 'dps-error' }, bad.length > 3 ? api.t('web.select.problem_more', { list: names, n: bad.length - 3 }) : api.t('web.select.problem', { list: names }));
	},

	// «Подробнее» — исход по каждой цели (select_progress rows[].verdicts): строкой на всю ширину под строкой стратегии,
	// чтобы список не раздувал столбец «Стратегия» и не сжимал числа. Раскрытое не сворачивается при опросе.
	details: function(r) {
		var self = this;
		if (!r.verdicts || !r.verdicts.length)
			return null;
		this.openDetails = this.openDetails || {};
		var open = !!this.openDetails[r.id];
		var row = E('tr', { 'class': 'tr dps-more-row', hidden: open ? null : '' }, [
			E('td', { 'class': 'td', colspan: 6 }, E('ul', { 'class': 'dps-list' }, r.verdicts.slice().sort(function(a, b) { return a.ok - b.ok; }).map(function(v) {
				var text = api.t(v.ok ? 'web.select.v_ok' : 'web.select.v_fail', { host: self.host(v.url), detail: api.msg(v.detail_msg || v.detail) });
				if (v.seconds)
					text += ', ' + api.t('router.row.seconds', { value: v.seconds.toFixed(1) });
				if (!v.ok && self.ipBlocked && self.ipBlocked[v.url])
					text += ' — ' + api.t('web.select.ip_blocked');
				return E('li', { 'class': v.ok ? '' : 'dps-error' }, text);
			})))
		]);
		var toggle = E('button', { 'type': 'button', 'class': 'dps-more', 'aria-expanded': open ? 'true' : 'false', click: function() {
			open = !open;
			self.openDetails[r.id] = open;
			if (open)
				row.removeAttribute('hidden');
			else
				row.setAttribute('hidden', '');
			toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
		} }, api.t('common.more'));
		return { toggle: toggle, row: row };
	},

	// Ширина: числа и заголовки — по содержимому, без переноса (dps-num); «Что не так» и название переносятся;
	// отметка «лучшая» и кнопки — одним узким столбцом. На телефоне строка — карточкой (CSS dps-sel в api.js).
	rowsTable: function(p, rows, withApply) {
		var self = this;
		if (!rows.length)
			return E('div', { 'class': 'dps-muted' }, api.t('web.select.none_yet'));
		var cols = [api.t('select.col.strategy'), api.t('select.col.services'), api.t('select.col.addresses'), api.t('select.col.time'), api.t('select.col.problem')];
		var kind = ['dps-strat', 'dps-num', 'dps-num', 'dps-num', 'dps-prob'];
		var t = E('table', { 'class': 'table dps-table dps-sel' }, [
			E('tr', { 'class': 'tr table-titles' }, cols.map(function(c, i) { return E('th', { 'class': 'th ' + kind[i] }, c); }).concat([E('th', { 'class': 'th dps-act' }, '')]))
		]);
		rows.forEach(function(r) {
			var isBest = p.best && r.id == p.best;
			var last = [];
			if (isBest)
				last.push(E('span', { 'class': 'dps-ok' }, api.t(p.applied ? 'web.select.applied' : 'web.select.best')));
			else if (withApply && !r.candidate && r.group_share > 0)
				last.push(api.btn(api.t('web.apply'), function() { return self.apply(p, r.id); }, 'cbi-button-neutral'));
			// «Проверить только её» — как на ПК (матрица п. 29): только для готовых стратегий, не для кандидатов поиска.
			if (withApply && !r.candidate && !p.running)
				last.push(api.btn(api.t('web.select.check_one'), function() { return self.checkOne(r.id); }, 'cbi-button-neutral'));
			var more = self.details(r);
			t.appendChild(E('tr', { 'class': 'tr' + (more ? ' dps-has-more' : '') }, [
				E('td', { 'class': 'td dps-strat', 'data-title': cols[0] }, [
					isBest ? E('b', {}, api.stratTitle(r.title || r.id)) : api.stratTitle(r.title || r.id),
					r.needs_ts ? E('div', { 'class': 'dps-muted', 'style': 'font-size:.85em' }, api.t('web.select.needs_ts')) : '',
					more ? E('div', {}, more.toggle) : ''
				]),
				// «N из M» (groups_ok/groups_total, роутер с r8); у старых — доля.
				E('td', { 'class': 'td dps-num', 'data-title': cols[1] }, r.groups_total != null ? api.t('router.row.groups_of', { ok: r.groups_ok, total: r.groups_total }) : api.percent(r.group_share)),
				E('td', { 'class': 'td dps-num', 'data-title': cols[2] }, api.percent(r.target_share)),
				E('td', { 'class': 'td dps-num', 'data-title': cols[3] }, r.avg_s != null ? api.t('router.row.seconds', { value: r.avg_s.toFixed(1) }) : '—'),
				E('td', { 'class': 'td dps-prob', 'data-title': cols[4] }, self.problem(r)),
				E('td', { 'class': 'td dps-act' }, last.length ? E('div', { 'class': 'dps-act-box' }, last) : '')
			]));
			if (more)
				t.appendChild(more.row);
		});
		return t;
	},

	start: function() {
		var self = this;
		this.el.err.textContent = '';
		api.selectDialog(this.status, function() {
			self.lastJson = null;
			return self.tick();
		});
		return Promise.resolve();
	},

	run: function(args) {
		var self = this;
		this.el.err.textContent = '';
		return api.call('select_start', args).then(function() {
			self.lastJson = null;
			return self.tick();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	},

	// Глубже — с теми же группами, что прошлый подбор.
	startDeep: function(p) {
		return this.run({ groups: p.groups && p.groups.length ? p.groups : ['youtube', 'discord', 'user'], mode: 'full', depth: 'deep' });
	},

	checkOne: function(id) {
		var p = this.progress || {};
		return this.run({ groups: p.groups && p.groups.length ? p.groups : ['youtube', 'discord', 'user'], mode: 'full', ids: [id] });
	},

	cancel: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('select_cancel').then(function() {
			self.lastJson = null;
			return self.tick();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	},

	apply: function(p, id) {
		var self = this;
		this.el.err.textContent = '';
		var backups = (p.backups || []).filter(function(b) { return b != id; });
		if (p.best && p.best != id && backups.indexOf(p.best) < 0)
			backups.unshift(p.best);
		return api.call('apply', { id: id, backups: backups.slice(0, 3) }).then(function() {
			self.lastJson = null;
			return self.tick();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	}
});
