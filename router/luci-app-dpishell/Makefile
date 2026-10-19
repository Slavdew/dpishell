#
# luci-app-dpishell — страница LuCI «Службы → DpiShell» для пакета dpishell.
# Обычный пакет OpenWRT (без luci.mk — собирается вне дерева feeds/luci).
#

include $(TOPDIR)/rules.mk

PKG_NAME:=luci-app-dpishell
PKG_VERSION:=0.2.0
PKG_RELEASE:=5
PKG_LICENSE:=MIT
PKG_MAINTAINER:=DpiShell

PKG_BUILD_DIR:=$(BUILD_DIR)/$(PKG_NAME)

include $(INCLUDE_DIR)/package.mk

define Package/luci-app-dpishell
  SECTION:=luci
  CATEGORY:=LuCI
  SUBMENU:=3. Applications
  TITLE:=DpiShell — оптимизация соединения (страница LuCI)
  DEPENDS:=+dpishell +luci-base
  PKGARCH:=all
endef

define Package/luci-app-dpishell/description
  Страница «Службы → DpiShell»: включение оптимизации, подбор стратегии, исключения, DNS, диагностика.
  Короткий адрес: http://192.168.1.1/dpi
endef

define Build/Prepare
	mkdir -p $(PKG_BUILD_DIR)
endef

define Build/Configure
endef

define Build/Compile
endef

define Package/luci-app-dpishell/install
	$(INSTALL_DIR) $(1)/www/luci-static/resources/dpishell
	$(INSTALL_DATA) ./htdocs/luci-static/resources/dpishell/*.js $(1)/www/luci-static/resources/dpishell/
	$(INSTALL_DIR) $(1)/www/luci-static/resources/view/dpishell
	$(INSTALL_DATA) ./htdocs/luci-static/resources/view/dpishell/*.js $(1)/www/luci-static/resources/view/dpishell/
	$(INSTALL_DIR) $(1)/usr/share/luci/menu.d
	$(INSTALL_DATA) ./root/usr/share/luci/menu.d/luci-app-dpishell.json $(1)/usr/share/luci/menu.d/
	$(INSTALL_DIR) $(1)/www/dpi
	$(INSTALL_DATA) ./root/www/dpi/index.html $(1)/www/dpi/
endef

define Package/luci-app-dpishell/postinst
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] || { rm -rf /tmp/luci-indexcache* /tmp/luci-modulecache; exit 0; }
endef

define Package/luci-app-dpishell/postrm
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] || { rm -rf /tmp/luci-indexcache* /tmp/luci-modulecache; exit 0; }
endef

$(eval $(call BuildPackage,luci-app-dpishell))
