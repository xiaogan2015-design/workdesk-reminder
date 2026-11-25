using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

[assembly: AssemblyTitle("工笺 · WorkDesk")]
[assembly: AssemblyProduct("WorkDesk")]
[assembly: AssemblyDescription("Windows 工作待办与总结")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WorkDesk
{
    internal static class Program
    {
        [STAThread]
        public static int Main()
        {
            var store = new StateStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDesk"));
            try
            {
                var application = new Application();
                LoadTheme(application);
                return application.Run(new MainWindow(store, store.Load()));
            }
            catch (Exception ex)
            {
                MessageBox.Show("工笺无法启动。\n\n" + ex.Message + "\n\n数据文件：" + store.FilePath,
                    "工笺", MessageBoxButton.OK, MessageBoxImage.Error);
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
