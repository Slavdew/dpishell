#!/bin/sh
# Установка, обновление со старой версии, откат каждой функции и удаление — в эмуляторе, со сверкой со снимком роутера
# до установки. Запуск из корня репозитория (как vm-test): sh router/test/vm-lifecycle.sh
#   OLD_REF  — прежняя версия для обновления (git-ref; по умолчанию HEAD — рабочая папка считается новой версией)
#   OLD2_REF — ещё одна прежняя (по умолчанию — r10, cdc77ac^); пусто — пропустить
#   CORE_ZIP — архив ядра zapret-v72.13.zip (иначе роутер качает его с GitHub при каждой установке)
#   SHOTS    — папка для снимков страниц (по умолчанию — временная); NO_SHOTS=1 — без снимков
#   KEEP_INSTALLED=0 — после проверки не ставить новую версию обратно (по умолчанию ставит, с ядром и включённой оптимизацией)
# Что делает (decisions-owner-2026-10-20.md, «Ресурсы роутера»): снимок исходного состояния (uci dhcp/firewall/network/
# https-dns-proxy, пакеты, /etc/apk/world, crontab, nft без таблицы zapret, файлы, следы dpishell/zapret, здоровье служб) →
# установка старой версии → обновление из папки окна ПК → включение, DNS сети, свои списки, продвинутый режим, сторож,
# совместная работа с ПК → откат каждой функции со сверкой → «Изменения DpiShell» (отмена пункта и всех) → откат версии
# (через папку окна ПК на прежнюю версию без новых методов, из флеша) и обратно → удаление с сохранением настроек и повторная установка →
# полное удаление → сверка с исходным снимком (допустимая разница перечислена ниже) → LuCI открывается, страницы без
# ошибок JS (Edge headless, router/tools/luci-shot.mjs). Итог — PASS/FAIL по пунктам; код выхода 1 — есть FAIL.
VM=${VM:-/c/Dev/owrt-vm}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OLD_REF=${OLD_REF:-HEAD}
OLD2_REF=${OLD2_REF-cdc77ac^}
SSH="$VM/ssh.sh"
WORK=$(mktemp -d)
SHOTS=${SHOTS:-$WORK/shots}
RES=$WORK/results.txt
: > "$RES"

say() { printf '\n== %s\n' "$*"; }
# Строки «ok …»/«FAIL …» из эмулятора и свои — в итог.
collect() { tee -a "$RES"; }
pass() { echo "ok   $*" | collect; }
failx() { echo "FAIL $*" | collect; }
vm() { "$SSH" "$@"; }
step() { vm "sh /tmp/dpslc/lifecycle_vm.sh $*" | collect; }

# Сверка частей снимков (diff на компьютере). allow — sed-выражение строк, разница в которых допустима.
pull_snap() { rm -rf "$WORK/snap/$1"; mkdir -p "$WORK/snap"; vm "tar -C /tmp/dpslc/snap -cf - $1" | tar -C "$WORK/snap" -xf -; }
same() {   # same <что> <снимок-а> <снимок-б> <часть> [allow-regex]
	a="$WORK/snap/$2/$4"; b="$WORK/snap/$3/$4"
	if [ -n "$5" ]; then grep -Ev "$5" "$a" > "$a.f"; grep -Ev "$5" "$b" > "$b.f"; a="$a.f"; b="$b.f"; fi
	if diff -u "$a" "$b" > "$WORK/diff.txt"; then pass "$1"; else failx "$1"; sed 's/^/     /' "$WORK/diff.txt" | head -30; fi
}
shots() {   # shots <метка> <пути…>
	[ -n "$NO_SHOTS" ] && return 0
	label=$1; shift
	node "$ROOT/router/tools/luci-shot.mjs" "$SHOTS" "$label" "$@" > "$WORK/shots.json" 2>&1
	while read -r l; do
		p=$(echo "$l" | sed -n 's/.*"path":"\([^"]*\)".*/\1/p')
		case "$l" in *'"ok":true'*) pass "панель $label: $p" ;; *) failx "панель $label: ${p:-$l}"; echo "     $l" ;; esac
	done < "$WORK/shots.json"
}

