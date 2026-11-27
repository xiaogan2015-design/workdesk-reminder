using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WorkDesk
{
    internal sealed partial class MainWindow
    {
        private static readonly string[] QuadrantLabels = {
            "待判断", "重要且紧急", "重要不紧急", "紧急不重要", "不重要不紧急"
        };
        private static readonly string[] QuadrantActions = { "", "先处理", "安排时间", "协商转交", "酌情删减" };

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
            var picker = new ComboBox { ItemsSource = available, DisplayMemberPath = "Title", Width = 270,
                SelectedIndex = available.Length == 0 ? -1 : 0, MaxDropDownHeight = 280, Margin = new Thickness(0, 0, 8, 6) };
            AutomationProperties.SetName(picker, "选择待办事项");
            var add = Ui.Button("加入今日重点", delegate {
                var item = picker.SelectedItem as WorkItem;
                if (item != null) AddFocus(item.Id);
            }, "Primary");
            picker.IsEnabled = add.IsEnabled = available.Length > 0 && selected.Length < WorkState.FocusLimit;
            if (selected.Length == WorkState.FocusLimit) add.ToolTip = "移除一项后可加入其他事项。";
            var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) };
            controls.Children.Add(picker);
            controls.Children.Add(add);
            panel.Children.Add(controls);
            if (selected.Length == 0) panel.Children.Add(Ui.Text("选择一项待办，开始规划今天。", "Hint"));
            for (int i = 0; i < selected.Length; i++)
            {
                var item = selected[i];
                var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
                if (i > 0) actions.Children.Add(FocusAction("上移", item, -1));
                if (i + 1 < selected.Length) actions.Children.Add(FocusAction("下移", item, 1));
                actions.Children.Add(FocusAction("移出重点", item, 0));
                panel.Children.Add(TaskRow(item, now));
                panel.Children.Add(actions);
            }
            return Ui.Card(panel);
        }

        private Button FocusAction(string label, WorkItem item, int offset)
        {
            var button = Ui.Button(label, delegate { ChangeFocus(item.Id, offset); });
            button.Padding = new Thickness(9, 4, 9, 4);
            button.Margin = new Thickness(5, 0, 0, 0);
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
            var hint = Ui.Text("按重要性和紧急程度分类，点击象限查看待办。", "Hint");
            hint.Margin = new Thickness(0, 4, 0, 9);
            panel.Children.Add(hint);
            var matrix = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 6) };
            for (int i = 1; i < QuadrantLabels.Length; i++)
                matrix.Children.Add(QuadrantFilter((TaskQuadrant)i, pending));
            panel.Children.Add(matrix);
            var unclassified = QuadrantFilter(TaskQuadrant.Unclassified, pending);
            unclassified.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(unclassified);
            var heading = Ui.Text(QuadrantLabels[(int)quadrantFilter], "SectionTitle");
            heading.Margin = new Thickness(0, 12, 0, 8);
            panel.Children.Add(heading);
            var items = pending.Where(item => item.Quadrant == quadrantFilter).ToArray();
            if (items.Length == 0) panel.Children.Add(Ui.Text("这个分类暂时没有待办。", "Hint"));
            foreach (var item in items)
            {
                panel.Children.Add(TaskRow(item, now));
                var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
                var select = new ComboBox { ItemsSource = QuadrantLabels, SelectedIndex = (int)item.Quadrant, Width = 145,
                    Margin = new Thickness(0, 0, 7, 0) };
                AutomationProperties.SetName(select, "分类：" + item.Title);
                select.SelectionChanged += delegate {
                    if (select.SelectedIndex >= 0) AssignQuadrant(item.Id, (TaskQuadrant)select.SelectedIndex);
                };
                controls.Children.Add(select);
                bool alreadyFocused = focus.Any(chosen => chosen.Id == item.Id);
                var add = Ui.Button(alreadyFocused ? "已在今日重点" : "加入今日重点", delegate { AddFocus(item.Id); });
                add.IsEnabled = !alreadyFocused && focus.Length < WorkState.FocusLimit;
                if (!alreadyFocused && !add.IsEnabled) add.ToolTip = "今日重点已满三项。";
                controls.Children.Add(add);
                panel.Children.Add(controls);
            }
            return Ui.Card(panel);
        }

        private Button QuadrantFilter(TaskQuadrant quadrant, WorkItem[] pending)
        {
            int index = (int)quadrant;
            string title = QuadrantLabels[index] + " · " + pending.Count(item => item.Quadrant == quadrant);
            var button = Ui.Button("", delegate { quadrantFilter = quadrant; RefreshPlanning(DateTimeOffset.Now); },
                quadrantFilter == quadrant ? "Quiet" : null);
            var labels = new StackPanel();
            labels.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            if (index > 0) labels.Children.Add(Ui.Text(QuadrantActions[index], "Hint"));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Content = labels;
            AutomationProperties.SetName(button, QuadrantLabels[index]);
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
