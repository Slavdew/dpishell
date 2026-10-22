#!/bin/sh
# Шаги проверки установки, обновления, отката функций и удаления — внутри эмулятора. Зовёт router/test/vm-lifecycle.sh
# (с компьютера), сам по себе не запускается. Каталог с пакетами и этим файлом — /tmp/dpslc.
#   sh lifecycle_vm.sh snap <имя>            — снимок состояния роутера в /tmp/dpslc/snap/<имя>/
#   sh lifecycle_vm.sh install <old|new|old2> — apk add пакетов из /tmp/dpslc/<набор>/
#   sh lifecycle_vm.sh core                  — ядро (архив из /tmp/dpslc/core.zip, если есть) и ожидание итога
#   sh lifecycle_vm.sh enable <id>           — выбрать стратегию и включить
#   sh lifecycle_vm.sh update_local <набор>  — обновление из папки окна ПК (/tmp/dpishell-update) и ожидание итога
#   sh lifecycle_vm.sh uninstall <keep|full> — метод ubus uninstall и ожидание итога
#   sh lifecycle_vm.sh dns_set <вариант>     — dns_set и ожидание итога именно этого задания
#   sh lifecycle_vm.sh purge                 — убрать DpiShell полностью (новым uninstall.sh), если стоит
# Вывод проверок — строки «ok <что>» / «FAIL <что>»; итоги собирает vm-lifecycle.sh.
D=/tmp/dpslc
cmd=$1; shift

ok() { echo "ok   $*"; }
fail() { echo "FAIL $*"; }
check() { name=$1; shift; if "$@" >/dev/null 2>&1; then ok "$name"; else fail "$name"; fi; }
j() { jsonfilter -s "$1" -e "$2" 2>/dev/null; }

wait_file_json() {   # wait_file_json <файл> <выражение> <значение> <сек>
	i=0
	while [ $i -lt "$4" ]; do
		[ "$(jsonfilter -i "$1" -e "$2" 2>/dev/null)" = "$3" ] && return 0
		sleep 1; i=$((i + 1))
	done
	return 1
}

