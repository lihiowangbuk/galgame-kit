using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GalgameKit.Core
{
    /// <summary>一次开箱的结果。</summary>
    public sealed class UnpackOutcome
    {
        public bool Success;
        public string GameDirectory;      // 解出来的游戏所在目录（找得到才有）
        public string Launcher;           // 建议启动的 exe
        public GameEngine Engine = GameEngine.Unknown;
        public long TotalBytes;
        public int TotalFiles;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Failures = new List<string>();
        public double Seconds;

        public bool Playable { get { return !string.IsNullOrEmpty(Launcher); } }
    }

    public sealed class Options
    {
        /// <summary>解密口令。空字符串表示这批文件没加密。</summary>
        public string Password = "";

        /// <summary>解出来的中间文件（解密产物、解压产物）保留还是删掉。</summary>
        public bool KeepIntermediate = true;

        /// <summary>输出根目录。null 表示放在源目录下的 _unpack。</summary>
        public string OutputRoot;

        /// <summary>只体检不动手。</summary>
        public bool DryRun;

        /// <summary>递归枚举的最深层数。设 0 表示只看第一层。</summary>
        public int MaxDepth = 8;

        /// <summary>解压前检查目标盘剩余空间，不够就停下。</summary>
        public bool CheckFreeSpace = true;

        /// <summary>
        /// 取消令牌。取消时会杀掉正在运行的子进程（7z / java），
        /// 不等它自己结束 —— 否则用户点了「停止」还得再等十几分钟。
        /// </summary>
        public System.Threading.CancellationToken CancelToken = System.Threading.CancellationToken.None;
    }

    /// <summary>
    /// 开箱主流程。刻意不含任何界面代码 —— 图形界面、命令行、测试都调这一个类。
    /// <code>
    /// var runner = new UnpackPipeline(ToolSet.Locate(exeDir), new Options { Password = "..." });
    /// UnpackOutcome r = runner.Run(@"D:\Downloads\Gamex-123456", reporter);
    /// </code>
    /// </summary>
    public sealed class UnpackPipeline
    {
        readonly ToolSet _tools;
        readonly Options _opt;

        static readonly Encoding Utf8 = new UTF8Encoding(false);
        static readonly Encoding Ansi = Encoding.GetEncoding(936);   // 7z 的中文输出是 GBK

        static readonly Regex DecryptPercent =
            new Regex(@"Decrypting\s*\(([^)]+)\)\s*:\s*(\d+)%", RegexOptions.Compiled);
        static readonly Regex SevenZipPercent =
            new Regex(@"^\s*(\d+)%", RegexOptions.Compiled);

        /// <summary>这些扩展名一律不参与开箱（说明文件、预览图、下载残留）。</summary>
        static readonly HashSet<string> IgnoredExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".nfo", ".url", ".md", ".htm", ".html", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".ini"
        };

        public UnpackPipeline(ToolSet tools, Options options)
        {
            if (tools == null) throw new ArgumentNullException("tools");
            _tools = tools;
            _opt = options ?? new Options();
        }

        public ToolSet Tools { get { return _tools; } }

        // ==================== 入口 ====================

        public UnpackOutcome Run(string targetDir, IReporter r)
        {
            if (r == null) r = new NullReporter();
            UnpackOutcome outcome = new UnpackOutcome();
            DateTime t0 = DateTime.Now;

            try
            {
                RunCore(targetDir, r, outcome);
            }
            catch (Exception ex)
            {
                Fail(outcome, r, "未预期的错误：" + ex.Message);
            }

            outcome.Seconds = (DateTime.Now - t0).TotalSeconds;
            return outcome;
        }

        void RunCore(string targetDir, IReporter r, UnpackOutcome outcome)
        {
            if (string.IsNullOrWhiteSpace(targetDir) || !PathHelper.IsDir(targetDir))
            {
                Fail(outcome, r, "路径不存在：" + targetDir);
                return;
            }

            string outRoot = !string.IsNullOrEmpty(_opt.OutputRoot)
                ? _opt.OutputRoot
                : Path.Combine(targetDir, "_unpack");
            string decDir = Path.Combine(outRoot, "decrypted");
            string gameDir = Path.Combine(outRoot, "game");

            // ---- 1. 列文件 ----
            List<string> top = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(targetDir)) top.Add(f);
            }
            catch (Exception ex)
            {
                Fail(outcome, r, "读不了这个目录：" + ex.Message);
                return;
            }

            List<string> files = new List<string>();
            foreach (string f in top)
            {
                string ext = Path.GetExtension(f);
                if (IgnoredExtensions.Contains(ext)) continue;
                // 自己上一次的产出不要当成输入
                if (f.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(f);
            }
            files = VolumeGroup.SortNaturally(files);

            if (files.Count == 0)
            {
                r.Log("这个目录里没有可处理的文件。");
                r.Status("没什么可做的");
                return;
            }

            // ---- 1b. 本来就是解压好的游戏目录 ----
            List<string> all = PathHelper.AllFiles(targetDir, _opt.MaxDepth);
            if (LauncherPicker.LooksPlayable(all))
            {
                r.Log("");
                r.Log("这已经是解压好的游戏目录，不需要再解包。");
                ReportDirectory(r, outcome, all);
                outcome.Success = true;
                return;
            }

            // ---- 1c. 按需体检工具 ----
            // 位置刻意放在这里：已经是解压好的目录、或者只是 --dry-run 体检，都不该被工具缺失挡住。
            // 而且只看这批文件真的需要什么 —— 只放了个普通 7z 包的人不该被要求先装好 Java。
            bool needDecrypt = false;
            bool needExtract = false;
            foreach (string f in files)
            {
                ContainerKind kind = FileSignatures.Detect(f);
                if (kind == ContainerKind.Ssefe) needDecrypt = true;
                else if (kind == ContainerKind.SevenZip || kind == ContainerKind.Zip || kind == ContainerKind.Rar)
                    needExtract = true;
            }

            List<string> missing = new List<string>();
            if (needExtract && !_tools.CanExtract) missing.Add("7z.exe / 7zr.exe（解压用）");
            if (needDecrypt && !_tools.CanDecrypt) missing.Add("java.exe 与 ssefenc.jar（解密 .enc 用）");

            if (missing.Count > 0)
            {
                if (_opt.DryRun)
                {
                    // 体检模式就是让人先看缺什么，所以报出来但不中止。
                    foreach (string m in missing) r.Log("[提示] 真正开箱时会缺：" + m);
                    r.Log("工具目录：" + _tools.Root);
                }
                else
                {
                    foreach (string m in missing) Fail(outcome, r, "缺少工具：" + m);
                    r.Log("工具必须和程序放在同一个目录里。当前找的是：" + _tools.Root);
                    return;
                }
            }

            // ---- 2. 识别 + 分组 ----
            r.Status("正在识别文件类型…");
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string k = VolumeGroup.KeyOf(f);
                if (!groups.ContainsKey(k)) groups[k] = new List<string>();
                groups[k].Add(f);
            }

            r.Log("");
            r.Log("识别结果：");
            foreach (string f in files)
            {
                r.Log(string.Format("  {0,-34} {1,11}  {2}",
                    Path.GetFileName(f),
                    PathHelper.Bytes(PathHelper.SizeOf(f)),
                    DescribeWithVolumeHint(f)));
            }
            r.Log("共 " + groups.Count + " 组");

            if (_opt.DryRun)
            {
                r.Log("");
                r.Log("[体检模式] 不做实际改动。");
                outcome.Success = true;
                return;
            }

            // ---- 3. 逐组处理 ----
            bool anyOk = false;
            foreach (KeyValuePair<string, List<string>> kv in groups)
            {
                if (r.Cancelled) { r.Status("已取消"); break; }

                r.Log("");
                r.Log("=== 组：" + kv.Key + "（" + kv.Value.Count + " 个文件） ===");

                bool ok = RunGroup(kv.Key, VolumeGroup.SortNaturally(kv.Value), decDir, gameDir, r, outcome);
                if (ok) anyOk = true;
            }

            if (anyOk)
            {
                // 找最终的游戏目录：优先用最后一组解出来的
                if (PathHelper.IsDir(gameDir) && string.IsNullOrEmpty(outcome.GameDirectory))
                {
                    List<string> g = PathHelper.AllFiles(gameDir, _opt.MaxDepth);
                    if (g.Count > 0) ReportDirectory(r, outcome, g);
                }
                outcome.Success = true;
                r.Status("完成" + (outcome.Playable ? " —— 可以启动了" : ""));
                r.Log("########## 完成 ##########");
            }
            else
            {
                r.Status("结束（有未完成的组，看上面的错误）");
                r.Log("########## 结束 ##########");
            }
        }

        // ==================== 单组 ====================

        bool RunGroup(string key, List<string> group, string decDir, string gameDir, IReporter r, UnpackOutcome outcome)
        {
            List<string> current = group;

            // ---- 解密 ----
            bool anyEncrypted = false;
            foreach (string f in current)
                if (FileSignatures.Detect(f) == ContainerKind.Ssefe) { anyEncrypted = true; break; }

            if (anyEncrypted)
            {
                if (string.IsNullOrEmpty(_opt.Password))
                {
                    r.Log("[跳过] 这批是加密容器，需要口令。");
                    return false;
                }
                if (!PathHelper.IsDir(decDir))
                {
                    try { Directory.CreateDirectory(decDir); }
                    catch (Exception ex) { Fail(outcome, r, "建不了解密输出目录：" + ex.Message); return false; }
                }
                if (_opt.CheckFreeSpace)
                {
                    long need = 0;
                    foreach (string f in current) need += PathHelper.SizeOf(f);
                    string msg = CheckSpace(decDir, need);
                    if (msg != null) { Fail(outcome, r, msg); return false; }
                }

                r.Log("--- 解密到 " + decDir + " ---");
                List<string> decrypted = new List<string>();
                for (int i = 0; i < current.Count; i++)
                {
                    if (r.Cancelled) return false;
                    string f = current[i];
                    if (FileSignatures.Detect(f) != ContainerKind.Ssefe) continue;

                    int idx = i;
                    r.Status(string.Format("解密 {0}（{1}/{2}）", Path.GetFileName(f), i + 1, current.Count));

                    ProcessResult pr = DecryptFile(f, decDir, idx, current.Count, r);
                    if (pr.Cancelled) return false;

                    if (pr.ExitCode != 0)
                    {
                        string why = ExplainDecryptError(pr);
                        Fail(outcome, r, Path.GetFileName(f) + "：" + why);
                        return false;
                    }
                    r.Log("    OK  " + pr.Seconds.ToString("N1") + "s");
                }

                r.Log("  解密产物：");
                List<string> produced = new List<string>();
                foreach (string f in Directory.GetFiles(decDir)) produced.Add(f);
                produced = VolumeGroup.SortNaturally(produced);
                foreach (string f in produced)
                {
                    r.Log(string.Format("    {0,-34} {1,11}  {2}",
                        Path.GetFileName(f),
                        PathHelper.Bytes(PathHelper.SizeOf(f)),
                        DescribeWithVolumeHint(f)));
                }
                current = produced;
                if (current.Count == 0) { Fail(outcome, r, "解密没产出任何文件。"); return false; }
            }

            // ---- 解密产物本身可能已经是可玩的目录 ----
            if (LauncherPicker.LooksPlayable(current))
            {
                r.Log("  这已经是解压好的游戏目录，不用再解。");
                ReportDirectory(r, outcome, current);
                return true;
            }

            // ---- 找压缩包 ----
            string arch = PickFirstArchive(current);
            if (arch == null)
            {
                r.Log("  [跳过] 没找到能识别的压缩包。");
                return false;
            }
            ContainerKind ak = FileSignatures.Detect(arch);
            if (!PathHelper.IsDir(gameDir))
            {
                try { Directory.CreateDirectory(gameDir); }
                catch (Exception ex) { Fail(outcome, r, "建不了解压输出目录：" + ex.Message); return false; }
            }

            // ---- 分卷体检（只有 7z 能精确判断）----
            if (ak == ContainerKind.SevenZip)
            {
                SplitCheck chk = VolumeGroup.CheckSevenZip(arch, current);
                if (chk != null)
                {
                    r.Log("  " + chk);
                    if (!chk.Complete)
                    {
                        string msg = "卷不全，缺 " + PathHelper.Bytes(chk.Missing) + "。把缺的那一卷下完再来。";
                        Fail(outcome, r, msg);
                        return false;
                    }
                }
            }

            if (_opt.CheckFreeSpace)
            {
                long packed = 0;
                foreach (string f in current) packed += PathHelper.SizeOf(f);
                string msg = CheckSpace(gameDir, Math.Max(packed / 2, 512L * 1024 * 1024));
                if (msg != null) { Fail(outcome, r, msg); return false; }
            }

            // ---- 解压 ----
            string tool = File.Exists(_tools.SevenZip) ? _tools.SevenZip : _tools.SevenZipR;
            bool canZip = string.Equals(Path.GetFileName(tool), "7z.exe", StringComparison.OrdinalIgnoreCase);
            if (!canZip && ak != ContainerKind.SevenZip)
            {
                string msg = "只有 7zr.exe，它不支持 " + FileSignatures.Describe(ak) + "。请补上 7z.exe。";
                Fail(outcome, r, msg);
                return false;
            }

            r.Log("--- 解压（" + FileSignatures.Describe(ak) + "）到 " + gameDir + " ---");
            r.Status("解压 " + Path.GetFileName(arch) + " …");
            ProcessResult rx = Extract(arch, gameDir, r);

            if (rx.ExitCode != 0 && ak == ContainerKind.SevenZip && File.Exists(_tools.SevenZipR))
            {
                // 7z.exe 失败再用 7zr.exe 试一次（少见，但有的包 7z.exe 会因权限/长路径报错）
                r.Log("  7z.exe 失败，用 7zr.exe 重试一次…");
                rx = Extract(arch, gameDir, r, _tools.SevenZipR);
            }

            if (rx.Cancelled) return false;
            if (rx.ExitCode != 0)
            {
                string msg = ExplainExtractError(rx);
                Fail(outcome, r, msg);
                foreach (string line in rx.Tail) r.Log("    " + line);
                return false;
            }
            r.Log("  完成  " + rx.Seconds.ToString("N1") + "s");

            // ---- 产物分析 ----
            r.Log("");
            r.Log("--- 产物分析 ---");
            List<string> all = PathHelper.AllFiles(gameDir, _opt.MaxDepth);
            ReportDirectory(r, outcome, all);

            if (!_opt.KeepIntermediate && anyEncrypted)
            {
                // 只删解密产物（几百 MB 到几 GB 的压缩包），游戏本体保留
                TryDelete(decDir, r);
            }
            return true;
        }

        /// <summary>
        /// 从游戏目录里读出 (公司名, 游戏名)，用于猜存档位置。读不到对应项就是 null。
        /// Unity 的 *_Data\app.info 前两行就是公司名和游戏名，比看 exe 文件名准。
        /// </summary>
        static void TryReadInfo(string gameDir, out string company, out string title)
        {
            company = null;
            title = null;

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return;
            try
            {
                string[] info = Directory.GetFiles(gameDir, "app.info", SearchOption.AllDirectories);
                if (info.Length > 0)
                {
                    string[] lines = File.ReadAllLines(info[0]);
                    if (lines.Length >= 1 && lines[0].Trim().Length > 0) company = lines[0].Trim();
                    if (lines.Length >= 2 && lines[1].Trim().Length > 0) title = lines[1].Trim();
                    if (title != null) return;
                }
            }
            catch { }

            // 退一步：用最大的 exe 的文件名去掉扩展名当标题。
            try
            {
                string[] exes = Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly);
                string best = null;
                long bestSize = 0;
                foreach (string x in exes)
                {
                    long size = PathHelper.SizeOf(x);
                    if (size > bestSize) { bestSize = size; best = x; }
                }
                if (best != null) title = Path.GetFileNameWithoutExtension(best);
            }
            catch { }
        }

        void ReportDirectory(IReporter r, UnpackOutcome outcome, List<string> files)
        {
            long sum = 0;
            foreach (string f in files) sum += PathHelper.SizeOf(f);
            GameEngine e = EngineDetector.Detect(files);
            string launcher = LauncherPicker.Pick(files);

            r.Log(string.Format("  {0} 个文件，共 {1}", files.Count, PathHelper.Bytes(sum)));
            r.Log("  引擎：" + EngineDetector.Name(e));

            outcome.TotalFiles = files.Count;
            outcome.TotalBytes = sum;
            outcome.Engine = e;

            if (launcher != null)
            {
                outcome.Launcher = launcher;
                outcome.GameDirectory = Path.GetDirectoryName(launcher);
                r.Log("  启动：" + launcher);

                // 先看能不能用真实标题猜出已存在的存档目录；猜不到就给通用提示。
                string company, title;
                TryReadInfo(Path.GetDirectoryName(launcher), out company, out title);
                string real = EngineDetector.GuessSaveDirectory(e, company, title);
                if (real != null)
                    r.Log("  存档：" + real);
                else
                {
                    string hint = EngineDetector.SaveHint(e, title);
                    if (hint != null) r.Log("  存档：" + hint);
                }
            }
            else
            {
                outcome.GameDirectory = files.Count > 0 ? Path.GetDirectoryName(files[0]) : null;
                r.Log("  没找到可执行的 exe。");
            }
        }

        // ==================== 外部程序 ====================

        ProcessResult DecryptFile(string file, string outDir, int index, int total, IReporter r)
        {
            // 口令不放在命令行上（命令行对整个系统可见）。用临时文件传给 ssefenc。
            string pwFile = Path.Combine(Path.GetTempPath(),
                "gk-" + Guid.NewGuid().ToString("N") + ".pw");
            try
            {
                File.WriteAllText(pwFile, _opt.Password, Utf8);

                string args = string.Format(
                    "-Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 -jar \"{0}\" dec --password-file \"{1}\" -o \"{2}\" \"{3}\"",
                    _tools.SsefJar, pwFile, outDir, file);

                ProgressHandler onProgress = delegate (int pct, string alg)
                {
                    int overall = total > 1 ? (int)((index + pct / 100.0) / total * 100) : pct;
                    r.Percent(overall);
                    r.Status(string.Format("解密 {0}  {1}%  ({2})", Path.GetFileName(file), pct, alg));
                };

                return ProcessRunner.Run(_tools.Java, args, Utf8, onProgress, DecryptPercent,
                    delegate (string line)
                    {
                        if (line.StartsWith("WARNING", StringComparison.Ordinal)) return;
                        if (DecryptPercent.IsMatch(line)) return;   // 进度行进状态栏，不进日志
                        r.Log("  " + line);
                    },
                    _opt.CancelToken);
            }
            finally
            {
                try { File.Delete(pwFile); } catch { }
            }
        }

        ProcessResult Extract(string archive, string outDir, IReporter r)
        {
            return Extract(archive, outDir, r, File.Exists(_tools.SevenZip) ? _tools.SevenZip : _tools.SevenZipR);
        }

        ProcessResult Extract(string archive, string outDir, IReporter r, string tool)
        {
            string args = string.Format("x \"{0}\" -o\"{1}\" -y -bsp1 -bso1 -bse1", archive, outDir);

            ProgressHandler onProgress = delegate (int pct, string detail)
            {
                r.Percent(pct);
                r.Status("解压 " + Path.GetFileName(archive) + "  " + pct + "%");
            };

            return ProcessRunner.Run(tool, args, Ansi, onProgress, SevenZipPercent,
                delegate (string line) { }, _opt.CancelToken);
        }

        // ==================== 错误翻译 ====================

        static string ExplainDecryptError(ProcessResult pr)
        {
            switch (pr.ExitCode)
            {
                case 1: return "命令行参数不对（exit 1）。这通常是程序 bug，请把日志贴给作者。";
                case 2: return "写不进去（exit 2）。" + pr.LastLine +
                               "  —— 常见原因：输出目录没权限、磁盘满、或本程序被放在了受限目录里。";
                case 3: return "口令不对（exit 3）。换个口令再试。";
                case 6: return "文件损坏但可修（exit 6）。这一卷要重新下载。";
                case 7: return "文件损坏且修不了（exit 7）。这一卷要重新下载。";
                default: return "解密失败 exit=" + pr.ExitCode + "  " + pr.LastLine;
            }
        }

        static string ExplainExtractError(ProcessResult pr)
        {
            string tail = string.Join(" ", pr.Tail.ToArray());
            if (tail.IndexOf("Cannot create output", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tail.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0)
                return "解压时被拒绝写入。把工具和输出都放到普通用户目录下再试（别放 Program Files）。";

            if (tail.IndexOf("CRC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tail.IndexOf("Data Error", StringComparison.OrdinalIgnoreCase) >= 0)
                return "数据校验失败，包是坏的。重新下载这一卷。";

            if (tail.IndexOf("not enough space", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tail.IndexOf("磁盘空间", StringComparison.Ordinal) >= 0)
                return "磁盘空间不够。清一点再试。";

            if (tail.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tail.IndexOf("Can not open encrypted", StringComparison.OrdinalIgnoreCase) >= 0)
                return "这个压缩包本身还有一层密码（和 .enc 的口令不是一回事）。";

            return "解压失败 exit=" + pr.ExitCode + "。" + pr.LastLine;
        }

        // ==================== 杂项 ====================

        static string PickFirstArchive(List<string> files)
        {
            // 优先第一卷
            foreach (string f in files)
            {
                ContainerKind k = FileSignatures.Detect(f);
                if (FileSignatures.IsArchive(k) &&
                    (f.EndsWith(".001", StringComparison.OrdinalIgnoreCase) ||
                     Regex.IsMatch(f, @"part0?1\.rar$", RegexOptions.IgnoreCase))) return f;
            }
            foreach (string f in files)
                if (FileSignatures.IsArchive(FileSignatures.Detect(f))) return f;
            return null;
        }

        static string DescribeWithVolumeHint(string path)
        {
            ContainerKind k = FileSignatures.Detect(path);
            string label = FileSignatures.Describe(k);
            // 7z 的续卷没有魔数，靠名字认
            if (k == ContainerKind.Unknown && Regex.IsMatch(Path.GetFileName(path), @"\.\d{3}$"))
                label = "7-Zip 分卷（续卷）";
            return label;
        }

        static string CheckSpace(string dir, long need)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dir));
                DriveInfo d = new DriveInfo(root);
                if (d.AvailableFreeSpace < need)
                    return string.Format("空间不够：{0} 只剩 {1}，这次大约需要 {2}。",
                        root, PathHelper.Bytes(d.AvailableFreeSpace), PathHelper.Bytes(need));
            }
            catch { }
            return null;
        }

        static void TryDelete(string dir, IReporter r)
        {
            try
            {
                if (PathHelper.IsDir(dir)) Directory.Delete(dir, true);
                r.Log("  已清理中间目录：" + dir);
            }
            catch (Exception ex) { r.Log("  清理失败（不影响游戏）：" + ex.Message); }
        }

        /// <summary>
        /// 报错并记账。日志是给人看的，Failures 是给调用方（GUI 的概要、CLI 的 --json）
        /// 判断成败用的 —— 两处都要写，只写一处会让「失败了但 Failures 是空的」这种怪现象出现。
        /// </summary>
        static void Fail(UnpackOutcome outcome, IReporter r, string message)
        {
            r.Error(message);
            outcome.Success = false;
            if (!outcome.Failures.Contains(message)) outcome.Failures.Add(message);
        }
    }
}
