#!/bin/sh
# Дымовой тест установленного пакета на роутере/эмуляторе: все читающие методы ubus отвечают без ошибок ucode,
# init-скрипт перезапускается, status.json пишется и это JSON. Не меняет настройки.
fail=0
ok() { echo "ok   $1"; }
bad() { echo "FAIL $1: $2"; fail=1; }

out=$(/etc/init.d/dpishell restart 2>&1)
echo "$out" | grep -qiE 'error|undeclared' && bad 'init restart' "$out" || ok 'init restart'

for m in status strategies select_progress targets_get diag report dns_status site_check_result; do
	r=$(ubus call dpishell $m 2>&1)
	if [ $? -ne 0 ] || echo "$r" | grep -qiE 'Reference error|Syntax error|undeclared|Runtime error'; then
		bad "$m" "$(echo "$r" | head -3)"
	else
		ok "$m"
	fi
done
r=$(ubus call dpishell lists_get '{"name":"user-exclude"}' 2>&1) && ok lists_get || bad lists_get "$r"

[ -f /www/dpishell/status.json ] && ucode -e 'import {readfile} from "fs"; json(readfile("/www/dpishell/status.json"));' 2>/dev/null \
	&& ok 'status.json' || bad 'status.json' 'нет файла или не JSON'
grep -q 'dpishell/run watch' /etc/crontabs/root && ok 'cron watch' || bad 'cron watch' 'нет строки'

[ $fail = 0 ] && echo 'api_smoke: всё прошло' || echo 'api_smoke: есть ошибки'
exit $fail
