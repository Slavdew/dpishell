# Договор: управление обходом на роутере из Windows-приложения

Версия API 1. Сторона роутера — пакет `dpishell` (`router/dpishell`, ubus-объект `router/dpishell/files/usr/share/rpcd/ucode/dpishell`), сторона ПК — `src/DpiShell.Core/Router/RouterClient.cs`, тесты — `tests/DpiShell.Tests/FakeRouter.cs` и `Router*Tests.cs`. Документ — единственный источник формата: меняется только с увеличением `api` (до первого выпуска роутерного пакета — правки без увеличения, см. §9).

## 1. Как это устроено

- На роутере работает `rpcd` (штатная часть OpenWrt). Через него страница DpiShell в меню роутера вызывает методы объекта `dpishell`. Windows-приложение вызывает **те же методы тем же путём** — отдельного сервера для приложения нет.
- Транспорт: JSON-RPC 2.0, `POST http://<адрес роутера>/ubus`, `Content-Type: application/json`.
- Роутер принимает запросы только из домашней сети (uhttpd слушает LAN, fw4 закрывает WAN).

## 2. Поиск роутера

1. Адрес по умолчанию — основной шлюз IPv4 активного адаптера (у OpenWrt по умолчанию `192.168.1.1`). В настройках можно указать свой адрес (IP или имя, без схемы и пути).
2. `GET http://<адрес>/dpishell/status.json`, таймаут 1 с, без входа. Роутер с DpiShell — если `product == "dpishell-router"`. Формат — [architecture.md](architecture.md), «status.json», плюс поле `"api": 1`.
3. Нет ответа / 404 / другой `product` — «На роутере DpiShell не найден». Приложение работает как раньше, только с ПК.
   - Роутер может стоять не первым: «роутер провайдера → наш роутер → ещё один роутер/Mesh → ПК». Если по шлюзу DpiShell не найден, приложение пробует ещё до 3 частных адресов на пути наружу (первые узлы `tracert -d -h 4`, только `10/8`, `172.16/12`, `192.168/16`), тем же запросом с таймаутом 1 с. Найден не на шлюзе — управление так же, вход по паролю того роутера.
4. `api` у роутера больше, чем знает приложение — «Обновите приложение», разрешено только чтение статуса. Меньше **или поля `api` нет** (пакет старше договора) — «Обновите пакет на роутере», только чтение статуса.

## 3. Вход

```json
→ {"jsonrpc":"2.0","id":1,"method":"call","params":["00000000000000000000000000000000","session","login",{"username":"root","password":"<пароль>","timeout":600}]}
← {"jsonrpc":"2.0","id":1,"result":[0,{"ubus_rpc_session":"c1ed6c7b…","timeout":600,"expires":600,…}]}
```
- Пароль — пароль администратора роутера (тот же, что для меню). Хранится на ПК через DPAPI (`ProtectedData`, `CurrentUser`), в логи и отчёты не попадает.
- Сессия живёт `timeout` секунд с последнего вызова. Неверная или истёкшая сессия: штатный `uhttpd-mod-ubus` отвечает ошибкой JSON-RPC `{"error":{"code":-32002,"message":"Access denied"}}` без `result`; вызов, на который у сессии нет прав, возвращает `result[0] == 6`. Оба случая — «нет доступа»: войти заново один раз; снова — «Неверный пароль или сессия», запросить пароль.
- `result[0] == 6` на `login` — неверный пароль.

## 4. Вызов метода

