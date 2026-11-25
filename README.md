# Codex Usage Bar for Windows

**v0.1.0-alpha.1 · 早期预览版 · 非官方工具**

这是一个按需打开的 Windows 用量浮窗。它从本机 Codex 会话日志读取累计 token、缓存命中率和上下文占用。展开明细后，可以看到输入、未缓存输入、缓存读取、日志报告的缓存写入和输出。

## 快速开始

1. 下载并解压 `codex-usage-bar-windows-0.1.0-alpha.1.zip`。
2. 在 Windows 上双击 `CodexUsageBar.exe`。需要 .NET Framework 4.8；当前仅在开发机器的 Windows 环境中验证。
3. 点右侧箭头展开明细并切换会话。可以搜索标题、项目，或在 Codex 桌面应用里用 `Ctrl + Alt + L` 复制本地会话链接，再到浮窗里点「粘贴链接」。

浮窗关闭后退出，不设置 Windows 登录启动项。默认数据目录为用户目录下的 `.codex`，也识别环境变量 `CODEX_HOME`。程序无需 API Key。

## 用量口径与已知限制

- **显示位置**是独立置顶窗口。它无法嵌入 Codex 输入框，也没有账户订阅剩余额度、账单或跨设备统计。
- **自动跟随**依据本机会话日志的更新时间。若只在 Codex 切换到另一条对话而它没有写日志，浮窗可能继续显示之前的对话。粘贴本地会话链接可精确锁定。
- **上下文占用**约等于最近一次日志记录的用量除以该会话记录的有效窗口。压缩上下文后，读数会随日志更新。分母随会话和模型配置变化。
- **缓存命中率**是累计缓存输入除以累计输入。缓存写入显示日志报告值。缺失的写入计数可能由 Codex 记为零，因此零值无法说明服务端是否写过缓存。
- **累计用量**直接取选中日志最新的有效快照，包含重复发送的缓存输入。它不合并同一会话的多个日志文件，也不代表账单。老版本日志、云端会话或将来变动的日志格式可能缺少可用记录。
- **插件入口**由 Codex 选择工具。应用更新后，旧聊天可能要重启 Codex 才会加载新的工具清单。项目尚未验证跨版本和多显示器兼容性。

## 可选 Codex 插件

压缩包里的 `install-companion.ps1` 会把浮窗复制到当前用户的 `LocalAppData`，并添加「Codex Usage Bar」开始菜单入口。它不修改开机启动。

在解压目录打开 PowerShell，执行以下命令。需要已安装支持本地市场的 Codex CLI。

```powershell
powershell.exe -NoProfile -File .\install-companion.ps1
codex plugin marketplace add .\marketplace
codex plugin add codex-usage@local-usage-tools
codex plugin add codex-usage-report@local-usage-tools
```

重启 Codex 桌面应用后，可以用 `@Codex 用量` 打开浮窗，或用 `@Codex 用量查询` 查看本机最近活动会话的数字。查询结果由本地 MCP 服务返回给 Codex。需要移除插件时，用 `codex plugin remove` 分别卸载，再用 `codex plugin marketplace remove local-usage-tools` 移除本地市场入口。

## 从源码构建

在 Windows 运行 `powershell.exe -NoProfile -File .\build.ps1`。构建脚本使用本机 .NET Framework C# 编译器。输出到 `out/`，包括独立 EXE 和发行 ZIP；本地插件需要的桥接程序也会生成在市场目录中。运行时无需 Visual Studio、Node.js 或 npm。

## 灵感、技术参考与后续对比

| 项目或资料 | 与本项目的关系 |
| --- | --- |
| [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) | 界面灵感来自 DSH 底栏的用量、缓存和上下文提示。 |
| [OpenAI Codex](https://github.com/openai/codex) 与 [Codex 插件文档](https://developers.openai.com/plugins/build/plugins) | 用于核对本地用量字段、有效上下文窗口和本地市场打包方式。 |
| [Codex Desktop Usage Monitor for Windows](https://github.com/JiaYang-BUAA/Codex-Desktop-Usage-Monitor-Windows) | 本项目完成后用于对比交互和功能覆盖；本项目的 WPF 浮窗和日志读取为独立实现。 |

参见 [CHANGELOG](CHANGELOG.md) 了解本预览版的范围。
