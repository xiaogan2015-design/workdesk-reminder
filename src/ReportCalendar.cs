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
        private StackPanel reportCalendar;
        private DateTime calendarMonth;
        private bool calendarYear;

        private UIElement BuildReportCalendar()
        {
            calendarMonth = new DateTime(summaryDate.Year, summaryDate.Month, 1);
            reportCalendar = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            return reportCalendar;
        }

        private void RefreshReportCalendar()
        {
            var recorded = new HashSet<string>(state.Summaries.Where(report => report.Weekly == summaryWeekly).Select(report => report.Period));
            reportCalendar.Children.Clear();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            var year = CalendarButton("年", "按年查看记录", delegate { calendarYear = true; RefreshReportCalendar(); }, calendarYear ? "Quiet" : null);
            var month = CalendarButton("月", "按月查看记录", delegate { calendarYear = false; RefreshReportCalendar(); }, calendarYear ? null : "Quiet");
            DockPanel.SetDock(year, Dock.Right);
            DockPanel.SetDock(month, Dock.Right);
            header.Children.Add(year);
            header.Children.Add(month);
            var title = Ui.Text("记录日历");
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);
            reportCalendar.Children.Add(header);

            var navigation = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var back = CalendarButton("‹", calendarYear ? "上一年" : "上一个月", delegate { ShiftCalendar(-1); });
            var next = CalendarButton("›", calendarYear ? "下一年" : "下一个月", delegate { ShiftCalendar(1); });
            back.IsEnabled = calendarMonth.Year > 1 || (!calendarYear && calendarMonth.Month > 1);
            next.IsEnabled = calendarMonth.Year < 9999 || (!calendarYear && calendarMonth.Month < 12);
            DockPanel.SetDock(next, Dock.Right);
            navigation.Children.Add(back);
            navigation.Children.Add(next);
            var period = Ui.Text(calendarMonth.ToString(calendarYear ? "yyyy 年" : "yyyy 年 M 月", Chinese));
            period.HorizontalAlignment = HorizontalAlignment.Center;
            period.VerticalAlignment = VerticalAlignment.Center;
            navigation.Children.Add(period);
            reportCalendar.Children.Add(navigation);
            reportCalendar.Children.Add(calendarYear ? YearCalendar(recorded) : MonthCalendar(recorded));
            var legend = Ui.Text(summaryWeekly ? "紫色为已保存周报，标在周一。" : "紫色为已保存日报。", "Hint");
            legend.Margin = new Thickness(0, 6, 0, 0);
            reportCalendar.Children.Add(legend);
        }

        private void ShiftCalendar(int offset)
        {
            calendarMonth = calendarYear ? calendarMonth.AddYears(offset) : calendarMonth.AddMonths(offset);
            RefreshReportCalendar();
        }

        private static Button CalendarButton(string text, string name, RoutedEventHandler click, string style = null)
        {
            var button = Ui.Button(text, click, style);
            button.Padding = new Thickness(6, 3, 6, 3);
            button.Margin = new Thickness(2, 0, 0, 0);
            button.ToolTip = name;
            AutomationProperties.SetName(button, name);
            return button;
        }

        private UniformGrid MonthCalendar(HashSet<string> recorded)
        {
            var grid = new UniformGrid { Columns = 7 };
            foreach (string weekday in new[] { "一", "二", "三", "四", "五", "六", "日" })
                grid.Children.Add(new TextBlock { Text = weekday, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 3) });
            int offset = ((int)calendarMonth.DayOfWeek + 6) % 7;
            for (int i = 0; i < offset; i++) grid.Children.Add(new Border());
            for (int i = 0; i < DateTime.DaysInMonth(calendarMonth.Year, calendarMonth.Month); i++)
            {
                var day = calendarMonth.AddDays(i);
                var key = WorkState.PeriodKey(day, false);
                var number = day.Day.ToString(Chinese);
                if (recorded.Contains(key))
                {
                    var button = CalendarButton(number, (summaryWeekly ? "打开周报 " : "打开日报 ") + key,
                        delegate { OpenSummary(day, summaryWeekly); }, "Quiet");
                    button.Padding = new Thickness(0, 2, 0, 2);
                    button.Margin = new Thickness(1);
                    grid.Children.Add(button);
                }
                else grid.Children.Add(new Border {
                    Background = Ui.Color("#F4F1F9"), CornerRadius = new CornerRadius(4), Margin = new Thickness(1),
                    Child = new TextBlock { Text = number, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 3) }
                });
            }
            return grid;
        }

        private UniformGrid YearCalendar(HashSet<string> recorded)
        {
            var year = new UniformGrid { Columns = 3 };
            var marked = Ui.Color("#7854EE");
            var empty = Ui.Color("#EAE5F2");
            for (int month = 1; month <= 12; month++)
            {
                var first = new DateTime(calendarMonth.Year, month, 1);
                var dots = new UniformGrid { Columns = 7, Rows = 6 };
                int offset = ((int)first.DayOfWeek + 6) % 7;
                for (int blank = 0; blank < offset; blank++) dots.Children.Add(new Border());
                int count = 0;
                for (int i = 0; i < DateTime.DaysInMonth(first.Year, first.Month); i++)
                {
                    bool saved = recorded.Contains(WorkState.PeriodKey(first.AddDays(i), false));
                    if (saved) count++;
                    dots.Children.Add(new Border { Background = saved ? marked : empty, Height = 4,
                        Margin = new Thickness(0.5), CornerRadius = new CornerRadius(1) });
                }
                var content = new StackPanel();
                content.Children.Add(Ui.Text(month + "月", "Hint"));
                content.Children.Add(dots);
                var button = CalendarButton("", "查看 " + month + "月，已记录 " + count + " 篇", delegate {
                    calendarMonth = first;
                    calendarYear = false;
                    RefreshReportCalendar();
                });
                button.Content = content;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Padding = new Thickness(3);
                button.Margin = new Thickness(1);
                year.Children.Add(button);
            }
            return year;
        }
    }
}
