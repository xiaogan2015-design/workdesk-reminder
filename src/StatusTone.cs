using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WorkDesk
{
    internal sealed class StatusTone
    {
        public static readonly StatusTone Pending = new StatusTone("待处理", "#305382", "#F5F8FD", "#D9E5F5", "#E4EDF9");
        public static readonly StatusTone Upcoming = new StatusTone("即将到期", "#87430A", "#FFF8EE", "#EED5B1", "#FFEBD2");
        public static readonly StatusTone Overdue = new StatusTone("已逾期", "#A12449", "#FFF3F5", "#EFC7D5", "#FCE1EA");
        public static readonly StatusTone Completed = new StatusTone("已完成", "#076149", "#EFFAF5", "#C7E6D7", "#D9F1E7");
        public static readonly StatusTone Weekly = new StatusTone("周报提醒", "#59369D", "#F6F1FF", "#E0D4F6", "#EDE3FD");

        public readonly string Label;
        public readonly Brush Text;
        public readonly Brush Badge;
        private readonly Brush surface;
        private readonly Brush edge;

        private StatusTone(string label, string text, string surface, string edge, string badge)
        {
            Label = label;
            Text = Ui.Color(text);
            Badge = Ui.Color(badge);
            this.surface = Ui.Color(surface);
            this.edge = Ui.Color(edge);
        }

        public Border Card(Thickness padding)
        {
            return new Border {
                Background = surface, BorderBrush = edge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = padding
            };
        }
    }
}
