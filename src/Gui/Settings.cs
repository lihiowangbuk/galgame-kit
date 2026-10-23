using System;
using System.IO;
using System.Text;

namespace GalgameKit.Gui
{
    /// <summary>
    /// 极简设置持久化。存在 exe 同目录的 galgame-kit.ini 里；
    /// 写不进去（只读目录 / Program Files）就静默放弃，不弹错。
    /// 刻意不存口令 —— 口令不落盘。
    /// </summary>
    sealed class Settings
    {
        public bool KeepIntermediate = true;

        /// <summary>
        /// 上次用的口令的**提示**（不是口令本身）。空字符串表示没设过。
        /// 之所以留这个字段是为了兼容老版本 ini，实际不写入。
        /// </summary>
        public string DefaultPassword = "";

        static string PathOf(string appDir) { return Path.Combine(appDir, "galgame-kit.ini"); }

        public static Settings Load(string appDir)
        {
            Settings s = new Settings();
            try
            {
                string p = PathOf(appDir);
                if (!File.Exists(p)) return s;

                foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "keepintermediate") s.KeepIntermediate = (v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { }
            return s;
        }

        public void Save(string appDir)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 齿轮开箱器设置。删掉这个文件就恢复默认。");
                sb.AppendLine("KeepIntermediate=" + (KeepIntermediate ? "1" : "0"));
                File.WriteAllText(PathOf(appDir), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