```json
→ {"jsonrpc":"2.0","id":2,"method":"call","params":["<ubus_rpc_session>","dpishell","<метод>",{…аргументы…}]}
← {"jsonrpc":"2.0","id":2,"result":[0,{…ответ…}]}
```
- `result[0]` — код ubus: `0` успех, `2` неверные аргументы, `4` метода/объекта нет (пакет не установлен или старый), `6` нет доступа, `7` таймаут, `5` ответа нет.
- Ошибка уровня DpiShell при коде `0`: `{"error":{"code":"busy","text":"Идёт подбор"}}`. Коды: `busy`, `not_installed` (ядро не загружено), `invalid` (с полем `field`), `conflict` (работает чужой обход), `core_mismatch` (хеш ядра), `failed` (с `text`). У всех кодов `text` — готовая фраза для пользователя.
- `field` при `invalid` — путь к значению в аргументах: `items[3].url`, `items[0].roles.google`, `hours`, `groups`. Индексы с нуля.
- Таймаут HTTP — 10 с. Долгие операции (подбор, проверка, загрузка ядра, DNS) только запускаются и возвращаются сразу (rpcd-вызов не ждёт); ход — опросом `status`/`select_progress` раз в 1 с.

## 5. Методы

Время — ISO 8601 UTC. Идентификаторы стратегий — как в `strategies.json` / `user-strategies.json`.

### 5.1 Чтение

**`status`** `{}` →
```json
{ "api": "1.0", "version": "0.2.0", "min_app": "0.9.0", "methods": ["apply", "check_now", "…", "uninstall", "update_install"], "engine": "zapret1",
  "core": { "version": "v72.13", "installed": true, "hash_ok": true },
  "enabled": true, "mode": "all", "strategy": "alt10", "backups": ["alt9", "alt13"], "needs_tcp_timestamps": true,
  "last_check": { "at": "2026-10-16T21:00:00Z", "source": "timer", "ok": true,
                  "groups": [ { "id": "youtube", "ok": true }, { "id": "discord", "ok": true } ] },
  "fail_streak": 0,
  "schedule": { "enabled": true, "hours": 1, "auto_select": false },
  "jobs": { "select": false, "check": false, "core_install": false, "dns": false, "dns_analyze": false, "site_check": false, "update": false },
  "warnings": [ { "code": "flow_offload", "text": "Включено ускорение сети (flow offloading) — с ним обход не работает" } ],
  "update": { "available": true, "version": "0.2.1-r1", "notes": ["Глубокий поиск стратегий на самом роутере"], "checked_at": "2026-10-17T14:00:00Z",
              "job": { "action": "update", "from": "feed", "running": false, "ok": true, "message": "Обновлено до 0.2.0-r1", "at": "…" } } }
```
`source`: `manual` | `timer` | `app`. `jobs.dns` — идёт любое DNS-задание (`dns_analyze` — прежнее имя, то же значение). Коды `warnings`: `flow_offload`, `ipv6`, `dns_spoofed`, `conflict`, `core_missing`, `switched_backup` (сработала запасная стратегия, в `text` — какая), `auto_selected` (автоподбор нашёл и включил стратегию, в `text` — какую), `auto_select_failed` (автоподбор ничего не нашёл, оставлена прежняя), `dns_fallback` (защищённый DNS недоступен, временно DNS провайдера). `needs_tcp_timestamps` — включённая стратегия работает только у устройств с временными метками TCP (нужно только компьютерам на Windows: у Android, iOS, macOS и Linux они включены по умолчанию).

`api` — «мажор.минор» (§5.4); роутеры до 0.2.0 отдают число `1` (= «1.0»). `methods` — методы, которые этот роутер реально обрабатывает (заглушки «не реализовано» не входят); `min_app` — минимальная версия окна ПК, которую он понимает. `update` — есть ли в витрине версия новее установленной (проверка раз в сутки и по `update_check`), `notes` — что нового (до 15 строк, `router/release-notes.json`), `job` — последнее задание обновления (`null` — не было).

**`strategies`** `{}` → `{ "catalog_version": 3, "items": [ { "id": "alt10", "title": "general (ALT10)", "source": "catalog|user", "runs": 7, "passed": 6 } ] }` — порядок как в списке выбора (по статистике). `source: user` — присланные с ПК (`u-…`) и найденные глубоким поиском на роутере (`r-…`, последние 20).

