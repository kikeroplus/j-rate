// 生成の設定の整理 (generation-standard 2・3・4 章。PromptWordCrafter・ImageMimicArt の GenCommon と同じ決まり)
//   - 接続先ごとのワークフローのフォルダ (comfy_connection_folders.txt、共有) で、ワークフローがどの台用かを見分ける
//   - まとめて削除 (選んだものを未登録にする。ほかの番号は動かさない)
//   - フォルダから登録 (台のフォルダにある、まだ登録していない JSON をまとめて登録する)
//   - 図の画面のセットのカード (中身の概要が見えるカードを横に並べて選ぶ)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    // まとめて削除 : 登録してあるものから選んで未登録にする (接続先・ワークフロー・プロンプト・セット・エンハンサー・読み取りプリセット)
    // ------------------------------------------------------------------
    public class BulkDeleteDialog : Window
    {
        private readonly List<CheckBox> checks = new List<CheckBox>();
        private readonly List<int> indexes = new List<int>();
        private readonly TextBlock countText;
        private readonly Button ok;
        private readonly string what;

        public List<int> Selected { get; private set; }

        // items は登録済みのものの (番号 0 始まり, 一覧に出す文字)
        public BulkDeleteDialog(string what, List<KeyValuePair<int, string>> items, string note)
        {
            this.what = what;
            Title = what + "をまとめて削除";
            Width = 600;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(16) };
            var intro = new TextBlock
            {
                Text = "未登録にする" + what + "にチェックを付けてください。消した番号は空きになり、ほかの番号は動きません " +
                       "(並び順は「▲ 上へ」「▼ 下へ」で直します)。" + (string.IsNullOrEmpty(note) ? "" : "\n" + note),
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
            ok = new Button { Content = "削除", Width = 90 };
            var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(ok);
            right.Children.Add(cancel);
            buttons.Children.Add(right);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            countText = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(countText, Dock.Bottom);
            root.Children.Add(countText);

            var list = new StackPanel { Margin = new Thickness(4) };
            foreach (var kv in items)
            {
                var check = new CheckBox { Content = kv.Value, Margin = new Thickness(0, 2, 0, 2) };
                check.Checked += delegate { Update(); };
                check.Unchecked += delegate { Update(); };
                checks.Add(check);
                indexes.Add(kv.Key);
                list.Children.Add(check);
            }
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            root.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = scroll });
            Content = root;

            allOn.Click += delegate { foreach (var c in checks) c.IsChecked = true; };
            allOff.Click += delegate { foreach (var c in checks) c.IsChecked = false; };
            ok.Click += delegate
            {
                var chosen = Chosen();
                if (MessageBox.Show(this, what + " " + chosen.Count + " 件を未登録にします。よろしいですか？", Title,
                                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;
                Selected = chosen;
                DialogResult = true;
            };
            Update();
        }

        private List<int> Chosen()
        {
            var list = new List<int>();
            for (int i = 0; i < checks.Count; i++) if (checks[i].IsChecked == true) list.Add(indexes[i]);
            return list;
        }

        private void Update()
        {
            int n = Chosen().Count;
            countText.Text = n + " 件を選んでいます (登録 " + checks.Count + " 件)";
            ok.IsEnabled = n > 0;
        }

        // 選ばせる。登録が 1 件もなければ知らせて null。キャンセルなら null
        public static List<int> Ask(Window owner, string what, List<KeyValuePair<int, string>> items, string note)
        {
            if (items.Count == 0)
            {
                MessageBox.Show(owner, "登録してある" + what + "はありません。", what + "をまとめて削除", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            var dlg = new BulkDeleteDialog(what, items, note) { Owner = owner };
            return dlg.ShowDialog() == true && dlg.Selected != null && dlg.Selected.Count > 0 ? dlg.Selected : null;
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
