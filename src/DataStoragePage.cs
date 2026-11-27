using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace WorkDesk
{
    internal sealed partial class MainWindow
    {
        private static readonly string[] BackupCadences = { "关闭", "每周", "每月", "自定义天数" };
        private TextBox dataDirectoryBox;
        private TextBox backupDirectoryBox;
        private TextBlock backupStatus;
        private DateTime? lastAutomaticBackup;
        private DateTime retryBackupUtc;
        private bool backupDateLoaded;
        private bool automaticBackupRunning;
        private string automaticBackupError;

        private Border BuildDataStorageCard()
        {
            var content = new StackPanel();
            content.Children.Add(Ui.Text("本地数据与备份", "SectionTitle"));
            var directoryGroup = new StackPanel();
            dataDirectoryBox = ReadOnlyPath(store.DataDirectory);
            Ui.Label(directoryGroup, "数据目录", dataDirectoryBox);
            var change = Ui.Button("迁移到新目录", delegate { ChooseDataDirectory(); });
            change.HorizontalAlignment = HorizontalAlignment.Left;
            change.Margin = new Thickness(0, 8, 0, 5);
            directoryGroup.Children.Add(change);
            var safety = Ui.Text("选择空文件夹。数据校验通过后切换，原目录会保留一份。", "Hint");
            safety.TextWrapping = TextWrapping.Wrap;
            directoryGroup.Children.Add(safety);
            content.Children.Add(Ui.FormGroup(directoryGroup));

            var automatic = new StackPanel();
            var current = state.Backup;
            var choices = new ComboBox {
                ItemsSource = BackupCadences, SelectedIndex = current == null ? 0 : (int)current.Cadence,
                Width = 135, HorizontalAlignment = HorizontalAlignment.Left
            };
            var days = new TextBox {
                Width = 88, MaxLength = 4, HorizontalAlignment = HorizontalAlignment.Left,
                Text = current == null || current.Days < 1 ? "14" : current.Days.ToString(CultureInfo.InvariantCulture)
            };
            var fields = new WrapPanel();
            var modeField = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            Ui.Label(modeField, "自动备份频率", choices);
            fields.Children.Add(modeField);
            var dayField = new StackPanel();
            Ui.Label(dayField, "间隔天数", days);
            fields.Children.Add(dayField);
            dayField.Visibility = choices.SelectedIndex == (int)BackupCadence.Custom ? Visibility.Visible : Visibility.Collapsed;
            choices.SelectionChanged += delegate {
                dayField.Visibility = choices.SelectedIndex == (int)BackupCadence.Custom ? Visibility.Visible : Visibility.Collapsed;
            };
            automatic.Children.Add(fields);
            backupDirectoryBox = ReadOnlyPath(store.BackupDirectory);
            Ui.Label(automatic, "自动备份目录", backupDirectoryBox);
            backupStatus = Ui.Text("", "Hint");
            backupStatus.TextWrapping = TextWrapping.Wrap;
            backupStatus.Margin = new Thickness(0, 7, 0, 0);
            automatic.Children.Add(backupStatus);
            var hint = Ui.Text("到期时在后台备份。应用关闭期间错过的备份会在下次打开时补做。", "Hint");
            hint.Margin = new Thickness(0, 5, 0, 0);
            hint.TextWrapping = TextWrapping.Wrap;
            automatic.Children.Add(hint);
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var save = Ui.Button("保存备份设置", delegate {
                var cadence = (BackupCadence)choices.SelectedIndex;
                int interval = 0;
                if (cadence == BackupCadence.Custom &&
                    !Int32.TryParse(days.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out interval))
                    interval = 0;
                var schedule = new BackupSchedule { Cadence = cadence, Days = interval };
                if (!schedule.IsValid) { Banner("请输入 1 到 " + ArchiveSettings.MaxDays + " 之间的间隔天数。"); return; }
                var next = state.Copy();
                next.Backup = schedule;
                if (!Commit(next)) return;
                retryBackupUtc = DateTime.MinValue;
                automaticBackupError = null;
                Banner("自动备份设置已保存。");
                CheckAutomaticBackup(DateTimeOffset.Now);
                UpdateBackupStatus(DateTime.Today);
            }, "Primary");
            save.Margin = new Thickness(0, 0, 8, 5);
            actions.Children.Add(save);
            var export = Ui.Button("导出全部备份", delegate { ExportBackup(); });
            export.Margin = new Thickness(0, 0, 0, 5);
            actions.Children.Add(export);
            automatic.Children.Add(actions);
            content.Children.Add(Ui.FormGroup(automatic));
            RefreshBackupDate();
            UpdateBackupStatus(DateTime.Today);
            return Ui.Card(content);
        }

        private static TextBox ReadOnlyPath(string value)
        {
            return new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        }

        private void ChooseDataDirectory()
        {
            using (var picker = new Forms.FolderBrowserDialog {
                Description = "为待办笔记选择一个空的数据目录", SelectedPath = store.DataDirectory, ShowNewFolderButton = true
            })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                if (automaticBackupRunning) { Banner("正在自动备份，请等备份完成后再迁移目录。"); return; }
                try
                {
                    WorkState moved;
                    var target = store.Relocate(picker.SelectedPath, state, out moved);
                    if (ReferenceEquals(store, target)) { Banner("已在使用这个数据目录。"); return; }
                    store = target;
                    state = moved;
                    dataDirectoryBox.Text = store.DataDirectory;
                    backupDirectoryBox.Text = store.BackupDirectory;
                    backupDateLoaded = false;
                    automaticBackupError = null;
                    retryBackupUtc = DateTime.MinValue;
                    RefreshBackupDate();
                    UpdateBackupStatus(DateTime.Today);
                    Banner("数据已迁移。原目录已保留。");
                }
                catch (Exception ex) { Banner("迁移失败，仍在使用原数据目录。" + Environment.NewLine + ex.Message); }
            }
        }

        private void RefreshBackupDate()
        {
            if (backupDateLoaded || DateTime.UtcNow < retryBackupUtc) return;
            try
            {
                lastAutomaticBackup = store.LatestAutomaticBackup();
                backupDateLoaded = true;
            }
            catch (Exception ex)
            {
                automaticBackupError = ex.Message;
                retryBackupUtc = DateTime.UtcNow.AddMinutes(5);
            }
        }

        internal void CheckAutomaticBackup(DateTimeOffset now)
        {
            var plan = state.Backup;
            if (plan == null || plan.Cadence == BackupCadence.Disabled || automaticBackupRunning || DateTime.UtcNow < retryBackupUtc) return;
            RefreshBackupDate();
            var due = plan.NextDate(lastAutomaticBackup, now.Date);
            if (!backupDateLoaded || !due.HasValue || due.Value > now.Date) return;
            var target = store;
            var snapshot = state;
            var day = now.Date;
            automaticBackupRunning = true;
            automaticBackupError = null;
            UpdateBackupStatus(day);
            ThreadPool.QueueUserWorkItem(delegate {
                string failure = null;
                try { target.WriteAutomaticBackup(day, snapshot); }
                catch (Exception ex) { failure = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate {
                    automaticBackupRunning = false;
                    automaticBackupError = failure;
                    if (failure == null) lastAutomaticBackup = day;
                    else retryBackupUtc = DateTime.UtcNow.AddMinutes(5);
                    UpdateBackupStatus(DateTime.Today);
                }));
            });
        }

        private void UpdateBackupStatus(DateTime day)
        {
            if (backupStatus == null) return;
            var plan = state.Backup;
            var next = plan == null ? null : plan.NextDate(lastAutomaticBackup, day);
            if (automaticBackupRunning) backupStatus.Text = "正在生成完整备份…";
            else if (automaticBackupError != null) backupStatus.Text = "自动备份遇到问题，稍后会重试。" + Environment.NewLine + automaticBackupError;
            else
            {
                string last = lastAutomaticBackup.HasValue ? "上次备份 " + WorkState.PeriodKey(lastAutomaticBackup.Value, false) : "暂无自动备份";
                string upcoming = next.HasValue ? "下次备份 " + WorkState.PeriodKey(next.Value, false) : "自动备份已关闭";
                backupStatus.Text = last + "　·　" + upcoming;
            }
        }
    }
}
