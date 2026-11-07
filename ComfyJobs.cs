// J-Rate : 生成ジョブ (一覧とキャンセル)、生成時の設定の表示、名前の入力
// .NET Framework 4.x 標準の csc.exe (C# 5) でビルドできるように書いている。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace JRate
{
    public enum GenState { Waiting, Running, Done, Failed, Canceled }

    // ------------------------------------------------------------------
    // 生成ジョブ 1 件 (画像 1 枚を 1 回生成する)
    // ------------------------------------------------------------------
    public class GenJob : INotifyPropertyChanged
    {
        public readonly string Source;        // 元画像のパス
        public GenRequest Request;            // 頼んだ時点の内容 (あとで設定を変えても、この内容で続ける)
        public readonly bool Batch;           // まとめて生成の 1 件 (エラーの窓を出さない、完成しても移動しない)
        public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
        public List<string> Outputs;          // 保存したファイル (完了後)
        public string PromptId = "";          // ComfyUI が受け付けた番号 (続きから始めるときに使う)
        public bool Held;                     // 一時停止中に待機へ戻したもの (再開するまで実行に移さない)
        public DateTime Started;              // 実行に移った時刻

        private GenState state = GenState.Waiting;
        private string status = "";
        private bool paused;                  // 一時停止中の待機 (状態の欄に「一時停止」と出す)
        private double? runTime;              // 生成にかかった秒数 (完了したときに決まる)

        public GenJob(string source, GenRequest request, bool batch)
        {
            Source = source;
            Request = request;
            Batch = batch;
        }

        public string FileName { get { return Path.GetFileName(Source); } }
        public string Workflow { get { return Request.WorkflowName; } }

        public GenState State
        {
            get { return state; }
            set { state = value; OnPropertyChanged("State"); OnPropertyChanged("StateText"); }
        }

        public bool IsRunning { get { return state == GenState.Running; } }

        // 一時停止中かどうか (ジョブ一覧の表示用。待機中のものだけ「一時停止」と出す)
        public bool Paused
        {
            get { return paused; }
            set { if (paused != value) { paused = value; OnPropertyChanged("StateText"); } }
        }

        public double? RunTime
        {
            get { return runTime; }
            set { runTime = value; OnPropertyChanged("RunTimeText"); }
        }

        public string RunTimeText { get { return runTime == null ? "" : ComfyVideo.FormatElapsed(runTime.Value); } }

        // まだ終わっていない (待機中か実行中)
        public bool IsActive { get { return state == GenState.Waiting || state == GenState.Running; } }

        public string StateText
        {
            get
            {
                switch (state)
                {
                    case GenState.Waiting: return paused ? "一時停止" : "待機中";
                    case GenState.Running: return "実行中";
                    case GenState.Done: return "完了";
                    case GenState.Failed: return "失敗";
                    default: return "キャンセル";
                }
            }
        }

        // 進み具合や結果、エラーの内容 (1 行)
        public string Status
        {
            get { return status; }
            set { status = OneLine(value); OnPropertyChanged("Status"); }
        }

        private static string OneLine(string s)
        {
            return string.Join(" ", (s ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    // ------------------------------------------------------------------
    // 生成ジョブの一覧 (閉じてもジョブは続く)
    // ------------------------------------------------------------------
    public class JobWindow : Window
    {
        private readonly ObservableCollection<GenJob> jobs;
        private readonly ListView list;
        private readonly Button pauseButton, cancelButton, cancelAllButton, showButton, clearButton;
        private readonly TextBlock runningText;
        private readonly Func<bool> isPaused;
        private GenJob lastScrolled;   // 実行に移ったのを見えるようにした行 (同じものでは何度もスクロールしない)

        // cancel : そのジョブを取り消す / show : そのジョブの結果をメイン画面に表示する
        // isPaused / setPaused : 一時停止しているか / 一時停止・再開する
        public JobWindow(ObservableCollection<GenJob> jobs, Action<GenJob> cancel, Action<GenJob> show,
                         Func<bool> isPaused, Action<bool> setPaused)
        {
            this.jobs = jobs;
            this.isPaused = isPaused;
            Title = "生成ジョブ - J-Rate";
            Width = 760;
            Height = 380;
            MinWidth = 480;
            MinHeight = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };

            var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var close = new Button { Content = "閉じる", Width = 90, IsCancel = true };
            DockPanel.SetDock(close, Dock.Right);
            buttons.Children.Add(close);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            pauseButton = NewButton("一時停止");
            pauseButton.ToolTip = "新しく ComfyUI に頼むのをやめます (実行中のものは最後まで生成して受け取ります)。\n" +
                                  "一時停止中にワークフローを直せば、再開したあとのジョブから直した中身で生成します。\n" +
                                  "一時停止のままアプリを閉じると、次に起動したときも一時停止のまま残ります。";
            left.Children.Add(pauseButton);
            cancelButton = NewButton("キャンセル");
            cancelAllButton = NewButton("すべてキャンセル");
            showButton = NewButton("結果を表示");
            clearButton = NewButton("終わったものを消す");
            left.Children.Add(cancelButton);
            left.Children.Add(cancelAllButton);
            left.Children.Add(showButton);
            left.Children.Add(clearButton);
            buttons.Children.Add(left);
            root.Children.Add(buttons);

            // 今どれを処理しているかを、一覧をスクロールしても見えるように上に出す
            runningText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(runningText, Dock.Top);
            root.Children.Add(runningText);

            var grid = new GridView();
            grid.Columns.Add(new GridViewColumn { Header = "ファイル", Width = 190, DisplayMemberBinding = new Binding("FileName") });
            grid.Columns.Add(new GridViewColumn { Header = "ワークフロー", Width = 130, DisplayMemberBinding = new Binding("Workflow") });
            grid.Columns.Add(new GridViewColumn { Header = "状態", Width = 70, DisplayMemberBinding = new Binding("StateText") });
            grid.Columns.Add(new GridViewColumn { Header = "時間", Width = 60, DisplayMemberBinding = new Binding("RunTimeText") });
            grid.Columns.Add(new GridViewColumn { Header = "内容", Width = 300, DisplayMemberBinding = new Binding("Status") });
            list = new ListView { View = grid, ItemsSource = jobs, SelectionMode = SelectionMode.Extended };
            // 実行中の行は色を付けて太字に
            var rowStyle = new Style(typeof(ListViewItem));
            var running = new DataTrigger { Binding = new Binding("State"), Value = GenState.Running };
            running.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            running.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(50, 46, 125, 50))));
            rowStyle.Triggers.Add(running);
            list.ItemContainerStyle = rowStyle;
            root.Children.Add(list);
            Content = root;

            pauseButton.Click += delegate
            {
                setPaused(!isPaused());
                Refresh();
            };
            cancelButton.Click += delegate
            {
                foreach (var job in list.SelectedItems.Cast<GenJob>().ToList()) cancel(job);
                UpdateButtons();
            };
            cancelAllButton.Click += delegate
            {
                foreach (var job in jobs.ToList()) cancel(job);
                UpdateButtons();
            };
            showButton.Click += delegate
            {
                var job = list.SelectedItem as GenJob;
                if (job != null && job.Outputs != null) show(job);
            };
            clearButton.Click += delegate
            {
                foreach (var job in jobs.Where(j => !j.IsActive).ToList()) jobs.Remove(job);
                UpdateButtons();
            };
            close.Click += delegate { Close(); };
            list.SelectionChanged += delegate { UpdateButtons(); };
            list.MouseDoubleClick += delegate
            {
                var job = list.SelectedItem as GenJob;
                if (job != null && job.Outputs != null) show(job);
            };

            // ジョブの状態が変わったらボタンの有効・無効を合わせる
            PropertyChangedEventHandler changed = delegate(object s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == "State" || e.PropertyName == "Status") Refresh();
            };
            System.Collections.Specialized.NotifyCollectionChangedEventHandler listChanged = delegate(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (e.OldItems != null) foreach (GenJob j in e.OldItems) j.PropertyChanged -= changed;
                if (e.NewItems != null) foreach (GenJob j in e.NewItems) j.PropertyChanged += changed;
                Refresh();
            };
            foreach (var j in jobs) j.PropertyChanged += changed;
            jobs.CollectionChanged += listChanged;
            Closed += delegate
            {
                jobs.CollectionChanged -= listChanged;
                foreach (var j in jobs) j.PropertyChanged -= changed;
            };
            Refresh();
        }

        // 一覧の上の「実行中: ...」とボタンを、今の状態に合わせる
        public void Refresh()
        {
            var runningJobs = jobs.Where(j => j.IsRunning).ToList();
            int waiting = jobs.Count(j => j.IsActive && !j.IsRunning);
            var lines = runningJobs.Select(j => "実行中: " + j.FileName + "  " + j.Status).ToList();
            if (isPaused())
            {
                lines.Add(runningJobs.Count > 0
                    ? "一時停止中: 実行中のものが終わったら止まります (待機 " + waiting + " 件)"
                    : "一時停止中 (待機 " + waiting + " 件)。「再開」で続きを生成します");
                runningText.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
                runningText.FontWeight = FontWeights.Bold;
            }
            else if (runningJobs.Count > 0)
            {
                if (waiting > 0) lines.Add("待機 " + waiting + " 件");
                runningText.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                runningText.FontWeight = FontWeights.Bold;
            }
            else
            {
                lines.Add("実行中のジョブはありません");
                runningText.Foreground = Brushes.DimGray;
                runningText.FontWeight = FontWeights.Normal;
            }
            runningText.Text = string.Join("\n", lines);
            // 新しく実行に移ったものが見えるようにする
            var first = runningJobs.FirstOrDefault();
            if (first != null && first != lastScrolled)
            {
                lastScrolled = first;
                list.ScrollIntoView(first);
            }
            UpdateButtons();
        }

        private static Button NewButton(string text)
        {
            return new Button { Content = text, Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(0, 0, 8, 0) };
        }

        private void UpdateButtons()
        {
            var selected = list.SelectedItems.Cast<GenJob>().ToList();
            pauseButton.Content = isPaused() ? "再開" : "一時停止";
            pauseButton.IsEnabled = isPaused() || jobs.Any(j => j.IsActive);
            cancelButton.IsEnabled = selected.Any(j => j.IsActive);
            cancelAllButton.IsEnabled = jobs.Any(j => j.IsActive);
            showButton.IsEnabled = selected.Count == 1 && selected[0].Outputs != null;
            clearButton.IsEnabled = jobs.Any(j => !j.IsActive);
        }
    }

    // ------------------------------------------------------------------
    // 生成したファイルの「生成時の設定」を見せる
    // ------------------------------------------------------------------
    public class GenInfoDialog : Window
    {
        // 「この設定で生成する...」が押されたら true
        public bool Regenerate { get; private set; }

        public GenInfoDialog(string fileName, GenRecord record)
        {
            Title = "生成時の設定 - " + fileName;
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var req = record.Request;
            bool hasSource = File.Exists(record.Source);
            var panel = new StackPanel { Margin = new Thickness(16) };

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            AddRow(grid, "生成した日時", record.Time);
            AddRow(grid, "元の画像", record.Source + (hasSource ? "" : "  (見つかりません)"));
            AddRow(grid, "接続先", req.ConnectionName.Length > 0 ? req.ConnectionName + "  (" + req.Url + ")" : req.Url);
            AddRow(grid, "ワークフロー", req.WorkflowName.Length > 0 && req.WorkflowName != Path.GetFileName(req.WorkflowPath)
                                         ? req.WorkflowName + "  (" + req.WorkflowPath + ")" : req.WorkflowPath);
            AddRow(grid, "長さ", req.Seconds != null
                                 ? req.Seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 秒"
                                 : "(ワークフローのまま)");
            panel.Children.Add(grid);

            panel.Children.Add(new TextBlock { Text = req.Prompt != null ? "プロンプト" : "プロンプト (ワークフローのまま)" });
            panel.Children.Add(new TextBox
            {
                Text = req.Prompt ?? "",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 120,
                Padding = new Thickness(2, 3, 2, 3),
                Margin = new Thickness(0, 4, 0, 16)
            });

            if (req.BasePrompt.Length > 0)
            {
                panel.Children.Add(new TextBlock { Text = "ベースプロンプト (プロンプトのあとに続けて送りました)" });
                panel.Children.Add(new TextBox
                {
                    Text = req.BasePrompt,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Height = 70,
                    Padding = new Thickness(2, 3, 2, 3),
                    Margin = new Thickness(0, 4, 0, 16)
                });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var again = new Button
            {
                Content = "この設定で生成する...",
                Padding = new Thickness(12, 2, 12, 2),
                IsEnabled = hasSource,
                ToolTip = "元の画像から、同じ設定でもう一度生成します (seed は変わります)"
            };
            ToolTipService.SetShowOnDisabled(again, true);
            var close = new Button { Content = "閉じる", Width = 90, IsCancel = true, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(again);
            buttons.Children.Add(close);

            // 左下 : この設定をクリップボードへ (「生成する」の画面の「貼り付け」で使える)
            const string copyLabel = "パラメーターをコピー";
            var copy = new Button
            {
                Content = copyLabel,
                Padding = new Thickness(12, 2, 12, 2),
                ToolTip = "接続先・ワークフロー・プロンプト・長さをクリップボードにコピーします (「生成する」の画面で貼り付けられます)"
            };
            copy.Click += delegate { GenClipboard.Copy(copy, copyLabel, req); };
            var bottom = new DockPanel();
            DockPanel.SetDock(copy, Dock.Left);
            bottom.Children.Add(copy);
            bottom.Children.Add(buttons);
            panel.Children.Add(bottom);
            Content = panel;

            again.Click += delegate
            {
                Regenerate = true;
                DialogResult = true;
            };
        }

        private static void AddRow(Grid grid, string caption, string value)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = new TextBlock { Text = caption, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 16, 6) };
            // 選んでコピーできるように TextBox で出す
            var v = new TextBox
            {
                Text = value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(c, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(c);
            grid.Children.Add(v);
        }
    }

    // ------------------------------------------------------------------
    // 生成パラメーターをクリップボードに入れる / クリップボードから読む
    // ------------------------------------------------------------------
    public static class GenClipboard
    {
        // コピーできたら、押したボタンの表示をしばらく「コピーしました」にする (label は元の表示)
        public static void Copy(Button button, string label, GenRequest request)
        {
            try { Clipboard.SetText(GenParams.ToText(request)); }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(button), "クリップボードにコピーできません。\n\n" + ex.Message, "J-Rate",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (button.ActualWidth > 0) button.MinWidth = button.ActualWidth;   // 表示を変えても幅が動かないように
            button.Content = "コピーしました";
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += delegate
            {
                timer.Stop();
                button.Content = label;
            };
            timer.Start();
        }

        // クリップボードに生成パラメーターがなければ null
        public static GenRequest Paste()
        {
            try { return Clipboard.ContainsText() ? GenParams.Parse(Clipboard.GetText()) : null; }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------
    // ボタンに名前を付けて選ばせる確認 (「残して閉じる / 捨てて閉じる / やめる」など)。
    // Choice は押したボタンの番号 (Esc や × は escapeIndex)
    // ------------------------------------------------------------------
    public class ChoiceDialog : Window
    {
        public int Choice { get; private set; }

        public ChoiceDialog(string title, string message, string detail, string[] buttons, int defaultIndex, int escapeIndex)
        {
            Title = title;
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Choice = escapeIndex;

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = message, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(detail))
                panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                var b = new Button
                {
                    Content = buttons[i], MinWidth = 90, Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsDefault = i == defaultIndex, IsCancel = i == escapeIndex
                };
                b.Click += delegate { Choice = index; DialogResult = true; };
                row.Children.Add(b);
                if (i == defaultIndex) Loaded += delegate { b.Focus(); };
            }
            panel.Children.Add(row);
            Content = panel;
        }
    }

    // ------------------------------------------------------------------
    // 名前を 1 つ入力してもらう小さなダイアログ
    // ------------------------------------------------------------------
    public class NameDialog : Window
    {
        private readonly TextBox box;

        public string Value { get { return box.Text.Trim(); } }

        public NameDialog(string title, string caption, string initial)
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = caption });
            box = new TextBox { Text = initial ?? "", Padding = new Thickness(2, 3, 2, 3), Margin = new Thickness(0, 4, 0, 16) };
            panel.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
            var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;

            ok.Click += delegate { DialogResult = true; };
            Loaded += delegate
            {
                box.Focus();
                box.SelectAll();
            };
        }
    }
}
