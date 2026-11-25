using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WorkDesk
{
    internal static class MarkdownExport
    {
        public static string Plan(WorkState state, DateTimeOffset now)
        {
            var output = new StringBuilder("# 事项清单\n\n");
            AppendPlan(output, state, now, "##");
            return output.ToString();
        }

        public static string Summary(bool weekly, DateTime date, string body)
        {
            return "# " + SummaryTitle(weekly, date) + "\n\n" + WithFinalNewline(body);
        }

        public static string Archive(WorkState state, DateTimeOffset now)
        {
            var output = new StringBuilder("# 工笺备份\n\n导出日期：");
            output.Append(WorkState.PeriodKey(now.Date, false)).Append("\n\n## 事项清单\n\n");
            AppendPlan(output, state, now, "###");
            output.Append("## 工作总结\n\n");
            foreach (var summary in state.Summaries.OrderBy(s => s.Weekly).ThenByDescending(s => s.Period))
            {
                var date = DateTime.ParseExact(summary.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                output.Append("### ").Append(SummaryTitle(summary.Weekly, date)).Append("\n\n")
                    .Append(WithFinalNewline(summary.Text)).Append('\n');
            }
            if (state.Summaries.Count == 0) output.Append("暂无总结。\n");
            return output.ToString();
        }

        public static string SummaryTitle(bool weekly, DateTime date)
        {
            if (!weekly) return "日报 · " + WorkState.PeriodKey(date, false);
            var first = WorkState.WeekStart(date);
            var last = first.AddDays(Math.Min(6, (DateTime.MaxValue.Date - first).Days));
            return "周报 · " + WorkState.PeriodKey(first, false) + " 至 " + WorkState.PeriodKey(last, false);
        }

        public static void Write(StateStore store, string file, string data)
        {
            store.WriteExport(file, output => {
                using (var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true)) writer.Write(data);
            });
        }

        public static string WithImages(string data, string file, IEnumerable<SummaryImage> images)
        {
            var ids = new HashSet<string>(images.Select(image => image.Id));
            if (ids.Count == 0) return data;
            string folder = Uri.EscapeDataString(ImageFolder(file, ids));
            return SummaryImage.Reference.Replace(data, match => ids.Contains(match.Groups[1].Value)
                ? "![截图](" + folder + "/" + match.Groups[1].Value + ".png)"
                : match.Value);
        }

        public static void WriteWithImages(StateStore store, string file, string data, IEnumerable<SummaryImage> images)
        {
            var attachments = images.GroupBy(image => image.Id).Select(group => group.First()).ToArray();
            if (attachments.Length != 0)
            {
                string folder = Path.Combine(Path.GetDirectoryName(file), ImageFolder(file, attachments.Select(image => image.Id)));
                Directory.CreateDirectory(folder);
                foreach (var image in attachments)
                {
                    string path = Path.Combine(folder, image.Id + ".png");
                    byte[] png = image.ReadPng();
                    if (File.Exists(path))
                    {
                        if (!File.ReadAllBytes(path).SequenceEqual(png))
                            throw new IOException("图片目录中有同名文件，请换一个导出文件名。");
                    }
                    else store.WriteExport(path, output => output.Write(png, 0, png.Length));
                }
                data = WithImages(data, file, attachments);
            }
            Write(store, file, data);
        }

        private static string ImageFolder(string file, IEnumerable<string> ids)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(String.Concat(ids.OrderBy(id => id, StringComparer.Ordinal)));
            using (var hash = SHA256.Create())
                return Path.GetFileNameWithoutExtension(file) + "-图片-" + BitConverter.ToString(hash.ComputeHash(bytes), 0, 6).Replace("-", "").ToLowerInvariant();
        }

        private static void AppendPlan(StringBuilder output, WorkState state, DateTimeOffset now, string heading)
        {
            foreach (var group in new[] { TaskFilter.Pending, TaskFilter.Completed })
            {
                bool completed = group == TaskFilter.Completed;
                output.Append(heading).Append(completed ? " 已完成\n\n" : " 待处理\n\n");
                var items = state.FilterTasks(group, now).ToArray();
                foreach (var item in items)
                {
                    output.Append(completed ? "- [x] " : "- [ ] ").Append(Escape(Normalize(item.Title).Replace('\n', ' '))).Append('\n');
                    if (item.DueUtc.HasValue)
                        output.Append("  - 截止时间：").Append(LocalTime(item.DueUtc.Value, "yyyy-MM-dd HH:mm")).Append('\n');
                    if (item.CompletedUtc.HasValue)
                        output.Append("  - 完成时间：").Append(LocalTime(item.CompletedUtc.Value, "yyyy-MM-dd HH:mm:ss")).Append('\n');
                    if (!String.IsNullOrWhiteSpace(item.Notes))
                    {
                        output.Append("  - 备注：");
                        string[] lines = Normalize(item.Notes).Split('\n');
                        output.Append(Escape(lines[0])).Append('\n');
                        foreach (string line in lines.Skip(1)) output.Append("    ").Append(Escape(line)).Append('\n');
                    }
                }
                if (items.Length == 0) output.Append("暂无事项。\n");
                output.Append('\n');
            }
        }

        private static string LocalTime(DateTime utc, string format)
        {
            return utc.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
        }

        private static string Normalize(string text) { return text.Replace("\r\n", "\n").Replace('\r', '\n'); }

        private static string WithFinalNewline(string text)
        {
            var normalized = Normalize(text);
            return normalized.EndsWith("\n", StringComparison.Ordinal) ? normalized : normalized + "\n";
        }

        private static string Escape(string text)
        {
            var output = new StringBuilder();
            foreach (char c in text)
            {
                if ("\\`*_{}[]()#+-.!|><&".IndexOf(c) >= 0) output.Append('\\');
                output.Append(c);
            }
            return output.ToString();
        }
    }
}
