using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GalgameKit.Core
{
    /// <summary>游戏引擎。只用于给用户提示「这是什么引擎、怎么开、存档在哪」。</summary>
    public enum GameEngine
    {
        Unknown,
        Unity,
        Godot,
        RpgMakerVxAce,
        RpgMakerXp,
        RpgMakerMvMz,
        KiriKiri,
        Wolf,
        NScripter,
        Renpy,
        Artemis,
        Majiro
    }

    public static class EngineDetector
    {
        public static GameEngine Detect(IEnumerable<string> files)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasXp3 = false, hasPck = false, hasWolf = false, hasNsa = false, hasRpy = false;
            bool hasMajiro = false, hasArtemis = false;

            foreach (string f in files)
            {
                string n;
                try { n = Path.GetFileName(f); } catch { continue; }
                if (string.IsNullOrEmpty(n)) continue;
                names.Add(n);

                string ext = Path.GetExtension(n).ToLowerInvariant();
                if (ext == ".xp3") hasXp3 = true;
                else if (ext == ".pck") hasPck = true;
                else if (ext == ".wolf") hasWolf = true;
                else if (ext == ".nsa" || ext == ".ns2" || ext == ".sar") hasNsa = true;
                else if (ext == ".rpy" || ext == ".rpyc" || ext == ".rpa") hasRpy = true;
                else if (ext == ".arc" && n.StartsWith("data", StringComparison.OrdinalIgnoreCase)) hasMajiro = true;
                else if (ext == ".pfs" || ext == ".ain") hasArtemis = true;
            }

            if (names.Contains("UnityPlayer.dll")) return GameEngine.Unity;
            if (names.Contains("RGSS301.dll")) return GameEngine.RpgMakerVxAce;
            if (names.Contains("RGSS202E.dll")) return GameEngine.RpgMakerXp;
            if (names.Contains("RGSS104E.dll")) return GameEngine.RpgMakerXp;
            if (names.Contains("nw.dll") || names.Contains("package.json")) return GameEngine.RpgMakerMvMz;
            if (hasXp3) return GameEngine.KiriKiri;
            if (hasPck || names.Contains("godot.log")) return GameEngine.Godot;
            // Godot 导出会额外生成 <名字>.console.exe，Unity / RPG Maker 没有这个配对
            if (names.Any(delegate (string n) { return n.EndsWith(".console.exe", StringComparison.OrdinalIgnoreCase); }))
                return GameEngine.Godot;
            if (hasWolf) return GameEngine.Wolf;
            if (hasNsa) return GameEngine.NScripter;
            if (hasRpy) return GameEngine.Renpy;
            if (hasMajiro) return GameEngine.Majiro;
            if (hasArtemis) return GameEngine.Artemis;
            return GameEngine.Unknown;
        }

        public static string Name(GameEngine e)
        {
            switch (e)
            {
                case GameEngine.Unity:         return "Unity";
                case GameEngine.Godot:         return "Godot";
                case GameEngine.RpgMakerVxAce: return "RPG Maker VX Ace";
                case GameEngine.RpgMakerXp:    return "RPG Maker XP";
                case GameEngine.RpgMakerMvMz:  return "RPG Maker MV/MZ";
                case GameEngine.KiriKiri:      return "KiriKiri";
                case GameEngine.Wolf:          return "WOLF RPG";
                case GameEngine.NScripter:     return "NScripter";
                case GameEngine.Renpy:         return "Ren'Py";
                case GameEngine.Artemis:       return "Artemis";
                case GameEngine.Majiro:        return "Majiro";
                default:                       return "未知";
            }
        }

        /// <summary>这个引擎的存档通常在哪。返回 null 表示不知道，别乱写。</summary>
        public static string SaveHint(GameEngine e, string title)
        {
            switch (e)
            {
                case GameEngine.Unity:
                case GameEngine.Godot:
                case GameEngine.Renpy:
                    return @"%USERPROFILE%\AppData\LocalLow\<公司名>\<游戏名>\";
                case GameEngine.RpgMakerVxAce:
                case GameEngine.RpgMakerXp:
                case GameEngine.RpgMakerMvMz:
                    return @"游戏目录\Save\ 或 %USERPROFILE%\Saved Games\";
                case GameEngine.KiriKiri:
                    return @"%USERPROFILE%\Documents\<游戏名>\";
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// 从一堆产物文件里挑一个最像启动器的 exe。
    /// 规则：排除标准的垃圾名字（崩溃处理器 / 运行库 / 卸载器 / 控制台配套件），
    /// 剩下体积最大的那个 —— galgame 的启动器几乎总是最大的 exe。
    /// </summary>
    public static class LauncherPicker
    {
        static readonly Regex Junk = new Regex(
            @"UnityCrashHandler|CrashReport|vcredist|dxsetup|unins\d+|^uninstall|setup\.exe$|console\.exe$|QtWebEngineProcess|notification_helper|elevation_service",
            RegexOptions.IgnoreCase);

        public static string Pick(IEnumerable<string> files)
        {
            string best = null;
            long bestLen = -1;
            foreach (string f in files)
            {
                if (!f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string n = Path.GetFileName(f);
                if (Junk.IsMatch(n)) continue;
                long len = PathHelper.SizeOf(f);
                if (len > bestLen) { bestLen = len; best = f; }
            }
            return best;
        }

        /// <summary>目录本身看起来就已经是能跑的游戏？</summary>
        public static bool LooksPlayable(IEnumerable<string> files)
        {
            bool hasExe = false;
            foreach (string f in files)
            {
                string n;
                try { n = Path.GetFileName(f); } catch { continue; }
                if (n.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.Equals("RGSS301.dll", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.Equals("RGSS202E.dll", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.Equals("godot.log", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.StartsWith("nw.", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.EndsWith(".xp3", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Junk.IsMatch(n)) hasExe = true;
            }
            return hasExe;
        }
    }
}