**`select_progress`** `{}` →
```json
{ "running": true, "started_by": "app", "depth": "main", "index": 4, "total": 20, "eta_s": 95,
  "message": "alt3: YouTube",
  "rows": [ { "id": "alt", "title": "general (ALT)", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null,
              "verdicts": [ { "group": "youtube", "url": "https://www.youtube.com/", "ok": false, "detail": "TLS 1.2: поток оборвался на 16384 байтах", "seconds": 6.1 } ] } ],
  "finished_at": null, "cancelled": false, "best": null, "applied": false, "backups": [] }
```
`index` — номер проверяемой стратегии с 1. `title` в строках `rows` — всегда: название из каталога или короткое описание кандидата глубокого поиска («fake + multisplit, ts — YouTube»); у кандидатов ещё `candidate: true`, `ok`/`total` (сколько обязательных целей роли открылось); кандидаты в `strategies` не попадают — только найденные в финале. `started_by`: `page` | `app` | `auto` («Включить» без стратегии) | `timer` (автоподбор после неудачной проверки). Подбор, запущенный с другого устройства, виден так же и его можно отменить. `eta_s` — оценка оставшегося времени, `null`, пока не известна (глубокий поиск сужает перебор по ходу). После завершения — `best`, `applied: true` (подбор сам включил лучшую) и `backups` (до 3 запасных). Остановленный подбор — `cancelled: true`, `applied: false`, прежнее состояние восстановлено. Доли `group_share`/`target_share` — дробные. `verdicts` (у полных проверок, не у кандидатов) — исход по каждой цели для «Подробнее»: `detail` — одна строка (код, байты, редиректы; у провала — что сломалось, с версией TLS, если проверялись обе), `seconds` — самая долгая из проверок цели. Исходы проверки — как на ПК: свои редиректы проходятся до конечной страницы (до 5) и судятся по ней, чужой редирект — заглушка, HTTP 451 — блокировка, 403/404 у целей с телом — провал.

**`targets_get`** `{}` → `{ "items": [ { "url": "https://example.com/", "important": true } ] }` — только «Свои адреса» роутера.

**`lists_get`** `{ "name": "user-general" | "user-exclude" | "exclude" }` → `{ "lines": ["example.com"], "editable": true }` — `exclude` (стандартные исключения) только для чтения.

**`diag`** `{}` → `{ "conflicts": [ { "kind": "service", "name": "zapret2", "running": true } ], "flow_offload": false, "flowoffload": "software", "ipv6_wan": false, "dns": { "spoofed": true, "checked_at": "…" }, "overlay_free_kb": 18000 }`. `flow_offload` — разгрузка включена в межсетевом экране (мешает обходу, предупреждение `flow_offload`); `flowoffload` — разгрузку ведёт zapret после `flow_offload_fix` (`none` | `software` | `hardware`, обходу не мешает). `conflicts[].kind`: `service` (чужой демон — его останавливает `conflict_stop`), `package` (чужой zapret пакетом — `zapret`, `luci-app-zapret`, `zapret-manager-beam`, `zapret2`: занимает `/opt/zapret`, остановить нельзя, только удалить; `core_install` при нём отказывает).

**`site_check_result`** `{}` → `{ "running": false, "url": "https://example.com/", "with_bypass": { "ok": false, "detail": "сброс после 16 КБ" }, "without_bypass": { "ok": true, "detail": "" }, "suggest_exclude": true }` — `suggest_exclude`: открывается только без обхода, предложить добавить в исключения (`lists_set user-exclude`).

**`report`** `{}` → `{ "text": "…" }` — то же, что «Сохранить отчёт» на странице. Свои адреса пользователя в отчёте заменены на «свой адрес №N».

