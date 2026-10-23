#!/bin/sh
# «Полный флеш» внутри эмулятора (зовёт router/test/vm-fullflash.sh с компьютера; каталог — /tmp/dpff).
# weak-router-test.md: на почти полном флеше DNS «Google» ставил https-dns-proxy без libcares, uci commit оставлял пустой
# /etc/config/dhcp (нет DNS и DHCP), а задание писало «возвращено как было». Теперь: честный отказ до изменений, dhcp цел,
# DNS отвечает; обновление — тоже отказ; неудачная установка пакета убирает своё. Заглушка убирается на любом выходе.
# Вывод — строки «ok …» / «FAIL …».
D=/tmp/dpff
FILL=/root/dpff-fill
LEFT_KB=${LEFT_KB:-200}

ok() { echo "ok   $*"; }
fail() { echo "FAIL $*"; }
check() { name=$1; shift; if "$@" >/dev/null 2>&1; then ok "$name"; else fail "$name"; fi; }
jf() { jsonfilter -i "$1" -e "$2" 2>/dev/null; }
free_kb() { df -k /overlay 2>/dev/null | awk 'NR==2{print $4}' | grep . || df -k / | awk 'NR==2{print $4}'; }
resolves() { nslookup example.com 127.0.0.1 2>/dev/null | grep -q 'Address:.*[0-9]\.[0-9]'; }
dns_ok() { pidof dnsmasq >/dev/null && resolves; }
cleanup() { rm -f "$FILL"; sync; }
trap cleanup EXIT INT TERM

# Снимок того, что не должно меняться: настройки dhcp/network (uci export), пакеты, world, файлы прокси.
snap() {
	S=$D/snap-$1; rm -rf "$S"; mkdir -p "$S"
	uci export dhcp > "$S/dhcp" 2>&1; uci export network > "$S/network" 2>&1
	md5sum /etc/config/dhcp | cut -d' ' -f1 > "$S/dhcp.md5"
	apk list -I 2>/dev/null | cut -d' ' -f1 | sort > "$S/packages"
	# Наши пакеты, пришпиленные к файлу («dpishell><Q1…»), DpiShell сам переводит в «dpishell» (pkgs.unpin) — это не разница.
	tr ' ' '\n' < /etc/apk/world | sed '/^$/d; s/^\(dpishell\|luci-app-dpishell\)><.*/\1/' | sort > "$S/world"
	for f in /usr/sbin/https-dns-proxy /etc/init.d/https-dns-proxy /etc/config/https-dns-proxy /lib/apk/packages/https-dns-proxy.list \
		/usr/lib/libcares.so.2; do [ -e "$f" ] && echo "$f"; done > "$S/proxy-files"
}
same() {   # same <что> <часть>
	if cmp -s "$D/snap-0/$2" "$D/snap-1/$2"; then ok "$1"; else fail "$1"; { echo "было:"; head -8 "$D/snap-0/$2"; echo "стало:"; head -8 "$D/snap-1/$2"; } | sed 's/^/     /'; fi
}

# Задание DNS: дождаться итога именно этого вызова (at ≥ t0).
dns_job() {   # dns_job <метод> [<json>]
	rm -f /tmp/dpishell/dns_job.json; t0=$(date +%s)
	args=${2:-'{}'}
	ubus call dpishell "$1" "$args" >/dev/null || { fail "$1: вызов"; return 1; }
	i=0
	while [ $i -lt 240 ]; do
		f=/tmp/dpishell/dns_job.json
		[ "$(jf $f '@.action')" = "$1" ] && [ "$(jf $f '@.running')" = false ] && [ "$(jf $f '@.at' || echo 0)" -ge "$t0" ] 2>/dev/null && return 0
		sleep 1; i=$((i + 1))
	done
	fail "$1: задание не закончилось за 240 с"; return 1
}

# Оптимизация в эмуляторе рвёт собственные соединения роутера (DoH не проверить) — на время проверки выключить, потом вернуть.
WAS_ON=$(uci -q get dpishell.main.enabled)
if [ "$WAS_ON" = 1 ]; then
	ubus call dpishell disable '{}' >/dev/null; sleep 2
	trap 'cleanup; ubus call dpishell enable "{}" >/dev/null' EXIT INT TERM
fi

echo "Пакет: $(grep -A1 '^P:dpishell$' /lib/apk/db/installed | sed -n 's/^V://p'), свободно $(free_kb) КБ"

# Исходно: DNS провайдера, прокси нет (остатки прежних сбоев — убрать до проверки, иначе сверять не с чем).
[ "$(uci -q get dpishell.main.dns_preset)" ] && dns_job dns_restore
check "исходно: DNS роутера отвечает" dns_ok
check "исходно: https-dns-proxy не установлен" sh -c '! grep -qx "P:https-dns-proxy" /lib/apk/db/installed'
check "исходно: файлов прокси нет" sh -c '! ls /usr/sbin/https-dns-proxy /etc/init.d/https-dns-proxy 2>/dev/null | grep -q .'
snap 0

echo "== Заполнение флеша (оставить ${LEFT_KB} КБ)"
f=$(free_kb)
dd if=/dev/zero of="$FILL" bs=1024 count=$((f - LEFT_KB)) 2>/dev/null; sync
echo "свободно: $(free_kb) КБ"
check "флеш почти полон (< 1 МБ)" [ "$(free_kb)" -lt 1024 ]

echo "== Предупреждение flash_full (status, кеш фактов до 60 с)"
i=0; while [ $i -lt 75 ] && ! ubus call dpishell status | grep -q '"code": "flash_full"'; do sleep 3; i=$((i + 3)); done
check "status.warnings: flash_full" sh -c 'ubus call dpishell status | grep -q "\"code\": \"flash_full\""'

