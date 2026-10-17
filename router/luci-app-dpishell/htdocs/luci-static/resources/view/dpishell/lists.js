'use strict';
'require view';
'require ui';
'require dom';
'require dpishell.api as api';

// Вкладка «Исключения и списки» (architecture.md «Режимы роутера»): свои исключения, стандартные — просмотр,
// свои сайты (только в режиме «По спискам»), свои адреса для проверки.

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
			api.safe('targets_get')
		]);
	},

	render: function(data) {
		var status = data[0].__error ? {} : data[0];
		var listsMode = status.mode == 'lists';

		return E('div', {}, [
			E('h2', {}, 'Исключения и списки'),
			this.listEditor('user-exclude', 'Свои исключения', data[1],
				['Сайты, которые обход ломает: к ним он применяться не будет. Один адрес на строку, например example.com.',
					api.info('Строка example.com закрывает и все поддомены (video.example.com). Чтобы исключить только сам домен без поддоменов, начните строку с ^: ^example.com. Сюда попадают сайты, добавленные кнопкой «Сайт не открывается с обходом» на главной.')]),
			listsMode ? this.listEditor('user-general', 'Свои сайты', data[3],
				['Сайты, для которых нужен обход, кроме YouTube и Discord. Один адрес на строку. Действует в режиме «По спискам».',
					api.info('Домены из «Своих адресов» ниже попадают сюда автоматически при проверке. Не добавляйте сайты, которые и так открываются: обход для них ничего не улучшит, а в редких случаях может помешать.')]) : '',
			this.readOnlyList('Стандартные исключения', data[2],
				'Сайты, которым обход известно мешает (банки, госуслуги и другие). Список приходит с обновлениями пакета, править его нельзя.'),
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