**`dns_status`** `{}` → `{ "mode": "provider" | "doh" | "plain", "preset": "default", "custom": null, "spoofed": true, "checked_at": "…", "servers": [ { "id": "google", "name": "Google", "doh_ok": true, "ms": 38 }, { "id": "quad9", "name": "Quad9", "doh_ok": true, "ms": 41 } ], "fallback_active": false, "job": { "action": "dns_enable", "running": false, "ok": true, "message": "Готово", "at": "2026-10-17T00:30:00Z" } }` — `job` — последнее DNS-задание (`null`, если их не было); `at` — когда закончилось (пока `running: true` — нет). Установка пакета DoH занимает 10–20 с, поэтому `dns_enable`/`dns_restore`/`dns_set` фоновые, как подбор. `fallback_active`: оба DoH пропали, временно работает DNS провайдера. `preset` — включённый вариант (`dns_presets`), `custom` — последний свой вариант (как в `dns_set`, `null` — не задавался); `servers` — доступность и скорость DoH всех вариантов с одним сервером (по `dns_analyze`).

**`dns_presets`** `{}` → `{ "presets": [ { "id": "yandex", "title": "Яндекс", "note": "российский: применяет реестр блокировок", "resolvers": [ { "doh": "https://common.dot.dns.yandex.net/dns-query", "bootstrap": ["77.88.8.8", "77.88.8.1"] } ], "plain": ["77.88.8.8", "77.88.8.1"] } ], "current": "default", "custom": null }` — варианты DNS для всей сети: `provider` (от провайдера, как было), `default` (Google + Cloudflare — прежний `dns_enable`), `google`, `cloudflare`, `quad9`, `yandex`. `resolvers` — DoH (через https-dns-proxy), `plain` — обычный DNS (только для варианта без DoH). Приложение, не нашедшее метода (`4`), показывает прежнюю одну кнопку «Включить защищённый DNS».

### 5.2 Действия

