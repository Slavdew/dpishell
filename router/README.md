# Пакет DpiShell для роутера (OpenWRT)

ТЗ — `docs/06-router-spec.md`, договор с Windows-приложением — `docs/07-router-api.md`.

## Раскладка
- `dpishell/files/` — файлы пакета как на роутере: модули ucode (`usr/share/ucode/dpishell/`), ubus-объект (`usr/share/rpcd/ucode/dpishell`), ACL, исполнитель фоновых заданий (`usr/libexec/dpishell/run`), init-скрипт, uci по умолчанию, `roles.json`, `core.json`. Данные (`strategies.json`, `targets.json`, списки, фейки) берутся из `data/` при сборке.
- `luci-app-dpishell/` — страницы LuCI.
- `test/` — тесты ucode; `test/golden.json` — эталон сборки из `WinwsArgs.Build` (генератор — `tools/golden/`).
- `tools/vm-deploy.sh` — разложить файлы в эмулятор; `tools/vm-test.sh` — тесты в эмуляторе (`LIVE=1` — с живыми запросами).

## Эмулятор
`C:\Dev\owrt-vm`: `run.sh` (QEMU из MSYS2, OpenWRT 25.12.5 x86_64; запуск в фоне), `ssh.sh '<команда>'` (порт 8022, ключ `vmkey`), LuCI — http://127.0.0.1:8080 (root / test). Сеть QEMU `user` пересобирает TCP — действие стратегий в эмуляторе не проверяется.

## Состояние (2026-10-17)
Готово и проверено в эмуляторе: сборщик config (совпадает с Windows для 20 стратегий), ubus-объект (статус, вкл/выкл, стратегии, режим, расписание, свои адреса/списки/стратегии, вход через /ubus), проверка целей (DoH, тело, редиректы), подбор (без обхода → перебор → автоприменение + запасные; ts глазами Windows), проверка по таймеру с запасными и автоподбором.

Также готово: `core_install` — установка ядра с роутера (наш zip, основной адрес → зеркало, хеши архива и nfqws, шаги install_easy без cron), замки заданий с PID (мёртвый замок не блокирует).

Также готово: `site_check` (адрес временно в nozapret), `diag`/`report`/`conflict_stop`, DNS (анализ подмены: NXDOMAIN/служебные адреса/проверка делом; https-dns-proxy по кнопке; возврат с перезапуском dnsmasq; временный откат при недоступности DoH).

Также готово: страницы LuCI (`luci-app-dpishell`); пакеты собираются в GitHub Actions (`.github/workflows/router.yml`, SDK `openwrt/sdk:x86-64-v25.12.5`); первая сборка поставлена в эмулятор через `apk add --allow-untrusted` — объект ubus, cron, страница на месте. Найдена и исправлена ошибка порядка функций (ucode разрешает имена при компиляции) — проверка `tools/check-order.py` в тестах и в сборке; дымовой тест API — `SMOKE=1 sh router/tools/vm-test.sh`.

Также готово (2026-10-17, вторая сессия): подписанная витрина (пакеты ставятся с https://slavdew.github.io/dpishell/feed/ без `--allow-untrusted`, проверено в эмуляторе); глубокий поиск (`select.uc` + генератор `search.uc` = `CandidateGenerator` ПК, эталон `test/golden-search.json`, «первое улучшение» как `RefineAsync`); `flow_offload_fix` (разгрузку ведёт zapret), `ipv6_set`; предупреждения `flow_offload`/`ipv6`/`conflict`; чужой zapret пакетом (Zapret Manager) — конфликт `kind: package`. Инструкция установки — `INSTALL-роутер.md`.

Исправлено: ucode делит целые нацело (доли групп были 0 при частичном успехе, порядок по статистике не работал) — `1.0 *`, проверка в `check-order.py`; после `zapret restart` nfqws поднимается асинхронно (1–2 с) — подбор ждёт новый процесс; busybox `sleep` не понимает дробей — `sleep(мс)` ucode.

Проверка подбора в эмуляторе — имитатор DPI: `/tmp/dpishell/fake-dpi.json` (правила «сайт открывается, если в профиле nfqws есть такие приёмы», пример — `06` §4.5); без файла не действует.

## Где остановился (2026-10-17, ~03:30)
- Всё запушено; эмулятор — с пакетами из витрины поверх развёрнутых `vm-deploy.sh` файлов, ядро стоит.
- `kmod-nft-queue` для сторонняя прошивка OpenWrt 1.4.8 есть: модули лежат в папке по хешу ядра — `feed/targets/SoC/SoC/<vermagic>/packages/` (`kmod-nft-queue`, `kmod-nfnetlink-queue` 6.18.52), образ прописывает этот адрес себе в `customfeeds.list` при сборке. Каталог `feed/targets/.../packages` без хеша — старый фид для 1.2.x (на нём я сначала ошибочно решил, что модуля нет). У другой версии сторонняя прошивка OpenWrt хеш другой — проверка `INSTALL-роутер.md` §4.
- Дальше: живая проверка на роутер с пользователем; игровой режим и IP-списки (роутер v2, 09-features-spec).
