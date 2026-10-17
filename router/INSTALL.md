# Установка DpiShell на роутер с OpenWrt

DpiShell для роутера — пакеты для OpenWrt: `dpishell` (обход, подбор, ubus) и `luci-app-dpishell` (страница «Службы → DpiShell»). Пакеты не зависят от архитектуры, ядро zapret (nfqws) роутер скачивает сам под свою архитектуру и сверяет по хешу.

Установка OpenWrt — под ответственность пользователя, см. документацию OpenWrt и вашей прошивки.

## Что нужно

- OpenWrt 25.12+ (или snapshot) с `apk`, межсетевым экраном fw4 (nftables). Сборка под `opkg` (OpenWrt 24.10) — позже.
- В фиде прошивки — `kmod-nft-queue` под ядро роутера (проверка ниже). Без него обход не работает.
- Чужой zapret (пакеты `zapret`, `luci-app-zapret`, менеджеры zapret) — удалить: он занимает те же папки.

Адрес роутера в примерах — `192.168.1.1` (умолчание OpenWrt); у вас может быть другой.

## Проверка

```
ssh root@192.168.1.1
apk update
uname -r
apk search -v kmod-nft-queue
```
Версия `kmod-nft-queue` должна начинаться с того же, что `uname -r`. Иначе — стоп: модуль не загрузится.

## Установка из фида

```
wget -O /etc/apk/keys/dpishell.pem https://slavdew.github.io/dpishell/feed/dpishell.pem
sha256sum /etc/apk/keys/dpishell.pem
```
Сверить: `e3d98812b312b8c1475f7c98c72cb850ea87c90fdcd81bcd803dad7f9f9a1543`. Не совпало — `rm /etc/apk/keys/dpishell.pem` и не продолжать.

```
echo https://slavdew.github.io/dpishell/feed/packages.adb >> /etc/apk/repositories.d/customfeeds.list
apk update
apk add dpishell luci-app-dpishell
```

Дальше — `http://192.168.1.1/dpi` (или «Службы → DpiShell»):
1. «Загрузить ядро».
2. Предупреждение про ускорение сети (flow offloading) — «Передать zapret».
3. «Подобрать» → полный. 2–3 минуты; сайты в сети в это время могут моргать.
4. «Компьютерам на Windows нужна настройка» — на компьютерах с приложением DpiShell оно включит её само; на остальных Windows — один раз от администратора `netsh int tcp set global timestamps=enabled`. Телефонам, Mac и Linux ничего не нужно.

## Если GitHub с роутера недоступен

Пакеты DpiShell лежат на GitHub Pages, ядро zapret — на github.com. Если провайдер их режет, набор собирают на компьютере, где интернет работает, — под фиды и ключи именно этого роутера:

1. На роутере выгрузить фиды и ключи: `tar -czf /tmp/feeds.tgz /etc/apk/repositories.d /etc/apk/keys /etc/openwrt_release`, забрать: `scp -O root@192.168.1.1:/tmp/feeds.tgz .`
2. На компьютере, в папке проекта: `sh router/tools/offline-bundle.sh router/offline-bundle feeds.tgz` (нужен эмулятор OpenWrt, `C:\Dev\owrt-vm`). Получится папка с DpiShell, недостающими пакетами прошивки (в том числе `kmod-nft-queue`), архивом ядра и `install.sh`; подписи пакетов проверяются при сборке ключами роутера.
3. `scp -O -r router/offline-bundle root@192.168.1.1:/tmp/dpishell-offline` и `ssh root@192.168.1.1 "sh /tmp/dpishell-offline/install.sh"` — сверит суммы, архитектуру и ядро, поставит только недостающее, ядро — из набора, без сети.

## Удаление

`apk del luci-app-dpishell dpishell` — обход выключится, ускорение межсетевого экрана (если передавали zapret) вернётся. Файлы ядра в `/opt/zapret` остаются.
