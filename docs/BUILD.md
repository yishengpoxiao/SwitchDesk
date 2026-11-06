# 构建与源码对应关系

构建脚本不修改系统 PATH。本机 SDK 目录和工具位置只保存在被忽略的 `local.paths.json`，或单次构建进程的环境中。当前版本由根目录 `version.json` 定义，验证范围见[状态说明](STATUS.md)。

## Windows

本机已有 `local.paths.json` 时，可使用统一入口 `./scripts/build-local.ps1`；`-Target Native`、`-Target Switch` 或 `-Target All` 运行其他目标。该入口要求 PowerShell 7，始终以脚本所在项目为根目录；配置、相对路径和输出规则见 [scripts/README.md](../scripts/README.md)。SDK 安装路径只留在本地配置，Switch 构建默认离线复用专用 SDK。

本机交付集中在 `dist/`，可重建输出在 `artifacts/`，验证日志和隔离夹具在忽略的 `work/verification/`。这些目录不提交到源码仓库。

需要 Windows x64、.NET 10 SDK 与 Python 3；SDK 选择以 `global.json` 为准。默认发布包内含运行时。

```powershell
$paths = Get-Content local.paths.json -Raw | ConvertFrom-Json
./scripts/build.ps1 -Dotnet $paths.Dotnet -Python $paths.Python
# 或分别构建与测试
& $paths.Dotnet build src/SwitchDesk.App/SwitchDesk.App.csproj -c Release
& $paths.Dotnet run --project tests/SwitchDesk.Tests/SwitchDesk.Tests.csproj -c Release
```

输出：`artifacts/Windows/`。脚本默认完成测试、发布和离屏 UI 检查。测试程序是有退出码的控制台检查器，请使用 `dotnet run`，不能把仅执行 `dotnet test` 的空结果作为通过。可用 `-TestDirectory <目录>` 指定夹具目录。

`build.ps1` 发布时使用单文件、自包含配置，并关闭调试符号，避免将 PDB 中的本机目录带入交付。需要单独发布时使用同样的参数：

```powershell
& $paths.Dotnet publish src/SwitchDesk.App/SwitchDesk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/PublicWindows
```

发布包不要附带 PDB、bin/obj 或个人构建日志。仓库 README 的图片在 docs/images；打包文档时应一并保留。

## Switch

需要 devkitPro/devkitA64、deko3d、switch-curl、switch-glm、switch-zlib、switch-mbedtls、switch-cmake，以及 CMake、Ninja、GNU make、pkgconf、Git、Python。`DEVKITPRO` 指向本机安装目录。

```bash
export DEVKITPRO=/opt/devkitpro
# 将 devkitPro 和构建工具加入当前 shell 的 PATH，按实际安装目录调整
export PATH="$DEVKITPRO/tools/bin:$DEVKITPRO/devkitA64/bin:$DEVKITPRO/portlibs/switch/bin:$PATH"
python3 scripts/prepare-companion.py
bash scripts/build-companion.sh
```

准备脚本校验固定上游源码、应用 `companion/` 覆盖与补丁，再准备依赖。`prepare-sdk.py` 会从随附源码构建固定 libnx/newlib 并安装到指定 `DEVKITPRO` 前缀；请使用专用 SDK 前缀，避免影响其他项目。稳定版 libnx 包不足以构建此基线。

已验证的本机工具链包括 devkitA64 GCC 16.1.0、CMake 4.4.4、Ninja 1.13.2 和 Windows MSYS。SDK 包版本、SHA-256、公开签名均记录在 `build-dependencies.lock.json`。固定源包括 Sphaira `338348e74b6d278bc570a9be5373d25197bf6c8d`、libnx `b156b4774fb2df7b93402169509266da7547fc4c`、newlib `8e4d4166b043622b1fe463dbdb8255a05f0d56b2`。

若使用 Windows SDK 引导脚本：

```powershell
# 需要 Python zstandard 与可用 gpgv；工具位置仅保存在本机配置
$paths = Get-Content local.paths.json -Raw | ConvertFrom-Json
& $paths.Python scripts/bootstrap-windows-sdk.py --prefix $paths.DevkitPro `
  --gpgv (Join-Path $paths.MsysRoot 'usr/bin/gpgv.exe')
