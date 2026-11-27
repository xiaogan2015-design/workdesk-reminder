using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

[assembly: AssemblyTitle("待办笔记 · WorkDesk")]
[assembly: AssemblyProduct("待办笔记")]
[assembly: AssemblyDescription("Windows 工作待办与总结")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyVersion("1.5.4.0")]
[assembly: AssemblyFileVersion("1.5.4.0")]

namespace WorkDesk
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var store = new StateStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDesk"));
            try
            {
                using (var mutex = new Mutex(false, @"Local\WorkDesk.Instance.v1"))
                using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WorkDesk.Activate.v1"))
                {
                    bool ownsMutex;
                    try { ownsMutex = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex) { activation.Set(); return 0; }

                    try
                    {
                        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        LoadTheme(application);
                        var window = new MainWindow(store, store.Load());
                        application.MainWindow = window;
                        var registered = ThreadPool.RegisterWaitForSingleObject(activation, delegate {
                            application.Dispatcher.BeginInvoke(new Action(window.ShowWorkspace));
                        }, null, Timeout.Infinite, false);
                        try
                        {
                            using (var desktop = new DesktopHost(application, window))
                            {
                                if (Array.IndexOf(args, "--tray") < 0) window.ShowWorkspace();
                                desktop.Start();
                                return application.Run();
                            }
                        }
                        finally { registered.Unregister(null); }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                new DeskPrompt(null, "待办笔记无法启动", "请检查下方错误详情。", ex.Message + "\n\n数据文件：" + store.FilePath).ShowDialog();
                return 1;
            }
        }

        internal static void LoadTheme(Application app)
        {
            app.Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml("Theme.xaml"));
        }

        internal static object LoadXaml(string name)
        {
            using (var stream = typeof(Program).Assembly.GetManifestResourceStream("WorkDesk." + name))
                return XamlReader.Load(stream);
        }
    }
}
