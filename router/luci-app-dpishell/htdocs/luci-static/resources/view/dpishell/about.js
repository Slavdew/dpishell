'use strict';
'require view';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «Информация и обновления»: версии (пакет, страница, ядро, договор), вся работа с обновлением DpiShell; что внутри и где код.
// Плашка «Доступно обновление» на других вкладках ведёт сюда. Тексты — как на такой же вкладке окна ПК.

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return api.safe('status');
	},

	render: function(s) {
		var self = this;
		this.status = s;
		if (s.__error)
			return E('div', {}, [E('h2', {}, 'Информация и обновления'), api.card(null, [api.errorLine(s.__error)])]);

		this.el = {
			versions: E('div'),
			update: E('div'),
			err: E('div', { 'class': 'dps-error' }),
			check: api.btn('Проверить обновления', function() { return self.checkUpdate(); }, 'cbi-button-neutral')
		};
		var link = function(href, text) { return E('a', { href: href, target: '_blank', rel: 'noopener' }, text); };
		var view = E('div', {}, [
			E('h2', {}, 'Информация и обновления'),
			api.card('Версия', [
				this.el.versions,
				this.el.update,
				E('div', { 'class': 'dps-actions' }, [this.el.check]),
				this.el.err
			]),
			api.card('Информация', [
				api.line(['DpiShell на основе ', link('https://github.com/bol-van/zapret', 'zapret'), ' (bol-van).']),
				api.line([link('https://github.com/Slavdew/dpishell', 'github.com/Slavdew/dpishell'), ' · ',
					link('https://github.com/Slavdew/dpishell/blob/master/docs/router/README.md', 'Инструкция для роутера')]),
				api.line('Лицензия MIT.', 'dps-muted')
			])
		]);
		this.render2();
		poll.add(L.bind(this.tick, this), 3);
		return view;
	},

	tick: function() {
		var self = this;
		if (this.updating)
			return Promise.resolve();
		return api.safe('status').then(function(s) {
			if (s.__error)
				return;
			self.status = s;
			self.render2();
		});
	},

	// Версии строками: пакеты (status.packages, с r8), ядро (core.installed_version, с r8; раньше — только закреплённая версия).
	render2: function() {
		var self = this, s = this.status, p = s.packages || {}, c = s.core || {}, j = s.jobs || {};
		var core;
		if (j.core_install)
			core = E('span', { 'class': 'spinning' }, 'Загружаем ядро zapret…');
		else if (!c.installed)
			core = E('span', {}, ['не установлено ',
				api.btn('Загрузить ядро', function() { return self.act('core_install'); }, 'cbi-button-apply')]);
		else
			core = (c.installed_version || c.version || '—') + (c.hash_ok === false ? ' — файлы повреждены, «Восстановить» на вкладке «Диагностика»' : '');
		dom.content(this.el.versions, [
			api.row('DpiShell', E('span', {}, p.dpishell || s.version || '—')),
			api.row('Страница LuCI', E('span', {}, p['luci-app-dpishell'] || '—')),
			api.row('Ядро zapret (bol-van)', core),
			api.row('Договор с программой на ПК', E('span', {}, s.api ? 'api ' + s.api : '—'))
		]);
		// update_check запускает задание в фоне: пока оно не взяло замок, jobs.update ещё false — ждём смены checked_at (до минуты).
		var ck = this.checking, at = s.update && s.update.checked_at;
		if (ck && (at != ck.was || Date.now() - ck.since > 60000))
			ck = this.checking = null;
		var running = !!(j.update || ck);
		this.el.check.disabled = running;
		this.renderUpdate(s.update, running);
	},

	renderUpdate: function(u, running) {
		var self = this;
		if (this.updating) {
			this.el.updateStep = this.el.updateStep || E('div', { 'class': 'dps-muted' });
			dom.content(this.el.update, E('div', { 'class': 'alert-message notice' }, [
				E('span', { 'class': 'spinning' }, 'Обновляю DpiShell… страница перезагрузится сама'), this.el.updateStep]));
			return;
		}
		if (running) {
			dom.content(this.el.update, E('div', { 'class': 'dps-line' }, E('span', { 'class': 'spinning' }, 'Проверяю обновления…')));
			return;
		}
		if (!u || !u.available) {
			dom.content(this.el.update, api.line(u && u.checked_at ? 'Обновлений нет · проверено ' + api.time(u.checked_at) : 'Новая версия проверяется раз в сутки.', 'dps-muted'));
			return;
		}
		var notes = u.notes || [];
		dom.content(this.el.update, E('div', { 'class': 'alert-message notice' }, [
			E('div', { 'class': 'dps-warn' }, [
				E('span', {}, [E('b', {}, 'Доступна версия ' + (u.version || '').replace(/-r\d+$/, '')),
					notes.length ? E('ul', { 'style': 'margin:.3em 0 0 1.2em' }, notes.map(function(n) { return E('li', {}, n); })) : '']),
				api.btn('Обновить', function() { return self.updateInstall(); }, 'cbi-button-apply')
			])
		]));
	},

	act: function(method) {
		var self = this;
		this.el.err.textContent = '';
		return api.call(method).then(function() { return self.tick(); }, function(e) { self.el.err.textContent = api.text(e); });
	},

	checkUpdate: function() {
		this.checking = { since: Date.now(), was: this.status.update && this.status.update.checked_at };
		this.render2();
		return this.act('update_check');
	},

	updateInstall: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('update_install', { from: 'feed' }).then(function() {
			self.updating = true;
			self.renderUpdate();
			poll.stop();
			// Пакет меняет сам себя и перезапускает rpcd: ошибки ответа во время обновления — пережидаем.
			var started = Date.now();
			var timer = window.setInterval(function() {
				api.safe('status').then(function(s) {
					var sec = Math.round((Date.now() - started) / 1000);
					var j = !s.__error && s.update && s.update.job;
					// Ход (update_job.step); пока rpcd перезапускается, ответа нет — это и есть перезапуск.
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
						self.render2();
						poll.start();
						return;
					}
					window.location.reload();
				});
			}, 2000);
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	}
});
