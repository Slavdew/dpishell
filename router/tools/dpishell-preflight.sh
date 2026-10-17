#!/bin/sh
# Предпроверка роутера для установки DpiShell (окно ПК передаёт скрипт по SSH: ssh root@<роутер> 'sh -s' < dpishell-preflight.sh).
# Ничего не меняет. Ответ — одна строка JSON (schema 1, 07 §9).
#   sh -s                     — годится ли роутер: OpenWrt, пакетный менеджер, fw4/nftables, ядро и kmod-nft-queue, сеть, место, чужой обход
#   sh -s -- --update         — то же, но сначала обновить индексы пакетов (apk update / opkg update; можно через https_proxy)
#   sh -s -- --after-uninstall — что осталось после удаления DpiShell (left: [] — ничего)
MODE=check
for a in "$@"; do
	case "$a" in
		--update) UPDATE=1 ;;
		--after-uninstall) MODE=after ;;
	esac
done

# --- JSON ---
js() { printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\000-\037')"; }
jn() { [ -n "$1" ] && js "$1" || printf 'null'; }
jb() { [ "$1" = 1 ] && printf 'true' || printf 'false'; }
num() { case "$1" in ''|*[!0-9]*) printf 'null' ;; *) printf '%s' "$1" ;; esac; }

rel() { sed -n "s/^$1='\(.*\)'/\1/p" /etc/openwrt_release 2>/dev/null; }

PKG=
[ -x /usr/bin/apk ] && PKG=apk
[ -z "$PKG" ] && [ -x /bin/opkg ] && PKG=opkg

installed() {
	[ -f "/lib/apk/packages/$1.list" ] || [ -f "/usr/lib/opkg/info/$1.control" ]
}

pkg_version() {
	# apk 3: «apk info -v» выводит описание, а не версию — берём из базы установленных (P:/V:).
	if [ "$PKG" = apk ]; then
		awk -v n="$1" '$0 == "P:" n { f = 1; next } f && /^V:/ { print substr($0, 3); exit } /^$/ { f = 0 }' /lib/apk/db/installed 2>/dev/null
	elif [ "$PKG" = opkg ]; then
		opkg status "$1" 2>/dev/null | sed -n 's/^Version: //p' | head -1
	fi
}

# Версия в индексе фида (кэш индексов; --update — свежий).
avail_version() {
	if [ "$PKG" = apk ]; then
		apk search -x -v "$1" 2>/dev/null | sed -n "s/^$1-\([0-9][^ ]*\).*/\1/p" | head -1
	elif [ "$PKG" = opkg ]; then
		opkg info "$1" 2>/dev/null | sed -n 's/^Version: //p' | head -1
	fi
}

# Доступен ли адрес: curl, иначе wget (uclient-fetch в голом OpenWrt).
reach() {
	if command -v curl >/dev/null 2>&1; then
		[ "$(curl -s -o /dev/null --max-time 6 -w '%{http_code}' "$1" 2>/dev/null)" = 200 ]
	else
		wget -q -T 6 -O /dev/null "$1" >/dev/null 2>&1
	fi
}

free_kb() { df -k "$1" 2>/dev/null | awk 'NR == 2 { print $4 }'; }

ours_zapret() {
	[ "$(readlink /etc/init.d/zapret 2>/dev/null)" = /opt/zapret/init.d/openwrt/zapret ] && installed dpishell
}

foreign_json() {
	first=1
	item() { [ -n "$first" ] || printf ','; first=; printf '{"kind":"%s","name":%s}' "$1" "$(js "$2")"; }
	for p in zapret luci-app-zapret zapret-manager-beam zapret2 luci-app-zapret2 youtubeUnblock luci-app-youtubeUnblock b4 byedpi podkop; do
		installed "$p" && item package "$p"
	done
	for s in zapret2 youtubeUnblock b4 byedpi podkop zapret-manager zapret-manager-beam; do
		[ -e "/etc/init.d/$s" ] && item service "$s"
	done
	[ -e /etc/init.d/zapret ] && ! ours_zapret && ! installed zapret && item service zapret
	for pid in $(pidof nfqws nfqws2 byedpi youtubeUnblock 2>/dev/null); do
		exe=$(readlink "/proc/$pid/exe" 2>/dev/null)
		[ "$exe" = /opt/zapret/nfq/nfqws ] && ours_zapret && continue
		item process "${exe:-$pid}"
	done
}

