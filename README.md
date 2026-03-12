# 🚀 UniFi SoftCenter (安全管理中心)

![SoftCenter 主界面](img/main.png)

UniFi SoftCenter 是一个专为 UniFi OS (如 UCG-Fiber) 及类 Debian 路由器系统打造的**现代化、轻量级、无依赖**的第三方应用与脚本管理中心。

它采用 **.NET 10 Native AOT** 编译后端，结合 **Vue 3 + TailwindCSS** 打造了极致流畅的 Apple-Style 毛玻璃控制面板，让路由器底层折腾变得前所未有的优雅。

## ✨ 核心杀手级功能

* **🛡️ 固件升级无损自愈 (零干预)**：独创的底层防丢钩子。即使官方推送 UniFi OS 固件更新擦除了底层 Rootfs 和守护进程，重启后 SoftCenter 也能**自动复活**，并瞬间恢复所有定时任务与自启应用。完全平替并超越 UDM-Boot！
* **🍏 现代化沉浸式 UI**：带毛玻璃特效、原生拖拽排序、全局模糊搜索，告别简陋的传统路由器后台。
* **⚙️ 全局设置 UI 化**：彻底告别 SSH！直接在 Web 面板修改访问端口、安全 Token，以及配置全局下载代理（如 V2Ray 本地节点），保存后自动平滑重启生效。
* **☁️ 云端应用库与同步**：支持从 GitHub 云端一键拉取并安装适配好的软路由插件（如光猫助手、DDNSTO、微信通知）。更支持**一键同步云端最新配置**，平滑覆盖本地参数而不丢失自启状态。
* **📦 极简应用管理**：支持一键启停任意底层 Shell 脚本或二进制核心程序，并可视化配置开机自启。
* **🛠️ 动态参数热更新**：支持底层正则解析，在 Web 弹窗中直接修改应用的底层变量参数（免改文件），一键保存并平滑重启服务。
* **📜 极客级终端日志**：内置全屏“黑客瀑布流”日志查看器，支持实时拉取应用日志 (`tail`) 及系统核心底层守护日志 (`journalctl`)。
* **⏰ 彻底接管 Crontab**：在界面上直接管理 Linux 系统的定时任务，支持标准的 Cron 表达式添加与精准解析删除。
* **🔄 极客化在线 OTA 升级**：带实时终端日志输出的无感升级机制。自动通过配置的代理拉取最新版本，后端执行脱壳覆盖，双线程心跳探测自动刷新页面。
* **⚡️ 极致性能与兼容性**：基于 `.NET 10 Native AOT` 交叉编译至 `linux-arm64`，单文件运行，**0 运行库依赖**。兼容老版本 `GLIBC 2.31`，即使在老旧的底层固件上也能稳定狂飙。

---

## 🚀 一键部署

请通过 SSH 登录到你的 UniFi 路由器后台（推荐使用 `root` 权限），然后复制并执行以下命令：

**默认直连安装：**
```bash
curl -sSL https://raw.githubusercontent.com/fw867/unifi-softcenterstore/master/install.sh | bash
```

使用代理安装 (如果你在国内网络环境下载缓慢)：

代理地址可以是免费加速节点，也可以是你路由器的 V2Ray 节点。

```bash
curl -sSL https://ghp.ci/https://raw.githubusercontent.com/fw867/unifi-softcenterstore/master/install.sh | bash -s "https://ghp.ci/"
```

💡 说明：该脚本会自动从 GitHub 拉取最新版编译好的二进制包，配置 SQLite 数据库权限，注入防擦除钩子，并向系统注册 softcenter.service 底层守护进程。

## 🖥️ 访问与使用

访问地址：http://<你的路由器IP>:9958

默认令牌 (Token)：Your_Secret_Token_Here

安全建议：首次登录后，请立即点击右上角的 “系统设置” 按钮，修改默认的安全令牌 (Token) 和访问端口，也可以在其中配置全局更新代理地址（如 http://127.0.0.1:10809）。

## 🛠️ 技术栈与编译

Backend: C# 10 / ASP.NET Core (Minimal API) / Native AOT

Frontend: Vue 3 / TailwindCSS / Lucide Icons

Database: SQLite (Microsoft.Data.Sqlite 原生驱动)

CI/CD: GitHub Actions (使用 debian:11 容器进行交叉编译，彻底解决 Ubuntu Multi-Arch 依赖冲突，获取极佳的 GLIBC 2.31 兼容性)

## 📂 核心目录结构参考

```text
/data/softcenter/
├── bin/                        # 核心程序目录
│   ├── SoftCenterManager      # 核心二进制守护进程 (Native AOT)
│   └── libe_sqlite3.so        # 原生 SQLite 运行库
├── on_boot.d/                  # SoftCenter 专属应用开机自启脚本目录
├── config.json                 # 面板端口与 Token 配置文件 (可在Web端修改)
├── manager.db                  # 应用与 Cron 注册表数据库 (SQLite)
└── web/                        # 静态 Web 资源
    └── index.html             # 前端单页应用 (Vue 3)

/data/on_boot.d/
└── 99-softcenter.sh            # 系统的底层防丢钩子 (固件升级自愈核心)
```

## 🤝 贡献与反馈

如果您有更好的软路由插件建议、或是适配了新的应用配置，欢迎通过 GitHub PR 或 Issue 提交反馈，共同完善云端应用库！

项目地址: https://github.com/fw867/unifi-softcenterstore