echo "== DNS «Google» на полном флеше"
dns_job dns_set '{"preset":"google"}'
J=/tmp/dpishell/dns_job.json
echo "     итог: $(jf $J '@.message')"
check "DNS Google: отказ (ok=false)" [ "$(jf $J '@.ok')" = false ]
check "DNS Google: причина — мало места (web.err.flash_space)" [ "$(jf $J '@.message_msg.key')" = web.err.flash_space ]
check "DNS Google: в тексте «нужно … МБ, свободно … МБ»" sh -c "jsonfilter -i $J -e '@.message' | grep -q 'нужно .* МБ, свободно .* МБ'"
check "DNS роутера отвечает" dns_ok
check "dnsmasq запущен" pidof dnsmasq
check "/etc/config/dhcp не пустой" test -s /etc/config/dhcp
check "dns_status: DNS провайдера" sh -c 'ubus call dpishell dns_status | grep -q "\"mode\": \"provider\""'
snap 1
same "dhcp как было" dhcp
same "dhcp — файл не переписан" dhcp.md5
same "network как было" network
same "пакеты как были" packages
same "world как был" world
same "файлов прокси не появилось" proxy-files

echo "== Обновление из папки окна ПК на полном флеше"
want=$(ls $D/next/ | sed -n 's/^dpishell-\(.*\)\.apk$/\1/p')
was=$(grep -A1 '^P:dpishell$' /lib/apk/db/installed | sed -n 's/^V://p')
rm -rf /tmp/dpishell-update; mkdir -m 700 /tmp/dpishell-update
cp $D/next/*.apk /tmp/dpishell-update/
(cd /tmp/dpishell-update && sha256sum *.apk > SHA256SUMS)
rm -f /tmp/dpishell/update_job.json; t0=$(date +%s)
ubus call dpishell update_install '{"from":"local"}' >/dev/null
i=0
while [ $i -lt 120 ]; do
	U=/tmp/dpishell/update_job.json
	[ "$(jf $U '@.running')" = false ] && [ "$(jf $U '@.at' || echo 0)" -ge "$t0" ] 2>/dev/null && break
	sleep 1; i=$((i + 1))
done
echo "     итог: $(jf $U '@.message')"
check "обновление: отказ (ok=false)" [ "$(jf $U '@.ok')" = false ]
check "обновление: причина — мало места (web.err.flash_space)" [ "$(jf $U '@.message_msg.key')" = web.err.flash_space ]
check "обновление: стоит прежняя версия $was (не $want)" grep -qx "V:$was" /lib/apk/db/installed
check "обновление: ubus dpishell отвечает" ubus -t 5 call dpishell status
check "обновление: папка окна убрана" sh -c '! test -e /tmp/dpishell-update'

echo "== Заглушка убрана"
cleanup
check "свободно снова > 2 МБ" [ "$(free_kb)" -gt 2048 ]

echo "== DNS «Google», когда места впритык (оставить ${LEFT2_KB:-2600} КБ): ставится или честно возвращается, dhcp цел"
if [ -z "$NO_NET" ]; then
	snap 0
	f=$(free_kb)
	dd if=/dev/zero of="$FILL" bs=1024 count=$((f - ${LEFT2_KB:-2600})) 2>/dev/null; sync
	echo "свободно: $(free_kb) КБ"
	dns_job dns_set '{"preset":"google"}'
	echo "     итог: $(jf $J '@.message')"
	if [ "$(jf $J '@.ok')" = true ]; then
		ok "впритык: DNS Google включён"
	else
		check "впритык: отказ честный (мало места или возвращено как было)" sh -c "jsonfilter -i $J -e '@.message_msg.key' | grep -qxE 'web.err.(flash_space|dns_reverted|apk_space)'"
	fi
	check "впритык: DNS роутера отвечает" dns_ok
	check "впритык: /etc/config/dhcp не пустой и читается" sh -c 'test -s /etc/config/dhcp && uci -q show dhcp | grep -q dnsmasq'
	dns_job dns_restore
	check "впритык: возврат ок" [ "$(jf $J '@.ok')" = true ]
	cleanup
	snap 1
	same "впритык, после возврата: dhcp как было" dhcp
	same "впритык, после возврата: пакеты как были" packages
	same "впритык, после возврата: файлов прокси нет" proxy-files
	check "впритык, после возврата: DNS роутера отвечает" dns_ok
fi

echo "== Неудачная установка пакета убирает своё (pkgs.add, проверка после установки не прошла)"
if [ -z "$NO_NET" ] && apk update >/dev/null 2>&1; then
	snap 0
	cat > $D/add.uc <<'EOF'
import * as pk from 'dpishell.pkgs';
let e = pk.add(['https-dns-proxy'], 'https-dns-proxy', () => 'проверка теста');
print(e ? e.key : 'null', '\n');
EOF
	r=$(ucode -L /usr/share/ucode $D/add.uc 2>&1)
	echo "     итог: $r"
	check "pkgs.add: ошибка web.err.apk_broken" [ "$r" = web.err.apk_broken ]
	# Прокси при старте/стопе правит dhcp — вернуть его настройки, как dns.uc (guard), здесь не наше дело: только пакеты и файлы.
	/etc/init.d/dnsmasq restart >/dev/null 2>&1; sleep 2
	snap 1
	same "после уборки: пакеты как были" packages
	same "после уборки: world как был" world
	same "после уборки: файлов прокси нет" proxy-files
	check "после уборки: DNS роутера отвечает" dns_ok
else
	echo "     (нет связи с источником пакетов — пропущено)"
fi
