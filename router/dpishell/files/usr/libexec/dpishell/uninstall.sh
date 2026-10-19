#!/bin/sh
# Удаление DpiShell с возвратом всего, что он менял вне своих файлов (router-api.md §5).
#   sh uninstall.sh --keep      — удалить, настройки (/etc/dpishell: свои адреса, списки, стратегии, расписание) оставить
#   sh uninstall.sh --full      — удалить полностью, вместе с настройками, ключом и строкой витрины
#   sh uninstall.sh --from-prerm — только очистка, без удаления пакетов (зовёт prerm пакета при ручном apk del)
# Запуск: метод ubus uninstall (копия в /tmp, отвязанно — пакет исчезает по ходу), кнопка на странице, окно ПК по SSH
# (ssh root@<роутер> 'sh -s -- --keep' < uninstall.sh — работает и без пакета). Ход и итог — /tmp/dpishell-uninstall.json.
# Ядро zapret в /opt/zapret (ставил core_install) удаляется; kmod и прочие зависимости остаются.
KEEP=1
PKGS=1
for a in "$@"; do
	case "$a" in
		--keep) KEEP=1 ;;
		--full) KEEP= ;;
		--from-prerm) PKGS= ;;
	esac
done
OUT=/tmp/dpishell-uninstall.json
# Не в cgroup rpcd (запуск со страницы): шаг 9 перезапускает rpcd, procd не должен убить нас вместе с ним.
[ -n "$PKGS" ] && echo $$ > /sys/fs/cgroup/cgroup.procs 2>/dev/null

say() {
	[ -n "$PKGS" ] || return 0
	printf '{"running":%s,"ok":%s,"keep_settings":%s,"message":"%s","at":%s}\n' "$1" "$2" "$([ -n "$KEEP" ] && echo true || echo false)" "$3" "$(date +%s)" > "$OUT.tmp" && mv "$OUT.tmp" "$OUT"
}

say true null 'Выключаю оптимизацию'

# 1. Фоновые задания (подбор, проверка, DNS) — остановить: подбор держит свой config zapret и метки TCP.
for l in /tmp/dpishell/*.lock; do
	[ -f "$l" ] && kill "$(cat "$l")" 2>/dev/null
done
/etc/init.d/dpishell stop >/dev/null 2>&1
/etc/init.d/dpishell disable >/dev/null 2>&1

# 2. Обход: zapret stop убирает nfqws и таблицу inet zapret.
if [ -x /etc/init.d/zapret ]; then
	/etc/init.d/zapret stop >/dev/null 2>&1
	/etc/init.d/zapret disable >/dev/null 2>&1
fi
nft delete table inet zapret 2>/dev/null

# 3. Метки TCP: подбор временно выключает их для стратегий с ts (architecture.md «Временные метки TCP») — вернуть умолчание.
[ "$(cat /proc/sys/net/ipv4/tcp_timestamps 2>/dev/null)" = 0 ] && ! grep -qs tcp_timestamps /etc/sysctl.conf /etc/sysctl.d/*.conf &&
	echo 1 > /proc/sys/net/ipv4/tcp_timestamps

# 4. Разгрузка, переданная zapret кнопкой (flow_offload_fix), — обратно межсетевому экрану.
say true null 'Возвращаю настройки сети'
prev=$(uci -q get dpishell.main.fw_offload_prev)
if [ -n "$prev" ]; then
	uci set firewall.@defaults[0].flow_offloading=1
	[ "$prev" = hardware ] && uci set firewall.@defaults[0].flow_offloading_hw=1
	uci commit firewall
	uci -q delete dpishell.main.fw_offload_prev && uci commit dpishell
	/etc/init.d/firewall reload >/dev/null 2>&1
fi

# 5. DNS, если меняли (dns_set/dns_enable): тот же возврат, что dns_restore. Без модуля — по минимуму.
dp=$(uci -q get dpishell.main.dns_preset)
if [ -f /etc/dpishell/dns-backup.json ] || { [ -n "$dp" ] && [ "$dp" != provider ]; }; then
	if [ -f /usr/share/ucode/dpishell/dns.uc ]; then
		ucode -L '/usr/share/ucode/*.uc' -e "import * as dns from 'dpishell.dns'; dns.restore();" >/dev/null 2>&1
	else
		[ -x /etc/init.d/https-dns-proxy ] && { /etc/init.d/https-dns-proxy stop; /etc/init.d/https-dns-proxy disable; } >/dev/null 2>&1
		{ apk del https-dns-proxy || opkg remove https-dns-proxy; } >/dev/null 2>&1
		/etc/init.d/dnsmasq restart >/dev/null 2>&1
	fi
fi

# 6. Расписание.
if grep -qs 'dpishell' /etc/crontabs/root; then
	sed -i '/dpishell/d' /etc/crontabs/root
	/etc/init.d/cron restart >/dev/null 2>&1
fi

# 7. Пакеты (prerm — сам часть удаления пакета, его не трогаем).
if [ -n "$PKGS" ]; then
	say true null 'Удаляю пакеты'
	if [ -x /usr/bin/apk ]; then
		apk del luci-app-dpishell dpishell >/dev/null 2>&1
	elif [ -x /bin/opkg ]; then
		opkg remove luci-app-dpishell dpishell >/dev/null 2>&1
	fi
fi

# 8. Ядро и следы вне пакета.
rm -f /etc/init.d/zapret /etc/hotplug.d/iface/90-zapret /etc/rc.d/*zapret
rm -rf /opt/zapret /www/dpishell /www/dpi /tmp/dpishell /tmp/dpishell-offline /tmp/dpishell-update
# Папки пакетов — после apk del (там могут остаться файлы, положенные не пакетом: ручные правки, отладка).
[ -n "$PKGS" ] && rm -rf /usr/share/dpishell /usr/share/ucode/dpishell /usr/libexec/dpishell \
	/www/luci-static/resources/dpishell /www/luci-static/resources/view/dpishell
if [ -z "$KEEP" ]; then
	rm -rf /etc/dpishell
	rm -f /etc/config/dpishell /etc/config/dpishell-opkg /etc/config/dpishell.apk-new /etc/apk/keys/dpishell.pem
	[ -f /etc/apk/repositories.d/customfeeds.list ] && sed -i '/slavdew\.github\.io\/dpishell/d' /etc/apk/repositories.d/customfeeds.list
fi

# 9. Страницы и объект ubus — без пакета их не должно остаться в меню и в rpcd.
if [ -n "$PKGS" ]; then
	rm -rf /tmp/luci-indexcache* /tmp/luci-modulecache
	/etc/init.d/rpcd restart >/dev/null 2>&1
	if [ -f /lib/apk/packages/dpishell.list ] || [ -f /usr/lib/opkg/info/dpishell.control ]; then
		say false false 'Пакет dpishell не удалился — см. apk del dpishell'
		exit 1
	fi
	say false true "$([ -n "$KEEP" ] && echo 'DpiShell удалён, настройки сохранены' || echo 'DpiShell удалён полностью')"
fi
exit 0
