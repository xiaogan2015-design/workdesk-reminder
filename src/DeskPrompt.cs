using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WorkDesk
{
    internal sealed class DeskPrompt : Window
    {
        public DeskPrompt(Window owner, string title, string message, string details = null, string confirm = null)
        {
            bool destructive = confirm != null;
            var accent = Ui.Color(destructive ? "#A12449" : "#6040CB");
            Title = title;
            if (owner != null) Owner = owner;
            FontFamily = TryFindResource("InterfaceFont") as FontFamily ?? new FontFamily("Segoe UI, Microsoft YaHei UI");
            Foreground = Ui.Color("#211A37");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = owner == null;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            Width = Math.Min(510, SystemParameters.WorkArea.Width);
            MaxHeight = SystemParameters.WorkArea.Height;
            SizeToContent = SizeToContent.Height;

            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var close = Ui.Button("×", delegate { Close(); });
            close.Padding = new Thickness(8, 2, 8, 2);
            close.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetName(close, "关闭提示");
            DockPanel.SetDock(close, Dock.Right);
            heading.Children.Add(close);
            heading.Children.Add(new Border {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 11, 0),
                Background = Ui.Color(destructive ? "#FCECF1" : "#EDE8FD"), Child = new TextBlock {
                    Text = destructive ? "\uE74D" : "\uE783", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 18, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var label = new TextBlock {
                Text = title, FontSize = (double)(TryFindResource("SectionSize") ?? 19.0), FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
            };
            heading.Children.Add(label);

            var body = new StackPanel();
            body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Color("#534B65") });
            if (!String.IsNullOrEmpty(details))
            {
                var diagnostic = new TextBox {
                    Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 145, Margin = new Thickness(0, 11, 0, 0), Background = Ui.Color("#F7F4FC")
                };
                AutomationProperties.SetName(diagnostic, "错误详情");
                body.Children.Add(diagnostic);
            }

            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var safe = Ui.Button(destructive ? "取消" : "知道了", delegate { Close(); });
            safe.IsCancel = true;
            safe.IsDefault = true;
            if (destructive)
            {
                safe.Margin = new Thickness(0, 0, 8, 0);
                actions.Children.Add(safe);
                var remove = Ui.Button(confirm, delegate { DialogResult = true; });
                remove.Background = accent;
                remove.BorderBrush = accent;
                remove.Foreground = Brushes.White;
                actions.Children.Add(remove);
            }
            else
            {
                safe.Style = TryFindResource("Primary") as Style;
                actions.Children.Add(safe);
            }
            body.Children.Add(actions);

            var card = new Border {
                Background = Brushes.White, BorderBrush = Ui.Color("#DED5F1"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 17, 20, 17), Margin = new Thickness(12),
                Effect = new DropShadowEffect { BlurRadius = 17, ShadowDepth = 2, Opacity = 0.23, Color = Color.FromRgb(33, 26, 55) },
                Child = body
            };
            Content = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += delegate { safe.Focus(); };
        }
    }
}
