# SwitchDesk 工作约定

- 这是继续开发的唯一主仓库。总工作区的历史源码、发布准备副本和源码 ZIP 只用于追溯，不在旧副本继续修改。
- 若存在上级总工作区，先读 `../../AGENTS.md`、`../../shared/INDEX.md`、`../../projects/README.md`；独立克隆时不强制依赖它们。
- 每次开始先检查 Git 状态，再读 `README.md`、`PROGRESS.md`、`docs/STATUS.md`、`docs/BUILD.md` 和本任务相关验证记录，保留未提交修改。
- 本机目录和专用工具位置在被忽略的 `local.paths.json`。不要把这些绝对路径写入公开源码或修改系统 PATH。
- 迁移与原生隐藏是否可用以 `docs/STATUS.md` 的实际准入规则为准。电脑夹具、目标编译与真实设备结果分开记录；当前迁移准入表为空、原生隐藏关闭。
- 真机步骤按用户安排暂缓。未经后续指示，不部署 NRO、不向主机执行存档写回、安装、隐藏或删除。
- 私有存档、设备与账户身份、安装包、凭据和原始日志只留在忽略目录；不提交到 Git。二进制发布必须带匹配对应源码和摘要。
- 迁移后 Windows 构建与测试使用 `scripts/build.ps1`，显式传入本机配置中的 .NET 和 Python 路径；当前测试程序使用 `dotnet run`，不能把空的 `dotnet test` 输出当作通过。
- 本机 Switch 构建可使用被忽略的 `work/Build-Local-Switch.ps1` 兼容入口，当前委托统一构建脚本。Windows 中文路径下 `elf2nro` 的相对路径处理与入口机制见 `docs/BUILD.md`。
