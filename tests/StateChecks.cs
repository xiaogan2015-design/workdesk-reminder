using System;
using System.IO;
using System.Linq;
using WorkDesk;

internal static class StateChecks
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

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

        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkDesk.Checks." + Guid.NewGuid().ToString("N"));
        var store = new StateStore(folder);
        try
        {
            Check(store.Load().Tasks.Count == 0, "Fresh installation");
            store.Save(notified);
            var loaded = new StateStore(folder).Load();
            Check(loaded.Tasks.Any(t => t.Title == upcoming.Title) && !loaded.CollectReminders(now).Daily, "Round-trip Chinese text and reminder deduplication");
            Check(loaded.Tasks.Single(t => t.Id == upcoming.Id).DueUtc == item.DueUtc, "Round-trip UTC timestamps");
            store.Save(state);
            Check(new StateStore(folder).Load().FindSummary(today, true).Text == "本周工作已更新", "Round-trip weekly summary");
            File.WriteAllText(store.FilePath, "invalid JSON");
            var recovery = new StateStore(folder);
            Check(recovery.Load().LastDailyReminder != null && recovery.Recovered, "Read prior version when primary is corrupt");
            recovery.Save(state);
            Check(!recovery.Recovered && new StateStore(folder).Load().Summaries.Count == 2, "Restore primary without replacing last valid backup");
        }
        finally
        {
            File.Delete(store.FilePath);
            File.Delete(store.FilePath + ".bak");
            File.Delete(store.FilePath + ".tmp");
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
        Console.WriteLine("Passed {0} state and reminder checks.", assertions);
    }
}
