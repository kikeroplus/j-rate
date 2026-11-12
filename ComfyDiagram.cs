// 生成する: 図の画面 (左に設定・真ん中に上から順の処理・右に送る前の確認)。
// GenCommon の docs/relations-columns.html の図をそのまま画面にしたもの (PromptWordCrafter・ImageMimicArt の
// gencommon/diagram_dialog.py と同じ作り)。いつもの画面 (ComfyPromptDialog) と同じ欄を並べ替えただけなので、
// 設定の読み書きや「生成する」の中身はいつもの画面のものを使う。
// 真ん中の段をクリックすると、その段を通ったあとの文を右に出す (生成を押す前の確認。LLM はまだ呼ばない)。
// 読み取りの段の「今読み取る」だけは、その場で LLM に読ませて .txt に書く。
// generation-standard 4 章「図の画面」。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JRate
{
    public partial class ComfyPromptDialog
    {
        // 画面を切り替えるときに持っていく、Reopen (GenRequest) にない欄
        public class DialogCarry
        {
            public string Repeat = "1";
            public int Set = -1;
            public int PromptPreset = -1;
            public bool Reread;
        }

        // 「図で開く」/「いつもの画面で開く」を押して閉じた (Reopen と Carry で開き直す)
        public bool SwitchView { get; private set; }
        public DialogCarry Carry { get; private set; }

        private readonly bool settingsOnly;            // 「図で設定」: 生成せずに前回の値として保存
        private Button viewButton;
        private Dictionary<string, object> wfData;     // 選んでいるワークフロー (読めないときは null)

        private static readonly Brush OptionalLine = new SolidColorBrush(Color.FromRgb(0xb7, 0x85, 0x1f));
        private const double StepWidth = 250, ArrowWidth = 26;

        private Dictionary<string, StepBox> steps;     // 図の画面のときだけ
        private Dictionary<string, TextBlock> pointers;
        private string selectedStep = "generate";
        private TextBlock previewTitle, noBaseHint;
        private TextBox preview;
        private Button readButton;          // 読み取りの段の「今読み取る」
        private bool reading;               // 「今読み取る」の最中
        private bool dialogClosed;
        private string readNote = "";       // 「今読み取る」の結果 (読み取りの段に出す)

        // 真ん中の処理の段。クリックで選ぶ
        private class StepBox : Grid
        {
            public readonly string Key;
            private readonly bool optional;
            private readonly Rectangle frame;
            private readonly TextBlock title, state;
            private readonly StackPanel texts;

            public StepBox(string key, string text, bool optional)
            {
                Key = key;
                this.optional = optional;
                Width = StepWidth;
                Cursor = Cursors.Hand;
                ToolTip = "クリックすると、この段を通ったあとの内容を右に出します";
                frame = new Rectangle { RadiusX = 6, RadiusY = 6, Fill = Brushes.White, StrokeThickness = 1.5 };
                if (optional) frame.StrokeDashArray = new DoubleCollection { 4, 3 };
                Children.Add(frame);
                texts = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
                title = new TextBlock { Text = text, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
                state = new TextBlock
                {
                    Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                };
                texts.Children.Add(title);
                texts.Children.Add(state);
                Children.Add(texts);
            }

            public string TitleText { get { return title.Text; } }

            // 段の中の小さなボタン (押しても段は選ばない)
            public Button AddButton(string text, string tip)
            {
                var button = new Button
                {
                    Content = text, ToolTip = tip, Cursor = Cursors.Arrow,
                    Padding = new Thickness(10, 0, 10, 1), Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                texts.Children.Add(button);
                return button;
            }

            public void Show(string text, bool skipped, bool selected)
            {
                state.Text = text;
                frame.Stroke = selected ? SystemColors.HighlightBrush : optional ? OptionalLine : Brushes.Gray;
                frame.StrokeThickness = selected ? 2.5 : 1.5;
                title.Foreground = skipped ? Brushes.Gray : Brushes.Black;
            }
        }

        // ---------------- 並べ直し ----------------
        private void BuildDiagram(StackPanel panel, DockPanel setRow, DockPanel destRow, DockPanel wfRow,
                                  StackPanel secRow, StackPanel repeatRow, DockPanel bottom)
        {
            // いつもの画面の並びから外し、図の並びに入れ直す (欄そのものは同じ。処理もいつもの画面のまま)
            panel.Children.Clear();
            Width = 1200;
            steps = new Dictionary<string, StepBox>();
            pointers = new Dictionary<string, TextBlock>();

            // 読み取り・エンハンサーの行は横に長いので、左の列の幅に収まるように並べ直す
            Restack(readerRow);
            Restack(enhancerRow);
            baseCheck.Margin = new Thickness(0);
            captionHint.Margin = new Thickness(0, 4, 0, 0);
            llmNote.Margin = new Thickness(0, 4, 0, 0);
            workflowError.Margin = new Thickness(0, 0, 0, 8);
            machineHint.Margin = new Thickness(0, 4, 0, 0);
            promptBox.Margin = new Thickness(0, 4, 0, 4);
            secRow.Margin = new Thickness(0, 4, 0, 0);
            repeatRow.Margin = new Thickness(0, 4, 0, 0);
            destRow.Margin = new Thickness(0);
            wfRow.Margin = new Thickness(0);
            promptRow.Margin = new Thickness(0, 0, 0, 6);

            // セット: 中身の概要が見えるカードを横に並べる (選んでいる番号は setBox が持つ。setBox は画面に出さない)
            var setButtons = setRow.Children.OfType<Button>().ToList();   // 削除・保存
            var setLabel = setRow.Children.OfType<TextBlock>().FirstOrDefault();
            setRow.Children.Clear();
            if (setLabel != null)
            {
                setLabel.FontWeight = FontWeights.Bold;
                setLabel.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(setLabel, Dock.Left);
                setRow.Children.Add(setLabel);
            }
            var setSide = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var b in setButtons.AsEnumerable().Reverse())   // 保存、削除の順に縦に並べる
            {
                b.Margin = new Thickness(0, 0, 0, 6);
                setSide.Children.Add(b);
            }
            DockPanel.SetDock(setSide, Dock.Right);
            setRow.Children.Add(setSide);
            setStrip = new SetCardStrip(n => setBox.SelectedIndex = n);
            setRow.Children.Add(setStrip);
            setBox.SelectionChanged += delegate { RefreshSetCards(); };
            RefreshSetCards();

            var root = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(setRow, Dock.Top);
            root.Children.Add(setRow);
            DockPanel.SetDock(workflowError, Dock.Top);
            root.Children.Add(workflowError);
            bottom.Margin = new Thickness(0, 12, 0, 0);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            // 左 (設定) と真ん中 (処理) の表。右 (出力) は、その高さに合わせる
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StepWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });

            var noBaseText = "ベースプロンプトは未設定です (「生成の設定...」で設定します)";
            noBaseHint = new TextBlock { Text = noBaseText, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            var list = new List<Tuple<string, string, bool, UIElement>>
            {
                Tuple.Create("read", "読み取り", true,
                             (UIElement)Group("元画像と読み取り (読み取りプリセットは共有)", readerRow, captionHint)),
                Tuple.Create("compose", "組み立て", false,
                             (UIElement)Group("入力するプロンプト", promptRow, promptLabel, promptBox,
                                              Hint("「" + Reader.CaptionMark + "」と書くと、そこに説明プロンプトが入ります (なければ説明のあとに改行して続けます)"))),
                Tuple.Create("enhance", "エンハンサーで盛る", true, (UIElement)Group("エンハンサー (共有)", enhancerRow, llmNote)),
                Tuple.Create("base", "ベースプロンプトを付ける", true, (UIElement)Group("ベースプロンプト (共有)", baseCheck, noBaseHint)),
                Tuple.Create("workflow", "ワークフローに入れる", false, (UIElement)Group("ワークフロー (共有)", wfRow, machineHint, secRow)),
                Tuple.Create("generate", "ComfyUI で生成", false,
                             (UIElement)(settingsOnly ? Group("接続先 (共有)", destRow) : Group("接続先 (共有)", destRow, repeatRow))),
            };

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddAt(grid, Head("ユーザーが設定する"), 0, 0);
            AddAt(grid, Head("処理 (上から順に)"), 0, 2);
            for (int i = 0; i < list.Count; i++)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddAt(grid, list[i].Item4, row, 0);
                AddAt(grid, Arrow("→"), row, 1);
                var step = new StepBox(list[i].Item1, (i + 1) + ". " + list[i].Item2, list[i].Item3)
                {
                    VerticalAlignment = VerticalAlignment.Center
                };
                string key = list[i].Item1;
                step.MouseLeftButtonUp += delegate { selectedStep = key; RefreshDiagram(); };
                if (key == "read" && sources.Count > 0)
                {
                    readButton = step.AddButton("今読み取る", "生成を待たずに、今 LLM で読み取って .txt に書きます (読み取り済みのものは読み直します)");
                    readButton.Click += delegate { ReadNow(); };
                }
                steps[key] = step;
                AddAt(grid, step, row, 2);
                var pointer = new TextBlock
                {
                    FontSize = 16, Foreground = SystemColors.HighlightBrush,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                };
                pointers[key] = pointer;
                AddAt(grid, pointer, row, 3);
                if (i < list.Count - 1)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    AddAt(grid, Arrow("↓"), row + 1, 2);
                }
            }

            // 右: 出力 (送る前の確認)
            previewTitle = new TextBlock { FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            preview = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(2, 3, 2, 3)
            };
            var made = Hint("生成すると出来るもの: 説明プロンプト (.txt)・出来た画像・動画・生成の記録");
            made.Margin = new Thickness(0, 4, 0, 0);
            var outPanel = new DockPanel();
            DockPanel.SetDock(previewTitle, Dock.Top);
            DockPanel.SetDock(made, Dock.Bottom);
            outPanel.Children.Add(previewTitle);
            outPanel.Children.Add(made);
            outPanel.Children.Add(preview);
            var outBox = new GroupBox { Header = "出力", Content = outPanel, Padding = new Thickness(6) };
            var outColumn = new DockPanel();
            var outHead = Head("出力 (送る前の確認)");
            DockPanel.SetDock(outHead, Dock.Top);
            outColumn.Children.Add(outHead);
            outColumn.Children.Add(outBox);
            // 右の高さは左の表に合わせる (確認の文が長くても、窓を伸ばさずにスクロールさせる)
            outColumn.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = grid });

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.Margin = new Thickness(0, 0, 12, 0);
            outColumn.VerticalAlignment = VerticalAlignment.Top;
            AddAt(body, grid, 0, 0);
            AddAt(body, outColumn, 0, 1);
            root.Children.Add(body);
            Content = root;

            // 左で設定を変えたら、真ん中と右をすぐ直す
            promptBox.TextChanged += delegate { RefreshDiagram(); };
            readerBox.SelectionChanged += delegate { RefreshDiagram(); };
            enhancerBox.SelectionChanged += delegate { RefreshDiagram(); };
            presetBox.SelectionChanged += delegate { RefreshDiagram(); };
            workflowBox.SelectionChanged += delegate { RefreshDiagram(); };
            setBox.SelectionChanged += delegate { RefreshDiagram(); };
            secondsBox.TextChanged += delegate { RefreshDiagram(); };
            repeatBox.TextChanged += delegate { RefreshDiagram(); };
            RoutedEventHandler changed = delegate { RefreshDiagram(); };
            rereadCheck.Checked += changed;
            rereadCheck.Unchecked += changed;
            baseCheck.Checked += changed;
            baseCheck.Unchecked += changed;
            Closed += delegate { dialogClosed = true; };
            RefreshDiagram();
        }

        // 横に並んだ行 (ラベル・欄・チェック・「設定...」) を、欄を幅いっぱいにした 1 行と、チェックの行に並べ直す
        private static void Restack(StackPanel row)
        {
            var items = row.Children.Cast<UIElement>().ToList();
            row.Children.Clear();
            row.Orientation = Orientation.Vertical;
            row.Margin = new Thickness(0);
            var line = new DockPanel();
            var checks = new List<UIElement>();
            Control fill = null;
            foreach (var item in items)
            {
                if (item is CheckBox) { checks.Add(item); continue; }
                if (item is ComboBox) { fill = (Control)item; continue; }
                var fe = (FrameworkElement)item;
                DockPanel.SetDock(fe, item is Button ? Dock.Right : Dock.Left);
                line.Children.Add(fe);
            }
            if (fill != null)
            {
                fill.Width = double.NaN;
                line.Children.Add(fill);
            }
            row.Children.Add(line);
            foreach (var c in checks)
            {
                ((FrameworkElement)c).Margin = new Thickness(80, 4, 0, 0);
                row.Children.Add(c);
            }
        }

        // ---------------- セットのカード ----------------
        private SetCardStrip setStrip;
        private readonly Dictionary<string, string> wfKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void RefreshSetCards()
        {
            if (setStrip == null) return;
            var infos = new List<SetCardInfo>();
            var json = ComfyVideo.NewSerializer();
            for (int i = 0; i < setList.Length; i++)
            {
                var s = setList[i];
                if (s.IsEmpty)
                {
                    var empty = new SetCardInfo { Title = "空き", Empty = true, Tip = "空きの番号です。選んで「保存」を押すと、今の設定をこの番号に保存します。" };
                    empty.Lines.Add("保存すると、今の設定を");
                    empty.Lines.Add("ここに入れます");
                    infos.Add(empty);
                    continue;
                }
                var info = new SetCardInfo { Title = s.Name.Length > 0 ? s.Name : "(名前なし)" };
                var notes = new List<string>();
                // 動画か画像か (ワークフローの中身で見分ける。描き直すたびに読まないよう覚えておく)
                string kind;
                if (!wfKinds.TryGetValue(s.Workflow, out kind))
                {
                    try { kind = ComfyVideo.IsVideoWorkflow(ComfyVideo.LoadWorkflow(json, s.Workflow)) ? "video" : "image"; }
                    catch { kind = ""; }
                    wfKinds[s.Workflow] = kind;
                }
                if (kind == "video") info.Kind = "🎬 動画" + (s.Seconds.Length > 0 ? "  " + s.Seconds + " 秒" : "");
                else if (kind == "image") info.Kind = "🖼 画像";
                else { info.Kind = "⚠ ワークフローを読めません"; info.Warn = true; }
                int wi = workflowPath.FindIndex(p => string.Equals(p, s.Workflow, StringComparison.OrdinalIgnoreCase));
                string wfLabel = wi >= 0 ? workflowName[wi] : System.IO.Path.GetFileName(s.Workflow);
                if (wi < 0) notes.Add("ワークフローが登録にありません");
                int ci = presetUrl.FindIndex(u => ComfySettings.UrlKey(u) == ComfySettings.UrlKey(s.Url));
                string connLabel = ci >= 0 && presetName[ci].Length > 0 ? presetName[ci] : s.Url;
                if (ci < 0) notes.Add("接続先が登録にありません");
                string machine = MachineConflict(s.Url, s.Workflow);
                if (machine.Length > 0) notes.Add("ワークフローは " + machine + " 用です (接続先の台と違います)");
                info.Lines.Add("WF: " + wfLabel);
                info.Lines.Add("接続: " + connLabel);
                string prompt = string.Join(" ", s.Prompt.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
                if (prompt.Length > 0) info.Lines.Add("プロンプト: " + prompt);
                info.Lines.Add("読取: " + (s.Reader == null ? "—" : s.Reader.Length > 0 ? s.Reader : "なし") +
                               "  盛る: " + (s.Enhancer == null ? "—" : s.Enhancer.Length > 0 ? s.Enhancer : "なし"));
                if (notes.Count > 0)
                {
                    info.Warn = true;
                    if (!info.Kind.StartsWith("⚠")) info.Kind += notes.Count == 1 && machine.Length > 0 ? "  ⚠ 台が違う" : "  ⚠ 登録にない";
                }
                var tip = new List<string>
                {
                    info.Title,
                    "ワークフロー: " + wfLabel + "  (" + s.Workflow + ")",
                    "接続先: " + connLabel + "  (" + s.Url + ")"
                };
                if (s.Seconds.Length > 0) tip.Add("長さ: " + s.Seconds + " 秒");
                if (prompt.Length > 0) tip.Add("プロンプト: " + (prompt.Length > 200 ? prompt.Substring(0, 200) + "..." : prompt));
                tip.Add("読み取り: " + (s.Reader == null ? "(この項目なし。選んでも変えない)" : s.Reader.Length > 0 ? s.Reader : "なし"));
                if (s.Reread != null) tip.Add("読み取り済みでも読み直す: " + (s.Reread.Value ? "する" : "しない"));
                tip.Add("エンハンサー: " + (s.Enhancer == null ? "(この項目なし。選んでも変えない)" : s.Enhancer.Length > 0 ? s.Enhancer : "なし"));
                foreach (string n in notes) tip.Add("⚠ " + n);
                info.Tip = string.Join("\n", tip);
                infos.Add(info);
            }
            setStrip.Refresh(infos, setBox.SelectedIndex);
        }

        private static GroupBox Group(string header, params UIElement[] items)
        {
            var stack = new StackPanel();
            foreach (var item in items) stack.Children.Add(item);
            return new GroupBox { Header = header, Content = stack, Padding = new Thickness(6), Margin = new Thickness(0, 2, 0, 2) };
        }

        private static TextBlock Head(string text)
        {
            return new TextBlock
            {
                Text = text, FontWeight = FontWeights.Bold, FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 6)
            };
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock { Text = text, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        }

        private static TextBlock Arrow(string text)
        {
            return new TextBlock
            {
                Text = text, Foreground = Brushes.Gray, FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static void AddAt(Grid grid, UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        // ---------------- 段の状態と、右の確認 ----------------
        private void RefreshDiagram()
        {
            if (steps == null) return;   // いつもの画面 / 並べ直す前
            bool hasFile = sources.Count > 0;
            string name = hasFile ? System.IO.Path.GetFileName(sources[0]) : "";
            string caption = hasFile ? captions[0] : null;
            int ri = CurrentReader(), ei = CurrentEnhancer();
            bool reads = ri >= 0;
            bool reread = rereadCheck.IsChecked == true;
            bool willRead = reads && (!hasFile || reread || string.IsNullOrWhiteSpace(caption));
            bool canPrompt = promptBox.IsEnabled;
            LlmPreset enh = ei >= 0 ? enhancers[ei] : null;
            bool useBase = baseCheck.IsChecked == true && basePrompt.Length > 0;

            // 各段を通ったあとの文 (分からないところは〈〉で示す)
            string capText = !reads ? null : willRead ? "〈読み取った説明プロンプト〉" : caption.Trim();
            string input = canPrompt ? promptBox.Text.Trim() : "";
            string composed = Reader.Compose(input, capText);
            string afterEnhance = enh != null ? "〈盛った本文〉＋" : composed;
            string final = !useBase ? afterEnhance : afterEnhance.Length == 0 ? basePrompt : afterEnhance + "\n" + basePrompt;
            bool video = wfData != null && ComfyVideo.IsVideoWorkflow(wfData);
            string kind = video ? "動画用" : "画像用";

            var states = new Dictionary<string, Tuple<string, bool>>();
            // 1. 読み取り
            if (!reads) states["read"] = Tuple.Create("なし (説明プロンプトを使わない)", true);
            else if (sources.Count > 1)
            {
                int done = captions.Count(c => !string.IsNullOrWhiteSpace(c));
                states["read"] = Tuple.Create("済み " + done + " / 未 " + (sources.Count - done) + " 枚" + (reread ? " → すべて読み直す" : ""),
                                              !reread && done == sources.Count);
            }
            else if (!hasFile) states["read"] = Tuple.Create("未読み取りの画像は生成のときに読み取る", false);
            else if (willRead) states["read"] = Tuple.Create(reread && !string.IsNullOrWhiteSpace(caption) ? "読み直す" : "生成のときに読み取る", false);
            else states["read"] = Tuple.Create("読み取り済み (.txt を使う)", true);
            if (readNote.Length > 0) states["read"] = Tuple.Create(readNote + "\n" + states["read"].Item1, states["read"].Item2);
            if (readButton != null)
            {
                bool anyUnread = captions.Any(c => string.IsNullOrWhiteSpace(c));
                readButton.Content = anyUnread || reread ? "今読み取る" : "今読み直す";
                readButton.IsEnabled = !reading && reads && ReadTargets().Count > 0;
            }
            // 2. 組み立て
            string how = !canPrompt ? "プロンプトを入れる場所がない"
                       : input.Contains(Reader.CaptionMark) ? Reader.CaptionMark + " の位置に説明"
                       : !reads ? (input.Length == 0 ? "入力なし" : "入力をそのまま")
                       : input.Length == 0 ? "説明だけ" : "説明 + 改行 + 入力";
            states["compose"] = Tuple.Create(how, !canPrompt);
            // 3. 盛る
            states["enhance"] = Tuple.Create(enh != null ? enh.Name : "なし (素通り)", enh == null);
            // 4. ベース
            states["base"] = basePrompt.Length == 0 ? Tuple.Create("ベースプロンプトなし", true)
                           : Tuple.Create(useBase ? "付ける (" + kind + ")" : "付けない", !useBase);
            // 5. ワークフロー
            if (wfData == null) states["workflow"] = Tuple.Create("使えません (上の赤字を参照)", false);
            else states["workflow"] = Tuple.Create(workflowName[workflowBox.SelectedIndex] + " ・ " +
                                                   ComfyVideo.PromptSlotLabels(wfData).Count + " か所 (" + kind + ")", false);
            // 6. 生成
            int sel = Math.Max(presetBox.SelectedIndex, 0);
            string conn = presetName[sel].Length > 0 ? presetName[sel] : presetUrl[sel];
            string times = settingsOnly ? "" : " ・ " + RepeatText() + " 回" + (sources.Count > 1 ? "ずつ" : "");
            states["generate"] = Tuple.Create(conn + times, false);

            noBaseHint.Visibility = basePrompt.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var kv in steps)
            {
                kv.Value.Show(states[kv.Key].Item1, states[kv.Key].Item2, kv.Key == selectedStep);
                pointers[kv.Key].Text = kv.Key == selectedStep ? "▶" : "";
            }

            string title, text;
            Preview(selectedStep, name, caption, reads, willRead, ri, enh, composed, final, useBase, canPrompt, out title, out text);
            if (sources.Count > 1) title += "  ※ 1 枚目 (" + name + ") の例";
            else if (!hasFile) title += "  ※ 画像を選んでいないので、説明プロンプトは〈〉で示します";
            previewTitle.Text = title;
            preview.Text = text;
        }

        private string RepeatText()
        {
            string t = repeatBox.Text.Trim();
            return t.Length > 0 ? t : "1";
        }

        private void Preview(string key, string name, string caption, bool reads, bool willRead, int ri, LlmPreset enh,
                             string composed, string final, bool useBase, bool canPrompt, out string title, out string text)
        {
            string step = steps[key].TitleText;
            switch (key)
            {
                case "read":
                {
                    title = step + " のあと: 説明プロンプト";
                    if (!reads)
                    {
                        text = "読み取りは「なし」です。説明プロンプトは使わず、入力したプロンプトだけを送ります。";
                        return;
                    }
                    string how = "読み取りプリセット「" + readers[ri].Name + "」・モデル " + LlmSettings.ReaderModel + " で読み取ります。\n" +
                                 "読み取った文は " + (name.Length > 0 ? name : "元画像") + " と同じ名前の .txt に書き、次からはそれを使います。";
                    if (name.Length == 0)
                    {
                        title = step;
                        text = "説明プロンプトがまだない画像 (と「読み取り済みでも読み直す」のとき) は、生成のときに、" + how;
                    }
                    else if (willRead && !string.IsNullOrWhiteSpace(caption))
                        text = "生成のときに読み直します。" + how + "\n\n── 今の .txt ──\n" + caption.Trim();
                    else if (willRead) text = "まだ読み取っていません。生成のときに、" + how;
                    else
                    {
                        title = step + " のあと: 説明プロンプト (.txt の内容)";
                        text = caption.Trim();
                    }
                    return;
                }
                case "compose":
                    title = step + " のあと: プロンプト";
                    text = canPrompt ? composed
                                     : "このワークフローにはプロンプトを入れる場所がないので、プロンプトは送りません。";
                    return;
                case "enhance":
                    if (enh == null)
                    {
                        title = step + " のあと";
                        text = "エンハンサーは「なし」です。前の段の文をそのまま次へ渡します。\n\n" + composed;
                        return;
                    }
                    title = step + ": 生成のときに LLM に送る内容";
                    text = "エンハンサー「" + enh.Name + "」・モデル " + LlmSettings.EnhanceModel + " で盛ります。" +
                           "ジョブの番が来るたびに盛るので、回数を増やすと毎回違う盛り方になります。\n\n" +
                           "── 指示 (system) ──\n" + enh.System + "\n\n── 送る文 ──\n" + Enhancer.UserMessage(enh.Template, composed);
                    return;
                case "base":
                    title = step + " のあと: ComfyUI に送る本文";
                    text = (useBase ? "" : basePrompt.Length > 0 ? "(ベースプロンプトを付けません)\n\n" : "(ベースプロンプトは未設定です)\n\n") + final;
                    return;
                case "workflow":
                {
                    title = step + ": 入れる場所";
                    if (wfData == null)
                    {
                        text = workflowError.Text.Length > 0 ? workflowError.Text : "ワークフローを読めません。";
                        return;
                    }
                    var lines = new List<string> { "プロンプトを入れる場所:" };
                    var slots = ComfyVideo.PromptSlotLabels(wfData);
                    if (slots.Count == 0) lines.Add("  (ありません。プロンプトは送りません)");
                    foreach (var s in slots) lines.Add("  ・" + s);
                    lines.Add("  (タイトルに Negative とあるノードと、実際には使われない側には入れません)");
                    lines.Add("\n元画像は「Load Image」に入れて送ります。");
                    lines.Add("\n長さ: " + (secondsBox.IsEnabled ? secondsBox.Text.Trim() + " 秒" : "このワークフローでは変えられません"));
                    text = string.Join("\n", lines);
                    return;
                }
                default:
                {
                    title = step + ": 送るもの";
                    int sel = Math.Max(presetBox.SelectedIndex, 0);
                    int wf = workflowBox.SelectedIndex;
                    var lines = new List<string>
                    {
                        "接続先: " + (presetName[sel].Length > 0 ? presetName[sel] + "  " : "") + presetUrl[sel],
                        "ワークフロー: " + (wf >= 0 ? workflowName[wf] : "(なし)")
                    };
                    if (secondsBox.IsEnabled) lines.Add("長さ: " + secondsBox.Text.Trim() + " 秒");
                    if (!settingsOnly) lines.Add("回数: " + RepeatText() + " 回" + (sources.Count > 1 ? "ずつ" : ""));
                    lines.Add("\n── 送る本文 ──\n" + (canPrompt ? final : "(プロンプトは送りません)"));
                    text = string.Join("\n", lines);
                    return;
                }
            }
        }

        // ---------------- 今読み取る ----------------
        // 今読み取るもの: 未読み取り (読み直すときはすべて)。すべて読み取り済みなら、すべて読み直す
        private List<int> ReadTargets()
        {
            var every = Enumerable.Range(0, sources.Count).ToList();
            if (rereadCheck.IsChecked == true) return every;
            var unread = every.Where(i => string.IsNullOrWhiteSpace(captions[i])).ToList();
            return unread.Count > 0 ? unread : every;
        }

        private void ReadNow()
        {
            int ri = CurrentReader();
            var targets = ReadTargets();
            if (ri < 0 || targets.Count == 0 || reading) return;
            var snap = Reader.Snapshot(readers[ri]);
            var paths = targets.Select(i => sources[i]).ToList();
            int total = targets.Count;
            reading = true;
            readNote = "読み取り中... 0 / " + total;
            okButton.IsEnabled = false;   // 読み取っている最中は生成しない (ジョブでも読み取ってしまうので)
            RefreshDiagram();
            var dispatcher = Dispatcher;
            Task.Run(() =>
            {
                int done = 0;
                string error = null;
                for (int k = 0; k < total; k++)
                {
                    int n = k;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (dialogClosed) return;
                        readNote = "読み取り中... " + n + " / " + total;
                        RefreshDiagram();
                    }));
                    try
                    {
                        string text = Reader.ReadWith(snap, paths[n]);
                        Reader.WriteCaption(paths[n], text);   // 画面を閉じていても .txt には書く
                        int index = targets[n];
                        dispatcher.BeginInvoke(new Action(() => { captions[index] = text; }));
                        done++;
                    }
                    catch (Exception ex)
                    {
                        error = System.IO.Path.GetFileName(paths[n]) + ": " + ex.Message;
                        break;
                    }
                }
                int doneCount = done;
                string err = error;
                dispatcher.BeginInvoke(new Action(() => FinishRead(doneCount, total, err)));
            });
        }

        private void FinishRead(int done, int total, string error)
        {
            reading = false;
            if (dialogClosed) return;
            if (done > 0 && rereadCheck.IsChecked == true) rereadCheck.IsChecked = false;   // 今読み取ったので、生成のときは読み直さない
            readNote = error == null ? done + " / " + total + " 枚を今読み取りました" : "読み取りに失敗しました (" + done + " / " + total + ")";
            okButton.IsEnabled = wfData != null || settingsOnly;
            UpdateLlmHints();   // 「＋ 説明プロンプト」の行と、図の段・右の確認を出し直す
            if (error != null)
                MessageBox.Show(this, "読み取りに失敗しました。\n\n" + error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------------- 「図で設定」: 生成せずに前回の値として保存 ----------------
        private void SaveSettingsOnly()
        {
            double sec;
            if (secondsBox.IsEnabled && TryParseSeconds(secondsBox.Text, out sec)) ComfySettings.Seconds = secondsBox.Text;
            int sel = Math.Max(presetBox.SelectedIndex, 0);
            if (presetIndex[sel] >= 0) ComfySettings.ActivePreset = presetIndex[sel];
            if (workflowBox.SelectedIndex >= 0) ComfySettings.ActiveWorkflow = workflowIndex[workflowBox.SelectedIndex];
            ComfySettings.LastSet = setBox.SelectedIndex;
            if (promptBox.IsEnabled)
            {
                ComfySettings.Prompt = promptBox.Text.Trim();
                ComfySettings.LastPromptPreset = promptPresetBox.SelectedIndex;
            }
            ComfySettings.UseBasePrompt = baseCheck.IsChecked == true;
            LlmPresets.SaveIndex(Reader.GenerateFile, CurrentReader());
            LlmPresets.SaveIndex(Enhancer.GenerateFile, CurrentEnhancer());
            Reader.Reread = rereadCheck.IsChecked == true;
            DialogResult = true;
        }
    }
}