| Метод | Аргументы | Что делает | Ответ |
|---|---|---|---|
| `enable` | `{}` | включает обход с текущей стратегией; при работающем чужом обходе — `error: conflict` | `{}` или `error` |
| `disable` | `{}` | выключает | `{}` |
| `mode_set` | `{ "mode": "all" \| "lists" }` | «Все сайты» / «Только из списков»; перезапуск zapret и полная проверка с контрольной группой | `{ "started": true }` |
| `site_check` | `{ "url": "https://example.com/" }` (голое имя `example.com` → `https://example.com/`) | «Сайт не открывается с обходом»: проверка с обходом и без; ход — `status.jobs.site_check`, итог — `site_check_result` | `{ "started": true }` |
| `strategy_set` | `{ "id": "alt9" }` | выбор стратегии; при включённом обходе — перезапуск | `{}` |
| `check_now` | `{}` | запускает проверку (результат — в `status.last_check`, `source: app`) | `{ "started": true }` |
| `select_start` | `{ "groups": ["youtube","discord","user"], "mode": "full" \| "quick", "depth": "main" \| "deep" }` | запускает подбор; `deep` — глубокий поиск (генерация кандидатов по ролям, ориентир 5–15 мин), `main` — готовые стратегии (~2–3 мин). Только группа `user` при пустом списке своих адресов роутера — `error: invalid`, `field: groups` (приложение предлагает отправить свои адреса с ПК и повторить). При чужом обходе — `error: conflict` | `{ "started": true }` |
| `select_cancel` | `{}` | останавливает, возвращает прежнее | `{}` |
| `apply` | `{ "id": "alt9", "backups": ["alt13"] }` | выбрать другую стратегию из итога подбора вместо автоматически включённой | `{}` |
| `schedule_set` | `{ "enabled": true, "hours": 1, "auto_select": false }` (`hours` ∈ 0.5, 1, 3, 6, 12, 24) | таймер проверки; `auto_select` — при неудаче проверки и исчерпанных запасных роутер сам запускает быстрый подбор (`depth: main`, не чаще раза в 6 ч, `started_by: timer`, применяет сам; итог — `auto_selected`/`auto_select_failed` в `warnings`) | `{}` |
| `targets_set` | `{ "items": [ { "url": "…", "important": true } ] }` | свои адреса (до 50) | `{}` |
| `lists_set` | `{ "name": "user-general", "lines": [ … ] }` | свои списки (домены по шаблону, до 5000 строк) | `{}` |
| `strategies_user_set` | `{ "items": [ { "id": "u-…", "title": "…", "roles": { "google": "…" } } ] }` — `id` по шаблону `^u-[a-z0-9-]{1,48}$`, `title` до 80 символов; роли и значения — в той же записи, что `strategies.json` (§5.3) | загрузить найденные на ПК стратегии (§6); заменяет весь прежний набор из приложения; роутер перепроверяет их сам (`check_now`) до включения | `{}` или `error: invalid` |
| `dns_analyze` | `{}` | анализ DNS (подмена, доступность и скорость DoH), результат — в `dns_status` | `{ "started": true }` |
| `dns_enable` | `{}` | = `dns_set { preset: "default" }` | `{ "started": true }` |
| `dns_restore` | `{}` | = `dns_set { preset: "provider" }` — вернуть DNS как было | `{ "started": true }` |
| `dns_set` | `{ "preset": "quad9" }` или `{ "custom": { "title": "Мой", "doh": "https://…/dns-query", "bootstrap": ["ip", …], "plain": ["ip", …] } }` | включить вариант DNS (фоново, итог — `dns_status.job`, `action: "dns_set"`); не заработал — возвращается предыдущий. `custom`: `title` до 40 символов без кавычек; `doh` — только `https://сервер[:порт]/путь`; `bootstrap`/`plain` — до 4 адресов IPv4/IPv6; нужен `doh` или `plain`; у `doh` по имени — обязателен `bootstrap`. `{ "preset": "custom" }` — последний свой вариант | `{ "started": true }` или `error: invalid` с `field` |
| `conflict_stop` | `{ "name": "zapret2" }` (только имя из `diag.conflicts` с `kind: service`) | остановить и отключить чужой обход | `{}` |
| `flow_offload_fix` | `{}` | разгрузку из межсетевого экрана передать zapret (`FLOWOFFLOAD=software`, при аппаратной — `hardware`): та же скорость, пакеты для nfqws — в обход разгрузки; при удалении пакета разгрузка возвращается межсетевому экрану | `{ "changed": true, "flowoffload": "software" }` |
| `ipv6_set` | `{ "enabled": true }` | обход по IPv6 (`DISABLE_IPV6=0`); перезапуск zapret, если включён | `{}` |
| `core_install` | `{}` | загрузить ядро по манифесту | `{ "started": true }` |
| `uninstall` | `{ "keep_settings": true }` | удалить DpiShell (фоново, ядро договора §5.4): выключить обход, вернуть всё изменённое вне своих файлов (разгрузку, DNS, метки TCP, cron), удалить пакеты и ядро `/opt/zapret`; модули ядра и зависимости остаются. `keep_settings: false` — ещё `/etc/dpishell`, ключ и строку витрины. Объект `dpishell` по ходу исчезает; итог — `/tmp/dpishell-uninstall.json` `{ running, ok, keep_settings, message, at }` (по SSH) | `{ "started": true }` |
| `update_install` | `{ "from": "feed" \| "local" }` | обновить DpiShell (фоново, ядро договора §5.4): `feed` — из витрины (`apk update && apk upgrade`); `local` — из `/tmp/dpishell-update/` (кладёт окно ПК: только `dpishell-*.apk`/`luci-app-dpishell-*.apk` и `SHA256SUMS`, путь не принимается). Итог — `status.update.job`; rpcd перезапускается | `{ "started": true }` |
| `update_check` | `{}` | проверить новую версию сейчас (итог — `status.update`) | `{ "started": true }` |

Роутер проверяет всё сам: стратегии — по `options.json`/`roles.json`, домены — по шаблону, длины — по лимитам. Командной строки и путей от клиента не принимает.

