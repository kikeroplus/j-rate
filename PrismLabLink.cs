// PrismLab に送る (PrismLab の docs\spec.md 5 章)。
// 選んだ画像を、PrismLab の量産のレシピの入口か、開いている物語 (新しいシーン / 選んでいるシーンの
// 元画像・リファレンス・ラストフレーム) に入れてもらう。J-Rate は送るだけで、入れるかどうかは PrismLab が決める。
// - レシピの一覧は %APPDATA%\PrismLab\recipes.json の name を並び順に読む (PrismLab が動いていなくても出せる)。
// - PrismLab の 2 重起動を止めるパイプ (PrismLab-(ユーザー名)) に JSON を 1 行 (改行で終わる) 書き、
//   PrismLab が読み終えて切るまで待つ (先に切ると、読まれずに落ちることがある)。
// - PrismLab が動いていなければ、JSON を一時フォルダに書いて「PrismLab.exe --send (JSON のパス)」で起動する
//   (PrismLab.exe はデスクトップの Application、なければ隣の PrismLab\dist)。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace JRate
{
    public static class PrismLabLink
    {
        public const string Sender = "J-Rate";
        public const string SendArg = "--send";

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        // Python の getpass.getuser() と同じ名前 (Windows では USERNAME)
        public static string PipeName
        {
            get
            {
                string user = Environment.GetEnvironmentVariable("USERNAME");
                if (string.IsNullOrEmpty(user)) user = Environment.UserName;
                return "PrismLab-" + user;
            }
        }

        // テストで差し替える (null = 本物)
        public static string PipeNameOverride;
        public static Func<string> FindExeOverride;
        public static Action<string, string> StartOverride;

        private static string AppData
        {
            // テストでは ComfySettings.AppDataOverride で差し替える (実際の設定に触れないように)
            get { return ComfySettings.AppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        }

        /// <summary>PrismLab の量産のレシピの名前 (並び順。読めなければ空)。</summary>
        public static List<string> RecipeNames()
        {
            var names = new List<string>();
            string path = Path.Combine(Path.Combine(AppData, "PrismLab"), "recipes.json");
            try
            {
                if (!File.Exists(path)) return names;
                var items = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as object[];
                if (items == null) return names;
                foreach (var item in items)
                {
                    var d = item as Dictionary<string, object>;
                    object name;
                    names.Add(d != null && d.TryGetValue("name", out name) && name != null ? name.ToString() : "");
                }
            }
            catch (Exception)
            {
                names.Clear();
            }
            return names;
        }

        public static string FindExe()
        {
            if (FindExeOverride != null) return FindExeOverride();
            var candidates = new List<string>
            {
                Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Application"),
                             "PrismLab.exe")
            };
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
                candidates.Add(Path.Combine(Path.Combine(Path.Combine(d.FullName, "PrismLab"), "dist"), "PrismLab.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>量産のレシピの入口に送る。うまくいけば空、だめなら理由。</summary>
        public static string SendToRecipe(IList<string> files, string recipe, int index)
        {
            return Send(new Dictionary<string, object>
            {
                { "target", "recipe" }, { "recipe", recipe }, { "index", index }, { "files", files.ToArray() }
            });
        }

        /// <summary>物語に送る。slot は scenes / source / reference / last。</summary>
        public static string SendToStory(IList<string> files, string slot)
        {
            return Send(new Dictionary<string, object>
            {
                { "target", "story" }, { "slot", slot }, { "files", files.ToArray() }
            });
        }

        public static string Send(Dictionary<string, object> message)
        {
            var full = new Dictionary<string, object> { { "cmd", "send" }, { "from", Sender } };
            foreach (var kv in message) full[kv.Key] = kv.Value;
            string json = new JavaScriptSerializer().Serialize(full);
            byte[] data = new UTF8Encoding(false).GetBytes(json + "\n");
            try { AllowSetForegroundWindow(-1); } catch (Exception) { }   // PrismLab が前に出られるように (ASFW_ANY)

            string pipe = PipeNameOverride ?? PipeName;
            try
            {
                using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
                {
                    client.Connect(700);
                    client.Write(data, 0, data.Length);
                    client.Flush();
                    // PrismLab が読み終えて切るまで待つ (切られると Read が 0 を返す)
                    bool closed = false;
                    var reader = new Thread(() =>
                    {
                        try
                        {
                            var buf = new byte[64];
                            while (client.Read(buf, 0, buf.Length) > 0) { }
                        }
                        catch (Exception) { }
                        closed = true;
                    }) { IsBackground = true };
                    reader.Start();
                    if (!reader.Join(3000) || !closed)
                        return "PrismLab が受け取りませんでした (応答がありません)。";
                    return "";
                }
            }
            catch (TimeoutException)
            {
                // 動いていない: 起動して渡す
            }
            catch (IOException ex)
            {
                return "PrismLab に送れませんでした: " + ex.Message;
            }

            string exe = FindExe();
            if (exe == null)
                return "PrismLab が見つかりません (デスクトップの Application に PrismLab.exe を置いてください)。";
            try
            {
                string temp = Path.Combine(Path.GetTempPath(), "prismlab_send_" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(json));
                if (StartOverride != null) StartOverride(exe, temp);
                else Process.Start(new ProcessStartInfo(exe, SendArg + " \"" + temp + "\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                return "PrismLab を起動できません: " + ex.Message;
            }
            return "";
        }
    }
}
