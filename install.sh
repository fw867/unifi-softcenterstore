#!/bin/bash
set -e

echo "========================================="
echo "  SoftCenter 一键安装/平滑升级脚本"
echo "========================================="

# 设置你的仓库名
REPO="fw867/unifi-softcenterstore"

# 接收从 C# 后端传来的代理参数，如果没有则使用默认值
PROXY=${1:-"https://cdn.gh-proxy.org/"}
echo "🌐 当前使用加速通道: $PROXY"

# 定义核心路径
BIN_DIR="/data/bin"
DATA_DIR="/data/softcenter"
TMP_DIR="/tmp/softcenter_update"

echo "[1/4] 正在连接 GitHub 获取最新版本..."
LATEST_RELEASE=$(curl -s https://api.github.com/repos/$REPO/releases/latest)
ZIP_URL=$(echo "$LATEST_RELEASE" | grep "browser_download_url.*zip" | cut -d '"' -f 4)
VERSION=$(echo "$LATEST_RELEASE" | grep "tag_name" | cut -d '"' -f 4)

if [ -z "$ZIP_URL" ]; then
    echo "❌ 无法获取下载链接，请检查网络或 GitHub 发布页面。"
    exit 1
fi

# 🌟 拼接加速代理的下载链接
FAST_DOWNLOAD_URL="${PROXY}${ZIP_URL}"

echo "🚀 发现最新版本: $VERSION"
echo "[2/4] 正在下载更新包..."
# 清理可能存在的旧缓存并创建解压目录
rm -rf $TMP_DIR
mkdir -p $TMP_DIR/extracted
curl -L -o $TMP_DIR/update.zip "$FAST_DOWNLOAD_URL"

echo "[3/4] 暂停当前服务，解除文件占用..."
systemctl stop softcenter.service 2>/dev/null || true
sleep 1 

echo "正在分发核心文件与前端资源..."
# 先解压到临时隔离目录
unzip -o $TMP_DIR/update.zip -d $TMP_DIR/extracted/ > /dev/null

# 确保目标基础目录存在
mkdir -p $BIN_DIR
mkdir -p $DATA_DIR

# 1. 分发二进制文件和 SQLite 运行库到 /data/bin
cp -f $TMP_DIR/extracted/SoftCenterManager $BIN_DIR/
cp -f $TMP_DIR/extracted/libe_sqlite3.so $BIN_DIR/

# 2. 分发前端静态资源到 /data/softcenter (全量覆盖)
cp -rf $TMP_DIR/extracted/web $DATA_DIR/

echo "[4/4] 赋予权限并启动系统守护进程..."
chmod +x $BIN_DIR/SoftCenterManager

# 清理临时文件
rm -rf $TMP_DIR

# 智能启动判定
if [ ! -f "/etc/systemd/system/softcenter.service" ]; then
    echo "首次运行，执行底层注册..."
    # 首次运行指向 /data/bin 目录下的新位置
    $BIN_DIR/SoftCenterManager &
else
    systemctl daemon-reload
    systemctl start softcenter.service
fi

echo "✅ 更新完毕！请刷新浏览器。"