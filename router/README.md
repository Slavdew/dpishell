# Пакет DpiShell для роутера (OpenWrt) — исходники

Для пользователей: установка, страница `/dpi`, удаление, частые проблемы — [docs/router/README.md](../docs/router/README.md).
Для разработчиков: устройство — [docs/dev/architecture.md](../docs/dev/architecture.md) («Пакет для роутера»), договор с Windows-приложением — [docs/dev/router-api.md](../docs/dev/router-api.md), сборка, тесты и эмулятор — [docs/dev/building.md](../docs/dev/building.md).

## Раскладка
- `dpishell/files/` — файлы пакета как на роутере: модули ucode (`usr/share/ucode/dpishell/`), ubus-объект (`usr/share/rpcd/ucode/dpishell`), ACL, исполнитель фоновых заданий (`usr/libexec/dpishell/run`), init-скрипт, uci по умолчанию, `roles.json`, `core.json`, `dns-presets.json`. Данные (`strategies.json`, `targets.json`, списки, фейки) берутся из `data/` при сборке.
- `luci-app-dpishell/` — страницы LuCI.
- `test/` — тесты ucode; `test/golden.json` и `test/golden-search.json` — эталоны сборки config и генератора глубокого поиска из Windows-версии (генератор — `tools/golden/`).
- `test/vm-lifecycle.sh` — установка, обновление с прежней версии, откат каждой функции и удаление в эмуляторе со сверкой с исходным состоянием роутера и снимками страниц LuCI (PASS/FAIL по пунктам); пакеты для него собирает `tools/vm-mkapk.py`, снимки и ошибки JS — `tools/luci-shot.mjs`.
- `tools/` — `vm-deploy.sh`, `vm-deploy-luci.sh`, `vm-test.sh` (эмулятор), `check-order.py` (порядок объявлений ucode), `offline-bundle.sh`/`offline-install.sh` (офлайн-набор).
- `keys/` — публичный ключ фида.
