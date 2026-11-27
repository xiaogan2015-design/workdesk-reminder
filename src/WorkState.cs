using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace WorkDesk
{
    internal enum TaskQuadrant { Unclassified, DoNow, Schedule, Delegate, Reduce }

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
        [DataMember(EmitDefaultValue = false)] public DateTime? ArchivedUtc;
        [DataMember(EmitDefaultValue = false)] public bool AutoArchivePaused;
        [DataMember(EmitDefaultValue = false)] public TaskQuadrant Quadrant;

        public WorkItem Copy() { return (WorkItem)MemberwiseClone(); }

        public void SetSchedule(DateTime? dueUtc, int leadMinutes)
        {
            if (DueUtc != dueUtc) AutoArchivePaused = false;
            if (DueUtc != dueUtc || LeadMinutes != leadMinutes) ReminderShown = false;
            DueUtc = dueUtc;
            LeadMinutes = leadMinutes;
        }

        public void SetCompletion(DateTime? completedUtc)
        {
            if (CompletedUtc != completedUtc) AutoArchivePaused = false;
            if (CompletedUtc.HasValue && !completedUtc.HasValue) ReminderShown = false;
            CompletedUtc = completedUtc;
        }

        public bool IsOverdue(DateTime nowUtc)
        {
            return !CompletedUtc.HasValue && DueUtc <= nowUtc;
        }

        public bool ShouldRemind(DateTime nowUtc)
        {
            return !ArchivedUtc.HasValue && !CompletedUtc.HasValue && !ReminderShown && WithinReminderWindow(nowUtc);
        }

        public void RestoreFromArchive()
        {
            ArchivedUtc = null;
            AutoArchivePaused = true;
        }

        public bool WithinReminderWindow(DateTime nowUtc)
        {
            return DueUtc.HasValue &&
                (DueUtc.Value - nowUtc).TotalMinutes <= LeadMinutes;
        }
    }

    [DataContract]
    internal sealed class WorkSummary
    {
        [DataMember] public bool Weekly;
        [DataMember] public string Period;
        [DataMember] public DateTime UpdatedUtc;
        [DataMember(Name = "Text", EmitDefaultValue = false)] private string text;
        [DataMember(Name = "Images", EmitDefaultValue = false)] private List<SummaryImage> images;
        [DataMember(EmitDefaultValue = false)] public string BodyId;
        internal Lazy<WorkSummary> Body;

        public string Text { get { return Body == null ? text : Body.Value.text; } set { text = value; } }
        public List<SummaryImage> Images { get { return Body == null ? images : Body.Value.images; } set { images = value; } }

        internal void Validate(bool indexed)
        {
            if (BodyId != null)
            {
                if (!indexed || !StateStore.IsFileId(BodyId) || text != null || images != null)
                    throw new InvalidDataException("Invalid WorkDesk summary index.");
                return;
            }
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Invalid WorkDesk summary.");
            if (images == null) return;
            var ids = new HashSet<string>();
            foreach (var image in images)
                if (image == null || !image.IsValid(indexed) || !ids.Add(image.Id) || !text.Contains(image.Marker))
                    throw new InvalidDataException("Invalid WorkDesk summary image.");
        }
    }

    [DataContract]
    internal sealed class SummaryImage
    {
        public const int MaxEdge = 4096;
        public const int MaxBytes = 12 * 1024 * 1024;
        private static readonly byte[] PngHeader = { 137, 80, 78, 71, 13, 10, 26, 10 };
        public static readonly Regex Reference = new Regex(@"!\[截图\]\(workdesk-image:([0-9a-f]{32})\)", RegexOptions.CultureInvariant);

        [DataMember] public string Id;
        [DataMember(EmitDefaultValue = false)] public bool AlphaChecked;
        [DataMember(EmitDefaultValue = false)] public string FileId;
        private string png;
        private bool embed;
        internal string ImagePath;

        [DataMember(Name = "Png", EmitDefaultValue = false)]
        private string InlinePng { get { return embed ? Png : png; } set { png = value; } }

        public string Png { get { return ImagePath == null ? png : Convert.ToBase64String(ReadPng()); } set { png = value; } }

        public string Marker { get { return "![截图](workdesk-image:" + Id + ")"; } }
        public byte[] ReadPng()
        {
            if (ImagePath == null) return Convert.FromBase64String(png);
            using (var input = File.OpenRead(ImagePath))
            {
                if (input.Length < 24 || input.Length > MaxBytes) throw new InvalidDataException("图片文件大小无效。");
                var bytes = new byte[(int)input.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = input.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException();
                    offset += count;
                }
                if (!ValidPng(bytes)) throw new InvalidDataException("图片文件格式无效。");
                return bytes;
            }
        }

        internal SummaryImage ForBackup()
        {
            return new SummaryImage { Id = Id, AlphaChecked = AlphaChecked, png = png, ImagePath = ImagePath, embed = true };
        }

        public bool IsValid(bool indexed = false)
        {
            if (!StateStore.IsFileId(Id)) return false;
            if (FileId != null) return indexed && StateStore.IsImageFileId(FileId) && png == null;
            if (String.IsNullOrEmpty(png) || png.Length > MaxBytes / 3 * 4) return false;
            try { return ValidPng(ReadPng()); }
            catch (FormatException) { return false; }
        }

        private static bool ValidPng(byte[] png)
        {
            return png.Length >= 24 && png.Take(PngHeader.Length).SequenceEqual(PngHeader) &&
                ValidEdge(png, 16) && ValidEdge(png, 20);
        }

        private static bool ValidEdge(byte[] png, int offset)
        {
            uint edge = ((uint)png[offset] << 24) | ((uint)png[offset + 1] << 16) | ((uint)png[offset + 2] << 8) | png[offset + 3];
            return edge > 0 && edge <= MaxEdge;
        }
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

    [DataContract]
    internal sealed class ArchiveSettings
    {
        public const int MaxDays = 3650;

        [DataMember] public int CompletedDays;
        [DataMember] public int OverdueDays;

        public static bool IsValidDayCount(int days) { return days >= 0 && days <= MaxDays; }

        public bool ShouldArchive(WorkItem item, DateTime utc)
        {
            if (item.ArchivedUtc.HasValue || item.AutoArchivePaused) return false;
            var since = item.CompletedUtc ?? item.DueUtc;
            int days = item.CompletedUtc.HasValue ? CompletedDays : OverdueDays;
            return days > 0 && since.HasValue && utc - since.Value >= TimeSpan.FromDays(days);
        }
    }

    [DataContract]
    internal sealed class AppearanceSettings
    {
        public const int MinSize = 12;
        public const int MaxInterfaceSize = 20;
        public const int MaxEditorSize = 32;
        public const int DefaultSize = 14;

        [DataMember] public string InterfaceFont;
        [DataMember] public int InterfaceSize = DefaultSize;
        [DataMember] public string EditorFont;
        [DataMember] public int EditorSize = DefaultSize;

        public void Normalize()
        {
            if (InterfaceSize < MinSize || InterfaceSize > MaxInterfaceSize) InterfaceSize = DefaultSize;
            if (EditorSize < MinSize || EditorSize > MaxEditorSize) EditorSize = DefaultSize;
        }
    }

    internal enum TaskFilter { Pending, Today, Overdue, Completed, Archived }

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
        public const int DailyChecklistLength = 6;

        [DataMember] public int Version = 1;
        [DataMember] public List<WorkItem> Tasks = new List<WorkItem>();
        [DataMember] public List<WorkSummary> Summaries = new List<WorkSummary>();
        [DataMember] public ReminderSettings Settings = new ReminderSettings();
        [DataMember] public AppearanceSettings Appearance = new AppearanceSettings();
        [DataMember(EmitDefaultValue = false)] public ArchiveSettings Archiving = new ArchiveSettings();
        [DataMember(EmitDefaultValue = false)] public BackupSchedule Backup;
        [DataMember(EmitDefaultValue = false)] internal List<ArchiveMonth> ArchiveMonths;
        [DataMember] public string LastDailyReminder;
        [DataMember] public string LastWeeklyReminder;
        [DataMember(EmitDefaultValue = false)] public string DailyClearedOn;
        [DataMember(EmitDefaultValue = false)] private string FocusDate;
        [DataMember(EmitDefaultValue = false)] private string[] FocusIds;
        [DataMember(EmitDefaultValue = false)] private string DailyChecklistDate;
        [DataMember(EmitDefaultValue = false)] private bool[] DailyChecklistSteps;

        public static DateTime WeekStart(DateTime date)
        {
            return date.Date.AddDays(-((int)date.DayOfWeek + 6) % 7);
        }

        public static string PeriodKey(DateTime date, bool weekly)
        {
            return (weekly ? WeekStart(date) : date.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public bool[] GetDailyChecklist(DateTime date)
        {
            if (DailyChecklistDate != PeriodKey(date, false) || DailyChecklistSteps == null || DailyChecklistSteps.Length != DailyChecklistLength)
                return new bool[DailyChecklistLength];
            return (bool[])DailyChecklistSteps.Clone();
        }

        public void SetDailyChecklist(DateTime date, bool[] steps)
        {
            if (steps.Length != DailyChecklistLength) throw new ArgumentException("Unexpected daily checklist length.", "steps");
            DailyChecklistDate = PeriodKey(date, false);
            DailyChecklistSteps = (bool[])steps.Clone();
        }

        public WorkSummary FindSummary(DateTime date, bool weekly)
        {
            var period = PeriodKey(date, weekly);
            return Summaries.Find(s => s.Weekly == weekly && s.Period == period);
        }

        public const int FocusLimit = 3;

        public WorkItem[] FocusItems(DateTime day)
        {
            if (FocusDate != PeriodKey(day, false) || FocusIds == null) return new WorkItem[0];
            return FocusIds.Distinct(StringComparer.Ordinal)
                .Select(id => Tasks.Find(item => item.Id == id && !item.ArchivedUtc.HasValue))
                .Where(item => item != null).Take(FocusLimit).ToArray();
        }

        public void SetFocus(DateTime day, IEnumerable<string> ids)
        {
            FocusDate = PeriodKey(day, false);
            FocusIds = ids.Distinct(StringComparer.Ordinal).Take(FocusLimit).ToArray();
        }

        public void SetSummary(DateTime date, bool weekly, string text, DateTime updatedUtc, List<SummaryImage> images = null)
        {
            var summary = FindSummary(date, weekly);
            if (summary != null) Summaries.Remove(summary);
            if (String.IsNullOrWhiteSpace(text))
            {
                if (summary != null && !weekly && date.Date == updatedUtc.ToLocalTime().Date)
                    DailyClearedOn = summary.Period;
                return;
            }
            Summaries.Add(new WorkSummary {
                Period = PeriodKey(date, weekly), Weekly = weekly, Text = text, UpdatedUtc = updatedUtc,
                Images = images != null && images.Count != 0 ? new List<SummaryImage>(images) : null
            });
        }

        public IEnumerable<WorkItem> FilterTasks(TaskFilter filter, DateTimeOffset now)
        {
            if (filter == TaskFilter.Archived)
                return Tasks.Where(t => t.ArchivedUtc.HasValue).Concat(ArchivedTasks()).OrderByDescending(t => t.ArchivedUtc);
            var active = Tasks.Where(t => !t.ArchivedUtc.HasValue);
            if (filter == TaskFilter.Completed)
                return active.Where(t => t.CompletedUtc.HasValue).OrderByDescending(t => t.CompletedUtc);
            var pending = active.Where(t => !t.CompletedUtc.HasValue);
            if (filter == TaskFilter.Today)
                pending = pending.Where(t => t.DueUtc.HasValue && t.DueUtc.Value.ToLocalTime().Date == now.Date);
            if (filter == TaskFilter.Overdue)
                pending = pending.Where(t => t.IsOverdue(now.UtcDateTime));
            return pending.OrderBy(t => t.DueUtc ?? DateTime.MaxValue);
        }

        public void ArchiveAgedTasks(DateTime utc)
        {
            foreach (var item in Tasks.Where(t => Archiving.ShouldArchive(t, utc))) item.ArchivedUtc = utc;
        }

        internal IEnumerable<WorkItem> ArchivedTasks()
        {
            return ArchiveMonths == null ? Enumerable.Empty<WorkItem>() : ArchiveMonths.SelectMany(month => month.Items.Value);
        }

        internal static string ArchiveMonthKey(WorkItem item)
        {
            return item.ArchivedUtc.Value.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        internal IEnumerable<string> ArchiveMonthKeys()
        {
            return (ArchiveMonths == null ? Enumerable.Empty<string>() : ArchiveMonths.Select(month => month.Month))
                .Concat(Tasks.Where(task => task.ArchivedUtc.HasValue).Select(ArchiveMonthKey)).Distinct().OrderByDescending(key => key);
        }

        internal IEnumerable<WorkItem> ArchivedTasksIn(string key)
        {
            var segments = ArchiveMonths == null ? Enumerable.Empty<WorkItem>() :
                ArchiveMonths.Where(part => part.Month == key).SelectMany(part => part.Items.Value);
            return segments
                .Concat(Tasks.Where(task => task.ArchivedUtc.HasValue && ArchiveMonthKey(task) == key))
                .OrderByDescending(task => task.ArchivedUtc);
        }

        internal bool RestoreArchivedTask(string id, string key)
        {
            var item = Tasks.Find(task => task.Id == id && task.ArchivedUtc.HasValue);
            if (item == null && ArchiveMonths != null)
            {
                int index = ArchiveMonths.FindIndex(part => part.Month == key && part.Items.Value.Any(task => task.Id == id));
                if (index < 0) return false;
                var month = ArchiveMonths[index];
                var archived = month.Items.Value.Find(task => task.Id == id);
                if (archived == null) return false;
                item = archived.Copy();
                ArchiveMonths[index] = new ArchiveMonth(month.Month, month.Items.Value.Where(task => task.Id != id).ToList());
                Tasks.Add(item);
            }
            if (item == null) return false;
            item.RestoreFromArchive();
            return true;
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

        public WorkState Copy(bool copyTasks = true)
        {
            var copy = (WorkState)MemberwiseClone();
            copy.Tasks = copyTasks ? Tasks.Select(t => t.Copy()).ToList() : new List<WorkItem>(Tasks);
            copy.Summaries = new List<WorkSummary>(Summaries);
            if (ArchiveMonths != null) copy.ArchiveMonths = new List<ArchiveMonth>(ArchiveMonths);
            return copy;
        }

        public void Validate()
        {
            if ((Version != 1 && Version != 2) || Tasks == null || Summaries == null || Settings == null ||
                Settings.DailyMinute < 0 || Settings.DailyMinute >= 1440 ||
                Settings.WeeklyMinute < 0 || Settings.WeeklyMinute >= 1440 ||
                !Enum.IsDefined(typeof(DayOfWeek), Settings.WeeklyDay))
                throw new InvalidDataException("Invalid WorkDesk data version or reminder settings.");

            if (Appearance == null) Appearance = new AppearanceSettings();
            Appearance.Normalize();
            if (Archiving == null) Archiving = new ArchiveSettings();
            if (!ArchiveSettings.IsValidDayCount(Archiving.CompletedDays)) Archiving.CompletedDays = 0;
            if (!ArchiveSettings.IsValidDayCount(Archiving.OverdueDays)) Archiving.OverdueDays = 0;

            if (Backup != null && !Backup.IsValid) Backup = null;
            ValidateTasks(Tasks);
            if (ArchiveMonths != null && (Version != 2 || ArchiveMonths.Any(month => month == null || !month.IsValid) ||
                ArchiveMonths.Select(month => month.FileId).Distinct().Count() != ArchiveMonths.Count))
                throw new InvalidDataException("Invalid WorkDesk archive index.");

            var summaryKeys = new HashSet<string>();
            foreach (var summary in Summaries)
            {
                DateTime date;
                if (summary == null ||
                    !DateTime.TryParseExact(summary.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
                    summary.Period != PeriodKey(date, summary.Weekly) || !summaryKeys.Add(summary.Weekly + summary.Period))
                    throw new InvalidDataException("Invalid WorkDesk summary.");
                summary.Validate(Version == 2);
            }
        }

        internal static void ValidateTasks(IEnumerable<WorkItem> tasks)
        {
            var ids = new HashSet<string>();
            foreach (var item in tasks)
            {
                if (item == null || String.IsNullOrEmpty(item.Id) || !ids.Add(item.Id) ||
                    String.IsNullOrWhiteSpace(item.Title) || item.LeadMinutes < 0 || item.LeadMinutes > WorkItem.MaxLead)
                    throw new InvalidDataException("Invalid WorkDesk task.");
                if (!Enum.IsDefined(typeof(TaskQuadrant), item.Quadrant)) item.Quadrant = TaskQuadrant.Unclassified;
            }
        }
    }
}
