#!/bin/sh
# Тест совместной работы с ПК (pc_link_*) в эмуляторе: config, второй nfqws, правила nft, ignored, истечение, удаление.
# Запуск на эмуляторе: sh /tmp/pc-test.sh. Оптимизация включается на alt и в конце выключается.
F=0
ok() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; F=$((F+1)); fi; }
CFG=/opt/zapret/config
call() { ubus call dpishell "$1" "${2:-{\}}"; }
cp /etc/dpishell/pc-links.json /tmp/pc.bak 2>/dev/null; rm -f /etc/dpishell/pc-links.json
# Сторож — на время теста выключен: в сети QEMU user стратегии рвут соединения, и он выключил бы оптимизацию посреди теста.
WD=$(uci -q get dpishell.main.watchdog); uci set dpishell.main.watchdog=0; uci commit dpishell

call strategy_set '{"id":"alt"}' >/dev/null
call enable >/dev/null; call confirm >/dev/null; sleep 2
ok 'без связок — FILTER_MARK нет' '! grep -q "^FILTER_MARK" $CFG && [ "$(pidof nfqws | wc -w)" = 1 ]'

r=$(call pc_link_set '{"id":"pc-0001-test","macs":["AA-BB-CC-DD-EE-01"],"domains":["mysite.example","youtube.com","api.googlevideo.com","googleapis.com"]}')
ok 'pc_link_set: expires_at' '[ -n "$(echo "$r" | jsonfilter -e @.expires_at)" ]'
ok 'ignored — домены роутера (youtube.com, поддомен googlevideo.com, надомен googleapis.com)' '[ "$(echo "$r" | jsonfilter -e "@.ignored[*]" | sort | tr "\n" " ")" = "api.googlevideo.com googleapis.com youtube.com " ]'
ok 'config: FILTER_MARK и ПК' 'grep -q "^FILTER_MARK=0x10000000" $CFG && grep -q "^DPS_PC_N=1" $CFG && grep -q "^DPS_PC1_MACS=\"aa:bb:cc:dd:ee:01\"" $CFG'
ok 'исключение ПК в каждом профиле' '[ "$(grep "^DPS_PC1_OPT" $CFG | grep -o "hostlist-exclude=/etc/dpishell/lists/pc-1.txt" | wc -l)" = "$(( $(grep "^NFQWS_OPT" $CFG | grep -o -- "--new" | wc -l) + 1 ))" ]'
ok 'pc-1.txt — только свои сайты ПК' '[ "$(cat /etc/dpishell/lists/pc-1.txt)" = mysite.example ]'
sleep 2
ok 'второй nfqws на очереди 201' 'ps w | grep -q "[n]fqws.*--qnum=201"'
ok 'nft: метки по MAC' 'nft list chain inet zapret dps_pre | grep -q "ether saddr aa:bb:cc:dd:ee:01"'
ok 'nft: очередь 201 с меткой ПК' 'nft list table inet zapret | grep -q "oifname @wanif meta mark & 0x01000000 != 0x00000000 tcp.*queue.* to 201"'
ok 'nft: основная очередь только с FILTER_MARK' 'nft list table inet zapret | grep -q "oifname @wanif meta mark & 0x10000000 != 0x00000000 tcp.*queue.* to 200"'
ok 'nft: входящие ПК — с меткой, раньше основных' '[ "$(nft list table inet zapret | grep -n "iifname @wanif.*tcp sport.*queue" | head -1 | grep -c "meta mark & 0x01000000.* to 201")" = 1 ]'
ok 'nft: входящих в 201 без метки нет' '! nft list table inet zapret | grep "iifname @wanif.*tcp sport.*to 201" | grep -qv "meta mark"'
ok 'nft: ответы с WAN получают метку из ct' 'nft list chain inet zapret dps_pre | grep -q "iifname @wanif meta mark set"'
ok 'status.pc_links = 1' '[ "$(call status | jsonfilter -e @.pc_links)" = 1 ]'
g=$(call pc_link_get)
ok 'pc_link_get: active, domains 4, ignored 3' '[ "$(echo "$g" | jsonfilter -e "@.items[0].active")" = true ] && [ "$(echo "$g" | jsonfilter -e "@.items[0].domains")" = 4 ] && [ "$(echo "$g" | jsonfilter -e "@.items[0].ignored")" = 3 ]'

e() { call pc_link_set "$1" | jsonfilter -e @.error.field; }
ok 'invalid: MAC' '[ "$(e "{\"id\":\"pc-0001-test\",\"macs\":[\"zz\"],\"domains\":[]}")" = "macs[0]" ]'
ok 'invalid: id' '[ "$(e "{\"id\":\"x\",\"macs\":[\"aa:bb:cc:dd:ee:01\"],\"domains\":[]}")" = id ]'

# Истечение: срок в прошлом → tick переписывает config без ПК.
sed -i 's/"expires_at": [0-9]*/"expires_at": 1000/' /etc/dpishell/pc-links.json
ok 'истекла — active=false' '[ "$(call pc_link_get | jsonfilter -e "@.items[0].active")" = false ]'
/usr/libexec/dpishell/run tick
sleep 2
ok 'tick: config без ПК, один nfqws' '! grep -q "^DPS_PC_N" $CFG && [ "$(pidof nfqws | wc -w)" = 1 ]'
ok 'nft: цепочек ПК нет' '! nft list chain inet zapret dps_pre >/dev/null 2>&1'

call pc_link_set '{"id":"pc-0001-test","macs":["aa:bb:cc:dd:ee:01"],"domains":["mysite.example"]}' >/dev/null
ok 'продление возвращает ПК' 'grep -q "^DPS_PC_N=1" $CFG'
call pc_link_del '{"id":"pc-0001-test"}' >/dev/null
ok 'pc_link_del: ПК снят' '! grep -q "^DPS_PC_N" $CFG && [ "$(call pc_link_get | jsonfilter -e "@.items[0].id")" = "" ]'
ok 'lists_get google — только чтение' '[ "$(call lists_get "{\"name\":\"google\"}" | jsonfilter -e @.editable)" = false ]'

call disable >/dev/null
uci set dpishell.main.watchdog=${WD:-1}; uci commit dpishell
if [ -f /tmp/pc.bak ]; then cp /tmp/pc.bak /etc/dpishell/pc-links.json; else rm -f /etc/dpishell/pc-links.json; fi
[ $F = 0 ] && echo 'pc: всё прошло' || echo "pc: $F не прошло"
exit $F
