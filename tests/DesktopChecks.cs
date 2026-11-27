using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WorkDesk
{
    internal static class DesktopChecks
    {
        private static int assertions;

        [STAThread]
        public static int Main()
        {
            var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkDesk.Desktop." + Guid.NewGuid().ToString("N"));
            var store = new StateStore(folder);
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Program.LoadTheme(app);
                Run(store);
                app.Shutdown();
                Console.WriteLine("Passed {0} desktop checks.", assertions);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                File.Delete(store.FilePath);
                File.Delete(store.FilePath + ".bak");
                File.Delete(store.FilePath + ".tmp");
                if (Directory.Exists(folder)) Directory.Delete(folder);
            }
        }

        private static void Run(StateStore store)
        {
            var window = new MainWindow(store, store.Load());
            window.Show();
            window.UpdateLayout();
            Check(window.Title == "待办笔记 · WorkDesk" && Descendants<TextBlock>(window).Any(block => block.Text == "待办笔记"),
                "The window title and sidebar show the current app name.");
            var sidebar = window.Element<Border>("Sidebar");
            Check(Descendants<Button>(sidebar).Select(AutomationProperties.GetName).SequenceEqual(new[] { "工作台", "工作总结", "事项归档", "设置" }),
                "The sidebar places the archive immediately above Settings.");
            var add = window.Element<Button>("NewTask");
            var rows = window.Element<StackPanel>("TaskRows");
            Check(window.Element<TextBlock>("PendingCount").Text == "0", "An empty dashboard starts at zero.");
            WithEditor(add, editor => {
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
                Check(Descendants<TextBlock>(editor).Any(t => t.Text == "请输入事项标题。"), "Blank titles stay in the editor.");
                Find<TextBox>(editor, "事项标题").Text = "核对计划";
                Find<TextBox>(editor, "备注（选填）").Text = "两条说明\n续行";
                var date = Find<DatePicker>(editor, "截止日期");
                date.ApplyTemplate();
                Click((Button)date.Template.FindName("PART_Button", date));
                Check(date.IsDropDownOpen, "The redesigned date field opens its calendar.");
                date.IsDropDownOpen = false;
                Find<TextBox>(editor, "截止时间").Text = "25:99";
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
                Check(Descendants<TextBlock>(editor).Any(t => t.Text.StartsWith("请选择有效的截止")), "Invalid deadlines stay in the editor.");
                Descendants<CheckBox>(editor).Single(c => (string)c.Content == "设置截止时间").IsChecked = false;
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
            });
            Check(window.State.Tasks.Single().Title == "核对计划" && !window.State.Tasks[0].DueUtc.HasValue, "Save creates an unscheduled item.");
            Check(window.Element<TextBlock>("PendingCount").Text == "1" && rows.Children.Count == 1, "Saving refreshes the dashboard.");
            Check(((Border)rows.Children[0]).Margin.Bottom > 0 && AutomationProperties.GetItemStatus(rows.Children[0]) == "待处理",
                "Pending items appear in separate cards with a textual status.");
            Check(store.Load().Tasks.Single().Notes == "两条说明\n续行", "The notes are persisted.");

            var filters = window.Element<WrapPanel>("Filters");
            Click((Button)filters.Children[1]);
            Check(Descendants<CheckBox>(rows).Count() == 0, "An unscheduled item is absent from Today's due list.");
            Click((Button)filters.Children[0]);
            DateTime started = DateTime.UtcNow.AddSeconds(-1);
            Descendants<CheckBox>(rows).Single().IsChecked = true;
            Check(window.State.Tasks[0].CompletedUtc >= started && window.State.Tasks[0].CompletedUtc <= DateTime.UtcNow,
                "Completing an item records the current time.");
            Check(window.Element<TextBlock>("PendingCount").Text == "0" && window.Element<TextBlock>("CompletedCount").Text == "1",
                "Completing an item updates both counts.");
            Click((Button)filters.Children[3]);
            Check(AutomationProperties.GetItemStatus(rows.Children[0]) == "已完成" &&
                Descendants<TextBlock>(rows).Any(block => block.Text == "已完成" && block.Foreground == StatusTone.Completed.Text),
                "Completed cards visibly use the green state label.");
            var prior = DateTime.Today.AddDays(-1);
            WithEditor(Descendants<Button>(rows).Single(), editor => {
                Find<DatePicker>(editor, "完成日期").SelectedDate = prior;
                Find<TextBox>(editor, "完成时间").Text = "15:30";
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
            });
            Check(window.State.Tasks[0].CompletedUtc.Value.ToLocalTime() == prior.AddHours(15.5), "Editing records the manually selected time.");
            Check(window.Element<TextBlock>("CompletedCount").Text == "0", "Today's count uses the actual completion date.");
            Descendants<CheckBox>(rows).Single().IsChecked = false;
            Check(!window.State.Tasks[0].CompletedUtc.HasValue, "Unchecking a completed item restores the pending state.");
            window.Close();

            var reopened = new MainWindow(store, store.Load());
            reopened.Show();
            Check(reopened.Element<TextBlock>("PendingCount").Text == "1", "Opening a fresh window loads the saved pending state.");
            WithEditor(reopened.Element<Button>("NewTask"), editor => {
                Find<TextBox>(editor, "事项标题").Text = "临时事项";
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
            });
            CheckDeletePrompt(reopened, store, false);
            Check(reopened.State.Tasks.Count == 2 && store.Load().Tasks.Count == 2, "Canceling the delete prompt preserves the task.");
            CheckDeletePrompt(reopened, store, true);
            Check(reopened.State.Tasks.Single().Title == "核对计划" && store.Load().Tasks.Single().Title == "核对计划",
                "Explicit confirmation deletes only the selected task and saves the change.");
            reopened.Close();

            CheckSummariesAndReminders(store);
            CheckTaskShortcutsAndScrolling(store);
            CheckTaskArchiving(store);
            CheckHelpPage(store);
        }

        private static void CheckHelpPage(StateStore store)
        {
            var window = new MainWindow(store, store.Load());
            window.Show();
            Click(window.Element<Button>("HelpButton"));
            var page = window.Element<ContentControl>("OtherPage");
            var steps = Descendants<CheckBox>(page).ToArray();
            Check(window.Element<TextBlock>("PageTitle").Text == "帮助" && steps.Length == 6 &&
                Descendants<TextBlock>(page).Any(block => block.Text == "十个待办处理技巧") &&
                Descendants<TextBlock>(page).Any(block => block.Text == "研发工作常用的六种清单状态"),
                "The Help button opens the guide and six interactive checklist steps.");
            steps[0].IsChecked = true;
            steps[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(store.Load().GetDailyChecklist(DateTime.Today)[0] && Descendants<TextBlock>(page).Any(block => block.Text == "1/6"),
                "Checking a step saves it and updates progress.");
            window.RefreshClock(DateTimeOffset.Now.AddDays(1));
            Check(steps.All(step => step.IsChecked == false) && Descendants<TextBlock>(page).Any(block => block.Text == "0/6"),
                "An open help page resets its display when the local day changes.");
            Click(window.Element<Button>("DeskNav"));
            Click(window.Element<Button>("HelpButton"));
            Check(Descendants<CheckBox>(page).First().IsChecked == true, "Reopening Help restores today's saved progress.");
            Click(Descendants<Button>(page).Single(button => (string)button.Content == "重置清单"));
            Check(store.Load().GetDailyChecklist(DateTime.Today).All(step => !step) &&
                Descendants<TextBlock>(page).Any(block => block.Text == "0/6"), "Reset clears and saves today's checklist.");
            window.Close();
        }

        private static void CheckDeletePrompt(MainWindow window, StateStore store, bool confirm)
        {
            var rows = window.Element<StackPanel>("TaskRows");
            WithEditor(Find<Button>(rows, "编辑事项：临时事项"), editor => {
                Exception failure = null;
                editor.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate {
                    var prompt = Application.Current.Windows.OfType<DeskPrompt>().SingleOrDefault();
                    try
                    {
                        Check(prompt != null && prompt.Owner == editor && prompt.WindowStyle == WindowStyle.None &&
                            Descendants<TextBlock>(prompt).Any(block => block.Text.Contains("临时事项")),
                            "Deletion uses an owned, themed prompt naming the task.");
                        var cancel = Descendants<Button>(prompt).Single(button => button.Content as string == "取消");
                        Check(cancel.IsCancel && cancel.IsDefault && store.Load().Tasks.Count == 2, "Deletion defaults to cancel and waits for confirmation.");
                        Click(confirm ? Descendants<Button>(prompt).Single(button => button.Content as string == "删除事项") : cancel);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { if (prompt != null && prompt.IsVisible) prompt.Close(); }
                }));
                Click(Descendants<Button>(editor).Single(button => button.Content as string == "删除事项"));
                if (failure != null) throw new InvalidOperationException("Delete prompt check failed.", failure);
                Check(editor.IsVisible != confirm, "Cancel leaves the editor open and confirming closes it.");
            });
        }

        private static void CheckTaskArchiving(StateStore store)
        {
            var now = DateTimeOffset.Now;
            now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
            var state = new WorkState { Settings = new ReminderSettings { DailyEnabled = false } };
            var oldDone = new WorkItem { Title = "旧已完成", CompletedUtc = now.UtcDateTime.AddDays(-8) };
            var oldOverdue = new WorkItem { Title = "旧已逾期", DueUtc = now.UtcDateTime.AddDays(-31) };
            var recentOverdue = new WorkItem { Title = "近期逾期", DueUtc = now.UtcDateTime.AddDays(-29) };
            state.Tasks.AddRange(new[] { oldDone, oldOverdue, recentOverdue, new WorkItem { Title = "未设置截止" } });
            store.Save(state);
            var app = Application.Current;
            var window = new MainWindow(store, store.Load());
            using (var host = new DesktopHost(app, window))
            {
                window.Show();
                Click(window.Element<Button>("ArchiveNav"));
                var page = window.Element<ContentControl>("OtherPage");
                Check(window.Element<TextBlock>("PageTitle").Text == "事项归档" &&
                    Descendants<TextBlock>(page).Any(block => block.Text.StartsWith("暂无归档事项")), "The sidebar opens an unconfigured archive.");
                Click(Descendants<Button>(page).Single(button => button.Content as string == "归档设置"));
                var complete = Find<TextBox>(page, "完成后归档（天）");
                var overdue = Find<TextBox>(page, "逾期后归档（天）");
                var save = Descendants<Button>(page).Single(button => button.Content as string == "保存归档设置");
                Check(complete.Text == "0" && overdue.Text == "0", "Both archived settings start disabled.");
                complete.Text = "7";
                overdue.Text = "4000";
                Click(save);
                Check(store.Load().Archiving.CompletedDays == 0 && store.Load().Tasks.All(item => !item.ArchivedUtc.HasValue),
                    "An invalid threshold never changes settings or tasks.");
                overdue.Text = "30";
                Click(save);
                Check(store.Load().FilterTasks(TaskFilter.Archived, now).Count() == 2, "Saving archive rules immediately groups existing matching tasks.");
                Click(window.Element<Button>("DeskNav"));
                Check(window.Element<TextBlock>("PendingCount").Text == "2" && window.Element<TextBlock>("OverdueCount").Text == "1",
                    "The dashboard no longer counts archived tasks.");
                Click(window.Element<Button>("ArchiveNav"));
                Check(Descendants<TextBlock>(page).Any(block => block.Text.StartsWith("归档于 ")) &&
                    !Descendants<CheckBox>(page).Any(), "Archive rows show their dates and keep completion read-only.");
                Click(Find<Button>(page, "恢复归档事项：旧已完成"));
                Check(store.Load().Tasks.Single(item => item.Id == oldDone.Id).CompletedUtc == oldDone.CompletedUtc,
                    "Restoring from the archive preserves the original completion time.");
                window.Close();
                host.Tick(now.AddDays(2));
                Check(!window.IsVisible && store.Load().FilterTasks(TaskFilter.Archived, now).Count() == 2 &&
                    !app.Windows.OfType<ReminderPopup>().Any(), "The hidden window archives aged tasks, keeps a restored task, and skips archived reminders.");
                window.ShowWorkspace();
                Check(Find<Button>(page, "恢复归档事项：近期逾期") != null, "Background archiving updates the open archive page.");
                Click(window.Element<Button>("DeskNav"));
                Click(window.Element<Button>("CompletedShortcut"));
                Check(Find<Button>(window.Element<StackPanel>("TaskRows"), "编辑事项：旧已完成") != null,
                    "A restored completed task appears in the completed list.");
            }
            window.Close();
        }

        private static void CheckSummariesAndReminders(StateStore store)
        {
            var settings = new ReminderSettings {
                DailyEnabled = true, DailyMinute = 0, WeeklyEnabled = true, WeeklyMinute = 0, WeeklyDay = DateTime.Today.DayOfWeek
            };
            var state = store.Load();
            state.Settings = settings;
            state.Tasks[0].SetSchedule(DateTime.UtcNow.AddMinutes(-1), 0);
            var yesterday = DateTime.Today.AddDays(-1);
            state.SetSummary(yesterday, false, "昨天已存的记录", DateTime.UtcNow);
            store.Save(state);
            var app = Application.Current;
            var window = new MainWindow(store, store.Load());
            app.MainWindow = window;
            using (var host = new DesktopHost(app, window))
            {
                window.Show();
                Check(window.Icon != null && ReferenceEquals(window.Element<Image>("BrandIcon").Source, window.Icon),
                    "The sidebar uses the packaged window icon.");
                host.Start();
                window.Close();
                Check(!window.IsVisible && app.Windows.OfType<MainWindow>().Contains(window), "Closing leaves the window running in the tray.");
                app.Dispatcher.Invoke(DispatcherPriority.SystemIdle, new Action(delegate { }));
                var popup = app.Windows.OfType<ReminderPopup>().Single();
                Check(popup.IsVisible && popup.ShowActivated == false && popup.Topmost, "Reminders open a visible non-activating popup while the main window is hidden.");
                var labels = Descendants<TextBlock>(popup).ToArray();
                Check(labels.Any(label => label.Text == "事项已逾期" && label.Foreground == StatusTone.Overdue.Text) &&
                    labels.Any(label => label.Text == "日报提醒" && label.Foreground == StatusTone.Pending.Text) &&
                    labels.Any(label => label.Text == "周报提醒" && label.Foreground == StatusTone.Weekly.Text),
                    "Overdue, daily, and weekly reminders display different labeled tones.");
                var displayed = store.Load();
                Check(displayed.Tasks[0].ReminderShown && displayed.LastDailyReminder != null && displayed.LastWeeklyReminder != null,
                    "The independent reminder markers are saved before the popup.");
                Click(Descendants<Button>(popup).Single(b => (string)b.Content == "打开事项"));
                Check(window.IsVisible && window.Element<TextBlock>("PageTitle").Text == "工作台", "The deadline popup reopens the workbench.");
                Check(popup.IsVisible, "Opening the tasks keeps simultaneous report reminders available.");
                Click(Descendants<Button>(popup).Single(b => (string)b.Content == "写日报"));
                Check(window.Element<TextBlock>("PageTitle").Text == "工作总结" &&
                    Find<DatePicker>(window, "总结日期").SelectedDate == DateTime.Today, "The daily popup opens today's editor.");
                window.UpdateLayout();
                var daily = Find<SummaryEditor>(window, "总结正文");
                daily.Focus();
                CheckSummaryResize(window, daily);
                var plain = "    已完成核对\n\n- 待同步";
                SetBody(daily, plain);
                Check(daily.ReadMarkdown() == plain, "The inline editor preserves Markdown indentation and paragraphs.");
                var pixels = new byte[640 * 360 * 4];
                new Random(41).NextBytes(pixels);
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0;
                var picture = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
                daily.CaretPosition = daily.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                daily.InsertImage(picture);
                var images = new List<SummaryImage>();
                string withImage = daily.ReadMarkdown(images);
                Check(images.Count == 1 && withImage == plain + images[0].Marker && images[0].IsValid(), "Pasting inserts an actual image at the caret.");
                Check(daily.Undo() && daily.ReadMarkdown() == plain, "An inline image can be undone with the text editor's Undo.");
                daily.Redo();
                Check(daily.ReadMarkdown() == withImage, "Redo restores the inline image and its attachment.");
                Check(window.HasUnsavedSummary, "New summary content is tracked as a draft.");
                RespondToSummaryDialog(window, delegate { Click(window.Element<Button>("SettingsNav")); }, null);
                Check(window.HasUnsavedSummary && ReferenceEquals(daily, Find<SummaryEditor>(window, "总结正文")),
                    "Closing the custom unsaved dialog keeps the same draft and editor.");
                Click(Descendants<Button>(window).Single(b => b.Content as string == "全屏"));
                window.UpdateLayout();
                Check(window.WindowState == WindowState.Maximized && window.WindowStyle == WindowStyle.None &&
                    window.Element<Border>("Sidebar").Visibility == Visibility.Collapsed && daily.ActualHeight > daily.MinHeight,
                    "Full-screen mode maximizes the window and expands the editor.");
                Check(ReferenceEquals(daily, Find<SummaryEditor>(window, "总结正文")) && daily.ReadMarkdown() == withImage,
                    "Full-screen mode retains the open draft and image.");
                Click(Descendants<Button>(window).Single(b => b.Content as string == "退出全屏"));
                Check(window.WindowState == WindowState.Normal && window.WindowStyle != WindowStyle.None, "Leaving full screen restores the original window.");
                window.Close();
                window.ShowWorkspace();
                Check(daily.ReadMarkdown() == withImage && window.HasUnsavedSummary, "Hiding to the tray preserves the unsaved summary and its image.");
                Click(Descendants<Button>(window).Single(b => b.Content as string == "保存总结"));
                var savedDaily = store.Load().FindSummary(DateTime.Today, false);
                Check(!window.HasUnsavedSummary && savedDaily.Text == withImage && savedDaily.Images[0].Png == images[0].Png,
                    "The saved daily summary retains its formatting and original image data.");
                using (var stream = new MemoryStream(savedDaily.Images[0].ReadPng()))
                {
                    var image = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    var sample = new byte[4];
                    new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), sample, 4, 0);
                    Check(sample.Take(3).SequenceEqual(pixels.Take(3)) && sample[3] == 255, "New screenshots retain color and become visible when saved.");
                }
                Click(Descendants<Button>(window).Single(b => b.Content as string == "全屏"));
                Click(Descendants<Button>(window).Single(b => b.Content as string == WorkState.PeriodKey(yesterday, false)));
                Check(window.WindowStyle == WindowStyle.None && Find<SummaryEditor>(window, "总结正文").ReadMarkdown() == "昨天已存的记录",
                    "History remains available when making an old report full-screen.");
                Click(Descendants<Button>(window).Single(b => b.Content as string == "退出全屏"));
                Click(Descendants<Button>(popup).Single(b => (string)b.Content == "写周报"));
                Check(!popup.IsVisible && Find<DatePicker>(window, "选择周内任一天").SelectedDate == DateTime.Today,
                    "The weekly popup opens the corresponding week after the daily reminder.");
                SetBody(Find<SummaryEditor>(window, "总结正文"), "本周完成核对和发布");
                Click(Descendants<Button>(window).Single(b => b.Content as string == "保存总结"));
                var sunday = WorkState.WeekStart(DateTime.Today).AddDays(6);
                Check(window.OpenSummary(sunday, true) && Find<SummaryEditor>(window, "总结正文").ReadMarkdown() == "本周完成核对和发布",
                    "A different day of the same week opens the existing weekly summary.");
                window.OpenSummary(DateTime.Today, false);
                var reopened = Find<SummaryEditor>(window, "总结正文");
                var inline = reopened.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()).Single();
                Check(inline.Tag is SummaryImage && ((Border)inline.Child).Child is Image, "Reopening the report restores the image inside the editor.");
                var remove = ((Border)inline.Child).ContextMenu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "移除图片");
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(reopened.ReadMarkdown() == plain && window.HasUnsavedSummary, "The image menu removes just that image.");
                Click(Descendants<Button>(window).Single(b => b.Content as string == "保存总结"));
                Check(store.Load().FindSummary(DateTime.Today, false).Images == null, "Saving after removal drops the unused image from the report.");
                CheckExportDialog(window, store.Load().Summaries);
                SetBody(reopened, "仅用于测试的未保存文本");
                RespondToSummaryDialog(window, delegate { Click(window.Element<Button>("SettingsNav")); }, "不保存并离开");
                Check(store.Load().FindSummary(DateTime.Today, false).Text == plain && window.Element<TextBlock>("PageTitle").Text == "设置",
                    "Discard leaves the summary and keeps its saved version.");
                window.UpdateLayout();
                var weekday = Find<ComboBox>(window, "周报星期");
                var dropdown = (ToggleButton)weekday.Template.FindName("DropDownToggle", weekday);
                dropdown.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                app.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
                Check(weekday.IsDropDownOpen && ((Popup)weekday.Template.FindName("PART_Popup", weekday)).IsOpen,
                    "The redesigned weekday field opens its list.");
                weekday.IsDropDownOpen = false;
                Find<TextBox>(window, "日报时间（24 小时制）").Text = "25:00";
                Click(Descendants<Button>(window).Single(b => b.Content as string == "保存提醒设置"));
                Check(store.Load().Settings.DailyMinute == 0, "Invalid reminder settings are not written.");
                Find<TextBox>(window, "日报时间（24 小时制）").Text = "17:30";
                Click(Descendants<Button>(window).Single(b => b.Content as string == "保存提醒设置"));
                Check(store.Load().Settings.DailyMinute == 1050, "Edited reminder settings are saved.");
                host.Tick(DateTimeOffset.Now);
                Check(!app.Windows.OfType<ReminderPopup>().Any(), "The same task and summary reminders are not duplicated.");
                CheckTypography(window, store);
                var draftToSave = Find<SummaryEditor>(window, "总结正文");
                SetBody(draftToSave, "保存后离开");
                RespondToSummaryDialog(window, delegate { Click(window.Element<Button>("SettingsNav")); }, "保存并离开");
                Check(store.Load().FindSummary(DateTime.Today, false).Text == "保存后离开" &&
                    window.Element<TextBlock>("PageTitle").Text == "设置", "Save and leave commits the draft before opening the destination.");
            }
            window.Close();
        }

        private static void RespondToSummaryDialog(MainWindow window, Action open, string action)
        {
            Exception failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate {
                var dialog = Application.Current.Windows.OfType<SummaryLeaveDialog>().SingleOrDefault();
                try
                {
                    Check(dialog != null && dialog.IsVisible && dialog.WindowStyle == WindowStyle.None &&
                        Descendants<Button>(dialog).Any(button => button.Content as string == "继续编辑" && button.IsCancel),
                        "Leaving an unsaved report opens the styled confirmation with a safe cancel action.");
                    if (action != null) Click(Descendants<Button>(dialog).Single(button => button.Content as string == action));
                }
                catch (Exception ex) { failure = ex; }
                finally { if (dialog != null && dialog.IsVisible) dialog.Close(); }
            }));
            open();
            if (failure != null) throw new InvalidOperationException("Summary confirmation check failed.", failure);
        }

        private static void CheckTypography(MainWindow window, StateStore store)
        {
            var interfaceFont = Find<ComboBox>(window, "界面字体");
            var editorFont = Find<ComboBox>(window, "编辑框字体");
            var ui = interfaceFont.Items.Cast<string>().First(name => name == "Segoe UI");
            var text = editorFont.Items.Cast<string>().First(name => name == "Consolas");
            interfaceFont.SelectedItem = ui;
            editorFont.SelectedItem = text;
            Find<ComboBox>(window, "界面字号").SelectedItem = 18;
            Find<ComboBox>(window, "编辑框字号").SelectedItem = 26;
            Click(Descendants<Button>(window).Single(button => button.Content as string == "保存字体设置"));
            var saved = store.Load().Appearance;
            Check(saved.InterfaceFont == ui && saved.EditorFont == text && saved.InterfaceSize == 18 && saved.EditorSize == 26,
                "Font preferences from the settings page persist on disk independently.");
            Check(window.FontFamily.Source.StartsWith(ui + ",") && window.Element<TextBlock>("PageTitle").FontSize == 30,
                "The saved interface typography applies immediately to the existing window.");
            Click(window.Element<Button>("SummaryNav"));
            var editor = Find<SummaryEditor>(window, "总结正文");
            Check(editor.FontSize == 26 && editor.Document.FontSize == 26 && editor.Document.FontFamily.Source.StartsWith(text + ","),
                "The report editor and its document use their separate saved typography.");
        }

        private static void CheckTaskShortcutsAndScrolling(StateStore store)
        {
            var state = new WorkState();
            var now = DateTime.UtcNow;
            state.Tasks.Add(new WorkItem { Title = "逾期示例", DueUtc = now.AddDays(-1) });
            state.Tasks.Add(new WorkItem { Title = "完成示例", CompletedUtc = now });
            for (int i = 0; i < 30; i++) state.Tasks.Add(new WorkItem { Title = "滚动检查 " + i });
            for (int i = 0; i < 36; i++) state.SetSummary(DateTime.Today.AddDays(-i), false, "历史示例 " + i, now);
            var window = new MainWindow(store, state);
            window.Show();
            window.UpdateLayout();
            var page = window.Element<ScrollViewer>("PageScroll");
            var list = window.Element<ScrollViewer>("TaskListScroll");
            var rows = window.Element<StackPanel>("TaskRows");
            var tabs = window.Element<WrapPanel>("Filters");
            Click(window.Element<Button>("CompletedShortcut"));
            Check(AutomationProperties.GetItemStatus(tabs.Children[3]) == "已选中" && rows.Children.Count == 1 &&
                AutomationProperties.GetItemStatus(rows.Children[0]) == "已完成", "Today Completed navigates to the existing Completed filter.");
            Click(window.Element<Button>("OverdueShortcut"));
            Check(AutomationProperties.GetItemStatus(tabs.Children[2]) == "已选中" && rows.Children.Count == 1 &&
                AutomationProperties.GetItemStatus(rows.Children[0]) == "已逾期", "The Overdue card opens the overdue task filter.");
            Click(window.Element<Button>("PendingShortcut"));
            window.UpdateLayout();
            Check(AutomationProperties.GetItemStatus(tabs.Children[0]) == "已选中" && rows.Children.Count == 31 &&
                page.VerticalOffset > 0 && list.ScrollableHeight > 0, "The Pending card jumps to a scrollable list of pending tasks.");
            list.ScrollToEnd();
            page.ScrollToTop();
            window.UpdateLayout();
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            rows.RaiseEvent(wheel);
            window.UpdateLayout();
            Check(list.VerticalOffset > 0 && page.VerticalOffset > 0 && wheel.Handled,
                "At the end of an inner list, the mouse wheel continues scrolling the page.");
            Click(window.Element<Button>("SummaryNav"));
            window.UpdateLayout();
            var history = Descendants<ScrollViewer>(window.Element<ContentControl>("OtherPage")).Single();
            history.ScrollToEnd();
            window.UpdateLayout();
            Check(history.ScrollableHeight > 0 && history.VerticalOffset > 0, "A long report history scrolls independently of the editor.");
            Click(window.Element<Button>("SettingsNav"));
            page.ScrollToEnd();
            window.UpdateLayout();
            Check(page.ScrollableHeight > 0 && page.VerticalOffset > 0, "Settings cards can scroll to the bottom at the default window size.");
            window.Close();
        }

        private static void CheckSummaryResize(MainWindow window, SummaryEditor editor)
        {
            var initialSize = new Size(window.Width, window.Height);
            window.Width = 880;
            window.Height = 595;
            window.UpdateLayout();
            double narrowWidth = editor.ActualWidth;
            double shortHeight = editor.ActualHeight;
            window.Width = 1350;
            window.Height = 860;
            window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            window.UpdateLayout();
            Check(editor.ActualWidth > narrowWidth + 200 && editor.ActualHeight > shortHeight + 100,
                "Dragging a window larger expands the same report editor in both directions.");
            window.Width = initialSize.Width;
            window.Height = initialSize.Height;
            window.UpdateLayout();
            double normalHeight = editor.ActualHeight;
            window.WindowState = WindowState.Maximized;
            window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            window.UpdateLayout();
            Check(editor.ActualHeight > normalHeight && window.WindowStyle != WindowStyle.None &&
                window.Element<Border>("Sidebar").IsVisible, "Native maximizing also grows the editor and leaves the normal navigation accessible.");
            window.WindowState = WindowState.Normal;
        }

        private static void CheckExportDialog(MainWindow window, IEnumerable<WorkSummary> reports)
        {
            var dialog = new SummaryExportDialog(window, reports);
            Exception failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate {
                try
                {
                    var boxes = Descendants<CheckBox>(dialog).ToArray();
                    var export = Descendants<Button>(dialog).Single(button => button.Content as string == "导出所选总结");
                    Check(boxes.Length == 3 && !export.IsEnabled, "Export opens with all saved reports across both types unselected.");
                    Click(Descendants<Button>(dialog).Single(button => button.Content as string == "全选"));
                    Check(boxes.All(box => box.IsChecked == true) && export.IsEnabled, "All saved reports can be selected.");
                    Click(Descendants<Button>(dialog).Single(button => button.Content as string == "清空勾选"));
                    Check(boxes.All(box => box.IsChecked == false) && !export.IsEnabled, "Clearing selection prevents an empty export.");
                    boxes[0].IsChecked = true;
                    boxes[2].IsChecked = true;
                    Click(export);
                }
                catch (Exception ex) { failure = ex; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
            bool accepted = dialog.ShowDialog() == true;
            if (failure != null) throw new InvalidOperationException("Selected export dialog check failed.", failure);
            Check(accepted && dialog.SelectedReports.Length == 2 && dialog.SelectedReports.Any(report => report.Weekly),
                "The selected export dialog returns the two checked reports.");
        }

        private static void SetBody(SummaryEditor editor, string text)
        {
            new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text = text;
        }

        private static void WithEditor(Button button, Action<TaskEditor> steps)
        {
            Exception failure = null;
            button.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate {
                var editor = Application.Current.Windows.OfType<TaskEditor>().SingleOrDefault();
                try
                {
                    Check(editor != null && editor.IsVisible, "The task button opens a modal editor.");
                    steps(editor);
                }
                catch (Exception ex) { failure = ex; }
                finally { if (editor != null && editor.IsVisible) editor.Close(); }
            }));
            Click(button);
            if (failure != null) throw new InvalidOperationException("Task editor check failed.", failure);
        }

        private static T Find<T>(DependencyObject root, string label) where T : DependencyObject
        {
            return Descendants<T>(root).Single(element => AutomationProperties.GetName(element) == label);
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                var match = child as T;
                if (match != null) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }

        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
    }
}
