using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkDesk
{
    internal sealed class SummaryEditor : RichTextBox
    {
        private readonly Action<string> feedback;

        public SummaryEditor(WorkSummary saved, Action<string> feedback)
        {
            this.feedback = feedback;
            MinHeight = 250;
            MaxHeight = 410;
            AcceptsReturn = true;
            AllowDrop = false;
            IsDocumentEnabled = true;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Document.PagePadding = new Thickness(0);
            Document.LineHeight = 22;
            Document.Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph)) {
                Setters = { new Setter(Paragraph.MarginProperty, new Thickness(0)) }
            });
            Load(saved == null ? "" : saved.Text, saved == null ? null : saved.Images);
            CommandManager.AddPreviewCanExecuteHandler(this, delegate(object sender, CanExecuteRoutedEventArgs e) {
                if (e.Command == EditingCommands.ToggleBullets || e.Command == EditingCommands.ToggleNumbering)
                {
                    e.CanExecute = false;
                    e.Handled = true;
                    return;
                }
                if (e.Command != ApplicationCommands.Paste || IsReadOnly) return;
                try
                {
                    if (!Clipboard.ContainsImage()) return;
                    e.CanExecute = true;
                    e.Handled = true;
                }
                catch (System.Runtime.InteropServices.ExternalException) { }
            });
            CommandManager.AddPreviewExecutedHandler(this, delegate(object sender, ExecutedRoutedEventArgs e) {
                if (e.Command == ApplicationCommands.Paste && PasteImage(false)) e.Handled = true;
            });
            DataObject.AddPastingHandler(this, delegate(object sender, DataObjectPastingEventArgs e) {
                if (e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) e.FormatToApply = DataFormats.UnicodeText;
                else if (e.SourceDataObject.GetDataPresent(DataFormats.Text)) e.FormatToApply = DataFormats.Text;
                else e.CancelCommand();
            });
        }

        public bool PasteImage(bool explicitRequest)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    if (explicitRequest) feedback("剪贴板里没有图片。请先截屏或复制图片。");
                    return false;
                }
                var image = Clipboard.GetImage();
                if (image == null) throw new IOException("无法读取剪贴板图像。");
                InsertImage(image);
                return true;
            }
            catch (Exception ex)
            {
                feedback("图片粘贴失败。" + ex.Message);
                return true;
            }
        }

        internal void InsertImage(BitmapSource bitmap)
        {
            double scale = (double)SummaryImage.MaxEdge / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            if (scale < 1) bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var attachment = new SummaryImage { Id = Guid.NewGuid().ToString("N") };
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                if (stream.Length > SummaryImage.MaxBytes) throw new IOException("单张图片保存后不得超过 12 MB。");
                attachment.Png = Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
            }

            var figure = MakeFigure(attachment);
            Focus();
            LockCurrentUndoUnit();
            BeginChange();
            try
            {
                Selection.Text = "";
                var inline = new InlineUIContainer(figure, CaretPosition) { Tag = attachment };
                CaretPosition = inline.ElementEnd.GetInsertionPosition(LogicalDirection.Forward) ?? inline.ElementEnd;
            }
            finally { EndChange(); }
            LockCurrentUndoUnit();
        }

        public string ReadMarkdown(List<SummaryImage> images = null)
        {
            var text = new StringBuilder();
            bool first = true;
            foreach (var paragraph in Document.Blocks.OfType<Paragraph>())
            {
                if (!first) text.Append('\n');
                AppendInlines(paragraph.Inlines, text, images);
                first = false;
            }
            return text.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static void AppendInlines(InlineCollection inlines, StringBuilder text, List<SummaryImage> images)
        {
            foreach (var inline in inlines)
            {
                var run = inline as Run;
                var span = inline as Span;
                var figure = inline as InlineUIContainer;
                if (run != null) text.Append(run.Text);
                else if (inline is LineBreak) text.Append('\n');
                else if (span != null) AppendInlines(span.Inlines, text, images);
                else if (figure != null && figure.Tag is SummaryImage)
                {
                    var attachment = (SummaryImage)figure.Tag;
                    text.Append(attachment.Marker);
                    if (images != null && !images.Any(image => image.Id == attachment.Id)) images.Add(attachment);
                }
            }
        }

        private void Load(string markdown, List<SummaryImage> images)
        {
            Document.Blocks.Clear();
            var paragraph = new Paragraph();
            Document.Blocks.Add(paragraph);
            var byId = (images ?? new List<SummaryImage>()).ToDictionary(image => image.Id);
            int position = 0;
            foreach (System.Text.RegularExpressions.Match match in SummaryImage.Reference.Matches(markdown))
            {
                SummaryImage attachment;
                if (!byId.TryGetValue(match.Groups[1].Value, out attachment)) continue;
                AppendText(paragraph, markdown.Substring(position, match.Index - position));
                paragraph.Inlines.Add(new InlineUIContainer(MakeFigure(attachment)) { Tag = attachment });
                position = match.Index + match.Length;
            }
            AppendText(paragraph, markdown.Substring(position));
        }

        private static void AppendText(Paragraph paragraph, string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                if (lines[i].Length > 0) paragraph.Inlines.Add(new Run(lines[i]));
            }
        }

        private Border MakeFigure(SummaryImage attachment)
        {
            var figure = new Border {
                CornerRadius = new CornerRadius(8), Background = Ui.Color("#F5F2FC"), BorderBrush = Ui.Color("#DDD4F3"),
                BorderThickness = new Thickness(1), Padding = new Thickness(5), Margin = new Thickness(2, 4, 4, 4),
                ToolTip = "右键复制或移除图片"
            };
            AutomationProperties.SetName(figure, "总结截图");
            try
            {
                var source = Decode(attachment, 640);
                figure.Child = new Image { Source = source, MaxWidth = 264, MaxHeight = 164, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            }
            catch (IOException) { figure.Child = Ui.Text("图片预览不可用", "Hint"); }
            catch (NotSupportedException) { figure.Child = Ui.Text("图片预览不可用", "Hint"); }

            var context = new ContextMenu();
            var copy = new MenuItem { Header = "复制图片" };
            copy.Click += delegate {
                try { Clipboard.SetImage(Decode(attachment, 0)); feedback("已复制图片。"); }
                catch (Exception ex) { feedback("无法复制图片。" + ex.Message); }
            };
            var remove = new MenuItem { Header = "移除图片" };
            remove.Click += delegate {
                var parent = figure.Parent as InlineUIContainer;
                if (parent != null) new TextRange(parent.ElementStart, parent.ElementEnd).Text = "";
            };
            context.Items.Add(copy);
            context.Items.Add(remove);
            figure.ContextMenu = context;
            return figure;
        }

        private static BitmapSource Decode(SummaryImage attachment, int width)
        {
            using (var stream = new MemoryStream(attachment.ReadPng()))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = width;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
    }
}
