# 更新记录

## 一生破晓改进版（0.3.0-dev.2）— 2026-11-06

- 基于 Humboldt-Zh/SwitchDesk 继续改进，统一项目署名与主页，保留上游和第三方归属。
- 美化浅深主题、侧栏与游戏卡片；游戏库、存档库和资源搜索新增一键清空。
- 修复长中文／emoji 文件名导出、清单暂不可读后的扫描恢复、超大清单、设置保存失败回滚、水平滚动、打包一致性和命令行参数引用。
- 307 项 C#、35 项 Python、97 张 WPF 渲染及双端构建通过；完整边界见 [审查记录](docs/CODE-REVIEW-2026-11-06.md)。

## 本地扩充（基于 0.3.0-dev.2）— 2026-11-03

以下为本仓库本地扩充会话新增内容（基于 `sync/development-0.3.0-dev.2`，未发布版本号）：

- **局域网 FTP 文件传输**：新增 `SwitchDesk.Core/FtpClient.cs`（零依赖、被动模式、MLSD/Unix/Windows 列表解析回退），桌面端新增「局域网」页，可无线读写运行 ftpd / sys-ftpd 的 Switch SD 卡；写操作沿用 PathSafety 保护规则。测试以回环 FTP 模拟主机覆盖列表三种格式、上传下载、取消与断传恢复。
- **SysDVR 网络 Bridge 投屏**：投屏入口增加「USB / 网络 Bridge（无线）」模式与可选主机地址；Bridge 模式不再释放 USB 管理连接。参数构造依据 SysDVR-Client 6.x 官方 CLI。
- **金手指（EdiZon）管理**：游戏详情新增「金手指」标签；解析/导入/启用停用/删除 `.cheat`，仅放行 `/atmosphere/contents/<TitleID>/cheats/<构建ID>.cheat` 严格形状（`PathSafety.IsCheatPath`），Atmosphere 其他内容保持保护。
- **金手指需要设备端配合（真机关键）**：原设备端 `safety.hpp` 的 `Writable()` 拒绝全部 `/atmosphere` 写入，PC 端放行不足以在真机生效。已同步修改 companion：`safety.hpp` 新增 `IsCheatFile` / `IsCheatDirectory` 严格例外，`server.cpp` 的 `fs.mkdir` / `fs.delete` / `fs.rename` / `fs.write.open` 放行对应形状；PC 端安装前自动补建 `contents/<TitleID>/cheats`。**需用 devkitPro 重新构建并部署新版 NRO 后金手指才可用**（见 docs/BUILD.md）；新版 `device.info` 会公布 `cheats.write.v1` 能力，旧 NRO 上相关按钮保持禁用并提示。已在本机用 devkitPro 工具链完成编译：设备端 native 测试通过，NRO 构建成功（`0.3.0-dev.2 + a48c18e718ab`，内含 `cheats.write.v1`），产物 `artifacts/SwitchDeploy/SwitchDesk-0.3.0-local-Switch.zip`（SHA-256 `D0529B2863E28DA3652564A9D4A1628A0001878EF4FB923858AB314A0FA771F1`）。真机生效仍需把 NRO 拷入 SD 卡并实机验证。
- **Homebrew 应用商店**：可配置 JSON 索引浏览社区免费开源应用，下载并写入 SD 卡 `/switch/`；不涉及任何商业 ROM 分发。
- **游戏库维护**：扫描标记「文件缺失」、显式清理缺失索引、按 TitleID/内容哈希查找重复、批量校验（对应 ROADMAP 第三阶段）。
- **存档快照对比与导出**：`SnapshotTools.CompareAsync`（基于 SHA-256 清单的差异）与 `ExportSnapshotAsync`（JKSV 风格导出副本）。
- **SwitchDesk.Cli（跨平台命令行）**：FTP 传输、游戏库维护、快照校验/对比/导出、公共更新目录查询、Homebrew 索引、投屏参数构造；已从 Windows 交叉发布 osx-arm64 / osx-x64 自包含单文件（见 docs/MAC.md）。Core 的 SQLite 访问增加非 Windows 解析器（libsqlite3）。
- 自动联网查询游戏更新沿用本分支二期能力（TitleDB 公共目录 + 自有来源），并经 CLI 开放。迁移准入、原生 HOME 隐藏等仍按上游门控保持关闭。
- **本地复核修复**：FTP 空目录不再误报失败、所有命令拒绝换行注入、断线状态与关闭超时；Homebrew 跟随 HTTP 重定向并对哈希格式严格校验；库「文件缺失」只标记本次扫描目录内的条目；金手指列表切到标签时自动刷新、目录不存在时返回空；快照对比改用当前存档上下文；CLI 端口/选项参数校验。
- **已知限制收敛**：FTP 上传改为「临时文件 + 重命名」原子提交（取消/失败清理临时文件；不支持重命名的服务器退回直接写入），新增递归文件夹上传（桌面与 `ftp-put-tree`）；FTP 密码只在当前会话使用、不再写入配置文件（旧版明文会在加载时清除）；金手指解析兼容 BOM、`#`/`;` 注释与行内注释；快照导出同秒重名自动加序号，绝不覆盖。
- **Switch↔电脑存档同步（全部游戏）**：新增 `SaveSync`（状态/计划/方向判定）与存档页同步面板；先读取主机游戏与存档，按设备指纹（`saves.inspect`）与电脑快照哈希双向判定：主机更新→备份到电脑、电脑更新→写回主机（带恢复保护）、两端都变→冲突人工选择、首次→建议备份；状态存于 `SaveSync/save-sync.json`，不做格式转换，对全部有存档的游戏通用（Steam↔Switch 格式转换仍由跨平台适配器负责）。
- **跨系统存档迁移（sysMMC ↔ emuMMC）**：存档页选择其他系统环境的备份版本后「迁移到当前系统」；设备端 `saves.restore.begin` 新增受控 `foreignSource`（仅限同游戏/用户/类型，必须携带目标存档指纹做原子保护），PC 端 `RestoreAsync` 放开同源校验并强制原子恢复；官方真实系统无法运行破解程序，不在通道内。
- **NRO 更新**（`0.3.0-dev.2 + 9faad2a4165f`）：新增 `foreignSource` 与上轮的 `package.inspect` 阶段化报错（容器/密钥/NCA 头/CNMT）；产物 `artifacts/SwitchDeploy/SwitchDesk-0.3.0-local-Switch.zip`（SHA-256 `B9C4650F2BB99562F3E59CF63816E19CAA43150E3ED4A6B185A49F09E5880FA6`）。
- **用户态 CNMT 解析（DBI 同思路，修复「主机操作失败」）**：`package.inspect` 的 CNMT 解析改为用户态优先——`DecryptKeak`（spl 派生节密钥）→ `NcaReader`（按 section_ctr CTR 解密）→ 自行解析 PFS0 → 读取 CNMT；完全绕开 fsp-srv 按程序 ID 打开的系统调用，不再对 emuMMC / 新固件敏感。fsp-srv 保留为回退路径。
- **全量代码审查修复（3 个审查代理，17+ 发现）**：
  - **[CRITICAL]** FTP 导航/重命名/删除在后台线程读 `FtpPath.Text` 导致 100% 崩溃 → 全部改为 UI 线程预先捕获路径
  - **[HIGH]** 金手指列表竞争：切换游戏后旧结果可能填入新游戏详情并误操作 → `RefreshCheatsAsync` 加标题匹配守卫
  - **[HIGH]** FTP `TryListAsync` 丢弃传输完成回复 → 传输中止时截断列表被当作完整结果 → 检查 2xx 回复
  - **[HIGH]** FTP 传输非取消异常不排空服务器回复 → 控制连接失步 → 补充 AbortAsync 清理
  - **[HIGH]** FTP `Dispose` 在 UI 线程上 sync-over-async 死锁 → 改为直接 `CloseControl`
  - **[HIGH]** SaveSync：Recovery 快照污染同步候选池 → 恢复刚被覆盖的旧内容 → 排除 Recovery 目录
  - **[HIGH]** SaveSync：备份前后指纹不配对 → 游戏写入时误标 UpToDate → 备份前取指纹 + 备份后验证一致性
  - **[HIGH]** Library：文件重新出现后「文件缺失」标记永不清除 → 批量校验/查重永久排除 → 扫描时重置状态
  - **[MED]** C++ `Mkdirs` 把同名文件存在当成功 → EEXIST 时 stat 验证 S_ISDIR
  - **[MED]** C++ restore.begin `directories` 数组无上限 → 加 100000 上限
  - **[MED]** CLI `JsonException` 不在 catch 过滤器内 → 补充到过滤器
  - **[MED]** `InspectInstallEntriesAsync` 把取消当失败 → 补 OCE catch
  - **[MED]** `VerifyLocalAsync` 重试可能校验不同文件 → 捕获文件路径
  - **[MED]** `StopStreamAsync` 异常逃逸 → 补 try/catch
  - **[LOW]** FtpClient sync-over-async Dispose → 改为直接 CloseControl
  - Clean: HomebrewStore/Cheats/PathSafety/SettingsStore/SaveSyncOperations/Dialogs/Safety.hpp/ExportReads/RestorePolicy/Wire
