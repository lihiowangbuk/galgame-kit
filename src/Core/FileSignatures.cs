using System;
using System.Collections.Generic;
using System.IO;

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
        KiriKiri,     // .xp3
        UnityAsset,   // UnityFS
        CriCpk,       // CRIWARE .cpk
        NekoPack,
        PeImage,      // .exe / .dll
        Gzip,
        Bzip2,
        Xz,
        Lz4,
        Plain         // 没有魔数，靠扩展名兜底为「未知」
    }

    public sealed class Signature
    {
        public readonly byte[] Magic;
        public readonly ContainerKind Kind;
        public Signature(byte[] magic, ContainerKind kind) { Magic = magic; Kind = kind; }
    }

    /// <summary>
    /// 按文件头识别容器。识别失败时按扩展名兜底，再失败返回 Unknown。
    /// 所有判断都只读前 16 字节，不把整个文件读进内存。
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
            new Signature(new byte[] { 0x04, 0x22, 0x4D, 0x18 },                               ContainerKind.Lz4)
        };

        const int HeadBytes = 16;

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
            if (head == null || magic == null || head.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++) if (head[i] != magic[i]) return false;
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
                    if (Matches(head, Table[i].Magic)) return Table[i].Kind;
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
                case ".gz":  return ContainerKind.Gzip;
                case ".xz":  return ContainerKind.Xz;
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
                case ContainerKind.KiriKiri:   return "KiriKiri 资源";
                case ContainerKind.UnityAsset: return "Unity 资源";
                case ContainerKind.CriCpk:     return "CRI CPK";
                case ContainerKind.NekoPack:   return "NEKOPACK";
                case ContainerKind.PeImage:    return "Windows 程序";
                case ContainerKind.Gzip:       return "gzip";
                case ContainerKind.Bzip2:      return "bzip2";
                case ContainerKind.Xz:         return "xz";
                case ContainerKind.Lz4:        return "lz4";
                default:                       return "未知";
            }
        }

        public static bool IsArchive(ContainerKind k)
        {
            return k == ContainerKind.SevenZip || k == ContainerKind.Zip || k == ContainerKind.Rar;
        }
    }
}
