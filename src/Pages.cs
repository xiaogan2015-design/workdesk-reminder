using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace WorkDesk
{
    internal sealed partial class MainWindow
    {
        private const string ReportFormats = "Markdown（内嵌图片） (*.md)|*.md|网页（内嵌图片） (*.html)|*.html|Markdown 和图片压缩包 (*.zip)|*.zip";
        private const string DefaultFontChoice = "系统默认字体";
        private enum DeskPage { Desk, Archive, Summary, Settings }
        private static readonly string[] Weekdays = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        private DeskPage page;
        private StackPanel archiveRows;
        private TextBlock archiveCount;
        private Border archiveSettingsCard;
        private DateTime summaryDate;
        private DateTime summaryAnchor;
        private bool summaryWeekly;
        private string savedSummary;
        private SummaryEditor summaryInput;
        private StackPanel historyRows;
        private DatePicker summaryPicker;
        private Button fullScreenButton;
        private Button selectExportButton;
        private Grid summaryLayout;
        private bool summaryFullScreen;
        private WindowState windowedState;
        private WindowStyle windowedStyle;
        private ResizeMode windowedResizeMode;
        private GridLength sidebarWidth;

        private void InitializePages()
        {
            var desk = Element<Button>("DeskNav");
            var archive = Element<Button>("ArchiveNav");
            var summary = Element<Button>("SummaryNav");
            var settings = Element<Button>("SettingsNav");
            AutomationProperties.SetName(desk, "工作台");
            AutomationProperties.SetName(archive, "事项归档");
            AutomationProperties.SetName(summary, "工作总结");
            AutomationProperties.SetName(settings, "提醒设置");
            desk.Click += delegate { OpenWorkbench(false); };
            archive.Click += delegate { OpenArchive(); };
            summary.Click += delegate { if (page != DeskPage.Summary) OpenSummary(DateTime.Today, false); };
            settings.Click += delegate { OpenSettings(); };
            Element<Button>("TodaySummary").Click += delegate { OpenSummary(DateTime.Today, false); };
            Element<Button>("OpenSettings").Click += delegate { OpenSettings(); };
            Element<Button>("CopyPlan").Click += delegate { CopyMarkdown(MarkdownExport.Plan(state, DateTimeOffset.Now)); };
            Element<Button>("ExportPlan").Click += delegate {
                ExportMarkdown("事项清单-" + WorkState.PeriodKey(DateTime.Today, false), MarkdownExport.Plan(state, DateTimeOffset.Now));
            };
            var pageScroll = Element<ScrollViewer>("PageScroll");
            pageScroll.ScrollChanged += delegate(object sender, ScrollChangedEventArgs e) {
                if (!ReferenceEquals(e.OriginalSource, pageScroll) || e.ViewportHeightChange == 0) return;
                Element<ScrollViewer>("TaskListScroll").MaxHeight = Math.Max(210, pageScroll.ViewportHeight * 0.56);
                if (page == DeskPage.Summary) SizeSummaryLayout();
            };
            pageScroll.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) { ContinueScrollAtEdge(pageScroll, e); };
            Element<DockPanel>("PageHeader").SizeChanged += delegate { if (page == DeskPage.Summary) SizeSummaryLayout(); };
        }

        private static void ContinueScrollAtEdge(ScrollViewer page, MouseWheelEventArgs e)
        {
            DependencyObject node = e.OriginalSource as DependencyObject;
            while (node != null && !ReferenceEquals(node, page))
            {
                var inner = node as ScrollViewer;
                if (inner != null)
                {
                    bool canScroll = e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight - 0.5 : inner.VerticalOffset > 0.5;
                    if (!canScroll)
                    {
                        page.ScrollToVerticalOffset(page.VerticalOffset - e.Delta / 3.0);
                        e.Handled = true;
                    }
                    return;
                }
                var text = node as FrameworkContentElement;
                node = text != null ? text.Parent : (node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node));
            }
        }

        private void RefreshSideCards(DateTime today)
        {
            bool dailySaved = state.FindSummary(today, false) != null;
            Element<TextBlock>("SummaryStatus").Text = dailySaved ? "今日日报已保存。" : "今日日报尚未记录。";
            Element<Button>("TodaySummary").Content = dailySaved ? "查看今日日报  →" : "写今日日报  →";
            var settings = state.Settings;
            string daily = settings.DailyEnabled ? "日报  每天 " + Ui.Clock(settings.DailyMinute) : "日报  已关闭";
            string weekly = settings.WeeklyEnabled ? "周报  " + Weekdays[(int)settings.WeeklyDay] + " " + Ui.Clock(settings.WeeklyMinute) : "周报  已关闭";
            Element<TextBlock>("ScheduleStatus").Text = daily + "\n" + weekly;
        }

        internal bool OpenWorkbench(bool pending)
        {
            if (taskEditor != null || !CanLeaveSummary()) return false;
            if (pending) filter = TaskFilter.Pending;
            if (page != DeskPage.Desk) SetPage(DeskPage.Desk, "工作台", null);
            RefreshDashboard();
            return true;
        }

        internal bool OpenSummary(DateTime date, bool weekly)
        {
            if (taskEditor != null) return false;
            DateTime period = weekly ? WorkState.WeekStart(date) : date.Date;
            if (page == DeskPage.Summary && summaryDate == period && summaryWeekly == weekly)
            {
                summaryAnchor = date.Date;
                return true;
            }
            if (!CanLeaveSummary()) return false;
            summaryDate = period;
            summaryAnchor = date.Date;
            summaryWeekly = weekly;
            SetPage(DeskPage.Summary, "工作总结", BuildSummaryPage());
            return true;
        }

        private void OpenArchive()
        {
            if (page == DeskPage.Archive || !CanLeaveSummary()) return;
            var content = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 13) };
            var settings = Ui.Button("归档设置", delegate { OpenSettings(true); });
            DockPanel.SetDock(settings, Dock.Right);
            header.Children.Add(settings);
            archiveCount = Ui.Text("", "SectionTitle");
            archiveCount.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(archiveCount);
            content.Children.Add(header);
            archiveRows = new StackPanel();
            content.Children.Add(archiveRows);
            RefreshArchivedTasks();
            SetPage(DeskPage.Archive, "事项归档", Ui.Card(content));
        }

        private void RefreshArchivedTasks()
        {
            var now = DateTimeOffset.Now;
            var items = state.FilterTasks(TaskFilter.Archived, now).ToArray();
            archiveCount.Text = "已归档 · " + items.Length.ToString(Chinese) + " 项";
            archiveRows.Children.Clear();
            if (items.Length == 0) archiveRows.Children.Add(Ui.Text("暂无归档事项。可在归档设置中启用自动归档。", "Hint"));
            foreach (var item in items) archiveRows.Children.Add(TaskRow(item, now));
        }

        internal void RefreshTaskViews()
        {
            if (page == DeskPage.Archive) RefreshArchivedTasks();
            if (page == DeskPage.Desk) RefreshDashboard();
        }

        private void OpenSettings(bool showArchiveSettings = false)
        {
            if (page == DeskPage.Settings || !CanLeaveSummary()) return;
            SetPage(DeskPage.Settings, "提醒设置", BuildSettingsPage());
            if (showArchiveSettings)
            {
                UpdateLayout();
                archiveSettingsCard.BringIntoView();
            }
        }

        private void SetPage(DeskPage next, string title, UIElement content)
        {
            if (next != DeskPage.Summary && summaryFullScreen) SetSummaryFullScreen(false);
            page = next;
            Element<TextBlock>("PageTitle").Text = title;
            Element<Button>("DeskNav").Style = Ui.Style(next == DeskPage.Desk ? "ActiveNav" : "Nav");
            Element<Button>("ArchiveNav").Style = Ui.Style(next == DeskPage.Archive ? "ActiveNav" : "Nav");
            Element<Button>("SummaryNav").Style = Ui.Style(next == DeskPage.Summary ? "ActiveNav" : "Nav");
            Element<Button>("SettingsNav").Style = Ui.Style(next == DeskPage.Settings ? "ActiveNav" : "Nav");
            Element<StackPanel>("Dashboard").Visibility = next == DeskPage.Desk ? Visibility.Visible : Visibility.Collapsed;
            var other = Element<ContentControl>("OtherPage");
            other.Content = content;
            other.Visibility = content == null ? Visibility.Collapsed : Visibility.Visible;
            Element<ScrollViewer>("PageScroll").ScrollToTop();
            if (!store.Recovered) Banner("");
            if (next == DeskPage.Summary) SizeSummaryLayout();
        }

        private void SetSummaryFullScreen(bool active)
        {
            if (summaryFullScreen == active || page != DeskPage.Summary) return;
            summaryFullScreen = active;
            if (active)
            {
                windowedState = WindowState;
                windowedStyle = WindowStyle;
                windowedResizeMode = ResizeMode;
                sidebarWidth = shell.ColumnDefinitions[0].Width;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                shell.ColumnDefinitions[0].Width = new GridLength(0);
            }
            else
            {
                WindowStyle = windowedStyle;
                ResizeMode = windowedResizeMode;
                WindowState = windowedState;
                shell.ColumnDefinitions[0].Width = sidebarWidth;
            }
            var visibility = active ? Visibility.Collapsed : Visibility.Visible;
            Element<Border>("Sidebar").Visibility = visibility;
            Element<DockPanel>("PageHeader").Visibility = visibility;
            Element<ScrollViewer>("PageScroll").ScrollToTop();
            SizeSummaryLayout();
        }

        private void SizeSummaryLayout()
        {
            fullScreenButton.Content = summaryFullScreen ? "退出全屏" : "全屏";
            summaryLayout.ColumnDefinitions[2].Width = summaryFullScreen ? new GridLength(295) : new GridLength(1, GridUnitType.Star);
            var margin = Element<StackPanel>("PageContent").Margin;
            var header = Element<DockPanel>("PageHeader");
            double headerHeight = summaryFullScreen ? 0 : header.ActualHeight + header.Margin.Top + header.Margin.Bottom;
            var viewport = Element<ScrollViewer>("PageScroll").ViewportHeight;
            double minimum = 535 + Math.Max(0, state.Appearance.InterfaceSize - AppearanceSettings.DefaultSize) * 10;
            summaryLayout.Height = Math.Max(minimum, viewport - margin.Top - margin.Bottom - headerHeight);
        }

        internal bool HasUnsavedSummary
        {
            get { return page == DeskPage.Summary && summaryInput.ReadMarkdown() != savedSummary; }
        }

        internal bool CanLeaveSummary()
        {
            if (!HasUnsavedSummary) return true;
            var dialog = new SummaryLeaveDialog(this);
            if (dialog.ShowDialog() != true) return false;
            return dialog.Choice == SummaryDeparture.Discard || (dialog.Choice == SummaryDeparture.Save && SaveSummary());
        }

        private UIElement BuildSummaryPage()
        {
            summaryLayout = new Grid();
            summaryLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.15, GridUnitType.Star) });
            summaryLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            summaryLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 181, MaxWidth = 340 });
            var form = new DockPanel();
            var editorHeader = new StackPanel();
            var tabBar = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
            fullScreenButton = Ui.Button("全屏", delegate { SetSummaryFullScreen(!summaryFullScreen); });
            fullScreenButton.ToolTip = "F11 切换全屏 · Esc 退出全屏";
            DockPanel.SetDock(fullScreenButton, Dock.Right);
            tabBar.Children.Add(fullScreenButton);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            var daily = Ui.Button("日报", delegate { OpenSummary(summaryAnchor, false); }, summaryWeekly ? null : "Quiet");
            var weekly = Ui.Button("周报", delegate { OpenSummary(summaryAnchor, true); }, summaryWeekly ? "Quiet" : null);
            daily.Margin = new Thickness(0, 0, 7, 0);
            tabs.Children.Add(daily);
            tabs.Children.Add(weekly);
            tabBar.Children.Add(tabs);
            editorHeader.Children.Add(tabBar);

            var period = Ui.Text(MarkdownExport.SummaryTitle(summaryWeekly, summaryDate), "SectionTitle");
            period.TextWrapping = TextWrapping.Wrap;
            editorHeader.Children.Add(period);
            var date = new DatePicker {
                SelectedDate = summaryAnchor, Width = 154, HorizontalAlignment = HorizontalAlignment.Left,
                DisplayDateStart = new DateTime(1900, 1, 1), DisplayDateEnd = new DateTime(9998, 12, 31)
            };
            summaryPicker = date;
            bool restoringDate = false;
            date.SelectedDateChanged += delegate {
                if (restoringDate) return;
                if (!date.SelectedDate.HasValue || !OpenSummary(date.SelectedDate.Value, summaryWeekly))
                {
                    restoringDate = true;
                    date.SelectedDate = summaryAnchor;
                    restoringDate = false;
                }
            };
            Ui.Label(editorHeader, summaryWeekly ? "选择周内任一天" : "总结日期", date);

            var prior = state.FindSummary(summaryDate, summaryWeekly);
            summaryInput = new SummaryEditor(prior, Banner) { Style = Ui.Style("SummarySurface") };
            savedSummary = summaryInput.ReadMarkdown();
            var toolbar = new DockPanel { Margin = new Thickness(0, 16, 0, 6) };
            var paste = Ui.Button("粘贴图片", delegate { summaryInput.PasteImage(true); }, "Quiet");
            paste.ToolTip = "也可以在正文中按 Ctrl+V 粘贴剪贴板截图";
            paste.Padding = new Thickness(10, 5, 10, 5);
            DockPanel.SetDock(paste, Dock.Right);
            toolbar.Children.Add(paste);
            toolbar.Children.Add(new TextBlock { Text = "总结正文", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            AutomationProperties.SetName(summaryInput, "总结正文");
            editorHeader.Children.Add(toolbar);
            DockPanel.SetDock(editorHeader, Dock.Top);
            form.Children.Add(editorHeader);
            var actions = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
            var save = Ui.Button("保存总结", delegate { SaveSummary(); }, "Primary");
            var copy = Ui.Button("复制 Markdown", delegate {
                var images = new List<SummaryImage>();
                string text = summaryInput.ReadMarkdown(images);
                if (String.IsNullOrWhiteSpace(text)) { Banner("请先填写总结正文或粘贴图片。"); return; }
                CopyMarkdown(MarkdownExport.WithEmbeddedImages(MarkdownExport.Summary(summaryWeekly, summaryDate, text), images),
                    images.Count == 0 ? "已复制 Markdown。" : "已复制带图 Markdown。请在支持内嵌图片的工具中预览。");
            });
            var export = Ui.Button("导出当前篇", delegate {
                var images = new List<SummaryImage>();
                string text = summaryInput.ReadMarkdown(images);
                if (String.IsNullOrWhiteSpace(text)) { Banner("请先填写总结正文或粘贴图片。"); return; }
                string markdown = MarkdownExport.Summary(summaryWeekly, summaryDate, text);
                ExportFile(SummaryFilename(), ReportFormats, dialog => MarkdownExport.WriteWithImages(store, dialog.FileName, markdown, images));
            });
            export.ToolTip = "将当前编辑的日报或周报导出为 Markdown、网页或压缩包";
            foreach (var button in new[] { save, copy, export })
            {
                button.Margin = new Thickness(0, 0, 6, 5);
                actions.Children.Add(button);
            }
            DockPanel.SetDock(actions, Dock.Bottom);
            form.Children.Add(actions);
            form.Children.Add(summaryInput);
            form.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    SaveSummary();
                }
            };
            summaryLayout.Children.Add(Ui.Card(form));

            var history = new DockPanel();
            var historyHeader = new StackPanel();
            historyHeader.Children.Add(Ui.Text(summaryWeekly ? "周报历史" : "日报历史", "SectionTitle"));
            selectExportButton = Ui.Button("勾选导出", delegate { ExportSelectedSummaries(); });
            selectExportButton.HorizontalAlignment = HorizontalAlignment.Left;
            selectExportButton.Padding = new Thickness(9, 5, 9, 5);
            selectExportButton.Margin = new Thickness(0, 7, 0, 0);
            historyHeader.Children.Add(selectExportButton);
            DockPanel.SetDock(historyHeader, Dock.Top);
            history.Children.Add(historyHeader);
            historyRows = new StackPanel();
            history.Children.Add(new ScrollViewer { Content = historyRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 9, 0, 0) });
            var historyCard = Ui.Card(history);
            Grid.SetColumn(historyCard, 2);
            summaryLayout.Children.Add(historyCard);
            RefreshHistory();
            return summaryLayout;
        }

        private void ExportSelectedSummaries()
        {
            var dialog = new SummaryExportDialog(this, state.Summaries);
            if (dialog.ShowDialog() != true) return;
            var reports = dialog.SelectedReports;
            ExportFile("工作总结-" + WorkState.PeriodKey(DateTime.Today, false), ReportFormats,
                file => MarkdownExport.WriteSummaries(store, file.FileName, reports));
        }

        private string SummaryFilename() { return (summaryWeekly ? "周报-" : "日报-") + WorkState.PeriodKey(summaryDate, summaryWeekly); }

        private bool SaveSummary()
        {
            var images = new List<SummaryImage>();
            string value = summaryInput.ReadMarkdown(images);
            if (String.IsNullOrWhiteSpace(value)) { Banner("请先填写总结正文或粘贴图片。"); summaryInput.Focus(); return false; }
            if (value.Length > 40000) { Banner("总结正文最多可保存 40000 个字符。"); return false; }
            var next = state.Copy();
            next.SetSummary(summaryDate, summaryWeekly, value, DateTime.UtcNow, images);
            if (!Commit(next)) return false;
            savedSummary = value;
            Banner(summaryWeekly ? "周报已保存。" : "日报已保存。");
            RefreshHistory();
            return true;
        }

        private void RefreshHistory()
        {
            selectExportButton.IsEnabled = state.Summaries.Count != 0;
            historyRows.Children.Clear();
            foreach (var summary in state.Summaries.Where(s => s.Weekly == summaryWeekly).OrderByDescending(s => s.Period))
            {
                DateTime date = DateTime.ParseExact(summary.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var button = Ui.Button(summary.Period, delegate { OpenSummary(date, summaryWeekly); }, date == summaryDate ? "Quiet" : null);
                button.Margin = new Thickness(0, 0, 4, 6);
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                string preview = SummaryImage.Reference.Replace(summary.Text, "[截图]");
                button.ToolTip = preview.Length <= 180 ? preview : preview.Substring(0, 180) + "…";
                historyRows.Children.Add(button);
            }
            if (historyRows.Children.Count == 0) historyRows.Children.Add(Ui.Text("保存后可从这里回看。", "Hint"));
        }

        private UIElement BuildSettingsPage()
        {
            var panels = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            panels.Children.Add(BuildAppearanceCard());
            archiveSettingsCard = BuildArchiveSettingsCard();
            archiveSettingsCard.Margin = new Thickness(0, 12, 0, 0);
            panels.Children.Add(archiveSettingsCard);
            var schedules = new StackPanel();
            schedules.Children.Add(Ui.Text("总结提醒", "SectionTitle"));
            var settings = state.Settings;
            var daily = new CheckBox { Content = "每天提醒写日报", IsChecked = settings.DailyEnabled, Style = Ui.Style("FormToggle") };
            var weekly = new CheckBox { Content = "每周提醒写周报", IsChecked = settings.WeeklyEnabled, Style = Ui.Style("FormToggle") };
            var dailyTime = TimeBox(settings.DailyMinute);
            var weeklyTime = TimeBox(settings.WeeklyMinute);
            var weekday = new ComboBox { ItemsSource = Weekdays, SelectedIndex = (int)settings.WeeklyDay, Width = 122, HorizontalAlignment = HorizontalAlignment.Left };
            var dailyPanel = new StackPanel();
            dailyPanel.Children.Add(daily);
            Ui.Label(dailyPanel, "日报时间（24 小时制）", dailyTime);
            var weeklyPanel = new StackPanel();
            weeklyPanel.Children.Add(weekly);
            var weeklyFields = new WrapPanel();
            var weekdayField = new StackPanel { Margin = new Thickness(0, 0, 15, 0) };
            var weeklyTimeField = new StackPanel();
            Ui.Label(weekdayField, "周报星期", weekday);
            Ui.Label(weeklyTimeField, "周报时间（24 小时制）", weeklyTime);
            weeklyFields.Children.Add(weekdayField);
            weeklyFields.Children.Add(weeklyTimeField);
            weeklyPanel.Children.Add(weeklyFields);
            schedules.Children.Add(Ui.FormGroup(dailyPanel));
            schedules.Children.Add(Ui.FormGroup(weeklyPanel));
            var save = Ui.Button("保存提醒设置", delegate {
                TimeSpan dayClock, weekClock;
                bool validDaily = Ui.TryTime(dailyTime.Text, out dayClock);
                bool validWeekly = Ui.TryTime(weeklyTime.Text, out weekClock);
                if ((daily.IsChecked == true && !validDaily) || (weekly.IsChecked == true && (!validWeekly || weekday.SelectedIndex < 0)))
                {
                    Banner("请输入有效的提醒时间，例如 17:00，并选择周报星期。");
                    return;
                }
                var next = state.Copy();
                next.Settings = new ReminderSettings {
                    DailyEnabled = daily.IsChecked == true,
                    DailyMinute = validDaily ? (int)dayClock.TotalMinutes : state.Settings.DailyMinute,
                    WeeklyEnabled = weekly.IsChecked == true,
                    WeeklyMinute = validWeekly ? (int)weekClock.TotalMinutes : state.Settings.WeeklyMinute,
                    WeeklyDay = weekday.SelectedIndex >= 0 ? (DayOfWeek)weekday.SelectedIndex : state.Settings.WeeklyDay
                };
                if (Commit(next)) Banner("提醒设置已保存。");
            }, "Primary");
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 15, 0, 0);
            schedules.Children.Add(save);
            var reminderCard = Ui.Card(schedules);
            reminderCard.Margin = new Thickness(0, 12, 0, 0);
            panels.Children.Add(reminderCard);

            var local = new StackPanel();
            local.Children.Add(Ui.Text("托盘与本地数据", "SectionTitle"));
            var startup = new CheckBox { Content = "登录 Windows 后启动待办笔记", Margin = new Thickness(0, 11, 0, 0), Style = Ui.Style("FormToggle") };
            try { startup.IsChecked = StartupRegistration.Enabled; }
            catch (Exception ex) { startup.IsEnabled = false; startup.ToolTip = ex.Message; }
            bool reverting = false;
            RoutedEventHandler changeStartup = delegate {
                if (reverting) return;
                bool enabled = startup.IsChecked == true;
                try { StartupRegistration.SetEnabled(enabled); Banner(enabled ? "登录启动已开启。" : "登录启动已关闭。"); }
                catch (Exception ex)
                {
                    reverting = true;
                    startup.IsChecked = !enabled;
                    reverting = false;
                    Banner("无法更改登录启动：" + ex.Message);
                }
            };
            startup.Checked += changeStartup;
            startup.Unchecked += changeStartup;
            local.Children.Add(startup);
            var hint = Ui.Text("关闭窗口后，待办笔记会留在系统托盘。备份文件可以另存到你选择的位置。", "Hint");
            hint.Margin = new Thickness(0, 9, 0, 8);
            local.Children.Add(hint);
            var backup = Ui.Button("导出全部备份", delegate { ExportBackup(); });
            backup.HorizontalAlignment = HorizontalAlignment.Left;
            local.Children.Add(backup);
            var backupCard = Ui.Card(local);
            backupCard.Margin = new Thickness(0, 12, 0, 0);
            panels.Children.Add(backupCard);
            return panels;
        }

        private Border BuildArchiveSettingsCard()
        {
            var content = new StackPanel();
            content.Children.Add(Ui.Text("事项自动归档", "SectionTitle"));
            var hint = Ui.Text("分别设置保留天数。0 表示关闭，1 天按 24 小时计算。归档后可在「事项归档」中查看或恢复。", "Hint");
            hint.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(hint);

            var completed = new TextBox { Text = state.Archiving.CompletedDays.ToString(CultureInfo.InvariantCulture), Width = 82, MaxLength = 4,
                HorizontalAlignment = HorizontalAlignment.Left };
            var overdue = new TextBox { Text = state.Archiving.OverdueDays.ToString(CultureInfo.InvariantCulture), Width = 82, MaxLength = 4,
                HorizontalAlignment = HorizontalAlignment.Left };
            var completedField = new StackPanel { Margin = new Thickness(0, 0, 26, 0) };
            Ui.Label(completedField, "完成后归档（天）", completed);
            var overdueField = new StackPanel();
            Ui.Label(overdueField, "逾期后归档（天）", overdue);
            var fields = new WrapPanel();
            fields.Children.Add(completedField);
            fields.Children.Add(overdueField);
            content.Children.Add(Ui.FormGroup(fields));

            var save = Ui.Button("保存归档设置", delegate {
                int completedDays, overdueDays;
                if (!Int32.TryParse(completed.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out completedDays) ||
                    !Int32.TryParse(overdue.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out overdueDays) ||
                    !ArchiveSettings.IsValidDayCount(completedDays) || !ArchiveSettings.IsValidDayCount(overdueDays))
                {
                    Banner("请输入 0 到 " + ArchiveSettings.MaxDays + " 之间的整数天数。");
                    return;
                }
                var next = state.Copy();
                next.Archiving = new ArchiveSettings { CompletedDays = completedDays, OverdueDays = overdueDays };
                next.ArchiveAgedTasks(DateTime.UtcNow);
                if (Commit(next)) Banner("归档设置已保存，符合条件的事项已移至「事项归档」。");
            }, "Primary");
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 13, 0, 0);
            content.Children.Add(save);
            return Ui.Card(content);
        }

        private Border BuildAppearanceCard()
        {
            var content = new StackPanel();
            content.Children.Add(Ui.Text("字体与字号", "SectionTitle"));
            var appearance = state.Appearance;
            var fonts = new[] { DefaultFontChoice }.Concat(Ui.InstalledFonts()).ToArray();
            var interfaceFont = FontChoice(fonts, appearance.InterfaceFont);
            var editorFont = FontChoice(fonts, appearance.EditorFont);
            var interfaceSize = SizeChoice(appearance.InterfaceSize, AppearanceSettings.MaxInterfaceSize);
            var editorSize = SizeChoice(appearance.EditorSize, AppearanceSettings.MaxEditorSize);
            content.Children.Add(AppearanceGroup("界面字体", "界面字号", interfaceFont, interfaceSize));
            content.Children.Add(AppearanceGroup("编辑框字体", "编辑框字号", editorFont, editorSize));
            var save = Ui.Button("保存字体设置", delegate {
                var next = state.Copy();
                next.Appearance = new AppearanceSettings {
                    InterfaceFont = interfaceFont.SelectedIndex > 0 ? (string)interfaceFont.SelectedItem : null,
                    InterfaceSize = (int)interfaceSize.SelectedItem,
                    EditorFont = editorFont.SelectedIndex > 0 ? (string)editorFont.SelectedItem : null,
                    EditorSize = (int)editorSize.SelectedItem
                };
                if (!Commit(next)) return;
                Ui.ApplyTypography(state.Appearance);
                Banner("字体和字号已保存并应用。");
            }, "Primary");
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 13, 0, 0);
            content.Children.Add(save);
            return Ui.Card(content);
        }

        private static Border AppearanceGroup(string fontLabel, string sizeLabel, ComboBox font, ComboBox size)
        {
            var fields = new WrapPanel();
            var fontField = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var sizeField = new StackPanel();
            Ui.Label(fontField, fontLabel, font);
            Ui.Label(sizeField, sizeLabel, size);
            fields.Children.Add(fontField);
            fields.Children.Add(sizeField);
            return Ui.FormGroup(fields);
        }

        private static ComboBox FontChoice(string[] fonts, string selected)
        {
            int index = Array.FindIndex(fonts, name => String.Equals(name, selected, StringComparison.CurrentCultureIgnoreCase));
            return new ComboBox { Width = 215, ItemsSource = fonts, SelectedIndex = Math.Max(0, index), MaxDropDownHeight = 285 };
        }

        private static ComboBox SizeChoice(int selected, int maximum)
        {
            return new ComboBox {
                Width = 75, ItemsSource = Enumerable.Range(AppearanceSettings.MinSize, maximum - AppearanceSettings.MinSize + 1).ToArray(),
                SelectedItem = selected, MaxDropDownHeight = 285
            };
        }

        private static TextBox TimeBox(int minute)
        {
            return new TextBox { Text = Ui.Clock(minute), MaxLength = 5, Width = 79, HorizontalAlignment = HorizontalAlignment.Left };
        }

        private void CopyMarkdown(string data, string message = "已复制 Markdown。")
        {
            try { Clipboard.SetText(data, TextDataFormat.UnicodeText); Banner(message); }
            catch (Exception ex) { Banner("复制失败：" + ex.Message); }
        }

        private void ExportMarkdown(string filename, string data)
        {
            ExportFile(filename, "Markdown 文件 (*.md)|*.md", dialog => MarkdownExport.Write(store, dialog.FileName, data));
        }

        private void ExportBackup()
        {
            ExportFile("待办笔记备份-" + WorkState.PeriodKey(DateTime.Today, false),
                "完整数据备份 (*.json)|*.json|" + ReportFormats, dialog => {
                    if (System.IO.Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)) store.BackUp(dialog.FileName, state);
                    else MarkdownExport.WriteWithImages(store, dialog.FileName, MarkdownExport.Archive(state, DateTimeOffset.Now),
                        MarkdownExport.ReportImages(state.Summaries));
                });
        }

        private void ExportFile(string filename, string formats, Action<SaveFileDialog> write)
        {
            var dialog = new SaveFileDialog { Title = "保存到本机", FileName = filename, Filter = formats, AddExtension = true };
            try
            {
                if (dialog.ShowDialog(this) != true) return;
                write(dialog);
                string extension = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();
                string message = extension == ".zip" ? "压缩包已导出。解压后打开其中的 Markdown。" :
                    extension == ".html" ? "网页已导出。可以用浏览器打开。" : "文件已导出。";
                Banner(message + "\n" + dialog.FileName);
            }
            catch (Exception ex) { Banner("导出失败：" + ex.Message); }
        }
    }
}
