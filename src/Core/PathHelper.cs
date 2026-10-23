using System;
using System.Collections.Generic;
using System.IO;

namespace GalgameKit.Core
{
    /// <summary>
    /// 路径工具。集中处理三件容易踩的事：
    /// 1. 超过 260 字符的路径（加 \\?\ 前缀）
    /// 2. 枚举目录时遇到无权限子目录（不抛，跳过）
    /// 3. 判断路径是否真的是目录 / 文件（不抛异常）
    /// </summary>
    public static class PathHelper
    {
        public const int MaxClassicPath = 260;

        /// <summary>
        /// 为长路径准备 \\?\ 前缀。
        /// 只在真的超过 260 字符时才加 —— .NET Framework 的 FileStream 在
        /// 未开启长路径开关的进程里，遇到 \\?\ 前缀会直接报「路径中具有非法字符」，
        /// 所以短路径绝不能加。长路径的开关由 exe.config 里的
        /// AppContextSwitchOverrides 打开（见 src\app.config）。
        /// </summary>
        public static string Long(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { full = path; }   // 超长路径 GetFullPath 会抛 PathTooLongException，就用原样

            if (full.Length < MaxClassicPath) return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

        /// <summary>去掉 \\?\ 前缀，用于显示和给外部程序传参。</summary>
        public static string Display(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
            return path;
        }

        public static bool IsDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { if (Directory.Exists(path)) return true; } catch { }
            // 超长路径下 Directory.Exists 的原样形式会返回 false（不抛异常），再试带前缀的
            try { return Directory.Exists(Long(path)); } catch { return false; }
        }

        public static bool IsFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { if (File.Exists(path)) return true; } catch { }
            try { return File.Exists(Long(path)); } catch { return false; }
        }

        /// <summary>递归列出所有文件。跳过无权限目录、跳过符号链接环（默认不跟随）。</summary>
        public static List<string> AllFiles(string root, int maxDepth)
        {
            List<string> result = new List<string>();
            Collect(Long(root), 0, maxDepth, result);
            return result;
        }

        /// <summary>
        /// 递归枚举。内部一律用 \\?\ 形式（否则走到第 260 个字符就静默停下，
        /// 表现为「文件数比预期少」这种最难查的 bug），存回结果时去掉前缀，
        /// 因为下游要把它交给 7z.exe / java.exe，那些工具不认 \\?\。
        /// </summary>
        static void Collect(string dir, int depth, int maxDepth, List<string> acc)
        {
            if (depth > maxDepth) return;

            // 每进一层都要重新加前缀：父目录还短的时候 GetDirectories 返回的是短路径，
            // 一直往下拼就会在第 260 个字符处静默截断（Directory.GetFiles 抛异常被 catch 掉），
            // 变成「文件数比预期少」。
            dir = Long(dir);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return; }
            for (int i = 0; i < files.Length; i++) acc.Add(Display(files[i]));

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    // GetAttributes 也要走长路径形式，否则到第 260 个字符就抛，
                    // 被 catch 掉之后整棵子树都不再往下走。
                    FileAttributes a = File.GetAttributes(Long(subs[i]));
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;   // 不跟 junction，避免环
                }
                catch { continue; }
                Collect(subs[i], depth + 1, maxDepth, acc);
            }
        }

        public static long SizeOf(string file)
        {
            try { return new FileInfo(file).Length; } catch { return 0; }
        }

        /// <summary>可读的字节数。1.53 GB / 777.73 MB / 1.00 KB / 512 B</summary>
        public static string Bytes(long b)
        {
            if (b < 0) return "?";
            if (b < 1024) return b + " B";
            if (b < 1024L * 1024) return (b / 1024.0).ToString("N2", Culture) + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("N2", Culture) + " MB";
            return (b / 1073741824.0).ToString("N2", Culture) + " GB";
        }

        static readonly System.Globalization.CultureInfo Culture =
            System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>同盘直接 Move 是瞬时的；跨盘会退化成复制。返回是否为同盘。</summary>
        public static bool SameDrive(string a, string b)
        {
            try
            {
                string ra = Path.GetPathRoot(Path.GetFullPath(a));
                string rb = Path.GetPathRoot(Path.GetFullPath(b));
                return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
