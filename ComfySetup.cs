// 生成の設定の整理 (generation-standard 2・3・4 章。PromptWordCrafter・ImageMimicArt の GenCommon と同じ決まり)
//   - 接続先ごとのワークフローのフォルダ (comfy_connection_folders.txt、共有) で、ワークフローがどの台用かを見分ける
//   - まとめて編集 (選んだものを未登録にする・並べ替える・名前順・空きを詰める)
//   - フォルダから登録 (台のフォルダにある、まだ登録していない JSON をまとめて登録する)
//   - 図の画面のセットのカード (中身の概要が見えるカードを横に並べて選ぶ)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace JRate
{
    public static partial class ComfySettings
    {
        public const string ConnectionFoldersFile = "comfy_connection_folders.txt";

        internal static string UrlKey(string url)
        {
            return (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        }

        internal static string FolderKey(string path)
        {
            string t = (path ?? "").Trim().Trim('"');
            if (t.Length == 0) return "";
            try { t = Path.GetFullPath(t); } catch { }
            return t.TrimEnd('\\', '/').ToLowerInvariant();
        }

        // {接続先の URL (小文字・末尾の / なし): ワークフローのフォルダ}
        public static Dictionary<string, string> ConnectionFolders
        {
            get
            {
                var d = new Dictionary<string, string>();
                foreach (string line in (Load(ConnectionFoldersFile) ?? "").Split('\n'))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string url = UrlKey(line.Substring(0, tab)), folder = line.Substring(tab + 1).Trim();
                    if (url.Length > 0 && folder.Length > 0) d[url] = folder;
                }
                return d;
            }
        }

        private static void SaveConnectionFolders(Dictionary<string, string> d)
        {
            var sb = new StringBuilder();
            foreach (var kv in d) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append("\r\n");
            Save(ConnectionFoldersFile, sb.ToString());
        }

        // 接続先のワークフローのフォルダを書く (空なら消す)。oldUrl は URL を書き換えたときの前の URL。
        // ほかのアプリが同時に変えていても消さないよう、読み直してからその行だけを変える
        public static void SetConnectionFolder(string url, string folder, string oldUrl)
        {
            var d = ConnectionFolders;
            if (!string.IsNullOrEmpty(oldUrl) && UrlKey(oldUrl) != UrlKey(url)) d.Remove(UrlKey(oldUrl));
            string key = UrlKey(url);
            folder = OneLine(folder ?? "").Trim('"');
            if (key.Length > 0)
            {
                if (folder.Length > 0) d[key] = folder;
                else d.Remove(key);
            }
            SaveConnectionFolders(d);
        }

        // 消した接続先のフォルダを消す (同じ URL がほかの枠に残っていれば残す)
        public static void RemoveUnusedFolders(IEnumerable<string> removedUrls)
        {
            var remaining = new HashSet<string>(Presets.Where(p => !p.IsEmpty).Select(p => UrlKey(p.Value)));
            var d = ConnectionFolders;
            bool changed = false;
            foreach (string url in removedUrls)
                if (!remaining.Contains(UrlKey(url))) changed |= d.Remove(UrlKey(url));
            if (changed) SaveConnectionFolders(d);
        }

        // ワークフローを置いてあるフォルダが、ワークフローのフォルダになっている接続先 (共通のものは空)
        public static List<ComfyPreset> WorkflowMachines(string path, ComfyPreset[] conns, Dictionary<string, string> folders)
        {
            var list = new List<ComfyPreset>();
            string p = FolderKey(path);
            if (p.Length == 0) return list;
            foreach (var c in conns)
            {
                string f;
                if (c.IsEmpty || !folders.TryGetValue(UrlKey(c.Value), out f)) continue;
                string fk = FolderKey(f);
                if (fk.Length > 0 && p.StartsWith(fk + "\\", StringComparison.Ordinal)) list.Add(c);
            }
            return list;
        }

        // ワークフローの台の名前 (接続先の名前。名前がなければフォルダ名。共通なら空)
        public static string MachineName(string path, ComfyPreset[] conns, Dictionary<string, string> folders)
        {
            var names = new List<string>();
            foreach (var c in WorkflowMachines(path, conns, folders))
            {
                string name = c.Name;
                if (name.Length == 0)
                {
                    try { name = Path.GetFileName(folders[UrlKey(c.Value)].TrimEnd('\\', '/')); } catch { name = ""; }
                }
                if (name.Length > 0 && !names.Contains(name)) names.Add(name);
            }
            return string.Join(" / ", names);
        }

        // 一覧に出す名前の後ろに付ける台の名前 (名前にもう入っていれば付けない)
        public static string MachineTag(string name, string machine)
        {
            string m = (machine ?? "").Trim('[', ']', '［', '］');
            if (m.Length == 0 || (name ?? "").ToLowerInvariant().Contains(m.ToLowerInvariant())) return "";
            return "  ［" + m + "］";
        }

        // ワークフローが別の台用なら、その台の名前 (合っている・共通なら空)
        public static string MachineConflict(string url, string path, ComfyPreset[] conns, Dictionary<string, string> folders)
        {
            var machines = WorkflowMachines(path, conns, folders);
            if (machines.Count == 0 || machines.Any(c => UrlKey(c.Value) == UrlKey(url))) return "";
            return MachineName(path, conns, folders);
        }

        // フォルダにある .json のうち、まだワークフローに登録していないもの (名前順)。読めなければ空
        public static List<string> UnregisteredWorkflows(string folder)
        {
            var registered = new HashSet<string>(Workflows.Where(p => !p.IsEmpty).Select(p => FolderKey(p.Value)));
            try
            {
                return Directory.GetFiles(folder, "*.json")
                                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                                .Where(f => !registered.Contains(FolderKey(f))).ToList();
            }
            catch { return new List<string>(); }
        }

        // 空いている枠に、ファイル名 (拡張子なし) を名前にして登録する。戻り値は登録した数
        public static int RegisterWorkflows(List<string> paths)
        {
            var list = Workflows;   // 読み直してから空いている枠に入れる
            int added = 0;
            foreach (string path in paths)
            {
                int slot = Array.FindIndex(list, p => p.IsEmpty && p.Name.Length == 0);
                if (slot < 0) break;
                list[slot].Name = Path.GetFileNameWithoutExtension(path).Trim();
                list[slot].Value = path;
                added++;
            }
            if (added > 0) Workflows = list;
            return added;
        }
    }

    // ------------------------------------------------------------------
    // まとめて編集 : 登録してあるものをまとめて削除・並べ替えする
    // (接続先・ワークフロー・プロンプト・セット・エンハンサー・読み取りプリセット。GenCommon の bulk_edit.py と同じ)
    // ------------------------------------------------------------------
    public class BulkEditResult
    {
        public List<int> Order = new List<int>();     // 残すものの元の番号 (0 始まり) を、新しい並びの順に
        public List<int> Deleted = new List<int>();   // 削除するものの元の番号
        public bool Pack;                             // 空きを詰めて 1 番から並べる

        // 元の番号 → 新しい番号 (Order の順)。空きを詰めないときは、残すものが元いた番号を小さい順に使う
        public List<KeyValuePair<int, int>> PlanList()
        {
            var slots = Pack ? Enumerable.Range(0, Order.Count).ToList() : Order.OrderBy(i => i).ToList();
            var list = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < Order.Count; i++) list.Add(new KeyValuePair<int, int>(Order[i], slots[i]));
            return list;
        }

        public Dictionary<int, int> Plan()
        {
            return PlanList().ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        public int Moved { get { return PlanList().Count(kv => kv.Key != kv.Value); } }

        public bool Changed { get { return Deleted.Count > 0 || Moved > 0; } }

        // 「接続先の 1 件を未登録にし、2 件の番号を変更しました」
        public string Summary(string what)
        {
            var parts = new List<string>();
            if (Deleted.Count > 0) parts.Add(Deleted.Count + " 件を未登録に");
            if (Moved > 0) parts.Add(Moved + " 件の番号を変更");
            return what + "の " + string.Join("し、", parts) + "しました";
        }
    }

    public static class BulkEdit
    {
        public const string ButtonText = "まとめて編集...";
        public const string ButtonTip = "登録してあるものをまとめて削除したり、並べ替えたり (名前順も) します";

        // 読み直した一覧 current に結果を当てはめる。mapping は実際の 元の番号 → 新しい番号。
        // listed は画面に出した元の番号。画面を開いている間にほかのアプリが空きに足したもの (listed にない) は
        // その場所に残し、行き先がそれと重なったときは次の空きに入れる
        public static T[] Apply<T>(T[] current, IList<int> listed, BulkEditResult result, Func<T> empty,
                                   Func<T, bool> isEmpty, out Dictionary<int, int> mapping)
        {
            var items = (T[])current.Clone();
            foreach (int i in listed)
                if (i >= 0 && i < items.Length) items[i] = empty();
            mapping = new Dictionary<int, int>();
            var taken = new HashSet<int>();
            for (int i = 0; i < items.Length; i++) if (!isEmpty(items[i])) taken.Add(i);
            foreach (var kv in result.PlanList())
            {
                int old = kv.Key;
                if (old < 0 || old >= current.Length) continue;
                int target = kv.Value;
                while (target < items.Length && taken.Contains(target)) target++;
                if (target >= items.Length)   // 入る場所がない (ふつうは起きない): 空いているところへ
                {
                    target = -1;
                    for (int i = 0; i < items.Length; i++) if (!taken.Contains(i)) { target = i; break; }
                    if (target < 0) continue;
                }
                items[target] = current[old];
                taken.Add(target);
                mapping[old] = target;
            }
            return items;
        }

        // index 番の行き先 (動かないものはそのまま)
        public static int Remap(int index, Dictionary<int, int> mapping)
        {
            int to;
            return mapping.TryGetValue(index, out to) ? to : index;
        }

        // 「3: 名前」の番号を外す
        public static string StripNumber(string label)
        {
            return System.Text.RegularExpressions.Regex.Replace(label ?? "", @"^\d+:\s*", "");
        }
    }

    // 名前順の比べ方 (大文字小文字を区別せず、数字は数として比べる: wf2 < wf10)
    public class NaturalComparer : IComparer<string>
    {
        private static readonly System.Text.RegularExpressions.Regex Parts = new System.Text.RegularExpressions.Regex(@"\d+|\D+");

        public int Compare(string a, string b)
        {
            var x = Parts.Matches((a ?? "").ToLowerInvariant());
            var y = Parts.Matches((b ?? "").ToLowerInvariant());
            for (int i = 0; i < x.Count && i < y.Count; i++)
            {
                string p = x[i].Value, q = y[i].Value;
                bool pd = char.IsDigit(p[0]), qd = char.IsDigit(q[0]);
                int c;
                if (pd && qd)
                {
                    string pt = p.TrimStart('0'), qt = q.TrimStart('0');
                    c = pt.Length != qt.Length ? pt.Length.CompareTo(qt.Length) : string.CompareOrdinal(pt, qt);
                }
                else if (pd != qd) c = pd ? -1 : 1;   // 数字を先に
                else c = string.Compare(p, q, StringComparison.CurrentCulture);
                if (c != 0) return c;
            }
            return x.Count.CompareTo(y.Count);
        }
    }

    public class BulkEditDialog : Window
    {
        private readonly string what;
        private readonly List<int> original = new List<int>();
        private readonly Dictionary<int, string> texts = new Dictionary<int, string>();
        private readonly Dictionary<int, ListBoxItem> rows = new Dictionary<int, ListBoxItem>();
        private readonly Dictionary<int, CheckBox> checks = new Dictionary<int, CheckBox>();
        private readonly Dictionary<int, TextBlock> labels = new Dictionary<int, TextBlock>();
        private readonly ListBox list = new ListBox { SelectionMode = SelectionMode.Extended };
        private readonly CheckBox pack = new CheckBox
        {
            Content = "空きを詰めて 1 番から並べる", Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "オフ: 登録してある番号の位置はそのままで、中身の順だけ変えます。\nオン: 未登録の番号をなくして、1 番から順に詰めます。"
        };
        private readonly TextBlock countText = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        private readonly Button ok = new Button { Content = "OK", Width = 90 };
        private ButtonBase up, down, top, bottom;
        private bool updating;

        public BulkEditResult Result { get; private set; }

        // items は登録してあるものの (元の番号 0 始まり, 一覧に出す文字)
        public BulkEditDialog(string what, List<KeyValuePair<int, string>> items, string note)
        {
            this.what = what;
            Title = what + "をまとめて編集";
            Width = 680;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(16) };
            var intro = new TextBlock
            {
                Text = "削除する" + what + "にチェックを付けます。並び順は、行を選んで「▲ 上へ」「▼ 下へ」か、名前順のボタンで変えます " +
                       "(Shift / Ctrl で複数を選んで一緒に動かせます)。OK を押すまでは何も変わりません。" +
                       (string.IsNullOrEmpty(note) ? "" : "\n" + note),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var allOn = new Button { Content = "すべてチェック", Padding = new Thickness(12, 2, 12, 2) };
            var allOff = new Button { Content = "すべて外す", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(allOn);
            left.Children.Add(allOff);
            DockPanel.SetDock(left, Dock.Left);
            buttons.Children.Add(left);
            var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(ok);
            right.Children.Add(cancel);
            buttons.Children.Add(right);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            DockPanel.SetDock(countText, Dock.Bottom);
            root.Children.Add(countText);
            DockPanel.SetDock(pack, Dock.Bottom);
            root.Children.Add(pack);

            var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Width = 110 };
            up = SideButton("▲ 上へ", "選んでいる行を 1 つ上へ (押し続けると続けて動きます)", true);
            down = SideButton("▼ 下へ", "選んでいる行を 1 つ下へ (押し続けると続けて動きます)", true);
            top = SideButton("先頭へ", "選んでいる行をいちばん上へ", false);
            bottom = SideButton("末尾へ", "選んでいる行をいちばん下へ", false);
            var sortUp = SideButton("名前順", "名前の順 (あいうえお・ABC 順。数字は数の大きさで比べます) に並べます", false);
            var sortDown = SideButton("名前の逆順", "名前の逆の順に並べます", false);
            var reset = SideButton("元の順", "開いたときの並びに戻します (チェックはそのまま)", false);
            side.Children.Add(up);
            side.Children.Add(down);
            side.Children.Add(top);
            side.Children.Add(bottom);
            sortUp.Margin = new Thickness(0, 16, 0, 6);
            side.Children.Add(sortUp);
            side.Children.Add(sortDown);
            side.Children.Add(reset);
            DockPanel.SetDock(side, Dock.Right);
            root.Children.Add(side);

            foreach (var kv in items)
            {
                int id = kv.Key;
                original.Add(id);
                texts[id] = BulkEdit.StripNumber(kv.Value);
                var label = new TextBlock();
                var check = new CheckBox { Content = label, VerticalContentAlignment = VerticalAlignment.Center };
                check.Checked += delegate { Refresh(); };
                check.Unchecked += delegate { Refresh(); };
                labels[id] = label;
                checks[id] = check;
                rows[id] = new ListBoxItem { Content = check, Tag = id, Padding = new Thickness(4, 3, 4, 3) };
                list.Items.Add(rows[id]);
            }
            root.Children.Add(list);
            Content = root;

            allOn.Click += delegate { CheckAll(true); };
            allOff.Click += delegate { CheckAll(false); };
            up.Click += delegate { Move(-1); };
            down.Click += delegate { Move(1); };
            top.Click += delegate { MoveEnd(true); };
            bottom.Click += delegate { MoveEnd(false); };
            sortUp.Click += delegate { SetOrder(Order().OrderBy(i => texts[i], new NaturalComparer()).ThenBy(i => i).ToList(), SelectedIds()); };
            sortDown.Click += delegate { SetOrder(Order().OrderByDescending(i => texts[i], new NaturalComparer()).ThenByDescending(i => i).ToList(), SelectedIds()); };
            reset.Click += delegate { SetOrder(new List<int>(original), SelectedIds()); };
            pack.Checked += delegate { Refresh(); };
            pack.Unchecked += delegate { Refresh(); };
            list.SelectionChanged += delegate { UpdateButtons(); };
            ok.Click += delegate { Accept(); };
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            Refresh();
        }

        private ButtonBase SideButton(string text, string tip, bool repeat)
        {
            ButtonBase b = repeat ? (ButtonBase)new RepeatButton { Delay = 400, Interval = 120 } : new Button();
            b.Content = text;
            b.ToolTip = tip;
            b.Padding = new Thickness(8, 2, 8, 2);
            b.Margin = new Thickness(0, 0, 0, 6);
            return b;
        }

        // ---- 今の状態 ----
        private List<int> Order()
        {
            return list.Items.Cast<ListBoxItem>().Select(r => (int)r.Tag).ToList();
        }

        private BulkEditResult Current()
        {
            var r = new BulkEditResult { Pack = pack.IsChecked == true };
            foreach (int id in Order())
                if (checks[id].IsChecked == true) r.Deleted.Add(id); else r.Order.Add(id);
            return r;
        }

        private List<int> SelectedRows()
        {
            return list.SelectedItems.Cast<ListBoxItem>().Select(r => list.Items.IndexOf(r)).OrderBy(i => i).ToList();
        }

        private HashSet<int> SelectedIds()
        {
            return new HashSet<int>(list.SelectedItems.Cast<ListBoxItem>().Select(r => (int)r.Tag));
        }

        // 行の文字 (新しい番号・元の番号・削除) と件数を出し直す
        private void Refresh()
        {
            if (updating) return;
            var result = Current();
            var plan = result.Plan();
            foreach (int id in Order())
            {
                var label = labels[id];
                int to;
                if (plan.TryGetValue(id, out to))
                {
                    label.Text = (to + 1) + ": " + texts[id] + (to == id ? "" : "   (元 " + (id + 1) + ")");
                    label.TextDecorations = null;
                    label.Foreground = SystemColors.ControlTextBrush;
                }
                else
                {
                    label.Text = "削除: " + texts[id] + "   (元 " + (id + 1) + ")";
                    label.TextDecorations = TextDecorations.Strikethrough;
                    label.Foreground = Brushes.Gray;
                }
            }
            countText.Text = "登録 " + list.Items.Count + " 件 / 削除 " + result.Deleted.Count + " 件 / 番号が変わる " + result.Moved + " 件";
            ok.IsEnabled = result.Changed;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var sel = SelectedRows();
            int n = list.Items.Count;
            bool canUp = sel.Count > 0 && !sel.SequenceEqual(Enumerable.Range(0, sel.Count));
            bool canDown = sel.Count > 0 && !sel.SequenceEqual(Enumerable.Range(n - sel.Count, sel.Count));
            up.IsEnabled = top.IsEnabled = canUp;
            down.IsEnabled = bottom.IsEnabled = canDown;
        }

        // ---- 並べ替え ----
        // 行を元の番号の並び order に並べ直す (チェックは付いたまま)。selected は選んでおく元の番号
        private void SetOrder(List<int> order, HashSet<int> selected)
        {
            updating = true;
            list.Items.Clear();
            foreach (int id in order) list.Items.Add(rows[id]);
            list.SelectedItems.Clear();
            ListBoxItem first = null;
            foreach (int id in order)
                if (selected.Contains(id))
                {
                    list.SelectedItems.Add(rows[id]);
                    if (first == null) first = rows[id];
                }
            if (first != null) list.ScrollIntoView(first);
            updating = false;
            Refresh();
        }

        // 選んでいる行を 1 つ動かす。端に着いた行は止まり、続く行はそこで詰まる (選んだ行どうしの順は崩さない)
        private void Move(int step)
        {
            var sel = SelectedRows();
            if (sel.Count == 0) return;
            var ids = SelectedIds();
            var order = Order();
            var placed = new HashSet<int>();
            IEnumerable<int> seq = step < 0 ? sel : Enumerable.Reverse(sel);
            foreach (int r in seq)
            {
                int t = r + step;
                if (t >= 0 && t < order.Count && !placed.Contains(t))
                {
                    int x = order[r]; order[r] = order[t]; order[t] = x;
                    placed.Add(t);
                }
                else placed.Add(r);
            }
            SetOrder(order, ids);
        }

        private void MoveEnd(bool toTop)
        {
            var ids = SelectedIds();
            if (ids.Count == 0) return;
            var order = Order();
            var picked = order.Where(ids.Contains).ToList();
            var rest = order.Where(i => !ids.Contains(i)).ToList();
            SetOrder(toTop ? picked.Concat(rest).ToList() : rest.Concat(picked).ToList(), ids);
        }

        private void CheckAll(bool on)
        {
            updating = true;
            foreach (var c in checks.Values) c.IsChecked = on;
            updating = false;
            Refresh();
        }

        private void Accept()
        {
            var result = Current();
            if (!result.Changed) return;
            var parts = new List<string>();
            if (result.Deleted.Count > 0) parts.Add(result.Deleted.Count + " 件を削除 (未登録に)");
            if (result.Moved > 0) parts.Add(result.Moved + " 件の番号を変更");
            if (MessageBox.Show(this, what + "の" + string.Join("・", parts) + "します。よろしいですか？", Title,
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            Result = result;
            DialogResult = true;
        }

        // 編集させる。登録が 1 件もなければ知らせて null。キャンセル・変更なしなら null
        public static BulkEditResult Ask(Window owner, string what, List<KeyValuePair<int, string>> items, string note)
        {
            if (items.Count == 0)
            {
                MessageBox.Show(owner, "登録してある" + what + "はありません。", what + "をまとめて編集", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            var dlg = new BulkEditDialog(what, items, note) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }
    }

    // ------------------------------------------------------------------
    // フォルダから登録 : 接続先ごとのワークフローのフォルダにある、まだ登録していない JSON を選んで登録する
    // ------------------------------------------------------------------
    public class RegisterWorkflowsDialog : Window
    {
        private readonly List<CheckBox> checks = new List<CheckBox>();
        private readonly List<string> paths = new List<string>();

        public List<string> Selected { get; private set; }

        // folders は (台の名前, フォルダ)
        public RegisterWorkflowsDialog(List<KeyValuePair<string, string>> folders)
        {
            Title = "フォルダからワークフローを登録";
            Width = 640;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            int free = ComfySettings.Workflows.Count(p => p.IsEmpty && p.Name.Length == 0);
            var root = new DockPanel { Margin = new Thickness(16) };
            var intro = new TextBlock
            {
                Text = "登録するワークフローにチェックを付けてください。名前はファイル名 (拡張子なし) になります " +
                       "(あとで「ワークフロー」のタブで変えられます)。空いている枠: " + free + " 件",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var allOn = new Button { Content = "すべて選ぶ", Padding = new Thickness(12, 2, 12, 2) };
            var allOff = new Button { Content = "すべて外す", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(allOn);
            left.Children.Add(allOff);
            DockPanel.SetDock(left, Dock.Left);
            buttons.Children.Add(left);
            var ok = new Button { Content = "登録", Width = 90, IsDefault = true };
            var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(ok);
            right.Children.Add(cancel);
            buttons.Children.Add(right);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var list = new StackPanel { Margin = new Thickness(4) };
            var json = ComfyVideo.NewSerializer();
            foreach (var kv in folders)
            {
                list.Children.Add(new TextBlock { Text = kv.Key + "   " + kv.Value, FontWeight = FontWeights.Bold, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 2) });
                var found = ComfySettings.UnregisteredWorkflows(kv.Value);
                if (found.Count == 0)
                    list.Children.Add(new TextBlock { Text = "(まだ登録していないものはありません。フォルダが見つからないときもこう出ます)", Foreground = Brushes.Gray, Margin = new Thickness(16, 0, 0, 2) });
                foreach (string path in found)
                {
                    string problem = null;
                    try { ComfyVideo.LoadWorkflow(json, path); }
                    catch (Exception ex) { problem = ex.Message.Split('\n')[0]; }
                    if (problem != null)
                    {
                        list.Children.Add(new TextBlock
                        {
                            Text = System.IO.Path.GetFileName(path) + "   (使えません: " + problem + ")",
                            Foreground = Brushes.Gray, Margin = new Thickness(16, 2, 0, 2), ToolTip = path
                        });
                        continue;
                    }
                    var check = new CheckBox { Content = System.IO.Path.GetFileName(path), ToolTip = path, Margin = new Thickness(16, 2, 0, 2) };
                    checks.Add(check);
                    paths.Add(path);
                    list.Children.Add(check);
                }
            }
            root.Children.Add(new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }
            });
            Content = root;

            allOn.IsEnabled = allOff.IsEnabled = checks.Count > 0;
            allOn.Click += delegate { foreach (var c in checks) c.IsChecked = true; };
            allOff.Click += delegate { foreach (var c in checks) c.IsChecked = false; };
            ok.Click += delegate
            {
                Selected = new List<string>();
                for (int i = 0; i < checks.Count; i++) if (checks[i].IsChecked == true) Selected.Add(paths[i]);
                DialogResult = true;
            };
        }
    }

    // ------------------------------------------------------------------
    // セットのカード : 中身の概要が見えるカードを横に並べ、横にスクロールする帯 (図の画面の最上部)
    // ------------------------------------------------------------------
    public class SetCardInfo
    {
        public string Title = "", Kind = "", Tip = "";
        public List<string> Lines = new List<string>();
        public bool Warn, Empty;
    }

    public class SetCardStrip : ScrollViewer
    {
        public const double CardWidth = 196;
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xb7, 0x85, 0x1f));
        private static readonly Brush EmptyLine = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0x9a));
        private readonly StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        private readonly Action<int> onSelect;

        public SetCardStrip(Action<int> onSelect)
        {
            this.onSelect = onSelect;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Content = row;
            // 縦のホイールでも横に送る
            PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                ScrollToHorizontalOffset(HorizontalOffset - e.Delta);
                e.Handled = true;
            };
        }

        public void Refresh(List<SetCardInfo> infos, int current)
        {
            row.Children.Clear();
            FrameworkElement selected = null;
            for (int i = 0; i < infos.Count; i++)
            {
                var card = Card(i, infos[i], i == current);
                row.Children.Add(card);
                if (i == current) selected = card;
            }
            if (selected != null)
                Dispatcher.BeginInvoke(new Action(() => selected.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private FrameworkElement Card(int index, SetCardInfo info, bool selected)
        {
            var grid = new Grid { Width = CardWidth, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = info.Tip, Background = Brushes.Transparent };
            var frame = new System.Windows.Shapes.Rectangle
            {
                RadiusX = 8, RadiusY = 8, Fill = Brushes.White,
                Stroke = selected ? SystemColors.HighlightBrush : info.Empty ? EmptyLine : Brushes.Silver,
                StrokeThickness = selected ? 2.5 : info.Empty ? 1.5 : 1
            };
            if (info.Empty && !selected) frame.StrokeDashArray = new DoubleCollection { 4, 3 };
            grid.Children.Add(frame);
            var stack = new StackPanel { Margin = new Thickness(9, 6, 9, 7) };

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var badge = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0) };
            var circle = new System.Windows.Shapes.Ellipse
            {
                Fill = selected ? SystemColors.HighlightBrush : info.Empty ? Brushes.Transparent : Brushes.Gray,
                Stroke = info.Empty && !selected ? EmptyLine : null, StrokeThickness = 1
            };
            if (info.Empty && !selected) circle.StrokeDashArray = new DoubleCollection { 2, 2 };
            badge.Children.Add(circle);
            badge.Children.Add(new TextBlock
            {
                Text = (index + 1).ToString(), FontWeight = FontWeights.Bold,
                Foreground = info.Empty && !selected ? Brushes.Gray : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            DockPanel.SetDock(badge, Dock.Left);
            head.Children.Add(badge);
            head.Children.Add(new TextBlock
            {
                Text = info.Title, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = info.Empty ? Brushes.Gray : Brushes.Black, VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(head);
            if (info.Kind.Length > 0)
                stack.Children.Add(new TextBlock { Text = info.Kind, Foreground = info.Warn ? WarnBrush : Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis });
            foreach (string line in info.Lines)
                stack.Children.Add(new TextBlock { Text = line, Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis });
            grid.Children.Add(stack);
            grid.MouseLeftButtonUp += delegate { onSelect(index); };
            return grid;
        }
    }
}
