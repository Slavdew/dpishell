# DpiShell

Простая оболочка над [zapret](https://github.com/bol-van/zapret) (bol-van) для оптимизации соединения с YouTube, Discord и другими сайтами. Нажали «Включить» — в большинстве сетей этого достаточно; если сайты не открылись — «Подобрать»: программа сама проверяет стратегии и включает лучшую. Без VPN, без телеметрии; всё заимствованное закреплено хешами и проверено.

Два самостоятельных продукта в одном репозитории, с независимыми лентами релизов:

| | Что | Документация | Релизы |
|---|---|---|---|
| **Windows-приложение** | окно без прав администратора + служба-помощник; подбор стратегий, IP-списки, игровой режим, «Починить адреса», DNS в один клик, управление роутером | [docs/windows/README.md](docs/windows/README.md) | `windows-vX.Y.Z` (portable-архив) |
| **Пакет для роутера с OpenWrt** | `dpishell` + `luci-app-dpishell`: оптимизация для всей сети, подбор с самого роутера, страница `/dpi`, DNS для всей сети, API для приложения | [docs/router/README.md](docs/router/README.md) | `router-vX.Y.Z` (пакеты и офлайн-набор) |

Они видят друг друга по [договору](docs/dev/router-api.md); каждый работает и сам по себе. Установка самой OpenWrt — под ответственность пользователя.

## Скачать (бета)

1. Откройте [страницу последнего выпуска](https://github.com/Slavdew/dpishell/releases/latest) и скачайте `DpiShell-<версия>-portable.zip`.
2. Распакуйте в любую папку и запустите `DpiShell.exe`. Программа не подписана — Windows SmartScreen может предупредить: «Подробнее» → «Выполнить в любом случае». Если такой кнопки нет — запуск, возможно, ограничивает Smart App Control или политика организации.
3. Нажмите «Включить». Подробно — [docs/windows/README.md](docs/windows/README.md).

Это бета: если что-то не работает, [напишите](https://github.com/Slavdew/dpishell/issues), что видели (лучше со снимком экрана). Снимки экрана программы появятся в следующих версиях.

## Разработчикам

- [Устройство обоих продуктов](docs/dev/architecture.md) — данные, подбор, безопасность, что планируется.
- [Сборка, тесты, публикация](docs/dev/building.md).
- [Договор с роутером](docs/dev/router-api.md), [протокол окно ↔ помощник](docs/dev/helper-protocol.md).
- [Проверка заимствованного](docs/vetting.md) — источники, хеши, лицензии.
- Исследования: [какие бывают движки оптимизации соединения (обход DPI) под Windows и почему zapret v1](docs/research/bypass-engines.md), [DpiShell на роутере](docs/research/router-openwrt.md) и [заметки по nfqws на OpenWrt](docs/research/router-nfqws.md), [функции Flowseal: игровой режим, IP-списки, hosts](docs/research/flowseal-features.md), [DNS и подмена ответов](docs/research/dns.md).
- [Тексты кнопок «(i)»](docs/info-texts.md) — часть продукта, их читает тест.

Папки: `src/` (C#/.NET 10, WPF), `tests/`, `data/` (общие каталог стратегий, цели, списки, манифесты), `packaging/`, `router/`, `vendor/` (резервная копия архива ядра).

## Лицензия

DpiShell — [MIT](LICENSE). Сторонние компоненты и их лицензии (zapret — MIT, Flowseal — MIT, WinDivert — LGPLv3/GPLv2, cygwin — LGPLv3, .NET — MIT) — `packaging/THIRD-PARTY-NOTICES.txt`.
