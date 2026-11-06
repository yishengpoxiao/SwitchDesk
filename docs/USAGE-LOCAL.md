# SwitchDesk 本地构建使用说明

本说明对应当前主仓库的 0.3.0-dev.2 开发构建（含局域网、金手指、Homebrew、快照对比等扩充）。本机工具位置从忽略的 `local.paths.json` 读取。上游历史发布版说明见 [INSTALL.md](INSTALL.md)。

## 1. 快速开始

### 1.1 运行 Windows 桌面端

```powershell
# 在当前仓库根目录运行
$paths = Get-Content local.paths.json -Raw | ConvertFrom-Json
& $paths.Dotnet run --project src\SwitchDesk.App -c Release
```

构建、运行测试并发布为可双击的单文件程序：

```powershell
./scripts/build.ps1 -Dotnet $paths.Dotnet -Python $paths.Python
```

数据默认保存在 `%LOCALAPPDATA%\SwitchDesk`；测试时用 `--data-dir work/test-data` 指定独立目录。

### 1.2 部署 Switch 端 NRO

1. 备份 SD 卡上现有的 `/switch/SwitchDesk/SwitchDesk.nro`。
2. 核对开发包与对应源码的来源标识、SHA-256，解压配套包，把 `switch/SwitchDesk/SwitchDesk.nro` 复制到 `SD:/switch/SwitchDesk/`。当前本地交付目录为 `dist/`，文件名以本次清单为准。
3. 在 Switch 上以**完整应用模式**启动（不是相册 Applet）。存档、安装、导出、金手指需要完整应用模式。
4. 用支持数据传输的 USB 线连接电脑，保持配套程序打开。

回滚使用事先保留的原配套程序；不要依赖已经迁移的旧工作目录。旧 0.2.4 配套不支持标准金手指写入和管理更新计划，具体能力由连接握手确定。

### 1.3 首次连接

- USB 接口需要 WinUSB 驱动，产品字符串为 **SwitchDesk PC**（VID_057E / PID_3000）。仅对匹配接口安装驱动；安装前退出 DBI、Sphaira、SysDVR 等占用 USB 的程序。
- 连接后在「连接诊断」查看状态、任务和错误报告；没有设备时先检查完整应用模式、驱动占用和线材。

## 2. 桌面端功能

### 游戏库
- 「电脑游戏目录」添加文件夹并扫描；支持搜索、主机/电脑来源筛选、网格/列表视图。
- 「＋ 安装游戏」选择 NSP/XCI/NSZ/XCZ，或直接拖入窗口；清单会预检组件、空间与主机环境，可批量安装到 SD 卡或用户分区。
- 设置页提供：批量校验、清理缺失索引、查找重复（只读检测，不会删除文件）。

### 更新与 DLC
1. 在详情页选择已读取的主机游戏 →「更新与 DLC」→「检查更新」。
2. 目录来自公共 TitleDB（连接后自动刷新，24 小时上限），安装包需要你自己的来源：设置页「添加来源」选择本地目录或自有 HTTP 目录（需含 `index.json`），也可「从安装包生成索引」。
3. 下载后 SHA-256 校验；「安装勾选清单」会先保护该游戏所有用户存档，再按更新 → DLC → DLC 更新的顺序安装并复读版本。
4. 安装需要新版 NRO 的 `install.plan.v1` 能力；旧 NRO 上按钮会提示不可用。

### 存档
- 「存档」页按游戏/用户展示备份时间线；「立即备份」或拖到资源管理器导出 ZIP。
- 恢复前自动创建 `Recovery` 快照；保留数量在设置中调整。
- 历史版本支持校验、对比（SHA-256 清单差异）、导出副本（同秒重名自动加序号）。

### 存档同步（Switch ↔ 电脑，全部游戏）
- 「存档」页进入任意游戏后点「检查同步」：对当前系统每个存档，比较设备指纹与电脑最新快照。
- 主机更新 → 「备份到电脑」；电脑更新 → 「写回主机」（恢复前自动保护备份）；两端都变 → 标为冲突，需人工选择方向；首次同步建议先备份。
- 同步状态保存在数据目录 `SaveSync/save-sync.json`；不转换存档格式，对全部有存档的游戏通用。

### 跨系统迁移（sysMMC ↔ emuMMC）
1. 在系统 A（如 emuMMC）的存档页选好来源与版本，先备份到电脑（或直接用「检查同步 → 备份到电脑」）。
2. 重启进入系统 B（如 sysMMC），打开配套程序并连接。
3. 在存档页把来源切换到系统 A 的记录，选好版本后点「迁移到当前系统…」。
4. 要求当前系统已有同游戏、同用户、同类型的存档（先在游戏里存一次档）；迁移使用带指纹保护的原子写回，当前内容先保护备份。
5. 官方真实系统无法运行破解程序，不在此通道内。

### 跨平台存档
- Steam 存档发现与双端比较已实现；真实迁移受上游准入表限制（当前为空），未准入时按钮保持禁用是预期行为。

### SD 文件
- 浏览/上传/下载/重命名/新建目录/删除；拖入窗口直接上传。
- `/Nintendo`、`/emuMMC`、`/atmosphere`（金手指除外）、`/bootloader`、配套程序自身目录仍受保护。

