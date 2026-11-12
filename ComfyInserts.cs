// 差し込み: フォルダのテキストを、ジョブ 1 件ごとに 1 つずつ順にプロンプトへ足す (generation-standard 6 章「差し込み」)。
// PromptWordCrafter・ImageMimicArt の gencommon/inserts.py と同じ動き・同じファイル。
// - 枠は 2 つ (初めはセリフ・アクション)。枠ごとに 名前・フォルダ・使うか・盛る前に入れるか を持つ。
// - フォルダの .txt を名前順 (自然順) に使い、最後まで使ったら最初に戻る。空のファイルは飛ばす。
// - どこまで使ったかは、フォルダごとに最後に使ったファイル名で覚える (%APPDATA%\GenCommon\insert_positions.txt。
//   この PC の 3 つのアプリで共通)。ファイルを足したり消したりしても、名前順で続きから使う。
// - ファイルを割り当てるのは「生成する」でジョブを登録したとき (続きから始めても変わらない)。
// ジョブ・記録に持たせる形 (GenRequest.Inserts): 使った枠ごとに {slot, name, folder, before, file, text}
// 生成の画面の欄の形 (前回の値・セット・入力中の内容): 枠ごとに {slot, name, folder, before, use}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JRate
{
    public class InsertException : Exception
    {
        public InsertException(string message) : base(message) { }
    }

    public class InsertSlot
    {
        public string Name = "", Folder = "";
        public bool Use, Before;   // Before: 盛る前に入れる (エンハンサーに渡す)。false = 盛ったあとに付ける

        public InsertSlot Copy() { return (InsertSlot)MemberwiseClone(); }

        public string Label(int index) { return Name.Trim().Length > 0 ? Name.Trim() : Inserts.DefaultName(index); }

        public Dictionary<string, object> ToData(int index)
        {
            return new Dictionary<string, object>
            {
                { "slot", index }, { "name", Name }, { "folder", Folder }, { "before", Before }, { "use", Use }
            };
        }
    }

    // 次に使うファイル (割り当てはしない)
    public class InsertPeek
    {
        public List<string> Files;   // フルパス (名前順)
        public int Index;            // 次に使うファイルの番号 (0 始まり)
        public string NextFile { get { return Files[Index]; } }
        public string Describe() { return "次: " + Path.GetFileName(NextFile) + " (" + (Index + 1) + " / " + Files.Count + ")"; }
    }

    public static class Inserts
    {
        public const int SlotCount = 2;
        public const string SlotsFile = "insert_slots.json";          // 前回の枠の設定 (アプリごと)
        public const string PositionsFile = "insert_positions.txt";   // 1 行に「フォルダ Tab ファイル名」
        private static readonly string[] DefaultNames = { "セリフ", "アクション" };
        private static readonly object Gate = new object();

        public static string DefaultName(int index)
        {
            return index >= 0 && index < DefaultNames.Length ? DefaultNames[index] : "差し込み " + (index + 1);
        }

        public static List<InsertSlot> DefaultSlots()
        {
            var list = new List<InsertSlot>();
            for (int i = 0; i < SlotCount; i++) list.Add(new InsertSlot { Name = DefaultName(i) });
            return list;
        }

        // ---------------- 欄の形 ⇔ InsertSlot ----------------
        // 欄の形 ("use" あり) は、その枠の中身をすべて入れ替える。
        // ジョブ・記録の形 ("use" なし) は、出てくる枠を使う・出てこない枠を使わないにする (名前・フォルダは残す)。
        // data が null なら slots のまま
        public static List<InsertSlot> Apply(List<InsertSlot> slots, IEnumerable<Dictionary<string, object>> data)
        {
            var result = slots.Select(s => s.Copy()).ToList();
            if (data == null) return result;
            var items = data.Where(d => d != null).ToList();
            bool fromJob = !items.Any(d => d.ContainsKey("use"));
            if (fromJob) foreach (var s in result) s.Use = false;
            foreach (var d in items)
            {
                int i;
                if (!int.TryParse(ComfySettings.Str(d, "slot"), out i) || i < 0 || i >= result.Count) continue;
                var s = result[i];
                string name = ComfySettings.Str(d, "name");
                if (name.Length > 0) s.Name = name;
                s.Folder = ComfySettings.Str(d, "folder");
                s.Before = Bool(d, "before");
                s.Use = fromJob || Bool(d, "use");
            }
            return result;
        }

        public static List<Dictionary<string, object>> ToData(List<InsertSlot> slots)
        {
            return slots.Select((s, i) => s.ToData(i)).ToList();
        }

        // JSON から読んだ並び (object[]) を、辞書の並びに
        public static List<Dictionary<string, object>> FromJson(object value)
        {
            var arr = value as object[];
            if (arr == null)
            {
                var list = value as System.Collections.ArrayList;
                if (list == null) return null;
                arr = list.ToArray();
            }
            return arr.OfType<Dictionary<string, object>>().ToList();
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o is bool && (bool)o;
        }

        public static List<InsertSlot> LoadSlots()
        {
            List<Dictionary<string, object>> data = null;
            try { data = FromJson(ComfyVideo.NewSerializer().DeserializeObject(ComfySettings.Load(SlotsFile) ?? "[]")); }
            catch { }
            return Apply(DefaultSlots(), data);
        }

        public static void SaveSlots(List<InsertSlot> slots)
        {
            ComfySettings.Save(SlotsFile, ComfyVideo.NewSerializer().Serialize(ToData(slots)));
        }

        // ---------------- フォルダの中身 ----------------
        // 名前順 (大文字小文字を区別せず、半角の数字は数として比べる: 2.txt < 10.txt)。
        // gencommon/inserts.py の natural_key と同じ並び (どこまで使ったかを共有するので)
        public static int NaturalCompare(string a, string b)
        {
            var x = Regex.Split(a.ToLowerInvariant(), "([0-9]+)").Where(p => p.Length > 0).ToList();
            var y = Regex.Split(b.ToLowerInvariant(), "([0-9]+)").Where(p => p.Length > 0).ToList();
            for (int i = 0; i < x.Count && i < y.Count; i++)
            {
                bool dx = char.IsDigit(x[i][0]) && x[i][0] < 128, dy = char.IsDigit(y[i][0]) && y[i][0] < 128;
                if (dx != dy) return dx ? -1 : 1;   // 数字のほうが先
                int c;
                if (dx)
                {
                    string nx = x[i].TrimStart('0'), ny = y[i].TrimStart('0');
                    c = nx.Length != ny.Length ? nx.Length.CompareTo(ny.Length) : string.CompareOrdinal(nx, ny);
                }
                else c = string.CompareOrdinal(x[i], y[i]);
                if (c != 0) return c;
            }
            int n = x.Count.CompareTo(y.Count);
            return n != 0 ? n : string.CompareOrdinal(a, b);
        }

        // フォルダの .txt を名前順に (サブフォルダは見ない)。使えなければ InsertException
        public static List<string> ListFiles(string folder)
        {
            folder = (folder ?? "").Trim();
            if (folder.Length == 0) throw new InsertException("フォルダが決まっていません");
            if (!Directory.Exists(folder)) throw new InsertException("フォルダが見つかりません (" + folder + ")");
            List<string> files;
            try
            {
                files = Directory.GetFiles(folder)
                                 .Where(f => string.Equals(Path.GetExtension(f), ".txt", StringComparison.OrdinalIgnoreCase))
                                 .ToList();
            }
            catch (Exception ex) { throw new InsertException("フォルダを読めません (" + ex.Message + ")"); }
            if (files.Count == 0) throw new InsertException("フォルダにテキスト (.txt) がありません (" + folder + ")");
            files.Sort((p, q) => NaturalCompare(Path.GetFileName(p), Path.GetFileName(q)));
            return files;
        }

        // 中身 (UTF-8 / Shift_JIS / UTF-16 を見分ける。前後の空白は除く)
        public static string ReadFile(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch { return ""; }
            string text = null;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                text = Encoding.UTF8.GetString(data, 3, data.Length - 3);
            else
            {
                foreach (var enc in new Encoding[]
                {
                    new UTF8Encoding(false, true),
                    Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                    new UnicodeEncoding(false, true, true),
                })
                {
                    try { text = enc.GetString(data); break; }
                    catch (DecoderFallbackException) { }
                }
            }
            return (text ?? Encoding.GetEncoding(28591).GetString(data)).Replace("\r\n", "\n").Trim();
        }

        private static string FolderKey(string folder)
        {
            try { return Path.GetFullPath(folder.Trim()).ToLowerInvariant(); }
            catch { return folder.Trim().ToLowerInvariant(); }
        }

        // ---------------- どこまで使ったか (%APPDATA%\GenCommon。この PC の 3 つのアプリで共通) ----------------
        private static string PositionsPath { get { return Path.Combine(ComfySettings.LocalDir, PositionsFile); } }

        public static Dictionary<string, string> LoadPositions()
        {
            var result = new Dictionary<string, string>();
            string text;
            try { text = File.Exists(PositionsPath) ? File.ReadAllText(PositionsPath) : ""; }
            catch { text = ""; }
            foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string key = line.Substring(0, tab).Trim(), name = line.Substring(tab + 1).Trim();
                if (key.Length > 0 && name.Length > 0) result[key] = name;
            }
            return result;
        }

        public static void SavePositions(Dictionary<string, string> positions)
        {
            try
            {
                string file = PositionsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string temp = file + "." + Environment.MachineName + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(temp, string.Concat(positions.Select(kv => kv.Key + "\t" + kv.Value + "\r\n")));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            catch { }
        }

        public static string LastUsed(string folder)
        {
            string name;
            return LoadPositions().TryGetValue(FolderKey(folder), out name) ? name : "";
        }

        // そのフォルダを最初から使い直す
        public static void Reset(string folder)
        {
            lock (Gate)
            {
                var positions = LoadPositions();
                if (positions.Remove(FolderKey(folder))) SavePositions(positions);
            }
        }

        // last の次 (名前順)。last がなければ・最後なら最初
        private static int NextIndex(List<string> files, string last)
        {
            if (string.IsNullOrEmpty(last)) return 0;
            for (int i = 0; i < files.Count; i++)
                if (NaturalCompare(Path.GetFileName(files[i]), last) > 0) return i;
            return 0;
        }

        public static InsertPeek Peek(string folder)
        {
            var files = ListFiles(folder);
            return new InsertPeek { Files = files, Index = NextIndex(files, LastUsed(folder)) };
        }

        // ---------------- ジョブへの割り当て ----------------
        // ジョブ 1 件ごとに、使う枠の次のファイルを 1 つずつ渡す。全部割り当ててから Commit で位置を書く。
        // 同じフォルダを 2 つの枠で使ったときは、同じ並びを続けて使う (位置はフォルダごと)
        public class Picker
        {
            private readonly List<Tuple<int, InsertSlot>> slots;
            private readonly Dictionary<string, List<string>> files = new Dictionary<string, List<string>>();
            private readonly Dictionary<string, string> texts = new Dictionary<string, string>();
            private readonly Dictionary<string, string> positions;
            private readonly HashSet<string> touched = new HashSet<string>();   // 割り当てたフォルダ

            public Picker(List<InsertSlot> list)
            {
                slots = (list ?? new List<InsertSlot>()).Select((s, i) => Tuple.Create(i, s)).Where(t => t.Item2.Use).ToList();
                lock (Gate) positions = LoadPositions();
                foreach (var t in slots)
                {
                    string key = FolderKey(t.Item2.Folder);
                    if (files.ContainsKey(key)) continue;
                    try { files[key] = ListFiles(t.Item2.Folder); }
                    catch (InsertException ex) { throw new InsertException("差し込み「" + t.Item2.Label(t.Item1) + "」: " + ex.Message); }
                }
            }

            // 次のジョブの差し込み (使う枠がなければ null)。中身のあるファイルがなければ InsertException
            public List<Dictionary<string, object>> Next()
            {
                if (slots.Count == 0) return null;
                var result = new List<Dictionary<string, object>>();
                foreach (var t in slots)
                {
                    var s = t.Item2;
                    string key = FolderKey(s.Folder);
                    var list = files[key];
                    string last;
                    int index = NextIndex(list, positions.TryGetValue(key, out last) ? last : "");
                    string path = null, text = "";
                    for (int k = 0; k < list.Count; k++)
                    {
                        path = list[index];
                        positions[key] = Path.GetFileName(path);
                        touched.Add(key);
                        if (!texts.TryGetValue(path, out text)) texts[path] = text = ReadFile(path);
                        if (text.Length > 0) break;
                        index = (index + 1) % list.Count;
                    }
                    if (text.Length == 0)
                        throw new InsertException("差し込み「" + s.Label(t.Item1) + "」: フォルダのテキストがすべて空です (" + s.Folder + ")");
                    result.Add(new Dictionary<string, object>
                    {
                        { "slot", t.Item1 }, { "name", s.Label(t.Item1) }, { "folder", s.Folder }, { "before", s.Before },
                        { "file", Path.GetFileName(path) }, { "text", text }
                    });
                }
                return result;
            }

            public void Commit()
            {
                if (touched.Count == 0) return;
                lock (Gate)
                {
                    // 使ったフォルダの分だけ書く (ほかのアプリが書いたものは、読み直して残す)
                    var now = LoadPositions();
                    foreach (string key in touched) now[key] = positions[key];
                    SavePositions(now);
                }
            }
        }

        // ---------------- 組み立て (GenRequest から使う) ----------------
        public static List<string> Texts(List<Dictionary<string, object>> items, bool before)
        {
            if (items == null) return new List<string>();
            return items.Where(d => d != null && Bool(d, "before") == before).Select(d => ComfySettings.Str(d, "text")).ToList();
        }

        // 空でないものを改行でつなぐ
        public static string Join(params string[] parts)
        {
            return string.Join("\n", parts.Where(p => p != null && p.Trim().Length > 0).Select(p => p.Trim()));
        }

        public static string Summary(List<Dictionary<string, object>> items)
        {
            if (items == null) return "";
            return string.Join("、", items.Where(d => d != null).Select(d => ComfySettings.Str(d, "name") + ": " + ComfySettings.Str(d, "file")));
        }
    }

    // ------------------------------------------------------------------
    // 差し込みの 1 枠の欄: 使う / 名前 / フォルダ / 選択... と、盛る前に入れる / 次のファイル / 最初に戻す
    // ------------------------------------------------------------------
    public class InsertRow : StackPanel
    {
        public readonly int Index;
        public readonly CheckBox Use, Before;
        public readonly TextBox NameBox, FolderBox;
        private readonly TextBlock status;
        private readonly Button reset;
        private bool setting;
        public string Problem { get; private set; }
        public event Action Changed;

        public InsertRow(int index)
        {
            Index = index;
            Margin = new Thickness(0, 0, 0, 4);
            var top = new DockPanel();
            Use = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                                 ToolTip = "オンにすると、ジョブ 1 件ごとにこのフォルダのテキストを 1 つずつ順に足します" };
            DockPanel.SetDock(Use, Dock.Left);
            top.Children.Add(Use);
            NameBox = new TextBox { Width = 90, Padding = new Thickness(2, 2, 2, 2), Margin = new Thickness(0, 0, 6, 0),
                                    ToolTip = "枠の名前 (記録・確認の画面に出します。空なら「" + Inserts.DefaultName(index) + "」)" };
            DockPanel.SetDock(NameBox, Dock.Left);
            top.Children.Add(NameBox);
            var browse = new Button { Content = "選択...", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(browse, Dock.Right);
            top.Children.Add(browse);
            FolderBox = new TextBox { Padding = new Thickness(2, 2, 2, 2), ToolTip = "テキスト (.txt) を入れたフォルダ" };
            top.Children.Add(FolderBox);
            Children.Add(top);

            var bottom = new DockPanel { Margin = new Thickness(22, 2, 0, 0) };
            Before = new CheckBox
            {
                Content = "盛る前に入れる", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "オン: プロンプトのあとに入れてから、エンハンサーで一緒に盛ります (言い換えられることがあります)。\n" +
                          "オフ: 盛ったあとに、そのまま付けます (一字一句そのまま送ります)。\n" +
                          "エンハンサーが「なし」のときは、盛る前に入れるものを先に並べます。"
            };
            DockPanel.SetDock(Before, Dock.Left);
            bottom.Children.Add(Before);
            reset = new Button { Content = "最初に戻す", Padding = new Thickness(8, 0, 8, 1), Margin = new Thickness(6, 0, 0, 0),
                                 ToolTip = "このフォルダを、次は名前順の最初のファイルから使います (すぐに覚えます)" };
            DockPanel.SetDock(reset, Dock.Right);
            bottom.Children.Add(reset);
            status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            bottom.Children.Add(status);
            Children.Add(bottom);

            RoutedEventHandler refresh = delegate { if (!setting) Refresh(); };
            Use.Checked += refresh;
            Use.Unchecked += refresh;
            RoutedEventHandler changed = delegate { if (!setting && Changed != null) Changed(); };
            Before.Checked += changed;
            Before.Unchecked += changed;
            NameBox.TextChanged += delegate { if (!setting && Changed != null) Changed(); };
            FolderBox.LostFocus += refresh;
            FolderBox.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) Refresh(); };
            browse.Click += delegate
            {
                var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "差し込みのフォルダ", SelectedPath = FolderBox.Text.Trim().Trim('"') };
                if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                setting = true;
                FolderBox.Text = fbd.SelectedPath;
                Use.IsChecked = true;
                setting = false;
                Refresh();
            };
            reset.Click += delegate
            {
                Inserts.Reset(FolderBox.Text);
                // 同じフォルダを使っているほかの枠も出し直す
                var parent = Parent as Panel;
                if (parent != null) foreach (var row in parent.Children.OfType<InsertRow>()) row.Refresh();
                else Refresh();
            };
        }

        public void SetSlot(InsertSlot slot)
        {
            setting = true;
            Use.IsChecked = slot.Use;
            Before.IsChecked = slot.Before;
            NameBox.Text = slot.Name;
            FolderBox.Text = slot.Folder;
            setting = false;
            Refresh();
        }

        public InsertSlot Slot()
        {
            return new InsertSlot { Name = NameBox.Text.Trim(), Folder = FolderBox.Text.Trim().Trim('"'),
                                    Use = Use.IsChecked == true, Before = Before.IsChecked == true };
        }

        public InsertPeek PeekNext()
        {
            try { return Inserts.Peek(Slot().Folder); }
            catch (InsertException) { return null; }
        }

        // 次に使うファイルを出す。使う枠でフォルダを使えなければ、その理由 (Problem)
        public void Refresh()
        {
            Problem = null;
            string folder = Slot().Folder, text = "";
            bool use = Use.IsChecked == true, bad = false;
            if (folder.Length > 0)
            {
                try { text = Inserts.Peek(folder).Describe(); }
                catch (InsertException ex) { text = ex.Message; bad = use; }
            }
            else if (use) { text = "フォルダを選んでください"; bad = true; }
            if (bad) Problem = text;
            status.Text = (!use && text.Length > 0 ? "(使わない) " : "") + text;
            status.ToolTip = text.Length > 0 ? text : null;
            status.Foreground = bad ? Brushes.Firebrick : Brushes.DimGray;
            reset.IsEnabled = folder.Length > 0 && Inserts.LastUsed(folder).Length > 0;
            if (Changed != null) Changed();
        }
    }

    // ------------------------------------------------------------------
    // 生成の画面の差し込みの欄 (いつもの画面と図の画面で同じものを使う)
    // ------------------------------------------------------------------
    public partial class ComfyPromptDialog
    {
        private GroupBox insertGroup;
        private readonly List<InsertRow> insertRows = new List<InsertRow>();

        // 「生成する」で決まった枠 (呼び出し側がジョブに割り当てる)。プロンプトを入れる場所がなければ空
        public List<InsertSlot> InsertSlots { get; private set; }

        private GroupBox BuildInserts()
        {
            var stack = new StackPanel();
            var slots = Inserts.LoadSlots();
            for (int i = 0; i < slots.Count; i++)
            {
                var row = new InsertRow(i);
                row.SetSlot(slots[i]);
                row.Changed += delegate { UpdateOkButton(); RefreshDiagram(); };
                stack.Children.Add(row);
                insertRows.Add(row);
            }
            insertGroup = new GroupBox
            {
                Header = "差し込み (フォルダのテキストを、1 件ごとに順に足す。最後まで使ったら最初に戻る)",
                Content = stack, Padding = new Thickness(6, 4, 6, 2), Margin = new Thickness(0, 0, 0, 12)
            };
            return insertGroup;
        }

        private List<InsertSlot> CurrentSlots() { return insertRows.Select(r => r.Slot()).ToList(); }

        private void ApplyInserts(IEnumerable<Dictionary<string, object>> data)
        {
            var slots = Inserts.Apply(CurrentSlots(), data);
            for (int i = 0; i < insertRows.Count; i++) insertRows[i].SetSlot(slots[i]);
        }

        // 使う枠でフォルダを使えない理由 (プロンプトを入れる場所がないワークフローでは見ない)
        private string InsertProblem()
        {
            if (promptBox == null || !promptBox.IsEnabled) return null;
            var bad = insertRows.FirstOrDefault(r => r.Problem != null);
            return bad == null ? null : "差し込み「" + bad.Slot().Label(bad.Index) + "」: " + bad.Problem;
        }

        // 「生成する」を押せるか: ワークフローを読めていて、使う差し込みのフォルダを使える (「図で設定」は保存できればよい)
        private void UpdateOkButton()
        {
            if (okButton == null || workflowError == null) return;
            string problem = settingsOnly ? null : InsertProblem();
            okButton.IsEnabled = (wfData != null || settingsOnly) && !reading && problem == null;
            if (insertError != null)
            {
                insertError.Text = problem ?? "";
                insertError.Visibility = problem != null ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private TextBlock insertError;
    }
}
