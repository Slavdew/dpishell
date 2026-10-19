'use strict';
'require view';
'require ui';
'require dom';
'require dpishell.api as api';

// Вкладка «Сайты» (architecture.md «Режимы роутера»): свои настройки для сайтов (продвинутый режим), свои исключения,
// стандартные — просмотр, свои сайты (только в режиме «Только из списков»), свои адреса для проверки.

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return Promise.all([
			api.safe('status'),
			api.safe('lists_get', { name: 'user-exclude' }),
			api.safe('lists_get', { name: 'exclude' }),
			api.safe('lists_get', { name: 'user-general' }),
			api.safe('targets_get'),
			api.safe('advanced_get'),
			api.safe('strategies')
		]);
	},

	render: function(data) {
		var status = data[0].__error ? {} : data[0];
		var listsMode = status.mode == 'lists';

		return E('div', {}, [
			E('h2', {}, 'Сайты'),
			this.advancedEditor(data[5], data[6].__error ? { items: [] } : data[6]),
			this.listEditor('user-exclude', 'Исключения', data[1],
				['Сайты, которые оптимизация ломает: к ним она применяться не будет. Один адрес на строку, например example.com.',
					api.info('Строка example.com закрывает и все поддомены (video.example.com). Чтобы исключить только сам домен без поддоменов, начните строку с ^: ^example.com. Сюда попадают сайты, добавленные кнопкой «Сайт не открывается с оптимизацией» на главной.')]),
			listsMode ? this.listEditor('user-general', 'Свои сайты для оптимизации', data[3],
				['Сайты, для которых нужна оптимизация, кроме YouTube и Discord. Один адрес на строку. Действует в режиме «Только из списков».',
					api.info('Домены из «Своих адресов» ниже попадают сюда автоматически при проверке. Не добавляйте сайты, которые и так открываются: оптимизация для них ничего не улучшит, а в редких случаях может помешать.')]) : '',
			this.readOnlyList('Исключения по умолчанию', data[2],
				'Сайты, которым оптимизация известно мешает (банки, госуслуги и другие). Список приходит с обновлениями пакета, править его нельзя.'),
			this.targetsEditor(data[4])
		]);
	},

	// Редактор своего списка: текстовое поле, строка на домен, «Сохранить».
	listEditor: function(name, title, got, descr) {
		var self = this;
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var ta = E('textarea', { 'class': 'cbi-input-textarea dps-textarea', rows: 8, spellcheck: 'false' }, (got.lines || []).join('\n'));
		var msg = E('div', { 'class': 'dps-line' });
		var save = api.btn('Сохранить', function() {
			msg.className = 'dps-line';
			msg.textContent = '';
			var lines = ta.value.split('\n');
			return api.call('lists_set', { name: name, lines: lines }).then(function() {
				msg.className = 'dps-line dps-ok';
				msg.textContent = 'Сохранено';
			}, function(e) {
				msg.className = 'dps-line dps-error';
				var m = /^lines\[(\d+)\]$/.exec(e.field || '');
				if (m) {
					var i = parseInt(m[1], 10);
					msg.textContent = 'Строка ' + (i + 1) + ': ' + api.text(e);
					self.selectLine(ta, i);
				} else
					msg.textContent = api.text(e);
			});
		}, 'cbi-button-save');
		return api.card(title, [
			api.line(descr, 'dps-muted'),
			ta,
			E('div', { 'class': 'dps-actions' }, [save]),
			msg
		]);
	},

	// Подсветить строку i (с нуля): выделить её в поле и прокрутить к ней.
	selectLine: function(ta, i) {
		var lines = ta.value.split('\n'), start = 0;
		for (var k = 0; k < i && k < lines.length; k++)
			start += lines[k].length + 1;
		var end = start + (lines[i] != null ? lines[i].length : 0);
		ta.focus();
		ta.setSelectionRange(start, end);
		var lineHeight = parseFloat(getComputedStyle(ta).lineHeight) || 18;
		ta.scrollTop = Math.max(0, (i - 2) * lineHeight);
	},

	readOnlyList: function(title, got, descr) {
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var lines = got.lines || [];
		return api.card(title, [
			api.line(descr, 'dps-muted'),
			E('details', {}, [
				E('summary', { style: 'cursor:pointer' }, 'Показать список — ' + lines.length + ' ' + this.plural(lines.length, 'строка', 'строки', 'строк')),
				E('textarea', { 'class': 'cbi-input-textarea dps-textarea', rows: 10, readonly: '' }, lines.join('\n'))
			])
		]);
	},

	plural: function(n, one, few, many) {
		var m10 = n % 10, m100 = n % 100;
		if (m10 == 1 && m100 != 11) return one;
		if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
		return many;
	},

	// Свои настройки для сайтов (продвинутый режим, router-api §5.2 advanced_set): строки «сайты → стратегия» и что делать
	// с остальными. Выключено — всё как сейчас, строки хранятся.
	advancedEditor: function(got, strategies) {
		var title = 'Свои настройки для сайтов';
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var items = strategies.items || [];
		var name = function(id) {
			var s = items.filter(function(x) { return x.id == id; })[0];
			return s ? s.title : id;
		};
		var on = E('input', { type: 'checkbox', checked: got.enabled ? '' : null });
		var rowsBox = E('div');
		var msg = E('div', { 'class': 'dps-line' });
		var others = {};
		var othersBox = E('div', {}, [['lists', 'Как сейчас (по выбору на вкладке «Оптимизация»)'], ['all', 'Для всех сайтов'], ['direct', 'Напрямую — без оптимизации']]
			.map(function(o) { others[o[0]] = api.radio('dps-adv-others', o[0], o[1], (got.others || 'lists') == o[0]); return others[o[0]]; }));

		function addRow(r) {
			var row = E('div', { 'class': 'dps-target dps-adv-row' });
			var doms = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'example.com, cdn.example.com', value: r ? (r.domains || []).join(', ') : '' });
			var sel = E('select', { 'class': 'cbi-input-select' }, [
				E('option', { value: 'auto' }, 'Авто'), E('option', { value: 'direct' }, 'Напрямую')
			].concat(items.map(function(s) { return E('option', { value: s.id }, s.title); })));
			sel.value = r ? r.strategy : 'auto';
			var note = E('div', { 'class': 'dps-muted', 'style': 'font-size:.9em;width:100%' },
				r && r.strategy == 'auto' && r.resolved ? 'Авто — сейчас ' + name(r.resolved) : '');
			row.appendChild(doms);
			row.appendChild(sel);
			row.appendChild(E('button', { 'class': 'btn cbi-button cbi-button-remove', title: 'Убрать', click: function(ev) { ev.preventDefault(); row.remove(); } }, '×'));
			row.appendChild(note);
			rowsBox.appendChild(row);
			return doms;
		}
		(got.rows || []).forEach(addRow);

		var add = api.btn('Добавить сайты', function() { addRow().focus(); }, 'cbi-button-add');
		var save = api.btn('Сохранить', function() {
			msg.className = 'dps-line';
			msg.textContent = '';
			var rows = rowsBox.querySelectorAll('.dps-adv-row'), sent = [], map = [];
			rows.forEach(function(r, i) {
				r.classList.remove('dps-bad');
				var d = r.querySelector('input').value.split(/[\s,;]+/).filter(function(x) { return x; });
				if (d.length) { sent.push({ domains: d, strategy: r.querySelector('select').value }); map.push(i); }
			});
			var oth = Object.keys(others).filter(function(k) { return others[k].firstChild.checked; })[0] || 'lists';
			return api.call('advanced_set', { enabled: on.checked, others: oth, rows: sent }).then(function() {
				msg.className = 'dps-line dps-ok';
				msg.textContent = 'Сохранено';
			}, function(e) {
				msg.className = 'dps-line dps-error';
				var m = /^rows\[(\d+)\]/.exec(e.field || '');
				if (m && map[parseInt(m[1], 10)] != null) {
					var r = rows[map[parseInt(m[1], 10)]];
					r.classList.add('dps-bad');
					r.querySelector('input').focus();
					msg.textContent = 'Строка ' + (map[parseInt(m[1], 10)] + 1) + ': ' + api.text(e);
				} else
					msg.textContent = api.text(e);
			});
		}, 'cbi-button-save');

		return api.card(title, [
			E('label', { 'class': 'dps-check' }, [on, 'Включить',
				api.info('Выключено — всё работает как сейчас. Включено — для перечисленных сайтов своя стратегия, а остальные сайты — как выбрано ниже. «Авто» — стратегию для строки выберет подбор, до него — текущая. «Напрямую» — эти сайты оптимизация не трогает. Свои строки важнее списков YouTube и Discord: если впишете youtube.com, он пойдёт по вашей строке.')]),
			rowsBox,
			E('div', { 'class': 'dps-actions' }, [add]),
			E('p', { 'style': 'margin:.6em 0 .2em' }, [E('b', {}, 'Остальные сайты'),
				api.info('«Для всех сайтов» может мешать обычным сайтам — после сохранения проверьте их. «Напрямую» — работают только ваши строки.')]),
			othersBox,
			E('div', { 'class': 'dps-actions' }, [save]),
			msg
		]);
	},

	// «Свои адреса» для проверки: адрес + флаг «важный».
	targetsEditor: function(got) {
		var self = this;
		var title = 'Свои адреса для проверки';
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var rowsBox = E('div');
		var msg = E('div', { 'class': 'dps-line' });

		function addRow(item) {
			var row = E('div', { 'class': 'dps-target' });
			var url = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'https://example.com/', value: item ? item.url : '' });
			var imp = E('input', { type: 'checkbox', checked: item && item.important ? '' : null });
			row.appendChild(url);
			row.appendChild(E('label', { style: 'white-space:nowrap' }, [imp, ' важный']));
			row.appendChild(E('button', { 'class': 'btn cbi-button cbi-button-remove', title: 'Убрать', click: function(ev) { ev.preventDefault(); row.remove(); } }, '×'));
			rowsBox.appendChild(row);
			return url;
		}
		(got.items || []).forEach(addRow);

		var add = api.btn('Добавить адрес', function() { addRow().focus(); }, 'cbi-button-add');
		var save = api.btn('Сохранить', function() {
			msg.className = 'dps-line';
			msg.textContent = '';
			var rows = rowsBox.querySelectorAll('.dps-target'), items = [];
			rows.forEach(function(r) {
				r.classList.remove('dps-bad');
				var u = r.querySelector('input[type=text]').value.trim();
				if (u && !/^https?:\/\//i.test(u))
					u = 'https://' + u;
				items.push({ url: u, important: r.querySelector('input[type=checkbox]').checked });
			});
			// Пустые строки — не отправляем, но индексы ошибок считаем по отправленному.
			var sent = [], map = [];
			items.forEach(function(it, i) { if (it.url) { sent.push(it); map.push(i); } });
			return api.call('targets_set', { items: sent }).then(function() {
				msg.className = 'dps-line dps-ok';
				msg.textContent = 'Сохранено';
				rows.forEach(function(r) {
					var inp = r.querySelector('input[type=text]');
					if (!inp.value.trim()) r.remove();
				});
			}, function(e) {
				msg.className = 'dps-line dps-error';
				var m = /^items\[(\d+)\]/.exec(e.field || '');
				if (m && map[parseInt(m[1], 10)] != null) {
					var r = rows[map[parseInt(m[1], 10)]];
					r.classList.add('dps-bad');
					r.querySelector('input[type=text]').focus();
					msg.textContent = 'Адрес ' + (map[parseInt(m[1], 10)] + 1) + ': ' + api.text(e);
				} else
					msg.textContent = api.text(e);
			});
		}, 'cbi-button-save');

		return api.card(title, [
			api.line(['Сайты, которые должны открываться, кроме YouTube и Discord: они проверяются при подборе и автопроверке. До 50 адресов.',
				api.info('Отметьте «важный», если без этого сайта подбор считать неудачным. Домены отсюда автоматически попадают в «Свои сайты». Если сайт перестал открываться после добавления — уберите его или добавьте в исключения.')], 'dps-muted'),
			rowsBox,
			E('div', { 'class': 'dps-actions' }, [add, save]),
			msg
		]);
	}
});
