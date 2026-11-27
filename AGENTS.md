# 待办笔记（WorkDesk）开发记忆

## 项目定位与开发入口

待办笔记是 Windows 本地应用，用于管理待办及截止提醒、记录日报和周报、导出工作内容。应用使用 .NET Framework 4.8 和 WPF，托盘使用 WinForms。可在 [README](./README.md) 查看当前用法和发布版本，在 [需求](./docs/requirements.md) 和 [架构设计](./docs/design.md) 查看初版方案。维护后续功能时以当前源码和测试为准。

- [build.ps1](./build.ps1) 直接调用 Windows 自带的 `csc.exe` 编译 `src/*.cs`。保持 C# 5 兼容，新增源码使用 UTF-8，沿用现有依赖。
- [Program.cs](./src/Program.cs) 是启动入口，负责加载 XAML、单实例和 `--tray` 启动。[DesktopHost.cs](./src/DesktopHost.cs) 负责托盘、自动启动、提醒调度和弹窗。
- [WorkState.cs](./src/WorkState.cs) 管理事项、总结、筛选和提醒判断。[StateStore.cs](./src/StateStore.cs) 负责本地持久化、数据目录和备份文件。[MainWindow.cs](./src/MainWindow.cs) 负责事项界面和状态提交，[Pages.cs](./src/Pages.cs) 负责导航、总结与设置，[DataStoragePage.cs](./src/DataStoragePage.cs) 提供目录迁移和备份设置。
- [Shell.xaml](./src/Shell.xaml) 定义主界面，[Theme.xaml](./src/Theme.xaml) 管理样式。[Editors.cs](./src/Editors.cs) 包含事项编辑和日期时间控件，[StatusTone.cs](./src/StatusTone.cs) 定义事项和提醒状态的配色。

## 数据与时间约定

- 默认数据目录为 `%LOCALAPPDATA%\WorkDesk`。相同目录下的 `data-directory.txt` 记录用户选用的位置。迁移校验成功后再切换位置，原目录保留。自定义目录缺失必须提示，避免把不可用的目录当成空数据。测试使用独立数据目录和样例。
- 正在使用的主文件为版本 `2`。归档事项按月分为多个独立片段，总结索引按需读取正文，图片单独存成 PNG。新版读取版本 `1` 的完整 JSON，首次保存再拆分。完整 JSON 导出仍使用版本 `1` 并包含历史和附件，可以单文件恢复。
- 更新走 `state.Copy()` → 修改候选状态 → `MainWindow.Commit(next)`。保存成功后再替换界面状态。`Copy()` 深复制事项，仍共享总结对象、附件、提醒设置和外观设置。修改总结用 `SetSummary`，修改设置新建对象，避免候选状态的修改提前影响当前状态。
- `StateStore.Save` 先写历史片段、总结和图片，再原子替换主文件，保留 `state.json.bak`。引用到的历史文件应能供旧快照和后台完整备份读取。从备份恢复后的第一次保存要保留有效备份。导出沿用 `StateStore.WriteExport`，保护应用使用的数据文件。
- 自动备份沿用桌面计时器，在后台生成完整 JSON，文件在 `backups` 子目录。周期按上次成功备份的本地日期计算，失败可重试，未完成的临时文件不计入成功记录。
- 事项截止、完成以及总结更新时间以 UTC 存储，显示和日期判断使用本地时间。日报按本地日期归档，周报按周一归档，键为 `(Weekly, Period)`，格式 `yyyy-MM-dd`。验证时间相关改动时覆盖截止边界、本地跨日和跨周。

## 托盘、提醒与事项列表

- 关闭主窗口会隐藏到托盘，计时器继续运行。由托盘「退出待办笔记」结束进程，退出前要处理未保存的总结。改显示名称时沿用 `WorkDesk` 的数据路径、单实例标识和启动项名称，避免升级后出现两份数据或重复启动项。开机启动项记录 EXE 路径，移动 EXE 后检查注册是否指向正确位置。
- `DesktopHost.Tick` 先计算本轮提醒，提交已提醒标记，再显示弹窗。保存失败留到下一轮重试。改截止时间或提前量、恢复为待办可以重置事项提醒，编辑标题和备注应保留原标记。事项可在休眠或重启后补提醒，日报和周报在对应的本地日期或星期触发，已保存的对应总结跳过提醒。
- 「今日到期」和「已逾期」可包含同一项。「今日完成」统计当天，点击后打开全部「已完成」列表。待办按截止时间排序，无截止事项放末尾，已完成按完成时间倒序。改卡片样式时检查状态文字、颜色和筛选联动。

