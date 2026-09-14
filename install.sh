#!/bin/bash
set -e

echo "========================================="
echo "  SoftCenter 一键安装升级部署脚本"
echo "========================================="

PROXY=$1
if [ -n "$PROXY" ]; then
    echo "🌐 当前使用代理: $PROXY"
    export http_proxy=$PROXY; export https_proxy=$PROXY; export all_proxy=$PROXY
fi

REPO="fw867/unifi-softcenterstore"
BIN_DIR="/data/softcenter/bin"
DATA_DIR="/data/softcenter"
TMP_DIR="/tmp/softcenter_update"

echo "[1/4] 正在连接 GitHub 获取最新版本..."
LATEST_RELEASE=$(curl -fsSL --connect-timeout 15 --max-time 60 "https://api.github.com/repos/$REPO/releases/latest") || {
    echo "❌ 获取 Release 信息失败，请检查网络或代理。"
    exit 1
}

# 从 Release assets 中取 SoftCenter-*-arm64.zip 的下载地址与 GitHub 提供的 sha256 digest
ASSET=$(printf '%s' "$LATEST_RELEASE" | tr '{' '\n' | grep 'browser_download_url' | grep 'SoftCenter-.*\.zip' | grep -v '\.dgst' | head -n1)
ZIP_URL=$(printf '%s' "$ASSET" | sed -n 's/.*"browser_download_url":"\([^"]*\)".*/\1/p')
ZIP_DIGEST=$(printf '%s' "$ASSET" | sed -n 's/.*"digest":"sha256:\([^"]*\)".*/\1/p')

if [ -z "$ZIP_URL" ]; then
    echo "❌ 无法获取下载链接，请检查网络或 GitHub 发布页面。"
    exit 1
fi
if [ -z "$ZIP_DIGEST" ]; then
    echo "❌ Release 资源缺少 SHA256 digest，为安全起见中止升级。"
    echo "   下载地址: $ZIP_URL"
    exit 1
fi

echo "    版本包: $ZIP_URL"
echo "    期望 SHA256: $ZIP_DIGEST"

echo "[2/4] 正在下载更新包并校验完整性..."
rm -rf $TMP_DIR && mkdir -p $TMP_DIR/extracted
if ! curl -fL --connect-timeout 15 --max-time 300 -o "$TMP_DIR/update.zip" "$ZIP_URL"; then
    echo "❌ 更新包下载失败。"
    rm -rf $TMP_DIR
    exit 1
fi

LOCAL_HASH=$(sha256sum "$TMP_DIR/update.zip" | awk '{print $1}')
if [ "$LOCAL_HASH" != "$ZIP_DIGEST" ]; then
    echo "❌ SHA256 校验失败，已中止，不会覆盖本地文件。"
    echo "   期望: $ZIP_DIGEST"
    echo "   实际: $LOCAL_HASH"
    rm -rf $TMP_DIR
    exit 1
fi
echo "✅ SHA256 校验通过: $LOCAL_HASH"

systemctl stop softcenter.service 2>/dev/null || true
unzip -o $TMP_DIR/update.zip -d $TMP_DIR/extracted/ > /dev/null
mkdir -p $BIN_DIR $DATA_DIR

cp -f $TMP_DIR/extracted/SoftCenterManager $BIN_DIR/
cp -f $TMP_DIR/extracted/libe_sqlite3.so $BIN_DIR/
cp -rf $TMP_DIR/extracted/web $DATA_DIR/
chmod +x $BIN_DIR/SoftCenterManager

# 初始化专属自启目录，并注入系统自愈钩子
mkdir -p /data/softcenter/on_boot.d
if [ ! -f "/etc/systemd/system/softcenter.service" ]; then
    echo "写入固件升级双保险防丢钩子"
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

echo "[3/4] 注册系统级守护进程..."
if [ ! -f "/etc/systemd/system/softcenter.service" ]; then
    echo "首次运行，执行底层自注册..."
    $BIN_DIR/SoftCenterManager &
else
    systemctl daemon-reload
    systemctl start softcenter.service
fi

rm -rf $TMP_DIR
echo "[4/4] ✅ 更新完毕！请刷新浏览器！"
