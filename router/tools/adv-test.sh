#!/bin/sh
# Тест продвинутого режима (advanced_get/advanced_set, сборка config) в эмуляторе. Запуск на эмуляторе: sh /tmp/adv-test.sh
# Обход включается на alt и в конце выключается; прежние настройки advanced восстанавливаются.
F=0
ok() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; F=$((F+1)); fi; }
CFG=/opt/zapret/config
call() { ubus call dpishell "$1" "${2:-{\}}"; }
cp /etc/dpishell/advanced.json /tmp/adv.bak 2>/dev/null

call strategy_set '{"id":"alt"}' >/dev/null
call enable >/dev/null
call confirm >/dev/null

r=$(call advanced_set '{"enabled":true,"others":"lists","rows":[{"domains":["Example.com"],"strategy":"alt10"},{"domains":["bank.example"],"strategy":"direct"},{"domains":["auto.example"]}]}')
ok 'advanced_set принимает строки' '[ "$(echo "$r" | jsonfilter -e @.error.code)" = "" ]'
ok 'строка со стратегией — первый профиль' 'grep -q "NFQWS_OPT=\"--filter-tcp=80,443 --hostlist-domains=example.com " $CFG'
ok 'direct — исключение во всех ролях каталога' '[ "$(grep -o "hostlist-exclude-domains=bank.example" $CFG | wc -l)" -ge 3 ]'
ok 'auto без подбора — по текущей стратегии' 'grep -q "hostlist-domains=auto.example" $CFG'
g=$(call advanced_get)
ok 'advanced_get: resolved auto = текущая' '[ "$(echo "$g" | jsonfilter -e "@.rows[2].resolved")" = "alt" ]'
ok 'advanced_get: direct без resolved' '[ -z "$(echo "$g" | jsonfilter -e "@.rows[1].resolved")" ]'
ok 'status.advanced' '[ "$(call status | jsonfilter -e @.advanced.rows)" = 3 ]'

call advanced_set '{"enabled":true,"others":"direct","rows":[{"domains":["example.com"],"strategy":"alt10"}]}' >/dev/null
ok 'остальные напрямую — без ролей каталога' '! grep -q "hostlist=/etc/dpishell/lists/google.txt" $CFG && grep -q "hostlist-domains=example.com" $CFG'
call advanced_set '{"enabled":true,"others":"direct","rows":[]}' >/dev/null
ok 'нечего обрабатывать — NFQWS_ENABLE=0' 'grep -q "^NFQWS_ENABLE=0" $CFG'
call advanced_set '{"enabled":true,"others":"all","rows":[]}' >/dev/null
ok 'остальные для всех — режим all' 'grep -q "mode=all" $CFG'
call advanced_set '{"enabled":false,"others":"direct","rows":[{"domains":["example.com"],"strategy":"alt10"}]}' >/dev/null
ok 'выключен — как раньше (строк нет в config)' '! grep -q "hostlist-domains=example.com" $CFG && grep -q "^NFQWS_ENABLE=1" $CFG'

e() { call advanced_set "$1" | jsonfilter -e @.error.field; }
ok 'invalid: others' '[ "$(e "{\"enabled\":true,\"others\":\"x\",\"rows\":[]}")" = others ]'
ok 'invalid: домен' '[ "$(e "{\"enabled\":true,\"others\":\"lists\",\"rows\":[{\"domains\":[\"ok.com\",\"bad domain\"]}]}")" = "rows[0].domains[1]" ]'
ok 'invalid: стратегия' '[ "$(e "{\"enabled\":true,\"others\":\"lists\",\"rows\":[{\"domains\":[\"ok.com\"],\"strategy\":\"nope\"}]}")" = "rows[0].strategy" ]'

# Откат по сроку подтверждения возвращает прежние строки.
call advanced_set '{"enabled":false,"others":"lists","rows":[]}' >/dev/null
call advanced_set '{"enabled":true,"others":"direct","rows":[],"confirm_s":60}' >/dev/null
ok 'ожидание подтверждения хранит прежние строки' '[ "$(jsonfilter -i /etc/dpishell/pending-confirm.json -e @.advanced.enabled)" = false ]'
/usr/libexec/dpishell/run revert now
ok 'откат вернул прежние настройки' '[ "$(jsonfilter -i /etc/dpishell/advanced.json -e @.enabled)" = false ] && grep -q "^NFQWS_ENABLE=1" $CFG'

call disable >/dev/null
if [ -f /tmp/adv.bak ]; then cp /tmp/adv.bak /etc/dpishell/advanced.json; else rm -f /etc/dpishell/advanced.json; fi
[ $F = 0 ] && echo 'adv: всё прошло' || echo "adv: $F не прошло"
exit $F
