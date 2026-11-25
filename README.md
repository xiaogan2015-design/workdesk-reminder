# workdesk-reminder

「工笺」「一款轻量的 Windows 桌面工作台，支持待办事项、截止提醒、完成时间记录，以及日报和周报」

## 下载

[下载 WorkDesk.exe](https://github.com/xiaogan2015-design/workdesk-reminder/raw/main/release/WorkDesk.exe)

v1.0.0 · Windows 10／11 · .NET Framework 4.8 或 4.8.1。下载后可直接打开。

SHA-256：`50b00b2d0a4eff3eb3f8c98dfdf0cb818cba666557023b0848055386fe3be021`

## 使用工笺

- **工作台**点「新建事项」并设置可选截止时间。勾选完成会自动记录时间，可在「编辑」里补记或修正。清单可以复制或导出为 Markdown。
- **工作总结**按日期写日报、按自然周写周报。单篇总结可复制或导出 Markdown。日报默认每天 17:00 提醒，周报可在设置中开启。
- **提醒设置**可调整排程、开启登录启动或导出全部备份。关闭窗口会回到托盘。彻底退出时，从托盘菜单选择「退出工笺」。

### 本地数据

已保存的记录位于 `%LOCALAPPDATA%\WorkDesk\state.json`。设置页支持导出完整 JSON 备份或便于阅读和分享的 Markdown。导出的完整备份包含事项、总结、提醒设置和提示记录。

手动恢复完整 JSON 备份时，先退出工笺并另存现有数据文件，再把选定的备份复制为 `%LOCALAPPDATA%\WorkDesk\state.json` 并重新打开工笺。

## 构建和自测

在 Windows PowerShell 中运行：

```powershell
.\build.ps1
.\test.ps1
```

构建产物为 `dist/WorkDesk.exe`。自测在隔离目录里运行，覆盖事项保存、完成时间、总结、定时提醒、托盘和导出。

## 设计文档

- [需求分析与验收](docs/requirements.md)
- [应用架构与开发计划](docs/design.md)