if [ "$MODE" = after ]; then
	# Что осталось от DpiShell после удаления.
	first=1
	left() { [ -n "$first" ] || printf ','; first=; printf '{"what":%s,"detail":%s}' "$(js "$1")" "$(jn "$2")"; }
	{
		printf '{"schema":1,"mode":"after-uninstall","left":['
		for p in dpishell luci-app-dpishell; do installed "$p" && left package "$p"; done
		pids=$(pidof nfqws 2>/dev/null); [ -n "$pids" ] && left process "nfqws $pids"
		nft list table inet zapret >/dev/null 2>&1 && left nft "table inet zapret"
		grep -qs zapret /etc/config/firewall && left firewall "/etc/config/firewall: zapret"
		ls /usr/share/nftables.d/*/*zapret* >/dev/null 2>&1 && left firewall "/usr/share/nftables.d: zapret"
		grep -qsiE 'dpishell|zapret' /etc/crontabs/root && left cron "$(grep -iE 'dpishell|zapret' /etc/crontabs/root | head -1)"
		for f in /etc/init.d/dpishell /etc/init.d/zapret /etc/hotplug.d/iface/90-zapret /opt/zapret /www/dpishell /www/dpi /usr/share/dpishell /tmp/dpishell; do
			[ -e "$f" ] || [ -L "$f" ] && left file "$f"
		done
		grep -qs '127.0.0.1#505' /etc/config/dhcp && left dns "dnsmasq → 127.0.0.1#505x"
		[ "$(uci -q get firewall.@defaults[0].flow_offloading)" != 1 ] && [ -n "$(uci -q get dpishell.main.fw_offload_prev 2>/dev/null)" ] && left offload "разгрузка не возвращена межсетевому экрану"
		printf '],"kept":{"settings":%s,"https_dns_proxy":%s}}\n' \
			"$(jb "$([ -e /etc/dpishell ] || [ -e /etc/config/dpishell ] && echo 1)")" \
			"$(jb "$(installed https-dns-proxy && echo 1)")"
	}
	exit 0
fi

if [ -n "$UPDATE" ]; then
	if [ "$PKG" = apk ]; then apk update >/dev/null 2>&1; elif [ "$PKG" = opkg ]; then opkg update >/dev/null 2>&1; fi
fi

KERNEL=$(uname -r)
KQ_AVAIL=$(avail_version kmod-nft-queue)
KQ_INST=$(pkg_version kmod-nft-queue)
KQ_V=${KQ_INST:-$KQ_AVAIL}
case "$KQ_V" in "$KERNEL"*) KQ_MATCH=1 ;; *) KQ_MATCH= ;; esac
# Модуль может быть собран в ядро, а не пакетом.
[ -z "$KQ_V" ] && [ -e "/lib/modules/$KERNEL/nft_queue.ko" ] && KQ_MATCH=1

FW_FEED=$(cat /etc/apk/repositories.d/*.list /etc/apk/repositories /etc/opkg/distfeeds.conf 2>/dev/null | sed -n 's#^\(src/gz [^ ]* \)\{0,1\}\(https\{0,1\}://[^ ]*\).*#\2#p' | head -1)
DPI_VER=$(pkg_version dpishell)
DPI_API=
[ -n "$DPI_VER" ] && DPI_API=$(ubus call dpishell status 2>/dev/null | jsonfilter -e '@.api' 2>/dev/null)

printf '{"schema":1,"mode":"check"'
printf ',"openwrt":{"release":%s,"revision":%s,"arch":%s,"target":%s}' "$(jn "$(rel DISTRIB_RELEASE)")" "$(jn "$(rel DISTRIB_REVISION)")" "$(jn "$(rel DISTRIB_ARCH)")" "$(jn "$(rel DISTRIB_TARGET)")"
printf ',"pkg":%s' "$(jn "$PKG")"
printf ',"fw4":%s,"nft":%s' "$(jb "$([ -x /sbin/fw4 ] && echo 1)")" "$(jb "$(command -v nft >/dev/null 2>&1 && echo 1)")"
printf ',"kernel":%s' "$(js "$KERNEL")"
printf ',"kmod_nft_queue":{"installed":%s,"available":%s,"version":%s,"matches_kernel":%s}' \
	"$(jb "$(installed kmod-nft-queue && echo 1)")" "$(jb "$([ -n "$KQ_AVAIL" ] && echo 1)")" "$(jn "$KQ_V")" "$(jb "$KQ_MATCH")"
printf ',"net":{"feed":%s,"fw_feed":%s,"github":%s}' \
	"$(jb "$(reach https://slavdew.github.io/dpishell/feed/dpishell.pem && echo 1)")" \
	"$(jb "$([ -n "$FW_FEED" ] && reach "$FW_FEED" && echo 1)")" \
	"$(jb "$(reach https://github.com/ && echo 1)")"
printf ',"space":{"overlay_free_kb":%s,"tmp_free_kb":%s}' "$(num "$(free_kb /overlay)")" "$(num "$(free_kb /tmp)")"
printf ',"foreign":[%s]' "$(foreign_json)"
if [ -n "$DPI_VER" ]; then
	printf ',"dpishell":{"version":%s,"api":%s}' "$(js "$DPI_VER")" "$(jn "$DPI_API")"
else
	printf ',"dpishell":null'
fi
printf ',"luci":%s}\n' "$(jb "$(installed luci-base && echo 1)")"
