#!/bin/sh
# Порядок объявлений функций (ucode разрешает имена при компиляции)
python "$(dirname "$0")/check-order.py" || exit 1
# Скопировать файлы пакета и данные в эмулятор OpenWRT и запустить тесты ucode.
# Эмулятор: C:\Dev\owrt-vm (run.sh, ssh.sh). Запуск из корня репозитория: sh router/tools/vm-test.sh
set -e
VM=${VM:-/c/Dev/owrt-vm}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
tar -C "$ROOT" -cf - router/dpishell/files router/test data/strategies.json data/targets.json \
	| "$VM/ssh.sh" 'rm -rf /tmp/dpitest && mkdir -p /tmp/dpitest && tar -C /tmp/dpitest -xf -'
"$VM/ssh.sh" 'cd /tmp/dpitest && F=/tmp/dpitest/router/dpishell/files &&
	ucode -L "$F/usr/share/ucode/*.uc" router/test/catalog_test.uc data $F/usr/share/dpishell/roles.json router/test/golden.json'
"$VM/ssh.sh" 'cd /tmp/dpitest && F=/tmp/dpitest/router/dpishell/files &&
	ucode -L "$F/usr/share/ucode/*.uc" router/test/probe_test.uc '"${LIVE:+live}"
"$VM/ssh.sh" 'cd /tmp/dpitest && F=/tmp/dpitest/router/dpishell/files &&
	ucode -L "$F/usr/share/ucode/*.uc" router/test/select_test.uc'
# Дымовой тест API — только если пакет развёрнут (vm-deploy.sh или apk add)
if [ -n "$SMOKE" ]; then
	"$VM/ssh.sh" 'sh /tmp/dpitest/router/test/api_smoke.sh'
fi
