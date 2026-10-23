using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalgameKit.Core
{
    /// <summary>
    /// 外部工具的位置与可用性。所有路径都相对 exe 自身目录推导，
    /// 因此整个工具目录可以整体搬到任意位置 / 任意机器。
    /// 也支持用环境变量 GALGAMEKIT_TOOLS 覆盖（把工具集中放别处的人会用到）。
    /// </summary>
    public sealed class ToolSet
    {
        public string Root;
        public string SevenZip;      // 7z.exe，支持 7z/zip/rar 全部格式
        public string SevenZipR;     // 7zr.exe，只支持 7z，作为兜底
        public string Java;          // 自带 JRE 的 java.exe
        public string SsefJar;       // ssefenc.jar

        public static ToolSet Locate(string exeDir)
        {
            ToolSet t = new ToolSet();

            string env = Environment.GetEnvironmentVariable("GALGAMEKIT_TOOLS");
            t.Root = (!string.IsNullOrEmpty(env) && Directory.Exists(env)) ? env : exeDir;

            t.SevenZip  = Path.Combine(t.Root, "7z.exe");
            t.SevenZipR = Path.Combine(t.Root, "7zr.exe");
            t.Java      = Path.Combine(t.Root, Path.Combine("jre", Path.Combine("bin", "java.exe")));
            t.SsefJar   = Path.Combine(t.Root, "ssefenc.jar");

            // 兼容老布局：admin\Paranoia File & Text Encryption\runtime\bin\java.exe
            if (!File.Exists(t.Java))
            {
                string alt = Path.Combine(t.Root,
                    Path.Combine("admin", Path.Combine("Paranoia File & Text Encryption",
                    Path.Combine("runtime", Path.Combine("bin", "java.exe")))));
                if (File.Exists(alt)) t.Java = alt;
            }
            // 再兼容：S.S.E. File Encryptor for PC\ssefenc.jar
            if (!File.Exists(t.SsefJar))
            {
                string alt = Path.Combine(t.Root, Path.Combine("S.S.E. File Encryptor for PC", "ssefenc.jar"));
                if (File.Exists(alt)) t.SsefJar = alt;
            }
            return t;
        }

        public bool CanExtract { get { return File.Exists(SevenZip) || File.Exists(SevenZipR); } }
        public bool CanDecrypt { get { return File.Exists(Java) && File.Exists(SsefJar); } }

        /// <summary>缺什么，按人话列出来。</summary>
        public List<string> Missing()
        {
            List<string> m = new List<string>();
            if (!File.Exists(SevenZip) && !File.Exists(SevenZipR)) m.Add("7z.exe / 7zr.exe");
            if (!File.Exists(Java)) m.Add("java.exe（自带 JRE 那份）");
            if (!File.Exists(SsefJar)) m.Add("ssefenc.jar");
            return m;
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("工具目录：" + Root);
            sb.AppendLine("  7z.exe   " + Ok(File.Exists(SevenZip)));
            sb.AppendLine("  7zr.exe  " + Ok(File.Exists(SevenZipR)));
            sb.AppendLine("  java.exe " + Ok(File.Exists(Java)));
            sb.AppendLine("  ssefenc  " + Ok(File.Exists(SsefJar)));
            return sb.ToString();
        }

        static string Ok(bool b) { return b ? "OK" : "缺失"; }
    }
}
