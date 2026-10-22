'use strict';
'require view';
'require poll';
'require dpishell.api as api';

// Строки — только текстом: свой E вместо LuCI (api.js, tools/xss-check.py).
var E = api.E;

// Вкладка «Информация и обновления»: версии (пакет, страница, ядро, договор), вся работа с обновлением DpiShell; что внутри и где код.
// Плашка «Доступно обновление» на других вкладках ведёт сюда. Тексты — как на такой же вкладке окна ПК.
// Внизу — свои переводы, которые не взяты (api.langProblems).

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return api.lang().then(function() { return api.safe('status'); });
	},

	render: function(s) {
		var self = this;
		this.status = s;
		if (s.__error)
			return E('div', {}, [E('h2', {}, api.t('about.title')), api.card(null, [api.errorLine(s.__error)])]);

		this.el = {
			versions: E('div'),
			update: E('div'),
			err: E('div', { 'class': 'dps-error' }),
			check: api.btn(api.t('router.update.check'), function() { return self.checkUpdate(); }, 'cbi-button-neutral')
		};
		var link = function(href, text) { return E('a', { href: href, target: '_blank', rel: 'noopener' }, text); };
		var view = E('div', {}, [
			E('h2', {}, api.t('about.title')),
			api.card(api.t('web.about.version'), [
				this.el.versions,
				this.el.update,
				E('div', { 'class': 'dps-actions' }, [this.el.check]),
				this.el.err
			]),
			api.card(api.t('web.about.info'), [
				api.line([api.t('web.about.based_on') + ' ', link('https://github.com/bol-van/zapret', 'zapret'), ' (bol-van).']),
				api.line([link('https://github.com/Slavdew/dpishell', 'github.com/Slavdew/dpishell'), ' · ',
					link('https://github.com/Slavdew/dpishell/blob/master/docs/router/README.md', api.t('web.about.guide'))]),
				api.line(api.t('web.about.license'), 'dps-muted')
			]),
			this.langCard()
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
			core = E('span', { 'class': 'spinning' }, api.t('web.core.loading'));
		else if (!c.installed)
			core = E('span', {}, [api.t('router.core.not_installed') + ' ',
				api.btn(api.t('web.core.download'), function() { return self.act('core_install'); }, 'cbi-button-apply')]);
		else
			core = (c.installed_version || c.version || '—') + (c.hash_ok === false ? ' — ' + api.t('web.core.broken_short') : '');
		api.content(this.el.versions, [
			api.row('DpiShell', E('span', {}, p.dpishell || s.version || '—')),
			api.row(api.t('web.about.luci'), E('span', {}, p['luci-app-dpishell'] || '—')),
			api.row(api.t('web.core.title'), core),
			api.row(api.t('web.about.api'), E('span', {}, s.api ? 'api ' + s.api : '—'))
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
			api.content(this.el.update, E('div', { 'class': 'alert-message notice' }, [
				E('span', { 'class': 'spinning' }, api.t('web.about.updating')), this.el.updateStep]));
			return;
		}
		if (running) {
			api.content(this.el.update, E('div', { 'class': 'dps-line' }, E('span', { 'class': 'spinning' }, api.t('web.about.checking'))));
			return;
		}
		if (!u || !u.available) {
			api.content(this.el.update, api.line(u && u.checked_at ? api.t('web.about.no_updates', { time: api.time(u.checked_at) }) : api.t('web.about.daily'), 'dps-muted'));
			return;
		}
		var notes = u.notes || [];
		api.content(this.el.update, E('div', { 'class': 'alert-message notice' }, [
			E('div', { 'class': 'dps-warn' }, [
				E('span', {}, [E('b', {}, api.t('router.update.available', { version: (u.version || '').replace(/-r\d+$/, '') })),
					notes.length ? E('ul', { 'style': 'margin:.3em 0 0 1.2em' }, notes.map(function(n) { return E('li', {}, n); })) : '']),
				api.btn(api.t('web.about.update_to', { version: (u.version || '').replace(/-r\d+$/, '') }), function() { return self.updateInstall(); }, 'cbi-button-apply')
			])
		]));
	},

	// Свои переводы с ошибками (api.langProblems) — видно, почему файл или строки не взяты.
	langCard: function() {
		var probs = api.langProblems();
		if (!probs.length)
			return '';
		return api.card(api.t('web.about.lang_problems'), [
			api.line(api.t('web.about.lang_problems_hint'), 'dps-muted'),
			E('ul', { 'class': 'dps-list' }, probs.map(function(p) { return E('li', {}, p); }))
		]);
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
					var step = s.__error ? 'restart' : (j && j.step);
					self.el.updateStep.textContent = (['download', 'install', 'restart'].indexOf(step) >= 0
						? api.t('router.update.step.' + step) + (step == 'install' && j && j.target ? ' ' + j.target : '')
						: api.t('web.about.preparing')) + ' · ' + api.duration(sec);
					if (s.__error && Date.now() - started < 300000)
						return;
					if (!s.__error && s.jobs && s.jobs.update)
						return;
					window.clearInterval(timer);
					var job = !s.__error && s.update && s.update.job;
					if (job && job.ok === false) {
						self.updating = false;
						self.status = s;
						self.el.err.textContent = api.msg(job.message_msg || job.message);
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
