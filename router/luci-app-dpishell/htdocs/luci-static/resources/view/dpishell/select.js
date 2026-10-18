'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «Подбор» (architecture.md «Подбор на роутере»): запуск, ход, итог.

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return Promise.all([api.safe('status'), api.safe('select_progress')]);
	},

	render: function(data) {
		var self = this;
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
			E('h2', {}, 'Подбор стратегии'),
			api.card(null, [
				api.line(['Роутер перебирает стратегии и проверяет, открываются ли YouTube, Discord и свои адреса. Страницу можно закрыть — подбор идёт на роутере. Закончившийся подбор сам включает лучшую стратегию.',
					api.info('Полный режим проверяет все стратегии и показывает таблицу; быстрый останавливается на первой, у которой всё открылось. Глубокий поиск придумывает новые стратегии, когда готовые не помогли, и занимает 5–30 минут. Во время подбора обход для всей сети несколько раз переключается — сайты и игры будут прерываться.')], 'dps-muted'),
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
		this.tickNo++;
		if (!this.running() && (this.tickNo % 3) != 0)
			return Promise.resolve();
		return Promise.all([api.safe('status'), api.safe('select_progress')]).then(function(d) {
			if (!d[0].__error)
				self.status = d[0];
			self.progress = d[1];
			self.update();
		});
	},

	update: function() {
		var p = this.progress;
		var json = JSON.stringify([p, this.status && this.status.enabled]);
		if (json == this.lastJson)
			return;
		this.lastJson = json;
		var self = this;

		if (p.__error) {
			this.el.head.textContent = 'Подбор не запущен';
			dom.content(this.el.actions, [api.btn('Подобрать', function() { return self.start(); }, 'cbi-button-apply')]);
			dom.content(this.el.body, api.errorLine(p.__error));
			return;
		}

		if (p.running) {
			var eta = p.eta_s != null ? ', осталось около ' + api.duration(p.eta_s) : '';
			this.el.head.textContent = 'Подбор… ' + (p.total ? '%d из %d'.format(Math.min(p.index || 1, p.total), p.total) : '') +
				(p.depth == 'deep' ? ' (глубокий поиск)' : '') + eta;
			dom.content(this.el.actions, [api.btn('Остановить', function() { return self.cancel(); }, 'cbi-button-remove')]);
			dom.content(this.el.body, [
				p.message ? api.line(p.message, 'dps-muted') : '',
				this.table(p, false)
			]);
			return;
		}

		dom.content(this.el.actions, [api.btn('Подобрать', function() { return self.start(); }, 'cbi-button-apply')]);
		if (!p.finished_at && !(p.rows && p.rows.length)) {
			this.el.head.textContent = 'Подбор ещё не запускали';
			dom.content(this.el.body, []);
			return;
		}

		var head;
		if (p.cancelled)
			head = 'Подбор остановлен ' + api.time(p.finished_at) + ' — прежние настройки возвращены';
		else if (p.best)
			head = 'Подбор завершён ' + api.time(p.finished_at) + ': ' + (p.applied ? 'включена ' : 'лучшая — ') + this.title(p, p.best);
		else if (!(p.rows && p.rows.length) && p.message)
			head = 'Подбор завершён ' + api.time(p.finished_at) + ': ' + p.message;
		else
			head = 'Подбор завершён ' + api.time(p.finished_at) + ': ни одна стратегия не открыла все сайты';
		this.el.head.textContent = head;
		// Стратегии не проверялись (блокировок нет, подбор прервался) — без пустой таблицы: «завершён» и «пока ни одна
		// не проверена» противоречили друг другу (ux-audit-web п.12).
		if (!(p.rows && p.rows.length) && !p.best) {
			dom.content(this.el.body, []);
			return;
		}

		var kids = [];
		if (p.message && !p.best)
			kids.push(api.line(p.message, 'dps-muted'));
		if (p.backups && p.backups.length)
			kids.push(api.line('Запасные: ' + p.backups.map(function(id) { return self.title(p, id); }).join(', '), 'dps-muted'));
		kids.push(this.table(p, !p.cancelled));
		dom.content(this.el.body, kids);
	},

	title: function(p, id) {
		var rows = p.rows || [];
		for (var i = 0; i < rows.length; i++)
			if (rows[i].id == id)
				return rows[i].title || id;
		return id;
	},

	// Глубокий поиск: кандидаты (candidate: true) — отдельным свёрнутым списком, во время поиска — последние 8.
	table: function(p, withApply) {
		var all = p.rows || [];
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
				E('summary', {}, 'Проверено вариантов: %d'.format(cands.length)),
				this.rowsTable(p, cands, false)
			]));
		return E('div', {}, parts);
	},

	// «Подробнее» — исход по каждой цели (select_progress rows[].verdicts); раскрытое не сворачивается при опросе.
	details: function(r) {
		var self = this;
		if (!r.verdicts || !r.verdicts.length)
			return '';
		this.openDetails = this.openDetails || {};
		var d = E('details', { 'class': 'dps-details', open: this.openDetails[r.id] ? '' : null, toggle: function() { self.openDetails[r.id] = d.open; } }, [
			E('summary', {}, 'Подробнее'),
			E('ul', { 'class': 'dps-list' }, r.verdicts.slice().sort(function(a, b) { return a.ok - b.ok; }).map(function(v) {
				var host = (v.url.match(/^https?:\/\/([^\/]+)/) || [])[1] || v.url;
				return E('li', { 'class': v.ok ? '' : 'dps-error' }, (v.ok ? 'да' : 'нет') + ' — ' + host + ': ' + v.detail +
					(v.seconds ? ', ' + v.seconds.toFixed(1) + ' с' : ''));
			}))
		]);
		return d;
	},

	rowsTable: function(p, rows, withApply) {
		var self = this;
		if (!rows.length)
			return E('div', { 'class': 'dps-muted' }, 'Пока ни одна стратегия не проверена');
		var t = E('table', { 'class': 'table dps-table' }, [
			E('tr', { 'class': 'tr table-titles' }, [
				E('th', { 'class': 'th' }, 'Стратегия'),
				E('th', { 'class': 'th' }, 'Группы'),
				E('th', { 'class': 'th' }, 'Сайты'),
				E('th', { 'class': 'th' }, 'Время'),
				E('th', { 'class': 'th' }, '')
			])
		]);
		rows.forEach(function(r) {
			var isBest = p.best && r.id == p.best;
			var last;
			if (r.error)
				last = E('span', { 'class': 'dps-error' }, r.error);
			else if (isBest)
				last = E('span', { 'class': 'dps-ok' }, p.applied ? 'включена' : 'лучшая');
			else if (withApply && !r.candidate && r.group_share > 0)
				last = api.btn('Выбрать другую', function() { return self.apply(p, r.id); }, 'cbi-button-neutral');
			else
				last = '';
			t.appendChild(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'data-title': 'Стратегия' }, [
					isBest ? E('b', {}, r.title || r.id) : (r.title || r.id),
					r.needs_ts ? E('div', { 'class': 'dps-muted', 'style': 'font-size:.85em' }, 'компьютерам на Windows нужна настройка') : '',
					self.details(r)
				]),
				E('td', { 'class': 'td', 'data-title': 'Группы' }, api.percent(r.group_share)),
				E('td', { 'class': 'td', 'data-title': 'Сайты' }, api.percent(r.target_share)),
				E('td', { 'class': 'td', 'data-title': 'Время' }, r.avg_s != null ? r.avg_s.toFixed(1) + ' с' : '—'),
				E('td', { 'class': 'td' }, last)
			]));
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

	cancel: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('select_cancel').then(function() {
			self.lastJson = null;
			return self.tick();
		}, function(e) {
			self.el.err.textContent = api.text(e);
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
			self.el.err.textContent = api.text(e);
		});
	}
});
