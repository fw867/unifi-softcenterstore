#!/bin/bash
# 校验 install.sh 的代理处理：协议头规范化（与提交 672e244 一致）、大小写导出、no_proxy
#
# 直接从 install.sh 里抽取真实的函数与导出代码来测，避免"测试和实现各写一份"。
# 用法: bash tools/test-install-proxy.sh [install.sh 路径]

set -u
SCRIPT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/install.sh}"
pass=0
fail=0

check() {                       # check <说明> <期望> <实际>
    if [ "$2" = "$3" ]; then
        printf '  ✓ %s\n' "$1"
        pass=$((pass + 1))
    else
        printf '  ✗ %s\n      期望: %s\n      实际: %s\n' "$1" "$2" "$3"
        fail=$((fail + 1))
    fi
}

[ -f "$SCRIPT" ] || { printf '找不到 install.sh：%s\n' "$SCRIPT" >&2; exit 2; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# 只取到下载部分之前的"代理处理"头部（到 REPO= 为止），这样 source 它不会有副作用
awk '/^REPO=/{exit} {print}' "$SCRIPT" > "$work/head.sh"
grep -q 'normalize_proxy' "$work/head.sh" || { printf '没有从 install.sh 里抽到 normalize_proxy\n' >&2; exit 2; }

# shellcheck disable=SC1090
source "$work/head.sh"

printf '代理协议头规范化：\n'
check 'socks5:// → socks5h://（域名交给代理解析）' \
    'socks5h://127.0.0.1:7891' "$(normalize_proxy 'socks5://127.0.0.1:7891')"
check 'socks4:// → socks4a://' \
    'socks4a://127.0.0.1:7891' "$(normalize_proxy 'socks4://127.0.0.1:7891')"
check 'socks5h:// 保持不变（已带 h）' \
    'socks5h://127.0.0.1:7891' "$(normalize_proxy 'socks5h://127.0.0.1:7891')"
check 'http:// 保持不变' \
    'http://127.0.0.1:7890' "$(normalize_proxy 'http://127.0.0.1:7890')"
check 'https:// 保持不变' \
    'https://proxy.local:8443' "$(normalize_proxy 'https://proxy.local:8443')"
check '没协议头 → 按 http:// 处理' \
    'http://127.0.0.1:7890' "$(normalize_proxy '127.0.0.1:7890')"
check '没协议头会打印提示' \
    '1' "$(normalize_proxy '127.0.0.1:7890' 2>&1 >/dev/null | grep -c 'socks5h://')"

printf '环境变量导出（位置参数优先，其次 PROXY / UPDATE_PROXY）：\n'
probe() {                       # probe <脚本片段> <要打印的变量>
    bash -c "set -u; $1; source '$work/head.sh' >/dev/null 2>&1; printf '%s' \"\${$2:-}\""
}
check '位置参数 socks5:// 规范化后大小写都导出' \
    'socks5h://1.2.3.4:1080' "$(probe 'set -- socks5://1.2.3.4:1080' http_proxy)"
check '大写 HTTP_PROXY 同样设置' \
    'socks5h://1.2.3.4:1080' "$(probe 'set -- socks5://1.2.3.4:1080' HTTP_PROXY)"
check 'HTTPS_PROXY 同样设置' \
    'socks5h://1.2.3.4:1080' "$(probe 'set -- socks5://1.2.3.4:1080' HTTPS_PROXY)"
check 'ALL_PROXY 同样设置' \
    'socks5h://1.2.3.4:1080' "$(probe 'set -- socks5://1.2.3.4:1080' ALL_PROXY)"
check 'no_proxy 覆盖本机' \
    '1' "$(probe 'set -- socks5://1.2.3.4:1080' no_proxy | grep -c '127.0.0.1')"
check 'NO_PROXY 与 no_proxy 一致' \
    'socks5h://1.2.3.4:1080' "$(probe 'set -- socks5://1.2.3.4:1080' ALL_PROXY)"
check 'PROXY 环境变量可代替位置参数' \
    'http://9.9.9.9:3128' "$(probe 'PROXY=http://9.9.9.9:3128' http_proxy)"
check 'UPDATE_PROXY 也能用' \
    'http://8.8.8.8:3128' "$(probe 'UPDATE_PROXY=http://8.8.8.8:3128' http_proxy)"
check '位置参数优先于环境变量' \
    'socks4a://1.1.1.1:1080' "$(probe 'set -- socks4://1.1.1.1:1080; PROXY=http://2.2.2.2:3128' http_proxy)"
check '不传代理时不导出（保持干净）' \
    '' "$(probe 'true' http_proxy)"

printf '\n通过 %d 项，失败 %d 项\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
