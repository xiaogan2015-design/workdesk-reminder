using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
            AppendSummaries(output, state.Summaries.OrderBy(s => s.Weekly).ThenByDescending(s => s.Period), "###");
            if (state.Summaries.Count == 0) output.Append("暂无总结。\n");
            return output.ToString();
        }

        public static void WriteSummaries(StateStore store, string file, WorkSummary[] reports, bool zip = false)
        {
            if (reports.Length == 0) return;
            var output = new StringBuilder();
            if (reports.Length > 1) output.Append("# 工作总结\n\n");
            AppendSummaries(output, reports.OrderBy(s => s.Weekly).ThenBy(s => s.Period), reports.Length == 1 ? "#" : "##");
            WriteWithImages(store, file, output.ToString(), ReportImages(reports), zip);
        }

        public static IEnumerable<SummaryImage> ReportImages(IEnumerable<WorkSummary> reports)
        {
            return reports.Where(summary => summary.Images != null).SelectMany(summary => summary.Images);
        }

        public static string ReportTitle(WorkSummary summary)
        {
            return SummaryTitle(summary.Weekly, DateTime.ParseExact(summary.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        private static void AppendSummaries(StringBuilder output, IEnumerable<WorkSummary> summaries, string heading)
        {
            foreach (var summary in summaries)
                output.Append(heading).Append(' ').Append(ReportTitle(summary)).Append("\n\n")
                    .Append(WithFinalNewline(summary.Text)).Append('\n');
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
            store.WriteExport(file, output => WriteText(output, data));
        }

        public static string WithImages(string data, IEnumerable<SummaryImage> images)
        {
            var ids = new HashSet<string>(images.Select(image => image.Id));
            if (ids.Count == 0) return data;
            return WithImageLinks(data, ids, ImageFolder(ids));
        }

        private static string WithImageLinks(string data, HashSet<string> ids, string folder)
        {
            return SummaryImage.Reference.Replace(data, match => ids.Contains(match.Groups[1].Value)
                ? "![截图](./" + folder + "/" + match.Groups[1].Value + ".png)"
                : match.Value);
        }

        public static void WriteWithImages(StateStore store, string file, string data, IEnumerable<SummaryImage> images, bool zip = false)
        {
            var attachments = images.GroupBy(image => image.Id).Select(group => group.First()).ToArray();
            var ids = new HashSet<string>(attachments.Select(image => image.Id));
            string folderName = ImageFolder(ids);
            if (ids.Count != 0) data = WithImageLinks(data, ids, folderName);
            if (zip)
            {
                if (!String.Equals(Path.GetExtension(file), ".zip", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("请为压缩包使用 .zip 扩展名。");
                WriteZip(store, file, data, folderName, attachments);
                return;
            }
            if (attachments.Length != 0)
            {
                string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)), folderName);
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
            }
            Write(store, file, data);
        }

        private static void WriteZip(StateStore store, string file, string data, string folder, SummaryImage[] images)
        {
            store.WriteExport(file, output => {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    using (var document = archive.CreateEntry(Path.GetFileNameWithoutExtension(file) + ".md").Open())
                        WriteText(document, data);
                    foreach (var image in images)
                    {
                        byte[] png = image.ReadPng();
                        using (var entry = archive.CreateEntry(folder + "/" + image.Id + ".png", CompressionLevel.NoCompression).Open())
                            entry.Write(png, 0, png.Length);
                    }
                }
            });
        }

        private static void WriteText(Stream output, string data)
        {
            using (var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true)) writer.Write(data);
        }

        private static string ImageFolder(IEnumerable<string> ids)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(String.Concat(ids.OrderBy(id => id, StringComparer.Ordinal)));
            using (var hash = SHA256.Create())
                return "workdesk-images-" + BitConverter.ToString(hash.ComputeHash(bytes), 0, 8).Replace("-", "").ToLowerInvariant();
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
