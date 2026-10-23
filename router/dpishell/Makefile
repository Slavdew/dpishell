#
# dpishell — обход DPI на zapret (nfqws) с автоподбором стратегий. Устройство: docs/dev/architecture.md.
# Файлы данных (каталог, цели, списки, фейки) кладёт в files/usr/share/dpishell сценарий сборки из data/ репозитория.
# Ядро (nfqws и скрипты zapret) в пакет не входит: роутер качает его по files/usr/share/dpishell/core.json.
#

include $(TOPDIR)/rules.mk

PKG_NAME:=dpishell
PKG_VERSION:=0.2.2
PKG_RELEASE:=1
PKG_LICENSE:=MIT
PKG_MAINTAINER:=DpiShell

PKG_BUILD_DIR:=$(BUILD_DIR)/$(PKG_NAME)

include $(INCLUDE_DIR)/package.mk

define Package/dpishell
  SECTION:=net
  CATEGORY:=Network
  TITLE:=DpiShell — оптимизация соединения для всей сети (zapret) с автоподбором
  DEPENDS:=+curl +unzip +nftables +kmod-nft-queue +kmod-nft-nat +ucode +ucode-mod-fs +ucode-mod-uci +rpcd +rpcd-mod-ucode
  PKGARCH:=all
endef

define Package/dpishell/description
  Ставит ядро zapret (nfqws) по закреплённому хешу, подбирает стратегию оптимизации с самого роутера,
  применяет её ко всей сети, следит за ней. Управление — страница LuCI и Windows-приложение DpiShell (ubus dpishell).
endef

define Package/dpishell/conffiles
/etc/config/dpishell
/etc/dpishell/
endef

define Build/Prepare
	mkdir -p $(PKG_BUILD_DIR)
endef

define Build/Configure
endef

define Build/Compile
endef

define Package/dpishell/install
	$(INSTALL_DIR) $(1)/usr/share/ucode/dpishell
	$(INSTALL_DATA) ./files/usr/share/ucode/dpishell/*.uc $(1)/usr/share/ucode/dpishell/
	$(INSTALL_DIR) $(1)/usr/share/rpcd/ucode $(1)/usr/share/rpcd/acl.d
	$(INSTALL_DATA) ./files/usr/share/rpcd/ucode/dpishell $(1)/usr/share/rpcd/ucode/
	$(INSTALL_DATA) ./files/usr/share/rpcd/acl.d/dpishell.json $(1)/usr/share/rpcd/acl.d/
	$(INSTALL_DIR) $(1)/usr/libexec/dpishell
	$(INSTALL_BIN) ./files/usr/libexec/dpishell/run ./files/usr/libexec/dpishell/uninstall.sh ./files/usr/libexec/dpishell/off $(1)/usr/libexec/dpishell/
	$(INSTALL_DIR) $(1)/etc/hotplug.d/button
	$(INSTALL_BIN) ./files/etc/hotplug.d/button/50-dpishell $(1)/etc/hotplug.d/button/
	$(INSTALL_DIR) $(1)/etc/init.d $(1)/etc/config
	$(INSTALL_BIN) ./files/etc/init.d/dpishell $(1)/etc/init.d/
	$(INSTALL_CONF) ./files/etc/config/dpishell $(1)/etc/config/
	$(INSTALL_DIR) $(1)/usr/share/dpishell/lists $(1)/usr/share/dpishell/fake
	$(INSTALL_DATA) ./files/usr/share/dpishell/*.json ./files/usr/share/dpishell/zapret-custom.sh \
		./files/usr/share/dpishell/release-key.pub $(1)/usr/share/dpishell/
	$(INSTALL_DATA) ./files/usr/share/dpishell/lists/*.txt $(1)/usr/share/dpishell/lists/
	$(INSTALL_DATA) ./files/usr/share/dpishell/fake/*.bin $(1)/usr/share/dpishell/fake/
	$(INSTALL_DIR) $(1)/www/dpishell
	$(INSTALL_DIR) $(1)/lib/upgrade/keep.d
	echo /etc/dpishell/ > $(1)/lib/upgrade/keep.d/dpishell
endef

define Package/dpishell/postinst
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] && exit 0
# Кеш LuCI до перезапуска rpcd: со старым кешем меню ссылается на прежние страницы и вход в панель ломается.
rm -rf /tmp/luci-indexcache* /tmp/luci-modulecache
# Перезапуск rpcd — отдельным процессом вне его cgroup и через 2 с: при установке через веб-вход apk — потомок rpcd,
# и перезапуск изнутри оставлял занятую cgroup («failed to remove busy cgroup of rpcd::instance1»), а вызов — без ответа.
sh -c 'echo $$$$ > /sys/fs/cgroup/cgroup.procs 2>/dev/null; sleep 2; /etc/init.d/rpcd restart' >/dev/null 2>&1 &
/etc/init.d/dpishell enable
/etc/init.d/dpishell start
exit 0
endef

define Package/dpishell/prerm
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] && exit 0
# Ручной apk del: та же очистка, что у кнопки «Удалить» (выключить обход, вернуть разгрузку, DNS, cron, убрать ядро
# из /opt/zapret), без удаления пакетов и с сохранением настроек /etc/dpishell.
[ -x /usr/libexec/dpishell/uninstall.sh ] && sh /usr/libexec/dpishell/uninstall.sh --keep --from-prerm
exit 0
endef

define Package/dpishell/postrm
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] && exit 0
# Файлы плагина уже удалены: без перезапуска rpcd держит объект dpishell, а LuCI — меню из кеша.
rm -rf /tmp/luci-indexcache* /tmp/luci-modulecache
sh -c 'echo $$$$ > /sys/fs/cgroup/cgroup.procs 2>/dev/null; sleep 2; /etc/init.d/rpcd restart' >/dev/null 2>&1 &
exit 0
endef

$(eval $(call BuildPackage,dpishell))
