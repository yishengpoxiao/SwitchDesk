# macOS 版本（SwitchDesk.Cli）

Mac 版目前是终端命令行程序 `SwitchDesk.Cli`，使用与 Windows 相同的 Core 源码。FTP 冲突保护、快照校验与导出、数据库资源释放、TitleDB 和 Homebrew HTTP 完整性修复均随构建同步。它尚无桌面图形界面、USB 主机管理、资源网站搜索或直接 DBI 安装入口。

## 获取与运行

本地交付目录 `dist/` 中的 `SwitchDesk-macOS-arm64.zip` 用于 Apple Silicon，`SwitchDesk-macOS-x64.zip` 用于 Intel。两者自带 .NET 运行时，对应源码为同目录的 `SwitchDesk-Source.zip`，摘要见 `SHA256SUMS.txt`。展开目录也位于 `dist/macOS/osx-arm64/` 和 `dist/macOS/osx-x64/`。

在 Mac 上解压所选 ZIP，在终端进入其中的 `SwitchDesk` 目录：

```bash
chmod +x SwitchDesk.Cli
./SwitchDesk.Cli --version
./SwitchDesk.Cli --help
```

`--version` 显示完整来源标识，可与包内 `build-info.json` 核对。此开发交付未进行 Developer ID 签名或 Apple 公证；首次运行可能需要按 macOS 的来源检查提示处理。

## 功能范围

| 功能 | 命令 | 说明 |
|---|---|---|
| 局域网 FTP | `ftp-ls / ftp-get / ftp-put / ftp-put-tree` | 使用 Switch 上的 ftpd；已有目标受保护，不支持重命名的服务会拒绝上传 |
| 电脑游戏库 | `library-scan / library-list / library-verify / library-clean / library-dupes` | SQLite 索引、校验、缺失索引清理和重复检测 |
| 存档快照 | `snapshot-verify / snapshot-compare / snapshot-export` | 校验、比较和导出已有电脑快照；不会通过这些命令向 Switch 写回存档 |
| 公共更新目录 | `updates-check [cacheFile]` | 完整下载 TitleDB 元数据后再原子替换缓存 |
| Homebrew 索引 | `homebrew-index <url> [cacheFile]` | 核对完整响应和长度后读取 NRO 索引；可原子保存缓存 |
| 投屏参数 | `stream-args` | 输出独立 SysDVR-Client 的参数；不内置 Mac 播放器 |

```bash
./SwitchDesk.Cli ftp-ls 192.168.1.50 5000
./SwitchDesk.Cli ftp-get 192.168.1.50 5000 - - /switch/example.txt ./example.txt
./SwitchDesk.Cli library-scan ./library.db ./Games
./SwitchDesk.Cli snapshot-export ./backup.zip ./Exports
./SwitchDesk.Cli stream-args --mode bridge --address 192.168.1.50
```

FTP 示例地址需要替换为实际主机地址；匿名读写命令用 `- -` 占位用户和密码。Ctrl+C 会请求取消并清理本次暂存文件。退出码为 `0` 成功、`2` 未知命令、`3` 参数或操作错误、`4` 批量校验有不匹配或跳过项目、`130` 用户取消。

## 构建与验证

安装 .NET 10 SDK 和 Python 3.12 后，在仓库执行：

```bash
python3 scripts/build-macos.py --dotnet dotnet --output artifacts/macOS
```

输出目录必须尚不存在。脚本先运行宿主 CLI 进程回归和便携 Core 检查，再分别发布两种架构、核对 Mach-O 架构、ZIP 完整性和可执行权限，并附上匹配的 .NET 许可证。Mac 宿主会额外运行匹配架构程序的 `--version`；Windows 交叉构建只记录构建检查。

两种架构分别发布依据 [.NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。CI 已配置 Apple Silicon 与 Intel Mac 作业；新增配置本身不表示 GitHub 作业已经运行通过，执行结果以实际 CI 日志为准。

当前本机是 Windows。Mac 包的交叉构建、电脑夹具、Mac 原生执行和真实 Switch 操作分别记录；未在 Mac 或主机上执行的步骤不计为通过。WinUSB 一键绑定和 Zadig 仅适用于 Windows；Mac 版不包含这些入口。SQLite 使用系统 `libsqlite3`，无需将 Windows DLL 复制到 Mac。
