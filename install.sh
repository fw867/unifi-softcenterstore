#!/bin/bash
set -e

echo "════════════════════════════════════════"
echo "  SoftCenter 一键安装升级"
echo "════════════════════════════════════════"

# 代理：位置参数优先，其次环境变量 PROXY / UPDATE_PROXY
PROXY="${1:-${PROXY:-${UPDATE_PROXY:-}}}"

# 代理规范化：与 apps/ 下各下载脚本保持一致（见提交 672e244）
#   socks5:// → socks5h://、socks4:// → socks4a://：让域名交给代理侧解析，
#   否则被污染的域名（GitHub 等）在本地解析就会失败；
#   没写协议头时按 HTTP 处理并提示（若那其实是 SOCKS5 端口，会被当成 HTTP 代理而连不上）。
normalize_proxy() {
    case "$1" in
        socks5://*) printf 'socks5h://%s' "${1#socks5://}" ;;
        socks4://*) printf 'socks4a://%s' "${1#socks4://}" ;;
        *://*)      printf '%s' "$1" ;;
        *)
            printf '⚠ 代理没写协议头，按 HTTP 代理处理：%s（若这是 SOCKS5 端口，请写成 socks5h://%s）\n' "$1" "$1" >&2
            printf 'http://%s' "$1"
            ;;
    esac
}

if [ -n "$PROXY" ]; then
    PROXY="$(normalize_proxy "$PROXY")"
    echo "🌐 使用代理: $PROXY"
    # 大小写都导出：curl/wget 读小写，Go 写的程序（如 gh）只读大写
    export http_proxy="$PROXY"   HTTP_PROXY="$PROXY"
    export https_proxy="$PROXY"  HTTPS_PROXY="$PROXY"
    export all_proxy="$PROXY"    ALL_PROXY="$PROXY"
    # 本机与局域网地址不走代理，否则本地接口/局域网请求可能被送去代理而失败
    export no_proxy="localhost,127.0.0.1,::1,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12"
    export NO_PROXY="$no_proxy"
    echo "    提示：这是本次安装使用的代理。要让软件中心自己下载/升级插件时也走代理，"
    echo "          请在网页端「系统设置 → 全局更新代理」里填同一个地址（推荐 socks5h://）。"
fi

REPO="fw867/unifi-softcenterstore"
BIN_DIR="/data/softcenter/bin"
DATA_DIR="/data/softcenter"
TMP_DIR="/tmp/softcenter_update"

human_size() {
    awk -v b="${1:-0}" 'BEGIN{
        split("B KB MB GB", u, " ")
        i = 1
        while (b >= 1024 && i < 4) { b /= 1024; i++ }
        if (i == 1) printf "%d %s", b, u[i]
        else printf "%.1f %s", b, u[i]
    }'
}

# 后台 curl 下载，按约 10% 进度换行输出，避免 \r 进度条刷乱日志
download_with_progress() {
    local url="$1"
    local out="$2"
    local err="$TMP_DIR/curl.err"
    local pid last=0 p got

    : > "$err"
    curl -fL --connect-timeout 15 --max-time 300 -o "$out" "$url" 2>"$err" &
    pid=$!

    while kill -0 "$pid" 2>/dev/null; do
        sleep 1
        p=$(tr '\r' '\n' < "$err" 2>/dev/null | sed -n 's/.*[[:space:]]\([0-9][0-9]*\)[[:space:]]*%.*/\1/p' | tail -n1)
        p=${p:-0}
        if [ "$p" -ge $((last + 10)) ] || [ "$p" -ge 100 ]; then
            got=$(wc -c < "$out" 2>/dev/null || echo 0)
            echo "    ▸ 进度 ${p}%  已下载 $(human_size "$got")"
            last=$p
        fi
    done

    wait "$pid"
}

echo ""
echo "▸ 步骤 [1/4]  获取最新 Release 信息"
API_URL="https://api.github.com/repos/$REPO/releases/latest"
if ! LATEST_RELEASE=$(curl -fsSL --connect-timeout 15 --max-time 60 "$API_URL"); then
    echo "❌ 请求 GitHub API 失败，请检查网络或代理。"
    echo "   接口: $API_URL"
    exit 1
fi

if [ -z "$LATEST_RELEASE" ] || ! printf '%s' "$LATEST_RELEASE" | grep -q '"tag_name"'; then
    echo "❌ GitHub API 返回异常（可能触发限流），响应片段："
    printf '%s\n' "$LATEST_RELEASE" | head -c 400
    echo ""
    exit 1
fi

TAG_NAME=$(printf '%s' "$LATEST_RELEASE" | grep -oE '"tag_name"[[:space:]]*:[[:space:]]*"[^"]+"' | head -n1 | grep -oE 'v?[0-9][^"]*')

