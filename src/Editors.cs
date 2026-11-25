using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkDesk
{
    internal static class Ui
    {
        public static Style Style(string key) { return (Style)Application.Current.FindResource(key); }
        public static Brush Color(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        public static TextBlock Text(string text, string style = null)
        {
            var label = new TextBlock { Text = text };
            if (style != null) label.Style = Style(style);
            return label;
        }

        public static Button Button(string text, RoutedEventHandler click, string style = null)
        {
            var button = new Button { Content = text };
            if (style != null) button.Style = Style(style);
            button.Click += click;
            return button;
        }

        public static Border Card(UIElement content)
        {
            return new Border { Style = Style("Card"), Child = content };
        }

        public static void Label(Panel panel, string label, FrameworkElement control)
        {
            var caption = Text(label);
            caption.FontWeight = FontWeights.SemiBold;
            caption.Margin = new Thickness(0, 11, 0, 5);
            AutomationProperties.SetName(control, label);
            panel.Children.Add(caption);
            panel.Children.Add(control);
        }

        public static bool TryTime(string text, out TimeSpan time)
        {
            return TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture,
                out time) && time < TimeSpan.FromDays(1);
        }

        public static string Clock(int minute)
        {
            return TimeSpan.FromMinutes(minute).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class DateTimeInput : StackPanel
    {
        private readonly DatePicker date;
        private readonly TextBox time;
        private bool invalidDate;

        public DateTimeInput(string name, DateTime local)
        {
            Orientation = Orientation.Horizontal;
            date = new DatePicker {
                Width = 150, SelectedDate = local.Date, DisplayDateStart = new DateTime(1900, 1, 1),
                DisplayDateEnd = new DateTime(9998, 12, 31)
            };
            date.DateValidationError += delegate { invalidDate = true; };
            date.SelectedDateChanged += delegate { invalidDate = false; };
            date.PreviewKeyDown += delegate { invalidDate = false; };
            time = new TextBox {
                Text = local.ToString("HH:mm", CultureInfo.InvariantCulture), Width = 72, MaxLength = 5,
                Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(date, name + "日期");
            AutomationProperties.SetName(time, name + "时间");
            Children.Add(date);
            Children.Add(time);
            Children.Add(new TextBlock {
                Text = "24 小时制", Style = Ui.Style("Hint"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
        }

        public bool TryUtc(DateTime? original, out DateTime result)
        {
            result = default(DateTime);
            TimeSpan clock;
            if (invalidDate || !date.SelectedDate.HasValue || !Ui.TryTime(time.Text, out clock)) return false;
            var local = DateTime.SpecifyKind(date.SelectedDate.Value + clock, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local)) return false;
            if (original.HasValue)
            {
                var prior = original.Value.ToLocalTime();
                if (prior.Date == local.Date && prior.Hour == local.Hour && prior.Minute == local.Minute)
                {
                    result = original.Value;
                    return true;
                }
            }
            result = TimeZoneInfo.ConvertTimeToUtc(local);
            return true;
        }
    }

    internal sealed class TaskEditor : Window
    {
        private readonly WorkItem original;
        private readonly Func<WorkItem, bool> persist;
        private readonly TextBox title = new TextBox { MaxLength = 120 };
        private readonly TextBox notes = new TextBox {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 98,
            MaxLength = 4000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        private readonly CheckBox scheduled = new CheckBox { Content = "设置截止时间" };
        private readonly CheckBox completed = new CheckBox { Content = "记录为已完成" };
        private readonly TextBox lead = new TextBox { Width = 72, MaxLength = 5, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly DateTimeInput dueInput;
        private readonly DateTimeInput completedInput;
        private readonly TextBlock error = Ui.Text("", "Hint");

        public TaskEditor(Window owner, WorkItem item, Func<WorkItem, bool> persist)
        {
            original = item ?? new WorkItem();
            this.persist = persist;
            Owner = owner;
            Title = item == null ? "新建事项" : "编辑事项";
            FontFamily = owner.FontFamily;
            Icon = owner.Icon;
            Background = Ui.Color("#F5F5FB");
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Width = 500;
            MaxHeight = SystemParameters.WorkArea.Height;
            SizeToContent = SizeToContent.Height;

            title.Text = original.Title ?? "";
            notes.Text = original.Notes ?? "";
            lead.Text = original.LeadMinutes.ToString(CultureInfo.InvariantCulture);
            scheduled.IsChecked = item == null || original.DueUtc.HasValue;
            completed.IsChecked = original.CompletedUtc.HasValue;
            dueInput = new DateTimeInput("截止", original.DueUtc.HasValue ? original.DueUtc.Value.ToLocalTime() : DateTime.Now.AddHours(1));
            completedInput = new DateTimeInput("完成", original.CompletedUtc.HasValue ? original.CompletedUtc.Value.ToLocalTime() : DateTime.Now);

            var form = new StackPanel { Margin = new Thickness(21, 15, 21, 17) };
            form.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeights.Bold, FontSize = 23 });
            Ui.Label(form, "事项标题", title);
            Ui.Label(form, "备注（选填）", notes);
            scheduled.Margin = new Thickness(0, 15, 0, 7);
            form.Children.Add(scheduled);
            var scheduleFields = new StackPanel();
            scheduleFields.Children.Add(dueInput);
            var leadRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            leadRow.Children.Add(new TextBlock { Text = "提前", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            AutomationProperties.SetName(lead, "提前提醒分钟数");
            leadRow.Children.Add(lead);
            leadRow.Children.Add(new TextBlock { Text = "分钟提醒（0 为到点提醒）", Style = Ui.Style("Hint"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            scheduleFields.Children.Add(leadRow);
            form.Children.Add(scheduleFields);
            completed.Margin = new Thickness(0, 15, 0, 7);
            form.Children.Add(completed);
            form.Children.Add(completedInput);

            Action updateFields = delegate {
                scheduleFields.Visibility = scheduled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                completedInput.Visibility = completed.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            };
            scheduled.Checked += delegate { updateFields(); };
            scheduled.Unchecked += delegate { updateFields(); };
            completed.Checked += delegate { updateFields(); };
            completed.Unchecked += delegate { updateFields(); };
            updateFields();

            error.Foreground = Ui.Color("#A12F49");
            error.Margin = new Thickness(0, 8, 0, 0);
            form.Children.Add(error);
            var actions = new DockPanel { Margin = new Thickness(0, 11, 0, 0), LastChildFill = false };
            if (item != null)
            {
                var delete = Ui.Button("删除事项", Delete);
                delete.Foreground = Ui.Color("#A12F49");
                actions.Children.Add(delete);
            }
            var save = Ui.Button("保存事项", Save, "Primary");
            save.IsDefault = true;
            DockPanel.SetDock(save, Dock.Right);
            actions.Children.Add(save);
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(cancel, Dock.Right);
            actions.Children.Add(cancel);
            form.Children.Add(actions);
            Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += delegate { Keyboard.Focus(title); };
        }

        private void Save(object sender, RoutedEventArgs e)
        {
            string name = title.Text.Trim();
            if (name.Length == 0) { error.Text = "请输入事项标题。"; title.Focus(); return; }
            int minutes = original.LeadMinutes;
            DateTime? deadline = null;
            DateTime? finished = null;
            DateTime value;
            if (scheduled.IsChecked == true)
            {
                if (!dueInput.TryUtc(original.DueUtc, out value)) { error.Text = "请选择有效的截止日期和时间（如 17:00）。"; return; }
                deadline = value;
                if (!Int32.TryParse(lead.Text, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
                    minutes < 0 || minutes > WorkItem.MaxLead)
                {
                    error.Text = "请输入 0 到 43200 之间的整数分钟。";
                    lead.Focus();
                    return;
                }
            }
            if (completed.IsChecked == true)
            {
                if (!completedInput.TryUtc(original.CompletedUtc, out value) || value > DateTime.UtcNow)
                {
                    error.Text = "请选择有效且不晚于当前时刻的完成时间。";
                    return;
                }
                finished = value;
            }
            var result = original.Copy();
            result.Title = name;
            result.Notes = notes.Text.Trim();
            result.SetSchedule(deadline, minutes);
            result.SetCompletion(finished);
            if (persist(result)) DialogResult = true;
            else error.Text = "事项未保存。请检查数据文件的访问权限和磁盘空间后重试。";
        }

        private void Delete(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "删除后将从事项清单中移除。", "删除事项", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (persist(null)) DialogResult = true;
            else error.Text = "删除未保存。请检查数据文件的访问权限和磁盘空间后重试。";
        }
    }
}
