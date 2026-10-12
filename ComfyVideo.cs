// J-Rate : ComfyUI に画像を渡して動画や画像を生成する (右クリック「生成する」)
// .NET Framework 4.x 標準の csc.exe (C# 5) でビルドできるように書いている。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace JRate
{
    // ------------------------------------------------------------------
    // プリセット (名前と値)。値は、接続先なら URL、ワークフローなら JSON のパス
    // ------------------------------------------------------------------
    public class ComfyPreset
    {
        public string Name = "";
        public string Value = "";

        // 値が入っていないものは未登録
        public bool IsEmpty { get { return Value.Length == 0; } }

        // 一覧に出す文字列 (index は 0 始まり)
        public string Label(int index)
        {
            return Label(index, Value);
        }

        // ワークフロー用 : パスの代わりにファイル名を出す
        public string FileLabel(int index)
        {
            string file = Value;
            try { file = Path.GetFileName(Value); } catch { }
            return Label(index, file);
        }

        // プロンプト用 : 長い本文の代わりに先頭だけを 1 行で出す
        public string TextLabel(int index)
        {
            string s = string.Join(" ", Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            int max = Name.Length > 0 ? 40 : 60;
            if (s.Length > max) s = s.Substring(0, max) + "...";
            return Label(index, s);
        }

        // 未登録に戻す
        public void Clear()
        {
            Name = "";
            Value = "";
        }

        private string Label(int index, string detail)
        {
            string s = Name.Length > 0 ? (detail.Length > 0 ? Name + "  (" + detail + ")" : Name)
                     : detail.Length > 0 ? detail : "(未登録)";
            return (index + 1) + ": " + s;
        }
    }

    // ------------------------------------------------------------------
    // セットのプリセット : 接続先・ワークフロー・プロンプト・秒数をまとめて呼び出す。
    // 接続先とワークフローは番号ではなく URL とパスで覚える (プリセットの番号を入れ替えても崩れない)
    // ------------------------------------------------------------------
    public class ComfySet
    {
        public string Name = "", Url = "", Workflow = "", Prompt = "", Seconds = "";
        public int PromptPreset = -1;   // 保存したときに選んでいたプロンプトのプリセットの番号 (0 始まり。なければ -1)

        public bool IsEmpty { get { return Url.Length == 0 && Workflow.Length == 0; } }

        // 一覧に出す文字列 (index は 0 始まり)
        public string Label(int index)
        {
            return (index + 1) + ": " + (IsEmpty ? "(未登録)" : Name.Length > 0 ? Name : "(名前なし)");
        }
    }

    // ------------------------------------------------------------------
    // 1 回の生成に使う設定。名前は記録と表示のためのもの
    // ------------------------------------------------------------------
    public class GenRequest
    {
        public string ConnectionName = "", Url = "", WorkflowName = "", WorkflowPath = "";
        public string Prompt;      // null = ワークフローに書かれているまま
        public double? Seconds;    // 同上
    }

    // 生成したファイル 1 つぶんの記録
    public class GenRecord
    {
        public string Output = "", Source = "", Time = "";
        public GenRequest Request = new GenRequest();
    }

    // ------------------------------------------------------------------
    // どの設定で生成したかの記録 (%APPDATA%\J-Rate\comfy_history.txt、1 行に 1 件の JSON)
    // ------------------------------------------------------------------
    public static class ComfyHistory
    {
        private static readonly object Gate = new object();

        public static void Add(string output, string source, GenRequest req)
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["output"] = output;
                d["source"] = source;
                d["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                d["connection"] = req.ConnectionName;
                d["url"] = req.Url;
                d["workflow"] = req.WorkflowName;
                d["workflowPath"] = req.WorkflowPath;
                d["prompt"] = req.Prompt;
                d["seconds"] = req.Seconds;
                string line = ComfyVideo.NewSerializer().Serialize(d) + "\r\n";
                string file = ComfySettings.FilePath("comfy_history.txt");
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
            }
            catch { }
        }

        // そのファイルを生成したときの記録。同じパスの記録を探し、なければ同じファイル名の
        // 記録 (生成後に別のフォルダへコピー・移動したもの) を使う。どちらも新しいものが優先
        public static GenRecord Find(string outputPath)
        {
            try
            {
                string file = ComfySettings.FilePath("comfy_history.txt");
                string[] lines;
                lock (Gate)
                {
                    if (!File.Exists(file)) return null;
                    lines = File.ReadAllLines(file, Encoding.UTF8);
                }
                string name = Path.GetFileName(outputPath);
                var json = ComfyVideo.NewSerializer();
                Dictionary<string, object> byName = null;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    // JSON の中ではパスの \ が \\ になるので、ファイル名で粗く絞ってから読む
                    if (lines[i].IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Dictionary<string, object> d;
                    try { d = json.DeserializeObject(lines[i]) as Dictionary<string, object>; }
                    catch { continue; }
                    if (d == null) continue;
                    string output = ComfySettings.Str(d, "output");
                    if (string.Equals(output, outputPath, StringComparison.OrdinalIgnoreCase)) return ToRecord(d);
                    if (byName == null && string.Equals(Path.GetFileName(output), name, StringComparison.OrdinalIgnoreCase))
                        byName = d;
                }
                return byName != null ? ToRecord(byName) : null;
            }
            catch { return null; }
        }

        private static GenRecord ToRecord(Dictionary<string, object> d)
        {
            var r = new GenRecord();
            r.Output = ComfySettings.Str(d, "output");
            r.Source = ComfySettings.Str(d, "source");
            r.Time = ComfySettings.Str(d, "time");
            r.Request.ConnectionName = ComfySettings.Str(d, "connection");
            r.Request.Url = ComfySettings.Str(d, "url");
            r.Request.WorkflowName = ComfySettings.Str(d, "workflow");
            r.Request.WorkflowPath = ComfySettings.Str(d, "workflowPath");
            object o;
            if (d.TryGetValue("prompt", out o) && o != null) r.Request.Prompt = o.ToString();
            if (d.TryGetValue("seconds", out o) && o != null) r.Request.Seconds = Convert.ToDouble(o);
            return r;
        }
    }

    // ------------------------------------------------------------------
    // 設定 (%APPDATA%\J-Rate\comfy_*.txt)
    // ------------------------------------------------------------------
    public static class ComfySettings
    {
        public const string DefaultUrl = "http://127.0.0.1:8188";
        public const int PresetCount = 10;
        public const int WorkflowCount = 100;   // ワークフローだけは多く登録できる

        // 接続先のプリセット (comfy_presets.txt)。いつも PresetCount 件 (未登録のものは空)
        public static ComfyPreset[] Presets
        {
            get { return LoadPresets("comfy_presets.txt", "comfy_url.txt", DefaultUrl, PresetCount); }
            set { SavePresets("comfy_presets.txt", value, PresetCount); }
        }

        // 使う接続先の番号 (0 始まり)
        public static int ActivePreset
        {
            get { return LoadIndex("comfy_preset.txt", PresetCount); }
            set { Save("comfy_preset.txt", (value + 1).ToString()); }
        }

        // ワークフローのプリセット (comfy_workflows.txt)。いつも WorkflowCount 件 (未登録のものは空)。
        // 値は ComfyUI の「Export (API)」で保存したワークフロー JSON のパス
        public static ComfyPreset[] Workflows
        {
            get { return LoadPresets("comfy_workflows.txt", "comfy_workflow.txt", "", WorkflowCount); }
            set { SavePresets("comfy_workflows.txt", value, WorkflowCount); }
        }

        // 使うワークフローの番号 (0 始まり)
        public static int ActiveWorkflow
        {
            get { return LoadIndex("comfy_workflow_preset.txt", WorkflowCount); }
            set { Save("comfy_workflow_preset.txt", (value + 1).ToString()); }
        }

        // 使うワークフローのパス (未登録なら空文字)
        public static string WorkflowPath
        {
            get { return Workflows[ActiveWorkflow].Value; }
        }

        // ファイルは 1 行に「番号 <Tab> 名前 <Tab> 値」。
        // まだなければ、プリセットができる前の設定 (legacyFile) を 1 番に引き継ぐ
        private static ComfyPreset[] LoadPresets(string file, string legacyFile, string legacyDefault, int count)
        {
            var list = new ComfyPreset[count];
            for (int i = 0; i < count; i++) list[i] = new ComfyPreset();
            string s = Load(file);
            if (s == null)
            {
                string old = Load(legacyFile);
                list[0].Value = string.IsNullOrWhiteSpace(old) ? legacyDefault : old.Trim().TrimEnd('/');
                return list;
            }
            foreach (string line in s.Split('\n'))
            {
                string[] p = line.TrimEnd('\r').Split('\t');
                int n;
                if (p.Length < 3 || !int.TryParse(p[0], out n) || n < 1 || n > count) continue;
                list[n - 1].Name = p[1].Trim();
                list[n - 1].Value = p[2].Trim().TrimEnd('/');
            }
            return list;
        }

        private static void SavePresets(string file, ComfyPreset[] list, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && i < list.Length; i++)
                sb.Append(i + 1).Append('\t').Append(OneLine(list[i].Name)).Append('\t')
                  .Append(OneLine(list[i].Value).TrimEnd('/')).Append("\r\n");
            Save(file, sb.ToString());
        }

        private static int LoadIndex(string file, int count)
        {
            int n;
            return int.TryParse(Load(file), out n) && n >= 1 && n <= count ? n - 1 : 0;
        }

        private static string OneLine(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // 前回のプロンプトと秒数 (「生成する」のダイアログの初期値)
        public static string Prompt
        {
            get { return Load("comfy_prompt.txt") ?? ""; }
            set { Save("comfy_prompt.txt", (value ?? "").Trim()); }
        }

        // プロンプトのプリセット。値はプロンプトの本文 (改行があるので 1 件ずつ
        // comfy_prompt_1.txt ～ comfy_prompt_10.txt)、名前は comfy_prompt_names.txt に「番号 <Tab> 名前」
        public static ComfyPreset[] PromptPresets
        {
            get
            {
                var list = new ComfyPreset[PresetCount];
                for (int i = 0; i < PresetCount; i++)
                {
                    list[i] = new ComfyPreset();
                    list[i].Value = Load("comfy_prompt_" + (i + 1) + ".txt") ?? "";
                }
                foreach (string line in (Load("comfy_prompt_names.txt") ?? "").Split('\n'))
                {
                    string[] p = line.TrimEnd('\r').Split('\t');
                    int n;
                    if (p.Length < 2 || !int.TryParse(p[0], out n) || n < 1 || n > PresetCount) continue;
                    list[n - 1].Name = p[1].Trim();
                }
                return list;
            }
            set
            {
                var names = new StringBuilder();
                for (int i = 0; i < PresetCount && i < value.Length; i++)
                {
                    string file = "comfy_prompt_" + (i + 1) + ".txt";
                    string text = (value[i].Value ?? "").Trim();
                    if (text.Length > 0 || File.Exists(FilePath(file))) Save(file, text);
                    names.Append(i + 1).Append('\t').Append(OneLine(value[i].Name)).Append("\r\n");
                }
                Save("comfy_prompt_names.txt", names.ToString());
            }
        }

        public static string Seconds
        {
            get { return Load("comfy_seconds.txt") ?? ""; }
            set { Save("comfy_seconds.txt", (value ?? "").Trim()); }
        }

        // 元の画像の評価を、生成したファイルに引き継ぐか (既定はオン)
        public static bool InheritRating
        {
            get { return Load("comfy_inherit.txt") != "0"; }
            set { Save("comfy_inherit.txt", value ? "1" : "0"); }
        }

        // 生成が終わったら、出来たファイルを表示するか (既定はオン。まとめて生成のときは移動しない)
        public static bool JumpToResult
        {
            get { return Load("comfy_jump.txt") != "0"; }
            set { Save("comfy_jump.txt", value ? "1" : "0"); }
        }

        // セットのプリセット (comfy_sets.txt、JSON)。いつも PresetCount 件 (未登録のものは空)
        public static ComfySet[] Sets
        {
            get
            {
                var list = new ComfySet[PresetCount];
                for (int i = 0; i < PresetCount; i++) list[i] = new ComfySet();
                try
                {
                    var arr = ComfyVideo.NewSerializer().DeserializeObject(Load("comfy_sets.txt") ?? "") as object[];
                    for (int i = 0; arr != null && i < arr.Length && i < PresetCount; i++)
                    {
                        var d = arr[i] as Dictionary<string, object>;
                        if (d == null) continue;
                        list[i].Name = Str(d, "name");
                        list[i].Url = Str(d, "url");
                        list[i].Workflow = Str(d, "workflow");
                        list[i].Prompt = Str(d, "prompt");
                        list[i].Seconds = Str(d, "seconds");
                        int pp;
                        list[i].PromptPreset = int.TryParse(Str(d, "promptPreset"), out pp) && pp >= 1 && pp <= PresetCount ? pp - 1 : -1;
                    }
                }
                catch { }
                return list;
            }
            set
            {
                var arr = new List<Dictionary<string, object>>();
                foreach (var s in value)
                {
                    var d = new Dictionary<string, object>();
                    d["name"] = s.Name;
                    d["url"] = s.Url;
                    d["workflow"] = s.Workflow;
                    d["prompt"] = s.Prompt;
                    d["seconds"] = s.Seconds;
                    d["promptPreset"] = s.PromptPreset + 1;
                    arr.Add(d);
                }
                Save("comfy_sets.txt", ComfyVideo.NewSerializer().Serialize(arr));
            }
        }

        // 「生成する」の画面で前回選んでいたセットの番号 (0 始まり。選んでいなければ -1)
        public static int LastSet
        {
            get
            {
                int n;
                return int.TryParse(Load("comfy_set.txt"), out n) && n >= 1 && n <= PresetCount ? n - 1 : -1;
            }
            set { Save("comfy_set.txt", (value + 1).ToString()); }
        }

        // 同じく、前回選んでいたプロンプトのプリセットの番号 (0 始まり。選んでいなければ -1)
        public static int LastPromptPreset
        {
            get
            {
                int n;
                return int.TryParse(Load("comfy_prompt_preset.txt"), out n) && n >= 1 && n <= PresetCount ? n - 1 : -1;
            }
            set { Save("comfy_prompt_preset.txt", (value + 1).ToString()); }
        }

        // ---------------- エクスポート / インポート ----------------
        // 生成の設定 (comfy_*.txt) をまとめて 1 つの JSON にする。生成の記録 (comfy_history.txt) は入れない。
        // ワークフローの JSON は中身も入れておき、インポート先に同じパスのファイルがなければ
        // %APPDATA%\J-Rate\workflows に書き出して、登録してあるパスをそちらに付け替える
        private const string ExportKind = "comfy-settings";

        // インポートの直前に、それまでの設定を控えておくファイル (エクスポートと同じ形式)
        public static string ImportBackupPath
        {
            get { return FilePath("backup_before_import.json"); }
        }

        private static bool IsSettingFile(string name)
        {
            if (!name.StartsWith("comfy_") || !name.EndsWith(".txt") || name == "comfy_history.txt") return false;
            foreach (char c in name)
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '.')) return false;
            return true;
        }

        private static List<string> SettingFiles()
        {
            var list = new List<string>();
            string dir = Path.GetDirectoryName(FilePath("x"));
            if (!Directory.Exists(dir)) return list;
            foreach (string f in Directory.GetFiles(dir, "comfy_*.txt"))
                if (IsSettingFile(Path.GetFileName(f))) list.Add(f);
            return list;
        }

        // 戻り値 : 結果の説明。書き出せなかったときは例外
        public static string Export(string path)
        {
            var files = new Dictionary<string, object>();
            foreach (string f in SettingFiles()) files[Path.GetFileName(f)] = File.ReadAllText(f);

            // プリセットとセットに登録してあるワークフローの中身
            var paths = new List<string>();
            foreach (var p in Workflows) paths.Add(p.Value);
            foreach (var s in Sets) paths.Add(s.Workflow);
            var workflows = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int missing = 0;
            foreach (string wf in paths)
            {
                if (wf.Length == 0 || !seen.Add(wf)) continue;
                string text;
                try { text = File.ReadAllText(wf); }
                catch { missing++; continue; }
                var d = new Dictionary<string, object>();
                d["path"] = wf;
                d["content"] = text;
                workflows.Add(d);
            }

            var root = new Dictionary<string, object>();
            root["app"] = "J-Rate";
            root["kind"] = ExportKind;
            root["version"] = 1;
            root["exported"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            root["files"] = files;
            root["workflows"] = workflows;
            File.WriteAllText(path, ComfyVideo.NewSerializer().Serialize(root), Encoding.UTF8);

            return "生成の設定をエクスポートしました (ワークフローの JSON " + workflows.Count + " 個を含む" +
                   (missing > 0 ? "。" + missing + " 個は読めなかったので含めていません" : "") + "): " + path;
        }

        // 今の設定をすべて置き換える。戻り値 : 結果の説明。読めないファイルのときは例外 (設定は変えない)
        public static string Import(string path)
        {
            Dictionary<string, object> root = null;
            string json = File.ReadAllText(path);
            try { root = ComfyVideo.NewSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch { }
            object o;
            var files = root != null && root.TryGetValue("files", out o) ? o as Dictionary<string, object> : null;
            if (files == null || Str(root, "kind") != ExportKind)
                throw new Exception("J-Rate の生成の設定をエクスポートしたファイルではありません。");

            try { Export(ImportBackupPath); } catch { }

            foreach (string f in SettingFiles()) File.Delete(f);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath("x")));
            foreach (var kv in files)
                if (IsSettingFile(kv.Key) && kv.Value != null) File.WriteAllText(FilePath(kv.Key), kv.Value.ToString());

            // この PC にないワークフローは書き出して、登録してあるパスを付け替える
            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = root.TryGetValue("workflows", out o) ? o as object[] : null;
            foreach (object item in list ?? new object[0])
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                string old = Str(d, "path");
                if (old.Length == 0 || moved.ContainsKey(old)) continue;
                bool exists = false;
                try { exists = File.Exists(old); } catch { }
                if (!exists) moved[old] = RestoreWorkflow(old, Str(d, "content"));
            }
            if (moved.Count > 0)
            {
                string to;
                var presets = Workflows;
                foreach (var p in presets)
                    if (moved.TryGetValue(p.Value, out to)) p.Value = to;
                Workflows = presets;
                var sets = Sets;
                bool changed = false;
                foreach (var s in sets)
                    if (moved.TryGetValue(s.Workflow, out to)) { s.Workflow = to; changed = true; }
                if (changed) Sets = sets;
            }

            return "生成の設定をインポートしました。" +
                   (moved.Count > 0 ? "\n\nこの PC になかったワークフローの JSON " + moved.Count + " 個を次のフォルダに書き出しました。\n" +
                                      FilePath("workflows") : "");
        }

        // ワークフローの JSON を %APPDATA%\J-Rate\workflows に書き出してパスを返す。
        // 同じ名前で中身も同じファイルがあればそれを使い、中身が違えば「名前 (2)」のようにする
        private static string RestoreWorkflow(string oldPath, string content)
        {
            string name = "workflow.json";
            try
            {
                // 他の PC のパスなので、区切りは \ と / のどちらもありうる
                string n = oldPath.Substring(oldPath.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                if (n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) name = n;
            }
            catch { }
            string dir = FilePath("workflows");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, name);
            for (int i = 2; File.Exists(file); i++)
            {
                if (File.ReadAllText(file) == content) return file;
                file = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            }
            File.WriteAllText(file, content);
            return file;
        }

        internal static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o != null ? o.ToString() : "";
        }

        internal static string FilePath(string name)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "J-Rate");
            return Path.Combine(dir, name);
        }

        internal static string Load(string name)
        {
            try
            {
                string f = FilePath(name);
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch { return null; }
        }

        internal static void Save(string name, string value)
        {
            try
            {
                string f = FilePath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                File.WriteAllText(f, value);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // ComfyUI の HTTP API を使った生成 (画像 → 動画、画像 → 画像)
    //   1. POST /upload/image  で画像を送る
    //   2. ワークフローの LoadImage ノードの画像名を差し替え、シードをランダムにする
    //      (プロンプトと秒数も、指定があれば差し替える)
    //   3. POST /prompt        で実行を依頼
    //   4. GET  /history/{id}  で完了を待つ (待ち中は GET /queue で順番を表示)
    //   5. GET  /view          で出来たファイルを受け取り、元画像と同じフォルダに保存
    // ------------------------------------------------------------------
    public static class ComfyVideo
    {
        private static readonly string[] VideoExts = { ".mp4", ".m4v", ".webm", ".mov", ".mkv", ".gif", ".webp" };
        private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg" };
        private static readonly Random Rng = new Random();

        // prompt / seconds が null のときはワークフローに書かれている値のまま。
        // 戻り値 : 保存したファイルのパス (動画なら 1 つ、画像なら出力された枚数ぶん)。
        //          失敗したときは例外 (メッセージは日本語)。
        //          cancel で止められたときは ComfyUI 側の実行も取り消して OperationCanceledException。
        public static List<string> Generate(string imagePath, string baseUrl, string workflowPath,
                                            string prompt, double? seconds, Action<string> progress,
                                            CancellationToken cancel)
        {
            if (progress == null) progress = delegate { };
            baseUrl = (baseUrl ?? ComfySettings.DefaultUrl).TrimEnd('/');

            // ワークフローの読み込みと確認
            var json = NewSerializer();
            var workflow = LoadWorkflow(json, workflowPath);
            var loadNodes = FindNodes(workflow, "LoadImage");
            if (loadNodes.Count == 0)
                throw new Exception("ワークフローに「Load Image」(LoadImage) ノードがありません。\n" + workflowPath);

            // 画像のアップロード
            cancel.ThrowIfCancellationRequested();
            progress("ComfyUI へ画像を送信中...");
            string uploadName = Upload(baseUrl, imagePath, json);

            foreach (var node in loadNodes) Inputs(node)["image"] = uploadName;
            RandomizeSeeds(workflow);
            if (prompt != null)
                foreach (var slot in PromptSlots(workflow)) slot.Key[slot.Value] = prompt;
            if (seconds != null) SetDuration(workflow, seconds.Value);

            // 実行を依頼
            cancel.ThrowIfCancellationRequested();
            progress("ComfyUI に実行を依頼中...");
            var body = new Dictionary<string, object>();
            body["prompt"] = workflow;
            body["client_id"] = "j-rate-" + Guid.NewGuid().ToString("N");
            string res = Request(baseUrl + "/prompt", "POST", "application/json",
                                 Encoding.UTF8.GetBytes(json.Serialize(body)));
            var resObj = json.DeserializeObject(res) as Dictionary<string, object>;
            object pidObj;
            if (resObj == null || !resObj.TryGetValue("prompt_id", out pidObj) || pidObj == null)
                throw new Exception("ComfyUI が実行を受け付けませんでした。\n" + Shorten(res));
            string promptId = pidObj.ToString();

            // 完了を待つ
            var started = DateTime.Now;
            int failures = 0;
            Dictionary<string, object> entry = null;
            while (true)
            {
                if (cancel.WaitHandle.WaitOne(2000))
                {
                    CancelPrompt(baseUrl, json, promptId);
                    throw new OperationCanceledException();
                }
                try
                {
                    string h = Request(baseUrl + "/history/" + Uri.EscapeDataString(promptId), "GET", null, null);
                    var hist = json.DeserializeObject(h) as Dictionary<string, object>;
                    object e;
                    if (hist != null && hist.TryGetValue(promptId, out e))
                    {
                        entry = e as Dictionary<string, object>;
                        if (entry != null) break;
                    }
                    progress(QueueState(baseUrl, json, promptId) + " " + Elapsed(started));
                    failures = 0;
                }
                catch (WebException)
                {
                    // ComfyUI が落ちた・再起動中など。1 分ほど続いたらあきらめる
                    if (++failures >= 30) throw new Exception("ComfyUI との接続が切れました。(" + baseUrl + ")");
                    progress("ComfyUI の応答待ち... " + Elapsed(started));
                }
            }

            CheckStatus(entry);

            // 出力ファイルを探して保存
            var outputs = FindOutputs(entry);
            if (outputs.Count == 0)
                throw new Exception("ワークフローの出力に動画や画像のファイルが見つかりません。\n" +
                                    "Save Video / Save Image などの保存ノードがあるか確認してください。");

            // 動画は「(元の名前)_video」、画像は「(元の名前)_i2i」で保存する
            var saved = new List<string>();
            foreach (var output in outputs)
            {
                cancel.ThrowIfCancellationRequested();
                progress(outputs.Count > 1 ? "結果を受け取り中... (" + (saved.Count + 1) + " / " + outputs.Count + ")"
                                           : "結果を受け取り中...");
                string dest = UniquePath(Path.Combine(Path.GetDirectoryName(imagePath),
                                         Path.GetFileNameWithoutExtension(imagePath) + (output.IsVideo ? "_video" : "_i2i") +
                                         Path.GetExtension(output.Filename)));
                string url = baseUrl + "/view?filename=" + Uri.EscapeDataString(output.Filename)
                           + "&subfolder=" + Uri.EscapeDataString(output.Subfolder)
                           + "&type=" + Uri.EscapeDataString(output.Type);
                string temp = dest + ".part";
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Proxy = null;
                        wc.DownloadFile(url, temp);
                    }
                    File.Move(temp, dest);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
                saved.Add(dest);
            }
            return saved;
        }

        // ComfyUI 側の実行を取り消す。順番待ちなら待ち行列から外し、実行中なら中断する
        private static void CancelPrompt(string baseUrl, JavaScriptSerializer json, string promptId)
        {
            try
            {
                var del = new Dictionary<string, object>();
                del["delete"] = new[] { promptId };
                Request(baseUrl + "/queue", "POST", "application/json", Encoding.UTF8.GetBytes(json.Serialize(del)));
            }
            catch { }
            try
            {
                // 中断は「今実行しているもの」に効くので、自分の番のときだけ送る
                var q = json.DeserializeObject(Request(baseUrl + "/queue", "GET", null, null)) as Dictionary<string, object>;
                object running;
                if (q != null && q.TryGetValue("queue_running", out running) && ContainsPrompt(running, promptId) >= 0)
                {
                    var body = new Dictionary<string, object>();
                    body["prompt_id"] = promptId;
                    Request(baseUrl + "/interrupt", "POST", "application/json", Encoding.UTF8.GetBytes(json.Serialize(body)));
                }
            }
            catch { }
        }

        // ComfyUI が動いているか確かめる。戻り値はエラーメッセージ (正常なら null)
        public static string Test(string baseUrl)
        {
            try
            {
                Request((baseUrl ?? "").TrimEnd('/') + "/system_stats", "GET", null, null);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ワークフローを読み込む。API 形式でなければ説明付きの例外
        public static Dictionary<string, object> LoadWorkflow(JavaScriptSerializer json, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new Exception("ワークフローのファイルが見つかりません。\n" + path);
            Dictionary<string, object> wf;
            try { wf = json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception ex) { throw new Exception("ワークフローの JSON を読めません。\n" + ex.Message); }
            if (wf == null || wf.ContainsKey("nodes") || !wf.Values.Any(v => v is Dictionary<string, object> &&
                                                          ((Dictionary<string, object>)v).ContainsKey("class_type")))
                throw new Exception("このファイルは API 形式のワークフローではありません。\n" +
                                    "ComfyUI のメニュー「Export (API)」(Save (API Format)) で保存した JSON を指定してください。\n" + path);
            return wf;
        }

        public static List<Dictionary<string, object>> FindNodes(Dictionary<string, object> wf, string classType)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var v in wf.Values)
            {
                var node = v as Dictionary<string, object>;
                object ct;
                if (node != null && node.TryGetValue("class_type", out ct) && (ct as string) == classType && Inputs(node) != null)
                    list.Add(node);
            }
            return list;
        }

        public static JavaScriptSerializer NewSerializer()
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 1000;
            return js;
        }

        private static Dictionary<string, object> Inputs(Dictionary<string, object> node)
        {
            object o;
            return node.TryGetValue("inputs", out o) ? o as Dictionary<string, object> : null;
        }

        private static string ClassType(Dictionary<string, object> node)
        {
            object o;
            return node.TryGetValue("class_type", out o) ? o as string ?? "" : "";
        }

        private static string Title(Dictionary<string, object> node)
        {
            object o, t;
            var meta = node.TryGetValue("_meta", out o) ? o as Dictionary<string, object> : null;
            return meta != null && meta.TryGetValue("title", out t) ? t as string ?? "" : "";
        }

        // ワークフローにプロンプトを入れる場所があるか
        public static bool HasPrompt(Dictionary<string, object> wf)
        {
            return PromptSlots(wf).Count > 0;
        }

        // プロンプトを入れる場所 (inputs とキー名)。
        //   文字列が直接入っている "prompt" 入力があればそこ (すべて)。
        //   なければ CLIPTextEncode の "text" : 1 つならそれ、複数ならタイトルに Positive とあるもの。
        private static List<KeyValuePair<Dictionary<string, object>, string>> PromptSlots(Dictionary<string, object> wf)
        {
            var slots = new List<KeyValuePair<Dictionary<string, object>, string>>();
            var clip = new List<Dictionary<string, object>>();
            foreach (var v in wf.Values)
            {
                var node = v as Dictionary<string, object>;
                if (node == null) continue;
                var inputs = Inputs(node);
                if (inputs == null) continue;
                object val;
                if (inputs.TryGetValue("prompt", out val) && val is string)
                    slots.Add(new KeyValuePair<Dictionary<string, object>, string>(inputs, "prompt"));
                else if (ClassType(node) == "CLIPTextEncode" && inputs.TryGetValue("text", out val) && val is string)
                    clip.Add(node);
            }
            if (slots.Count > 0) return slots;

            var pick = clip.Count == 1
                ? clip
                : clip.Where(n => Title(n).IndexOf("positive", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var n in pick)
                slots.Add(new KeyValuePair<Dictionary<string, object>, string>(Inputs(n), "text"));
            return slots;
        }

        // ワークフローに書かれている動画の長さ (秒)。変えられる場所がなければ null
        public static double? GetDuration(Dictionary<string, object> wf)
        {
            string key;
            var inputs = DurationSlot(wf, out key);
            if (inputs == null) return null;
            return Convert.ToDouble(inputs[key]);
        }

        // 長さを整数でしか受け付けないワークフローか (Int ノードで指定している)
        public static bool DurationIsInteger(Dictionary<string, object> wf)
        {
            string key;
            var inputs = DurationSlot(wf, out key);
            if (inputs == null || key != "value") return false;
            return wf.Values.OfType<Dictionary<string, object>>()
                     .Any(n => Inputs(n) == inputs && ClassType(n) == "PrimitiveInt");
        }

        private static void SetDuration(Dictionary<string, object> wf, double seconds)
        {
            string key;
            var inputs = DurationSlot(wf, out key);
            if (inputs == null) return;
            // 端数がなければ整数で入れる (整数の入力でも小数の入力でも通る)。
            // JSON では小数の入力でも 5 と書かれているので、元の値の型では見分けられない
            if (seconds == Math.Floor(seconds)) inputs[key] = (int)seconds;
            else inputs[key] = seconds;
        }

        // 長さを入れる場所。
        //   タイトルに duration とある Float / Int ノードの "value"。
        //   なければ、数値が直接入っている "duration" 入力。
        private static Dictionary<string, object> DurationSlot(Dictionary<string, object> wf, out string key)
        {
            Dictionary<string, object> direct = null;
            foreach (var v in wf.Values)
            {
                var node = v as Dictionary<string, object>;
                if (node == null) continue;
                var inputs = Inputs(node);
                if (inputs == null) continue;
                object val;
                string ct = ClassType(node);
                if ((ct == "PrimitiveFloat" || ct == "PrimitiveInt") &&
                    Title(node).IndexOf("duration", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    inputs.TryGetValue("value", out val) && IsNumber(val))
                {
                    key = "value";
                    return inputs;
                }
                if (direct == null && inputs.TryGetValue("duration", out val) && IsNumber(val)) direct = inputs;
            }
            key = "duration";
            return direct;
        }

        // seed / noise_seed に数値が直接入っていれば毎回変える (他ノードからの接続 [id, n] はそのまま)
        private static void RandomizeSeeds(Dictionary<string, object> wf)
        {
            foreach (var v in wf.Values)
            {
                var node = v as Dictionary<string, object>;
                if (node == null) continue;
                var inputs = Inputs(node);
                if (inputs == null) continue;
                foreach (string key in new[] { "seed", "noise_seed" })
                {
                    object val;
                    if (inputs.TryGetValue(key, out val) && IsNumber(val))
                    {
                        long seed;
                        lock (Rng) seed = (long)(Rng.NextDouble() * 1e15);
                        inputs[key] = seed;
                    }
                }
            }
        }

        private static bool IsNumber(object o)
        {
            return o is int || o is long || o is decimal || o is double;
        }

        // multipart/form-data で /upload/image に送る。戻り値はワークフローに入れる画像名
        private static string Upload(string baseUrl, string imagePath, JavaScriptSerializer json)
        {
            // ファイル名は ASCII にしておく (日本語名だと環境によって化けるため)
            int r;
            lock (Rng) r = Rng.Next(1000, 9999);
            string name = "jrate_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + r +
                          Path.GetExtension(imagePath).ToLowerInvariant();
            string boundary = "----JRate" + Guid.NewGuid().ToString("N");
            byte[] data;
            using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                Action<string> w = s => { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); };
                w("--" + boundary + "\r\n");
                w("Content-Disposition: form-data; name=\"image\"; filename=\"" + name + "\"\r\n");
                w("Content-Type: application/octet-stream\r\n\r\n");
                fs.CopyTo(ms);
                w("\r\n--" + boundary + "\r\n");
                w("Content-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n");
                w("--" + boundary + "--\r\n");
                data = ms.ToArray();
            }
            string res = Request(baseUrl + "/upload/image", "POST", "multipart/form-data; boundary=" + boundary, data);
            var obj = json.DeserializeObject(res) as Dictionary<string, object>;
            object n, sub;
            if (obj == null || !obj.TryGetValue("name", out n) || n == null)
                throw new Exception("画像のアップロードに失敗しました。\n" + Shorten(res));
            string result = n.ToString();
            if (obj.TryGetValue("subfolder", out sub) && sub != null && sub.ToString().Length > 0)
                result = sub.ToString() + "/" + result;
            return result;
        }

        private static string QueueState(string baseUrl, JavaScriptSerializer json, string promptId)
        {
            try
            {
                var q = json.DeserializeObject(Request(baseUrl + "/queue", "GET", null, null)) as Dictionary<string, object>;
                if (q != null)
                {
                    object running, pending;
                    if (q.TryGetValue("queue_running", out running) && ContainsPrompt(running, promptId) >= 0)
                        return "生成中";
                    if (q.TryGetValue("queue_pending", out pending))
                    {
                        // pending は順不同なので、番号 (先頭要素) で並べて順番を出す
                        var arr = pending as IEnumerable;
                        if (arr != null)
                        {
                            var list = arr.Cast<object>().OfType<IList>().Where(x => x.Count >= 2)
                                          .OrderBy(x => Convert.ToDouble(x[0])).ToList();
                            int idx = list.FindIndex(x => Convert.ToString(x[1]) == promptId);
                            if (idx >= 0) return "順番待ち (" + (idx + 1) + " 番目)";
                        }
                    }
                }
            }
            catch { }
            return "生成中";
        }

        private static int ContainsPrompt(object queue, string promptId)
        {
            var arr = queue as IEnumerable;
            if (arr == null) return -1;
            int i = 0;
            foreach (var x in arr)
            {
                var l = x as IList;
                if (l != null && l.Count >= 2 && Convert.ToString(l[1]) == promptId) return i;
                i++;
            }
            return -1;
        }

        private static void CheckStatus(Dictionary<string, object> entry)
        {
            object st;
            if (!entry.TryGetValue("status", out st)) return;
            var status = st as Dictionary<string, object>;
            if (status == null) return;
            object s;
            if (!status.TryGetValue("status_str", out s) || (s as string) != "error") return;

            // エラーの詳細 (messages の execution_error) を拾う
            string detail = "";
            object msgs;
            if (status.TryGetValue("messages", out msgs) && msgs is IEnumerable)
            {
                foreach (var m in (IEnumerable)msgs)
                {
                    var pair = m as IList;
                    if (pair == null || pair.Count < 2 || Convert.ToString(pair[0]) != "execution_error") continue;
                    var d = pair[1] as Dictionary<string, object>;
                    if (d == null) continue;
                    object nt, em;
                    d.TryGetValue("node_type", out nt);
                    d.TryGetValue("exception_message", out em);
                    detail = (nt != null ? "[" + nt + "] " : "") + (em != null ? em.ToString().Trim() : "");
                }
            }
            throw new Exception("ComfyUI での生成中にエラーが起きました。\n" + Shorten(detail));
        }

        private class OutputFile
        {
            public string Filename, Subfolder, Type;
            public bool IsVideo;
        }

        // 出力ノードの結果 (gifs / videos / images など) から受け取るファイルを選ぶ。
        //   動画があれば 1 つだけ (mp4 を優先)。
        //   なければ画像をすべて (保存ノードのものがあればそれだけ、なければプレビューのもの)。
        private static List<OutputFile> FindOutputs(Dictionary<string, object> entry)
        {
            var found = new List<OutputFile>();
            object o;
            if (!entry.TryGetValue("outputs", out o)) return found;
            var outputs = o as Dictionary<string, object>;
            if (outputs == null) return found;

            foreach (var nodeOut in outputs.Values.OfType<Dictionary<string, object>>())
            {
                foreach (var kv in nodeOut)
                {
                    var arr = kv.Value as IEnumerable;
                    if (arr == null || kv.Value is string) continue;
                    foreach (var f in arr.OfType<Dictionary<string, object>>())
                    {
                        object fn, sub, type;
                        if (!f.TryGetValue("filename", out fn) || fn == null) continue;
                        string ext = Path.GetExtension(fn.ToString()).ToLowerInvariant();
                        bool video = Array.IndexOf(VideoExts, ext) >= 0;
                        if (!video && Array.IndexOf(ImageExts, ext) < 0) continue;
                        f.TryGetValue("subfolder", out sub);
                        f.TryGetValue("type", out type);
                        found.Add(new OutputFile
                        {
                            Filename = fn.ToString(),
                            Subfolder = sub != null ? sub.ToString() : "",
                            Type = type != null ? type.ToString() : "output",
                            IsVideo = video
                        });
                    }
                }
            }

            // 動画の優先順位 : output フォルダ > temp、拡張子は VideoExts の並び順 (mp4 が先頭)
            var best = found.Where(f => f.IsVideo)
                            .OrderBy(f => f.Type == "output" ? 0 : 1)
                            .ThenBy(f => Array.IndexOf(VideoExts, Path.GetExtension(f.Filename).ToLowerInvariant()))
                            .FirstOrDefault();
            if (best != null) return new List<OutputFile> { best };

            var kept = found.Where(f => f.Type == "output").ToList();
            return kept.Count > 0 ? kept : found;
        }

        private static string Request(string url, string method, string contentType, byte[] body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Proxy = null;   // ローカル / LAN の ComfyUI にプロキシを通さない
            req.Timeout = 30000;
            req.ReadWriteTimeout = 120000;
            if (body != null)
            {
                req.ContentType = contentType;
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            }
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                // 400 などは本文に理由 (node_errors など) が入っているので、それを見せる
                var res = ex.Response as HttpWebResponse;
                if (res == null) throw;
                string text;
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
                throw new Exception("ComfyUI がエラーを返しました (" + (int)res.StatusCode + ")。\n" + Shorten(text));
            }
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
        }

        private static string Elapsed(DateTime start)
        {
            var t = DateTime.Now - start;
            return string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        private static string Shorten(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length > 800 ? s.Substring(0, 800) + "..." : s;
        }
    }
}