# 直接抽取 URL 与 sha256，避免按 {} 切分 JSON 时 digest 与下载地址被拆到不同块
ZIP_URL=$(printf '%s' "$LATEST_RELEASE" | grep -oE 'https://[^"]+SoftCenter-[^"]+\.zip' | grep -v '\.dgst' | head -n1)
ZIP_DIGEST=$(printf '%s' "$LATEST_RELEASE" | grep -oE 'sha256:[a-fA-F0-9]{64}' | head -n1 | sed 's/^sha256://')

# 兜底：任意 zip 资源
if [ -z "$ZIP_URL" ]; then
    ZIP_URL=$(printf '%s' "$LATEST_RELEASE" | grep -oE 'https://[^"]+\.zip' | grep -v '\.dgst' | head -n1)
fi

echo "    版本:     ${TAG_NAME:-unknown}"

if [ -z "$ZIP_URL" ]; then
    echo "❌ 无法从 Release 中解析下载链接。"
    echo "   已识别 tag: ${TAG_NAME:-无}"
    echo "   资源列表片段："
    printf '%s' "$LATEST_RELEASE" | grep -oE '"name"[[:space:]]*:[[:space:]]*"[^"]+"|https://[^"]+\.zip' | head -n 12
    exit 1
fi

ZIP_NAME=$(basename "$ZIP_URL")
echo "    包名:     $ZIP_NAME"
echo "    下载地址: $ZIP_URL"

if [ -z "$ZIP_DIGEST" ]; then
    echo "❌ Release 资源缺少 SHA256 digest，为安全起见中止升级。"
    echo "   下载地址: $ZIP_URL"
    exit 1
fi
echo "    期望校验: $ZIP_DIGEST"

echo ""
echo "▸ 步骤 [2/4]  下载更新包并校验"
rm -rf "$TMP_DIR"
mkdir -p "$TMP_DIR/extracted"

if ! download_with_progress "$ZIP_URL" "$TMP_DIR/update.zip"; then
    echo "❌ 更新包下载失败。"
    rm -rf "$TMP_DIR"
    exit 1
fi

ZIP_SIZE=$(wc -c < "$TMP_DIR/update.zip" | tr -d ' ')
echo "    下载完成: $(human_size "$ZIP_SIZE")"

echo "    正在比对 SHA256..."
LOCAL_HASH=$(sha256sum "$TMP_DIR/update.zip" | awk '{print $1}')
if [ "$LOCAL_HASH" != "$ZIP_DIGEST" ]; then
    echo "❌ SHA256 校验失败，已中止，不会覆盖本地文件。"
    echo "   期望: $ZIP_DIGEST"
    echo "   实际: $LOCAL_HASH"
    rm -rf "$TMP_DIR"
    exit 1
fi
echo "✅ 校验通过  $LOCAL_HASH"

echo "    停止服务并解压..."
systemctl stop softcenter.service 2>/dev/null || true
unzip -o "$TMP_DIR/update.zip" -d "$TMP_DIR/extracted/" > /dev/null
mkdir -p "$BIN_DIR" "$DATA_DIR"

cp -f "$TMP_DIR/extracted/SoftCenterManager" "$BIN_DIR/"
cp -f "$TMP_DIR/extracted/libe_sqlite3.so" "$BIN_DIR/"
cp -rf "$TMP_DIR/extracted/web" "$DATA_DIR/"
chmod +x "$BIN_DIR/SoftCenterManager"
echo "    已覆盖 SoftCenterManager / libe_sqlite3.so / web/"

mkdir -p /data/softcenter/on_boot.d
if [ ! -f "/etc/systemd/system/softcenter.service" ]; then
    echo "    写入固件升级防丢钩子..."
    mkdir -p /data/on_boot.d
    cat << 'EOF' > /data/on_boot.d/99-softcenter.sh
#!/bin/bash
# 固件升级防丢钩子: 如果 Systemd 配置被刷除，则自动由 UDM-Boot 重新拉起注册
if [ ! -f "/etc/systemd/system/softcenter.service" ]; then
    /data/softcenter/bin/SoftCenterManager &
fi
EOF
    chmod +x /data/on_boot.d/99-softcenter.sh
fi

echo ""
echo "▸ 步骤 [3/4]  注册系统级守护进程"
if [ ! -f "/etc/systemd/system/softcenter.service" ]; then
    echo "    首次运行，执行底层自注册..."
    "$BIN_DIR/SoftCenterManager" &
else
    systemctl daemon-reload
    systemctl start softcenter.service
    echo "    softcenter.service 已启动"
fi

rm -rf "$TMP_DIR"
echo ""
echo "▸ 步骤 [4/4]  ✅ 更新完毕！请刷新浏览器！"
