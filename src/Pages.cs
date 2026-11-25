using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace WorkDesk
{
    internal sealed partial class MainWindow
    {
        private enum DeskPage { Desk, Summary, Settings }
        private static readonly string[] Weekdays = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        private DeskPage page;
        private DateTime summaryDate;
        private DateTime summaryAnchor;
        private bool summaryWeekly;
        private string savedSummary;
        private SummaryEditor summaryInput;
        private StackPanel historyRows;

        private void InitializePages()
        {
            var desk = Element<Button>("DeskNav");
            var summary = Element<Button>("SummaryNav");
            var settings = Element<Button>("SettingsNav");
            AutomationProperties.SetName(desk, "工作台");
            AutomationProperties.SetName(summary, "工作总结");
            AutomationProperties.SetName(settings, "提醒设置");
            desk.Click += delegate { OpenWorkbench(false); };
            summary.Click += delegate { if (page != DeskPage.Summary) OpenSummary(DateTime.Today, false); };
            settings.Click += delegate { OpenSettings(); };
            Element<Button>("TodaySummary").Click += delegate { OpenSummary(DateTime.Today, false); };
            Element<Button>("OpenSettings").Click += delegate { OpenSettings(); };
            Element<Button>("CopyPlan").Click += delegate { CopyMarkdown(MarkdownExport.Plan(state, DateTimeOffset.Now)); };
            Element<Button>("ExportPlan").Click += delegate {
                ExportMarkdown("事项清单-" + WorkState.PeriodKey(DateTime.Today, false), MarkdownExport.Plan(state, DateTimeOffset.Now));
            };
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

        private void OpenSettings()
        {
            if (page == DeskPage.Settings || !CanLeaveSummary()) return;
            SetPage(DeskPage.Settings, "提醒设置", BuildSettingsPage());
        }

        private void SetPage(DeskPage next, string title, UIElement content)
        {
            page = next;
            Element<TextBlock>("PageTitle").Text = title;
            Element<Button>("DeskNav").Style = Ui.Style(next == DeskPage.Desk ? "ActiveNav" : "Nav");
            Element<Button>("SummaryNav").Style = Ui.Style(next == DeskPage.Summary ? "ActiveNav" : "Nav");
            Element<Button>("SettingsNav").Style = Ui.Style(next == DeskPage.Settings ? "ActiveNav" : "Nav");
            Element<StackPanel>("Dashboard").Visibility = next == DeskPage.Desk ? Visibility.Visible : Visibility.Collapsed;
            var other = Element<ContentControl>("OtherPage");
            other.Content = content;
            other.Visibility = content == null ? Visibility.Collapsed : Visibility.Visible;
            Element<ScrollViewer>("PageScroll").ScrollToTop();
            if (!store.Recovered) Banner("");
        }

        internal bool HasUnsavedSummary
        {
            get { return page == DeskPage.Summary && summaryInput.ReadMarkdown() != savedSummary; }
        }

        internal bool CanLeaveSummary()
        {
            if (!HasUnsavedSummary) return true;
            var decision = MessageBox.Show(this, "这篇总结有未保存的修改。离开前要保存吗？", "工作总结",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            return decision == MessageBoxResult.No || (decision == MessageBoxResult.Yes && SaveSummary());
        }

        private UIElement BuildSummaryPage()
        {
            var section = new Grid();
            section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.15, GridUnitType.Star) });
            section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 181 });
            var form = new StackPanel();
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            var daily = Ui.Button("日报", delegate { OpenSummary(summaryAnchor, false); }, summaryWeekly ? null : "Quiet");
            var weekly = Ui.Button("周报", delegate { OpenSummary(summaryAnchor, true); }, summaryWeekly ? "Quiet" : null);
            daily.Margin = new Thickness(0, 0, 7, 0);
            tabs.Children.Add(daily);
            tabs.Children.Add(weekly);
            form.Children.Add(tabs);

            var period = Ui.Text(MarkdownExport.SummaryTitle(summaryWeekly, summaryDate), "SectionTitle");
            period.TextWrapping = TextWrapping.Wrap;
            form.Children.Add(period);
            var date = new DatePicker {
                SelectedDate = summaryAnchor, Width = 154, HorizontalAlignment = HorizontalAlignment.Left,
                DisplayDateStart = new DateTime(1900, 1, 1), DisplayDateEnd = new DateTime(9998, 12, 31)
            };
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
            Ui.Label(form, summaryWeekly ? "选择周内任一天" : "总结日期", date);

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
            form.Children.Add(toolbar);
            form.Children.Add(summaryInput);
            var actions = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
            var save = Ui.Button("保存总结", delegate { SaveSummary(); }, "Primary");
            var copy = Ui.Button("复制 Markdown", delegate {
                var images = new List<SummaryImage>();
                string text = summaryInput.ReadMarkdown(images);
                if (String.IsNullOrWhiteSpace(text)) { Banner("请先填写总结正文或粘贴图片。"); return; }
                CopyMarkdown(MarkdownExport.WithImages(MarkdownExport.Summary(summaryWeekly, summaryDate, text), SummaryFilename(), images),
                    images.Count == 0 ? "已复制 Markdown。" : "已复制 Markdown。分享时请将导出的图片目录与正文放在一起。");
            });
            var export = Ui.Button("导出 Markdown", delegate {
                var images = new List<SummaryImage>();
                string text = summaryInput.ReadMarkdown(images);
                if (String.IsNullOrWhiteSpace(text)) { Banner("请先填写总结正文或粘贴图片。"); return; }
                string markdown = MarkdownExport.Summary(summaryWeekly, summaryDate, text);
                ExportFile(SummaryFilename(), "Markdown 文件 (*.md)|*.md", dialog => MarkdownExport.WriteWithImages(store, dialog.FileName, markdown, images));
            });
            foreach (var button in new[] { save, copy, export })
            {
                button.Margin = new Thickness(0, 0, 6, 5);
                actions.Children.Add(button);
            }
            form.Children.Add(actions);
            form.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    SaveSummary();
                }
            };
            section.Children.Add(Ui.Card(form));

            var history = new StackPanel();
            history.Children.Add(Ui.Text(summaryWeekly ? "周报历史" : "日报历史", "SectionTitle"));
            historyRows = new StackPanel();
            history.Children.Add(new ScrollViewer { Content = historyRows, MaxHeight = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 9, 0, 0) });
            var historyCard = Ui.Card(history);
            historyCard.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(historyCard, 2);
            section.Children.Add(historyCard);
            RefreshHistory();
            return section;
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
            var panels = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
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
            panels.Children.Add(Ui.Card(schedules));

            var local = new StackPanel();
            local.Children.Add(Ui.Text("托盘与本地数据", "SectionTitle"));
            var startup = new CheckBox { Content = "登录 Windows 后启动工笺", Margin = new Thickness(0, 11, 0, 0), Style = Ui.Style("FormToggle") };
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
            var hint = Ui.Text("关闭窗口后，工笺会留在系统托盘。备份文件可以另存到你选择的位置。", "Hint");
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
            ExportFile("工笺备份-" + WorkState.PeriodKey(DateTime.Today, false),
                "完整数据备份 (*.json)|*.json|可阅读的 Markdown (*.md)|*.md", dialog => {
                    if (dialog.FilterIndex == 1) store.BackUp(dialog.FileName, state);
                    else MarkdownExport.WriteWithImages(store, dialog.FileName, MarkdownExport.Archive(state, DateTimeOffset.Now),
                        state.Summaries.Where(summary => summary.Images != null).SelectMany(summary => summary.Images));
                });
        }

        private void ExportFile(string filename, string formats, Action<SaveFileDialog> write)
        {
            var dialog = new SaveFileDialog { Title = "保存到本机", FileName = filename, Filter = formats, AddExtension = true };
            try
            {
                if (dialog.ShowDialog(this) != true) return;
                write(dialog);
                Banner("文件已导出。\n" + dialog.FileName);
            }
            catch (Exception ex) { Banner("导出失败：" + ex.Message); }
        }
    }
}
