# 通过 DBI 安装

“通过 DBI 安装”会启动官方电脑后端，由 Switch 上已安装的 DBI 识别游戏、选择存储并执行安装。此入口不依赖 SwitchDesk 配套的安装预检，也不需要为使用 DBI 更新 SwitchDesk NRO。其他管理功能继续使用 SwitchDesk 配套。

## 使用

### 从 SwitchDesk 直接打开主机上的 DBI

更新到带此功能的 SwitchDesk 配套后，在配套的 USB 管理界面按 **Y**。也可以保持电脑连接，点击 Windows 顶栏的 **在主机打开 DBI**。配套会在 SD 卡上依次检查 `/switch/DBI/DBI.nro` 和 `/switch/DBI.nro`，核对 NRO 文件头，并在当前任务空闲时交给 homebrew 加载器启动。使用官方发布的 DBI 时，把其配置文件留在 NRO 同目录。[DBI 官方安装说明](https://github.com/rashevskyv/dbi#installation)

等 Switch 屏幕显示 **DBI** 后，再按一次 **Y** 进入 **Install title from DBIbackend**。Windows 端添加本机文件并启动后端，在 Switch 上选择文件和目标存储并确认。电脑显示“已安排打开”表示配套已登记启动请求，可在主机屏幕上确认切换结果。[DBI 官方后端流程](https://github.com/rashevskyv/dbi#install-title-from-dbibackend)

### 主机已打开 DBI，或使用旧版配套

1. 在游戏库点击“通过 DBI 安装”，添加电脑上的 NSP、NSZ、XCI 或 XCZ 文件。
2. 选择已有的 `dbibackend.exe`，或使用界面的官方下载功能。下载固定版本并核对长度与 SHA-256；首次需要联网，下载后使用本机缓存。
3. 启动 DBI 电脑后端。SwitchDesk 会释放 USB 管理连接，并在后端会话期间暂停自动连接。
4. 在 Switch 退出 SwitchDesk 配套，打开你已安装的 DBI，进入 **Install title from DBIbackend**（通常按 Y）。在主机选择游戏、目标存储并确认安装。[DBI 官方流程](https://github.com/rashevskyv/dbi#install-title-from-dbibackend)。
5. 以主机 DBI 的安装结果为准。电脑后端退出只能说明会话结束，不能证明游戏安装成功。结束时关闭电脑后端窗口，再返回 SwitchDesk 配套恢复其他管理功能。

已连接的主机任务或投屏尚未结束时，先完成或停止对应任务，再进入 DBI。若电脑后端报告 USB 驱动错误，按 DBI 官方说明检查所需驱动；应用不会自动修改系统驱动。

## 文件与会话

只将用户选定的文件路径作为独立命令行参数交给官方后端，不拼接 shell 命令。相同文件名的多份文件须先重命名区分，避免后端按文件名覆盖列表。会话期间保持只读文件句柄；来源已登记长度与摘要的内容在交给后端前继续核对。

后端是独立窗口，安装仍需在 Switch 上确认。停止会话时正常关闭后端窗口；SwitchDesk 不自动强杀进程、不自动重试主机安装。受管理的更新计划继续走原有严格流程，不会自动改用 DBI。

## 官方后端

运行时下载来源为 [DBI 官方 912ru 发布](https://github.com/rashevskyv/dbi/releases/tag/912ru)中的电脑后端 `dbibackend.exe`，长度 10,609,140 字节，SHA-256：

```text
d2ca9e101609dfdff56f94446b08b9de75e344489885a78daac6021e43992ff2
```

该摘要与官方发布资产元数据核对。后端保存在用户本机缓存或由用户自行选择；不将它复制进本项目源码或 Windows 发布 ZIP，不下载或替换用户的 DBI NRO。官方电脑后端的文件参数入口可在[公开代码](https://github.com/rashevskyv/dbi/blob/main/dbibackend)核对。

## 验证范围

下文记录首次接入电脑后端时的版本。快捷打开 DBI 的本轮验证见[代码审查与测试记录](CODE-REVIEW-2026-11-05.md)。

2026-11-04 在本机使用官方 912ru 后端实际启动了空 GUI，并以空目录参数完成与用户现有 DBI 的 USB 空列表交换。窗口持续收到列表请求，提供的文件数和游戏字节数均为 0；本轮没有执行游戏安装。测试启动的后端实例均正常关闭。

同一发布的源码资产确认有文件参数入口，实际 EXE 的启动和 USB 行为另行验证；不将两者声称为可重建的逐字对应产物。应用回归使用进程／下载替身，单独检查路径传参、来源约束、只读文件锁、会话退出、USB 管理互斥和缓存校验。完整安装是否成功仍以用户在主机 DBI 上的实际结果为准。

最终开发包来源 `15e581a3978f`，完整源码 SHA-256 为 `15e581a3978f7846c498fd970540dceffbf5c2a73d58a6e7cb5c55b165a462cd`。269 项 C#、24 项 Python 和 96 张 WPF 渲染通过，两主题的启动／返回按钮均校验位于窗口和截图内。原生策略／协议／argv 与 57 项 CNMT 主机检查通过，匹配 NRO 另行目标构建；真实安装与这些电脑／构建结果分开记录。本机验证保留于 `work/verification/2026-11-04-dbi-backend/`（按本轮开始日期命名），本地最新交付在 `dist/`。

匹配 NRO SHA-256：`2f85883fd09ee42f910e64fc459a4e03847e5890e3bcdd35549aa7ef0e00d7b9`。本轮没有部署新 NRO、执行真实游戏安装或运行 ASan/UBSan。
