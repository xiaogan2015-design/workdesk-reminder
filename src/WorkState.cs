using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WorkDesk
{
    [DataContract]
    internal sealed class WorkItem
    {
        internal const int MaxLead = 43200;
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Title;
        [DataMember] public string Notes;
        [DataMember] public DateTime? DueUtc;
        [DataMember] public int LeadMinutes = 30;
        [DataMember] public DateTime? CompletedUtc;
        [DataMember] public bool ReminderShown;

        public WorkItem Copy() { return (WorkItem)MemberwiseClone(); }

        public void SetSchedule(DateTime? dueUtc, int leadMinutes)
        {
            if (DueUtc != dueUtc || LeadMinutes != leadMinutes) ReminderShown = false;
            DueUtc = dueUtc;
            LeadMinutes = leadMinutes;
        }

        public void SetCompletion(DateTime? completedUtc)
        {
            if (CompletedUtc.HasValue && !completedUtc.HasValue) ReminderShown = false;
            CompletedUtc = completedUtc;
        }

        public bool IsOverdue(DateTime nowUtc)
        {
            return !CompletedUtc.HasValue && DueUtc <= nowUtc;
        }

        public bool ShouldRemind(DateTime nowUtc)
        {
            return !CompletedUtc.HasValue && !ReminderShown && DueUtc.HasValue &&
                (DueUtc.Value - nowUtc).TotalMinutes <= LeadMinutes;
        }
    }

    [DataContract]
    internal sealed class WorkSummary
    {
        [DataMember] public bool Weekly;
        [DataMember] public string Period;
        [DataMember] public string Text;
        [DataMember] public DateTime UpdatedUtc;
    }

    [DataContract]
    internal sealed class ReminderSettings
    {
        [DataMember] public bool DailyEnabled = true;
        [DataMember] public int DailyMinute = 17 * 60;
        [DataMember] public bool WeeklyEnabled = false;
        [DataMember] public int WeeklyMinute = 17 * 60;
        [DataMember] public DayOfWeek WeeklyDay = DayOfWeek.Friday;
    }

    internal enum TaskFilter { Pending, Today, Overdue, Completed }

    internal sealed class ReminderBatch
    {
        public WorkItem[] Tasks;
        public bool Daily;
        public bool Weekly;
        public DateTime Day;
        public bool Any { get { return Tasks.Length > 0 || Daily || Weekly; } }
    }

    [DataContract]
    internal sealed class WorkState
    {
        [DataMember] public int Version = 1;
        [DataMember] public List<WorkItem> Tasks = new List<WorkItem>();
        [DataMember] public List<WorkSummary> Summaries = new List<WorkSummary>();
        [DataMember] public ReminderSettings Settings = new ReminderSettings();
        [DataMember] public string LastDailyReminder;
        [DataMember] public string LastWeeklyReminder;

        public static DateTime WeekStart(DateTime date)
        {
            return date.Date.AddDays(-((int)date.DayOfWeek + 6) % 7);
        }

        public static string PeriodKey(DateTime date, bool weekly)
        {
            return (weekly ? WeekStart(date) : date.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public WorkSummary FindSummary(DateTime date, bool weekly)
        {
            var period = PeriodKey(date, weekly);
            return Summaries.Find(s => s.Weekly == weekly && s.Period == period);
        }

        public void SetSummary(DateTime date, bool weekly, string text, DateTime updatedUtc)
        {
            var summary = FindSummary(date, weekly);
            if (summary != null) Summaries.Remove(summary);
            Summaries.Add(new WorkSummary {
                Period = PeriodKey(date, weekly), Weekly = weekly, Text = text, UpdatedUtc = updatedUtc
            });
        }

        public IEnumerable<WorkItem> FilterTasks(TaskFilter filter, DateTimeOffset now)
        {
            if (filter == TaskFilter.Completed)
                return Tasks.Where(t => t.CompletedUtc.HasValue).OrderByDescending(t => t.CompletedUtc);
            var pending = Tasks.Where(t => !t.CompletedUtc.HasValue);
            if (filter == TaskFilter.Today)
                pending = pending.Where(t => t.DueUtc.HasValue && t.DueUtc.Value.ToLocalTime().Date == now.Date);
            if (filter == TaskFilter.Overdue)
                pending = pending.Where(t => t.IsOverdue(now.UtcDateTime));
            return pending.OrderBy(t => t.DueUtc ?? DateTime.MaxValue);
        }

        public ReminderBatch CollectReminders(DateTimeOffset now)
        {
            var day = now.Date;
            int minute = now.Hour * 60 + now.Minute;
            return new ReminderBatch {
                Day = day,
                Tasks = Tasks.Where(t => t.ShouldRemind(now.UtcDateTime)).OrderBy(t => t.DueUtc).ToArray(),
                Daily = Settings.DailyEnabled && minute >= Settings.DailyMinute &&
                    LastDailyReminder != PeriodKey(day, false) && FindSummary(day, false) == null,
                Weekly = Settings.WeeklyEnabled && now.DayOfWeek == Settings.WeeklyDay && minute >= Settings.WeeklyMinute &&
                    LastWeeklyReminder != PeriodKey(day, true) && FindSummary(day, true) == null
            };
        }

        public void MarkReminded(ReminderBatch batch)
        {
            var ids = new HashSet<string>(batch.Tasks.Select(item => item.Id));
            foreach (var item in Tasks.Where(t => ids.Contains(t.Id))) item.ReminderShown = true;
            if (batch.Daily) LastDailyReminder = PeriodKey(batch.Day, false);
            if (batch.Weekly) LastWeeklyReminder = PeriodKey(batch.Day, true);
        }

        public WorkState Copy()
        {
            var copy = (WorkState)MemberwiseClone();
            copy.Tasks = Tasks.Select(t => t.Copy()).ToList();
            copy.Summaries = new List<WorkSummary>(Summaries);
            return copy;
        }

        public void Validate()
        {
            if (Version != 1 || Tasks == null || Summaries == null || Settings == null ||
                Settings.DailyMinute < 0 || Settings.DailyMinute >= 1440 ||
                Settings.WeeklyMinute < 0 || Settings.WeeklyMinute >= 1440 ||
                !Enum.IsDefined(typeof(DayOfWeek), Settings.WeeklyDay))
                throw new InvalidDataException("Invalid WorkDesk data version or reminder settings.");

            var taskIds = new HashSet<string>();
            foreach (var item in Tasks)
                if (item == null || String.IsNullOrEmpty(item.Id) || !taskIds.Add(item.Id) ||
                    String.IsNullOrWhiteSpace(item.Title) || item.LeadMinutes < 0 || item.LeadMinutes > WorkItem.MaxLead)
                    throw new InvalidDataException("Invalid WorkDesk task.");

            var summaryKeys = new HashSet<string>();
            foreach (var summary in Summaries)
            {
                DateTime date;
                if (summary == null || String.IsNullOrWhiteSpace(summary.Text) ||
                    !DateTime.TryParseExact(summary.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
                    summary.Period != PeriodKey(date, summary.Weekly) || !summaryKeys.Add(summary.Weekly + summary.Period))
                    throw new InvalidDataException("Invalid WorkDesk summary.");
            }
        }
    }

    internal sealed class StateStore
    {
        private readonly DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(WorkState));
        private readonly string path;
        private bool recovered;

        public StateStore(string directory) { path = Path.Combine(directory, "state.json"); }
        public string FilePath { get { return path; } }
        public bool Recovered { get { return recovered; } }

        public WorkState Load()
        {
            string backup = path + ".bak";
            try { return Read(path); }
            catch (FileNotFoundException) { if (!File.Exists(backup)) return new WorkState(); }
            catch (DirectoryNotFoundException) { return new WorkState(); }
            catch (InvalidDataException) { if (!File.Exists(backup)) throw; }
            catch (SerializationException) { if (!File.Exists(backup)) throw; }
            var state = Read(backup);
            recovered = true;
            return state;
        }

        private WorkState Read(string file)
        {
            using (var input = File.OpenRead(file))
            {
                var state = serializer.ReadObject(input) as WorkState;
                if (state == null) throw new InvalidDataException("WorkDesk data is empty.");
                state.Validate();
                return state;
            }
        }

        public void Save(WorkState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(output, state);
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, recovered ? null : path + ".bak");
            else File.Move(temporary, path);
            recovered = false;
        }
    }
}