PAGES_STD="admin/status/overview admin/network/network"
PAGES_DPI="admin/services/dpishell/main admin/services/dpishell/select admin/services/dpishell/lists admin/services/dpishell/dns admin/services/dpishell/diag admin/services/dpishell/about"

say 'Пакеты'
python -I "$ROOT/router/tools/vm-mkapk.py" "$WORK/pk/new" > /dev/null || exit 1
# Следующая сборка той же рабочей папки (номер сборки +1): откат версии с роутера и обратно.
RELEASE_ADD=1 python -I "$ROOT/router/tools/vm-mkapk.py" "$WORK/pk/new2" > /dev/null || exit 1
python -I "$ROOT/router/tools/vm-mkapk.py" "$WORK/pk/old" "$OLD_REF" > /dev/null || exit 1
[ -n "$OLD2_REF" ] && { python -I "$ROOT/router/tools/vm-mkapk.py" "$WORK/pk/old2" "$OLD2_REF" > /dev/null || exit 1; }
ls "$WORK"/pk/*/
cp "$ROOT/router/test/lifecycle_vm.sh" "$ROOT/router/dpishell/files/usr/libexec/dpishell/uninstall.sh" "$WORK/pk/"
[ -n "$CORE_ZIP" ] && [ -f "$CORE_ZIP" ] && cp "$CORE_ZIP" "$WORK/pk/core.zip"
tar -C "$WORK/pk" --owner=0 --group=0 -cf - . | vm 'rm -rf /tmp/dpslc && mkdir -p /tmp/dpslc && tar -C /tmp/dpslc -xf -'

say 'Исходное состояние: DpiShell нет, у пользователя включено ускорение сети (flow offloading)'
step purge
FO_ORIG=$(vm 'uci -q get firewall.@defaults[0].flow_offloading')
vm 'uci set firewall.@defaults[0].flow_offloading=1; uci commit firewall; /etc/init.d/firewall reload >/dev/null 2>&1'
step snap s0; pull_snap s0
shots before $PAGES_STD

upgrade_from() {   # upgrade_from <набор> <ref>
	say "Установка прежней версии ($2) и обновление из папки окна ПК"
	step install "$1"
	step core
	step enable alt
	# Следы прежних версий: итог подбора в results/ (r14 их не пишет и убирает), ссылка на ipset-all.txt.
	vm 'mkdir -p /etc/dpishell/results && echo "{}" > /etc/dpishell/results/1700000000.json; ls /etc/dpishell/lists/ipset-all.txt >/dev/null 2>&1; true'
	vm 'uci get dpishell.main.strategy' > "$WORK/strategy.before"
	step update_local new
	vm '/usr/libexec/dpishell/run tick; sleep 1'
	r=$(vm 'test -e /etc/dpishell/results && echo T_RESULTS; test -L /etc/dpishell/lists/ipset-all.txt && echo T_IPSET; test -e /tmp/dpishell-update && echo T_UPD;
		test -L /www/dpishell/status.json && echo T_STATUS; pidof nfqws >/dev/null && echo T_NFQWS; uci get dpishell.main.enabled; uci get dpishell.main.strategy')
	case "$r" in *T_RESULTS*) failx "после обновления: results/ убран" ;; *) pass "после обновления: results/ убран" ;; esac
	case "$r" in *T_IPSET*) failx "после обновления: ссылка ipset-all убрана" ;; *) pass "после обновления: ссылка ipset-all убрана" ;; esac
	case "$r" in *T_UPD*) failx "после обновления: /tmp/dpishell-update убрана" ;; *) pass "после обновления: /tmp/dpishell-update убрана" ;; esac
	case "$r" in *T_STATUS*) pass "status.json — ссылка в ОЗУ" ;; *) failx "status.json — ссылка в ОЗУ" ;; esac
	case "$r" in *T_NFQWS*) pass "после обновления: оптимизация работает" ;; *) failx "после обновления: оптимизация работает" ;; esac
	if [ "$(echo "$r" | tail -1)" = "$(cat "$WORK/strategy.before")" ]; then pass "после обновления: стратегия та же"; else failx "после обновления: стратегия та же"; fi
	vm 'curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1/dpishell/status.json' | grep -q 200 && pass 'status.json отдаётся (200)' || failx 'status.json отдаётся (200)'
	vm "sh /tmp/dpslc/lifecycle_vm.sh snap up-$1" > /dev/null
}

if [ -n "$OLD2_REF" ]; then
	upgrade_from old2 "$OLD2_REF"
	step uninstall full
fi
upgrade_from old "$OLD_REF"
shots after-update $PAGES_DPI

say 'Функции и откат каждой по отдельности'
step snap s1; pull_snap s1

# DNS сети: Google (DoH, ставит https-dns-proxy) → «как было у провайдера». В эмуляторе оптимизация в режиме «все сайты»
# рвёт собственные соединения роутера (сеть QEMU user пересобирает TCP) — проверка DoH не прошла бы; DNS — при выключенной.
vm "ubus call dpishell disable >/dev/null; sleep 1"
step dns_set google
vm 'uci -q get dhcp.@dnsmasq[0].noresolv; uci -q get dhcp.@dnsmasq[0].server' | grep -q . && pass 'DNS Google: dnsmasq перенастроен' || failx 'DNS Google: dnsmasq перенастроен'
step dns_set provider
step snap s1-dns; pull_snap s1-dns
same 'DNS «как было»: uci dhcp' s1 s1-dns uci-dhcp
same 'DNS «как было»: https-dns-proxy' s1 s1-dns uci-https-dns-proxy
same 'DNS «как было»: пакеты' s1 s1-dns packages
same 'DNS «как было»: world' s1 s1-dns world
same 'DNS «как было»: network' s1 s1-dns uci-network
same 'DNS «как было»: настройки DpiShell' s1 s1-dns uci-dpishell 'option enabled'
step enable "$(cat "$WORK/strategy.before")"

# Свои списки.
vm "ubus call dpishell lists_set '{\"name\":\"user-general\",\"lines\":[\"example.org\"]}' >/dev/null"
vm 'grep -qx example.org /etc/dpishell/lists/user-general.txt' && pass 'свой список записан' || failx 'свой список записан'
vm "ubus call dpishell lists_set '{\"name\":\"user-general\",\"lines\":[]}' >/dev/null"
vm 'test ! -s /etc/dpishell/lists/user-general.txt' && pass 'свой список очищен' || failx 'свой список очищен'

# Продвинутый режим → advanced_revert.
vm 'md5sum /opt/zapret/config; cat /etc/dpishell/advanced.json 2>/dev/null' > "$WORK/adv.before"
vm "ubus call dpishell advanced_set '{\"enabled\":true,\"others\":\"lists\",\"rows\":[{\"domains\":[\"example.net\"],\"strategy\":\"auto\"}]}' >/dev/null"
vm 'grep -q example.net /opt/zapret/config' && pass 'продвинутый режим в config' || failx 'продвинутый режим в config'
vm "ubus call dpishell advanced_revert >/dev/null"
vm 'md5sum /opt/zapret/config; cat /etc/dpishell/advanced.json 2>/dev/null' > "$WORK/adv.after"
diff -q "$WORK/adv.before" "$WORK/adv.after" >/dev/null && pass 'advanced_revert: config и настройки как были' || failx 'advanced_revert: config и настройки как были'

# Сторож и расписание.
vm "ubus call dpishell schedule_set '{\"watchdog\":false}' >/dev/null; uci get dpishell.main.watchdog" | grep -qx 0 && pass 'сторож выключен' || failx 'сторож выключен'
vm "ubus call dpishell schedule_set '{\"watchdog\":true}' >/dev/null"

# Совместная работа с ПК: связка, продление без записи во флеш, снятие.
vm 'md5sum /opt/zapret/config' > "$WORK/pc.before"
vm "ubus call dpishell pc_link_set '{\"id\":\"test-pc-0001\",\"macs\":[\"02:00:00:00:00:01\"],\"domains\":[\"example.com\"]}' >/dev/null; sleep 2"
vm 'grep -q 02:00:00:00:00:01 /opt/zapret/config' && pass 'связка с ПК в config' || failx 'связка с ПК в config'
m1=$(vm 'date -r /etc/dpishell/pc-links.json +%s; md5sum /etc/dpishell/pc-links.json')
vm 'sleep 2'; vm "ubus call dpishell pc_link_set '{\"id\":\"test-pc-0001\",\"macs\":[\"02:00:00:00:00:01\"],\"domains\":[\"example.com\"]}' >/dev/null"
m2=$(vm 'date -r /etc/dpishell/pc-links.json +%s; md5sum /etc/dpishell/pc-links.json')
[ "$m1" = "$m2" ] && vm 'test -f /tmp/dpishell/pc-expiry.json' && pass 'продление связки — без записи во флеш' || failx 'продление связки — без записи во флеш'
vm "ubus call dpishell pc_link_del '{\"id\":\"test-pc-0001\"}' >/dev/null; sleep 2"
vm 'md5sum /opt/zapret/config' > "$WORK/pc.after"
diff -q "$WORK/pc.before" "$WORK/pc.after" >/dev/null && pass 'связка снята: config как был' || failx 'связка снята: config как был'

# Выключение и включение.
vm "ubus call dpishell disable >/dev/null; sleep 2"
vm '! pidof nfqws >/dev/null && ! /etc/init.d/zapret enabled' && pass 'выключено: nfqws нет, автозапуска нет' || failx 'выключено: nfqws нет, автозапуска нет'
step enable "$(cat "$WORK/strategy.before")"
step snap s1-end; pull_snap s1-end
same 'после откатов: uci dhcp/firewall/network' s1 s1-end uci-dhcp
same 'после откатов: firewall' s1 s1-end uci-firewall
same 'после откатов: настройки DpiShell' s1 s1-end uci-dpishell
same 'после откатов: пакеты' s1 s1-end packages

say '«Изменения DpiShell»: отмена по пунктам и «Отменить всё»'
STRAT=$(cat "$WORK/strategy.before")
step changes "$STRAT"
step enable "$STRAT"
shots changes admin/services/dpishell/diag

say 'Откат версии и обратно'
ver() { ls "$WORK/pk/$1/" | sed -n 's/^dpishell-\(.*\)\.apk$/\1/p'; }
V_OLD=$(ver old); V_NEW=$(ver new); V_NEW2=$(ver new2)
rb() { vm "ubus call dpishell status | jsonfilter -e '@.rollback.version' -e '@.rollback.saved'" | tr '\n' ' '; }
# Настройки, которые откат и обновление должны сохранить: uci dpishell, свои файлы (кроме пакетов отката), работает ли обход.
keep_state() { vm 'uci export dpishell; cd /etc/dpishell && for f in user-targets.json user-strategies.json found-strategies.json advanced.json pc-links.json dns-custom.json lists/user-*.txt; do
	[ -f "$f" ] && md5sum "$f"; done; pidof nfqws >/dev/null && echo nfqws'; }
keep_state > "$WORK/keep.before"
# Прежнюю версию ставила прежняя версия пакета: её пакетов на роутере нет — откат только через окно ПК (папка обновления).
[ "$(rb)" = "$V_OLD false " ] && pass "откат: прежняя $V_OLD, пакеты не сохранены" || failx "откат: прежняя $V_OLD, пакеты не сохранены ($(rb))"
vm "ubus call dpishell update_rollback '{\"from\":\"saved\"}'" | grep -q '"invalid"' && pass 'откат из флеша без пакетов — отказ' || failx 'откат из флеша без пакетов — отказ'
step rollback local old
keep_state > "$WORK/keep.after"
diff "$WORK/keep.before" "$WORK/keep.after" > /dev/null && pass "откат на $V_OLD: настройки и оптимизация как были" || { failx "откат на $V_OLD: настройки и оптимизация как были"; diff "$WORK/keep.before" "$WORK/keep.after" | head -10; }
# Версия без новых методов: окно и страница их не зовут (status.methods), страницы открываются без ошибок.
vm 'ubus call dpishell status | jsonfilter -e "@.methods[*]"' | grep -qx changes_list && failx "$V_OLD: changes_list в status.methods" || pass "$V_OLD: changes_list в status.methods нет"
shots "rollback-$V_OLD" admin/services/dpishell/diag admin/services/dpishell/about
step update_local new
step update_local new2
[ "$(rb)" = "$V_NEW false " ] && pass "после $V_NEW2: прежняя $V_NEW без пакетов (их ставила $V_OLD)" || failx "после $V_NEW2: прежняя $V_NEW без пакетов ($(rb))"
step rollback local new
step update_local new2
[ "$(rb)" = "$V_NEW true " ] && pass "после $V_NEW2: прежняя $V_NEW сохранена во флеше" || failx "после $V_NEW2: прежняя $V_NEW сохранена во флеше ($(rb))"
r=$(vm 'ls /etc/dpishell/pkg/cur /etc/dpishell/pkg/prev 2>/dev/null | grep -c "\.apk$"; du -sk /etc/dpishell/pkg | cut -f1; ls -ld /etc/dpishell/pkg | cut -c1-10')
echo "$r" | head -1 | grep -qx 4 && pass 'флеш: ровно установленная и одна прежняя пара' || failx "флеш: ровно установленная и одна прежняя пара ($r)"
echo "     пакеты отката во флеше: $(echo "$r" | sed -n 2p) КБ"
echo "$r" | tail -1 | grep -qx "drwx------" && pass 'флеш: каталог пакетов только root (700)' || failx "флеш: каталог пакетов только root ($r)"
shots "rollback-ready" admin/services/dpishell/about
keep_state > "$WORK/keep.before"
step rollback saved
keep_state > "$WORK/keep.after"
diff "$WORK/keep.before" "$WORK/keep.after" > /dev/null && pass "откат из флеша на $V_NEW: настройки как были" || { failx "откат из флеша на $V_NEW: настройки как были"; diff "$WORK/keep.before" "$WORK/keep.after" | head -10; }
[ "$(rb)" = "" ] && pass 'после отката: прежней версии нет (новее не храним)' || failx "после отката: прежней версии нет ($(rb))"
vm 'ls /etc/dpishell/pkg/prev 2>/dev/null; ls /etc/dpishell/pkg/cur' | grep -q "dpishell-$V_NEW.apk" && pass "после отката: во флеше только $V_NEW" || failx "после отката: во флеше только $V_NEW"
step update_local new2
keep_state > "$WORK/keep.after"
diff "$WORK/keep.before" "$WORK/keep.after" > /dev/null && pass "и обратно на $V_NEW2: настройки как были" || failx "и обратно на $V_NEW2: настройки как были"

say 'Удаление с сохранением настроек и повторная установка'
step uninstall keep
r=$(vm 'grep -qx "P:dpishell" /lib/apk/db/installed && echo pkg; test -d /opt/zapret && echo core; test -f /etc/dpishell/user-targets.json -o -f /etc/dpishell/stats.json && echo settings; uci -q get dpishell.main.strategy; uci -q get firewall.@defaults[0].flow_offloading')
case "$r" in *pkg*) failx 'keep: пакет удалён' ;; *) pass 'keep: пакет удалён' ;; esac
case "$r" in *core*) failx 'keep: ядро удалено' ;; *) pass 'keep: ядро удалено' ;; esac
case "$r" in *settings*) pass 'keep: настройки сохранены' ;; *) failx 'keep: настройки сохранены' ;; esac
vm 'test -e /etc/dpishell/pkg' && failx 'keep: пакеты отката убраны из флеша' || pass 'keep: пакеты отката убраны из флеша'
# Прежние версии (r13 и раньше) выключали ускорение сети при установке ядра, не запоминая, — пользователь включает его
# снова; новая установка ядра выключает и запоминает, полное удаление возвращает (сверка с исходным снимком ниже).
vm 'uci set firewall.@defaults[0].flow_offloading=1; uci commit firewall; /etc/init.d/firewall reload >/dev/null 2>&1'
step install new
step core
vm 'uci -q get firewall.@defaults[0].flow_offloading; uci -q get dpishell.main.fw_offload_prev' | tr '\n' ' ' | grep -q '^0 software' &&
	pass 'ядро выключило ускорение сети и запомнило прежнее' || failx 'ядро выключило ускорение сети и запомнило прежнее'
r=$(vm 'sleep 3; uci -q get dpishell.main.strategy; pidof nfqws >/dev/null && echo nfqws')
case "$r" in *"$(cat "$WORK/strategy.before")"*nfqws*) pass 'повторная установка: прежняя стратегия работает' ;; *) failx "повторная установка: прежняя стратегия работает ($r)" ;; esac

say 'Полное удаление'
step uninstall full
step snap s2; pull_snap s2
same 'удаление: uci dhcp' s0 s2 uci-dhcp
same 'удаление: uci firewall (ускорение сети вернулось)' s0 s2 uci-firewall
same 'удаление: uci network' s0 s2 uci-network
same 'удаление: uci https-dns-proxy' s0 s2 uci-https-dns-proxy
same 'удаление: настроек DpiShell нет' s0 s2 uci-dpishell
same 'удаление: пакеты' s0 s2 packages
same 'удаление: /etc/apk/world' s0 s2 world
same 'удаление: crontab' s0 s2 crontab
same 'удаление: nft (без таблицы zapret)' s0 s2 nft
same 'удаление: файлы в системных каталогах' s0 s2 files
same 'удаление: фиды apk' s0 s2 feeds
same 'удаление: следов dpishell/zapret нет' s0 s2 traces
same 'удаление: службы и DNS работают' s0 s2 health
vm '! ubus list dpishell >/dev/null 2>&1' && pass 'удаление: объекта ubus dpishell нет' || failx 'удаление: объекта ubus dpishell нет'
vm 'curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1/cgi-bin/luci/' | grep -Eq '^(200|403)$' && pass 'удаление: LuCI открывается' || failx 'удаление: LuCI открывается'
shots after-uninstall $PAGES_STD

say 'Повторное удаление (после сбоя) безопасно'
vm 'sh /tmp/dpslc/uninstall.sh --full >/dev/null 2>&1; echo rc=$?' | grep -qx 'rc=0' && pass 'повтор uninstall.sh: код 0' || failx 'повтор uninstall.sh: код 0'
step snap s3; pull_snap s3
for p in uci-dhcp uci-firewall packages world crontab nft files traces health; do same "повтор: $p без изменений" s2 s3 "$p"; done

# Вернуть эмулятор: исходное ускорение сети, новая версия с ядром и включённой оптимизацией (им пользуются другие проверки).
vm "uci set firewall.@defaults[0].flow_offloading=${FO_ORIG:-0}; uci commit firewall; /etc/init.d/firewall reload >/dev/null 2>&1"
if [ "${KEEP_INSTALLED:-1}" = 1 ]; then
	say 'Эмулятор: новая версия обратно'
	vm "sh /tmp/dpslc/lifecycle_vm.sh install new; sh /tmp/dpslc/lifecycle_vm.sh core; sh /tmp/dpslc/lifecycle_vm.sh enable $(cat "$WORK/strategy.before")" | sed 's/^/  /'
fi
vm 'rm -rf /tmp/dpslc'

say 'Итог'
n_ok=$(grep -c '^ok' "$RES"); n_fail=$(grep -c '^FAIL' "$RES")
grep '^FAIL' "$RES"
echo "PASS: $n_ok, FAIL: $n_fail${NO_SHOTS:+ (без снимков)}; снимки страниц — $SHOTS"
[ "$n_fail" = 0 ]