case "$cmd" in
snap)
	S=$D/snap/$1
	rm -rf "$S"; mkdir -p "$S"
	for c in dhcp firewall network https-dns-proxy dpishell; do
		uci export "$c" > "$S/uci-$c" 2>/dev/null || echo "(нет)" > "$S/uci-$c"
	done
	apk list -I 2>/dev/null | cut -d' ' -f1 | sort > "$S/packages"
	tr ' ' '\n' < /etc/apk/world | sed '/^$/d' | sort > "$S/world"
	cat /etc/crontabs/root > "$S/crontab" 2>/dev/null
	# nft без таблицы zapret (её ведёт ядро обхода) и без счётчиков (-s).
	nft -s list ruleset 2>/dev/null | awk '/^table inet zapret /{skip=1} !skip{print} skip&&/^}/{skip=0}' > "$S/nft"
	for p in /etc/init.d /etc/rc.d /etc/hotplug.d/iface /etc/hotplug.d/button /opt /www /etc/apk/keys /etc/apk/repositories.d \
		/lib/upgrade/keep.d /usr/share/rpcd/ucode /usr/share/rpcd/acl.d /usr/libexec /usr/share/ucode /usr/share/luci/menu.d \
		/www/luci-static/resources /etc/config; do
		ls -1A "$p" 2>/dev/null | sed "s#^#$p/#"
	done | grep -v '^/etc/config/\.' > "$S/files"
	cat /etc/apk/repositories.d/*.list 2>/dev/null > "$S/feeds"
	# Свои следы по всей системе (кроме /proc, /sys, каталога проверки).
	find / -xdev \( -path /proc -o -path /sys -o -path "$D" \) -prune -o \( -name '*dpishell*' -o -name '*zapret*' -o -name 'dpi' \) -print 2>/dev/null |
		grep -v -e '^/lib/apk/db' -e '^/tmp/dpishell-uninstall.json$' | sort > "$S/traces"
	{
		echo "tcp_timestamps=$(cat /proc/sys/net/ipv4/tcp_timestamps)"
		echo "dnsmasq_running=$(pidof dnsmasq >/dev/null && echo 1 || echo 0)"
		echo "dnsmasq_enabled=$(/etc/init.d/dnsmasq enabled && echo 1 || echo 0)"
		echo "firewall_enabled=$(/etc/init.d/firewall enabled && echo 1 || echo 0)"
		echo "fw4_table=$(nft list table inet fw4 >/dev/null 2>&1 && echo 1 || echo 0)"
		echo "resolves=$(nslookup example.com 127.0.0.1 2>/dev/null | grep -q 'Address:.*[0-9]\.[0-9]' && echo 1 || echo 0)"
		echo "rpcd=$(pidof rpcd >/dev/null && echo 1 || echo 0)"
		echo "uhttpd=$(pidof uhttpd >/dev/null && echo 1 || echo 0)"
	} > "$S/health"
	echo "snap $1"
	;;
install)
	set -- $D/$1/*.apk
	apk add --allow-untrusted "$@" >/tmp/dpslc/apk.log 2>&1 && ok "apk add $(basename "$1")" || { fail "apk add"; tail -5 /tmp/dpslc/apk.log; }
	# rpcd перезапускается postinst через 2 с.
	i=0; while [ $i -lt 20 ] && ! ubus -t 2 list dpishell >/dev/null 2>&1; do sleep 1; i=$((i + 1)); done
	sleep 3
	check "объект ubus dpishell" ubus -t 5 list dpishell
	;;
core)
	[ -f $D/core.zip ] && cp $D/core.zip /tmp/dpishell-core.zip && chmod 600 /tmp/dpishell-core.zip
	ubus call dpishell core_install >/dev/null
	sleep 2
	wait_file_json /tmp/dpishell/core_install.json '@.running' false 300
	check "ядро установлено" wait_file_json /tmp/dpishell/core_install.json '@.ok' true 1
	check "nfqws на месте" test -x /opt/zapret/nfq/nfqws
	;;
enable)
	ubus call dpishell strategy_set "{\"id\":\"$1\"}" >/dev/null
	r=$(ubus call dpishell enable '{}')
	sleep 3
	check "включено: enabled=1" [ "$(uci -q get dpishell.main.enabled)" = 1 ]
	check "включено: nfqws работает" pidof nfqws
	check "включено: автозапуск zapret" /etc/init.d/zapret enabled
	;;
update_local)
	rm -rf /tmp/dpishell-update; mkdir -m 700 /tmp/dpishell-update
	cp $D/$1/*.apk /tmp/dpishell-update/
	(cd /tmp/dpishell-update && sha256sum *.apk > SHA256SUMS)
	want=$(ls $D/$1/ | sed -n 's/^dpishell-\(.*\)\.apk$/\1/p')
	ubus call dpishell update_install '{"from":"local"}' >/dev/null
	sleep 3
	wait_file_json /tmp/dpishell/update_job.json '@.running' false 120
	sleep 3
	i=0; while [ $i -lt 20 ] && ! ubus -t 2 list dpishell >/dev/null 2>&1; do sleep 1; i=$((i + 1)); done
	check "обновление: задание ок" wait_file_json /tmp/dpishell/update_job.json '@.ok' true 1
	check "обновление: стоит $want" grep -qx "V:$want" /lib/apk/db/installed
	jsonfilter -i /tmp/dpishell/update_job.json -e '@.message'
	;;
uninstall)
	rm -f /tmp/dpishell-uninstall.json
	if [ "$1" = full ]; then ubus call dpishell uninstall '{"keep_settings":false}' >/dev/null; else ubus call dpishell uninstall '{"keep_settings":true}' >/dev/null; fi
	sleep 2
	wait_file_json /tmp/dpishell-uninstall.json '@.running' false 120
	check "удаление ($1): итог ок" wait_file_json /tmp/dpishell-uninstall.json '@.ok' true 1
	jsonfilter -i /tmp/dpishell-uninstall.json -e '@.message'
	sleep 4
	;;
dns_set)
	# Задание DNS начинается не сразу (rpcd запускает его в фоне), а прежний итог dns_job.json остаётся от прошлого задания:
	# ждать итог именно этого — dns_set, законченный не раньше вызова (at ≥ t0), а не «running=false» вообще.
	t0=$(date +%s)
	ubus call dpishell dns_set "{\"preset\":\"$1\"}" >/dev/null || { fail "dns_set $1: вызов"; exit 0; }
	i=0
	while [ $i -lt 180 ]; do
		f=/tmp/dpishell/dns_job.json
		if [ "$(jsonfilter -i $f -e '@.action' 2>/dev/null)" = dns_set ] && [ "$(jsonfilter -i $f -e '@.running' 2>/dev/null)" = false ] &&
			[ "$(jsonfilter -i $f -e '@.at' 2>/dev/null || echo 0)" -ge "$t0" ] 2>/dev/null; then
			break
		fi
		sleep 1; i=$((i + 1))
	done
	check "DNS $1: задание закончено" [ $i -lt 180 ]
	check "DNS $1: задание ок" [ "$(jsonfilter -i /tmp/dpishell/dns_job.json -e '@.ok' 2>/dev/null)" = true ]
	jsonfilter -i /tmp/dpishell/dns_job.json -e '@.message'
	;;
changes)
	# «Изменено DpiShell» (changes_list / changes_undo / changes_undo_all, с r16): пункты по тому, что поменяли, отмена одного
	# пункта и всех. DNS — при выключенной оптимизации (в эмуляторе она рвёт собственные соединения роутера, DoH не проверить).
	strat=$1
	undo_wait() {   # undo_wait <id|all>: вызов и ожидание итога именно этого задания (started_at ≥ t0)
		t0=$(date +%s)
		if [ "$1" = all ]; then r=$(ubus call dpishell changes_undo_all); else r=$(ubus call dpishell changes_undo "{\"id\":\"$1\"}"); fi
		echo "$r" | grep -q '"started": true' || { fail "отмена $1: вызов ($r)"; return 1; }
		f=/tmp/dpishell/changes_job.json; i=0
		while [ $i -lt 120 ]; do
			[ "$(jsonfilter -i $f -e '@.id' 2>/dev/null)" = "$1" ] && [ "$(jsonfilter -i $f -e '@.running' 2>/dev/null)" = false ] &&
				[ "$(jsonfilter -i $f -e '@.started_at' 2>/dev/null || echo 0)" -ge "$t0" ] 2>/dev/null && return 0
			sleep 1; i=$((i + 1))
		done
		fail "отмена $1: задание не закончилось"; return 1
	}
	has() { ubus call dpishell changes_list | jsonfilter -e '@.items[*].id' | grep -qx "$1"; }
	ubus call dpishell disable >/dev/null; sleep 1
	sh $D/lifecycle_vm.sh dns_set google | grep -v '^ok'
	uci set firewall.@defaults[0].flow_offloading=1; uci commit firewall; /etc/init.d/firewall reload >/dev/null 2>&1
	ubus call dpishell flow_offload_fix >/dev/null
	ubus call dpishell strategy_set "{\"id\":\"$strat\"}" >/dev/null; ubus call dpishell enable '{}' >/dev/null; sleep 2
	ubus call dpishell lists_set '{"name":"user-general","lines":["example.org","example.info"]}' >/dev/null
	ubus call dpishell ipv6_set '{"enabled":true}' >/dev/null
	ubus call dpishell advanced_set '{"enabled":true,"others":"lists","rows":[{"domains":["example.net"],"strategy":"auto"}]}' >/dev/null
	ubus call dpishell pc_link_set '{"id":"test-pc-0002","macs":["02:00:00:00:00:02"],"domains":["example.com"]}' >/dev/null; sleep 2
	cl=$(ubus call dpishell changes_list)
	for x in enable list:user-general ipv6 advanced pc:test-pc-0002 flow_offload dns core; do
		echo "$cl" | jsonfilter -e '@.items[*].id' | grep -qx "$x" && ok "журнал: $x" || fail "журнал: $x"
	done
	check "журнал: время у включения и ускорения сети" [ -n "$(j "$cl" '@.items[@.id="enable"].at')" -a -n "$(j "$cl" '@.items[@.id="flow_offload"].at')" ]
	check "журнал: DNS и ускорение сети — с предупреждением о связи" [ "$(j "$cl" '@.items[@.id="dns"].risk')$(j "$cl" '@.items[@.id="flow_offload"].risk')" = linklink ]
	check "журнал: ядро — отдельно нельзя" [ "$(j "$cl" '@.items[@.id="core"].undo')" = no ]
	ubus call dpishell changes_undo '{"id":"core"}' | grep -q '"invalid"' && ok 'ядро: changes_undo отказывает' || fail 'ядро: changes_undo отказывает'
	check "журнал во флеше — не больше 4 ключей" [ "$(jsonfilter -i /etc/dpishell/changes.json -e '@.*' | wc -l)" -le 4 ]
	# Один пункт: остальные остаются.
	md5a=$(md5sum < /opt/zapret/config)
	undo_wait list:user-general && check "отмена пункта: список пуст" sh -c '! test -s /etc/dpishell/lists/user-general.txt'
	check "отмена пункта: статус «возвращено»" [ "$(jsonfilter -i /tmp/dpishell/changes_job.json -e '@.items[0].status')" = restored ]
	check "отмена пункта: пункта больше нет" sh -c '! ubus call dpishell changes_list | jsonfilter -e "@.items[*].id" | grep -qx list:user-general'
	check "отмена пункта: остальное на месте" sh -c 'ubus call dpishell changes_list | jsonfilter -e "@.items[*].id" | grep -qx advanced && pidof nfqws >/dev/null'
	undo_wait advanced && check "отмена: продвинутый режим убран из config" sh -c '! grep -q example.net /opt/zapret/config && ! test -e /etc/dpishell/advanced.json'
	ubus call dpishell changes_undo '{"id":"advanced"}' | grep -q '"invalid"' && ok 'повторная отмена: «нет такого пункта»' || fail 'повторная отмена: «нет такого пункта»'
	# Всё: выключено, ускорение сети и DNS — как было, связка снята, ядро осталось.
	undo_wait all
	jsonfilter -i /tmp/dpishell/changes_job.json -e '@.items[*].id' | tr '\n' ' '; echo
	check "«Отменить всё»: всё «возвращено»" sh -c '! jsonfilter -i /tmp/dpishell/changes_job.json -e "@.items[*].status" | grep -vqx restored'
	check "«Отменить всё»: оптимизация выключена" sh -c '[ "$(uci get dpishell.main.enabled)" = 0 ] && ! pidof nfqws >/dev/null && ! /etc/init.d/zapret enabled'
	check "«Отменить всё»: ускорение сети вернулось" sh -c '[ "$(uci -q get firewall.@defaults[0].flow_offloading)" = 1 ] && [ -z "$(uci -q get dpishell.main.fw_offload_prev)" ]'
	check "«Отменить всё»: DNS провайдера" sh -c '! test -e /etc/dpishell/dns-backup.json && [ -z "$(uci -q get dpishell.main.dns_preset)" ] && ! grep -qx "P:https-dns-proxy" /lib/apk/db/installed'
	check "«Отменить всё»: связки и IPv6 нет" sh -c '! jsonfilter -i /etc/dpishell/pc-links.json -e "@.items[0]" >/dev/null && [ "$(uci get dpishell.main.ipv6)" = 0 ]'
	check "«Отменить всё»: в списке только ядро" [ "$(ubus call dpishell changes_list | jsonfilter -e '@.items[*].id' | tr '\n' ' ')" = 'core ' ]
	check "«Отменить всё»: DNS роутера отвечает" sh -c 'nslookup example.com 127.0.0.1 | grep -q "Address:.*[0-9]\.[0-9]"'
	# Пусто — «Отменить всё» отвечает и ничего не делает.
	undo_wait all && check "«Отменить всё» без пунктов — пустой итог" [ -z "$(jsonfilter -i /tmp/dpishell/changes_job.json -e '@.items[*].id')" ]
	;;
rollback)
	# Откат версии: rollback saved — из флеша (status.rollback.saved), rollback local <набор> — из папки окна ПК.
	if [ "$1" = local ]; then
		rm -rf /tmp/dpishell-update; mkdir -m 700 /tmp/dpishell-update
		cp $D/$2/*.apk /tmp/dpishell-update/
		(cd /tmp/dpishell-update && sha256sum *.apk > SHA256SUMS)
		want=$(ls $D/$2/ | sed -n 's/^dpishell-\(.*\)\.apk$/\1/p')
	else
		want=$(ubus call dpishell status | jsonfilter -e '@.rollback.version')
	fi
	t0=$(date +%s)
	r=$(ubus call dpishell update_rollback "{\"from\":\"$1\"}")
	echo "$r" | grep -q '"started": true' || fail "откат ($1): вызов — $r"
	i=0
	while [ $i -lt 180 ]; do
		[ "$(jsonfilter -i /tmp/dpishell/update_job.json -e '@.action' 2>/dev/null)" = rollback ] && [ "$(jsonfilter -i /tmp/dpishell/update_job.json -e '@.running' 2>/dev/null)" = false ] &&
			[ "$(jsonfilter -i /tmp/dpishell/update_job.json -e '@.started_at' 2>/dev/null || echo 0)" -ge "$t0" ] 2>/dev/null && break
		sleep 1; i=$((i + 1))
	done
	sleep 3
	i=0; while [ $i -lt 20 ] && ! ubus -t 2 list dpishell >/dev/null 2>&1; do sleep 1; i=$((i + 1)); done
	check "откат ($1): задание ок" [ "$(jsonfilter -i /tmp/dpishell/update_job.json -e '@.ok')" = true ]
	check "откат ($1): стоит $want" grep -qx "V:$want" /lib/apk/db/installed
	jsonfilter -i /tmp/dpishell/update_job.json -e '@.message'
	;;
purge)
	if grep -qx 'P:dpishell' /lib/apk/db/installed || [ -d /etc/dpishell ] || [ -d /opt/zapret ]; then
		sh $D/uninstall.sh --full >/dev/null 2>&1
		sleep 4
	fi
	check "DpiShell не стоит" sh -c '! grep -qx "P:dpishell" /lib/apk/db/installed'
	;;
*)
	echo "нет шага $cmd" >&2; exit 2 ;;
esac
exit 0
