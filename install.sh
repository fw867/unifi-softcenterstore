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
LATEST_RELEASE=$(curl -s https://api.github.com/repos/$REPO/releases/latest)
ZIP_URL=$(echo "$LATEST_RELEASE" | grep "browser_download_url.*zip" | cut -d '"' -f 4)

if [ -z "$ZIP_URL" ]; then echo "❌ 无法获取下载链接，请检查网络或 GitHub 发布页面。"; exit 1; fi

echo "[2/4] 正在下载更新包并覆盖核心..."
rm -rf $TMP_DIR && mkdir -p $TMP_DIR/extracted
curl -L -o $TMP_DIR/update.zip "$ZIP_URL"

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