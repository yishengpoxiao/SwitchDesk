# 脚本入口与项目路径

所有入口都从脚本所在仓库确定项目根目录，与终端当前目录无关。相对路径参数以项目根目录解析。本机绝对路径只保存在被忽略的 `local.paths.json`；不要写入公开源码或系统 PATH。

Windows 本机统一入口需要 PowerShell 7（`pwsh`）。在项目根目录的 PowerShell 7 终端运行：

```powershell
./scripts/build-local.ps1                         # Windows 构建、测试、发布与界面检查
./scripts/build-local.ps1 -Target Native          # 原生协议、argv 与 CNMT 合成数据回归
./scripts/build-local.ps1 -Target Switch          # 使用现有专用 SDK 构建 NRO
./scripts/build-local.ps1 -Target All             # 以上全部
./scripts/build-local.ps1 -Target All -ValidateOnly
./scripts/build-local.ps1 -Target All -OutputRoot work/my-verification
```

`local.paths.json` 字段：`ProjectRoot`（可选，但必须等于当前仓库）、`Dotnet`、`Python`、`MsysRoot`、`DevkitPro`。填写本机文件／目录路径；`Dotnet` 与 `Python` 指向可执行文件。`-PathsFile` 可以指定另一份本地配置，仍相对项目根目录解析。

默认输出为 `artifacts/Local/`：Windows 程序在 `Windows/`，电脑测试和界面图每轮保存在独立的 `Tests/run-<标识>/`，避免重复构建复用测试状态；原生检查在 `Native/`，配套程序在 `Switch/switch/SwitchDesk/`。Switch 生成源码与中间文件分别在 `companion-source/` 和 `switch-build/`。`-OutputRoot` 必须位于当前项目内；构建会更新选定输出目录，不自动清理历史目录，已有打包 ZIP 由 `package.py` 保护。

本机 Switch 构建默认离线，复用现有 SDK，不重编译或安装 SDK；需要先准备锁定依赖缓存。`-AllowNetwork` 仅允许准备缺少的锁定配套源码依赖。Windows 构建中的固定 SysDVR 资源准备沿用 `build.ps1`，缓存缺少时可能获取锁定资源。`-ValidateOnly` 只核对根目录、配置及工具位置，不创建构建目录或运行编译器。

其他入口继续保留：

- `build.ps1`：CI／手动 Windows 入口，显式传 `-Dotnet`、`-Python`。
- `build-companion.sh`：Linux／MSYS 配套构建；可用 `SWITCHDESK_SOURCE_DIR`、`SWITCHDESK_BUILD_DIR`、`SWITCHDESK_OUTPUT_DIR` 指定项目相对输出，`SWITCHDESK_OFFLINE=1` 限制离线准备，现有 SDK 使用 `SWITCHDESK_SKIP_SDK_BUILD=1`。
- `test-cnmt.py`：直接编译生产解析器，运行合成 AES 回归；`--verify-regression` 另外验证旧密钥槽缺陷会被测试捕获。
- `package.py`：打包当前来源匹配的 Windows 程序和完整对应源码；已有同名 ZIP 时拒绝覆盖。

以上脚本只生成本机产物，不部署 NRO，不向主机执行安装、存档写回、隐藏或删除。发布二进制仍须附上匹配的完整对应源码及摘要。