### 局域网（FTP + Homebrew）
- 在 Switch 运行 **ftpd / sys-ftpd**（默认端口 **5000**），电脑与 Switch 同一局域网。
- 填写主机、端口、用户名/密码（匿名可留空）→「连接」。支持浏览、上传文件、**上传文件夹**、下载、重命名、新建/删除目录。
- 上传先写临时文件再重命名，取消/失败会尽量清理；FTP 密码只在本次会话使用，不写入配置。
- Homebrew：设置中填写社区商店的 **JSON 索引 URL**（字段示例：`{"list":[{"name":"工具","author":"dev","version":"1.0","details":"说明","dlurl":"https://.../app.nro"}]}`），刷新后选择应用安装到 `SD:/switch/<名称>/`。当前只支持直接下载 `.nro`，ZIP 等需要解压的分发包不会列入可安装条目；索引可提供 64 位十六进制 `sha256`，安装前会核对，不匹配时不向主机写入。仅面向免费开源内容，不接入商业 ROM 站点。

### 金手指
1. 选择已读取的主机游戏 → 详情页「金手指」标签。
2. 「导入文件夹」选择包含 `<16位构建ID>.txt` 或旧 `.cheat` 的文件夹；导入统一写为 Atmosphère 标准 `.txt`。
3. 列表支持刷新、启用/停用（添加或去掉 `.off` 后缀）、删除；旧 `.cheat` 文件仍可管理。
4. 标准 TXT 写入需要新版 NRO 的 `cheats.text.v1` 能力；旧 NRO 上相应按钮禁用。解析支持 BOM、`#`/`;` 注释及每行 1 至 4 个 32 位代码字。
5. [资源下载](RESOURCE-DOWNLOADS.md)支持金手指直链和索引，先下载电脑，再补齐并核对 TitleID / BuildID、确认安装。

### 画面预览（SysDVR）
- 设置 →「游戏投屏」选择 **USB** 或 **网络 Bridge**，Bridge 可填主机 IP（留空自动发现）。
- 需要 SysDVR sysmodule 与官方 SysDVR-Client 客户端；本地源码构建默认不带客户端，请在设置中指向 `SysDVR-Client.exe`（保留整个目录），或使用包含 Streaming 资源的打包产物。
- USB 模式会释放管理连接；Bridge 模式保持管理连接。停止播放器后按提示在主机侧关闭串流再恢复管理。

## 3. 跨平台命令行（Windows / macOS）

```powershell
dotnet run --project src\SwitchDesk.Cli -c Release -- --help
```

macOS 发布：

```bash
python3 scripts/build-macos.py --dotnet dotnet --output artifacts/macOS
# Apple Silicon；Intel 改为 osx-x64
./artifacts/macOS/osx-arm64/SwitchDesk.Cli --version
./artifacts/macOS/osx-arm64/SwitchDesk.Cli --help
```

常用命令：

```text
ftp-ls      <host> <port> [user] [password] [path]
ftp-get     <host> <port> <user> <password> <remote> <localFile>
ftp-put     <host> <port> <user> <password> <localFile> <remote>
ftp-put-tree<host> <port> <user> <password> <localDir> <remoteDir>
library-scan|list|verify|clean|dupes <db> ...
snapshot-verify|compare|export ...
updates-check [cacheFile]
homebrew-index <indexUrl> [cacheFile]
stream-args [--mode usb|bridge] [--address ip] [--title text]
```

匿名 FTP 用 `-` 占位用户/密码。macOS 版不含 USB 主机管理（WinUSB 为 Windows 专用），局域网、库、快照、目录查询、投屏参数均可用。

两架构本地开发 ZIP 位于 `dist/`；构建脚本及验证边界见 [Mac 说明](MAC.md)。

## 4. 构建与测试

```powershell
# 构建、实际测试程序、发布与 UI 冒烟（渲染 + 绑定错误检查）
$paths = Get-Content local.paths.json -Raw | ConvertFrom-Json
./scripts/build.ps1 -Dotnet $paths.Dotnet -Python $paths.Python `
  -TestDirectory work/verification/local-run -OutputDirectory artifacts/Windows
```

重新构建 Switch NRO（本机环境）：

```powershell
# PowerShell 7；复用 local.paths.json 中的专用工具链
./work/Build-Local-Switch.ps1
```

该本机入口的默认输出为 `artifacts/Local/Switch/switch/SwitchDesk/SwitchDesk.nro`。独立克隆使用 `./scripts/build-local.ps1 -Target Switch`，详见[构建入口](../scripts/README.md)。构建不部署到主机。`vendor/` 是可重建的依赖缓存。

## 5. 常见问题

| 现象 | 处理 |
|---|---|
| 检测不到主机 | 完整应用模式、WinUSB 驱动、退出其他 USB 工具；看「连接诊断」 |
| 提示「不支持此操作：inspect」 | 相册启动是 Applet 模式；退出配套程序，在主界面**按住 R 打开任意游戏**进入 hbmenu，再启动 SwitchDesk |
| 标准金手指导入按钮灰色 | 需要声明 `cheats.text.v1` 且来源标识匹配的新 NRO；旧 `cheats.write.v1` 不表示支持标准 TXT |
| 更新安装不可用 | 需要新 NRO + 已配置内容来源（本地/HTTP `index.json`） |
| FTP 连不上 | 确认 ftpd 已运行、端口（默认 5000）、同一网段、防火墙放行 |
| Homebrew 索引失败 | 必须是可直接访问的 http(s) JSON（支持重定向），不是网页地址 |
| 投屏无画面 | 主机侧 SysDVR 设置与模式匹配；本地源码构建需手动指定客户端路径 |
| 恢复存档被拒 | 快照必须与当前主机、系统环境、游戏、用户一致 |

> 合规说明：更新/DLC 只从你自有来源获取；Homebrew 仅免费开源内容；不提供商业 ROM 下载或破解资源。
