#!/bin/sh
# Разложить файлы luci-app-dpishell в эмуляторе OpenWRT как при установке пакета, сбросить кэш меню LuCI
# и перезапустить rpcd. Бэкенд (router/dpishell) кладёт vm-deploy.sh.
# Запуск из корня репозитория: sh router/tools/vm-deploy-luci.sh
set -e
VM=${VM:-/c/Dev/owrt-vm}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
APP="$ROOT/router/luci-app-dpishell"
STAGE=$(mktemp -d)
mkdir -p "$STAGE/www"
cp -r "$APP/htdocs/." "$STAGE/www/"
cp -r "$APP/root/." "$STAGE/"
# Только файлы (без записей каталогов), владелец root: иначе tar меняет владельца и права /, /etc, /www.
(cd "$STAGE" && find . -type f -o -type l) | sed 's|^\./||' > "$STAGE.list"
tar -C "$STAGE" --owner=0 --group=0 --no-recursion -T "$STAGE.list" -cf - \
	| "$VM/ssh.sh" 'tar -C / -xf - && rm -rf /tmp/luci-* && /etc/init.d/rpcd restart'
rm -f "$STAGE.list"
rm -rf "$STAGE"
echo deployed luci-app-dpishell
