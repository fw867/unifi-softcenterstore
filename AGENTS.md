# unifi-softcenterstore 工作约定

> 项目：UniFi SoftCenter 面板（ASP.NET Core + 单文件 Vue）与云端插件库（`apps/apps.json`）。

## 推送

- **不要自行推送到 GitHub**，等用户明确指令（详见全局 `~/.dsh/AGENTS.md`）。本地 commit 可以照常做，做完说明「等你指令再推送」。

## 面板发版

- 面板后端编译/发版由 `.github/workflows/publish.yml` 触发：推送到 master 时，提交信息里带 **`[release]`** 才会真正编译发版（`[publish]` 已废弃）；打 `v*` tag 或手动 `workflow_dispatch` 始终执行。
- 只改插件（`apps/**`）不需要发版。
- 面板设置里的「全局更新代理」同时供面板自升级（走 curl，支持 `socks5h://`）与插件下载（走 .NET，`socks5h://` 会被翻译成 `socks5://`）。新增任何服务端下载 GitHub 资源的代码，都要用 `CreateGitHubClient()`。

## 插件清单 `apps/apps.json`

- 改了某插件的运行文件，就把该插件 `Version` **+0.0.1**；同一批未提交的改动只加一次（不是每轮都加）。
- `Files[].Sha256` 必须是**入库字节**（git 归一化后）的 SHA256：
  `git hash-object -w --path <路径> <路径>` → `git cat-file blob <sha1>` → 对取出的内容算 SHA256。
  工作区可能是 CRLF、仓库是 LF，直接对本地字节算会算错（历史踩过）。
- 新增/改名的插件文件要在 `.gitattributes` 里声明：脚本 `text eol=lf`，ELF/二进制 `binary`。
- 若清单里声明的文件在仓库里不存在（漏 `git add`），面板安装会 404；任何一个文件哈希对不上，安装会「中止，不替换任何文件」——发布前用归一化哈希逐个核对一遍。

## 插件脚本

- 运行在路由器的 `/bin/sh`（busybox ash）：只能用 POSIX 语法（不要 `[[ ]]`、`${v//}`、数组），提交前跑 `sh -n`，并用 LF 保存。
- `<id>-status` 脚本（会变成卡片上的「运行状态」按钮）只有在**确实有诊断价值**时才加；判断不了就先不加。若删掉，记得同步去掉 `Files` 里的声明。
- 面板后端 `{id}/status_json` 只接受以 `{` 开头的 stdout，脚本必须只输出 JSON，stderr 保持干净。

## 版本与验证习惯

- 交付前用真实环境验证：能在 Linux 侧跑的就跑（WSL + `dash` + 真实进程/socket），面板改动至少 `dotnet build` 0 警告 0 错误，前端改动跑 `node --check`。
