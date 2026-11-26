using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WorkDesk
{
    internal sealed class SummaryExportDialog : Window
    {
        private readonly List<CheckBox> choices = new List<CheckBox>();
        private readonly TextBlock count = Ui.Text("", "Hint");
        private readonly Button export;

        public WorkSummary[] SelectedReports { get; private set; }

        public SummaryExportDialog(Window owner, IEnumerable<WorkSummary> reports)
        {
            var ordered = reports.OrderBy(report => report.Weekly).ThenByDescending(report => report.Period).ToArray();
            Owner = owner;
            Icon = owner.Icon;
            SetResourceReference(FontFamilyProperty, "InterfaceFont");
            Foreground = owner.Foreground;
            Background = Ui.Color("#F5F5FB");
            Title = "勾选导出工作总结";
            Width = 550;
            MinHeight = 298;
            Height = Math.Min(SystemParameters.WorkArea.Height, Math.Min(555, Math.Max(298, 210 + 26 * ordered.Length)));
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new DockPanel();
            var header = new StackPanel();
            header.Children.Add(Ui.Text("选择已保存的总结", "SectionTitle"));
            var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
            var clear = Ui.Button("清空勾选", delegate { SelectAll(false); });
            clear.Margin = new Thickness(7, 0, 0, 0);
            DockPanel.SetDock(clear, Dock.Right);
            actions.Children.Add(clear);
            var all = Ui.Button("全选", delegate { SelectAll(true); });
            DockPanel.SetDock(all, Dock.Right);
            actions.Children.Add(all);
            count.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(count);
            header.Children.Add(actions);
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);

            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
            export = Ui.Button("导出所选总结", delegate {
                SelectedReports = choices.Where(choice => choice.IsChecked == true).Select(choice => (WorkSummary)choice.Tag).ToArray();
                if (SelectedReports.Length > 0) DialogResult = true;
            }, "Primary");
            export.IsDefault = true;
            DockPanel.SetDock(export, Dock.Right);
            footer.Children.Add(export);
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(cancel, Dock.Right);
            footer.Children.Add(cancel);
            DockPanel.SetDock(footer, Dock.Bottom);
            panel.Children.Add(footer);

            var list = new StackPanel();
            foreach (var report in ordered)
            {
                var choice = new CheckBox { Content = MarkdownExport.ReportTitle(report), Tag = report, Margin = new Thickness(0, 3, 0, 5) };
                choice.Checked += delegate { RefreshCount(); };
                choice.Unchecked += delegate { RefreshCount(); };
                choices.Add(choice);
                list.Children.Add(choice);
            }
            panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var card = Ui.Card(panel);
            card.Margin = new Thickness(13);
            Content = card;
            RefreshCount();
        }

        private void SelectAll(bool value)
        {
            foreach (var choice in choices) choice.IsChecked = value;
        }

        private void RefreshCount()
        {
            int selected = choices.Count(choice => choice.IsChecked == true);
            count.Text = "已选 " + selected + " / " + choices.Count + " 篇";
            export.IsEnabled = selected > 0;
        }
    }
}
