using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace WorkDesk
{
    internal enum SummaryDeparture { Stay, Save, Discard }

    internal sealed class SummaryLeaveDialog : Window
    {
        public SummaryDeparture Choice { get; private set; }

        public SummaryLeaveDialog(Window owner)
        {
            Owner = owner;
            Title = "总结尚未保存";
            SetResourceReference(FontFamilyProperty, "InterfaceFont");
            Foreground = owner.Foreground;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = Math.Min(490, SystemParameters.WorkArea.Width);
            MaxHeight = SystemParameters.WorkArea.Height;
            SizeToContent = SizeToContent.Height;

            var content = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var close = Ui.Button("×", delegate { Close(); });
            close.Padding = new Thickness(8, 2, 8, 2);
            close.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetName(close, "关闭确认框并继续编辑");
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            var icon = new Border {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(9), Background = Ui.Color("#EDE8FD"),
                Margin = new Thickness(0, 0, 11, 0), Child = new TextBlock {
                    Text = "\uE70B", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17,
                    Foreground = Ui.Color("#6040CB"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            header.Children.Add(icon);
            var heading = Ui.Text(Title, "SectionTitle");
            heading.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(heading);
            content.Children.Add(header);
            content.Children.Add(Ui.Text("这篇总结有未保存的修改。离开前可以保存，或继续编辑。", "Hint"));

            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 19, 0, 0) };
            var stay = new Button { Content = "继续编辑", IsCancel = true, Margin = new Thickness(0, 0, 8, 5) };
            var discard = Ui.Button("不保存并离开", delegate { Finish(SummaryDeparture.Discard); });
            discard.Foreground = Ui.Color("#A12449");
            discard.Margin = new Thickness(0, 0, 8, 5);
            var save = Ui.Button("保存并离开", delegate { Finish(SummaryDeparture.Save); }, "Primary");
            save.IsDefault = true;
            save.Margin = new Thickness(0, 0, 0, 5);
            actions.Children.Add(stay);
            actions.Children.Add(discard);
            actions.Children.Add(save);
            content.Children.Add(actions);
            var card = Ui.Card(content);
            card.Padding = new Thickness(21, 17, 21, 14);
            card.BorderBrush = Ui.Color("#CFC1F0");
            Content = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += delegate { stay.Focus(); };
        }

        private void Finish(SummaryDeparture choice)
        {
            Choice = choice;
            DialogResult = true;
        }
    }
}
