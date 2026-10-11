// J-Rate : ComfyUI に画像を渡して動画を生成する (右クリック「動画にする」)
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
    // 設定 (%APPDATA%\J-Rate\comfy_url.txt / comfy_workflow.txt)
    // ------------------------------------------------------------------
    public static class ComfySettings
    {
        public const string DefaultUrl = "http://127.0.0.1:8188";

        public static string Url
        {
            get
            {
                string s = Load("comfy_url.txt");
                return string.IsNullOrWhiteSpace(s) ? DefaultUrl : s.Trim().TrimEnd('/');
            }
            set { Save("comfy_url.txt", (value ?? "").Trim().TrimEnd('/')); }
        }

        // ComfyUI の「Export (API)」で保存したワークフロー JSON
        public static string WorkflowPath
        {
            get { return Load("comfy_workflow.txt"); }
            set { Save("comfy_workflow.txt", (value ?? "").Trim()); }
        }

        // 前回のプロンプトと秒数 (「動画にする」のダイアログの初期値)
        public static string Prompt
        {
            get { return Load("comfy_prompt.txt") ?? ""; }
            set { Save("comfy_prompt.txt", (value ?? "").Trim()); }
        }

        public static string Seconds
        {
            get { return Load("comfy_seconds.txt") ?? ""; }
            set { Save("comfy_seconds.txt", (value ?? "").Trim()); }
        }

        private static string FilePath(string name)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "J-Rate");
            return Path.Combine(dir, name);
        }

        private static string Load(string name)
        {
            try
            {
                string f = FilePath(name);
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch { return null; }
        }

        private static void Save(string name, string value)
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
    // ComfyUI の HTTP API を使った動画生成
    //   1. POST /upload/image  で画像を送る
    //   2. ワークフローの LoadImage ノードの画像名を差し替え、シードをランダムにする
    //      (プロンプトと秒数も、指定があれば差し替える)
    //   3. POST /prompt        で実行を依頼
    //   4. GET  /history/{id}  で完了を待つ (待ち中は GET /queue で順番を表示)
    //   5. GET  /view          で出来た動画を受け取り、元画像と同じフォルダに保存
    // ------------------------------------------------------------------
    public static class ComfyVideo
    {
        private static readonly string[] VideoExts = { ".mp4", ".m4v", ".webm", ".mov", ".mkv", ".gif", ".webp" };
        private static readonly Random Rng = new Random();

        // prompt / seconds が null のときはワークフローに書かれている値のまま。
        // 戻り値 : 保存した動画のパス。失敗したときは例外 (メッセージは日本語)。
        public static string Generate(string imagePath, string baseUrl, string workflowPath,
                                      string prompt, double? seconds, Action<string> progress)
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
            progress("ComfyUI へ画像を送信中...");
            string uploadName = Upload(baseUrl, imagePath, json);

            foreach (var node in loadNodes) Inputs(node)["image"] = uploadName;
            RandomizeSeeds(workflow);
            if (prompt != null)
                foreach (var slot in PromptSlots(workflow)) slot.Key[slot.Value] = prompt;
            if (seconds != null) SetDuration(workflow, seconds.Value);

            // 実行を依頼
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
                Thread.Sleep(2000);
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
            var output = FindVideoOutput(entry);
            if (output == null)
                throw new Exception("ワークフローの出力に動画ファイルが見つかりません。\n" +
                                    "Video Combine / Save Video などの保存ノードがあるか確認してください。");

            progress("動画を受け取り中...");
            string dest = UniquePath(Path.Combine(Path.GetDirectoryName(imagePath),
                                     Path.GetFileNameWithoutExtension(imagePath) + "_video" + Path.GetExtension(output.Filename)));
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
            return dest;
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
        }

        // 出力ノードの結果 (gifs / videos / images など) から動画ファイルを 1 つ選ぶ。mp4 を優先
        private static OutputFile FindVideoOutput(Dictionary<string, object> entry)
        {
            object o;
            if (!entry.TryGetValue("outputs", out o)) return null;
            var outputs = o as Dictionary<string, object>;
            if (outputs == null) return null;

            var found = new List<OutputFile>();
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
                        if (Array.IndexOf(VideoExts, ext) < 0) continue;
                        f.TryGetValue("subfolder", out sub);
                        f.TryGetValue("type", out type);
                        found.Add(new OutputFile
                        {
                            Filename = fn.ToString(),
                            Subfolder = sub != null ? sub.ToString() : "",
                            Type = type != null ? type.ToString() : "output"
                        });
                    }
                }
            }
            // 優先順位 : output フォルダ > temp、拡張子は VideoExts の並び順 (mp4 が先頭)
            return found.OrderBy(f => f.Type == "output" ? 0 : 1)
                        .ThenBy(f => Array.IndexOf(VideoExts, Path.GetExtension(f.Filename).ToLowerInvariant()))
                        .FirstOrDefault();
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
