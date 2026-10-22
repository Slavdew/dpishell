#!/bin/sh
# Шаги проверки установки, обновления, отката функций и удаления — внутри эмулятора. Зовёт router/test/vm-lifecycle.sh
# (с компьютера), сам по себе не запускается. Каталог с пакетами и этим файлом — /tmp/dpslc.
#   sh lifecycle_vm.sh snap <имя>            — снимок состояния роутера в /tmp/dpslc/snap/<имя>/
#   sh lifecycle_vm.sh install <old|new|old2> — apk add пакетов из /tmp/dpslc/<набор>/
#   sh lifecycle_vm.sh core                  — ядро (архив из /tmp/dpslc/core.zip, если есть) и ожидание итога
#   sh lifecycle_vm.sh enable <id>           — выбрать стратегию и включить
#   sh lifecycle_vm.sh update_local <набор>  — обновление из папки окна ПК (/tmp/dpishell-update) и ожидание итога
#   sh lifecycle_vm.sh uninstall <keep|full> — метод ubus uninstall и ожидание итога
#   sh lifecycle_vm.sh wait_dns              — дождаться конца задания DNS
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
wait_dns)
	sleep 2
	wait_file_json /tmp/dpishell/dns_job.json '@.running' false 180
	jsonfilter -i /tmp/dpishell/dns_job.json -e '@.message'
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
