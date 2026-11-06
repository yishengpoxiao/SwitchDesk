# 当前改进

一生破晓维护，基于 [Humboldt-Zh/SwitchDesk](https://github.com/Humboldt-Zh/SwitchDesk)。版本 **0.3.0-dev.4**，来源 `c522b75843b5`。

- USB／FTP 下载使用短临时文件名，支持接近文件系统名称上限的合法目标名。
- FTP 取消后确认旧回复已排空；超时则关闭连接，避免下一次请求失步。
- 依赖下载先校验临时文件，再写入缓存；中断或错误摘要不再阻断重试。
- 资源搜索的取消按钮固定显示，所有来源均可取消；长错误提示不再挤占列表。
- 保留破晓主题、图标与精简文档。CI 自动构建和校验；公开发布需显式执行，支持本地上传。

本轮本地验证：311 项 C#、47 项 Python、97 张 WPF 渲染通过，Windows 构建零警告、零错误。跨平台产物由同提交 CI 构建与复核；结果和源码标识随 Release 清单提供。真机步骤暂缓。

[构建与使用](docs/BUILD.md) · [验证范围及限制](docs/STATUS.md) · [发布下载](https://github.com/yishengpoxiao/SwitchDesk/releases)