### 5.3 Запись стратегии
- Набор ролей **не фиксирован**: он растёт (игровые профили, обновляемый ipset). Роутер принимает роли, которые знает его каталог (`strategies.json` той версии, что в пакете; `catalog_version` в `strategies`). Сейчас это `quic`, `discord_udp`, `discord_media`, `google`, `general`, `ipset_udp`, `ipset_tcp`. Роль, неизвестная роутеру, — `invalid` с `field` на неё; приложение не отбрасывает роли само, а при таком ответе предлагает обновить пакет на роутере.
- Значения — как в `strategies.json`: `{fake}/<файл>.bin` (набор файлов `fake/` у роутера тот же), `0x…`, `!`. Подстановку `{fake}` роутер разворачивает сам. Файл, которого нет в его `fake/`, — `invalid`.

### 5.4 Ядро договора и совместимость
- `status.api` — «мажор.минор». Минор растёт при добавлениях без поломок (новые методы и поля), мажор — только при несовместимых изменениях.
- Приложение сравнивает с поддерживаемым: мажор другой — команды запрещены, доступны только состояние, «Обновить» и «Удалить»; минор другой — предупреждение, кнопки методов, которых нет в `status.methods`, неактивны; `min_app` новее приложения — «обновите приложение».
- **Ядро договора** — `status` (поля `product`, `api`, `version`, `methods`, `min_app`, `update`), `update_install`, `uninstall`: остаются совместимыми при любом мажоре — по ним пользователь выбирается из несовместимости. Аргументы и ответы ядра только расширяются.
- Выпуски: тег `router-vX.Y.Z`, в релизе — пакеты, `release.json` `{ schema, version, tag, api, min_app, pkg, changes, assets: [{ name, sha256 }] }` и `release.json.sig` — base64 подписи ed25519 над точными байтами `release.json` (проверочный ключ — в приложении). Тот же `release.json` без подписи — в витрине (`feed/release.json`) для заметок к обновлению. Какой выпуск под какую OpenWrt — `data/router-releases.json` (`schema 1`: `feed { url, key_url, key_sha256 }`, `releases [{ version, tag, api, min_app, pkg, openwrt { min, snapshot }, packages, release_json }]`).

### 5.5 Сценарии по SSH (когда API нет или пакета ещё нет)
Окно передаёт сценарий через stdin, на роутер его не кладёт: `ssh root@<роутер> 'sh -s -- <аргументы>' < файл`. Последняя строка ответа — JSON.
- `router/tools/dpishell-preflight.sh` — пригодность роутера: `{ schema: 1, mode: "check", openwrt { release, revision, arch, target }, pkg, fw4, nft, kernel, kmod_nft_queue { installed, available, version, matches_kernel }, net { feed, fw_feed, github }, space { overlay_free_kb, tmp_free_kb }, foreign [{ kind, name }], dpishell { version, api } | null, luci }`; `--update` — сначала обновить индексы; `--after-uninstall` — что осталось: `{ schema: 1, mode: "after-uninstall", left: [{ what, detail }], kept { settings, https_dns_proxy } }`.
- `router/tools/dpishell-install.sh` — установка: ключ витрины (встроен, sha256) → строка витрины → `apk add dpishell luci-app-dpishell` → ядро → проверка. Ход — строки `# …`, итог `{ schema: 1, ok, step, message, version, api, core }`. Архив ядра окно кладёт заранее по SFTP в `/tmp/dpishell/core/zapret.zip` — тогда сеть для ядра не нужна.
- **Без SSH — через веб-вход** (логин и пароль, как в браузере; эталон — `router/tools/web-install.py`). Только штатные права LuCI для `root`: `session.login` → `file.write /etc/apk/keys/dpishell.pem` (группа `luci-mod-system-repokeys`) → `file.read`/`file.write /etc/apk/repositories.d/customfeeds.list` и `file.exec /usr/libexec/package-manager-call update | install dpishell luci-app-dpishell` (`luci-app-package-manager`) → вход заново и ожидание объекта `dpishell` → архив ядра — `cgi-upload` в `/tmp/dpishell-core.zip` (право записи — в ACL пакета; `core_install` берёт его, если sha256 совпал) → `dpishell.core_install`. Ограничения: rpcd прерывает `file.exec` через 30 с (код ubus 7), а команда идёт дальше — `install` при занятой базе apk повторять, обрыв ответа на `install` (postinst перезапускает rpcd) значит «ставится»; роутеру нужен свой доступ к GitHub для пакетов (туннель через ПК — только SSH). Через веб же можно включить SSH (`uci dropbear`, группа `luci-mod-system-ssh`), если его выключили.
- `router/dpishell/files/usr/libexec/dpishell/uninstall.sh` — удаление без API: `--keep` | `--full`, итог — `/tmp/dpishell-uninstall.json`.
- **Роутер без доступа к GitHub** — через ПК: `ssh -R <порт>:127.0.0.1:<порт>` к локальному HTTP-прокси окна и `https_proxy=http://127.0.0.1:<порт>` в команде. apk шлёт запросы в абсолютной форме (`GET https://…`, TLS к серверу делает прокси), curl — `CONNECT`; прокси должен уметь оба. Подлинность не страдает: apk проверяет подпись индекса и хеши, участок роутер—ПК идёт внутри SSH. Проверено в эмуляторе с закрытым GitHub.

