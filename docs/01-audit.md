# Аудит основы для оболочки обхода DPI под Windows

Дата: 2026-10-16. Ничего не запускалось на машине — только чтение источников. Где написано «не проверено» — утверждение из документации/логики, не подтверждённое экспериментом.

## Итог

1. **Ядро — `winws.exe` из zapret v1 (bol-van), перехват пакетов через WinDivert.** Это единственная схема, которая покрывает всю систему (TCP+UDP: TLS, QUIC, голос Discord, игры) без настройки прокси в каждом приложении. Прокси-ядра (byedpi/ciadpi, SpoofDPI, Xray) проигрывают именно на UDP и «приложениях без прокси»; своё ядро на WinDivert — месяцы работы ради повторения того, что в winws уже есть.
2. **Бинарники берём напрямую из релизов `bol-van/zapret`** (zip содержит `windows-x86_64/{winws.exe, WinDivert.dll, WinDivert64.sys, cygwin1.dll}` и `sha256sum.txt`). Версию и хеши закрепляем в своём манифесте. От релизов Flowseal бинарно не зависим.
3. **Стратегии и списки — свои данные**, посев из Flowseal (`general*.bat` как данные, `lists/*.txt`, фейки `bin/*.bin`; MIT) плюс порядок перебора из `blockcheck.sh`. Храним в своём репозитории, обновляем по своему манифесту с хешами.
4. **zapret2 (winws2, lua-стратегии) — не сейчас.** Синтаксис другой, автор позиционирует как «конструктор для энтузиастов», лицензия в репозитории не объявлена. Закладываем слой «движок → аргументы», чтобы добавить winws2 позже.
5. **Критерий «работает»** — не статус < 500, а: TLS-рукопожатие (отдельно 1.2 и 1.3) + полная загрузка тела > 20 КБ (ТСПУ рвёт на 16–20 КБ) + отсутствие редиректа на чужой хост + QUIC-проверка для YouTube. Две попытки на цель.
6. **Параллельный подбор** — документация допускает несколько winws с непересекающимися фильтрами; разнести по диапазонам исходных портов через `--wf-raw` можно теоретически. Не проверено. В ТЗ — как эксперимент с запасным последовательным режимом (≈5–7 с на стратегию).
7. **Стек — C#/.NET 8 + WPF**, один self-contained exe с манифестом `requireAdministrator`, трей, `sc` для службы. Python+PyInstaller даёт ложные срабатывания антивирусов поверх и так подозрительного WinDivert.

## 0. Нужен ли zapret вообще

Цель: обход DPI для всей системы, удобная оболочка, автоподбор, свои адреса для проверки. Варианты:

