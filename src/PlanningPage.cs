using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WorkDesk
{
    internal sealed partial class MainWindow
    {
        private static readonly string[] QuadrantLabels = {
            "待判断", "重要且紧急", "重要不紧急", "紧急不重要", "不重要不紧急"
        };
        private static readonly string[] QuadrantActions = { "", "先处理", "安排时间", "协商转交", "酌情删减" };
        private static readonly string[][] QuadrantColors = {
            new[] { "#FAF8FF", "#EDE8FD", "#5437A2" },
            new[] { "#FFF7F4", "#FDE5E2", "#983C4B" },
            new[] { "#F8F5FF", "#EDE6FF", "#5736A7" },
            new[] { "#FFF9EC", "#FCEBD1", "#855219" },
            new[] { "#F2FAF8", "#DBF1EB", "#27665A" }
        };

        private StackPanel planningBody;
        private DateTime planningDay;
        private bool showQuadrants;
        private TaskQuadrant quadrantFilter;

        private void OpenPlanning(bool showFocus)
        {
            if (taskEditor != null || !CanLeaveSummary()) return;
            if (showFocus) showQuadrants = false;
            if (page != DeskPage.Planning)
            {
                planningBody = new StackPanel();
                SetPage(DeskPage.Planning, "工作规划", planningBody);
            }
            RefreshPlanning(DateTimeOffset.Now);
        }

        private void RefreshPlanning(DateTimeOffset now)
        {
            planningDay = now.Date;
            planningBody.Children.Clear();
            var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            var focus = Ui.Button("今日重点", delegate { showQuadrants = false; RefreshPlanning(DateTimeOffset.Now); }, showQuadrants ? null : "Quiet");
            focus.Margin = new Thickness(0, 0, 8, 0);
            tabs.Children.Add(focus);
            tabs.Children.Add(Ui.Button("四象限", delegate { showQuadrants = true; RefreshPlanning(DateTimeOffset.Now); }, showQuadrants ? "Quiet" : null));
            planningBody.Children.Add(tabs);
            planningBody.Children.Add(showQuadrants ? BuildQuadrantPlanner(now) : BuildFocusPlanner(now));
        }

        private void RefreshFocusPreview(DateTime day)
        {
            var items = state.FocusItems(day);
            Element<TextBlock>("FocusTitle").Text = "今日重点 · " + items.Length + "/" + WorkState.FocusLimit;
            var preview = Element<StackPanel>("FocusPreview");
            preview.Children.Clear();
            if (items.Length == 0) preview.Children.Add(Ui.Text("还没有选择今天的重点。", "Hint"));
            foreach (var item in items)
            {
                var label = Ui.Text((item.CompletedUtc.HasValue ? "✓  " : "•  ") + item.Title, "Hint");
                label.TextWrapping = TextWrapping.NoWrap;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.ToolTip = item.Title;
                label.Margin = new Thickness(0, 0, 0, 4);
                preview.Children.Add(label);
            }
        }

        private Border BuildFocusPlanner(DateTimeOffset now)
        {
            var selected = state.FocusItems(now.Date);
            var available = state.FilterTasks(TaskFilter.Pending, now)
                .Where(item => !selected.Any(focus => focus.Id == item.Id)).ToArray();
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("今日重点 · " + selected.Length + "/" + WorkState.FocusLimit, "SectionTitle"));
            var hint = Ui.Text("从待办里挑出今天最值得推进的事项。完成后仍可在当天回看。", "Hint");
            hint.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(hint);
            var picker = new ComboBox {
                ItemsSource = available.Length == 0 ? new[] { "暂无可加入的待办" } : available.Select(item => item.Title).ToArray(),
                ItemTemplate = (DataTemplate)FindResource("PlanningOption"), SelectedIndex = 0,
                MaxDropDownHeight = 280, Margin = new Thickness(0, 0, 8, 0)
            };
            AutomationProperties.SetName(picker, "选择待办事项");
            var add = Ui.Button("加入今日重点", delegate {
                if (picker.SelectedIndex >= 0 && picker.SelectedIndex < available.Length)
                    AddFocus(available[picker.SelectedIndex].Id);
            }, "Primary");
            picker.IsEnabled = add.IsEnabled = available.Length > 0 && selected.Length < WorkState.FocusLimit;
            if (selected.Length == WorkState.FocusLimit) add.ToolTip = "移除一项后可加入其他事项。";
            var controls = new Grid { Margin = new Thickness(0, 14, 0, 12), MaxWidth = 570, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 450 };
            controls.ColumnDefinitions.Add(new ColumnDefinition());
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.Children.Add(picker);
            Grid.SetColumn(add, 1);
            controls.Children.Add(add);
            panel.Children.Add(controls);
            if (selected.Length == 0) panel.Children.Add(Ui.Text("选择一项待办，开始规划今天。", "Hint"));
            for (int i = 0; i < selected.Length; i++)
            {
                var item = selected[i];
                var actions = PlanningActions();
                if (i > 0) actions.Children.Add(FocusAction("上移", item, -1));
                if (i + 1 < selected.Length) actions.Children.Add(FocusAction("下移", item, 1));
                actions.Children.Add(FocusAction("移出重点", item, 0));
                panel.Children.Add(TaskRow(item, now, actions));
            }
            return Ui.Card(panel);
        }

        private Button FocusAction(string label, WorkItem item, int offset)
        {
            var button = Ui.Button(label, delegate { ChangeFocus(item.Id, offset); });
            button.Padding = new Thickness(9, 6, 9, 6);
            button.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetName(button, label + "：" + item.Title);
            return button;
        }

        private void AddFocus(string id)
        {
            var day = DateTime.Today;
            var ids = state.FocusItems(day).Select(item => item.Id).ToList();
            if (ids.Count >= WorkState.FocusLimit || ids.Contains(id) ||
                !state.Tasks.Any(item => item.Id == id && !item.CompletedUtc.HasValue && !item.ArchivedUtc.HasValue)) return;
            ids.Add(id);
            SaveFocus(day, ids);
        }

        private void ChangeFocus(string id, int offset)
        {
            var day = DateTime.Today;
            var ids = state.FocusItems(day).Select(item => item.Id).ToList();
            int index = ids.IndexOf(id);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= ids.Count) return;
            ids.RemoveAt(index);
            if (offset != 0) ids.Insert(target, id);
            SaveFocus(day, ids);
        }

        private void SaveFocus(DateTime day, List<string> ids)
        {
            var next = state.Copy();
            next.SetFocus(day, ids);
            if (Commit(next)) RefreshPlanning(DateTimeOffset.Now);
        }

        private Border BuildQuadrantPlanner(DateTimeOffset now)
        {
            var pending = state.FilterTasks(TaskFilter.Pending, now).ToArray();
            var focus = state.FocusItems(now.Date);
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("四象限", "SectionTitle"));
            panel.Children.Add(BuildQuadrantMatrix(pending));
            var heading = new DockPanel { Margin = new Thickness(0, 12, 0, 8) };
            var unclassified = QuadrantFilter(TaskQuadrant.Unclassified, pending);
            DockPanel.SetDock(unclassified, Dock.Right);
            heading.Children.Add(unclassified);
            var label = Ui.Text(QuadrantLabels[(int)quadrantFilter], "SectionTitle");
            label.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(label);
            panel.Children.Add(heading);
            var items = pending.Where(item => item.Quadrant == quadrantFilter).ToArray();
            if (items.Length == 0) panel.Children.Add(Ui.Text("这个分类暂时没有待办。", "Hint"));
            foreach (var item in items)
            {
                var controls = PlanningActions();
                var select = new ComboBox { ItemsSource = QuadrantLabels, SelectedIndex = (int)item.Quadrant, Width = 145,
                    ItemTemplate = (DataTemplate)FindResource("PlanningOption"), Margin = new Thickness(0, 0, 7, 0) };
                AutomationProperties.SetName(select, "分类：" + item.Title);
                select.SelectionChanged += delegate {
                    if (select.SelectedIndex >= 0) AssignQuadrant(item.Id, (TaskQuadrant)select.SelectedIndex);
                };
                controls.Children.Add(select);
                bool alreadyFocused = focus.Any(chosen => chosen.Id == item.Id);
                var add = Ui.Button(alreadyFocused ? "已在今日重点" : "加入今日重点", delegate { AddFocus(item.Id); }, "Quiet");
                add.IsEnabled = !alreadyFocused && focus.Length < WorkState.FocusLimit;
                if (!alreadyFocused && !add.IsEnabled) add.ToolTip = "今日重点已满三项。";
                controls.Children.Add(add);
                panel.Children.Add(TaskRow(item, now, controls));
            }
            return Ui.Card(panel);
        }

        private static StackPanel PlanningActions()
        {
            return new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        }

        private Grid BuildQuadrantMatrix(WorkItem[] pending)
        {
            var matrix = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            matrix.ColumnDefinitions.Add(new ColumnDefinition());
            matrix.ColumnDefinitions.Add(new ColumnDefinition());
            matrix.RowDefinitions.Add(new RowDefinition());
            matrix.RowDefinitions.Add(new RowDefinition());
            matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var vertical = new DockPanel { Margin = new Thickness(0, 5, 8, 5) };
            var high = Ui.Text("高 ↑", "Hint");
            var low = Ui.Text("低", "Hint");
            DockPanel.SetDock(high, Dock.Top);
            DockPanel.SetDock(low, Dock.Bottom);
            vertical.Children.Add(high);
            vertical.Children.Add(low);
            var important = Ui.Text("重要程度", "Hint");
            important.VerticalAlignment = VerticalAlignment.Center;
            important.TextWrapping = TextWrapping.Wrap;
            vertical.Children.Add(important);
            Grid.SetRowSpan(vertical, 2);
            matrix.Children.Add(vertical);

            var horizontal = new DockPanel { Margin = new Thickness(5, 0, 5, 0) };
            var left = Ui.Text("低", "Hint");
            var right = Ui.Text("高", "Hint");
            DockPanel.SetDock(right, Dock.Right);
            horizontal.Children.Add(right);
            horizontal.Children.Add(left);
            var urgent = Ui.Text("紧急程度 →", "Hint");
            urgent.HorizontalAlignment = HorizontalAlignment.Center;
            horizontal.Children.Add(urgent);
            Grid.SetRow(horizontal, 2);
            Grid.SetColumn(horizontal, 1);
            Grid.SetColumnSpan(horizontal, 2);
            matrix.Children.Add(horizontal);

            var axis = new Border { BorderBrush = Ui.Color("#DDD3EF"), BorderThickness = new Thickness(1, 0, 0, 1),
                IsHitTestVisible = false, Margin = new Thickness(0, 0, 0, 3) };
            Grid.SetColumn(axis, 1);
            Grid.SetColumnSpan(axis, 2);
            Grid.SetRowSpan(axis, 2);
            matrix.Children.Add(axis);

            var positions = new[] { TaskQuadrant.Schedule, TaskQuadrant.DoNow, TaskQuadrant.Reduce, TaskQuadrant.Delegate };
            for (int i = 0; i < positions.Length; i++)
            {
                var button = QuadrantFilter(positions[i], pending);
                Grid.SetRow(button, i / 2);
                Grid.SetColumn(button, 1 + i % 2);
                matrix.Children.Add(button);
            }
            return matrix;
        }

        private Button QuadrantFilter(TaskQuadrant quadrant, WorkItem[] pending)
        {
            int index = (int)quadrant;
            int count = pending.Count(item => item.Quadrant == quadrant);
            bool selected = quadrantFilter == quadrant;
            var button = Ui.Button("", delegate { quadrantFilter = quadrant; RefreshPlanning(DateTimeOffset.Now); });
            var colors = QuadrantColors[index];
            button.Background = Ui.Color(selected ? colors[1] : colors[0]);
            button.BorderBrush = Ui.Color(selected ? colors[2] : colors[1]);
            button.Foreground = Ui.Color(colors[2]);
            AutomationProperties.SetName(button, QuadrantLabels[index]);
            AutomationProperties.SetItemStatus(button, (selected ? "已选中，" : "") + count + " 项");
            if (quadrant == TaskQuadrant.Unclassified)
            {
                button.Content = QuadrantLabels[index] + " · " + count;
                return button;
            }

            var tile = new DockPanel();
            var amount = new TextBlock { Text = count.ToString(), Foreground = button.Foreground, FontWeight = FontWeights.SemiBold };
            var countBadge = new Border { Child = amount, Background = Ui.Color(selected ? "#FFFFFF" : colors[1]), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(countBadge, Dock.Right);
            tile.Children.Add(countBadge);
            var labels = new StackPanel();
            labels.Children.Add(new TextBlock { Text = QuadrantLabels[index], TextWrapping = TextWrapping.Wrap,
                Foreground = button.Foreground, FontWeight = FontWeights.SemiBold });
            var next = Ui.Text(QuadrantActions[index], "Hint");
            next.Margin = new Thickness(0, 6, 0, 0);
            labels.Children.Add(next);
            tile.Children.Add(labels);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(14, 11, 14, 11);
            button.Margin = new Thickness(6, 0, 0, 6);
            button.Content = tile;
            return button;
        }

        private void AssignQuadrant(string id, TaskQuadrant quadrant)
        {
            var next = state.Copy();
            var item = next.Tasks.Find(task => task.Id == id && !task.CompletedUtc.HasValue && !task.ArchivedUtc.HasValue);
            if (item == null || item.Quadrant == quadrant) return;
            item.Quadrant = quadrant;
            Commit(next);
            RefreshPlanning(DateTimeOffset.Now);
        }
    }
}
