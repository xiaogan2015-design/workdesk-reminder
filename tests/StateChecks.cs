using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkDesk;

internal static class StateChecks
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    [STAThread]
    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("{0}: {1}", ex.GetType().FullName, ex.Message);
            Console.Error.WriteLine("Completed {0} assertions before failure.", assertions);
            return 1;
        }
    }

    private static void Run()
    {
        var today = new DateTime(2026, 11, 27, 17, 0, 0, DateTimeKind.Local);
        var now = new DateTimeOffset(today);
        var state = new WorkState();
        state.Settings.WeeklyEnabled = true;
        var upcoming = new WorkItem { Title = "复核发布清单", Notes = "等复核结果", DueUtc = now.UtcDateTime.AddMinutes(30) };
        var overdue = new WorkItem { Title = "待回顾", DueUtc = now.UtcDateTime.AddDays(-1) };
        var unscheduled = new WorkItem { Title = "整理资料" };
        state.Tasks.AddRange(new[] { unscheduled, upcoming, overdue });

        Check(state.FilterTasks(TaskFilter.Pending, now).SequenceEqual(new[] { overdue, upcoming, unscheduled }), "Pending order");
        Check(state.FilterTasks(TaskFilter.Today, now).Single() == upcoming, "Today's local date filter");
        Check(state.FilterTasks(TaskFilter.Overdue, now).Single() == overdue, "Overdue filter");
        Check(state.CollectReminders(now.AddSeconds(-1)).Tasks.Single() == overdue, "Task trigger before its boundary");
        Check(!state.CollectReminders(now.AddSeconds(-1)).Daily, "Daily trigger before its boundary");
        Check(!state.CollectReminders(now.AddDays(1)).Weekly, "Weekly trigger on scheduled weekday only");

        var batch = state.CollectReminders(now);
        Check(batch.Daily && batch.Weekly && batch.Tasks.Length == 2, "All reminders on their exact boundary");
        var notified = state.Copy();
        notified.MarkReminded(batch);
        Check(!notified.CollectReminders(now).Any && state.CollectReminders(now).Any, "Deduplication without mutating the prior state");
        Check(notified.CollectReminders(now.AddDays(1)).Daily, "Daily re-arms on another date");
        Check(notified.CollectReminders(now.AddDays(7)).Weekly, "Weekly re-arms on another week");

        var item = notified.Tasks.Find(t => t.Id == upcoming.Id);
        item.SetSchedule(item.DueUtc, item.LeadMinutes);
        Check(!item.ShouldRemind(now.UtcDateTime), "Saving an unchanged schedule keeps its reminder marker");
        item.SetSchedule(item.DueUtc, 45);
        Check(item.ShouldRemind(now.UtcDateTime), "Changing the lead re-arms the reminder");
        item.SetSchedule(item.DueUtc.Value.AddDays(1), 45);
        Check(!item.ShouldRemind(now.UtcDateTime), "Changing the deadline reschedules the reminder");
        item.SetSchedule(now.UtcDateTime, 0);
        Check(item.ShouldRemind(now.UtcDateTime), "Zero lead reminds at the deadline");
        item.SetCompletion(now.UtcDateTime);
        Check(notified.FilterTasks(TaskFilter.Completed, now).Single().CompletedUtc == now.UtcDateTime, "Automatic completion time");
        item.SetCompletion(now.UtcDateTime.AddHours(-2));
        Check(item.CompletedUtc == now.UtcDateTime.AddHours(-2) && !item.ShouldRemind(now.UtcDateTime), "Manual completion time suppresses reminders");
        item.ReminderShown = true;
        item.SetCompletion(null);
        Check(item.ShouldRemind(now.UtcDateTime), "Reopening clears the prior reminder marker");

        state.SetSummary(today, false, "今日工作", now.UtcDateTime);
        state.SetSummary(today, true, "本周工作", now.UtcDateTime);
        state.SetSummary(today.AddDays(2), true, "本周工作已更新", now.UtcDateTime);
        Check(state.Summaries.Count == 2 && state.FindSummary(today.AddDays(-4), true).Text == "本周工作已更新", "One summary per Monday-based week");
        Check(!state.CollectReminders(now).Daily && !state.CollectReminders(now).Weekly, "Existing summaries suppress reminders");
        Check(WorkState.PeriodKey(new DateTime(2027, 1, 1), true) == "2026-12-28", "Week across year boundary");
        state.Tasks.Single(t => t.Id == unscheduled.Id).Title = "整理 [资料]";
        state.Tasks.Single(t => t.Id == unscheduled.Id).SetCompletion(now.UtcDateTime);
        string plan = MarkdownExport.Plan(state, now);
        Check(plan.Contains("- [ ] 复核发布清单") && plan.Contains("- [x] 整理 \\[资料\\]") && plan.Contains("完成时间："), "Markdown plan preserves status, times, and literal titles");
        string formatted = "    保留 Markdown 缩进\r\n\r\n- 工作事项";
        string dailyMarkdown = MarkdownExport.Summary(false, today, formatted);
        Check(dailyMarkdown.Contains("日报 · 2026-11-27\n\n    保留 Markdown 缩进\n\n- 工作事项"), "Individual Markdown preserves formatting");
        string archive = MarkdownExport.Archive(state, now);
        Check(archive.Contains("日报 · 2026-11-27") && archive.Contains("周报 · 2026-11-23 至 2026-11-29") &&
            archive.Contains("本周工作已更新") && archive.Contains("复核发布清单"), "Archive contains tasks and reports");

        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkDesk.Checks." + Guid.NewGuid().ToString("N"));
        var store = new StateStore(folder);
        var backup = new StateStore(Path.Combine(folder, "backup"));
        string markdownFile = Path.Combine(folder, "archive.md");
        string picturesFile = Path.Combine(folder, "汇总 示例.md");
        string selectedFile = Path.Combine(folder, "selected.md");
        string selectedZip = Path.Combine(folder, "带图 总结.zip");
        var image = new SummaryImage {
            Id = Guid.NewGuid().ToString("N"),
            Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9a8AAAAASUVORK5CYII="
        };
        string picturesDirectory = null;
        string pngFile = null;
        string selectedDirectory = null;
        string selectedPng = null;
        try
        {
            Check(store.Load().Tasks.Count == 0, "Fresh installation");
            notified.Appearance = null;
            store.Save(notified);
            var loaded = new StateStore(folder).Load();
            Check(loaded.Appearance.InterfaceSize == 14 && loaded.Appearance.EditorSize == 14 && loaded.Appearance.InterfaceFont == null,
                "Legacy settings without typography use the existing defaults.");
            Check(loaded.Tasks.Any(t => t.Title == upcoming.Title) && !loaded.CollectReminders(now).Daily, "Round-trip Chinese text and reminder deduplication");
            Check(loaded.Tasks.Single(t => t.Id == upcoming.Id).DueUtc == item.DueUtc, "Round-trip UTC timestamps");
            store.Save(state);
            Check(new StateStore(folder).Load().FindSummary(today, true).Text == "本周工作已更新", "Round-trip weekly summary");
            File.WriteAllText(store.FilePath, "invalid JSON");
            var recovery = new StateStore(folder);
            Check(recovery.Load().LastDailyReminder != null && recovery.Recovered, "Read prior version when primary is corrupt");
            recovery.Save(state);
            Check(!recovery.Recovered && new StateStore(folder).Load().Summaries.Count == 2, "Restore primary without replacing last valid backup");
            Directory.CreateDirectory(Path.GetDirectoryName(backup.FilePath));
            state.Appearance = new AppearanceSettings { InterfaceFont = "Segoe UI", InterfaceSize = 17, EditorFont = "Consolas", EditorSize = 26 };
            store.BackUp(backup.FilePath, state);
            var backupState = backup.Load();
            Check(backupState.Tasks.Count == state.Tasks.Count && backupState.Summaries.Count == 2 && backupState.Settings.WeeklyEnabled,
                "JSON backup can be loaded as complete application data");
            Check(backupState.Appearance.InterfaceFont == "Segoe UI" && backupState.Appearance.InterfaceSize == 17 &&
                backupState.Appearance.EditorFont == "Consolas" && backupState.Appearance.EditorSize == 26,
                "Complete backups preserve the two font configurations.");
            MarkdownExport.Write(store, markdownFile, archive);
            Check(File.ReadAllText(markdownFile) == archive, "Markdown file preserves Chinese text and line breaks");
            state.SetSummary(today, false, image.Marker, now.UtcDateTime, new System.Collections.Generic.List<SummaryImage> { image });
            store.BackUp(backup.FilePath, state);
            var imageBackup = backup.Load().FindSummary(today, false);
            Check(imageBackup.Images.Single().Png == image.Png && imageBackup.Text == image.Marker, "JSON backups preserve image-only reports and their bytes");
            string illustrated = MarkdownExport.Archive(state, now);
            MarkdownExport.WriteWithImages(store, picturesFile, illustrated, imageBackup.Images);
            picturesDirectory = Directory.GetDirectories(folder, "workdesk-images-*").Single();
            pngFile = Path.Combine(picturesDirectory, image.Id + ".png");
            string link = "./" + Path.GetFileName(picturesDirectory) + "/" + image.Id + ".png";
            Check(File.ReadAllText(picturesFile).Contains(link) && MarkdownExport.WithImages(illustrated, imageBackup.Images).Contains(link) &&
                File.ReadAllBytes(pngFile).SequenceEqual(image.ReadPng()), "Markdown and copied text use the same ASCII relative image link in a Chinese path");
            MarkdownExport.WriteWithImages(store, picturesFile, illustrated, imageBackup.Images);
            Check(File.ReadAllText(picturesFile).Contains(link), "Re-exporting Markdown safely reuses the same image");
            var oldPixels = new byte[] { 12, 56, 209, 0, 211, 100, 7, 0 };
            var legacyBitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, oldPixels, 8);
            var oldImage = new SummaryImage { Id = Guid.NewGuid().ToString("N"), Png = SummaryImageCodec.Encode(legacyBitmap) };
            var legacyDay = today.AddDays(-1);
            state.SetSummary(legacyDay, false, "需要恢复的截图\n" + oldImage.Marker, now.UtcDateTime,
                new System.Collections.Generic.List<SummaryImage> { oldImage });
            state.SetSummary(today.AddDays(-2), false, "没有勾选的总结", now.UtcDateTime);
            store.Save(state);
            var restored = store.Load();
            var recoveredImage = restored.FindSummary(legacyDay, false).Images.Single();
            Check(RgbAndAlpha(recoveredImage).SequenceEqual(new byte[] { 12, 56, 209, 255, 211, 100, 7, 255 }),
                "Existing screenshots with zero alpha recover their original colors.");
            var transparent = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 12, 56, 209, 0, 211, 100, 7, 127 }, 8);
            Check(ReferenceEquals(transparent, SummaryImageCodec.RestoreScreenshotOpacity(transparent)),
                "Intentional transparency remains unchanged.");
            var selection = new[] { restored.FindSummary(legacyDay, false), restored.FindSummary(today, true) };
            MarkdownExport.WriteSummaries(store, selectedFile, selection);
            selectedDirectory = Directory.GetDirectories(folder, "workdesk-images-*")
                .Single(path => File.Exists(Path.Combine(path, oldImage.Id + ".png")));
            selectedPng = Path.Combine(selectedDirectory, oldImage.Id + ".png");
            string selectedText = File.ReadAllText(selectedFile);
            Check(selectedText.Contains("# 工作总结") && selectedText.Contains("## 日报 · 2026-11-26") &&
                selectedText.Contains("## 周报 · 2026-11-23") && !selectedText.Contains("2026-11-25") && !selectedText.Contains(image.Id),
                "Selected export merges only checked reports.");
            Check(File.ReadAllBytes(selectedPng).SequenceEqual(recoveredImage.ReadPng()) && Directory.GetFiles(selectedDirectory).Length == 1,
                "Selected export includes only the selected image, with repaired opacity.");
            MarkdownExport.WriteSummaries(store, selectedZip, selection, true);
            using (var stream = File.OpenRead(selectedZip))
            using (var archiveFile = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var zippedPng = archiveFile.GetEntry(Path.GetFileName(selectedDirectory) + "/" + oldImage.Id + ".png");
                using (var body = new StreamReader(archiveFile.GetEntry("带图 总结.md").Open()))
                    Check(body.ReadToEnd() == selectedText && archiveFile.Entries.Count == 2, "ZIP includes only the selected Markdown and its relative image");
                using (var embedded = zippedPng.Open())
                using (var png = new MemoryStream())
                {
                    embedded.CopyTo(png);
                    Check(png.ToArray().SequenceEqual(recoveredImage.ReadPng()), "Packaged PNG preserves the exported bytes");
                }
            }
            try { MarkdownExport.Write(store, store.FilePath, archive); throw new Exception("The live file cannot be exported over."); }
            catch (IOException) { Check(store.Load().Tasks.Count == 3, "Export cannot replace the live state file"); }
        }
        finally
        {
            File.Delete(store.FilePath);
            File.Delete(store.FilePath + ".bak");
            File.Delete(store.FilePath + ".tmp");
            File.Delete(markdownFile);
            File.Delete(picturesFile);
            File.Delete(selectedFile);
            File.Delete(selectedZip);
            if (pngFile != null) File.Delete(pngFile);
            if (selectedPng != null) File.Delete(selectedPng);
            if (Directory.Exists(picturesDirectory)) Directory.Delete(picturesDirectory);
            if (Directory.Exists(selectedDirectory)) Directory.Delete(selectedDirectory);
            File.Delete(backup.FilePath);
            if (Directory.Exists(Path.GetDirectoryName(backup.FilePath))) Directory.Delete(Path.GetDirectoryName(backup.FilePath));
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
        Console.WriteLine("Passed {0} state and reminder checks.", assertions);
    }

    private static byte[] RgbAndAlpha(SummaryImage image)
    {
        using (var stream = new MemoryStream(image.ReadPng()))
        {
            var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return pixels;
        }
    }
}
