using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GalgameKit.Core
{
    /// <summary>分卷体检结果。</summary>
    public sealed class SplitCheck
    {
        public int Volumes;          // 手上实际有几卷
        public long Total;           // 这几卷加起来多少字节
        public long Need;            // 7z 主头声明总共需要多少字节
        public bool Complete;        // Total >= Need
        public long Missing { get { long m = Need - Total; return m > 0 ? m : 0; } }
        public override string ToString()
        {
            return string.Format("分卷 {0} 个，合并 {1}，需要 {2} -> {3}",
                Volumes, PathHelper.Bytes(Total), PathHelper.Bytes(Need),
                Complete ? "完整" : ("缺 " + PathHelper.Bytes(Missing)));
        }
    }

    /// <summary>
    /// 把一堆文件按「同一套资源」分组，并处理分卷。
    /// 支持：PC.001.enc / PC.7z.001 / app.part1.rar / data.z01 / 单文件。
    /// </summary>
    public static class VolumeGroup
    {
        static readonly Regex EncSuffix = new Regex(@"^(.+?)\.(enc)$", RegexOptions.IgnoreCase);
        static readonly Regex NumSuffix = new Regex(@"^(.*)\.(\d{3})$", RegexOptions.IgnoreCase);
        static readonly Regex PartRar = new Regex(@"^(.*)\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        static readonly Regex ZipSpan = new Regex(@"^(.*)\.z(\d{2})$", RegexOptions.IgnoreCase);

        /// <summary>
        /// 组键：同一资源的所有分卷映射到同一个字符串。
        /// <c>PC.001.enc</c> + <c>PC.002.enc</c> -&gt; <c>PC</c>；
        /// <c>app.7z.001</c> -&gt; <c>app.7z</c>；<c>X.part2.rar</c> -&gt; <c>X</c>。
        /// </summary>
        public static string KeyOf(string fileName)
        {
            string n = Path.GetFileName(fileName);
            if (string.IsNullOrEmpty(n)) return n;

            Match mp = PartRar.Match(n);
            if (mp.Success) return mp.Groups[1].Value;

            Match mz = ZipSpan.Match(n);
            if (mz.Success) return mz.Groups[1].Value;

            // PC.001.enc -> 去掉 .enc 再判断 .001
            Match me = EncSuffix.Match(n);
            if (me.Success)
            {
                string inner = me.Groups[1].Value;
                Match mi = NumSuffix.Match(inner);
                return mi.Success ? mi.Groups[1].Value : inner;
            }

            // PC.7z.001 -> PC.7z
            Match mn = NumSuffix.Match(n);
            if (mn.Success) return mn.Groups[1].Value;

            return Path.GetFileNameWithoutExtension(n);
        }

        /// <summary>
        /// 判断某个文件是否属于 <paramref name="key"/> 这一组的某一卷。
        /// 用组键反查，不依赖 KindOf —— 7z 的续卷没有魔数。
        /// </summary>
        public static bool BelongsTo(string fileName, string key)
        {
            return string.Equals(KeyOf(fileName), key, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按名字排序（001 &lt; 002 &lt; 010 的自然顺序）。</summary>
        public static List<string> SortNaturally(IEnumerable<string> files)
        {
            List<string> list = new List<string>(files);
            list.Sort(NaturalCompare);
            return list;
        }

        public static int NaturalCompare(string a, string b)
        {
            string na = Path.GetFileName(a) ?? "";
            string nb = Path.GetFileName(b) ?? "";
            int i = 0, j = 0;
            while (i < na.Length && j < nb.Length)
            {
                if (char.IsDigit(na[i]) && char.IsDigit(nb[j]))
                {
                    int si = i, sj = j;
                    while (i < na.Length && char.IsDigit(na[i])) i++;
                    while (j < nb.Length && char.IsDigit(nb[j])) j++;
                    string da = na.Substring(si, i - si).TrimStart('0');
                    string db = nb.Substring(sj, j - sj).TrimStart('0');
                    if (da.Length != db.Length) return da.Length - db.Length;
                    int c = string.CompareOrdinal(da, db);
                    if (c != 0) return c;
                }
                else
                {
                    char ca = char.ToUpperInvariant(na[i]);
                    char cb = char.ToUpperInvariant(nb[j]);
                    if (ca != cb) return ca < cb ? -1 : 1;
                    i++; j++;
                }
            }
            return (na.Length - i) - (nb.Length - j);
        }

        /// <summary>
        /// 7z 分卷完整性体检：主头 32 字节里 offset(12) / size(20) 声明了「从文件第 32 字节起
        /// 还需要多少字节才能读到尾部的元数据」。和所有卷的实际总长比对即可知道缺不缺。
        /// 非 7z / 读不出来时返回 null（例如 zip / rar 分卷）。
        /// </summary>
        public static SplitCheck CheckSevenZip(string firstVolume, IEnumerable<string> groupFiles)
        {
            byte[] h;
            try { h = FileSignatures.ReadHead(firstVolume, 32); }
            catch { return null; }
            if (h == null || h.Length < 32) return null;
            if (!(h[0] == 0x37 && h[1] == 0x7A && h[2] == 0xBC && h[3] == 0xAF)) return null;

            long off = BitConverter.ToInt64(h, 12);
            long size = BitConverter.ToInt64(h, 20);
            if (off < 0 || size < 0) return null;
            long need = 32 + off + size;

            string key = KeyOf(Path.GetFileName(firstVolume));
            long total = 0;
            int vol = 0;
            foreach (string f in groupFiles)
            {
                if (!BelongsTo(Path.GetFileName(f), key)) continue;
                total += PathHelper.SizeOf(f);
                vol++;
            }
            if (vol == 0) { total = PathHelper.SizeOf(firstVolume); vol = 1; }

            SplitCheck c = new SplitCheck();
            c.Need = need; c.Total = total; c.Volumes = vol;
            c.Complete = total >= need;
            return c;
        }

        /// <summary>这一组里应该拿哪一卷去喂解压器（总是第一卷 / 第一个文件）。</summary>
        public static string FirstVolume(IEnumerable<string> groupFiles)
        {
            List<string> sorted = SortNaturally(groupFiles);
            return sorted.Count > 0 ? sorted[0] : null;
        }
    }
}
