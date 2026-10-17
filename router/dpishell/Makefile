#
# dpishell — обход DPI на zapret (nfqws) с автоподбором стратегий. ТЗ: docs/06-router-spec.md.
# Файлы данных (каталог, цели, списки, фейки) кладёт в files/usr/share/dpishell сценарий сборки из data/ репозитория.
# Ядро (nfqws и скрипты zapret) в пакет не входит: роутер качает его по files/usr/share/dpishell/core.json.
#

include $(TOPDIR)/rules.mk

PKG_NAME:=dpishell
PKG_VERSION:=0.1.0
PKG_RELEASE:=2
PKG_LICENSE:=MIT
PKG_MAINTAINER:=DpiShell

PKG_BUILD_DIR:=$(BUILD_DIR)/$(PKG_NAME)

include $(INCLUDE_DIR)/package.mk

define Package/dpishell
  SECTION:=net
  CATEGORY:=Network
  TITLE:=DpiShell — обход DPI для всей сети (zapret) с автоподбором
  DEPENDS:=+curl +unzip +nftables +kmod-nft-queue +kmod-nft-nat +ucode +ucode-mod-fs +ucode-mod-uci +rpcd +rpcd-mod-ucode
  PKGARCH:=all
endef

define Package/dpishell/description
  Ставит ядро zapret (nfqws) по закреплённому хешу, подбирает стратегию обхода с самого роутера,
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
	$(INSTALL_BIN) ./files/usr/libexec/dpishell/run $(1)/usr/libexec/dpishell/
	$(INSTALL_DIR) $(1)/etc/init.d $(1)/etc/config
	$(INSTALL_BIN) ./files/etc/init.d/dpishell $(1)/etc/init.d/
	$(INSTALL_CONF) ./files/etc/config/dpishell $(1)/etc/config/
	$(INSTALL_DIR) $(1)/usr/share/dpishell/lists $(1)/usr/share/dpishell/fake
	$(INSTALL_DATA) ./files/usr/share/dpishell/*.json $(1)/usr/share/dpishell/
	$(INSTALL_DATA) ./files/usr/share/dpishell/lists/*.txt $(1)/usr/share/dpishell/lists/
	$(INSTALL_DATA) ./files/usr/share/dpishell/fake/*.bin $(1)/usr/share/dpishell/fake/
	$(INSTALL_DIR) $(1)/www/dpishell
	$(INSTALL_DIR) $(1)/lib/upgrade/keep.d
	echo /etc/dpishell/ > $(1)/lib/upgrade/keep.d/dpishell
endef

define Package/dpishell/postinst
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] && exit 0
/etc/init.d/rpcd restart
/etc/init.d/dpishell enable
/etc/init.d/dpishell start
exit 0
endef

define Package/dpishell/prerm
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] && exit 0
/etc/init.d/dpishell stop
/etc/init.d/dpishell disable
# Без пакета обход некому вести: выключаем zapret (файлы ядра в /opt/zapret остаются).
[ -x /etc/init.d/zapret ] && { /etc/init.d/zapret stop; /etc/init.d/zapret disable; }
# Разгрузку, переданную zapret кнопкой (flow_offload_fix), возвращаем межсетевому экрану.
prev=$$(uci -q get dpishell.main.fw_offload_prev)
if [ -n "$$prev" ]; then
	uci set firewall.@defaults[0].flow_offloading=1
	[ "$$prev" = hardware ] && uci set firewall.@defaults[0].flow_offloading_hw=1
	uci commit firewall
	/etc/init.d/firewall reload
fi
exit 0
endef

$(eval $(call BuildPackage,dpishell))
