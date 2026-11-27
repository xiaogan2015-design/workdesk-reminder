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
            Console.Error.WriteLine(ex);
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
        Check(archive.StartsWith("# 待办笔记备份\n", StringComparison.Ordinal) &&
            archive.Contains("日报 · 2026-11-27") && archive.Contains("周报 · 2026-11-23 至 2026-11-29") &&
            archive.Contains("本周工作已更新") && archive.Contains("复核发布清单"), "Archive contains tasks and reports");

        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkDesk.Checks." + Guid.NewGuid().ToString("N"));
        var store = new StateStore(folder);
        var backup = new StateStore(Path.Combine(folder, "backup"));
        string markdownFile = Path.Combine(folder, "archive.md");
        string picturesFile = Path.Combine(folder, "汇总 示例.md");
        string selectedFile = Path.Combine(folder, "selected.md");
        string allFile = Path.Combine(folder, "all.md");
        string htmlFile = Path.Combine(folder, "带图 总结.html");
        string selectedZip = Path.Combine(folder, "带图 总结.zip");
        var image = new SummaryImage {
            Id = Guid.NewGuid().ToString("N"),
            Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9a8AAAAASUVORK5CYII="
        };
        try
        {
            Check(store.Load().Tasks.Count == 0, "Fresh installation");
            notified.Appearance = null;
            notified.Archiving = null;
            store.Save(notified);
            var loaded = new StateStore(folder).Load();
            Check(loaded.Appearance.InterfaceSize == 14 && loaded.Appearance.EditorSize == 14 && loaded.Appearance.InterfaceFont == null,
                "Legacy settings without typography use the existing defaults.");
            Check(loaded.Archiving.CompletedDays == 0 && loaded.Archiving.OverdueDays == 0 && loaded.Tasks.All(t => !t.ArchivedUtc.HasValue),
                "Legacy files keep both archive rules disabled and their tasks active.");
            Check(loaded.Tasks.Any(t => t.Title == upcoming.Title) && !loaded.CollectReminders(now).Daily, "Round-trip Chinese text and reminder deduplication");
            Check(loaded.Tasks.All(t => t.Quadrant == TaskQuadrant.Unclassified) && loaded.FocusItems(today).Length == 0,
                "Older tasks start unclassified and no focus entries are inferred.");
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
            string link = "![截图](data:image/png;base64," + image.Png + ")";
            string copied = MarkdownExport.WithEmbeddedImages(illustrated, imageBackup.Images);
            Check(copied.Contains(link) && File.ReadAllText(picturesFile) == copied &&
                Directory.GetDirectories(folder, "workdesk-images-*").Length == 0,
                "Both Markdown export and copied Markdown include the PNG without a folder or local file link.");
            MarkdownExport.WriteWithImages(store, picturesFile, illustrated, imageBackup.Images);
            Check(File.ReadAllText(picturesFile).Contains(link), "Re-exporting Markdown safely reuses the same image");
            var oldPixels = new byte[] { 12, 56, 209, 0, 211, 100, 7, 0 };
            var legacyBitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, oldPixels, 8);
            var oldImage = new SummaryImage { Id = Guid.NewGuid().ToString("N"), Png = SummaryImageCodec.Encode(legacyBitmap) };
            var legacyDay = today.AddDays(-1);
            state.SetSummary(legacyDay, false, "需要恢复的截图\n" + oldImage.Marker, now.UtcDateTime,
                new System.Collections.Generic.List<SummaryImage> { oldImage });
            state.SetSummary(today.AddDays(-2), false, "没有勾选的总结\n<script>alert(1)</script>", now.UtcDateTime);
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
            string selectedText = File.ReadAllText(selectedFile);
            Check(selectedText.Contains("# 工作总结") && selectedText.Contains("## 日报 · 2026-11-26") &&
                selectedText.Contains("## 周报 · 2026-11-23") && !selectedText.Contains("2026-11-25") && !selectedText.Contains(image.Png),
                "Selected export merges only checked reports.");
            Check(selectedText.Contains("data:image/png;base64," + recoveredImage.Png) && !selectedText.Contains("workdesk-image:"),
                "Selected export directly embeds only the chosen image, with repaired opacity.");
            MarkdownExport.WriteSummaries(store, allFile, restored.Summaries.ToArray());
            string allText = File.ReadAllText(allFile);
            Check(allText.Contains(link) && allText.Contains(recoveredImage.Png) && !allText.Contains("workdesk-image:"),
                "Selecting all saved reports embeds pictures from multiple reports.");
            MarkdownExport.WriteSummaries(store, htmlFile, restored.Summaries.ToArray());
            string html = File.ReadAllText(htmlFile);
            Check(html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;") && !html.Contains("<script>") &&
                html.Contains("<img alt=\"截图\" src=\"data:image/png;base64," + image.Png + "\">") &&
                html.Contains("<img alt=\"截图\" src=\"data:image/png;base64," + recoveredImage.Png + "\">"),
                "The browser export embeds both images and treats authored text as text.");
            MarkdownExport.WriteSummaries(store, selectedZip, selection);
            using (var stream = File.OpenRead(selectedZip))
            using (var archiveFile = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var zippedPng = archiveFile.Entries.Single(entry => entry.FullName.EndsWith("/" + oldImage.Id + ".png", StringComparison.Ordinal));
                using (var body = new StreamReader(archiveFile.GetEntry("带图 总结.md").Open()))
                    Check(body.ReadToEnd() == selectedText.Replace("data:image/png;base64," + recoveredImage.Png, "./" + zippedPng.FullName) &&
                        archiveFile.Entries.Count == 2, "ZIP includes only the selected Markdown and its relative image");
                using (var embedded = zippedPng.Open())
                using (var png = new MemoryStream())
                {
                    embedded.CopyTo(png);
                    Check(png.ToArray().SequenceEqual(recoveredImage.ReadPng()), "Packaged PNG preserves the exported bytes");
                }
            }
            try { MarkdownExport.Write(store, store.FilePath, archive); throw new Exception("The live file cannot be exported over."); }
            catch (IOException) { Check(store.Load().Tasks.Count == 3, "Export cannot replace the live state file"); }
            CheckTaskArchiving(store, backup, now);
            CheckStorageAndBackups(folder, image, today);
            CheckDailyChecklist(store, backup, today);
        }
        finally
        {
            File.Delete(store.FilePath);
            File.Delete(store.FilePath + ".bak");
            File.Delete(store.FilePath + ".tmp");
            File.Delete(markdownFile);
            File.Delete(picturesFile);
            File.Delete(selectedFile);
            File.Delete(allFile);
            File.Delete(htmlFile);
            File.Delete(selectedZip);
            if (File.Exists(backup.FilePath)) File.Delete(backup.FilePath);
            if (Directory.Exists(Path.GetDirectoryName(backup.FilePath))) Directory.Delete(Path.GetDirectoryName(backup.FilePath));
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        Console.WriteLine("Passed {0} state and reminder checks.", assertions);
    }

    private static void CheckDailyChecklist(StateStore store, StateStore backup, DateTime today)
    {
        var state = store.Load();
        var steps = state.GetDailyChecklist(today);
        Check(steps.Length == 6 && !steps.Any(step => step), "Existing data starts with an empty daily checklist.");
        var next = state.Copy();
        steps[1] = true;
        next.SetDailyChecklist(today, steps);
        steps[1] = false;
        Check(next.GetDailyChecklist(today)[1] && !state.GetDailyChecklist(today)[1], "A staged checklist update leaves the current state and caller array independent.");
        store.Save(next);
        store.BackUp(backup.FilePath, next);
        Check(store.Load().GetDailyChecklist(today)[1] && backup.Load().GetDailyChecklist(today)[1], "The local state and JSON backup retain daily checklist progress.");
        Check(!store.Load().GetDailyChecklist(today.AddDays(1)).Any(step => step), "A new local date starts a new checklist.");
    }

    private static void CheckTaskArchiving(StateStore store, StateStore backup, DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var done = new WorkItem { Title = "已完成的归档事项", CompletedUtc = utc.AddDays(-7), DueUtc = utc.AddDays(-50) };
        var overdue = new WorkItem { Title = "已逾期的归档事项", DueUtc = utc.AddDays(-30), Notes = "保留这份备注" };
        var recent = new WorkItem { Title = "近期完成", CompletedUtc = utc.AddDays(-2), DueUtc = utc.AddDays(-50) };
        var noDeadline = new WorkItem { Title = "无截止时间" };
        var state = new WorkState();
        state.Tasks.AddRange(new[] { done, overdue, recent, noDeadline });
        state.ArchiveAgedTasks(utc);
        Check(state.Tasks.All(t => !t.ArchivedUtc.HasValue), "Automatic archiving is off by default.");

        state.Archiving = new ArchiveSettings { CompletedDays = 7 };
        state.ArchiveAgedTasks(utc.AddTicks(-1));
        Check(state.Tasks.All(t => !t.ArchivedUtc.HasValue), "Archiving waits until the exact threshold.");
        state.ArchiveAgedTasks(utc);
        Check(state.FilterTasks(TaskFilter.Archived, now).Single() == done, "The completed rule works independently of the overdue rule.");
        state.Archiving.OverdueDays = 30;
        state.ArchiveAgedTasks(utc.AddSeconds(1));
        Check(state.FilterTasks(TaskFilter.Archived, now).SequenceEqual(new[] { overdue, done }), "Archive is newest first.");
        Check(state.FilterTasks(TaskFilter.Pending, now).Single() == noDeadline &&
            state.FilterTasks(TaskFilter.Completed, now).Single() == recent && !state.FilterTasks(TaskFilter.Overdue, now).Any(),
            "Archived tasks leave the active lists and a completed task uses its completion date, regardless of its old deadline.");
        Check(state.CollectReminders(now).Tasks.Length == 0, "An archived overdue task stops generating reminders.");
        string export = MarkdownExport.Plan(state, now);
        Check(export.Contains("## 已归档\n\n- [ ] 已逾期的归档事项") && export.Contains("- [x] 已完成的归档事项") &&
            export.Contains("归档时间：") && MarkdownExport.Archive(state, now).Contains("### 已归档"), "Both Markdown exports keep archived items and their statuses.");

        done.RestoreFromArchive();
        done.SetSchedule(done.DueUtc, 45);
        state.ArchiveAgedTasks(utc.AddMinutes(1));
        Check(!done.ArchivedUtc.HasValue && done.CompletedUtc == utc.AddDays(-7), "Restoring preserves completion and a lead-only edit does not re-arm archiving.");
        store.Save(state);
        store.BackUp(backup.FilePath, state);
        foreach (var loaded in new[] { store.Load(), backup.Load() })
        {
            var archived = loaded.FilterTasks(TaskFilter.Archived, now).Single();
            var restored = loaded.Tasks.Single(t => t.Id == done.Id);
            Check(archived.ArchivedUtc == overdue.ArchivedUtc && archived.Notes == overdue.Notes &&
                restored.AutoArchivePaused && loaded.Archiving.CompletedDays == 7 && loaded.Archiving.OverdueDays == 30,
                "Saved files preserve archive timestamps, rules, and restored-task exceptions.");
        }
        done.SetCompletion(utc.AddDays(-8));
        state.ArchiveAgedTasks(utc.AddMinutes(1));
        Check(done.ArchivedUtc.HasValue, "A changed completion time re-arms archiving for a restored task.");
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

    private static void CheckStorageAndBackups(string folder, SummaryImage image, DateTime today)
    {
        var january = new DateTime(2026, 1, 31);
        Check(new BackupSchedule { Cadence = BackupCadence.Weekly }.NextDate(january, today) == january.AddDays(7) &&
            new BackupSchedule { Cadence = BackupCadence.Monthly }.NextDate(january, today) == new DateTime(2026, 2, 28),
            "Weekly and monthly backups use local dates and handle short months.");
        var interval = new BackupSchedule { Cadence = BackupCadence.Custom, Days = 9 };
        Check(interval.NextDate(january, today) == january.AddDays(9) && interval.NextDate(null, today) == today.Date &&
            !new BackupSchedule { Cadence = BackupCadence.Custom, Days = 0 }.IsValid &&
            new BackupSchedule { Cadence = BackupCadence.Disabled }.NextDate(null, today) == null,
            "Custom scheduling rejects zero days, runs the first backup immediately and can be disabled.");

        var legacyStore = new StateStore(Path.Combine(folder, "legacy-data"));
        Directory.CreateDirectory(legacyStore.DataDirectory);
        var current = new WorkState { Backup = interval };
        var active = new WorkItem { Title = "当前事项" };
        var historic = new WorkItem { Title = "归档事项", ArchivedUtc = january.ToUniversalTime(), CompletedUtc = january.AddDays(-9).ToUniversalTime() };
        current.Tasks.AddRange(new[] { active, historic });
        current.SetSummary(today, false, "截图总结\n" + image.Marker, today.ToUniversalTime(),
            new System.Collections.Generic.List<SummaryImage> { image });
        new StateStore(folder).BackUp(legacyStore.FilePath, current);
        var loaded = legacyStore.Load();
        Check(loaded.Version == 1 && loaded.Tasks.Count == 2 && loaded.FindSummary(today, false).Images.Count == 1,
            "Legacy single-file JSON restores tasks and images.");
        legacyStore.Save(loaded);
        var raw = File.ReadAllText(legacyStore.FilePath);
        var reportFile = Directory.GetFiles(Path.Combine(legacyStore.DataDirectory, "reports")).Single();
        var archiveFile = Directory.GetFiles(Path.Combine(legacyStore.DataDirectory, "archives")).Single();
        Check(!raw.Contains("截图总结") && !raw.Contains("归档事项") && !raw.Contains(image.Png) &&
            Directory.GetFiles(Path.Combine(legacyStore.DataDirectory, "images"), "*.png").Length == 1 &&
            raw.Contains(active.Id) && archiveFile.Contains(WorkState.ArchiveMonthKey(historic)),
            "The active JSON references report, image and archive files instead of embedding them.");

        var split = legacyStore.Load();
        var archivedMonth = split.ArchiveMonths.Single();
        var report = split.FindSummary(today, false);
        Check(!archivedMonth.Items.IsValueCreated && !report.Body.IsValueCreated, "Startup only reads history indexes.");
        split.Tasks.Single().Title = "当前事项已改";
        legacyStore.Save(split);
        Check(!archivedMonth.Items.IsValueCreated && !report.Body.IsValueCreated &&
            Directory.GetFiles(Path.GetDirectoryName(reportFile)).Single() == reportFile &&
            Directory.GetFiles(Path.GetDirectoryName(archiveFile)).Single() == archiveFile,
            "Editing an active task leaves stored history untouched and unloaded.");

        legacyStore.WriteAutomaticBackup(today.Date, split);
        var backupPath = Directory.GetFiles(legacyStore.BackupDirectory).Single();
        var restoreStore = new StateStore(Path.Combine(folder, "restored-backup"));
        Directory.CreateDirectory(restoreStore.DataDirectory);
        File.Copy(backupPath, restoreStore.FilePath);
        var recovered = restoreStore.Load();
        Check(legacyStore.LatestAutomaticBackup() == today.Date && recovered.Tasks.Count == 2 &&
            recovered.Tasks.Any(item => item.Id == historic.Id && item.ArchivedUtc == historic.ArchivedUtc) &&
            recovered.FindSummary(today, false).Images.Single().Png == image.Png && recovered.Backup.Days == 9,
            "The scheduled JSON alone restores the active task, history, original image and settings.");

        var targetPath = Path.Combine(folder, "relocated-data");
        var original = File.ReadAllText(legacyStore.FilePath);
        WorkState moved;
        var target = legacyStore.Relocate(targetPath, split, out moved);
        var reopened = StateStore.Open(legacyStore.DataDirectory);
        Check(File.ReadAllText(legacyStore.FilePath) == original && reopened.DataDirectory == target.DataDirectory &&
            reopened.LatestAutomaticBackup() == today.Date && moved.FindSummary(today, false).Images.Single().Png == image.Png,
            "Relocation keeps the source and previous backups intact and is remembered after restart.");
        try { target.Relocate(legacyStore.DataDirectory, moved, out moved); throw new Exception("Migration overwrote a nonempty folder."); }
        catch (IOException) { Check(StateStore.Open(legacyStore.DataDirectory).DataDirectory == targetPath, "A rejected migration keeps the previous location."); }
        moved = reopened.Load();
        Check(moved.RestoreArchivedTask(historic.Id, archivedMonth.Month), "An archived item can be restored from its month.");
        reopened.Save(moved);
        Check(reopened.Load().Tasks.Any(item => item.Id == historic.Id) && !reopened.Load().ArchiveMonthKeys().Any(),
            "Restoration removes the archive index and preserves the active item.");
        File.WriteAllText(reopened.FilePath, "invalid JSON");
        var backupRecovery = StateStore.Open(legacyStore.DataDirectory);
        Check(backupRecovery.Load().ArchivedTasksIn(archivedMonth.Month).Single().Id == historic.Id && backupRecovery.Recovered,
            "The previous state file still references usable archive data after restoration.");
        string absentPath = Path.Combine(folder, "unavailable");
        File.WriteAllText(Path.Combine(legacyStore.DataDirectory, "data-directory.txt"), absentPath);
        try { StateStore.Open(legacyStore.DataDirectory).Load(); throw new Exception("Unavailable locations cannot open with empty data."); }
        catch (IOException) { Check(!Directory.Exists(absentPath), "An unavailable custom directory never starts with an empty state."); }
    }
}
