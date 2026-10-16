#!/bin/sh
# Разложить файлы пакета в эмуляторе OpenWRT как при установке (без apk) и перезапустить rpcd.
# Данные (каталог, цели, списки, фейки) — из data/ репозитория, как при сборке пакета.
set -e
VM=${VM:-/c/Dev/owrt-vm}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
STAGE=$(mktemp -d)
cp -r "$ROOT/router/dpishell/files/." "$STAGE/"
mkdir -p "$STAGE/usr/share/dpishell/lists" "$STAGE/usr/share/dpishell/fake"
cp "$ROOT/data/strategies.json" "$ROOT/data/targets.json" "$STAGE/usr/share/dpishell/"
cp "$ROOT/data/lists/"*.txt "$STAGE/usr/share/dpishell/lists/"
cp "$ROOT/data/fake/"*.bin "$STAGE/usr/share/dpishell/fake/"
# Свой config пользователя не затирать
"$VM/ssh.sh" 'test -f /etc/config/dpishell' && rm -f "$STAGE/etc/config/dpishell"
# Только файлы (без записей каталогов), владелец root: иначе tar меняет владельца и права /, /etc, /usr.
(cd "$STAGE" && find . -type f -o -type l) | sed 's|^\./||' > "$STAGE.list"
tar -C "$STAGE" --owner=0 --group=0 --no-recursion -T "$STAGE.list" -cf - 	| "$VM/ssh.sh" 'tar -C / -xf - && chmod 755 /usr/share/rpcd/ucode && /etc/init.d/rpcd restart'
rm -f "$STAGE.list"
rm -rf "$STAGE"
echo deployed
