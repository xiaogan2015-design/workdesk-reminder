using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WorkDesk
{
    internal sealed partial class MainWindow
    {
        private static readonly string[] ChecklistLabels = {
            "上班后清理 Inbox，明确新增事项",
            "确定今天最重要的三件事",
            "优先处理核心任务，限制并行工作数量",
            "集中处理零碎事项",
            "检查 Waiting 清单，跟进阻塞事项",
            "下班前复盘，安排明天的任务"
        };

        private DateTime checklistDay;
        private CheckBox[] checklistBoxes;
        private TextBlock checklistProgress;
        private Button resetChecklist;

        private void OpenHelp()
        {
            if (page == DeskPage.Help || !CanLeaveSummary()) return;
            SetPage(DeskPage.Help, "帮助", BuildHelpPage());
            RefreshHelpChecklist(DateTime.Today);
        }

        private UIElement BuildHelpPage()
        {
            var panel = new StackPanel();
            var intro = Ui.Text("把时间留给最有价值的工作，同时把要处理、要跟进的事项记下来。", "Hint");
            intro.Margin = new Thickness(0, 0, 0, 13);
            panel.Children.Add(intro);
            panel.Children.Add(BuildHelpChecklist());
            panel.Children.Add(HelpGuide("推荐处理流程", "可以先从 GTD、两分钟法则、四象限、每日三件事和等待清单这五项开始。", new[,] {
                { "收集", "把新收到的事项记入 Inbox。两分钟内能做完且不会打断重要工作的，可以直接处理。" },
                { "明确", "写出具体的下一步。按重要性和紧急程度筛选，选出今天最重要的三件事。" },
                { "执行与跟进", "为核心工作留出时间块。批量处理零碎事项，把等别人反馈的任务放进 Waiting 并约定跟进时间。" },
                { "回顾", "下班前检查完成项和阻塞项，写下当天的成果，并安排明天要推进的事。" }
            }));
            panel.Children.Add(HelpGuide("十个待办处理技巧", null, new[,] {
                { "两分钟法则（2-Minute Rule）", "两分钟内能做完且不会打断重要工作，就可以直接做，如回复简单邮件或确认信息。" },
                { "四象限法则（艾森豪威尔矩阵）", "重要又紧急的先做，重要但不紧急的安排时间，紧急但不重要的酌情委派，两者皆非的考虑删减。" },
                { "GTD 工作法", "把待办从脑子移到清单，依次收集、明确、组织、回顾和执行。" },
                { "二八法则", "找出能带来大部分工作价值的少数关键事项，先给它们留出精力。" },
                { "时间块管理法（Time Blocking）", "给任务约定执行时段，例如上午 9 点至 11 点做核心开发，下午集中沟通。" },
                { "每日三件事（MIT）", "挑出今天最重要的三件事，先确保核心目标有进展。" },
                { "下一步行动法（Next Action）", "把笼统的任务写成可立即执行的动作。例如把「解决性能问题」拆成「采集性能日志，定位最耗时的函数」。" },
                { "批量处理法（Task Batching）", "把同类任务放到一起做，例如集中回复消息或做代码评审，减少切换。" },
                { "等待清单（Waiting For）", "记录依赖谁、预期何时完成，以及自己什么时候跟进。" },
                { "帕金森定律", "工作容易占满预留的时间。提前定一个合理的用时上限，到点检查是否该收尾、拆分或重新排期。" }
            }));
            panel.Children.Add(HelpGuide("研发工作常用的六种清单状态", "Today 适合用来安排今天的工作，Doing 和 Waiting 用来记录进展。", new[,] {
                { "Inbox · 待整理", "新收到、尚未明确下一步的事项。" },
                { "Today · 今日计划", "今天计划完成或推进的事项。" },
                { "Next · 下一步", "后续计划执行的事项。" },
                { "Doing · 进行中", "正在执行的事项。" },
                { "Waiting · 等待中", "正在等待他人回复或外部条件的事项。" },
                { "Done · 已完成", "已经完成的事项。" }
            }));
            return panel;
        }

        private Border BuildHelpChecklist()
        {
            var content = new StackPanel();
            var header = new DockPanel();
            resetChecklist = Ui.Button("重置清单", delegate { SaveHelpChecklist(DateTime.Today, new bool[WorkState.DailyChecklistLength]); });
            DockPanel.SetDock(resetChecklist, Dock.Right);
            header.Children.Add(resetChecklist);
            checklistProgress = Ui.Text("", "Hint");
            checklistProgress.Margin = new Thickness(10, 0, 12, 0);
            checklistProgress.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(checklistProgress, Dock.Right);
            header.Children.Add(checklistProgress);
            var heading = Ui.Text("每日工作检查清单", "SectionTitle");
            heading.TextWrapping = TextWrapping.Wrap;
            heading.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(heading);
            content.Children.Add(header);
            var hint = Ui.Text("勾选会自动保存到本机，次日从一份新清单开始。", "Hint");
            hint.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(hint);

            var list = new StackPanel();
            checklistBoxes = new CheckBox[ChecklistLabels.Length];
            for (int i = 0; i < checklistBoxes.Length; i++)
            {
                int index = i;
                var label = Ui.Text(ChecklistLabels[i]);
                label.TextWrapping = TextWrapping.Wrap;
                var box = new CheckBox { Content = label, MinHeight = 29, Margin = new Thickness(0, 1, 0, 1) };
                box.Click += delegate {
                    var day = DateTime.Today;
                    var steps = state.GetDailyChecklist(day);
                    steps[index] = box.IsChecked == true;
                    SaveHelpChecklist(day, steps);
                };
                checklistBoxes[i] = box;
                list.Children.Add(box);
            }
            content.Children.Add(Ui.FormGroup(list));
            var card = Ui.Card(content);
            card.Margin = new Thickness(0, 0, 0, 14);
            return card;
        }

        private void SaveHelpChecklist(DateTime day, bool[] steps)
        {
            var next = state.Copy();
            next.SetDailyChecklist(day, steps);
            Commit(next);
            RefreshHelpChecklist(DateTime.Today);
        }

        private void RefreshHelpChecklist(DateTime day)
        {
            checklistDay = day.Date;
            var steps = state.GetDailyChecklist(checklistDay);
            for (int i = 0; i < checklistBoxes.Length; i++) checklistBoxes[i].IsChecked = steps[i];
            int checkedCount = steps.Count(step => step);
            checklistProgress.Text = checkedCount + "/" + steps.Length;
            resetChecklist.IsEnabled = checkedCount > 0;
        }

        private static Border HelpGuide(string heading, string intro, string[,] entries)
        {
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text(heading, "SectionTitle"));
            if (intro != null)
            {
                var hint = Ui.Text(intro, "Hint");
                hint.Margin = new Thickness(0, 5, 0, 0);
                panel.Children.Add(hint);
            }
            for (int i = 0; i < entries.GetLength(0); i++)
            {
                var title = Ui.Text(entries[i, 0]);
                title.FontWeight = FontWeights.SemiBold;
                title.TextWrapping = TextWrapping.Wrap;
                title.Margin = new Thickness(0, 12, 0, 3);
                panel.Children.Add(title);
                panel.Children.Add(Ui.Text(entries[i, 1], "Hint"));
            }
            var card = Ui.Card(panel);
            card.Margin = new Thickness(0, 0, 0, 14);
            return card;
        }
    }
}