- 验证：171 项电脑测试（+26 新增）与 WPF 冒烟全部通过；设备端 native 测试通过，NRO 校验 `NRO0` 与新增能力/错误字符串；协议仍为 1，旧 0.2.4 NRO 保留在 `release-archives/` 可回滚。

## 0.3.0-dev.2（未发布）— 2026-11-03

- 修复 NSP 导出在头部和内容段边界正常短读时误报文件变化的问题；需要匹配的新配套 NRO。
- 跨平台存档增加独立侧栏首页，选择游戏和用户后自动发现进度文件；一次比较完成双端备份，迟到响应不会串到其他用户。
- 存档页改为独立备份库和主区域时间线；已有备份不再受电脑安装包筛选影响，离线仍可按完整来源身份浏览。
- 同步尚未公开提交的二期基础：双端发现与比较、迁移保护、自有更新／DLC 来源及中断状态核对。迁移准入表仍为空，原生 HOME 隐藏仍关闭。
- 整合 USB 单线预览、受控原生窗口宿主，以及固定 SysDVR 运行时、许可和源码资源；打包与电脑生命周期检查不代表真实连续音画通过。
- 本地来源 `bc5abe181644`：145 项电脑测试、72 张 WPF 渲染、原生 ASan/UBSan 与双端构建通过。新 NRO 尚未部署，真实导出和其他硬件闭环继续待验收。
- 旧版 0.2.5 / 0.2.4 Release 与下载资产保持不变；完整范围见[实测修复记录](docs/FIELD-FIXES-2026-11-03.md)。

## Windows 0.2.5 — 2026-10-23

- 使用既有 SwitchDesk 标志统一 EXE、标题栏和任务栏图标。
- 稳定性基线沿用 0.2.4；配套 NRO 仍为已部署的 0.2.4，协议 1。
- 开源整理补齐中英文介绍、演示截图、构建／使用／验证文档和贡献入口；公开 Windows 构建去除本机调试路径。整理未扩充实机兼容承诺。

## 双端 0.2.4 — 2026-10-23

- 修复快照身份校验、句柄清理、取消提交窗口、导出文件冲突、危险文件名、损坏设置与缓存等问题。
- 完成 70 项电脑自动化检查与目标平台编译。
- OLED 以 emuMMC 完整应用模式完成 NRO 启动、X/B、重新打开和一次 USB 资料读取。主机写入及故障矩阵仍待测。

## Windows 0.2.3

- 将任务进度与历史集中到连接诊断；正常使用页面移除底部任务区，避免重复通知。

## 双端 0.2.2

- 落地澄空 / AIR 视觉：游戏库、右侧详情、主题与响应式布局。
