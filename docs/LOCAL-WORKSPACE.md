# 本机工作目录

项目根目录只保留当前源码、构建配置、文档和依赖说明。本机工具位置放在被忽略的 `local.paths.json`，构建入口为 `scripts/build-local.ps1`。

- `dist/`：当前交付。包含 Windows 便携包、Switch 配套包、完整对应源码、摘要和构建清单；`Windows/` 可直接运行，`Switch/` 为配套文件。
- `artifacts/`：脚本默认生成的构建中间产物，可重建。本次清理删除旧内容，后续构建时按需重新创建。
- `work/verification/`：按日期和来源标识保存当前验证日志、结果和界面图。
- `work/history/`：旧审查的必要日志与源码基线压缩归档。记录中的旧阶段目录为历史地址。
- `work/Build-Local-Switch.ps1`：本机 Switch 构建兼容入口，调用正式脚本。
- `third_party/` 与锁定的 `vendor/` 缓存：对应源码和离线构建依赖，继续保留。

`dist/`、`artifacts/` 和 `work/` 均为本机目录，不提交二进制或私人记录。历史安装包不参与当前构建；本次清理移除旧 `release-archives/` 和各阶段交付副本。

打包脚本仍拒绝覆盖同名交付。重新交付时先生成到新的输出目录，核对来源与摘要后再更新 `dist/`。
