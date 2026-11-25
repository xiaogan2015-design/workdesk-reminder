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

        public MainWindow(StateStore store, WorkState state)
        {
            this.store = store;
            this.state = state;
            Title = "工笺 · WorkDesk";
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
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
            Element<Button>("SummaryNav").Visibility = Visibility.Collapsed;
            Element<Button>("SettingsNav").Visibility = Visibility.Collapsed;
            Element<StackPanel>("RightColumn").Visibility = Visibility.Collapsed;
            Grid.SetColumnSpan(Element<Border>("TaskCard"), 3);
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control && taskEditor == null)
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
            var row = new DockPanel { LastChildFill = true };
            var check = new CheckBox {
                IsChecked = item.CompletedUtc.HasValue, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 11, 0)
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
            details.Children.Add(title);
            var meta = Ui.Text(TaskCaption(item, now), "Hint");
            meta.Margin = new Thickness(0, 3, 0, 0);
            if (item.IsOverdue(now.UtcDateTime)) meta.Foreground = Ui.Color("#A23B28");
            else if (item.CompletedUtc.HasValue) meta.Foreground = Ui.Color("#13745A");
            details.Children.Add(meta);
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
            return new Border {
                Child = row, Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = (Brush)FindResource("Line")
            };
        }

        private static string TaskCaption(WorkItem item, DateTimeOffset now)
        {
            var deadline = item.DueUtc.HasValue ? item.DueUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Chinese) : null;
            if (item.CompletedUtc.HasValue)
                return "完成于 " + item.CompletedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Chinese) +
                    (deadline == null ? "" : "  ·  原定 " + deadline);
            if (deadline == null) return "无截止时间";
            return (item.IsOverdue(now.UtcDateTime) ? "已逾期  ·  " : "截止  ·  ") + deadline;
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
            var editor = new TaskEditor(this, item);
            taskEditor = editor;
            bool saved;
            try { saved = editor.ShowDialog() == true; }
            finally { taskEditor = null; }
            if (!saved) return;
            var next = state.Copy();
            if (editor.DeleteRequested) next.Tasks.RemoveAll(t => t.Id == id);
            else if (item == null) next.Tasks.Add(editor.Result);
            else
            {
                var current = next.Tasks.Find(t => t.Id == id);
                current.Title = editor.Result.Title;
                current.Notes = editor.Result.Notes;
                current.SetSchedule(editor.Result.DueUtc, editor.Result.LeadMinutes);
                current.SetCompletion(editor.Result.CompletedUtc);
            }
            if (Commit(next))
            {
                Banner(editor.DeleteRequested ? "已删除事项。" : "事项已保存。");
                RefreshDashboard();
            }
        }
    }
}