## 编辑器、图片与导出

- [SummaryEditor.cs](./src/SummaryEditor.cs) 在 `RichTextBox` 中插入图片，正文保存 `workdesk-image:<id>` 标记。`Images` 关联 PNG 文件，完整 JSON 备份内嵌 PNG。`ReadMarkdown` 读取当前正文及其中实际使用的图片。改编辑器时验证光标处粘贴、删除和撤销图片、保存后重开历史记录。
- 缩略图呈白块时先查像素。部分 Windows 截图的 RGB 有内容，Alpha 却全为零。[SummaryImageCodec.cs](./src/SummaryImageCodec.cs) 在缩放前修复匹配这一特征的截图。旧附件加载时也尝试修复。混合透明度应保留，回归测试应检查 PNG 的像素和透明度。
- [MarkdownExport.cs](./src/MarkdownExport.cs) 负责转换与写出。「复制 Markdown」和「导出当前篇」取当前编辑内容，可以包含未保存的修改。「勾选导出」由 [SummaryExportDialog.cs](./src/SummaryExportDialog.cs) 选择已保存的日报或周报。
- Markdown 文件和剪贴板 Markdown 都使用 `data:image/png;base64,` 内嵌 PNG，复制后无需原目录。部分 Markdown 阅读器会限制 `data:` 图片。单文件 HTML 可在浏览器离线查看，正文按文本转义后显示。ZIP 内的 Markdown 通过相对路径引用同包 PNG，完整解压后可用。
- 带图导出修复需覆盖当前篇、勾选多篇或全选，以及复制到外部工具。把输出放到独立目录，检查 Markdown 或 HTML 真实渲染且图片有像素内容，另检查 ZIP 中每个相对路径都能找到 PNG。记录用过的阅读器和仍待验证的兼容性。
- 切换页面、日期、总结类型或退出时，走 `CanLeaveSummary` 和 [SummaryLeaveDialog.cs](./src/SummaryLeaveDialog.cs) 的保存、放弃、取消流程。取消或保存失败时保留草稿。窗口缩放由 `SizeSummaryLayout` 跟随可用高度，内层列表滚到边缘后由 `ContinueScrollAtEdge` 接续外层滚动。改布局或字体时检查最大化、F11／Esc、长列表滚动和界面／编辑器字号独立生效。

## 自测、打包与发布

在仓库根目录用 PowerShell 执行相关检查。

```powershell
.\build.ps1
.\test.ps1
```

- 构建产出 `dist/WorkDesk.exe`。[StateChecks.cs](./tests/StateChecks.cs) 覆盖状态、存储、时间和导出。[DesktopChecks.cs](./tests/DesktopChecks.cs) 覆盖 WPF 与桌面交互。可用 `./build.ps1 -DesktopChecks` 单独构建 `dist/DesktopChecks.exe`。
- XAML 作为资源随 EXE 打包，启动时由 `XamlReader.Load` 解析。修改 XAML、资源键、控件名称或模板后，编译通过仍需跑桌面检查。涉及布局、托盘、剪贴板或导出时，再用样例走一遍对应入口，记录实际看到的结果。
- 发布新版时同步更新 `Program.cs` 中两个版本号及 [WorkDesk.manifest](./src/WorkDesk.manifest) 中的版本号，用 `release/WorkDesk-vX.Y.Z.exe` 命名成品，更新 README 下载链接及实际文件的 SHA-256。在 [CHANGELOG.md](./CHANGELOG.md) 记录相对上一版的用户可见改动和升级要点。`dist/` 是本地构建目录，用户数据和真实工作截图留在本地。
- 开工确认仓库和当前分支。每次提交写明改动和原因，只纳入与当前里程碑相关的内容。运行有关检查后，按用户授权的目标正常推送并逐一核对结果。

新回归查明后，把复现条件、应保留的行为和验证方式补到对应小节。
