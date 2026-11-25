using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WorkDesk
{
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Entry = "WorkDesk";
        internal static string Executable { get { return Assembly.GetExecutingAssembly().Location; } }
        internal static string Command { get { return "\"" + Executable + "\" --tray"; } }

        public static bool Enabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && String.Equals(key.GetValue(Entry) as string, Command, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled && Command.Length > 260) throw new IOException("EXE 路径过长，无法注册登录启动。");
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new IOException("Windows 登录启动设置不可写入。");
                if (enabled) key.SetValue(Entry, Command, RegistryValueKind.String);
                else key.DeleteValue(Entry, false);
            }
        }
    }

    internal sealed class DesktopHost : IDisposable
    {
        private readonly Application app;
        private readonly MainWindow window;
        private readonly Forms.NotifyIcon tray;
        private readonly Drawing.Icon icon;
        private readonly Forms.ContextMenuStrip menu;
        private readonly DispatcherTimer timer;
        private readonly Queue<ReminderBatch> queue = new Queue<ReminderBatch>();
        private ReminderPopup popup;
        private bool exiting;

        public DesktopHost(Application app, MainWindow window)
        {
            this.app = app;
            this.window = window;
            icon = Drawing.Icon.ExtractAssociatedIcon(StartupRegistration.Executable);
            if (icon != null)
                window.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开工笺", null, delegate { window.ShowWorkspace(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出工笺", null, delegate { Exit(); });
            tray = new Forms.NotifyIcon { Icon = icon ?? Drawing.SystemIcons.Application, Text = "工笺 · WorkDesk", ContextMenuStrip = menu };
            tray.DoubleClick += delegate { window.ShowWorkspace(); };
            window.Closing += HideWindow;
            app.SessionEnding += SessionEnding;

            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += delegate { Tick(DateTimeOffset.Now); };
        }

        public void Start()
        {
            tray.Visible = true;
            timer.Start();
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { Tick(DateTimeOffset.Now); }));
        }

        internal void Tick(DateTimeOffset now)
        {
            if (exiting) return;
            window.RefreshClock(now);
            var batch = window.State.CollectReminders(now);
            if (!batch.Any) return;
            var next = window.State.Copy();
            next.MarkReminded(batch);
            if (!window.Commit(next, true)) return;
            queue.Enqueue(batch);
            ShowNext();
        }

        private void ShowNext()
        {
            if (exiting || popup != null || queue.Count == 0) return;
            var batch = queue.Dequeue();
            popup = new ReminderPopup(batch, window.FontFamily, delegate(bool? weekly) {
                window.ShowWorkspace();
                return weekly.HasValue ? window.OpenSummary(batch.Day, weekly.Value) : window.OpenWorkbench(true);
            });
            popup.Closed += delegate { popup = null; ShowNext(); };
            popup.Show();
        }

        private void HideWindow(object sender, CancelEventArgs e)
        {
            if (exiting) return;
            e.Cancel = true;
            window.Hide();
        }

        private void SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            exiting = true;
        }

        internal void Exit()
        {
            if (exiting) return;
            if (window.HasUnsavedSummary)
            {
                window.ShowWorkspace();
                if (!window.CanLeaveSummary()) return;
            }
            exiting = true;
            app.Shutdown();
        }

        public void Dispose()
        {
            exiting = true;
            timer.Stop();
            app.SessionEnding -= SessionEnding;
            window.Closing -= HideWindow;
            if (popup != null) popup.Close();
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            if (icon != null) icon.Dispose();
        }
    }

    internal sealed class ReminderPopup : Window
    {
        private readonly DispatcherTimer expiration;

        public ReminderPopup(ReminderBatch batch, FontFamily font, Func<bool?, bool> open)
        {
            Title = "工笺提醒";
            FontFamily = font;
            Foreground = (Brush)FindResource("Ink");
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = Math.Min(366, SystemParameters.WorkArea.Width);
            SizeToContent = SizeToContent.Height;

            var contents = new StackPanel();
            var header = new DockPanel();
            var close = Ui.Button("×", delegate { Close(); });
            close.Padding = new Thickness(9, 1, 9, 1);
            AutomationProperties.SetName(close, "关闭提醒");
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(Ui.Text("工笺提醒", "SectionTitle"));
            contents.Children.Add(header);

            if (batch.Tasks.Length > 0)
            {
                string text = batch.Tasks.Length == 1 ? batch.Tasks[0].Title : batch.Tasks[0].Title + " 等 " + batch.Tasks.Length + " 项";
                AddNotice(contents, "事项截止提醒", text, "打开事项", delegate { return open(null); });
            }
            if (batch.Daily) AddNotice(contents, "日报提醒", "该写今天的工作总结了。", "写日报", delegate { return open(false); });
            if (batch.Weekly) AddNotice(contents, "周报提醒", "该写本周的工作总结了。", "写周报", delegate { return open(true); });

            var card = Ui.Card(contents);
            card.BorderBrush = Ui.Color("#C1B0F5");
            card.Padding = new Thickness(14);
            Content = card;
            Loaded += delegate { Position(); };
            SizeChanged += delegate { Position(); };
            expiration = new DispatcherTimer { Interval = TimeSpan.FromSeconds(24) };
            expiration.Tick += delegate {
                if (!IsMouseOver && !IsKeyboardFocusWithin) Close();
            };
            Loaded += delegate { expiration.Start(); };
            Closed += delegate { expiration.Stop(); };
        }

        private void Position()
        {
            Rect work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, work.Right - Width - 16);
            Top = Math.Max(work.Top, work.Bottom - ActualHeight - 16);
        }

        private void AddNotice(StackPanel contents, string title, string text, string action, Func<bool> open)
        {
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var button = Ui.Button(action, delegate {
                if (!open()) return;
                contents.Children.Remove(row);
                if (contents.Children.Count == 1) Close();
            }, "Quiet");
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            var message = new StackPanel();
            var label = Ui.Text(title);
            label.FontWeight = FontWeights.SemiBold;
            message.Children.Add(label);
            var detail = Ui.Text(text.Replace('\r', ' ').Replace('\n', ' '), "Hint");
            detail.TextWrapping = TextWrapping.NoWrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.ToolTip = text;
            message.Children.Add(detail);
            row.Children.Add(message);
            contents.Children.Add(row);
        }
    }
}
