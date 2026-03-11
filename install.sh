#!/bin/bash
set -e

echo "========================================="
echo "  SoftCenter 一键安装/平滑升级脚本"
echo "========================================="

# 设置你的仓库名
REPO="fw867/unifi-softcenterstore"
INSTALL_DIR="/data/softcenter"
TMP_DIR="/tmp/softcenter_update"

echo "[1/4] 正在连接 GitHub 获取最新版本..."
LATEST_RELEASE=$(curl -s https://api.github.com/repos/$REPO/releases/latest)
ZIP_URL=$(echo "$LATEST_RELEASE" | grep "browser_download_url.*zip" | cut -d '"' -f 4)
VERSION=$(echo "$LATEST_RELEASE" | grep "tag_name" | cut -d '"' -f 4)

if [ -z "$ZIP_URL" ]; then
    echo "无法获取下载链接，请检查网络或 GitHub 发布页面。"
    exit 1
fi

echo "发现最新版本: $VERSION"
echo "[2/4] 正在下载更新包..."
mkdir -p $TMP_DIR
curl -L -o $TMP_DIR/update.zip "$ZIP_URL"

echo "[3/4] 暂停当前服务，解除文件占用..."
systemctl stop softcenter.service 2>/dev/null || true
sleep 1 

echo "正在覆盖核心文件..."
mkdir -p $INSTALL_DIR
unzip -o $TMP_DIR/update.zip -d $INSTALL_DIR/ > /dev/null

echo "[4/4] 赋予权限并启动系统守护进程..."
chmod +x $INSTALL_DIR/SoftCenterManager
rm -rf $TMP_DIR

if [ ! -f "/etc/systemd/system/softcenter.service" ]; then
    echo "    首次运行，执行底层注册..."
    $INSTALL_DIR/SoftCenterManager &
else
    systemctl daemon-reload
    systemctl start softcenter.service
fi

echo "更新完毕！请刷新浏览器。"