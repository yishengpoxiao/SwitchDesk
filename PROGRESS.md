# 当前改进

一生破晓维护，基于 [Humboldt-Zh/SwitchDesk](https://github.com/Humboldt-Zh/SwitchDesk)。版本 **0.3.0-dev.3**，来源 `9d90d117d2b8`。

- 全新暖橙／石墨界面、深色侧栏、独立功能区及游戏详情卡片。
- 新生成的“破晓”图标，PNG 与多尺寸 ICO。
- 统一展示署名，保留原作者及第三方许可。
- 修复长 Unicode 导出名、读取重试、设置回滚、参数引用及打包校验；修复新侧栏进度绑定。
- 修复跨平台 CI 路径比较和 GCC 严格编译，新增自动构建并发布完整 Release 的流程。
- 删除旧阶段报告、规划和重复说明；README 与 docs 精简。

本轮本地验证：307 项 C#、44 项 Python、97 张 WPF 渲染通过；Windows、Switch 配套、macOS 两种架构构建完成。CLI 10 项和 Portable Core 8 项通过，3 项平台用例跳过。真机步骤暂缓。

[构建与使用](docs/BUILD.md) · [验证范围及限制](docs/STATUS.md) · [发布下载](https://github.com/yishengpoxiao/SwitchDesk/releases)
