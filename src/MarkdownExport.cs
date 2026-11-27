using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

        public static void WriteSummaries(StateStore store, string file, WorkSummary[] reports)
        {
            if (reports.Length == 0) return;
            var output = new StringBuilder();
            if (reports.Length > 1) output.Append("# 工作总结\n\n");
            AppendSummaries(output, reports.OrderBy(s => s.Weekly).ThenBy(s => s.Period), reports.Length == 1 ? "#" : "##");
            WriteWithImages(store, file, output.ToString(), ReportImages(reports));
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

        public static string WithEmbeddedImages(string data, IEnumerable<SummaryImage> images)
        {
            return WithImageLinks(data, IndexImages(images), DataUrl);
        }

        private static Dictionary<string, SummaryImage> IndexImages(IEnumerable<SummaryImage> images)
        {
            return images.GroupBy(image => image.Id).ToDictionary(group => group.Key, group => group.First());
        }

        private static string DataUrl(SummaryImage image)
        {
            return "data:image/png;base64," + image.Png.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");
        }

        private static string WithImageLinks(string data, Dictionary<string, SummaryImage> images, Func<SummaryImage, string> address)
        {
            if (images.Count == 0) return data;
            return SummaryImage.Reference.Replace(data, match => {
                SummaryImage image;
                return images.TryGetValue(match.Groups[1].Value, out image) ? "![截图](" + address(image) + ")" : match.Value;
            });
        }

        public static void WriteWithImages(StateStore store, string file, string data, IEnumerable<SummaryImage> images)
        {
            var attachments = IndexImages(images);
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".md":
                    Write(store, file, WithImageLinks(data, attachments, DataUrl));
                    break;
                case ".html":
                    WriteHtml(store, file, data, attachments);
                    break;
                case ".zip":
                    WriteZip(store, file, data, attachments);
                    break;
                default:
                    throw new IOException("请使用 .md、.html 或 .zip 扩展名。");
            }
        }

        private static void WriteZip(StateStore store, string file, string data, Dictionary<string, SummaryImage> images)
        {
            string folder = ImageFolder(images.Keys);
            string markdown = WithImageLinks(data, images, image => "./" + folder + "/" + image.Id + ".png");
            store.WriteExport(file, output => {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    using (var document = archive.CreateEntry(Path.GetFileNameWithoutExtension(file) + ".md").Open())
                        WriteText(document, markdown);
                    foreach (var image in images.Values)
                    {
                        byte[] png = image.ReadPng();
                        using (var entry = archive.CreateEntry(folder + "/" + image.Id + ".png", CompressionLevel.NoCompression).Open())
                            entry.Write(png, 0, png.Length);
                    }
                }
            });
        }

        private static void WriteHtml(StateStore store, string file, string data, Dictionary<string, SummaryImage> images)
        {
            store.WriteExport(file, output => {
                using (var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true))
                {
                    writer.Write("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
                    writer.Write("<title>工笺 · 工作记录</title><style>body{background:#f5f5fb;color:#211a37;font:16px/1.65 'Segoe UI','Microsoft YaHei UI',sans-serif;margin:0;padding:24px}main{max-width:900px;margin:auto;background:white;border:1px solid #e8e4f1;border-radius:14px;padding:clamp(16px,4vw,32px);white-space:pre-wrap;overflow-wrap:anywhere}img{max-width:100%;height:auto;display:block;margin:.65em 0;border-radius:8px}</style></head><body><main>");
                    int start = 0;
                    foreach (Match match in SummaryImage.Reference.Matches(data))
                    {
                        SummaryImage image;
                        if (!images.TryGetValue(match.Groups[1].Value, out image)) continue;
                        writer.Write(WebUtility.HtmlEncode(data.Substring(start, match.Index - start)));
                        writer.Write("<img alt=\"截图\" src=\"");
                        writer.Write(DataUrl(image));
                        writer.Write("\">");
                        start = match.Index + match.Length;
                    }
                    writer.Write(WebUtility.HtmlEncode(data.Substring(start)));
                    writer.Write("</main></body></html>");
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
            foreach (var group in new[] { TaskFilter.Pending, TaskFilter.Completed, TaskFilter.Archived })
            {
                var items = state.FilterTasks(group, now).ToArray();
                if (group == TaskFilter.Archived && items.Length == 0) continue;
                string label = group == TaskFilter.Archived ? "已归档" : group == TaskFilter.Completed ? "已完成" : "待处理";
                output.Append(heading).Append(' ').Append(label).Append("\n\n");
                foreach (var item in items)
                {
                    output.Append(item.CompletedUtc.HasValue ? "- [x] " : "- [ ] ").Append(Escape(Normalize(item.Title).Replace('\n', ' '))).Append('\n');
                    if (item.DueUtc.HasValue)
                        output.Append("  - 截止时间：").Append(LocalTime(item.DueUtc.Value, "yyyy-MM-dd HH:mm")).Append('\n');
                    if (item.CompletedUtc.HasValue)
                        output.Append("  - 完成时间：").Append(LocalTime(item.CompletedUtc.Value, "yyyy-MM-dd HH:mm:ss")).Append('\n');
                    if (item.ArchivedUtc.HasValue)
                        output.Append("  - 归档时间：").Append(LocalTime(item.ArchivedUtc.Value, "yyyy-MM-dd HH:mm:ss")).Append('\n');
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