```

`third_party/devkitpro-signing-keys.gpg` 是用于校验 SDK 包的**公开签名密钥环**，不是主机密钥或私钥。完整源包额外带 `vendor/cache/*.zip`，可离线准备 Sphaira 覆盖层：

```bash
python3 scripts/prepare-companion.py --offline
```

离线准备成功不代表已经编译 NRO；编译仍需完整工具链。输出：`artifacts/Switch/switch/SwitchDesk/SwitchDesk.nro`。不承诺容器 `latest` 与本地 SDK 产物完全一致。

### Windows 中文路径与本机入口

Windows `switch-tools 1.13.1-1` 的 `elf2nro` 曾在处理含中文的 RomFS 绝对路径时失败。遇到此类打包错误时，应在目标生成目录中执行 `elf2nro`，将 ELF、NACP、图标、RomFS 和 NRO 输出参数改为相对该目录的路径；不要移动主仓库或修改系统 PATH 来绕过问题。编译、链接成功与最终 NRO 打包成功需要分别核对。

本机忽略目录中的 `work/Build-Local-Switch.ps1` 是兼容入口，当前委托 `scripts/build-local.ps1 -Target Switch`，转发输出目录、并行数和网络选项；构建逻辑统一保留在 `scripts/`。它读取 `local.paths.json` 指定的专用工具链，不维护第二份活动源码，也不自动部署 NRO。独立克隆直接使用公开入口即可：

```powershell
./scripts/build-local.ps1 -Target Switch
# 本机兼容入口存在时也可使用
./work/Build-Local-Switch.ps1
```

默认统一入口输出为 `artifacts/Local/Switch/switch/SwitchDesk/SwitchDesk.nro`。本机兼容入口本身不承诺自动规避所有工具版本的中文路径限制；若 `elf2nro` 仍失败，按上述相对路径规则处理最终打包并核对产物。

## macOS 命令行版

使用 .NET 10 SDK 与 Python 构建共享 Core 的 CLI：

```bash
python3 scripts/build-macos.py --dotnet dotnet --output artifacts/macOS
```

输出目录必须尚不存在。脚本运行宿主 CLI 与便携 Core 检查，再发布 Apple Silicon / Intel 两种自包含程序，核对 Mach-O 架构、ZIP 权限并附许可证。Windows 交叉构建不计为 Mac 原生运行通过；Mac 宿主额外执行匹配架构的 `--version`。实际验证范围见[状态说明](STATUS.md)。

在 Mac 解压后运行 `chmod +x SwitchDesk.Cli`，再用 `./SwitchDesk.Cli --help` 查看 FTP、电脑游戏库、快照、元数据和投屏参数命令；`--version` 可与包内 `build-info.json` 核对。当前没有 Mac 桌面界面、WinUSB 管理或直接 DBI 安装；开发包未签名或公证，SQLite 使用系统 `libsqlite3`。

## 原生检查与 CI

```bash
g++ -std=c++20 -Wall -Wextra -Wpedantic -Werror -fsanitize=address,undefined -fno-omit-frame-pointer -Icompanion/include companion/tests/native_tests.cpp -o native-tests
./native-tests
g++ -std=c++20 -O1 -g -Wall -Wextra -Wpedantic -Werror -fsanitize=address,undefined -fno-omit-frame-pointer companion/tests/argv_tests.cpp -o argv-tests
./argv-tests
```

CI 配置位于 [GitHub 工作流](../.github/workflows/)，实际执行结果以对应提交的运行记录为准。本地通过不等于远端 CI 通过；sanitizer 只覆盖便携逻辑，不能代替 Horizon 实机检查。

## 发布与对应源码

当前 Windows、Switch 与 Mac 构建应使用同一份冻结源码和版本配置。`build-info.json` 通过实际 `src/`、`companion/`、`scripts/` 和版本／依赖锁生成来源指纹；不要将旧 NRO 改名作为新配套，也不要混用来源不一致的运行包。

每次二进制发布附上**匹配的完整对应源码**，包含自有代码、补丁、构建脚本、固定上游及依赖源码，并保留许可证；不含编译器二进制或私人数据。具体版本、来源与哈希见同一交付的清单和 `SHA256SUMS.txt`。历史产物与对应源码在 Git／Release 历史中追溯，不从当前主分支倒推。

源码仓库不提交 `bin/`、`obj/`、`artifacts/`、`vendor/`、日志或个人数据。`third_party/` 固定源码归档有意保留，便于追溯来源；`vendor/` 可以重新生成。

本地打包使用 `python scripts/package.py --windows artifacts/Windows --output artifacts/Packages`。脚本复用构建来源生成器，拒绝源码已经变化的旧 Windows 构建；源码 ZIP 只从 `vendor/cache` 加入锁文件列出的依赖源码，并核对摘要。请先完成配套依赖准备；缺少或损坏的源码归档会中止打包。输出目录须在 Windows 构建目录之外，已有同名交付文件会保留，请改用新的输出目录。此命令只生成本地文件，不发布 Release。

[开发版发布工作流](../.github/workflows/release.yml)在主仓库 `main` 的版本配置或发布流程更新时运行，也可手动触发。它复用现有 Windows、双架构 Mac 与 Switch 构建，完成软件检查后统一核对完整来源标识、对应源码依赖和摘要，生成可供本地上传的 `verified-release` 产物。默认不公开发布；仅手动触发并明确勾选 `publish` 时，最终发布任务才持有内容写权限并创建预览标签。已有标签不会被覆盖，失败时查看对应 Actions 记录。自动构建与发布不表示设备验收通过。

## 随包播放器

准备脚本按 `streaming-dependencies.lock.json` 下载并校验官方 SysDVR v6.3（提交 `531e9c25c4e5a25954eeec677fe439fe812e9b19`），需要 Python 与可解压 7z 的 `tar`：

```powershell
python scripts/prepare-streaming.py --windows-output artifacts/Windows --setup-output artifacts/Windows/StreamingSetup --verify-runtime
# 已有完整缓存时添加 --offline；缓存缺失或摘要不符会明确失败
```

`Streaming/SysDVR/` 包含独立官方客户端、完整运行时、原生 DLL、资源、`Licenses/` 与 `Sources/`，不能只复制 EXE。缓存位于忽略的 `vendor/streaming/`。`--verify-runtime` 仅检查版本、帮助与解码器，不打开 USB 或播放窗口，不代替设备音画验收。准备脚本不改系统 PATH、驱动或主机文件。

`StreamingSetup/SDCard/` 提供官方完整版 sysmodule 与 `switch/SysDVR-conf.nro`，由用户自行备份原文件并安装。生成资源移除 `config/sysdvr/usb`、`rtsp`、`tcp` 开机标记，使新配置默认关闭；复制目录不会删除已有 SD 卡上的旧标记。已有安装需在 SysDVR-conf 点 **Stop streaming**，再保存当前模式为启动默认值。

管理前在主机停止串流再打开 SwitchDesk 配套；预览前结束管理并退出配套，在 SysDVR-conf 切到 USB，再打开支持录制的游戏和电脑播放器。返回管理时先停电脑预览，再在主机停止串流。USB 模式占用主机 `usbDs`，仅关闭电脑播放器无法释放它。固定版本仅捕获游戏输出，支持 720p / 30fps，不捕获 HOME、系统设置或 Applet 画面，底座 USB 串流不受支持；不自动安装额外 DVR 补丁。

SysDVR 为 GPL-2.0，保持独立进程并随包提供固定完整源码。包内 FFmpeg DLL 实际报告 LGPL version 3 or later，对应 `n5.1.2-39-g2953c6381a`、源码 `2953c6381afc3d52f3c97692228a5120fad4e0dd`，原构建配置随源码保留；FFmpeg.AutoGen 5.1.2.1 也保留 LGPL-3.0 文本。其他依赖、未修改字体的许可与来源见[第三方声明](../THIRD-PARTY-NOTICES.md)和随包原文。二进制、源归档和配置变更由锁文件、`bundle-manifest.json`、`setup-manifest.json` 追溯。

来源：[SysDVR v6.3](https://github.com/exelix11/SysDVR/releases/tag/v6.3)、[固定源码与限制](https://github.com/exelix11/SysDVR/blob/531e9c25c4e5a25954eeec677fe439fe812e9b19/readme.md)。

## 可选 DBI 后端

“通过 DBI 安装”调用独立官方后端，由用户选择程序或按需下载到本机缓存，不嵌入源码或发布 ZIP。固定来源为 [DBI 912ru](https://github.com/rashevskyv/dbi/releases/tag/912ru) 的 `dbibackend.exe`，长度 10,609,140 字节，SHA-256 为 `d2ca9e101609dfdff56f94446b08b9de75e344489885a78daac6021e43992ff2`。DBI 自身权利与使用条款独立适用。

支持 `dbi.launch.v1` 的配套可在空闲时检查 `/switch/DBI/DBI.nro`、`/switch/DBI.nro` 并交给加载器；“已安排打开”须在主机画面确认。随后在主机 DBI 进入 **Install title from DBIbackend**，选择文件与存储并确认。电脑后端退出仅表示会话结束，不表示安装成功。管理、DBI 与投屏互斥，不自动重试安装、不自动替换 DBI NRO 或驱动；受管理更新不降级改用 DBI。操作入口遵循[官方流程](https://github.com/rashevskyv/dbi#install-title-from-dbibackend)。

## 格式来源与许可

- [Stagstation 固定源码](https://github.com/dizzyfrogs/stagstation/blob/475357523442333586ed4c80a96216340903c7ac/converter.js) 提供 Knight 字符串封装常量与 AES / JSON 格式参考：MIT，Copyright (c) 2025 AJB，许可原文见 [Stagstation-MIT.txt](../third_party/licenses/Stagstation-MIT.txt)。SwitchDesk 重写有边界检查的 C# 解析与转换层，不调用 BinaryFormatter，不引入 Node 运行依赖。
- [Vampire Survivors Save Editor](https://github.com/chrisdfennell/vampire-survivors-save-editor) 用于 PC 摘要和序列化差异核对；PC 说明不能证明 Switch 格式兼容。
- [Alive Cells 格式实现](https://github.com/N3rdL0rd/alivecells/blob/main/savetool/savetool.py)及[格式文档](https://n3rdl0rd.github.io/ModDocCE/files/save/)用于死亡细胞文件头、摘要和数据块核对。未嵌入上述项目的 Python 程序，也不复制带额外使用限制的其他转换站点代码。
- [libnx 固定 fs 接口](https://github.com/switchbrew/libnx/blob/b156b4774fb2df7b93402169509266da7547fc4c/nx/include/switch/services/fs.h)提供额外数据、提交时间与 commit_id；本工具不写入额外时间字段。[TitleDB](https://github.com/blawar/titledb)仅提供元数据，不提供安装包。

适配器注册、文件可解析或备份成功均不授予迁移资格；具体版本和游戏内准入规则见[状态说明](STATUS.md#迁移与原生隐藏准入)。

## USB 协议与写入保护

设备端使用 Sphaira 的双向 USB Bulk 接口，USB 产品字符串必须为 `SwitchDesk PC`。WinUSB 主机端先读取描述符，再交换应用协议。管理状态与 SysDVR USB 会话分开运行。

### 帧

16 字节小端头：4 字节 `0x314B4453`（ASCII `SDK1`）、2 字节协议版本 1、2 字节类型、8 字节载荷长度。单帧上限 16 MiB，普通文件块上限 1 MiB。

类型：1 请求 JSON，2 响应 JSON，3 二进制，5 源文件随机读取请求，6 进度 JSON，7 取消会话（空载荷）。

请求为 `{"id":1,"method":"hello","args":{"protocol":1,"client":"SwitchDesk/<version>+<sourceId>"}}`。

响应成功为 `{"id":1,"ok":true,"result":{...}}`；失败为 `{"id":1,"ok":false,"error":{"code":"...","message":"..."}}`。`result` 可以是对象或数组。对象中出现 `binaryLength` 时，紧接一个长度匹配的二进制帧。

安装或解析期间，设备发送类型 5，载荷为 `{"offset":4294967313,"length":1048576}`；主机验证文件范围后发送类型 3。源文件保持随机访问，不把整个安装包预先传入 SD。

进度为 `{"completed":123,"total":456,"stage":"..."}`，表示当前子传输；`total=0` 为未知总量。所有大小与偏移都用 64 位整数，不能截断为 32 位。

请求按连接串行执行。取消会话会终止连接并清理句柄；传输半包时设备使用有限的数据块超时，随后重新初始化 USB。恢复写回一旦开始会继续完成或返回失败，不支持事务性掉电回滚。

### 查询和文件接口

- `hello`：协议协商；`device.info`：设备信息和能力列表；`device.release`：结束配套管理模式。
- `storages.list`：SD 与当前系统用户分区。emuMMC 环境标识还包含配置路径指纹，避免不同虚拟系统共用备份命名空间。
- `titles.list`：按应用组织的内容；`titles.icon {titleId}`：按需返回图标二进制。
- `saves.list`：Account/Device 存档及主机侧不透明 key。
- `fs.list {path}`、`fs.stat {path}`、`fs.mkdir {path}`、`fs.rename {path,target}`、`fs.delete {path}`。
- `fs.read.open {path}` → `{handle,size}`；`read.chunk {handle,path,offset,length}` → 二进制响应。普通文件和游戏导出使用空 `path`，存档使用相对路径。
- `fs.write.open {path,size}` → `{handle}`；`write.chunk {handle,path,offset}` 后立即跟二进制帧；`fs.write.commit {handle}` 完成临时文件改名。
- `handle.close {handle}` 释放资源，连接结束也释放所有资源。

远端绝对路径以 `/` 开始，不接受 `..`、反斜杠、驱动器或空字节。主机和电脑都执行路径保护。普通文件接口不能修改 Nintendo、emuMMC、Atmosphère、bootloader、配套程序自身及关键启动文件。

### 存档接口

- `saves.read.open {key}` → `{handle}`；`handle.tree {handle}` 返回相对文件和目录；`read.chunk` 读取已挂载存档。
- ZIP 格式版本 1：`manifest.json`，文件位于 `data/<relative-path>`；目录清单单独保存在 manifest 中。清单含身份信息、每文件 SHA-256、大小及内容指纹。
- `saves.restore.begin {key,manifest}`：核对身份、容量和 SD 暂存空间，返回句柄。
- `saves.restore.file {handle,path,size,sha256}` 登记清单内文件；随后使用 `write.chunk`，主机限制顺序偏移和总大小。
- `saves.restore.commit {handle}`：先完整验证暂存文件，再挂载并替换目标内容、提交和逐文件复核。失败时保留已写入阶段的暂存目录与电脑端恢复日志。

电脑端在恢复请求之前，已完整验证输入 ZIP 并生成当前存档的 Recovery 快照。首次恢复必须使用测试存档验收。

### 游戏接口

- `device.info` 在加载器支持启动另一个 NRO 时返回能力 `dbi.launch.v1`。
- `dbi.launch {}` 由用户明确触发。配套检查固定路径上的 DBI，拒绝自定义参数及传输中的请求，返回 `{scheduled:true,path:"/switch/DBI/DBI.nro"}`（也可能为 `/switch/DBI.nro`）。收到成功回复后，电脑释放管理 USB 连接；主机结束配套，加载器接手。回复丢失时由用户查看主机画面，电脑不会自动重试。

- `package.inspect {name,size}`：通过随机读取源文件解析容器、NCA 及 CNMT，返回组件及解压后的所需空间。
- `titles.install {name,size,storage}`：再次预检空间，调用上游安装引擎。`storage` 只能是 `SdCard` 或 `BuiltInUser`。
- `titles.export.open {titleId,version,storage}`：通过上游 NSP 构造器打包单个组件，返回 `{handle,size}`，后续使用 `read.chunk`。

UI DTO 定义位于 `src/SwitchDesk.Core/Models.cs`；设备实现位于 `companion/source/server.cpp`。权限和密钥错误返回实际失败，不用模拟数据替代。

### 本地扩充：金手指写入能力

- 完整应用模式返回 `cheats.write.v1`（旧 `.cheat`）及新增 `cheats.text.v1`（Atmosphère 标准 `.txt`）。仅声明旧能力不表示支持标准 TXT，电脑端按所选文件类型协商。
- 普通文件接口的保护规则保持不变；仅对下面两种目录形状和一种文件形状开放写入：
  - `/atmosphere/contents/<16 位十六进制 TitleID>`
  - `/atmosphere/contents/<16 位十六进制 TitleID>/cheats`
  - `/atmosphere/contents/<16 位十六进制 TitleID>/cheats/<16 位十六进制构建ID>.txt`（或 `.txt.off`），保留旧 `.cheat[.off]`
- `fs.mkdir` 仅额外接受上述两个目录；`fs.delete` / `fs.write.open` 仅额外接受该文件形状；`fs.rename` 仅在源与目标都是该文件形状时放行（用于 `.cheat` ↔ `.cheat.off`）。
- 除此之外 `/atmosphere`、`/Nintendo`、`/emuMMC`、`/bootloader` 等仍由主机与电脑两端共同拒绝。

### 扩展能力与恢复边界

扩展通过 `device.info.capabilities` 协商，不能仅凭桌面版本启用；完整应用模式和 Applet 模式的能力不同。

- `saves.inspect` 返回整个存档文件系统指纹、可用的 POSIX 提交时间与 commit_id；时间不可靠时返回 ObservedOnly，提交时间不表示某个槽位的独立游戏时间。
- `restore.atomic.v1` 要求完整暂存与校验、数据及日志空间预检、PC 保护副本和 SD 原始副本，最后一次 `fsFsCommit`。空间不足直接拒绝，不退回清空后提交或逐文件提交；真实掉电行为仍需专用存档验收。
- `operations.status` 按操作 ID 查询 SD 日志；上次仍处于 writing／installing 时必须复核实际内容，不能直接重放。
- `package.dependencies.v1` 提供真实 CNMT 的应用归属、组件类型、最低系统和应用版本。
- `install.plan.v1` 只接受一个明确选定的组件，核对设备、环境、原内容、版本和依赖，显式设置安装策略，不继承上游跳过检查或降级设置。

未知格式只检查与备份，不执行存档中的脚本。私有存档、设备及账户身份、原始日志只能留在被忽略目录；验证与启用门槛见[状态说明](STATUS.md)。
