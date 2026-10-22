#!/bin/sh
# Удаление DpiShell с возвратом всего, что он менял вне своих файлов (router-api.md §5).
#   sh uninstall.sh --keep      — удалить, настройки (/etc/dpishell: свои адреса, списки, стратегии, расписание) оставить
#   sh uninstall.sh --full      — удалить полностью, вместе с настройками, ключом и строкой витрины
#   sh uninstall.sh --from-prerm — только очистка, без удаления пакетов (зовёт prerm пакета при ручном apk del)
# Запуск: метод ubus uninstall (копия в /tmp, отвязанно — пакет исчезает по ходу), кнопка на странице, окно ПК по SSH
# (ssh root@<роутер> 'sh -s -- --keep' < uninstall.sh — работает и без пакета). Ход и итог — /tmp/dpishell-uninstall.json.
# Ядро zapret в /opt/zapret (ставил core_install) удаляется; kmod и прочие зависимости остаются.
# Правила (decisions-owner-2026-10-20.md, «Ресурсы роутера»): удаляется только своё — точные пути, без шаблонов вне своих
# каталогов, без перехода по ссылкам; чужие пакеты и настройки (dnsmasq, firewall, https-dns-proxy, другой zapret) — только
# то, что меняли мы, и как было. Повтор и запуск после сбоя безопасны: каждый шаг проверяет, есть ли что делать.
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
	# Временный файл — mktemp: по постоянному имени в общем /tmp чужая ссылка увела бы запись root в другой файл.
	t=$(mktemp /tmp/dpishell-uninstall.XXXXXX) || return 0
	printf '{"running":%s,"ok":%s,"keep_settings":%s,"message":"%s","at":%s}\n' "$1" "$2" "$([ -n "$KEEP" ] && echo true || echo false)" "$3" "$(date +%s)" > "$t" &&
		chmod 644 "$t" && mv -f "$t" "$OUT" || rm -f "$t"
}

# Остановить задание по замку «PID время_старта» (state.uc): только живой run DpiShell с тем же временем старта —
# PID мог достаться другому процессу, а замок прежнего формата — содержать что угодно.
kill_lock() {
	set -- $(cat "$1" 2>/dev/null)
	case "$1" in ''|*[!0-9]*) return 0 ;; esac
	[ "$1" -gt 1 ] || return 0
	tr '\0' ' ' < "/proc/$1/cmdline" 2>/dev/null | grep -q 'dpishell/run' || return 0
	[ -z "$2" ] || [ "$(sed 's/.*) //' "/proc/$1/stat" 2>/dev/null | cut -d' ' -f20)" = "$2" ] || return 0
	kill "$1" 2>/dev/null
}

# Пакет установлен: база apk (строка «P:<имя>») или opkg.
pkg_installed() {
	grep -qx "P:$1" /lib/apk/db/installed 2>/dev/null || [ -f "/usr/lib/opkg/info/$1.control" ]
}

# Ядро в /opt/zapret — наше: его ставил core_install (core-installed.json) и нет пакета другого zapret, который владеет
# /opt/zapret сам (core.uc, FOREIGN_PACKAGES). Чужое ядро (пакет, ручной install_easy.sh) не трогаем.
OURS_CORE=
if [ -f /etc/dpishell/core-installed.json ]; then
	OURS_CORE=1
	for n in zapret luci-app-zapret zapret-manager-beam zapret2 luci-app-zapret2; do
		pkg_installed "$n" && OURS_CORE=
	done
fi

# Ссылка указывает в наше ядро — удалить ссылку (не то, на что она указывает).
rm_link_to() {
	[ -L "$1" ] || return 0
	case "$(readlink "$1")" in "$2") rm -f "$1" ;; esac
}

say true null 'Выключаю оптимизацию'

