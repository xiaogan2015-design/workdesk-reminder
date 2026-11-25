using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkDesk
{
    internal sealed partial class MainWindow : Window
    {
        private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
        private readonly StateStore store;
        private readonly Grid shell;
        private WorkState state;
        private TaskFilter filter;
        private TaskEditor taskEditor;
        private long clockMinute;

        public MainWindow(StateStore store, WorkState state)
        {
            this.store = store;
            this.state = state;
            Title = "工笺 · WorkDesk";
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            Foreground = (Brush)FindResource("Ink");
            Language = System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN");
            UseLayoutRounding = true;
            Width = Math.Min(1100, SystemParameters.WorkArea.Width);
            Height = Math.Min(745, SystemParameters.WorkArea.Height);
            MinWidth = Math.Min(840, SystemParameters.WorkArea.Width);
            MinHeight = Math.Min(530, SystemParameters.WorkArea.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            shell = (Grid)Program.LoadXaml("Shell.xaml");
            Content = shell;

            Element<Button>("NewTask").Click += delegate { EditTask(null); };
            InitializePages();
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (page == DeskPage.Summary && (e.Key == Key.F11 || (e.Key == Key.Escape && summaryFullScreen && !summaryPicker.IsDropDownOpen)))
                {
                    e.Handled = true;
                    SetSummaryFullScreen(!summaryFullScreen);
                }
                else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control && page == DeskPage.Desk && taskEditor == null)
                {
                    e.Handled = true;
                    EditTask(null);
                }
            };
            Activated += delegate { RefreshDashboard(); };
            RefreshDashboard();
            if (store.Recovered) Banner("已从上一份有效备份恢复。下次保存将修复主数据文件。");
        }

        internal WorkState State { get { return state; } }
        internal T Element<T>(string name) where T : FrameworkElement { return (T)shell.FindName(name); }

        internal void ShowWorkspace()
        {
            if (taskEditor != null) { taskEditor.Activate(); return; }
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = summaryFullScreen ? WindowState.Maximized : WindowState.Normal;
            Activate();
        }

        internal void RefreshClock(DateTimeOffset now)
        {
            long minute = now.Ticks / TimeSpan.TicksPerMinute;
            if (clockMinute == minute) return;
            clockMinute = minute;
            if (page == DeskPage.Desk) RefreshDashboard();
            else Element<TextBlock>("DateLabel").Text = now.ToString("M月d日 dddd", Chinese);
        }

        internal void Banner(string message)
        {
            Element<TextBlock>("BannerMessage").Text = message;
            Element<Border>("Banner").Visibility = String.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        internal bool Commit(WorkState next, bool background = false)
        {
            try
            {
                store.Save(next);
                state = next;
                return true;
            }
            catch (Exception ex)
            {
                var message = "保存失败。请检查数据文件的访问权限和磁盘空间。";
                Banner(message);
                if (!background)
                    MessageBox.Show(this, message + "\n\n" + ex.Message + "\n" + store.FilePath,
                        "无法保存", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        internal void RefreshDashboard()
        {
            var now = DateTimeOffset.Now;
            int pending = state.Tasks.Count(t => !t.CompletedUtc.HasValue);
            int overdue = state.Tasks.Count(t => t.IsOverdue(now.UtcDateTime));
            int finished = state.Tasks.Count(t => t.CompletedUtc.HasValue && t.CompletedUtc.Value.ToLocalTime().Date == now.Date);
            Element<TextBlock>("DateLabel").Text = now.ToString("M月d日 dddd", Chinese);
            Element<TextBlock>("PendingCount").Text = pending.ToString(Chinese);
            Element<TextBlock>("OverdueCount").Text = overdue.ToString(Chinese);
            Element<TextBlock>("CompletedCount").Text = finished.ToString(Chinese);
            Element<TextBlock>("HeroStatus").Text = pending == 0 ? "当前没有待处理的事项" :
                String.Format(Chinese, "{0} 项待处理，{1} 项已逾期", pending, overdue);
            RefreshSideCards(now.Date);
            RenderFilters();
            RenderRows(now);
        }

        private void RenderFilters()
        {
            var labels = new[] { "全部待办", "今日到期", "已逾期", "已完成" };
            var filters = Element<WrapPanel>("Filters");
            if (filters.Children.Count == 0)
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    var choice = (TaskFilter)i;
                    var button = Ui.Button(labels[i], delegate {
                        filter = choice;
                        RefreshDashboard();
                    });
                    button.Margin = new Thickness(0, 0, 6, 4);
                    button.Padding = new Thickness(10, 5, 10, 5);
                    filters.Children.Add(button);
                }
            }
            for (int i = 0; i < labels.Length; i++)
            {
                var button = (Button)filters.Children[i];
                button.Style = i == (int)filter ? Ui.Style("Quiet") : (Style)TryFindResource(typeof(Button));
                AutomationProperties.SetItemStatus(button, i == (int)filter ? "已选中" : "未选中");
            }
        }

        private void RenderRows(DateTimeOffset now)
        {
            var rows = Element<StackPanel>("TaskRows");
            rows.Children.Clear();
            var items = state.FilterTasks(filter, now).ToArray();
            if (items.Length == 0)
            {
                string message = filter == TaskFilter.Pending ? "没有待办事项。点击「新建事项」来添加。" : "这个筛选下暂时没有事项。";
                var empty = Ui.Text(message, "Hint");
                empty.Margin = new Thickness(0, 15, 0, 16);
                rows.Children.Add(empty);
                return;
            }
            foreach (var item in items) rows.Children.Add(TaskRow(item, now));
        }

        private Border TaskRow(WorkItem item, DateTimeOffset now)
        {
            StatusTone tone = item.CompletedUtc.HasValue ? StatusTone.Completed :
                item.IsOverdue(now.UtcDateTime) ? StatusTone.Overdue :
                item.WithinReminderWindow(now.UtcDateTime) ? StatusTone.Upcoming : StatusTone.Pending;
            var row = new DockPanel { LastChildFill = true };
            var check = new CheckBox {
                IsChecked = item.CompletedUtc.HasValue, VerticalAlignment = VerticalAlignment.Center,
                Width = 23, MinHeight = 29, Margin = new Thickness(1, 0, 5, 0)
            };
            AutomationProperties.SetName(check, (item.CompletedUtc.HasValue ? "恢复待办：" : "标记完成：") + item.Title);
            check.Checked += delegate { ToggleCompletion(item.Id, true); };
            check.Unchecked += delegate { ToggleCompletion(item.Id, false); };
            row.Children.Add(check);

            var edit = Ui.Button("编辑", delegate { EditTask(item.Id); });
            edit.Margin = new Thickness(8, 0, 0, 0);
            edit.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(edit, "编辑事项：" + item.Title);
            DockPanel.SetDock(edit, Dock.Right);
            row.Children.Add(edit);

            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text(item.Title);
            title.TextWrapping = TextWrapping.Wrap;
            title.FontWeight = FontWeights.SemiBold;
            title.FontSize = 15;
            details.Children.Add(title);
            var metadata = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var status = new TextBlock { Text = tone.Label, Foreground = tone.Text, FontSize = 12, FontWeight = FontWeights.SemiBold };
            metadata.Children.Add(new Border {
                Background = tone.Badge, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 7, 0), Child = status
            });
            var timing = Ui.Text(TaskCaption(item), "Hint");
            timing.VerticalAlignment = VerticalAlignment.Center;
            metadata.Children.Add(timing);
            details.Children.Add(metadata);
            if (!String.IsNullOrWhiteSpace(item.Notes))
            {
                var notes = Ui.Text(item.Notes.Replace('\n', ' ').Replace('\r', ' '), "Hint");
                notes.TextWrapping = TextWrapping.NoWrap;
                notes.TextTrimming = TextTrimming.CharacterEllipsis;
                notes.ToolTip = item.Notes;
                notes.Margin = new Thickness(0, 3, 0, 0);
                details.Children.Add(notes);
            }
            row.Children.Add(details);
            var card = tone.Card(new Thickness(12, 10, 10, 10));
            card.Margin = new Thickness(0, 0, 0, 8);
            card.Child = row;
            AutomationProperties.SetItemStatus(card, tone.Label);
            return card;
        }

        private static string TaskCaption(WorkItem item)
        {
            var deadline = item.DueUtc.HasValue ? item.DueUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Chinese) : null;
            if (item.CompletedUtc.HasValue)
                return "完成于 " + item.CompletedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Chinese) +
                    (deadline == null ? "" : "  ·  原定 " + deadline);
            if (deadline == null) return "无截止时间";
            return "截止 " + deadline;
        }

        private void ToggleCompletion(string id, bool completed)
        {
            var next = state.Copy();
            var item = next.Tasks.Find(t => t.Id == id);
            if (item == null) return;
            item.SetCompletion(completed ? (DateTime?)DateTime.UtcNow : null);
            if (Commit(next)) Banner(completed ? "已记录完成时间。" : "已恢复为待办。");
            RefreshDashboard();
        }

        private void EditTask(string id)
        {
            if (taskEditor != null) return;
            var item = id == null ? null : state.Tasks.Find(t => t.Id == id);
            if (id != null && item == null) return;
            var editor = new TaskEditor(this, item, result => PersistTask(id, result));
            taskEditor = editor;
            try { editor.ShowDialog(); }
            finally { taskEditor = null; }
        }

        private bool PersistTask(string id, WorkItem result)
        {
            var next = state.Copy();
            if (result == null) next.Tasks.RemoveAll(t => t.Id == id);
            else if (id == null) next.Tasks.Add(result);
            else
            {
                var current = next.Tasks.Find(t => t.Id == id);
                current.Title = result.Title;
                current.Notes = result.Notes;
                current.SetSchedule(result.DueUtc, result.LeadMinutes);
                current.SetCompletion(result.CompletedUtc);
            }
            if (!Commit(next, true)) return false;
            Banner(result == null ? "已删除事项。" : "事项已保存。");
            RefreshDashboard();
            return true;
        }
    }
}
