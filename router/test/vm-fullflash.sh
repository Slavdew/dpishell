#!/bin/sh
# «Полный флеш» в эмуляторе (weak-router-test.md, decisions-owner-2026-10-20.md «Ресурсы роутера»): флеш заполняется
# заглушкой (остаётся LEFT_KB, по умолчанию 200 КБ) → DNS «Google» — честный отказ «мало места», dhcp/network/пакеты как
# были, DNS роутера отвечает → обновление из папки окна ПК — отказ, версия прежняя → заглушка убрана → неудачная установка
# пакета (pkgs.add) убирает своё. Запуск из корня репозитория: sh router/test/vm-fullflash.sh (VM=… — другой эмулятор,
# лучше слабый: run-weak.sh). Пакет из рабочей папки ставится сам (apk add, если DpiShell нет; файлы — как vm-deploy.sh).
# NO_NET=1 — без проверки уборки после установки (нужна связь с источником пакетов OpenWrt). Код выхода 1 — есть FAIL.
VM=${VM:-/c/Dev/owrt-vm}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
vm() { "$VM/ssh.sh" "$@"; }

python -I "$ROOT/router/tools/vm-mkapk.py" "$WORK/pk/new" > /dev/null || exit 1
# «Следующая версия» той же папки — для обновления, которое должно отказать.
RELEASE_ADD=1 python -I "$ROOT/router/tools/vm-mkapk.py" "$WORK/pk/next" > /dev/null || exit 1
cp "$ROOT/router/test/fullflash_vm.sh" "$WORK/pk/"
tar -C "$WORK/pk" --owner=0 --group=0 -cf - . | vm 'rm -rf /tmp/dpff && mkdir -p /tmp/dpff && tar -C /tmp/dpff -xf -'

if ! vm 'grep -qx "P:dpishell" /lib/apk/db/installed'; then
	vm 'apk add --allow-untrusted /tmp/dpff/new/*.apk >/dev/null 2>&1; i=0; while [ $i -lt 20 ] && ! ubus -t 2 list dpishell >/dev/null 2>&1; do sleep 1; i=$((i+1)); done'
fi
VM=$VM sh "$ROOT/router/tools/vm-deploy.sh" > /dev/null || exit 1
vm 'i=0; while [ $i -lt 20 ] && ! ubus -t 2 list dpishell >/dev/null 2>&1; do sleep 1; i=$((i+1)); done'

vm "LEFT_KB=${LEFT_KB:-200} NO_NET=$NO_NET sh /tmp/dpff/fullflash_vm.sh" | tee "$WORK/out.txt"
vm 'rm -rf /tmp/dpff'
n_ok=$(grep -c '^ok ' "$WORK/out.txt"); n_fail=$(grep -c '^FAIL ' "$WORK/out.txt")
echo
echo "Итог: ok $n_ok, FAIL $n_fail"
rm -rf "$WORK"
[ "$n_fail" -eq 0 ] && [ "$n_ok" -gt 0 ]
