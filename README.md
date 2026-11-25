# workdesk-reminder

「工笺」「一款轻量的 Windows 桌面工作台，支持待办事项、截止提醒、完成时间记录，以及日报和周报」

## 下载

[下载 WorkDesk.exe](https://github.com/xiaogan2015-design/workdesk-reminder/raw/main/release/WorkDesk.exe)

v1.2.0 · Windows 10／11 · .NET Framework 4.8 或 4.8.1。更新前请先从托盘退出旧版，再打开新版。

SHA-256：`da0d99976c44411abfdac872bbbcad68dd7fc5e71a1f3fdb3ff33ba957cec9ff`

## 使用工笺

- **工作台**点「新建事项」并设置可选截止时间。勾选完成会自动记录时间，可在「编辑」里补记或修正。清单可以复制或导出为 Markdown。
- **工作总结**按日期写日报、按自然周写周报。在正文按 `Ctrl+V` 或点击「粘贴图片」，即可把截图插在光标处。右键图片可复制或移除。点「全屏」或按 `F11` 可展开编辑区，继续从右侧切换历史记录，按 `Esc` 退出。
- **导出总结**点「导出当前篇」保存当前一篇，或从历史区点「勾选导出」，把选中的已保存日报和周报合成一个 Markdown。导出带图的 Markdown 时，把 `.md` 和它引用的图片文件夹一并分享。
- **提醒设置**可调整排程、开启登录启动或导出全部备份。关闭窗口会回到托盘。彻底退出时，从托盘菜单选择「退出工笺」。

### 本地数据

已保存的记录位于 `%LOCALAPPDATA%\WorkDesk\state.json`。设置页支持导出完整 JSON 备份或便于阅读和分享的 Markdown。导出的完整备份包含事项、总结及图片、提醒设置和提示记录。

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