| Вариант | Что это | UDP (QUIC, Discord voice, игры) | Вся система без настройки приложений | Готовность стратегий | Вердикт |
|---|---|---|---|---|---|
| winws (zapret v1) | перехват пакетов WinDivert | да: `--filter-udp`, фейки QUIC/STUN/Discord ([general.bat](https://github.com/Flowseal/zapret-discord-youtube/blob/main/general.bat)) | да | десятки режимов, fake/autottl/multisplit/seqovl, hostlist/ipset ([readme](https://github.com/bol-van/zapret/blob/master/docs/readme.en.md)) | **берём** |
| winws2 (zapret2) | то же ядро, стратегии на Lua | да | да | библиотека `zapret-antidpi.lua`, другой синтаксис ([zapret2](https://github.com/bol-van/zapret2)) | позже, через адаптер |
| Своё ядро на WinDivert (pydivert/Go/Rust) | писать перехват и десинк самим | только если напишем | да | ноль; повторить fake-пакеты, autottl, multisplit с seqovl, разбор TLS/QUIC, hostlist, RST-детект — месяцы; pydivert жив ([pushed 2026-10](https://api.github.com/repos/ffalcinelli/pydivert)), но это только биндинг | нет |
| GoodbyeDPI | перехват WinDivert, предшественник | QUIC только **блокирует** (`-q`), не обходит ([README](https://github.com/ValdikSS/GoodbyeDPI)) | да | последний релиз 0.2.2, 2022-03 ([releases](https://api.github.com/repos/ValdikSS/GoodbyeDPI/releases/latest)) | нет |
| byedpi / ciadpi | локальный SOCKS5/HTTP-прокси, Windows-сборки есть (`byedpi-17.3-x86_64-w64.zip`, 2025-09, [releases](https://api.github.com/repos/hufrea/byedpi/releases/latest)) | SOCKS5 UDP ASSOCIATE и `--udp-fake` есть ([README](https://github.com/hufrea/byedpi)), но приложение должно само уметь SOCKS5-UDP; голос Discord через прокси не идёт (обсуждения в [issues byedpi](https://github.com/hufrea/byedpi/discussions/179)) | нет: нужен системный прокси (игнорируется многими приложениями/играми) или ProxiFyre — а это снова драйвер WinDivert ([ByeDPIManager](https://github.com/romanvht/ByeDPIManager)) | хороший набор TCP-методов, `--auto` | нет как основа |
| SpoofDPI | HTTP-прокси на Go, TLS-фрагментация ([repo](https://github.com/xvzc/SpoofDPI)) | нет | нет | 1–2 приёма | нет |
| Xray/sing-box `fragment`+TUN | TLS ClientHello фрагментация, UDP «noises» ([freedom](https://xtls.github.io/en/config/outbounds/freedom.html)) | частично | через TUN (wintun — тоже драйвер, админ) | 2 приёма, тяжёлая зависимость | нет |

Схема «прокси + системный прокси/TUN» против «перехват пакетов»:
- Плюсы прокси: без ядра-драйвера (если без ProxiFyre/TUN), меньше претензий антивирусов, подбор параллелится естественно (каждая стратегия — свой порт).
- Минусы прокси: приложения без поддержки прокси и игры идут мимо; UDP (QUIC YouTube, голос Discord) требует SOCKS5-UDP на стороне клиента; TUN-вариант возвращает драйвер и права админа, то есть теряет главный плюс.
- Перехват (winws): покрывает всё, но требует админа, драйвер WinDivert ругают антивирусы ([bundle readme](https://github.com/bol-van/zapret-win-bundle)), параллельный подбор нетривиален (см. п. 4).

**Решение: winws.** Параллельность подбора решаем на стороне проверки (диапазоны исходных портов) или принимаем последовательный режим — это дешевле, чем терять UDP и «всю систему». Гибрид «ciadpi для быстрого TCP-отсева + winws для финала» отвергнут: разные движки, стратегии не переносимы.

## 1. Какую основу брать

### 1.1 bol-van/zapret (v1)
- Релиз [v72.13](https://api.github.com/repos/bol-van/zapret/releases/latest) (2026-07-21): `zapret-v72.13.zip` + `sha256sum.txt`. В `sha256sum.txt` есть `windows-x86_64/{winws.exe, WinDivert.dll, WinDivert64.sys, cygwin1.dll, killall.exe, mdig.exe, ip2net.exe}` и `windows-x86/…` — то есть **бинарники Windows лежат в основном релизе**, отдельный бандл для них не нужен.
- Windows-специфика ([windows.md](https://github.com/bol-van/zapret/blob/master/docs/windows.md), [windows.en.md](https://raw.githubusercontent.com/bol-van/zapret/master/docs/windows.en.md)): фильтр перехвата `--wf-l3`, `--wf-tcp`, `--wf-udp`, `--wf-raw=@file` (полный фильтр WinDivert, заменяет остальные), `--wf-raw-part`, `--wf-save` (сохранить сконструированный фильтр), `--wf-filter-lan`, `--ssid-filter`, `--nlm-filter`. «Можно запускать несколько процессов winws с разными стратегиями. Однако не следует делать пересекающиеся фильтры». Если рядом с winws лежит вторая копия `cygwin1.dll` — не запустится. Secure Boot: «могут быть проблемы из-за подписи драйвера… отключите secureboot или включите testsigning».
- Проверка аргументов без запуска: `--dry-run` ([readme.en.md](https://raw.githubusercontent.com/bol-van/zapret/master/docs/readme.en.md)). Там же `--hostlist-auto` (автосписок по признакам блокировки: `--hostlist-auto-fail-threshold`, `-fail-time`, `-retrans-threshold`) — полезно как режим «всё, что ломается», но не заменяет подбор стратегии.
- Лицензия: README говорит MIT, но файла LICENSE в корне нет ([contents](https://api.github.com/repos/bol-van/zapret/contents/), GitHub API `license: null`). Распространение бинарников практикуют Flowseal и все GUI; формально — открытый вопрос.

### 1.2 zapret-win-bundle
- [Репозиторий](https://github.com/bol-van/zapret-win-bundle): `zapret-winws/` (winws.exe 223 КБ, winws2.exe 656 КБ, WinDivert, cygwin1.dll, `preset1_example.cmd`, `service_create.cmd`, `windivert_delete.cmd`, `elevator.exe`, `killall.exe`, `lua/`, `files/`), `blockcheck/` (`blockcheck.cmd`, `blockcheck-kyber.cmd`, `blockcheck2.cmd`), `cygwin/` с curl (есть `cygnghttp3-9.dll` → HTTP/3 в curl вероятен, не проверено).
- blockcheck под Windows — это `blockcheck.sh` в cygwin: при CYGWIN запускает `winws $WF --ipset=…` для каждой стратегии, `SKIP_TPWS=1`. Требует остановить zapret и другие обходы. Для нашей оболочки это референс алгоритма, а не компонент: тащить cygwin (десятки МБ) и bash ради перебора нецелесообразно.
- Служба: `service_create.cmd` — `sc create winws1 binPath= "\"%~dp0winws.exe\" %ARGS%" DisplayName= "…" start= auto`, закомментировано с предупреждением «не запускать без редактирования». То есть winws **работает как служба напрямую** через `sc create`, без обёртки (так же делает Flowseal).
- Лицензия бандла не объявлена (API `null`).

### 1.3 zapret2 / winws2
- [bol-van/zapret2](https://github.com/bol-van/zapret2): активен (push 2026-10-04, 5.7k звёзд), релизы v0.9.x. nfqws2 оставляет разбор протоколов, профили, hostlist/ipset, а воздействие на трафик вынесено в Lua: `--lua-init=@lua/zapret-lib.lua`, `--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=11:tls_mod=rnd,dupsid`, цепочки `--lua-desync` по порядку. Фильтры `--wf-tcp-in/out`, `--wf-udp-in/out`, `--wf-raw-part`. Конвертация из v1 нетривиальна (`--dpi-desync-ttl=6` → `--lua-desync=pktmod:ip_ttl=6:ip6_ttl=6`; `fake,multisplit` → два `--lua-desync`) ([docs/readme.md](https://raw.githubusercontent.com/bol-van/zapret2/master/docs/readme.md)).
- Позиционирование: «не готовое решение для новичков, инструмент для энтузиастов»; v1 не объявлен замороженным (push v1 тоже 2026-10-04). Лицензия в репозитории не объявлена.
- Вывод: v1 — стабильный синтаксис, вся экосистема стратегий (Flowseal, blockcheck) на нём; v2 — больше гибкости, но свои стратегии придётся писать/портировать. Делаем адаптер движка (`engine: winws | winws2`, шаблон аргументов), v2 — не в первой версии.

### 1.4 Flowseal/zapret-discord-youtube
- [Дерево](https://api.github.com/repos/Flowseal/zapret-discord-youtube/git/trees/main?recursive=1): 20+ `general*.bat` (ALT1–13, SIMPLE FAKE, FAKE TLS AUTO, EXP), `service.bat`, `bin/` (winws.exe, WinDivert, cygwin1.dll, фейки `tls_clienthello_*.bin`, `quic_initial_*.bin`, `ACTIVE_DISCORD_UDP.bin`, `ACTIVE_GAME_UDP.bin`, `stun*.bin`), `lists/` (`list-general.txt`, `list-google.txt`, `list-exclude.txt`, `ipset-all.txt`, `ipset-exclude.txt`, `*-user.txt`), `utils/` (`targets.txt`, `test zapret.ps1`), `.service/version.txt`. README: бинарники «взяты из zapret-win-bundle и zapret/releases». Лицензия MIT (`LICENSE.txt`).
- Структура стратегии (из [general.bat](https://api.github.com/repos/Flowseal/zapret-discord-youtube/contents/general.bat)): один winws, `--wf-tcp=80,443,2053,…,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP%`, далее 8–9 профилей через `--new`: QUIC на 443 с `--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=…`; Discord/STUN на UDP 19294–19344, 50000–50100 с `--filter-l7=discord,stun`; TCP 443 по `list-google.txt` с `multisplit` + seqovl + паттерн ClientHello; общий TCP по `list-general.txt`; ipset-профили; игровые порты с `--dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3`. Варианты ALT отличаются десинк-частью профилей. Это **данные**, их можно хранить в своём формате (так делает [noirlie/zapret-desktop](https://github.com/noirlie/zapret-desktop): «BAT-файлы анализируются как данные»).
- `service.bat` ([разбор](https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/service.bat)): Install = парсит выбранный .bat и делает `sc create zapret binPath= "\"…winws.exe\" <args>"`, имя файла пишет в реестр `HKLM\…\Services\zapret\zapret-discord-youtube`; Check Status = `sc query zapret`, `tasklist winws.exe`, `sc query WinDivert`; Diagnostics = предлагает удалить конфликтующие службы `GoodbyeDPI`, `discordfix_zapret`, `winws1`, `winws2`, затем `net stop WinDivert`, `sc delete WinDivert`, `sc delete WinDivert14`; Game Filter = порты `1024-65535` TCP/UDP в `utils/game_filter.enabled`; обновления — сравнение `.service/version.txt` с raw GitHub и открытие страницы релиза.
- Тесты ([test zapret.ps1](https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/utils/test%20zapret.ps1)): последовательно по .bat-файлам (`Start-Process cmd /c bat`, перед следующим `Stop-Process winws`), для каждой стратегии параллельно (runspace pool, 8–16 потоков) `curl.exe -I -s -m 4 --connect-timeout 2 -o NUL -w %{http_code}` по `targets.txt` в трёх вариантах (HTTP/1.1, TLS1.2, TLS1.3) + ping; счёт = число OK. **HEAD без тела** — не ловит обрыв на 16 КБ. Режим DPI checker берёт `https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.v2.json` (35 хостов по провайдерам) и шлёт POST со случайным телом 16–20 КБ, `LIKELY_BLOCKED` = байты ушли, ответа нет, таймаут. `targets.txt`: discord.com/gateway/cdn/updates, youtube.com, youtu.be, i.ytimg.com, редирект на googlevideo, google.com, gstatic, cloudflare + ping 1.1.1.1/8.8.8.8.
- Релиз ([release.yml](https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.github/workflows/release.yml)): zip/rar/tar.gz из `bin/ lists/ utils/ *.bat`; SHA-256 печатается в лог workflow, **к релизу файл с хешами не прикладывается** — закреплять по хешу придётся собственным манифестом.
- Что берём: списки доменов/ipset, фейки `.bin`, набор стратегий как посев каталога, идею game filter и диагностики конфликтов. Что не берём: .bat как исполняемый код, их механизм обновления, cygwin1.dll из их bin (берём из релиза zapret).

### 1.5 Лицензии
- zapret: MIT по README, файла LICENSE нет — уточнить (открытый вопрос). zapret2 и win-bundle: не объявлена.
- Flowseal: MIT. ByeByeDPI: GPL-3.0 (код не берём — не важно). hyperion dpi-checkers: Apache-2.0.
- WinDivert: LGPLv3 ([doc](https://raw.githubusercontent.com/basil00/WinDivert/master/doc/windivert.html)); используется как отдельные DLL/sys — достаточно приложить текст лицензии и ссылку на исходники. cygwin1.dll: LGPLv3 (так указывает [LoST202/zapret2gui](https://github.com/LoST202/zapret2gui)) — то же.

### 1.6 Вывод по зависимостям
- Бинарники: `bol-van/zapret` релиз (zip + `sha256sum.txt`), закреплённая версия, хеши четырёх файлов зашиты в манифест приложения.
- Стратегии: собственный каталог (JSON) в своём репозитории, посев из Flowseal + blockcheck; обновление каталога — свой манифест с хешами.
- Списки и фейки: копия из Flowseal в своём репозитории; опциональное обновление с raw GitHub Flowseal по URL с проверкой хеша из нашего манифеста.
- Проверка блокировки на 16–20 КБ: свой клиент; список хостов hyperion — как дополнительный источник целей.

## 2. Автоподбор у других

- **blockcheck.sh** ([источник](https://raw.githubusercontent.com/bol-van/zapret/master/blockcheck.sh)): домены по умолчанию `rutracker.org`, спрашивает список; сначала проверка без обхода (`check_domain_prolog`) — если работает, пропуск; тесты `curl_test_http`, `curl_test_https_tls12`, `curl_test_https_tls13`, `curl_test_http3` (`--http3-only`), тело в `/dev/null` (полная загрузка), `CURL_MAX_TIME=2`; редирект 301/302/307/308 на другой домен и HTTP 400 — подозрительны; `REPEATS`, `SCANLEVEL=quick|standard|force`; порядок стратегий: HTTP-трюки → multisplit/multidisorder по позициям → TTL → fake/fakedsplit/fakeddisorder/hostfakesplit → fooling badsum/badseq/datanoack/ts/md5sig → seqovl → autottl. Запуск winws на каждую стратегию последовательно, `PARALLEL=1` только для повторов одного теста. Берём: порядок перебора, пропуск уже работающих, DoH при подменённом DNS (`doh_find_working`).
- **ByeByeDPI** (только UX): группы сайтов по сервисам (`proxytest_youtube.sites`: youtube.com, i.ytimg.com, googleapis, googlevideo, …; discord, cloudflare, telegram, general), пользователь добавляет свои; настройки «запросов на сайт», «таймаут», «пауза между стратегиями», «полный лог»; результат — процент успешных запросов по каждой стратегии с разбивкой по сайтам, применение одним нажатием, результаты сохраняются. Критерий у них — тело прочитано не меньше `Content-Length` (иначе «Block detected»), до 1 МБ ([SiteCheckUtils.kt](https://raw.githubusercontent.com/romanvht/ByeByeDPI/master/app/src/main/java/io/github/romanvht/byedpi/utility/SiteCheckUtils.kt)); 20 параллельных запросов на стратегию. Переносим: группы, свои адреса, процент с разбивкой, одна кнопка «применить».
- **Windows-оболочки**: [Arxn0n/zapret-gui](https://github.com/Arxn0n/zapret-gui) (Python/customtkinter, PyInstaller; стратегии последовательно, на каждую параллельно «страница, картинка, CDN с видео», 5–7 с на стратегию, предварительная проверка YouTube без zapret для детекта VPN); [noirlie/zapret-desktop](https://github.com/noirlie/zapret-desktop) (C#/WPF, служба + named pipe, сначала сохранённая стратегия для текущей сети, «один неуспешный ответ не переключает», SHA-256 при загрузке компонентов, откат); [LoST202/zapret2gui](https://github.com/LoST202/zapret2gui) (.NET 10/WPF под winws2, watchdog, очистка драйвера); [Tchk-zz/cat-zapret](https://github.com/Tchk-zz/cat-zapret) (PyQt6, GPL-3, «отличает реальный ответ от страницы блокировки», проверка zip на bomb/traversal). Избегать: исполнение .bat, HEAD-проверки, зависимость от релизов Flowseal, отсутствие памяти «стратегия ↔ сеть».

## 3. Критерий «стратегия работает»

Собрано из blockcheck, ByeByeDPI, hyperion и Flowseal; ни один источник не делает всё сразу.
1. TLS-рукопожатие на 443 отдельно для TLS 1.2 и TLS 1.3 (blockcheck): ТСПУ режет по-разному, браузеры используют 1.3, старые приложения — 1.2.
2. Полная загрузка тела **> 20 КБ** и сравнение с `Content-Length` (ByeByeDPI) — ловит обрыв после 16–20 КБ, который hyperion описывает как отдельный метод блокировки. Нужны цели с большим ответом (страница youtube.com, картинка `i.ytimg.com`, `https://www.gstatic.com/...`), либо POST 16–20 КБ по методике hyperion.
3. Редирект на другой хост и HTTP 400 — провал (blockcheck); статус < 500 без этих проверок — ложный успех.
4. QUIC: `--http3-only`-запрос к `www.youtube.com`/`googlevideo` (blockcheck). Если QUIC не обходится, но TCP обходится — YouTube всё равно работает (браузер падает на TCP), но хуже; учитывать как «частично».
5. googlevideo отдельно от youtube.com: домены видео (`*.googlevideo.com`, `manifest.googlevideo.com`) — обязательные цели.
6. Голос Discord/игры (UDP без HTTP): автоматической проверки ни у кого нет (Flowseal только ping). В v1 — не проверяем, профиль Discord/STUN берём фиксированным из каталога; в UI — ручное «проверить голос» (открытый вопрос).
7. Две попытки на цель с паузой; стратегия считается рабочей по группе, если прошли все обязательные цели группы хотя бы в одной попытке; итог — доля групп, затем время.
8. До подбора: проверка без обхода (пропуск уже доступного; если всё доступно — VPN/не заблокировано), проверка DNS (ответ системного DNS vs DoH; при расхождении — предупреждение).

## 4. Параллельный подбор под Windows

- Документация: несколько winws допустимы, пересекающиеся фильтры нежелательны ([windows.md](https://raw.githubusercontent.com/bol-van/zapret/master/docs/windows.md)). WinDivert: несколько хендлов сосуществуют, порядок по `priority`; «injected packets can be captured and diverted again by other WinDivert handles with lower priorities» ([doc](https://raw.githubusercontent.com/basil00/WinDivert/master/doc/windivert.html)) — то есть пересечение фильтров ведёт к двойной обработке.
- Схема: N экземпляров winws, каждый с `--wf-raw=@slotN.txt`, где фильтр = сконструированный базовый (получить через `--wf-save`) с добавкой `tcp.SrcPort >= A and tcp.SrcPort <= B` для исходящих и зеркально `tcp.DstPort` для входящих; клиент проверки привязывает сокет к порту из диапазона слота. Фильтры не пересекаются → реинжект одного экземпляра не попадает в другой. Для UDP/QUIC — аналогично `udp.SrcPort`. **Не проверено**; известные риски: winws может ожидать в фильтре определённые части (входящие RST/ответы для autottl), ошибка синтаксиса даёт код 87 «The parameter is incorrect» ([issue 1672](https://github.com/bol-van/zapret/issues/1672)); конфликты «WinDivert STOP_PENDING» в issues Flowseal связаны с античитами и чужими обходами, не с числом экземпляров ([#16170](https://github.com/Flowseal/zapret-discord-youtube/issues/16170)).
- Последовательный режим (запасной): 1 с старт winws + 4–6 с проверок ≈ 5–7 с на стратегию (оценка Arxn0n); 25 стратегий ≈ 2.5–3 мин, 60 (глубокий перебор) ≈ 6–7 мин. Приемлемо с прогресс-баром и ранним выходом «первая рабочая».
- Решение: сначала последовательный режим как обязательный; параллельный — spike на 1–2 дня в начале разработки, включается при успехе (4 слота → ≈4× быстрее).

## 5. Подводные камни Windows

- Антивирусы: WinDivert детектится как RiskTool/VulnDriver ([Malwarebytes](https://forums.malwarebytes.com/topic/338772-most-likely-false-positive-riskwarewindivertvulndriver-windivert64sys/), [ESET](https://forum.eset.com/topic/42349-false-positive/)); Kaspersky удалял драйвер ([#7585](https://github.com/Flowseal/zapret-discord-youtube/issues/7585)). Нужны: проверка целостности файлов по хешам при каждом запуске, понятное сообщение «антивирус удалил драйвер», инструкция по исключению папки.
- Конфликты: GoodbyeDPI (служба `GoodbyeDPI`, драйвер `WinDivert14`), чужие `winws1/winws2/zapret/discordfix_zapret`, античиты (Fortnite) → `WinDivert STOP_PENDING` ([#16170](https://github.com/Flowseal/zapret-discord-youtube/issues/16170), [#15906](https://github.com/Flowseal/zapret-discord-youtube/issues/15906)). Нужна диагностика как у Flowseal, с согласия пользователя. GoodbyeDPI также несовместим с ESET и Killer «Advanced Stream Detect» ([README](https://github.com/ValdikSS/GoodbyeDPI)) — та же природа.
- Служба vs процесс: служба `sc create … start= auto` переживает выход из системы и не требует запущенного GUI; процесс нужен для подбора (быстрый старт/стоп). Оба режима обязательны. Драйвер WinDivert ставится автоматически при первом `WinDivertOpen`, удаляется `sc stop/delete WinDivert` ([doc](https://raw.githubusercontent.com/basil00/WinDivert/master/doc/windivert.html)).
- Права: WinDivert требует администратора всегда — и для подбора, и для службы. GUI запускаем с `requireAdministrator`; автозапуск GUI с повышением — через планировщик (бандл использует `task_*.cmd`).
- Secure Boot: драйвер подписан, но документация предупреждает о возможных проблемах; ARM64 — только testsigning (вне рамок). Windows 7 — отдельные WinDivert (вне рамок).
- VPN рядом: при активном VPN-туннеле трафик уходит в интерфейс VPN, обход не нужен/мешает; практика — пауза winws при обнаружении VPN ([Polinzfr/Zapret-Turkey v1.1.0](https://github.com/Polinzfr/Zapret-Turkey/releases/tag/V1.1.0)); официальных указаний у bol-van нет (не проверено). У нас: детект VPN-адаптера/«всё доступно без обхода» → предупреждение, подбор не запускать.
- Копия `cygwin1.dll` рядом с winws — не стартует; прочие: Mark-of-the-Web на скачанных файлах (Flowseal просит «Разблокировать»), смена сети (Wi-Fi/мобильный) — другая стратегия, hosts-файл для Discord/Telegram (Flowseal правит hosts — в v1 не трогаем).

## 6. Стек оболочки

Требования: один exe, UAC-манифест, трей, автозапуск, управление службой, HTTP-клиент с контролем версии TLS и желательно HTTP/3, минимум претензий антивирусов.

| | C#/.NET 8 + WPF | Python + PySide6/customtkinter + PyInstaller |
|---|---|---|
| Один exe | `PublishSingleFile` + `SelfContained` + `IncludeNativeLibrariesForSelfExtract` ([docs](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)); размер ~70–100 МБ без trimming (оценка, не проверял) | `--onefile --uac-admin --windowed` ([docs](https://pyinstaller.org/en/stable/usage.html)) |
| Антивирусы | обычный PE, без распаковки в temp | onefile-бутлоадер регулярно ловится эвристикой ([pythonguis](https://www.pythonguis.com/faq/problems-with-antivirus-software-and-pyinstaller/)); поверх WinDivert — двойной риск |
| UAC/трей/служба | манифест `requireAdministrator`, `NotifyIcon`, `ServiceController`/`sc` | через pywin32/ctypes, больше склейки |
| HTTP-клиент | `HttpClient`: TLS-версии через `SslClientAuthenticationOptions`, HTTP/3 через msquic ([docs](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-http3), требования платформы не проверял) | `ssl`/`urllib3` для TLS; HTTP/3 — внешний curl или aioquic |
| Привязка исходного порта | `SocketsHttpHandler.ConnectCallback` (стандартный API, не проверял) | `urllib3 HTTPConnection(source_address=…)` |
| Прецеденты | noirlie, LoST202 — самые зрелые оболочки на WPF | Arxn0n (customtkinter), cat-zapret (PyQt6) |

**Рекомендация: C#/.NET 8 + WPF**, Core-библиотека без UI (как у LoST202: `Zapret.Core` + `Zapret.App`) и консольный тестовый раннер поверх Core. Причина: антивирусы и UAC/трей/служба — «из коробки», без PyInstaller-бутлоадера. Если команда только на Python — PySide6 + Nuitka (не onefile PyInstaller), но это второй выбор.