## 6. Что приложение делает с этим (для Windows-стороны)

- **Настройки → «Роутер»**: адрес (по умолчанию «Автоматически — основной шлюз»), пароль, кнопка «Подключиться», строка состояния («Подключено, DpiShell 0.1.0» / «DpiShell на роутере не найден» / «Неверный пароль»).
- **Главный экран**, если роутер найден и обход на нём включён: блок «Обход делает роутер — стратегия N, проверено в 14:00». Кнопки: Включить/Выключить на роутере, Проверить, Подобрать на роутере, выбор стратегии. **Обход на ПК и обход на роутере одновременно не работают**: пакеты, изменённые обходом ПК, роутер изменяет второй раз — стратегии ломаются непредсказуемо, а подбор на ПК находит стратегии, работающие только «в паре» с роутером.
  - Пока `status.enabled == true`, «Включить» на ПК не включает обход на ПК, а предлагает: «Обход уже работает на роутере» и кнопки «Подобрать на роутере» / «Выключить на роутере и включить на этом ПК».
  - Подбор на ПК при `enabled == true` не стартует: «Выключить обход на роутере на время подбора» (`disable`, после подбора — вопрос «Включить на роутере обратно?» и `enable`) или «Подобрать на роутере».
  - Это решение по факту «включён», а не по тому, открываются ли сайты: сломанная стратегия на роутере всё равно обрабатывает пакеты.
  - Обход на ПК уже включён, а роутер сообщает `enabled == true` (включили со страницы роутера или с другого ПК) — приложение выключает обход на ПК и сообщает «Обход перешёл на роутер».
- **Подбор на роутере**: тот же диалог, что для ПК (группы, режим, глубина), таблица из `select_progress`, «Применить на роутере».
- **Стратегия с ПК → на роутер**: найденную глубоким поиском на ПК стратегию можно отправить на роутер (`strategies_user_set` + `strategy_set`). Путь ПК → провайдер идёт через роутер, поэтому стратегия, найденная на ПК при выключенном обходе на роутере, обычно подходит и роутеру; после отправки — `check_now`.
- **Свои адреса и списки роутера** — отдельная вкладка «На роутере» в тех же экранах, с ПК не синхронизируются автоматически (кнопка «Скопировать с ПК»).
- **DNS роутера**: на вкладке «Роутер» — варианты из `dns_presets` плюс свои варианты пользователя (те же, что для ПК; уходят как `custom`: с шаблоном — адреса как `bootstrap`, без — как `plain`), «Вернуть как было» — `dns_restore`. Старый пакет без `dns_presets` — прежняя одна кнопка.
- **Диагностика**: раздел «Роутер» — `diag`, кнопки «Остановить» у конфликтов `kind: service`, «Исправить» для `flow_offload` (`flow_offload_fix`) и `ipv6` (`ipv6_set`), «Сохранить отчёт роутера».
- **Временные метки TCP** (`needs_tcp_timestamps: true`): приложение включает метки на этом ПК само (команда помощника `tcp_timestamps_on`), как перед своим подбором, и пишет одной строкой «Включены временные метки TCP — нужны стратегии роутера». Выключать обратно при уходе из сети не нужно — метки ничему не мешают.
- Сеть сменилась (ноутбук ушёл из дома) — роутер не найден, блок пропадает, приложение работает как обычно.

