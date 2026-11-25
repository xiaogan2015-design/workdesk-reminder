using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            var add = window.Element<Button>("NewTask");
            var rows = window.Element<StackPanel>("TaskRows");
            Check(window.Element<TextBlock>("PendingCount").Text == "0", "An empty dashboard starts at zero.");
            WithEditor(add, editor => {
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
                Check(Descendants<TextBlock>(editor).Any(t => t.Text == "请输入事项标题。"), "Blank titles stay in the editor.");
                Find<TextBox>(editor, "事项标题").Text = "核对计划";
                Find<TextBox>(editor, "备注（选填）").Text = "两条说明\n续行";
                Find<TextBox>(editor, "截止时间").Text = "25:99";
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
                Check(Descendants<TextBlock>(editor).Any(t => t.Text.StartsWith("请选择有效的截止")), "Invalid deadlines stay in the editor.");
                Descendants<CheckBox>(editor).Single(c => (string)c.Content == "设置截止时间").IsChecked = false;
                Click(Descendants<Button>(editor).Single(b => (string)b.Content == "保存事项"));
            });
            Check(window.State.Tasks.Single().Title == "核对计划" && !window.State.Tasks[0].DueUtc.HasValue, "Save creates an unscheduled item.");
            Check(window.Element<TextBlock>("PendingCount").Text == "1" && rows.Children.Count == 1, "Saving refreshes the dashboard.");
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
            reopened.Close();
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
