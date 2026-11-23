// PrismLab のレシピで生成する (PrismLab の docs\spec.md 6 章。2026-11-22 利用者の決定)。
// 生成の設定はすべて PrismLab で行い、J-Rate は PrismLab が「ほかのアプリに出す」にしたレシピを選んで生成するだけ。
// - 出してもらったレシピ: %APPDATA%\PrismLab\published.json (PrismLab がレシピを保存するたびに書く)
// - 頼み方: パイプ PrismLab-(ユーザー名) に {"cmd": "run", "id", "from": "J-Rate", "recipe", "index", "files", "machines"}。
//   PrismLab が動いていなければ「PrismLab.exe --background --send (JSON のパス)」で、画面を出さずに起動する。
// - 進み具合: %APPDATA%\PrismLab\external_status.json (PrismLab が書く。こちらは読むだけ。出来たファイルのパスも入る)
// - 台: J-Rate で選んだ台 (%APPDATA%\J-Rate\comfy_machines.txt。最大 3 台。アプリごと)
// - 出来たファイルは元画像と同じフォルダに PrismLab が置く (PromptWordCrafter・MuseFactory の GenCommon recipe_link.py と同じ決まり)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace JRate
{
    public class PublishedRecipe
    {
        public string Name = "";
        public int Index = -1;
        public string Flow = "";      // 「読み取り → 盛る → 生成 (wan) 🎬」
        public int Repeat = 1;
        public string Problem = "";   // J-Rate では使えない理由 (なければ空)
    }

    public class RecipeStatus
    {
        public string Id = "", Recipe = "", State = "running", Error = "";
        public int Total, Done, Failed, Canceled, Running, Waiting, Gated, Outputs;
        public double Created;
        public double? FinishedAt;
        public List<string> Paths = new List<string>();
        public List<string> Errors = new List<string>();
        public string Warning = "";  // 知らせ (失敗ではない。「LLM に繋がらないので、英語にせずに送りました (n 件)」)
        public List<Dictionary<string, object>> Machines = new List<Dictionary<string, object>>();
        public bool Active { get { return State == "running"; } }
    }

    public static class RecipeLink
    {
        public const string App = "J-Rate";
        public const int MaxMachines = 3;
        private const string MachinesFile = "comfy_machines.txt";

        private static string AppData
        {
            get { return ComfySettings.AppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        }

        private static string PrismDir { get { return Path.Combine(AppData, "PrismLab"); } }

        // 読めなければ null。PrismLab が書き換えている瞬間に当たったときのため、少し待ってもう一度だけ読む
        private static object ReadJson(string path)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                }
                catch (Exception)
                {
                    if (attempt == 0) System.Threading.Thread.Sleep(50);
                }
            }
            return null;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        private static int Int(Dictionary<string, object> d, string key, int def = 0)
        {
            int n;
            return int.TryParse(Str(d, key), out n) ? n : def;
        }

        private static double Num(Dictionary<string, object> d, string key)
        {
            double n;
            return double.TryParse(Str(d, key), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        private static List<string> StrList(Dictionary<string, object> d, string key)
        {
            object v;
            var arr = d != null && d.TryGetValue(key, out v) ? v as object[] : null;
            return arr == null ? new List<string>() : arr.Where(x => x != null).Select(x => x.ToString()).ToList();
        }

        /// <summary>J-Rate に出してもらったレシピ (PrismLab の並び順)。</summary>
        public static List<PublishedRecipe> Recipes()
        {
            var result = new List<PublishedRecipe>();
            var root = ReadJson(Path.Combine(PrismDir, "published.json")) as Dictionary<string, object>;
            object items;
            if (root == null || !root.TryGetValue("recipes", out items) || !(items is object[])) return result;
            foreach (var o in (object[])items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !StrList(d, "apps").Contains(App)) continue;
                object problems;
                var pd = d.TryGetValue("problems", out problems) ? problems as Dictionary<string, object> : null;
                result.Add(new PublishedRecipe
                {
                    Name = Str(d, "name"), Index = Int(d, "index", -1), Flow = Str(d, "flow"),
                    Repeat = Math.Max(1, Int(d, "repeat", 1)), Problem = Str(pd, App)
                });
            }
            return result;
        }

        // ---------------- 台 (J-Rate で使う ComfyUI。最大 3 台) ----------------

        private static string MachinesPath { get { return Path.Combine(ComfySettings.AppDir, MachinesFile); } }

        private static List<string> ParseUrls(string text)
        {
            var urls = new List<string>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                string url = line.Trim().TrimEnd('/');
                if (url.Length > 0 && !urls.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
                    urls.Add(url);
            }
            return urls.Take(MaxMachines).ToList();
        }

        private static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>J-Rate で使う台の URL。まだ決めていなければ PrismLab の台 (休みは除く) を写す。</summary>
        public static List<string> Machines()
        {
            string text = ReadText(MachinesPath);
            if (text == null)
            {
                var prism = ParseUrls(ReadText(Path.Combine(PrismDir, "machines.txt")));
                var off = ParseUrls(ReadText(Path.Combine(PrismDir, "machines_off.txt")));
                var seed = prism.Where(u => !off.Any(o => string.Equals(o, u, StringComparison.OrdinalIgnoreCase))).ToList();
                SetMachines(seed);
                return seed;
            }
            return ParseUrls(text);
        }

        public static void SetMachines(List<string> urls)
        {
            try
            {
                Directory.CreateDirectory(ComfySettings.AppDir);
                File.WriteAllText(MachinesPath, string.Join("\n", urls.Take(MaxMachines)), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// <summary>台の名前 (登録した接続先の名前。なければ URL)。</summary>
        public static string NameOf(string url)
        {
            var p = ComfySettings.Presets.FirstOrDefault(x => !x.IsEmpty &&
                string.Equals(x.Value.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            return p != null && p.Name.Length > 0 ? p.Name : url;
        }

        // ---------------- 頼む・止める ----------------

        /// <summary>レシピで生成するよう頼む。戻り値は理由 (うまくいけば空)。id に頼みの番号。</summary>
        public static string Run(PublishedRecipe recipe, IList<string> files, out string id)
        {
            id = "";
            if (recipe.Problem.Length > 0) return recipe.Problem;
            var urls = Machines();
            if (urls.Count == 0) return "使う台が決まっていません (「レシピで生成」の「台」で選んでください)。";
            id = "JRa-" + DateTime.Now.ToString("MMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            return PrismLabLink.SendCommand(new Dictionary<string, object>
            {
                { "cmd", "run" }, { "id", id }, { "from", App }, { "recipe", recipe.Name }, { "index", recipe.Index },
                { "files", files.ToArray() }, { "machines", urls.ToArray() }
            }, true);
        }

        public static string Cancel(string id)
        {
            return PrismLabLink.SendCommand(new Dictionary<string, object> { { "cmd", "run_cancel" }, { "id", id } }, false);
        }

        /// <summary>J-Rate が頼んだものの進み具合 (番号 → 状態)。</summary>
        public static Dictionary<string, RecipeStatus> Statuses()
        {
            var result = new Dictionary<string, RecipeStatus>();
            var root = ReadJson(Path.Combine(PrismDir, "external_status.json")) as Dictionary<string, object>;
            object items;
            if (root == null || !root.TryGetValue("requests", out items) || !(items is object[])) return result;
            foreach (var o in (object[])items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || Str(d, "app") != App || Str(d, "id").Length == 0) continue;
                object fin, ms;
                var st = new RecipeStatus
                {
                    Id = Str(d, "id"), Recipe = Str(d, "recipe"), State = Str(d, "state"), Error = Str(d, "error"),
                    Total = Int(d, "total"), Done = Int(d, "done"), Failed = Int(d, "failed"), Canceled = Int(d, "canceled"),
                    Running = Int(d, "running"), Waiting = Int(d, "waiting"), Gated = Int(d, "gated"),
                    Outputs = Int(d, "outputs"), Created = Num(d, "created"),
                    FinishedAt = d.TryGetValue("finished_at", out fin) && fin != null ? (double?)Num(d, "finished_at") : null,
                    Paths = StrList(d, "paths"), Errors = StrList(d, "errors"),
                    Warning = Str(d, "warning")
                };
                if (d.TryGetValue("machines", out ms) && ms is object[])
                    st.Machines = ((object[])ms).OfType<Dictionary<string, object>>().ToList();
                result[st.Id] = st;
            }
            return result;
        }

        /// <summary>進み具合の 1 行。</summary>
        public static string Line(RecipeStatus s)
        {
            string main = LineMain(s);
            return s.Warning.Length > 0 && s.State != "error" ? main + "  ⚠ " + s.Warning : main;
        }

        static string LineMain(RecipeStatus s)
        {
            if (s.State == "error") return "✗「" + s.Recipe + "」: " + s.Error;
            int finished = s.Done + s.Failed + s.Canceled;
            if (s.Active)
                return "▶「" + s.Recipe + "」" + finished + "/" + s.Total + " 件 (実行中 " + s.Running
                       + (s.Waiting > 0 ? "・待機 " + s.Waiting : "") + (s.Failed > 0 ? "・失敗 " + s.Failed : "")
                       + (s.Gated > 0 ? "・★待ち " + s.Gated : "") + ")";
            string text = (s.State == "canceled" ? "■「" + s.Recipe + "」止めました: " : "✓「" + s.Recipe + "」完了: ")
                          + s.Done + "/" + s.Total + " 件 (出来たファイル " + s.Outputs + ")";
            if (s.Failed > 0) text += "  失敗 " + s.Failed + (s.Errors.Count > 0 ? ": " + s.Errors[0] : "");
            return text;
        }
    }
}
