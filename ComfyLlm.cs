// J-Rate : ローカル LLM を使う機能 (エンハンサー = AI で盛る、読み取り = 画像から説明プロンプトを作る)。
// 決まりは generation-standard.md 6 章「AI で盛ってから送る」「読み取り」。登録 (エンハンサー・読み取りプリセット・
// LLM の接続) は PromptWordCrafter・ImageMimicArt と共有する (%APPDATA%\GenCommon。ファイルの形も同じ)。
// .NET Framework 4.x 標準の csc.exe (C# 5) でビルドできるように書いている。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace JRate
{
    // ------------------------------------------------------------------
    // LLM に頼むときのプリセット (名前・指示文・送る文・temperature)。エンハンサーと読み取りで同じ作り
    // ------------------------------------------------------------------
    public class LlmPreset
    {
        public string Name = "", System = "", Template = "";
        public double Temperature = LlmPresets.DefaultTemperature;

        public bool IsEmpty
        {
            get { return System.Trim().Length == 0 && LlmPresets.DefaultTemplates.Contains(Template.Trim()); }
        }

        public string Label(int index)
        {
            return (index + 1) + ": " + (IsEmpty ? "(未登録)" : Name.Length > 0 ? Name : "(名前なし)");
        }

        public LlmPreset Clone()
        {
            return new LlmPreset { Name = Name, System = System, Template = Template, Temperature = Temperature };
        }
    }

    public class LlmException : Exception
    {
        public LlmException(string message) : base(message) { }
    }

    public static class LlmPresets
    {
        public const int Count = 20;
        public const double DefaultTemperature = 0.7;
        public const string PromptMark = "{prompt}";   // エンハンサーの送る文で、元のプロンプトに置き換わる目印
        public const string ReadTemplate = "Write a text-to-image prompt that recreates this image.";
        public static readonly string[] DefaultTemplates = { "", PromptMark, ReadTemplate };   // これだけで指示文がなければ未登録

        // file (共有の置き場所) を読む。まだなければ 1 番から例を入れておく
        public static LlmPreset[] Load(string file, LlmPreset[] examples, string defaultTemplate)
        {
            var list = new LlmPreset[Count];
            for (int i = 0; i < Count; i++) list[i] = new LlmPreset { Template = defaultTemplate };
            string text = ComfySettings.Load(file);
            if (text == null)
            {
                for (int i = 0; i < examples.Length && i < Count; i++) list[i] = examples[i].Clone();
                return list;
            }
            object[] items;
            try { items = ComfyVideo.NewSerializer().DeserializeObject(text) as object[]; }
            catch { return list; }
            for (int i = 0; items != null && i < items.Length && i < Count; i++)
            {
                var d = items[i] as Dictionary<string, object>;
                if (d == null) continue;
                object t;
                list[i] = new LlmPreset
                {
                    Name = ComfySettings.Str(d, "name"),
                    System = ComfySettings.Str(d, "system"),
                    Template = d.TryGetValue("template", out t) && t != null ? t.ToString() : defaultTemplate,
                    Temperature = ToTemperature(d.TryGetValue("temperature", out t) ? t : null),
                };
            }
            return list;
        }

        public static void Save(string file, LlmPreset[] list)
        {
            var arr = list.Take(Count).Select(p =>
            {
                var d = new Dictionary<string, object>();
                d["name"] = p.Name;
                d["system"] = p.System;
                d["template"] = p.Template;
                d["temperature"] = p.Temperature;
                return d;
            }).ToList();
            ComfySettings.Save(file, ComfyVideo.NewSerializer().Serialize(arr));
        }

        // 前回選んだ番号 (0 始まり。なし / 読めなければ -1)。ファイルには 1 始まり (0 = なし) で書く
        public static int LoadIndex(string file)
        {
            int n;
            return int.TryParse(ComfySettings.Load(file), out n) && n >= 1 && n <= Count ? n - 1 : -1;
        }

        public static void SaveIndex(string file, int index)
        {
            ComfySettings.Save(file, (index >= 0 ? index + 1 : 0).ToString());
        }

        // ジョブに持たせる、登録した時点のプリセットの写し (あとで設定を変えても、頼んだときの内容で)
        public static Dictionary<string, object> Snapshot(LlmPreset p, string url, string model)
        {
            var d = new Dictionary<string, object>();
            d["name"] = p.Name;
            d["system"] = p.System;
            d["template"] = p.Template;
            d["temperature"] = p.Temperature;
            d["url"] = url;
            d["model"] = model;
            return d;
        }

        public static LlmPreset FromSnapshot(Dictionary<string, object> d, string defaultTemplate)
        {
            object t;
            return new LlmPreset
            {
                Name = ComfySettings.Str(d, "name"),
                System = ComfySettings.Str(d, "system"),
                Template = d.TryGetValue("template", out t) && t != null ? t.ToString() : defaultTemplate,
                Temperature = ToTemperature(d.TryGetValue("temperature", out t) ? t : null),
            };
        }

        public static double ToTemperature(object o)
        {
            try { return o == null ? DefaultTemperature : Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return DefaultTemperature; }
        }

        private static readonly Regex Fence = new Regex(@"^```[^\n]*\n(.*?)\n?```$", RegexOptions.Singleline);

        // 指示しても付けてくることがある、全体を囲むコードブロックや引用符を外す
        public static string Clean(string text)
        {
            text = (text ?? "").Trim();
            var m = Fence.Match(text);
            if (m.Success) text = m.Groups[1].Value.Trim();
            string[] opens = { "\"", "“", "「" }, closes = { "\"", "”", "」" };
            for (int i = 0; i < opens.Length; i++)
            {
                if (text.Length >= 2 && text.StartsWith(opens[i]) && text.EndsWith(closes[i]))
                {
                    string inner = text.Substring(1, text.Length - 2);
                    if (!inner.Contains(opens[i]) && !inner.Contains(closes[i])) text = inner.Trim();
                    break;
                }
            }
            return text;
        }
    }

    // ------------------------------------------------------------------
    // LLM の接続 (読み取り・エンハンサー・PromptWordCrafter の翻訳で同じサーバー。モデルは別々。共有)
    // ------------------------------------------------------------------
    public static class LlmSettings
    {
        public const string DefaultUrl = "http://192.168.11.6:1234/v1";
        public const string DefaultModel = "qwen2.5-14b-instruct-uncensored";
        public const int DefaultMaxSide = 1024;

        public static string Url
        {
            get { return Nz(ComfySettings.Load("llm_url.txt")) ?? DefaultUrl; }
            set { ComfySettings.Save("llm_url.txt", (value ?? "").Trim().TrimEnd('/')); }
        }

        public static string ReaderModel
        {
            get { return Nz(ComfySettings.Load("reader_model.txt")) ?? Nz(ComfySettings.Load("enhance_model.txt")) ?? DefaultModel; }
            set { ComfySettings.Save("reader_model.txt", (value ?? "").Trim()); }
        }

        public static string EnhanceModel
        {
            get { return Nz(ComfySettings.Load("enhance_model.txt")) ?? Nz(ComfySettings.Load("reader_model.txt")) ?? DefaultModel; }
            set { ComfySettings.Save("enhance_model.txt", (value ?? "").Trim()); }
        }

        public static int MaxSide
        {
            get
            {
                double n;
                return double.TryParse(ComfySettings.Load("reader_max_side.txt"), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out n) && n >= 256 && n <= 4096
                    ? (int)n : DefaultMaxSide;
            }
            set { ComfySettings.Save("reader_max_side.txt", value.ToString()); }
        }

        private static string Nz(string s) { return string.IsNullOrEmpty(s) ? null : s; }
    }

    // ------------------------------------------------------------------
    // ローカル LLM (LM Studio などの OpenAI 互換 API) の呼び出し
    // ------------------------------------------------------------------
    public static class Llm
    {
        private const int TimeoutMs = 180000;   // ローカル LLM は長文だと時間がかかる
        private static readonly Regex Think = new Regex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static Dictionary<string, object> RequestJson(string url, object payload, int timeout)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.Timeout = timeout;
            req.ReadWriteTimeout = timeout;
            req.Headers["Authorization"] = "Bearer lm-studio";
            if (payload != null)
            {
                req.Method = "POST";
                req.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes(json.Serialize(payload));
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            }
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                {
                    var d = json.DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
                    if (d == null) throw new LlmException("サーバーの応答を読めませんでした。");
                    return d;
                }
            }
            catch (WebException ex)
            {
                var res = ex.Response as HttpWebResponse;
                if (res == null) throw new LlmException("サーバーに接続できません: " + ex.Message);
                string detail = "";
                try { using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) detail = r.ReadToEnd().Trim(); } catch { }
                string code = ((int)res.StatusCode).ToString();
                if (LooksLikeNoVision(detail))
                    throw new LlmException("このモデルは画像を受け付けませんでした。読み取りにはビジョン対応のモデル " +
                                           "(qwen2.5-vl、gemma3 など) が必要です。「読み取りの設定」でモデルを替えてください。\n(" +
                                           code + ") " + Short(detail));
                if (detail.Contains("No models loaded"))
                    throw new LlmException("指定したモデルを使えません。モデル名がサーバーにあるか確かめてください (設定の「モデル取得」で一覧から選べます)。");
                throw new LlmException("サーバーがエラーを返しました (" + code + ") " + Short(detail));
            }
        }

        private static string Short(string s) { return s.Length > 300 ? s.Substring(0, 300) : s; }

        private static bool LooksLikeNoVision(string detail)
        {
            string t = detail.ToLowerInvariant();
            return new[] { "vision", "image input", "images are not supported", "does not support image", "multimodal" }.Any(t.Contains);
        }

        // system と user を 1 往復送り、返事の本文を返す。imageUrl (data:image/jpeg;base64,...) を渡すと画像も見せる
        public static string Chat(string baseUrl, string model, string system, string user, double temperature, string imageUrl)
        {
            var messages = new List<object>();
            if (system.Trim().Length > 0) messages.Add(new Dictionary<string, object> { { "role", "system" }, { "content", system } });
            if (!string.IsNullOrEmpty(imageUrl))
            {
                var content = new List<object>
                {
                    new Dictionary<string, object> { { "type", "text" }, { "text", user } },
                    new Dictionary<string, object> { { "type", "image_url" }, { "image_url", new Dictionary<string, object> { { "url", imageUrl } } } },
                };
                messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", content } });
            }
            else messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", user } });
            var payload = new Dictionary<string, object> { { "model", model }, { "messages", messages }, { "temperature", temperature } };
            var body = RequestJson(baseUrl.TrimEnd('/') + "/chat/completions", payload, TimeoutMs);
            try
            {
                var choice = ((object[])body["choices"])[0] as Dictionary<string, object>;
                var message = choice["message"] as Dictionary<string, object>;
                object c;
                string text = message.TryGetValue("content", out c) && c != null ? c.ToString() : "";
                return Think.Replace(text, "").Trim();   // 考える過程を出すモデルは、その部分を除く
            }
            catch (Exception) { throw new LlmException("サーバーの応答を読めませんでした。"); }
        }

        public static List<string> ListModels(string baseUrl)
        {
            var body = RequestJson(baseUrl.TrimEnd('/') + "/models", null, 15000);
            try
            {
                object data;
                body.TryGetValue("data", out data);
                return ((object[])data ?? new object[0]).OfType<Dictionary<string, object>>()
                       .Select(d => ComfySettings.Str(d, "id")).Where(s => s.Length > 0).OrderBy(s => s, StringComparer.Ordinal).ToList();
            }
            catch (Exception) { throw new LlmException("モデル一覧を読めませんでした。"); }
        }

        // モデル一覧を取ったとき欄に出す名前。今の名前がサーバーになければ、一覧の先頭 (埋め込み用は除く)
        public static string PickModel(string current, List<string> models)
        {
            if (models.Contains(current) || models.Count == 0) return current;
            var chat = models.Where(m => m.ToLowerInvariant().IndexOf("embed") < 0).ToList();
            return (chat.Count > 0 ? chat : models)[0];
        }

        // LLM の返事を待つ。待つ間もキャンセルが効く (返事は捨てる)
        public static string Wait(Func<string> call, CancellationToken cancel)
        {
            var task = Task.Run(call);
            while (!task.Wait(200)) cancel.ThrowIfCancellationRequested();
            cancel.ThrowIfCancellationRequested();
            return task.Result;
        }
    }

    // ------------------------------------------------------------------
    // エンハンサー (AI で盛る)
    // ------------------------------------------------------------------
    public static class Enhancer
    {
        public const string File = "enhancer_presets.json";      // 共有
        public const string GenerateFile = "enhancer_generate.txt";   // 生成の画面で前回選んだ番号 (0 = なし)

        private static readonly LlmPreset[] Examples =
        {
            new LlmPreset
            {
                Name = "例: 自然文 (英語)",
                System = "You are a prompt engineer for text-to-image and text-to-video models.\n" +
                         "Rewrite the user's prompt into one richly detailed English description in natural sentences: " +
                         "subject, appearance, action, setting, lighting, camera and style.\n" +
                         "- Keep every subject and the intent of the original. Do not change who or what is depicted.\n" +
                         "- The input may be in Japanese or a list of tags; the output must always be English prose.\n" +
                         "- Keep weighting syntax such as (word:1.2) and special tags such as <lora:...> exactly as written.\n" +
                         "Output only the rewritten prompt, with no explanation, title or quotation marks.",
                Template = LlmPresets.PromptMark,
            },
            new LlmPreset
            {
                Name = "例: タグ (カンマ区切り)",
                System = "You are a prompt engineer for anime-style text-to-image models that use comma-separated tags.\n" +
                         "Expand the user's prompt into a comma-separated list of English tags: keep the original tags first, " +
                         "then add tags for details, composition, lighting and quality.\n" +
                         "- Keep every subject and the intent of the original.\n" +
                         "- The input may be in Japanese; translate it into tags.\n" +
                         "- Keep weighting syntax such as (word:1.2) and special tags such as <lora:...> exactly as written.\n" +
                         "Output only the tag list on one line, with no explanation.",
                Template = LlmPresets.PromptMark,
            },
        };

        public static LlmPreset[] Load() { return LlmPresets.Load(File, Examples, LlmPresets.PromptMark); }

        public static Dictionary<string, object> Snapshot(LlmPreset p)
        {
            return LlmPresets.Snapshot(p, LlmSettings.Url, LlmSettings.EnhanceModel);
        }

        // 雛形の {prompt} を元のプロンプトに置き換える。目印がなければ雛形のあとに付ける
        public static string UserMessage(string template, string text)
        {
            if (template.Contains(LlmPresets.PromptMark)) return template.Replace(LlmPresets.PromptMark, text);
            return template.Trim().Length > 0 ? template.TrimEnd() + "\n\n" + text : text;
        }

        public static string EnhanceWith(Dictionary<string, object> snap, string text)
        {
            var p = LlmPresets.FromSnapshot(snap, LlmPresets.PromptMark);
            return LlmPresets.Clean(Llm.Chat(ComfySettings.Str(snap, "url"), ComfySettings.Str(snap, "model"),
                                             p.System, UserMessage(p.Template, text), p.Temperature, null));
        }
    }

    // ------------------------------------------------------------------
    // 読み取り (元画像 → 説明プロンプト)。説明は元画像と同じフォルダの、拡張子を除いた名前の .txt
    // ------------------------------------------------------------------
    public static class Reader
    {
        public const string File = "reader_presets.json";        // 共有
        public const string GenerateFile = "reader_generate.txt";    // 生成の画面で前回選んだ番号 (0 = なし)
        public const string PanelFile = "reader_preset.txt";        // 説明プロンプトの小窓で選んでいる番号
        public const string RereadFile = "reader_reread.txt";       // 「読み取り済みでも読み直す」
        public const string CaptionMark = "{caption}";              // 入力したプロンプトで、説明に置き換わる目印
        public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".jpe", ".webp", ".bmp" };

        private static readonly LlmPreset[] Examples =
        {
            new LlmPreset
            {
                Name = "例: 自然文の詳しい説明 (英語)",
                System = "You are an expert at writing prompts for text-to-image models.\n" +
                         "Look at the image and write one detailed English prompt that would recreate it as closely as possible: " +
                         "the subject (appearance, clothing, pose, expression), setting and background, composition and framing, " +
                         "lighting, colors, camera and lens feel, and the art style or medium.\n" +
                         "- Describe only what is visible. Do not invent a story.\n" +
                         "- Write natural sentences in a single paragraph.\n" +
                         "Output only the prompt, with no explanation, title or quotation marks.",
                Template = LlmPresets.ReadTemplate,
                Temperature = 0.4,
            },
            new LlmPreset
            {
                Name = "例: タグ (カンマ区切り)",
                System = "You are an expert at tagging images for anime-style text-to-image models.\n" +
                         "Look at the image and list English tags that would recreate it: subject and count, appearance, " +
                         "clothing, pose, expression, background, composition, lighting and style.\n" +
                         "- Use short lowercase tags separated by commas, most important first.\n" +
                         "Output only the tag list on one line, with no explanation.",
                Template = "List the tags for this image.",
                Temperature = 0.4,
            },
        };

        public static LlmPreset[] Load() { return LlmPresets.Load(File, Examples, LlmPresets.ReadTemplate); }

        public static Dictionary<string, object> Snapshot(LlmPreset p)
        {
            var d = LlmPresets.Snapshot(p, LlmSettings.Url, LlmSettings.ReaderModel);
            d["max_side"] = LlmSettings.MaxSide;
            return d;
        }

        public static bool Reread
        {
            get { return ComfySettings.Load(RereadFile) == "1"; }
            set { ComfySettings.Save(RereadFile, value ? "1" : "0"); }
        }

        // ---- 説明プロンプトのファイル ----
        public static string CaptionPath(string image)
        {
            return Path.Combine(Path.GetDirectoryName(image), Path.GetFileNameWithoutExtension(image) + ".txt");
        }

        // .txt を読む (なければ null)。文字コードは PromptWordCrafter と同じ順で判定する
        public static string ReadCaption(string image)
        {
            byte[] data;
            try { data = System.IO.File.ReadAllBytes(CaptionPath(image)); }
            catch { return null; }
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            foreach (var enc in new Encoding[]
            {
                new UTF8Encoding(false, true),
                Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                new UnicodeEncoding(false, true, true),
            })
            {
                try { return enc.GetString(data); }
                catch (DecoderFallbackException) { }
                catch (ArgumentException) { }
            }
            return Encoding.GetEncoding(28591).GetString(data);   // latin-1
        }

        // UTF-8 (BOM なし) で書く。失敗したら例外
        public static void WriteCaption(string image, string text)
        {
            System.IO.File.WriteAllText(CaptionPath(image), text, new UTF8Encoding(false));
        }

        // 同じ名前の別の画像 (photo.png と photo.jpg など) と .txt を共用しているときの知らせ (なければ空)
        public static string SharingNote(string image)
        {
            string ext = Path.GetExtension(image).ToLowerInvariant();
            var others = ImageExts.Where(e => e != ext).Select(e => Path.ChangeExtension(image, e))
                                  .Where(System.IO.File.Exists).Select(Path.GetFileName).ToList();
            return others.Count == 0 ? "" : " ※ " + Path.GetFileName(CaptionPath(image)) + " は " + string.Join("、", others) + " とも共用です";
        }

        // 画像と一緒に動かす (削除・コピー・移動する) .txt。同じ名前の別の画像が元のフォルダに残るものは除く
        // (ui-standard 8 章「説明プロンプトの .txt」)。stays : 残すべき .txt (移動ではコピーにする)
        public static List<string> Companions(IEnumerable<string> images, out HashSet<string> stays)
        {
            var going = new HashSet<string>(images, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            stays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string image in going)
            {
                string txt = CaptionPath(image);
                if (!seen.Add(txt) || !System.IO.File.Exists(txt)) continue;
                string ext = Path.GetExtension(image).ToLowerInvariant();
                bool other = ImageExts.Where(e => e != ext).Select(e => Path.ChangeExtension(image, e))
                                      .Any(p => !going.Contains(p) && System.IO.File.Exists(p));
                result.Add(txt);
                if (other) stays.Add(txt);
            }
            return result;
        }

        // 画像を長い辺 maxSide まで縮めた JPEG にして data URL で返す (元より大きくはしない。透過は白で埋める)
        public static string ImageDataUrl(string path, int maxSide)
        {
            try
            {
                using (var src = System.Drawing.Image.FromFile(path))
                {
                    // 撮影時の向き (EXIF Orientation) に合わせる
                    const int OrientationId = 0x0112;
                    if (src.PropertyIdList.Contains(OrientationId))
                    {
                        int o = src.GetPropertyItem(OrientationId).Value[0];
                        var flip = o == 2 ? System.Drawing.RotateFlipType.RotateNoneFlipX
                                 : o == 3 ? System.Drawing.RotateFlipType.Rotate180FlipNone
                                 : o == 4 ? System.Drawing.RotateFlipType.Rotate180FlipX
                                 : o == 5 ? System.Drawing.RotateFlipType.Rotate90FlipX
                                 : o == 6 ? System.Drawing.RotateFlipType.Rotate90FlipNone
                                 : o == 7 ? System.Drawing.RotateFlipType.Rotate270FlipX
                                 : o == 8 ? System.Drawing.RotateFlipType.Rotate270FlipNone
                                 : System.Drawing.RotateFlipType.RotateNoneFlipNone;
                        src.RotateFlip(flip);
                    }
                    double scale = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
                    int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
                    using (var bmp = new System.Drawing.Bitmap(w, h))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.Clear(System.Drawing.Color.White);
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                        var prm = new System.Drawing.Imaging.EncoderParameters(1);
                        prm.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, codec, prm);
                            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new LlmException("画像を読めません: " + Path.GetFileName(path) + " (" + ex.Message + ")");
            }
        }

        // 写し (Snapshot) で読み取る。空なら例外
        public static string ReadWith(Dictionary<string, object> snap, string image)
        {
            var p = LlmPresets.FromSnapshot(snap, LlmPresets.ReadTemplate);
            int maxSide = LlmSettings.DefaultMaxSide;
            object o;
            if (snap.TryGetValue("max_side", out o) && o != null) { try { maxSide = Convert.ToInt32(o); } catch { } }
            string reply = Llm.Chat(ComfySettings.Str(snap, "url"), ComfySettings.Str(snap, "model"), p.System,
                                    p.Template.Trim().Length > 0 ? p.Template : LlmPresets.ReadTemplate,
                                    p.Temperature, ImageDataUrl(image, maxSide));
            string text = LlmPresets.Clean(reply);
            if (text.Length == 0) throw new LlmException("読み取った結果が空でした。");
            return text;
        }

        // 入力したプロンプトと説明を組み立てる (generation-standard 6 章「読み取り」の J-Rate)。
        // {caption} があればそこを説明に置き換え、なければ「説明 + 改行 + 入力」。入力が空なら説明だけ
        public static string Compose(string prompt, string caption)
        {
            prompt = prompt ?? "";
            if (caption == null) return prompt.Replace(CaptionMark, "").Trim();
            caption = caption.Trim();
            if (prompt.Contains(CaptionMark)) return prompt.Replace(CaptionMark, caption).Trim();
            return prompt.Trim().Length == 0 ? caption : caption + "\n" + prompt.Trim();
        }
    }

    // ------------------------------------------------------------------
    // プリセットの番号を選ぶ欄、名前・temperature・指示文・送る文、「プリセットを保存」「削除」「▲」「▼」。
    // 共有の一覧なので、書く前に読み直してその枠だけを変え、並べ替えたらすべてのアプリの番号を直す
    // ------------------------------------------------------------------
    public class LlmPresetEditor : StackPanel
    {
        public LlmPreset[] Presets;
        public int Shown = -1;
        private readonly string file, defaultTemplate;
        private readonly Func<LlmPreset[]> load;
        private readonly ComboBox box = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
        private readonly TextBox nameBox = new TextBox { Padding = new Thickness(2, 3, 2, 3) };
        private readonly TextBox tempBox = new TextBox { Width = 60, Padding = new Thickness(2, 3, 2, 3), HorizontalContentAlignment = HorizontalAlignment.Right };
        private readonly TextBox systemBox = Multi(150), templateBox = Multi(60);
        private bool relabeling;

        private static TextBox Multi(double height)
        {
            return new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = height, Padding = new Thickness(2, 3, 2, 3), Margin = new Thickness(0, 4, 0, 8)
            };
        }

        public LlmPresetEditor(string caption, string file, string defaultTemplate, Func<LlmPreset[]> load,
                               string systemCaption, string templateCaption, string templateTip, string tempTip, int index)
        {
            this.file = file;
            this.defaultTemplate = defaultTemplate;
            this.load = load;
            Presets = load();
            Children.Add(new TextBlock { Text = caption });
            string offline = ComfySettings.OfflineMessage;
            if (offline.Length > 0)   // 別の PC と共有している置き場所に繋がらない (保存できない)
                Children.Add(new TextBlock { Text = offline, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Margin = new Thickness(0, 2, 0, 4) });
            for (int i = 0; i < Presets.Length; i++) box.Items.Add(Presets[i].Label(i));
            Children.Add(box);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var tempRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
            tempRow.Children.Add(new TextBlock { Text = "temperature", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            tempBox.ToolTip = tempTip;
            tempRow.Children.Add(tempBox);
            DockPanel.SetDock(tempRow, Dock.Right);
            row.Children.Add(tempRow);
            row.Children.Add(new TextBlock { Text = "名前", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(nameBox);
            Children.Add(row);
            Children.Add(new TextBlock { Text = systemCaption });
            Children.Add(systemBox);
            Children.Add(new TextBlock { Text = templateCaption });
            templateBox.ToolTip = templateTip;
            Children.Add(templateBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var up = MoveButton("▲ 上へ", -1);
            var down = MoveButton("▼ 下へ", 1);
            var save = new Button { Content = "プリセットを保存", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(24, 0, 0, 0) };
            var del = new Button { Content = "削除", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
            var bulk = new Button
            {
                Content = BulkEdit.ButtonText, Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0),
                ToolTip = BulkEdit.ButtonTip
            };
            bulk.Click += delegate { BulkEditPresets(); };
            buttons.Children.Add(up);
            buttons.Children.Add(down);
            buttons.Children.Add(save);
            buttons.Children.Add(del);
            buttons.Children.Add(bulk);
            Children.Add(buttons);

            box.SelectionChanged += delegate
            {
                if (relabeling || box.SelectedIndex < 0) return;
                Shown = box.SelectedIndex;   // 番号を選ぶと中身を欄に出す (保存していない入力は捨てる)
                ShowPreset(Presets[Shown]);
            };
            save.Click += delegate { Store(); };
            del.Click += delegate
            {
                if (!Presets[Shown].IsEmpty &&
                    MessageBox.Show(Window.GetWindow(this), "プリセット " + (Shown + 1) + " を削除しますか？\n\n" + Presets[Shown].Name,
                                    "削除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;
                Reload();
                Presets[Shown] = new LlmPreset { Template = defaultTemplate };
                ShowPreset(Presets[Shown]);
                Saved();
            };
            box.SelectedIndex = Math.Min(Math.Max(index, 0), Presets.Length - 1);
        }

        private RepeatButton MoveButton(string text, int step)
        {
            var b = new RepeatButton
            {
                Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(step < 0 ? 0 : 8, 0, 0, 0), Delay = 400, Interval = 120,
                ToolTip = "選んでいるプリセットを 1 つ" + (step < 0 ? "上" : "下") + "の番号と入れ替えます (押し続けると続けて動きます)"
            };
            b.Click += delegate { Move(step); };
            return b;
        }

        private void ShowPreset(LlmPreset p)
        {
            nameBox.Text = p.Name;
            systemBox.Text = p.System;
            templateBox.Text = p.Template;
            tempBox.Text = p.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private LlmPreset Edited()
        {
            double t;
            if (!double.TryParse(tempBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t)
                || t < 0 || t > 2) t = LlmPresets.DefaultTemperature;
            return new LlmPreset
            {
                Name = nameBox.Text.Trim(), System = systemBox.Text.Trim(),
                Template = templateBox.Text.Trim().Length > 0 ? templateBox.Text.Trim() : defaultTemplate,
                Temperature = Math.Round(t, 2),
            };
        }

        // ほかのアプリで変えたものを消さないよう、書く前に読み直す (まだなければ今の内容のまま)
        private void Reload()
        {
            if (ComfySettings.Load(file) == null) return;
            var fresh = load();
            for (int i = 0; i < Presets.Length && i < fresh.Length; i++) Presets[i] = fresh[i];
        }

        private void Store()
        {
            var edited = Edited();
            Reload();
            Presets[Shown] = edited;
            Saved();
        }

        // まとめて削除・並べ替え。すべてのアプリの番号 (前回選んだもの) も並べ替えに合わせる
        private void BulkEditPresets()
        {
            Reload();
            Saved();
            var items = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < Presets.Length; i++)
                if (!Presets[i].IsEmpty) items.Add(new KeyValuePair<int, string>(i, Presets[i].Label(i)));
            var result = BulkEditDialog.Ask(Window.GetWindow(this), "プリセット", items, "プリセットは 3 つのアプリで共有しています。");
            if (result == null) return;
            Reload();
            Dictionary<int, int> mapping;
            var arranged = BulkEdit.Apply(Presets, items.Select(kv => kv.Key).ToList(), result,
                                          () => new LlmPreset { Template = defaultTemplate }, p => p.IsEmpty, out mapping);
            for (int i = 0; i < Presets.Length; i++) Presets[i] = arranged[i];
            Shown = BulkEdit.Remap(Shown, mapping);
            ShowPreset(Presets[Shown]);
            Saved();
            ComfySettings.RemapIndexes(file, mapping);
        }

        private void Saved()
        {
            relabeling = true;
            for (int i = 0; i < Presets.Length; i++) box.Items[i] = Presets[i].Label(i);
            box.SelectedIndex = Shown;
            relabeling = false;
            LlmPresets.Save(file, Presets);
        }

        // 隣の番号と入れ替えて、すぐ保存する。欄の内容 (保存していない入力も) はそのまま付いていく
        private void Move(int step)
        {
            int a = Shown, b = Shown + step;
            if (a < 0 || b < 0 || b >= Presets.Length) return;
            Reload();
            var t = Presets[a]; Presets[a] = Presets[b]; Presets[b] = t;
            Shown = b;
            Saved();
            ComfySettings.SwapIndexes(file, a, b);
        }
    }

    // ------------------------------------------------------------------
    // 「読み取りの設定」「エンハンサーの設定」: サーバーURL・モデル (・送る画像の大きさ) は OK で決まり、
    // プリセットはその場で保存する (キャンセルしても残る)
    // ------------------------------------------------------------------
    public class LlmSettingsDialog : Window
    {
        public readonly LlmPresetEditor Editor;
        private readonly TextBox urlBox = new TextBox { Padding = new Thickness(2, 3, 2, 3) };
        private readonly ComboBox modelBox = new ComboBox { IsEditable = true };
        private readonly TextBox sideBox = new TextBox { Width = 70, Padding = new Thickness(2, 3, 2, 3), HorizontalContentAlignment = HorizontalAlignment.Right };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) };
        private readonly bool reader;

        public LlmSettingsDialog(bool reader, int index)
        {
            this.reader = reader;
            Title = reader ? "読み取りの設定" : "エンハンサーの設定";
            Width = 680;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var panel = new StackPanel { Margin = new Thickness(16) };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            urlBox.Text = LlmSettings.Url;
            urlBox.ToolTip = "LM Studio などの OpenAI 互換 API (例: http://127.0.0.1:1234/v1)。読み取り・エンハンサー・翻訳で同じものを使います (3 つのアプリで共有)";
            var test = new Button { Content = "接続テスト", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 6) };
            AddRow(grid, 0, "サーバーURL", urlBox, test);
            modelBox.Text = reader ? LlmSettings.ReaderModel : LlmSettings.EnhanceModel;
            var fetch = new Button { Content = "モデル取得", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 6) };
            AddRow(grid, 1, reader ? "読み取り用のモデル" : "LLM のモデル", modelBox, fetch);
            if (reader)
            {
                sideBox.Text = LlmSettings.MaxSide.ToString();
                var sideRow = new StackPanel { Orientation = Orientation.Horizontal };
                sideRow.Children.Add(sideBox);
                sideRow.Children.Add(new TextBlock { Text = " px (長い辺をこの大きさまで縮めて送ります。元より大きくはしません)", VerticalAlignment = VerticalAlignment.Center });
                AddRow(grid, 2, "送る画像の大きさ", sideRow, null);
            }
            panel.Children.Add(grid);
            status.Text = reader ? "読み取りにはビジョン対応のモデル (qwen2.5-vl、gemma3 など) を選んでください。"
                                 : "サーバーURL は読み取り・翻訳と同じものを使います (3 つのアプリで共有)。";
            panel.Children.Add(status);

            Editor = reader
                ? new LlmPresetEditor("読み取りプリセット (画像をどう説明させるか。3 つのアプリで共有)", Reader.File, LlmPresets.ReadTemplate, Reader.Load,
                                      "指示文 (system) — どんな説明プロンプトを書かせるか", "送る文 (user) — 画像と一緒に送る文",
                                      "画像と一緒に送る短い頼みの文です。空なら既定の文を送ります。", "低いほど毎回同じような説明になります (目安 0.2〜0.7)", index)
                : new LlmPresetEditor("エンハンサーのプリセット (生成 AI のモデルごとに登録します。3 つのアプリで共有)", Enhancer.File, LlmPresets.PromptMark, Enhancer.Load,
                                      "指示文 (system) — モデルごとに推奨されている指示文を貼ります",
                                      "送る文の雛形 (user) — " + LlmPresets.PromptMark + " が元のプロンプト (盛る前) に置き換わります",
                                      "ふつうは " + LlmPresets.PromptMark + " だけで十分です。" + LlmPresets.PromptMark + " がないときは、雛形のあとに元のプロンプトを付けて送ります。\n"
                                      + LlmPresets.PromptMark + " の前後に書いた文も AI に送られるので、盛ったプロンプトに混ざったり、言い換えられたり、"
                                      + "落とされたりします。必ずそのまま付けたい語はベースプロンプトに書きます (盛ったあとに付きます)。",
                                      "高いほど毎回違う盛り方になります (目安 0.5〜1.0)", index);
            panel.Children.Add(Editor);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
            var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;

            test.Click += delegate { FetchModels(); };   // 一覧が取れればつながっている
            fetch.Click += delegate { FetchModels(); };
            ok.Click += delegate
            {
                string url = urlBox.Text.Trim().TrimEnd('/');
                if (url.Length > 0) LlmSettings.Url = url;
                string model = modelBox.Text.Trim();
                if (model.Length > 0) { if (reader) LlmSettings.ReaderModel = model; else LlmSettings.EnhanceModel = model; }
                int side;
                if (reader && int.TryParse(sideBox.Text.Trim(), out side) && side >= 256 && side <= 4096) LlmSettings.MaxSide = side;
                DialogResult = true;
            };
        }

        private static void AddRow(Grid grid, int row, string caption, FrameworkElement field, FrameworkElement side)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) };
            field.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(c, row);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(c);
            grid.Children.Add(field);
            if (side != null)
            {
                Grid.SetRow(side, row);
                Grid.SetColumn(side, 2);
                grid.Children.Add(side);
            }
        }

        private async void FetchModels()
        {
            string url = urlBox.Text.Trim().TrimEnd('/');
            status.Text = "モデル一覧を取得中...";
            try
            {
                var models = await Task.Run(() => Llm.ListModels(url));
                if (models.Count == 0) { status.Text = "モデルが見つかりませんでした。"; return; }
                string current = modelBox.Text.Trim();
                string picked = Llm.PickModel(current, models);
                modelBox.Items.Clear();
                foreach (var m in models) modelBox.Items.Add(m);
                modelBox.Text = picked;
                status.Text = "モデル " + models.Count + " 件を取得しました (接続できています)。" +
                              (picked != current ? "「" + current + "」はサーバーにないので「" + picked + "」にしました。" : "");
            }
            catch (Exception ex) { status.Text = "モデル取得失敗: " + ex.Message; }
        }
    }

    // ------------------------------------------------------------------
    // 右クリック「説明プロンプト...」: 元画像の .txt を見る・直す (Ctrl+S)・読み取る (Ctrl+R)。
    // 読み取り中に閉じても裏で続け、終わったら .txt に書いて notify で知らせる
    // ------------------------------------------------------------------
    public class CaptionWindow : Window
    {
        private readonly string image;
        private readonly Action<string> notify;
        private readonly TextBox textBox;
        private readonly ComboBox readerBox = new ComboBox { MinWidth = 260 };
        private readonly Button readButton, saveButton;
        private readonly TextBlock info = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) };
        private LlmPreset[] readers;
        private readonly List<int> readerIndex = new List<int>();
        private string saved;      // 最後に保存した (読んだ) 内容
        private bool reading;

        public CaptionWindow(string image, Action<string> notify)
        {
            this.image = image;
            this.notify = notify;
            Title = "説明プロンプト - " + Path.GetFileName(image);
            Width = 640;
            Height = 460;
            MinWidth = 420;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };
            var top = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            top.Text = Reader.CaptionPath(image) + Reader.SharingNote(image);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var readRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            readRow.Children.Add(new TextBlock { Text = "読み取りプリセット", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            readRow.Children.Add(readerBox);
            readButton = new Button { Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
            readRow.Children.Add(readButton);
            var settings = new Button { Content = "設定...", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), ToolTip = "読み取りの設定 (サーバーURL、モデル、読み取りプリセット)" };
            readRow.Children.Add(settings);
            DockPanel.SetDock(readRow, Dock.Top);
            root.Children.Add(readRow);

            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            var close = new Button { Content = "閉じる", Width = 90, IsCancel = true };
            saveButton = new Button { Content = "保存 (Ctrl+S)", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(0, 0, 8, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(saveButton);
            right.Children.Add(close);
            DockPanel.SetDock(right, Dock.Right);
            bottom.Children.Add(right);
            bottom.Children.Add(info);
            root.Children.Add(bottom);

            textBox = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(2, 3, 2, 3)
            };
            root.Children.Add(textBox);
            Content = root;

            saved = Reader.ReadCaption(image);
            textBox.Text = saved ?? "";
            FillReaders(LlmPresets.LoadIndex(Reader.PanelFile));
            UpdateButtons();

            textBox.TextChanged += delegate { UpdateButtons(); };
            readerBox.SelectionChanged += delegate { if (readerBox.SelectedIndex >= 0 && readerIndex.Count > 0) LlmPresets.SaveIndex(Reader.PanelFile, readerIndex[readerBox.SelectedIndex]); };
            saveButton.Click += delegate { Save(); };
            readButton.Click += delegate { Read(); };
            close.Click += delegate { Close(); };
            settings.Click += delegate
            {
                int cur = CurrentReader();
                var dlg = new LlmSettingsDialog(true, Math.Max(cur, 0)) { Owner = this };
                dlg.ShowDialog();
                FillReaders(cur);
            };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                if (e.Key == Key.S) { e.Handled = true; Save(); }
                else if (e.Key == Key.R) { e.Handled = true; Read(); }
            };
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!Dirty) return;
                var r = MessageBox.Show(this, "説明プロンプトを保存していません。保存しますか？", Title,
                                        MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) e.Cancel = true;
                else if (r == MessageBoxResult.Yes && !Save()) e.Cancel = true;
            };
            Loaded += delegate { textBox.Focus(); };
        }

        private bool Dirty { get { return !reading && textBox.Text != (saved ?? ""); } }

        private void FillReaders(int index)
        {
            readers = Reader.Load();
            readerIndex.Clear();
            readerBox.Items.Clear();
            for (int i = 0; i < readers.Length; i++)
            {
                if (readers[i].IsEmpty) continue;
                readerBox.Items.Add(readers[i].Label(i));
                readerIndex.Add(i);
            }
            if (readerIndex.Count == 0) readerBox.Items.Add("(読み取りプリセットが未登録です。「設定...」で登録してください)");
            int pos = readerIndex.IndexOf(index);
            readerBox.SelectedIndex = pos >= 0 ? pos : 0;
            UpdateButtons();
        }

        private int CurrentReader()
        {
            return readerIndex.Count == 0 || readerBox.SelectedIndex < 0 ? -1 : readerIndex[readerBox.SelectedIndex];
        }

        private void UpdateButtons()
        {
            readButton.Content = saved != null ? "読み直す (Ctrl+R)" : "読み取る (Ctrl+R)";
            readButton.IsEnabled = !reading && CurrentReader() >= 0;
            saveButton.IsEnabled = !reading && Dirty;
            textBox.IsReadOnly = reading;
            if (!reading && saved == null && textBox.Text.Length == 0) info.Text = "まだ読み取っていません (読み取る: Ctrl+R)";
            else if (!reading) info.Text = Dirty ? "保存していない変更があります" : "";
        }

        private bool Save()
        {
            if (reading) return false;
            try
            {
                Reader.WriteCaption(image, textBox.Text);
                saved = textBox.Text;
                info.Text = "保存しました";
                UpdateButtons();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存できません。\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private async void Read()
        {
            int index = CurrentReader();
            if (reading || index < 0) return;
            if ((saved != null || textBox.Text.Length > 0) &&
                MessageBox.Show(this, "今の説明プロンプトを、読み取った内容で上書きします。よろしいですか？", Title,
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            var snap = Reader.Snapshot(readers[index]);
            reading = true;
            info.Text = "読み取っています... (閉じても裏で続けます)";
            UpdateButtons();
            string name = Path.GetFileName(image);
            try
            {
                string text = await Task.Run(() => Reader.ReadWith(snap, image));
                Reader.WriteCaption(image, text);
                reading = false;
                saved = text;
                if (IsLoaded) { textBox.Text = text; info.Text = "読み取って保存しました"; UpdateButtons(); }
                notify("説明プロンプトを読み取りました: " + name + Reader.SharingNote(image));
            }
            catch (Exception ex)
            {
                reading = false;
                if (IsLoaded) { info.Text = "読み取れませんでした: " + ex.Message; UpdateButtons(); }
                notify("説明プロンプトを読み取れませんでした: " + name + " (" + ex.Message.Replace("\n", " ") + ")");
            }
        }
    }
}