## 7. Тестирование без роутера

Для тестов Windows-стороны — фейковый роутер в памяти (`tests/DpiShell.Tests/FakeRouter.cs`), который отвечает по этому документу: примеры ответов — разделы 3–5 дословно. Случаи: роутер не найден; `status.json` без `api`; неверный пароль; истёкшая сессия (JSON-RPC `-32002` и `result[0] == 6` → повторный вход → успех); метод отсутствует (`4`); `busy`; `invalid` с `field`; подбор от `running: true` до `best` + `applied: true`; остановленный подбор (`cancelled`); подбор, запущенный не приложением (`started_by: page`); пустые свои адреса роутера при подборе только по ним.

## 8. Планируется (в договор ещё не внесено)

- Пакеты под `opkg` (OpenWrt 24.10): те же методы, сборка в SDK 24.10, записи `pkg: "opkg"` в `data/router-releases.json`.
- Ключ подписи выпусков (`RELEASE_SIGNING_KEY` в Actions, проверочный — в приложении); до него выпуски без `release.json.sig`.

## 9. История договора

API 1 (до первого выпуска роутерного пакета правки шли без увеличения номера):
- Подбор сам включает лучшую (`applied`, `backups`, `cancelled` вместо `restore_at`), `started_by`, `depth`, `eta_s`, `title` у строк; `apply` — выбор другой стратегии из итога; `schedule.hours` с `0.5`, по умолчанию `1`; `schedule.auto_select`, `started_by: timer`, предупреждения `auto_selected`/`auto_select_failed`.
- Истёкшая сессия — JSON-RPC `-32002`; `error.field` — путь; §5.3 — запись стратегии, набор ролей из каталога роутера; присланные стратегии роутер перепроверяет сам.
- Поиск DpiShell не только на шлюзе, но и на частных узлах пути наружу; запрет одновременной работы обходов ПК и роутера; `needs_tcp_timestamps` и включение меток на ПК.
- Режим `mode` (`all`/`lists`), `mode_set`, `site_check`, `lists_get exclude`; группа `control` в подборе (поле `"control": true` в `targets.json`; стратегию бракуют только её `required`-цели, в счёт успеха не входит).
- DNS-задания фоновые (`dns_status.job`, `status.jobs.dns`); варианты DNS — `dns_presets`, `dns_set`, в `dns_status` — `preset`, `custom`, `mode: plain`, `servers[].id`.
- Глубокий поиск на роутере (`depth: deep`, `candidate`, `ok`/`total`, стратегии `r-…`); дробные доли в `rows`.
- `flow_offload_fix`, `ipv6_set`; предупреждения `flow_offload`/`ipv6`/`conflict` в `status.warnings`; `enable`/`select_start` при чужом обходе — `conflict`; `kind: package` в `diag.conflicts`; поле `flowoffload` в `diag`.
- `select_start` только с группой `user` при пустых своих адресах — `invalid`/`groups`; свои адреса в `report` скрыты.

API 1.0 (первый выпуск пакета, 0.2.0): `api` строкой «мажор.минор», `methods`, `min_app`, `update`, `jobs.update`; `uninstall`, `update_install`, `update_check`; ядро договора (§5.4); сценарии по SSH (§5.5); `mode_set` — «Все сайты» / «Только из списков». `rows[].verdicts` в `select_progress`.
