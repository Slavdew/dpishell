# B. zapret (nfqws) на OpenWRT — перенос DpiShell

Источники: bol-van/zapret тег v72.13 (`R` = https://github.com/bol-van/zapret/blob/v72.13), релиз https://github.com/bol-van/zapret/releases/tag/v72.13, openwrt/packages ветка openwrt-25.12.

## 1. Установка zapret на OpenWRT (по документации v72.13)

- Документ один на Linux и OpenWRT: `R/docs/quick_start.md` (файла quick_start_openwrt нет). Порядок: распаковать релиз в /tmp → `install_bin.sh` → `install_prereq.sh` → `blockcheck.sh` → `install_easy.sh` (копирует в `/opt/zapret`). Нужно 1–2 МБ свободного места; при нехватке — только tpws (`R/init.d/openwrt-minimal/readme.txt`, 120–200 КБ).
- Ассеты релиза: `zapret-v72.13.tar.gz` (7.8 МБ, все платформы), `zapret-v72.13-openwrt-embedded.tar.gz` (3.5 МБ: только binaries/linux-*, common, ipset, init.d, files/fake, скрипты), `sha256sum.txt`.
- Бинарники: `binaries/linux-{arm,arm64,lexra,mips,mips64,mipsel,ppc,x86,x86_64}/{nfqws,tpws,mdig,ip2net}` (listing tar.gz). Архитектуру `install_bin.sh` выбирает тест-запуском `ip2net` из каждой папки (`R/install_bin.sh`, `check_dir`), приоритет у `binaries/my`.
- Пакетный менеджер: установщик распознаёт и opkg, и apk (`R/common/installer.sh#L207-L216`: `apk add`, без update). OpenWRT 25.12.0 (март 2026) заменил opkg на apk — подтверждено (heise, helpnetsecurity).
- Пакеты (`R/common/installer.sh#L732-L748`): nftables-вариант — `curl nftables kmod-nft-nat kmod-nft-offload kmod-nft-queue`; iptables-вариант — `curl ipset iptables iptables-mod-extra iptables-mod-nfqueue iptables-mod-filter iptables-mod-ipopt iptables-mod-conntrack-extra iptables-mod-u32` (+ip6tables-mod-nat, ip6tables-extra). Опционально GNU gzip.
- Официального пакета zapret в feeds OpenWRT нет (issue remittor/zapret-openwrt#824, discussion bol-van/zapret#2031). Сторонние: remittor/zapret-openwrt (релиз v72.20260307, zip по архитектурам, luci-app-zapret), etozhecyber/zapret-openwrt; для zapret2 — 1andrevich/zapret2-openwrt, edwardgushchin/luci-app-zapret2. Не проверены на содержимое.
- Служба: `/etc/init.d/zapret` → symlink на `/opt/zapret/init.d/openwrt/zapret`, procd (`USE_PROCD=1`, `START=21`), каждый демон — `procd_open_instance` с pidfile (`R/init.d/openwrt/zapret#L50-L62`). Номера инстансов: tpws-socks=2, tpws=1, nfqws=3 (`R/common/linux_daemons.sh`), custom — 1000–1999 (`alloc_dnum`, `R/common/custom.sh`). Доп. команды: `start_fw/stop_fw/restart_fw/reload_ifsets/list_table/start_daemons/stop_daemons/restart_daemons`.
- Конфиг `/opt/zapret/config` (из `R/config.default`): `NFQWS_ENABLE`, `NFQWS_PORTS_TCP=80,443`, `NFQWS_PORTS_UDP=443`, `NFQWS_TCP_PKT_OUT=$((6+3))`, `NFQWS_TCP_PKT_IN=3`, `NFQWS_UDP_PKT_OUT=9`, `NFQWS_UDP_PKT_IN=0`, `NFQWS_PORTS_TCP_KEEPALIVE`, многострочный `NFQWS_OPT` с профилями через `--new` и плейсхолдерами `<HOSTLIST>`/`<HOSTLIST_NOAUTO>`, `MODE_FILTER=none|ipset|hostlist|autohostlist`, `FLOWOFFLOAD=donttouch|none|software|hardware`, `DISABLE_IPV6=1`, `POSTNAT` (по умолчанию post-nat), `DESYNC_MARK=0x40000000`, `FILTER_MARK`, `OPENWRT_LAN/WAN4/WAN6`, `WS_USER=daemon`, `QNUM=200` (`R/init.d/openwrt/functions#L19-L24`), `FILTER_TTL_EXPIRED_ICMP=1`, `GETLIST`.
- Командная строка nfqws = `--user=$WS_USER --dpi-desync-fwmark=$DESYNC_MARK --qnum=$QNUM $NFQWS_OPT` (`R/init.d/openwrt/zapret#L48`, `R/common/linux_daemons.sh#L25`).
- Firewall: при fw4/nftables zapret ведёт собственную таблицу `inet zapret` из init-скрипта (`start_service` → `start_fw`, `R/init.d/openwrt/zapret#L171`), fw4 не трогает; hotplug `R/init.d/openwrt/90-zapret` перечитывает наборы интерфейсов при ifup/ifdown. При fw3+iptables — include `/etc/firewall.zapret`. `install_easy.sh` при `FLOWOFFLOAD=donttouch` и включённом nfqws выставляет `uci firewall.@defaults[0].flow_offloading=0` (`R/install_easy.sh#L768-L805`).
- custom.d: `/opt/zapret/init.d/openwrt/custom.d/*` — shell-файлы с функциями `zapret_custom_daemons`, `zapret_custom_firewall` (ipt), `zapret_custom_firewall_nft`, `zapret_custom_firewall_nft_flush`; очереди `alloc_qnum` 65400–65499. Примеры: `R/init.d/custom.d.examples.linux/50-discord-media`, `50-quic4all`, `50-nfqws-ipset`.

## 2. Как nfqws ловит трафик

- Правило (`R/common/nft.sh#L615-L637`, `#L468-L478`): `oifname @wanif  mark and 0x40000000 == 0  tcp dport {80,443} ct original packets 1-9  ip daddr != @nozapret  [meta mark set mark or 0x20000000]  queue num 200 bypass`. `nft_connbytes="ct original packets"` (`nft.sh#L2`); лимит = `NFQWS_*_PKT_OUT`. Цепочка: `postnat` (hook postrouting, priority 101 — после SNAT) в режиме POSTNAT (по умолчанию) или `postrouting` (priority 99) при `POSTNAT=0` (`nft.sh#L89-L98`, `#L449-L466`).
- Входящие (`PKT_IN`, нужны для autottl/autohostlist): `iifname @wanif tcp sport {..} ct reply packets 1-3` в `prenat`/`prerouting` (`nft.sh#L254-L257`, `#L508-L520`). Сгенерированные nfqws пакеты (mark) — `notrack` в hook output priority -401 (`nft.sh#L103-L111`).
- Локальный vs транзитный: hook postrouting видит и FORWARD-пакеты клиентов, и OUTPUT-пакеты роутера; фильтра по `skuid` у nfqws-правил нет (у tpws есть `skuid != $WS_USER`). curl на роутере проходит через ту же очередь и те же профили nfqws, что и клиенты. Разницы по TTL нет: nfqws выставляет TTL фейка абсолютно, autottl считает от TTL входящего SYN,ACK (путь роутер→сервер одинаков).
- Отличия по техникам (`R/docs/readme.md#L325-L372`): `badsum` — с роутера работает, через роутер на mediatek не проходит (драйвер режет); conntrack checksum на OpenWRT по умолчанию выключен. `datanoack` — на nftables без ограничений, ломает NAT с iptables masquerade. `md5sig`+fakedsplit с малым split-pos — переполнение MTU при kyber («message too long»).
- Flow offloading (`readme.md#L1006-L1030`): после попадания сеанса в flowtable nfqws его не видит. zapret при `FLOWOFFLOAD=software|hardware` строит свою flowtable и exemption-правила из своих же NFQUEUE-правил (`nft.sh#L262-L288`, `#L355-L380`), offload только для `forward oifname @wanif` — локальный трафик роутера не оффлоадится никогда. Системный `flow_offloading=1` fw4 несовместим с nfqws (установщик выключает). Hardware offload — только nftables, не все порты mediatek его умеют (`nft.sh#L370-L379`).
- Mediatek + `rx-gro-list` + UDP: при появлении nfqueue теряются пакеты (`readme.md#L1035-L1040`).

## 3. blockcheck.sh на OpenWRT (`R/blockcheck.sh`)

- Неинтерактивно: `BATCH=1` убирает все `read`/`ask_*` (`#L85`, `#L1928-L2052`). Переменные: `DOMAINS="a.com b.com"`, `IPVS=4|6|46`, `ENABLE_HTTP`, `ENABLE_HTTPS_TLS12`, `ENABLE_HTTPS_TLS13`, `ENABLE_HTTP3`, `REPEATS`, `PARALLEL=1` (параллелятся только повторы одного теста), `SCANLEVEL=quick|standard|force`, `SKIP_TPWS`, `SKIP_PKTWS`, `SKIP_IPBLOCK`, `SKIP_DNSCHECK`, `CURL_MAX_TIME=2`, `CURL_HTTPS_GET=1` (иначе HTTPS проверяется HEAD `-I`, `#L1875`), `QNUM=59780`, `PKTWS_EXTRA[_PRE][_1..9]`, `CURL_OPT`, `USER_AGENT`, `UNBLOCKED_DOM=iana.org`, `DOH_SERVERS`. Нужен root.
- Разбор вывода: строки `!!!!! AVAILABLE !!!!!` / `UNAVAILABLE code=N` (`#L1065-L1072`), в конце `* SUMMARY` и `report_print` — по строке `<домен> <тест> ipv4 : nfqws <параметры>` на каждый успех (`#L1174-L1216`, `#L2328`). Парсить реально.
- Критерий (`#L698-L760`): curl завершился с 0. HTTP: редирект 301/302/307/308 на чужой хост → 254, код 400 → 254. HTTPS: `--tlsv1.2 --tls-max 1.2` / `--tlsv1.3 --tls-max 1.3`, `-o /dev/null`, по умолчанию HEAD; размер тела не проверяется. HTTP/3: `--http3-only`. Имена резолвит mdig и подставляет IP (`curl_with_dig`, `#L665`). Перед перебором: проверка DNS-подмены (`DNSCHECK_DNS/DOM`, DoH), проверка блокировки по IP (`check_domain_port_block`, тест домена на IP iana.org, `#L1703-L1717`).
- Firewall у blockcheck свой (`nft_scheme`, `#L820-L838`): таблица с hook postrouting priority 102, без connbytes (вся сессия в очередь), `ip daddr {резолвленные IP}`, qnum 59780; комментарий: «works only in OUTPUT, not in FORWARD» — проверяет только трафик самого роутера.
- Перебор (`#L1337-L1552`): multisplit/multidisorder по позициям, fake × TTL 1..12 (`MAX_TTL`), fooling (badsum, badseq, md5sig, datanoack…), fakedsplit/hostfakesplit, seqovl, autottl, syndata, wssize; `quick` останавливается на первом успехе. Длительность не измерена: каждый тест ≤ `CURL_MAX_TIME`×`REPEATS`, в standard — сотни тестов на домен.
- Чего нет относительно §4.3 спеки: полного чтения тела и порога 20 КБ (HTTPS вообще HEAD); групп целей и `required`; «успех если одна из двух попыток» (у blockcheck при REPEATS>1 все попытки должны пройти, `#L1054-L1062`); многопрофильных стратегий (quic+discord+tcp разом) — тестируется один профиль nfqws; параллельного перебора разных стратегий; проверки googlevideo-редиректа; памяти по сетям. Есть у blockcheck и нет у нас: проверка подмены DNS через mdig/DoH, детект блокировки по IP, перебор TTL/autottl, тест на «сервер получил фейк» (400).

## 4. curl на OpenWRT

- openwrt/packages 25.12 `net/curl/Makefile`: curl 8.22.0; `Config.in`: SSL по умолчанию `LIBCURL_MBEDTLS`, варианты wolfSSL/OpenSSL/GnuTLS; `LIBCURL_HTTP2` default y; `LIBCURL_HTTP3` default n и `depends on LIBCURL_OPENSSL` (nghttp3+ngtcp2). Вывод: штатный пакет `curl` из репозитория — без HTTP/3; `--http3` даст «option unknown», blockcheck в этом случае отключает QUIC-тесты (`#L628-L632`, `#L2006-L2018`).
- TLS 1.3: openwrt-25.12 (и 24.10) `package/libs/mbedtls` — mbedtls 3.6.7, `MBEDTLS_SSL_PROTO_TLS1_3 default y` → TLS 1.3 в libcurl есть. 23.05 (mbedtls 2.28/wolfssl) — TLS 1.3 не гарантирован, не проверено. blockcheck проверяет в рантайме: `curl_supports_tls13` (пробный запрос к iana.org с `--tlsv1.3`), `curl_supports_tlsmax` (`#L599-L618`).
- `--tlsv1.3 --tls-max 1.3`, `-w '%{size_download} %{http_code} %{redirect_url}'`, `--max-time`, `--connect-to`, `--local-port` — общие опции curl, не зависят от TLS-бэкенда; на роутере не проверялись. Для чтения тела вместо HEAD — `-o /dev/null` без `-I` (как `CURL_HTTPS_GET=1` у blockcheck). Фингерпринт curl ≠ браузер (`quick_start.md`, п.6: kyber/многопакетный ClientHello) — результат подбора с роутера может отличаться от Windows-проверки через .NET.

## 5. Перенос каталога: winws → nfqws

Справка nfqws v72.13: `R/nfq/nfqws.c` функция `exithelp` (126 опций). Windows-only под `#ifdef __CYGWIN__`: `--wf-iface --wf-l3 --wf-tcp --wf-udp --wf-raw --wf-raw-part --wf-filter-lan --wf-save --ssid-filter --nlm-filter --nlm-list`. Linux-only: `--qnum --user --uid --bind-fix4/6 --dpi-desync-fwmark --filter-ssid`.

| Параметр каталога | В nfqws | Отличие |
|---|---|---|
| `--dpi-desync` (fake, multisplit, multidisorder, fakedsplit, hostfakesplit, syndata) | да | нет |
| `--dpi-desync-repeats`, `--dpi-desync-fooling` (ts, badseq, md5sig) | да | режимы: none md5sig badseq badsum datanoack ts hopbyhop hopbyhop2 |
| `--dpi-desync-split-pos` (`1`, `2,sniext+1`, `1,midsld`), `--dpi-desync-split-seqovl`, `--dpi-desync-split-seqovl-pattern` | да | нет |
| `--dpi-desync-fake-tls=@файл`, `=0xHEX`, `=!`/`!+N` | да | нет. В .bat Flowseal встречается `^!` (экранирование cmd); в `data/strategies.json` было `^!`, исправлено в 4401f0b |
| `--dpi-desync-fake-tls-mod` (none, rnd, dupsid, sni=) | да | нет |
| `--dpi-desync-fake-http`, `-fake-quic`, `-fake-discord`, `-fake-stun`, `-fake-unknown-udp`, `-fake-syndata` | да | нет |
| `--dpi-desync-fakedsplit-pattern=0x00`, `--dpi-desync-fakedsplit-mod` | да | нет |
| `--dpi-desync-hostfakesplit-mod=host=..,altorder=1`, `-midhost` | да | нет |
| `--dpi-desync-badseq-increment`, `-badack-increment`, `-autottl`, `-ttl`, `-cutoff`, `-start`, `-any-protocol`, `-fake-tcp-mod` | да | для IPv6 отдельно `--dpi-desync-ttl6/-autottl6` |
| `--ip-id=zero` | да | общая опция (вне ifdef) |
| `--dup`, `--dup-cutoff`, `--dup-fooling`, `--dup-autottl`, `--dup-ttl` | да | нет |
| `--filter-tcp`, `--filter-udp`, `--filter-l7=discord,stun`, `--new` | да | l7: http tls quic wireguard dht discord stun unknown |
| `--hostlist`, `--hostlist-exclude`, `--hostlist-domains`, `--ipset`, `--ipset-exclude` | да | в-процессе; ядро эффективнее (`install_easy.sh#L142-L143`), но для нашего `ipset-all.txt` — `--ipset` в nfqws приемлем (см. §7) |
| `--wf-tcp=80,443,2053,2083,2087,2096,8443`, `--wf-udp=443,19294-19344,50000-50100` | нет | → `NFQWS_PORTS_TCP`/`NFQWS_PORTS_UDP` в config (nft `{80,443,2053-2096}`; для iptables `-` → `:`) + `NFQWS_*_PKT_OUT` (connbytes) |
| `{fake}/*.bin` | да | абсолютный путь; из каталога в zapret есть только `quic_initial_www_google_com.bin`, `tls_clienthello_www_google_com.bin`, `stun.bin`; `ACTIVE_DISCORD_UDP.bin`, `tls_clienthello_max_ru.bin`, `tls_clienthello_4pda_to.bin`, `stun2.bin`, `tls_clienthello_sochi_park.bin` — свои, везти с собой; читаемость под `WS_USER=daemon` |

Вывод: каталог переносится на уровне ролей как есть (все десинк-параметры есть в nfqws v72.13, один и тот же `nfqws.c`). В сборщике меняется только обвязка: вместо `--wf-*` — порты в config и `--qnum/--user/--dpi-desync-fwmark` из init-скрипта; профили ролей с `--filter-tcp/udp --hostlist ... --new` вставляются в `NFQWS_OPT` без изменений (`MODE_FILTER=none`, плейсхолдеры `<HOSTLIST>` не используем). Проверка `--dpi-desync-cutoff`: в Linux её роль частично берёт connbytes (`config.default#L83-L84`).

## 6. Несколько nfqws и параллельный подбор

- Несколько экземпляров штатно: каждый со своим `--qnum`, пул 65400–65499 (`custom.sh alloc_qnum`); blockcheck сам работает на qnum 59780 в своей таблице параллельно с рабочим zapret (`#L27`, `#L201-L213` проверка доступности очереди).
- Разнести слоты: curl `--local-port P_k-P_k+99` + правило `tcp sport P_k-P_k+99 ... queue num Q_k` в postrouting и `tcp dport P_k-P_k+99 ct reply packets 1-3` в prerouting для входящих; альтернатива — `meta skuid` (запуск curl под отдельным uid на слот) или `meta mark` (curl ставить марку нечем, нужен `SO_MARK` — не подходит). nft `tcp sport`/`meta skuid` — одна строка, против WinDivert-фильтра из спеки §4.5 проще. Не проверено на железе.
- Оценка: реалистично для 2–4 слотов на одном роутере; CPU nfqws в простое мал, обрабатываются только первые пакеты. Риск тот же, что у blockcheck `PARALLEL` (`#L2036`): параллельные запросы к одному домену могут вызвать защиту от DDoS и ложный результат. Подбор с роутера — всегда через OUTPUT; транзитный (клиентский) путь в автоподборе не тестируется.

## 7. Нюансы

- IPv6: по умолчанию `DISABLE_IPV6=1` (`config.default#L136`); при включении — отдельные наборы `wanif6`, `ttl6/autottl6`, правила на `ip6 daddr`.
- Flow offloading: см. §2; `FLOWOFFLOAD=donttouch` + nfqws → установщик выключает системный offload; выборочный `software|hardware` через zapret работает, hardware только nftables и не на всех портах.
- DNS-подмена: решение вне zapret (`quick_start.md` п.5): публичные DNS, DoH/DoT, или `77.88.8.88#1253` в `/etc/config/dhcp` (`list server`), `peerdns 0` у wan. blockcheck определяет подмену (сравнение с 8.8.8.8/1.1.1.1/77.88.8.1 и DoH) и уходит на DoH; для клиентов это не решает.
- Память: `readme.md#L1690-L1694` — большие hostlist в nfqws/tpws на роутере могут вызвать OOM (чисел нет); `docs/nftables_notes.txt` — nft set на 100 000 ip-записей требует ~300 МБ при загрузке → `MODE_FILTER=ipset` с большими списками на роутере не вариант; наш `ipset-all.txt` лучше подавать через `--ipset` в nfqws (размер оценить).
- `--hostlist-auto` (`MODE_FILTER=autohostlist`): нужны PKT_IN-правила и `sysctl net.netfilter.nf_conntrack_tcp_be_liberal=1` (`linux_fw.sh#L26`, `readme.md#L1710-L1716`), файл должен быть записываем под `WS_USER`. Для DpiShell не нужен: списки свои.
- `FILTER_TTL_EXPIRED_ICMP=1`: дроп ICMP time-exceeded для помеченных сессий (мешает tcp/udp-traceroute на роутере).
- Keep-alive HTTP (stateless DPI): `NFQWS_PORTS_TCP_KEEPALIVE` без connbytes — дорого по CPU (`config.default#L87-L92`).

## 8. zapret2 (nfqws2, Lua) на OpenWRT

- Релиз v1.0.5.2 от 2026-09-15, ассеты: `zapret2-v1.0.5.2-openwrt-embedded.tar.gz`, `.tar.gz`, `.zip`, `sha256sum.txt` → сборки под роутеры есть; README: «aimed primarily at low-power embedded devices — routers running OpenWrt»; стратегии в Lua (`--lua-desync`), есть `blockcheck2.sh`. Сторонние пакеты: 1andrevich/zapret2-openwrt, luci-app-zapret2.
- Для DpiShell: синтаксис стратегий другой (Lua-функции вместо `--dpi-desync=...`), каталог не переносится без адаптера `IEngine` (02-spec §4.2). Совместимость опций nfqws2 с каталогом не проверялась. В v1 — nfqws1 v72.13, тот же, что и winws.

## Итог
1. zapret v72.13 ставится на OpenWRT штатно: `openwrt-embedded.tar.gz`, `install_bin.sh`/`install_prereq.sh`/`install_easy.sh`, opkg и apk оба поддерживаются; пакеты `curl nftables kmod-nft-nat kmod-nft-offload kmod-nft-queue`; официального пакета нет.
2. Служба — procd через `/opt/zapret/init.d/openwrt/zapret`, конфиг `/opt/zapret/config` (`NFQWS_OPT`, `NFQWS_PORTS_*`, `MODE_FILTER`, `FLOWOFFLOAD`), своя таблица nft `inet zapret`, расширения через `custom.d`.
3. Перехват: `oifname @wanif tcp dport {..} ct original packets 1-9 queue num 200 bypass` в postrouting после NAT — ловит и клиентов, и сам роутер одинаково; curl на роутере проходит те же профили.
4. blockcheck умеет batch-режим (`BATCH=1 DOMAINS=.. SCANLEVEL=quick`), вывод парсится; но HTTPS проверяет HEAD без тела, без групп и порога 20 КБ — наш критерий надо писать свой (curl `-o /dev/null -w`).
5. curl 8.22 из репозитория 25.12: TLS 1.3 есть (mbedtls 3.6), HTTP/3 нет (нужна сборка с OpenSSL) — QUIC-цель на роутере «не проверялась».
6. Каталог переносится как есть: все десинк-опции есть в nfqws (один исходник); меняется только обвязка: `--wf-*` → `NFQWS_PORTS_TCP/UDP`; 5 fake-файлов Flowseal везти с собой.
7. Параллельный подбор реалистичен: несколько nfqws с разными `--qnum` + `tcp sport` диапазон (`curl --local-port`), проще WinDivert; 2–4 слота.
8. Грабли: системный flow offload выключать (установщик делает), badsum/datanoack ведут себя иначе с роутера и через него, большие списки — через `--ipset` nfqws, а не nft set; IPv6 по умолчанию выключен; zapret2 — отдельный движок, не для v1.
