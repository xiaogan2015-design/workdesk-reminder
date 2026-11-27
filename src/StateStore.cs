using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace WorkDesk
{
    internal enum BackupCadence { Disabled, Weekly, Monthly, Custom }

    [DataContract]
    internal sealed class BackupSchedule
    {
        [DataMember] public BackupCadence Cadence;
        [DataMember] public int Days;

        public bool IsValid
        {
            get { return Enum.IsDefined(typeof(BackupCadence), Cadence) &&
                (Cadence != BackupCadence.Custom || (Days >= 1 && Days <= ArchiveSettings.MaxDays)); }
        }

        public DateTime? NextDate(DateTime? last, DateTime today)
        {
            if (!IsValid || Cadence == BackupCadence.Disabled) return null;
            if (!last.HasValue) return today.Date;
            var day = last.Value.Date;
            if (Cadence == BackupCadence.Monthly)
                return day.Year == 9999 && day.Month == 12 ? DateTime.MaxValue.Date : day.AddMonths(1);
            return day.AddDays(Math.Min(Cadence == BackupCadence.Weekly ? 7 : Days, (DateTime.MaxValue.Date - day).Days));
        }
    }

    [DataContract]
    internal sealed class ArchiveMonth
    {
        [DataMember] public string Month;
        [DataMember] public string FileId;
        internal Lazy<List<WorkItem>> Items;

        public ArchiveMonth(string month, List<WorkItem> items)
        {
            Month = month;
            Items = new Lazy<List<WorkItem>>(() => items);
        }

        public bool IsValid
        {
            get
            {
                DateTime date;
                return DateTime.TryParseExact(Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
                    StateStore.IsFileId(FileId);
            }
        }
    }

    internal sealed class StateStore
    {
        private const string DateFormat = "yyyy-MM-dd";
        private const string BackupPrefix = "WorkDesk-";
        private const string LocationFilename = "data-directory.txt";
        private readonly string directory;
        private readonly string settingsDirectory;
        private readonly bool requireExisting;
        private bool recovered;

        public StateStore(string directory, bool requireExisting = false, string settingsDirectory = null)
        {
            this.directory = Path.GetFullPath(directory);
            this.settingsDirectory = settingsDirectory == null ? this.directory : Path.GetFullPath(settingsDirectory);
            this.requireExisting = requireExisting;
        }

        public string FilePath { get { return Path.Combine(directory, "state.json"); } }
        public string DataDirectory { get { return directory; } }
        public string BackupDirectory { get { return Path.Combine(directory, "backups"); } }
        public bool Recovered { get { return recovered; } }

        internal static StateStore Open(string settingsDirectory)
        {
            string setting = Path.Combine(settingsDirectory, LocationFilename);
            string selected;
            try { selected = File.ReadAllText(setting).Trim(); }
            catch (FileNotFoundException) { return new StateStore(settingsDirectory); }
            catch (DirectoryNotFoundException) { return new StateStore(settingsDirectory); }
            if (String.IsNullOrWhiteSpace(selected) || !Path.IsPathRooted(selected))
                throw new InvalidDataException("数据目录配置无效：" + setting);
            return new StateStore(selected, true, settingsDirectory);
        }

        public WorkState Load()
        {
            string path = FilePath;
            string backup = path + ".bak";
            recovered = false;
            try { return ReadState(path); }
            catch (FileNotFoundException) { if (!File.Exists(backup) && !requireExisting) return new WorkState(); }
            catch (DirectoryNotFoundException)
            {
                if (!requireExisting) return new WorkState();
                throw new IOException("配置的数据目录不可用。请重新连接该目录后再次打开应用。" + Environment.NewLine + directory);
            }
            catch (InvalidDataException) { if (!File.Exists(backup)) throw; }
            catch (SerializationException) { if (!File.Exists(backup)) throw; }
            if (!File.Exists(backup))
                throw new IOException("配置的数据目录中缺少数据文件。请确认目录已连接且文件仍在。" + Environment.NewLine + directory);
            var state = ReadState(backup);
            recovered = true;
            return state;
        }

        private WorkState ReadState(string path)
        {
            var state = Read<WorkState>(path);
            state.Validate();
            foreach (var summary in state.Summaries)
            {
                if (summary.BodyId == null)
                {
                    if (summary.Images != null)
                        foreach (var image in summary.Images) SummaryImageCodec.RepairLegacy(image);
                }
                else
                {
                    var reportPath = BodyPath(summary.BodyId);
                    summary.Body = new Lazy<WorkSummary>(() => ReadBody(reportPath, summary));
                }
            }
            if (state.ArchiveMonths != null)
                foreach (var month in state.ArchiveMonths)
                    month.Items = new Lazy<List<WorkItem>>(() => ReadArchive(ArchivePath(month)));
            return state;
        }

        private WorkSummary ReadBody(string path, WorkSummary index)
        {
            var body = Read<WorkSummary>(path);
            if (body.BodyId != null || body.Weekly != index.Weekly || body.Period != index.Period)
                throw new InvalidDataException("总结文件与记录日期不匹配。");
            body.Validate(true);
            if (body.Images != null)
                foreach (var image in body.Images) image.ImagePath = image.FileId == null ? null : ImagePath(image.FileId);
            return body;
        }

        private static List<WorkItem> ReadArchive(string path)
        {
            var items = Read<List<WorkItem>>(path);
            WorkState.ValidateTasks(items);
            if (items.Any(item => !item.ArchivedUtc.HasValue))
                throw new InvalidDataException("归档文件包含未归档事项。");
            return items;
        }

        public void Save(WorkState state)
        {
            if (requireExisting && !File.Exists(FilePath) && !File.Exists(FilePath + ".bak"))
                throw new IOException("数据文件不可用：" + FilePath);
            Directory.CreateDirectory(directory);
            var disk = state.Copy(false);
            disk.Version = 2;
            disk.ArchiveMonths = SaveArchiveMonths(disk);
            disk.Summaries = disk.Summaries.Select(SaveBody).ToList();
            WriteAtomic(FilePath, FilePath + ".tmp", recovered ? null : FilePath + ".bak", output => Write(output, disk));
            state.Version = disk.Version;
            state.Tasks = disk.Tasks;
            state.ArchiveMonths = disk.ArchiveMonths;
            state.Summaries = disk.Summaries;
            recovered = false;
        }

        private List<ArchiveMonth> SaveArchiveMonths(WorkState disk)
        {
            var months = disk.ArchiveMonths ?? new List<ArchiveMonth>();
            var additions = disk.Tasks.Where(item => item.ArchivedUtc.HasValue).GroupBy(WorkState.ArchiveMonthKey);
            months.AddRange(additions.Select(group => new ArchiveMonth(group.Key, group.ToList())));
            disk.Tasks.RemoveAll(item => item.ArchivedUtc.HasValue);
            var saved = new List<ArchiveMonth>();
            foreach (var month in months)
            {
                if (month.FileId != null) { saved.Add(month); continue; }
                if (month.Items.Value.Count == 0) continue;
                var newMonth = new ArchiveMonth(month.Month, month.Items.Value) { FileId = NewFileId() };
                WriteNew(ArchivePath(newMonth), output => Write(output, newMonth.Items.Value));
                saved.Add(newMonth);
            }
            return saved.Count == 0 ? null : saved;
        }

        private WorkSummary SaveBody(WorkSummary summary)
        {
            if (summary.BodyId != null) return summary;
            var body = new WorkSummary {
                Weekly = summary.Weekly, Period = summary.Period, UpdatedUtc = summary.UpdatedUtc, Text = summary.Text,
                Images = summary.Images == null ? null : summary.Images.Select(SaveImage).ToList()
            };
            var index = new WorkSummary { Weekly = body.Weekly, Period = body.Period, UpdatedUtc = body.UpdatedUtc, BodyId = NewFileId() };
            WriteNew(BodyPath(index.BodyId), output => Write(output, body));
            index.Body = new Lazy<WorkSummary>(() => body);
            return index;
        }

        private SummaryImage SaveImage(SummaryImage image)
        {
            if (image.FileId != null) return image;
            if (!image.AlphaChecked)
            {
                image = new SummaryImage { Id = image.Id, Png = image.Png };
                SummaryImageCodec.RepairLegacy(image);
            }
            var png = image.ReadPng();
            var fileId = ImageHash(png);
            var path = ImagePath(fileId);
            if (!File.Exists(path)) WriteNew(path, output => output.Write(png, 0, png.Length));
            return new SummaryImage { Id = image.Id, FileId = fileId, ImagePath = path, AlphaChecked = image.AlphaChecked };
        }

        private string ArchivePath(ArchiveMonth month) { return Path.Combine(directory, "archives", month.Month + "-" + month.FileId + ".json"); }
        private string BodyPath(string id) { return Path.Combine(directory, "reports", id + ".json"); }
        private string ImagePath(string id) { return Path.Combine(directory, "images", id + ".png"); }

        private static string NewFileId() { return Guid.NewGuid().ToString("N"); }

        internal static bool IsFileId(string value)
        {
            Guid id;
            return Guid.TryParseExact(value, "N", out id) && id.ToString("N") == value;
        }

        internal static bool IsImageFileId(string value)
        {
            return value != null && value.Length == 64 && value.All(character =>
                (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
        }

        private static string ImageHash(byte[] png)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(png)).Replace("-", "").ToLowerInvariant();
        }

        public void BackUp(string destination, WorkState state)
        {
            var snapshot = state.Copy(false);
            snapshot.Version = 1;
            snapshot.Tasks.AddRange(state.ArchivedTasks());
            snapshot.ArchiveMonths = null;
            snapshot.Summaries = state.Summaries.Select(summary => new WorkSummary {
                Weekly = summary.Weekly, Period = summary.Period, UpdatedUtc = summary.UpdatedUtc, Text = summary.Text,
                Images = summary.Images == null ? null : summary.Images.Select(image => image.ForBackup()).ToList()
            }).ToList();
            WriteExport(destination, output => Write(output, snapshot));
        }

        internal DateTime? LatestAutomaticBackup()
        {
            if (!Directory.Exists(BackupDirectory)) return null;
            DateTime? latest = null;
            foreach (string file in Directory.EnumerateFiles(BackupDirectory, BackupPrefix + "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                DateTime day;
                if (name.StartsWith(BackupPrefix, StringComparison.Ordinal) &&
                    DateTime.TryParseExact(name.Substring(BackupPrefix.Length), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day) &&
                    (!latest.HasValue || day > latest.Value)) latest = day;
            }
            return latest;
        }

        internal void WriteAutomaticBackup(DateTime day, WorkState state)
        {
            if (requireExisting && !File.Exists(FilePath) && !File.Exists(FilePath + ".bak"))
                throw new IOException("数据文件不可用：" + FilePath);
            Directory.CreateDirectory(BackupDirectory);
            var filename = BackupPrefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + ".json";
            BackUp(Path.Combine(BackupDirectory, filename), state);
        }

        internal StateStore Relocate(string destination, WorkState state, out WorkState moved)
        {
            moved = state;
            destination = Path.GetFullPath(destination);
            if (String.Equals(destination.TrimEnd(Path.DirectorySeparatorChar), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return this;
            Directory.CreateDirectory(destination);
            if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("请选择空文件夹，避免覆盖已有数据。");
            var target = new StateStore(destination, false, settingsDirectory);
            foreach (string part in new[] { "archives", "reports", "images", "backups" })
            {
                var sourceFolder = Path.Combine(directory, part);
                if (destination.TrimEnd(Path.DirectorySeparatorChar).Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("请选择独立的数据文件夹。");
                if (!Directory.Exists(sourceFolder)) continue;
                foreach (string source in Directory.EnumerateFiles(sourceFolder))
                {
                    if (Path.GetExtension(source) == ".tmp") continue;
                    var targetFolder = Path.Combine(destination, part);
                    Directory.CreateDirectory(targetFolder);
                    CopyFile(source, Path.Combine(targetFolder, Path.GetFileName(source)));
                }
            }
            target.Save(state.Copy(false));
            if (File.Exists(FilePath + ".bak")) CopyFile(FilePath + ".bak", target.FilePath + ".bak");
            var check = target.ReadState(target.FilePath);
            WorkState.ValidateTasks(check.Tasks.Concat(check.ArchivedTasks()));
            foreach (var report in check.Summaries)
                if (report.Images != null)
                    foreach (var image in report.Images)
                        if (image.FileId != ImageHash(image.ReadPng()))
                            throw new InvalidDataException("迁移后的图片校验未通过：" + image.Id);
            Directory.CreateDirectory(settingsDirectory);
            var setting = Path.Combine(settingsDirectory, LocationFilename);
            WriteAtomic(setting, setting + ".tmp", null, output => {
                using (var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), 1024, true)) writer.Write(destination);
            });
            moved = check;
            return new StateStore(destination, true, settingsDirectory);
        }

        private static void CopyFile(string source, string destination)
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
        }

        public void WriteExport(string destination, Action<Stream> write)
        {
            destination = Path.GetFullPath(destination);
            var folder = Path.GetDirectoryName(destination);
            if (new[] { FilePath, FilePath + ".bak", FilePath + ".tmp", Path.Combine(settingsDirectory, LocationFilename) }
                    .Contains(destination, StringComparer.OrdinalIgnoreCase) ||
                new[] { "archives", "reports", "images" }.Any(part => String.Equals(folder,
                    Path.Combine(directory, part), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("请选择其他位置，避免覆盖待办笔记正在使用的数据文件。");
            string temporary = Path.Combine(folder, ".workdesk-" + NewFileId() + ".tmp");
            try { WriteAtomic(destination, temporary, null, write); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static T Read<T>(string path) where T : class
        {
            using (var input = File.OpenRead(path))
            {
                var data = new DataContractJsonSerializer(typeof(T)).ReadObject(input) as T;
                if (data == null) throw new InvalidDataException("WorkDesk data is empty.");
                return data;
            }
        }

        private static void Write<T>(Stream output, T data)
        {
            new DataContractJsonSerializer(typeof(T)).WriteObject(output, data);
        }

        private static void WriteNew(string path, Action<Stream> write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteAtomic(path, path + ".tmp", null, write);
        }

        private static void WriteAtomic(string target, string temporary, string backup, Action<Stream> write)
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(output);
                output.Flush(true);
            }
            if (File.Exists(target)) File.Replace(temporary, target, backup);
            else File.Move(temporary, target);
        }
    }
}
