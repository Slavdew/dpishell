# DpiShell: совместная работа с ПК (router-api §5.2 pc_link_set, mixed-strategies.md §7). Кладётся DpiShell в
# /opt/zapret/init.d/openwrt/custom.d/50-dpishell; zapret сам зовёт функции при start/stop. Параметры — из config zapret:
#   DPS_PC_N=<число ПК>, DPS_PC<i>_MACS="aa:bb:..,..", DPS_PC<i>_OPT="<NFQWS_OPT + исключения ПК в каждом профиле>"
# Основной nfqws (очередь 200) с FILTER_MARK берёт всех, кроме ПК; ПК i — свой nfqws на очереди 200+i со своей меткой.
# Метка ставится в prerouting по MAC и копируется в ct mark: ответы с WAN получают её обратно (входящие пакеты nfqws).
# DPS_PC_N пусто или 0 — ничего не делается, всё как без совместной работы.

dps_pc_mark()
{
	# $1 - номер ПК с 1: 0x01000000, 0x02000000, 0x04000000, 0x08000000
	printf '0x%08x' $(( 0x01000000 << ($1 - 1) ))
}

zapret_custom_daemons()
{
	# $1 - 1 запуск, 0 остановка
	local i=1 opt
	[ -n "$DPS_PC_N" ] || return 0
	while [ $i -le "$DPS_PC_N" ]; do
		eval opt="\$DPS_PC${i}_OPT"
		do_nfqws $1 $((50 + i)) "--qnum=$((QNUM + i)) $opt"
		i=$((i + 1))
	done
}

zapret_custom_firewall_nft()
{
	local i=1 macs mark all="" q=$QNUM fm=$FILTER_MARK
	[ -n "$DPS_PC_N" ] && [ "$DPS_PC_N" -gt 0 ] || return 0
	nft add chain inet $ZAPRET_NFT_TABLE dps_pre "{ type filter hook prerouting priority -150; }"
	nft flush chain inet $ZAPRET_NFT_TABLE dps_pre
	nft add chain inet $ZAPRET_NFT_TABLE dps_out "{ type filter hook output priority -150; }"
	nft flush chain inet $ZAPRET_NFT_TABLE dps_out
	while [ $i -le "$DPS_PC_N" ]; do
		eval macs="\$DPS_PC${i}_MACS"
		mark=$(dps_pc_mark $i)
		nft add rule inet $ZAPRET_NFT_TABLE dps_pre iifname @lanif ether saddr "{ $macs }" meta mark set meta mark or $mark ct mark set ct mark or $mark
		all="${all:+$all,}$macs"
		i=$((i + 1))
	done
	# Все прочие из LAN и сам роутер (его проверки и подбор) — основному nfqws. Метку соединения им не ставим: входящие
	# основного zapret берёт без фильтра (всё, что не забрали правила ПК), а ct mark от ICMP самого роутера (ttl expired
	# на приманках с малым TTL) приписал бы чужому соединению лишний бит (проверено на стенде).
	nft add rule inet $ZAPRET_NFT_TABLE dps_pre iifname @lanif ether saddr != "{ $all }" meta mark set meta mark or $fm
	nft add rule inet $ZAPRET_NFT_TABLE dps_out meta mark set meta mark or $fm
	# Ответы с WAN — метка соединения обратно в пакет (у входящих своей метки нет; nft не складывает две переменные).
	nft add rule inet $ZAPRET_NFT_TABLE dps_pre iifname @wanif meta mark set ct mark and 0x1f000000
	nft add rule inet $ZAPRET_NFT_TABLE dps_pre iifname @wanif6 meta mark set ct mark and 0x1f000000
	# Очереди ПК: исходящие — те же правила, что у основного nfqws, со своей очередью и меткой (функции zapret, переменные
	# на время). Входящие zapret строит без фильтра по метке — первое такое правило забрало бы ответы всей сети,
	# поэтому для ПК они свои, с меткой (её вернуло правило iifname @wanif выше), и вставлены раньше основного.
	local tin=$NFQWS_TCP_PKT_IN uin=$NFQWS_UDP_PKT_IN pre=$(get_prechain)
	i=1
	while [ $i -le "$DPS_PC_N" ]; do
		mark=$(dps_pc_mark $i)
		QNUM=$((q + i)); FILTER_MARK=$mark; NFQWS_TCP_PKT_IN=0; NFQWS_UDP_PKT_IN=0
		zapret_apply_firewall_standard_nfqws_rules_nft
		[ -n "$tin" ] && [ "$tin" != 0 ] && {
			nft insert rule inet $ZAPRET_NFT_TABLE $pre iifname @wanif meta mark and $mark != 0 tcp sport "{ $NFQWS_PORTS_TCP }" ct reply packets 1-$tin ip saddr != @nozapret $CONNMARKER queue num $QNUM bypass
			[ "$DISABLE_IPV6" = 1 ] || nft insert rule inet $ZAPRET_NFT_TABLE $pre iifname @wanif6 meta mark and $mark != 0 tcp sport "{ $NFQWS_PORTS_TCP }" ct reply packets 1-$tin ip6 saddr != @nozapret6 $CONNMARKER queue num $QNUM bypass
		}
		[ -n "$uin" ] && [ "$uin" != 0 ] && {
			nft insert rule inet $ZAPRET_NFT_TABLE $pre iifname @wanif meta mark and $mark != 0 udp sport "{ $NFQWS_PORTS_UDP }" ct reply packets 1-$uin ip saddr != @nozapret $CONNMARKER queue num $QNUM bypass
		}
		i=$((i + 1))
	done
	QNUM=$q; FILTER_MARK=$fm; NFQWS_TCP_PKT_IN=$tin; NFQWS_UDP_PKT_IN=$uin
}

zapret_custom_firewall_nft_flush()
{
	nft delete chain inet $ZAPRET_NFT_TABLE dps_pre 2>/dev/null
	nft delete chain inet $ZAPRET_NFT_TABLE dps_out 2>/dev/null
	return 0
}