# 1. Фоновые задания (подбор, проверка, DNS) — остановить: подбор держит свой config zapret и метки TCP.
for l in /tmp/dpishell/*.lock; do
	[ -f "$l" ] && kill_lock "$l"
done
[ -x /etc/init.d/dpishell ] && { /etc/init.d/dpishell stop; /etc/init.d/dpishell disable; } >/dev/null 2>&1

# 2. Обход: zapret stop убирает nfqws и таблицу inet zapret. Только наше ядро.
if [ -n "$OURS_CORE" ]; then
	if [ -x /etc/init.d/zapret ]; then
		/etc/init.d/zapret stop >/dev/null 2>&1
		/etc/init.d/zapret disable >/dev/null 2>&1
	fi
	nft delete table inet zapret 2>/dev/null
fi

# 3. Метки TCP: подбор временно выключает их для стратегий с ts (architecture.md «Временные метки TCP») — вернуть умолчание.
[ "$(cat /proc/sys/net/ipv4/tcp_timestamps 2>/dev/null)" = 0 ] && ! grep -qs tcp_timestamps /etc/sysctl.conf /etc/sysctl.d/*.conf &&
	echo 1 > /proc/sys/net/ipv4/tcp_timestamps

# 4. Разгрузка, отключённая нами (flow_offload_fix или установка ядра, с r14), — обратно межсетевому экрану.
say true null 'Возвращаю настройки сети'
prev=$(uci -q get dpishell.main.fw_offload_prev)
if [ -n "$prev" ]; then
	uci set firewall.@defaults[0].flow_offloading=1
	[ "$prev" = hardware ] && uci set firewall.@defaults[0].flow_offloading_hw=1
	uci commit firewall
	uci -q delete dpishell.main.fw_offload_prev && uci commit dpishell
	/etc/init.d/firewall reload >/dev/null 2>&1
fi

# 5. DNS, если меняли (dns_set/dns_enable): тот же возврат, что dns_restore. Без модуля — по минимуму: прокси удаляем,
# только если его ставили мы (в копии нет «installed»: true).
dp=$(uci -q get dpishell.main.dns_preset)
if [ -f /etc/dpishell/dns-backup.json ] || { [ -n "$dp" ] && [ "$dp" != provider ]; }; then
	if [ -f /usr/share/ucode/dpishell/dns.uc ]; then
		ucode -L '/usr/share/ucode/*.uc' -e "import * as dns from 'dpishell.dns'; dns.restore();" >/dev/null 2>&1
	elif ! grep -qs '"installed": *true' /etc/dpishell/dns-backup.json; then
		[ -x /etc/init.d/https-dns-proxy ] && { /etc/init.d/https-dns-proxy stop; /etc/init.d/https-dns-proxy disable; } >/dev/null 2>&1
		{ apk del https-dns-proxy || opkg remove https-dns-proxy; } >/dev/null 2>&1
		/etc/init.d/dnsmasq restart >/dev/null 2>&1
	fi
fi

# 6. Расписание: только своя строка (run tick/watch/guard).
if grep -qs '/usr/libexec/dpishell/run' /etc/crontabs/root; then
	sed -i '\#/usr/libexec/dpishell/run#d' /etc/crontabs/root
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

# 8. Ядро и следы вне пакета — точными путями. rm -rf по ссылке удаляет саму ссылку, не то, на что она указывает.
if [ -n "$OURS_CORE" ]; then
	rm_link_to /etc/init.d/zapret /opt/zapret/init.d/openwrt/zapret
	rm_link_to /etc/hotplug.d/iface/90-zapret /opt/zapret/init.d/openwrt/90-zapret
	# Автозапуск (zapret disable уже убрал; init.d мог пропасть раньше — тогда ссылки висят): только ссылки на ../init.d/zapret.
	for f in /etc/rc.d/S[0-9][0-9]zapret /etc/rc.d/K[0-9][0-9]zapret; do
		rm_link_to "$f" ../init.d/zapret
	done
	# Ядро и остатки прерванной установки ядра (core.uc: .new/.old).
	rm -rf /opt/zapret /opt/zapret.new /opt/zapret.old
	# Отметка о нашем ядре — вместе с ядром: иначе после ручной установки чужого zapret его сочли бы нашим.
	rm -f /etc/dpishell/core-installed.json
fi
rm -rf /www/dpishell /tmp/dpishell /tmp/dpishell-pub /tmp/dpishell-offline /tmp/dpishell-update
rm -f /tmp/dpishell-core.zip
# Папки пакетов — после apk del (там могут остаться файлы, положенные не пакетом: ручные правки, отладка).
# Файлы пакетов в /etc apk оставляет, если они отличаются от пакета (защищённый /etc), — свои точные пути убрать.
if [ -n "$PKGS" ]; then
	rm -rf /usr/share/dpishell /usr/share/ucode/dpishell /usr/libexec/dpishell \
		/www/luci-static/resources/dpishell /www/luci-static/resources/view/dpishell /www/dpi
	rm -f /etc/init.d/dpishell /etc/rc.d/S95dpishell /etc/hotplug.d/button/50-dpishell /lib/upgrade/keep.d/dpishell \
		/usr/share/rpcd/ucode/dpishell /usr/share/rpcd/acl.d/dpishell.json /usr/share/luci/menu.d/luci-app-dpishell.json
fi
if [ -z "$KEEP" ]; then
	rm -rf /etc/dpishell
	rm -f /etc/config/dpishell /etc/config/dpishell-opkg /etc/config/dpishell.apk-new /etc/apk/keys/dpishell.pem
	[ -f /etc/apk/repositories.d/customfeeds.list ] && sed -i '\#^https://slavdew\.github\.io/dpishell/feed/#d' /etc/apk/repositories.d/customfeeds.list
fi

# 9. Страницы и объект ubus — без пакета их не должно остаться в меню и в rpcd. Кеш LuCI — как у postinst пакетов LuCI.
if [ -n "$PKGS" ]; then
	rm -rf /tmp/luci-indexcache* /tmp/luci-modulecache
	/etc/init.d/rpcd restart >/dev/null 2>&1
	if pkg_installed dpishell; then
		say false false 'Пакет dpishell не удалился — см. apk del dpishell'
		exit 1
	fi
	say false true "$([ -n "$KEEP" ] && echo 'DpiShell удалён, настройки сохранены' || echo 'DpiShell удалён полностью')"
fi
# Своя папка, из которой запустил метод uninstall (mktemp в rpcd), — убрать.
case "$0" in /tmp/dpishell-uninst.*/uninstall.sh) rm -rf "${0%/uninstall.sh}" ;; esac
exit 0
