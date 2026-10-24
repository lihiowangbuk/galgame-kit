using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalgameKit.Core
{
    /// <summary>能识别的容器类型。名字只用于显示，逻辑分支请用枚举。</summary>
    public enum ContainerKind
    {
        Unknown,
        Ssefe,        // S.S.E. File Encryptor 容器（资源站常见的 .enc）
        SevenZip,
        Zip,
        Rar,
        Tar,          // 未压缩的 tar（魔数在偏移 257）
        KiriKiri,     // .xp3
        UnityAsset,   // UnityFS
        CriCpk,       // CRIWARE .cpk
        NekoPack,
        PeImage,      // .exe / .dll
        Gzip,
        Bzip2,
        Xz,
        Lz4,
        Zstd,         // .zst
        Plain         // 没有魔数，靠扩展名兜底为「未知」
    }

    public sealed class Signature
    {
        public readonly byte[] Magic;
        public readonly ContainerKind Kind;
        /// <summary>魔数在文件里的偏移。tar 的 "ustar" 在 257。</summary>
        public readonly int Offset;
        public Signature(byte[] magic, ContainerKind kind) : this(magic, kind, 0) { }
        public Signature(byte[] magic, ContainerKind kind, int offset)
        {
            Magic = magic; Kind = kind; Offset = offset;
        }
    }

    /// <summary>
    /// 按文件头识别容器。识别失败时按扩展名兜底，再失败返回 Unknown。
    /// 只读前 264 字节（tar 的魔数在 257），不把整个文件读进内存。
    /// </summary>
    public static class FileSignatures
    {
        static readonly Signature[] Table = new Signature[]
        {
            new Signature(new byte[] { 0x53, 0x53, 0x45, 0x46, 0x45 },                          ContainerKind.Ssefe),
            new Signature(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },                   ContainerKind.SevenZip),
            new Signature(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 },                   ContainerKind.Rar),
            new Signature(new byte[] { 0x50, 0x4B, 0x03, 0x04 },                               ContainerKind.Zip),
            new Signature(new byte[] { 0x58, 0x50, 0x33, 0x0D, 0x0A, 0x20, 0x0A, 0x1A, 0x8B }, ContainerKind.KiriKiri),
            new Signature(new byte[] { 0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53 },             ContainerKind.UnityAsset),
            new Signature(new byte[] { 0x43, 0x50, 0x4B, 0x20 },                               ContainerKind.CriCpk),
            new Signature(new byte[] { 0x4D, 0x5A },                                           ContainerKind.PeImage),
            new Signature(new byte[] { 0x1F, 0x8B },                                           ContainerKind.Gzip),
            new Signature(new byte[] { 0x42, 0x5A, 0x68 },                                     ContainerKind.Bzip2),
            new Signature(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 },                   ContainerKind.Xz),
            new Signature(new byte[] { 0x04, 0x22, 0x4D, 0x18 },                               ContainerKind.Lz4),
            new Signature(new byte[] { 0x28, 0xB5, 0x2F, 0xFD },                               ContainerKind.Zstd),
            // tar 的 "ustar" 落在第 257 字节。放最后，避免它挡住别的判断。
            new Signature(Encoding.ASCII.GetBytes("ustar"), ContainerKind.Tar, 257)
        };

        /// <summary>要读多少字节才够判断。tar 的魔数在 257，所以必须读到 262。</summary>
        const int HeadBytes = 264;

        public static byte[] ReadHead(string path, int count)
        {
            using (FileStream fs = new FileStream(PathHelper.Long(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] b = new byte[count];
                int n = fs.Read(b, 0, count);
                if (n < count) Array.Resize(ref b, n);
                return b;
            }
        }

        public static bool Matches(byte[] head, byte[] magic)
        {
            return Matches(head, magic, 0);
        }

        public static bool Matches(byte[] head, byte[] magic, int offset)
        {
            if (head == null || magic == null || offset < 0) return false;
            if (head.Length < offset + magic.Length) return false;
            for (int i = 0; i < magic.Length; i++) if (head[offset + i] != magic[i]) return false;
            return true;
        }

        public static ContainerKind Detect(string path)
        {
            byte[] head;
            try { head = ReadHead(path, HeadBytes); }
            catch { head = null; }

            if (head != null)
            {
                for (int i = 0; i < Table.Length; i++)
                    if (Matches(head, Table[i].Magic, Table[i].Offset)) return Table[i].Kind;
            }

            // 不认识的文件头 -> 按扩展名兜底
            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }
            switch (ext)
            {
                case ".exe":
                case ".dll": return ContainerKind.PeImage;
                case ".xp3": return ContainerKind.KiriKiri;
                case ".7z":  return ContainerKind.SevenZip;
                case ".zip": return ContainerKind.Zip;
                case ".rar": return ContainerKind.Rar;
                case ".tar": return ContainerKind.Tar;
                case ".gz":  return ContainerKind.Gzip;
                case ".bz2": return ContainerKind.Bzip2;
                case ".xz":  return ContainerKind.Xz;
                case ".lz4": return ContainerKind.Lz4;
                case ".zst":
                case ".zstd": return ContainerKind.Zstd;
                // .enc 但魔数不认识：仍然按加密容器处理 —— 站点偶尔换实现，
                // 让用户拿到「要口令」的提示，比直接判定 Unknown 然后跳过有用得多。
                case ".enc": return ContainerKind.Ssefe;
                default:     return ContainerKind.Unknown;
            }
        }

        /// <summary>给日志用的中文说明。</summary>
        public static string Describe(ContainerKind k)
        {
            switch (k)
            {
                case ContainerKind.Ssefe:      return "加密容器（要口令）";
                case ContainerKind.SevenZip:   return "7-Zip";
                case ContainerKind.Zip:        return "ZIP";
                case ContainerKind.Rar:        return "RAR";
                case ContainerKind.Tar:        return "tar";
                case ContainerKind.KiriKiri:   return "KiriKiri 资源";
                case ContainerKind.UnityAsset: return "Unity 资源";
                case ContainerKind.CriCpk:     return "CRI CPK";
                case ContainerKind.NekoPack:   return "NEKOPACK";
                case ContainerKind.PeImage:    return "Windows 程序";
                case ContainerKind.Gzip:       return "gzip";
                case ContainerKind.Bzip2:      return "bzip2";
                case ContainerKind.Xz:         return "xz";
                case ContainerKind.Lz4:        return "lz4";
                case ContainerKind.Zstd:       return "zstd";
                default:                       return "未知";
            }
        }

        /// <summary>
        /// 交给 7z 就能解开的单层压缩包。刻意不含 gzip/bzip2/xz/zstd：
        /// 那几种只会解出「一个文件」，多半是 .tar 的第一层，需要二次解包
        /// （见 <see cref="IsSingleStream"/>）。
        /// </summary>
        public static bool IsArchive(ContainerKind k)
        {
            return k == ContainerKind.SevenZip || k == ContainerKind.Zip ||
                   k == ContainerKind.Rar || k == ContainerKind.Tar;
        }

        /// <summary>
        /// 单流压缩（gzip/bzip2/xz/zstd/lz4）：解开只得到一个文件，通常还要再解一次。
        /// </summary>
        public static bool IsSingleStream(ContainerKind k)
        {
            return k == ContainerKind.Gzip || k == ContainerKind.Bzip2 ||
                   k == ContainerKind.Xz || k == ContainerKind.Zstd || k == ContainerKind.Lz4;
        }
    }
}
