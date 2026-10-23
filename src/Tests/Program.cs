using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GalgameKit.Core;

namespace GalgameKit.Tests
{
    /// <summary>
    /// 自带的极简测试跑器。不引第三方框架（xUnit / NUnit 都要装包），
    /// 保持「clone 下来就能编、能测」。
    ///   galgame-kit-tests.exe            跑全部
    ///   galgame-kit-tests.exe Volume     只跑名字里含 Volume 的
    /// 退出码 0 全过 / 1 有失败。
    /// </summary>
    static class Program
    {
        static int _pass, _fail;
        static readonly List<string> Failures = new List<string>();

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            string filter = args.Length > 0 ? args[0] : null;

            Run(filter, "FileSignatures", TestFileSignatures);
            Run(filter, "VolumeGroup", TestVolumeGroup);
            Run(filter, "PathHelper", TestPathHelper);
            Run(filter, "Engine", TestEngine);
            Run(filter, "Launcher", TestLauncher);
            Run(filter, "ToolSet", TestToolSet);
            Run(filter, "ProcessRunner", TestProcessRunner);
            Run(filter, "Pipeline", TestPipeline);

            Console.WriteLine();
            Console.WriteLine(string.Format("通过 {0}，失败 {1}", _pass, _fail));
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return _fail == 0 ? 0 : 1;
        }

        static void Run(string filter, string name, Action body)
        {
            if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return;
            Console.WriteLine("--- " + name + " ---");
            try { body(); }
            catch (Exception ex) { Fail(name + " 抛异常: " + ex.Message); }
        }

        static void Ok(string what) { _pass++; Console.WriteLine("  ok   " + what); }

        static void Fail(string what)
        {
            _fail++;
            Failures.Add(what);
            Console.WriteLine("  FAIL " + what);
        }

        static void Eq<T>(T expected, T actual, string what)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) Ok(what);
            else Fail(what + "  期望 [" + expected + "] 实际 [" + actual + "]");
        }

        static void True(bool cond, string what)
        {
            if (cond) Ok(what); else Fail(what);
        }

        // ==================== 案例 ====================

        static void TestFileSignatures()
        {
            string dir = TempDir("sig");
            try
            {
                Eq(ContainerKind.Ssefe, DetectBytes(dir, "a.enc", new byte[] { 0x53, 0x53, 0x45, 0x46, 0x45, 0x04, 0x09, 0x11 }), "SSEFE 魔数");
                Eq(ContainerKind.SevenZip, DetectBytes(dir, "b.7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }), "7z 魔数");
                Eq(ContainerKind.Zip, DetectBytes(dir, "c.zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 }), "zip 魔数");
                Eq(ContainerKind.Rar, DetectBytes(dir, "d.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }), "rar 魔数");
                Eq(ContainerKind.KiriKiri, DetectBytes(dir, "e.xp3", Encoding.ASCII.GetBytes("XP3\r\n \n\x1a\x8b")), "xp3 魔数");
                Eq(ContainerKind.UnityAsset, DetectBytes(dir, "f.bundle", Encoding.ASCII.GetBytes("UnityFS")), "UnityFS 魔数");
                Eq(ContainerKind.PeImage, DetectBytes(dir, "g.exe", new byte[] { 0x4D, 0x5A, 0x90, 0x00 }), "PE 魔数");

                // 魔数不认识时靠扩展名兜底
                Eq(ContainerKind.SevenZip, DetectBytes(dir, "h.7z", new byte[] { 1, 2, 3, 4, 5, 6 }), "未知内容 + .7z 扩展名 → 7z");
                Eq(ContainerKind.Ssefe, DetectBytes(dir, "i.enc", new byte[] { 9, 9, 9, 9 }), "未知内容 + .enc 扩展名 → SSEFE");
                Eq(ContainerKind.Unknown, DetectBytes(dir, "j.bin", new byte[] { 9, 9, 9, 9 }), "未知内容 + 未知扩展名 → Unknown");

                True(FileSignatures.IsArchive(ContainerKind.SevenZip), "7z 算压缩包");
                True(FileSignatures.IsArchive(ContainerKind.Zip), "zip 算压缩包");
                True(!FileSignatures.IsArchive(ContainerKind.Ssefe), "SSEFE 不算压缩包");
                True(!FileSignatures.IsArchive(ContainerKind.PeImage), "PE 不算压缩包");

                Eq("加密容器（要口令）", FileSignatures.Describe(ContainerKind.Ssefe), "SSEFE 的中文描述");
            }
            finally { Cleanup(dir); }
        }

        static void TestVolumeGroup()
        {
            Eq("PC", VolumeGroup.KeyOf(@"C:\x\PC.001.enc"), "PC.001.enc → PC");
            Eq("PC", VolumeGroup.KeyOf(@"C:\x\PC.002.enc"), "PC.002.enc → PC");
            Eq("PC.7z", VolumeGroup.KeyOf(@"C:\x\PC.7z.001"), "PC.7z.001 → PC.7z");
            Eq("app.7z", VolumeGroup.KeyOf(@"C:\x\app.7z.002"), "app.7z.002 → app.7z");
            Eq("game", VolumeGroup.KeyOf(@"C:\x\game.part1.rar"), "game.part1.rar → game");
            Eq("game", VolumeGroup.KeyOf(@"C:\x\game.part2.rar"), "game.part2.rar → game");
            Eq("data", VolumeGroup.KeyOf(@"C:\x\data.z01"), "data.z01 → data");
            Eq("single", VolumeGroup.KeyOf(@"C:\x\single.zip"), "单文件 → 文件名");

            List<string> sorted = VolumeGroup.SortNaturally(new List<string> { "PC.003", "PC.001", "PC.010", "PC.002" });
            Eq("PC.001,PC.002,PC.003,PC.010", string.Join(",", sorted.ToArray()), "自然排序（010 在 003 之后）");

            // 7z 完整性校验：造一个真包再截断
            string dir = TempDir("vol");
            try
            {
                string seven = FindSevenZip();
                if (seven == null) { Console.WriteLine("  skip 找不到 7z.exe，跳过完整性校验"); return; }

                string payload = Path.Combine(dir, "payload");
                Directory.CreateDirectory(payload);
                byte[] big = new byte[300 * 1024];
                new Random(7).NextBytes(big);
                File.WriteAllBytes(Path.Combine(payload, "blob.bin"), big);

                string archive = Path.Combine(dir, "PC.7z");
                ProcessRunner.Run(seven, string.Format("a -t7z -mx1 -v100k \"{0}\" \"{1}\\*\"", archive, payload),
                    Encoding.UTF8, null, null, delegate (string s) { }, System.Threading.CancellationToken.None);

                List<string> vols = new List<string>(Directory.GetFiles(dir, "PC.7z.*"));
                vols = VolumeGroup.SortNaturally(vols);
                True(vols.Count >= 3, "生成了多个分卷（实际 " + vols.Count + "）");

                SplitCheck ok = VolumeGroup.CheckSevenZip(vols[0], vols);
                True(ok != null && ok.Complete, "完整分卷 → Complete=true");
                Eq(0L, ok.Missing, "完整分卷 → 缺 0 字节");

                // 删掉最后一卷再查
                string last = vols[vols.Count - 1];
                long lastLen = new FileInfo(last).Length;
                File.Delete(last);
                vols.RemoveAt(vols.Count - 1);
                SplitCheck bad = VolumeGroup.CheckSevenZip(vols[0], vols);
                True(bad != null && !bad.Complete, "缺一卷 → Complete=false");
                Eq(lastLen, bad.Missing, "缺的字节数正好是那一卷的大小");
            }
            finally { Cleanup(dir); }
        }

        static void TestPathHelper()
        {
            // 短路径绝不能加 \\?\ 前缀：.NET Framework 的 FileStream 在 4.0 目标框架下会报「路径中具有非法字符」
            Eq(@"D:\a\b.txt", PathHelper.Long(@"D:\a\b.txt"), "短路径 → 原样返回（不加前缀）");
            string longPath = @"D:\" + new string('x', 300);
            True(PathHelper.Long(longPath).Length > longPath.Length, "超长路径也加前缀");
            True(PathHelper.Long(@"\\srv\share\" + new string('y', 300)).StartsWith(@"\\?\UNC\"), @"超长 UNC 转成 \\?\UNC\ 前缀");

            Eq("0 B", PathHelper.Bytes(0), "0 字节");
            Eq("512 B", PathHelper.Bytes(512), "512 字节");
            Eq("1.00 KB", PathHelper.Bytes(1024), "1 KB");
            Eq("1.00 MB", PathHelper.Bytes(1024 * 1024), "1 MB");
            Eq("1.50 GB", PathHelper.Bytes((long)(1.5 * 1024 * 1024 * 1024)), "1.5 GB");

            // 真跑一遍 300 字符的路径：写进去、再枚举出来。
            // 这条只有在 exe 旁边有 .exe.config（长路径开关）时才会通过。
            string root = TempDir("longpath");
            try
            {
                string deep = root;
                while (deep.Length < 300) deep = Path.Combine(deep, "d0123456789abcdef");
                Directory.CreateDirectory(PathHelper.Long(deep));
                File.WriteAllText(PathHelper.Long(Path.Combine(deep, "x.txt")), "hi");

                True(PathHelper.IsDir(deep), "超长目录 IsDir 认得出来");
                True(PathHelper.IsFile(Path.Combine(deep, "x.txt")), "超长文件 IsFile 认得出来");

                List<string> all = PathHelper.AllFiles(root, 40);
                Eq(1, all.Count, "超长目录下的文件能被枚举到（长路径开关生效）");
                if (all.Count == 1)
                {
                    True(all[0].StartsWith(root, StringComparison.OrdinalIgnoreCase), "枚举结果带完整根路径");
                    True(!all[0].StartsWith("\\\\?\\"), "枚举结果已去掉 \\\\?\\ 前缀（7z/java 不认）");
                }
            }
            catch (Exception ex)
            {
                Fail("超长路径往返：" + ex.GetType().Name + " / " + ex.Message);
            }
            finally { Cleanup(root); }
        }

        static void TestEngine()
        {
            Eq(GameEngine.Unity, EngineDetector.Detect(new[] { @"D:\g\UnityPlayer.dll", @"D:\g\x.exe" }), "UnityPlayer.dll → Unity");
            Eq(GameEngine.RpgMakerVxAce, EngineDetector.Detect(new[] { @"D:\g\RGSS301.dll" }), "RGSS301 → VX Ace");
            Eq(GameEngine.RpgMakerXp, EngineDetector.Detect(new[] { @"D:\g\RGSS202E.dll" }), "RGSS202E → XP");
            Eq(GameEngine.RpgMakerMvMz, EngineDetector.Detect(new[] { @"D:\g\nw.dll" }), "nw.dll → MV/MZ");
            Eq(GameEngine.KiriKiri, EngineDetector.Detect(new[] { @"D:\g\data.xp3" }), "*.xp3 → KiriKiri");
            Eq(GameEngine.Godot, EngineDetector.Detect(new[] { @"D:\g\game.pck" }), "*.pck → Godot");
            Eq(GameEngine.Godot, EngineDetector.Detect(new[] { @"D:\g\MyGame.console.exe" }), "*.console.exe → Godot");
            Eq(GameEngine.Renpy, EngineDetector.Detect(new[] { @"D:\g\script.rpyc" }), "*.rpyc → Ren'Py");
            Eq(GameEngine.Unknown, EngineDetector.Detect(new[] { @"D:\g\readme.txt" }), "什么都没有 → Unknown");

            // Unity 优先级高于 Godot（有的 Unity 游戏也带 .pck 资源）
            Eq(GameEngine.Unity, EngineDetector.Detect(new[] { @"D:\g\UnityPlayer.dll", @"D:\g\x.pck" }), "Unity 优先于 Godot");

            Eq("Unity", EngineDetector.Name(GameEngine.Unity), "引擎中文名");
            Eq("未知", EngineDetector.Name(GameEngine.Unknown), "未知引擎显示「未知」");
            True(EngineDetector.SaveHint(GameEngine.Unity, null) != null, "Unity 有存档提示");
            True(EngineDetector.SaveHint(GameEngine.Unknown, null) == null, "未知引擎不给假提示");

            // 存档位置：Unity 在 LocalLow，Godot / Ren'Py 在 Roaming —— 写错会把用户指到空目录
            True(EngineDetector.SaveHint(GameEngine.Unity, null).Contains("LocalLow"), "Unity → LocalLow");
            True(EngineDetector.SaveHint(GameEngine.Godot, null).Contains("Roaming"), "Godot → Roaming");
            True(EngineDetector.SaveHint(GameEngine.Godot, null).IndexOf("LocalLow") < 0, "Godot 不能是 LocalLow");
            True(EngineDetector.SaveHint(GameEngine.Renpy, null).Contains("Roaming"), "Ren'Py → Roaming");
            Eq(null, EngineDetector.SaveHint(GameEngine.Unknown, null), "未知引擎的存档提示是 null");

            // 目录不存在时必须返回 null，绝不返回一个不存在的路径
            Eq(null, EngineDetector.GuessSaveDirectory(GameEngine.Unity, Guid.NewGuid().ToString(), "NoSuchGame"), "存档目录不存在 → 不给答案");
            Eq(null, EngineDetector.GuessSaveDirectory(GameEngine.Godot, null, ""), "标题为空 → 不给答案");
            Eq(null, EngineDetector.GuessSaveDirectory(GameEngine.RpgMakerVxAce, null, "x"), "不认识的引擎 → 不给答案");
        }

        static void TestLauncher()
        {
            string dir = TempDir("launch");
            try
            {
                string big = MakeFile(dir, "Game.exe", 5000);
                string crash = MakeFile(dir, "UnityCrashHandler64.exe", 9000);
                string redist = MakeFile(dir, "vcredist_x64.exe", 20000);
                string console = MakeFile(dir, "Game.console.exe", 30000);
                string note = MakeFile(dir, "readme.txt", 100);

                List<string> files = new List<string>(Directory.GetFiles(dir));
                Eq(big, LauncherPicker.Pick(files), "挑最大的非垃圾 exe，忽略崩溃处理器/运行库/console");

                True(LauncherPicker.LooksPlayable(files), "目录里有可用的 exe → 可玩");

                // 只有垃圾 exe 时不应误判
                string dir2 = TempDir("launch2");
                try
                {
                    MakeFile(dir2, "UnityCrashHandler64.exe", 9000);
                    MakeFile(dir2, "vcredist_x64.exe", 20000);
                    List<string> junk = new List<string>(Directory.GetFiles(dir2));
                    True(LauncherPicker.Pick(junk) == null, "只有垃圾 exe → 挑不出启动器");
                }
                finally { Cleanup(dir2); }
            }
            finally { Cleanup(dir); }
        }

        static void TestToolSet()
        {
            string root = TempDir("tools");
            try
            {
                Environment.SetEnvironmentVariable("GALGAMEKIT_TOOLS", null);
                ToolSet empty = ToolSet.Locate(root);
                Eq(root, empty.Root, "没有环境变量时根目录 = exe 目录");
                True(!empty.CanExtract, "空目录 → 不能解压");
                True(!empty.CanDecrypt, "空目录 → 不能解密");
                Eq(3, empty.Missing().Count, "空目录 → 缺 3 样（7z / java / jar）");

                // 新布局
                Directory.CreateDirectory(Path.Combine(root, "jre"));
                Directory.CreateDirectory(Path.Combine(root, Path.Combine("jre", "bin")));
                MakeFile(root, "7z.exe", 10);
                MakeFile(Path.Combine(root, Path.Combine("jre", "bin")), "java.exe", 10);
                MakeFile(root, "ssefenc.jar", 10);

                ToolSet full = ToolSet.Locate(root);
                True(full.CanExtract, "放了 7z.exe → 能解压");
                True(full.CanDecrypt, "放了 java + jar → 能解密");
                Eq(0, full.Missing().Count, "工具齐了 → 缺 0 样");

                // 老布局回退
                string old = TempDir("oldtools");
                try
                {
                    MakeFile(old, "7zr.exe", 10);
                    MakeFile(old, "ssefenc.jar", 10);
                    string admin = Path.Combine(old, "admin");
                    admin = Path.Combine(admin, "Paranoia File & Text Encryption");
                    admin = Path.Combine(admin, "runtime");
                    admin = Path.Combine(admin, "bin");
                    Directory.CreateDirectory(admin);
                    MakeFile(admin, "java.exe", 10);

                    ToolSet legacy = ToolSet.Locate(old);
                    True(legacy.CanDecrypt, "老布局的 java.exe 能被找到");
                    True(legacy.CanExtract, "只有 7zr.exe 也算能解压");
                }
                finally { Cleanup(old); }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GALGAMEKIT_TOOLS", null);
                Cleanup(root);
            }
        }

        static void TestProcessRunner()
        {
            string dir = TempDir("proc");
            try
            {
                // 用一个 cmd 内建命令，保证任何 Windows 上都能跑
                ProcessResult r = ProcessRunner.Run("cmd.exe", "/c echo hello & echo world & exit 3",
                    Encoding.UTF8, null, null, null, System.Threading.CancellationToken.None);
                Eq(3, r.ExitCode, "捕获到退出码 3");
                True(r.Seconds > 0, "记录了耗时");

                // 进度解析
                string script = "/c (for /L %i in (1,1,5) do @echo %i of 5 20%%) ";
                ProcessResult p = ProcessRunner.Run("cmd.exe", script, Encoding.UTF8, null,
                    new System.Text.RegularExpressions.Regex(@"(\d+)%"), null, System.Threading.CancellationToken.None);
                True(p.ExitCode == 0, "进度脚本正常结束");

                // 取消
                System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
                System.Threading.Timer t = new System.Threading.Timer(delegate { cts.Cancel(); }, null, 300, System.Threading.Timeout.Infinite);
                try
                {
                    DateTime t0 = DateTime.Now;
                    ProcessResult c = ProcessRunner.Run("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul",
                        Encoding.UTF8, null, null, null, cts.Token);
                    double el = (DateTime.Now - t0).TotalSeconds;
                    True(c.Cancelled, "长命令被取消");
                    True(el < 10, "取消在一秒级内生效（实际 " + el.ToString("N1") + "s）");
                }
                finally { t.Dispose(); }
            }
            finally { Cleanup(dir); }
        }

        static void TestPipeline()
        {
            string dir = TempDir("pipe");
            try
            {
                // 已经是解压好的目录
                MakeFile(dir, "Game.exe", 5000);
                MakeFile(dir, "UnityPlayer.dll", 100);
                MakeFile(dir, "Game_Data".Replace("_Data", ""), 10);
                Directory.CreateDirectory(Path.Combine(dir, "Game_Data"));

                ToolSet tools = FakeTools(dir);
                UnpackPipeline pipe = new UnpackPipeline(tools, new Options { DryRun = true });
                NullReporter rep = new NullReporter();
                UnpackOutcome res = pipe.Run(dir, rep);

                True(res.Success, "已解压目录 → 成功");
                True(res.Playable, "已解压目录 → 能找到启动器");
                Eq(GameEngine.Unity, res.Engine, "已解压目录 → 引擎 Unity");
                True(Join(rep.Lines).Contains("不需要再解包"), "提示「不需要再解包」");

                // 不存在的路径
                NullReporter rep2 = new NullReporter();
                UnpackOutcome r2 = new UnpackPipeline(tools, new Options()).Run(Path.Combine(dir, "nope"), rep2);
                True(!r2.Success, "路径不存在 → 失败");
                True(r2.Failures.Count > 0, "路径不存在 → 有失败原因");

                // 缺工具：必须是真要解包时才报缺。
                // 已解压目录上面那次已经证明过「没工具也能报告目录内容」，这里换成真的加密包。
                string enc = TempDir("pipe-enc");
                try
                {
                    MakeFile(enc, "PC.001.enc", 4096);

                    NullReporter rep3 = new NullReporter();
                    UnpackOutcome r3 = new UnpackPipeline(new ToolSet { Root = Path.Combine(dir, "empty") }, new Options())
                        .Run(enc, rep3);
                    True(!r3.Success, "缺工具 → 失败");
                    True(Join(rep3.Lines).Contains("缺少工具"), "缺工具 → 明确说缺什么");
                    True(Join(rep3.Lines).Contains("ssefenc"), "缺工具 → 指出要 ssefenc.jar");

                    // 体检模式不该被工具缺失挡住：它本来就是用来看缺什么的
                    NullReporter repDry = new NullReporter();
                    UnpackOutcome rDry = new UnpackPipeline(new ToolSet { Root = Path.Combine(dir, "empty") },
                        new Options { DryRun = true }).Run(enc, repDry);
                    True(rDry.Success, "缺工具 + 体检模式 → 仍然成功");
                    True(Join(repDry.Lines).Contains("真正开箱时会缺"), "缺工具 + 体检模式 → 提前告知会缺什么");

                    // 只需要解压时不必要求 Java
                    string plain = TempDir("pipe-7z");
                    try
                    {
                        MakeFile(plain, "pack.7z", 2048);
                        NullReporter rep7 = new NullReporter();
                        UnpackOutcome r7 = new UnpackPipeline(new ToolSet { Root = Path.Combine(dir, "empty") }, new Options())
                            .Run(plain, rep7);
                        True(!r7.Success, "只有 7z 包 + 没工具 → 失败");
                        True(!Join(rep7.Lines).Contains("ssefenc"), "只有 7z 包 → 不该要求 ssefenc.jar");
                    }
                    finally { Cleanup(plain); }
                }
                finally { Cleanup(enc); }

                // 空目录
                string empty = TempDir("pipe-empty");
                try
                {
                    NullReporter rep4 = new NullReporter();
                    new UnpackPipeline(tools, new Options()).Run(empty, rep4);
                    True(Join(rep4.Lines).Contains("没有可处理的文件"), "空目录 → 如实说明");
                }
                finally { Cleanup(empty); }
            }
            finally { Cleanup(dir); }
        }

        // ==================== 小工具 ====================

        static ToolSet FakeTools(string dir)
        {
            string t = Path.Combine(dir, "_tools");
            Directory.CreateDirectory(t);
            MakeFile(t, "7z.exe", 10);
            MakeFile(t, "ssefenc.jar", 10);
            string j = Path.Combine(t, "jre");
            j = Path.Combine(j, "bin");
            Directory.CreateDirectory(j);
            MakeFile(j, "java.exe", 10);
            return ToolSet.Locate(t);
        }

        static ContainerKind DetectBytes(string dir, string name, byte[] head)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, head);
            return FileSignatures.Detect(p);
        }

        static string MakeFile(string dir, string name, int bytes)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            byte[] buf = new byte[bytes];
            new Random(bytes).NextBytes(buf);
            File.WriteAllBytes(p, buf);
            return p;
        }

        static string TempDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(),
                "gktest-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Cleanup(string dir)
        {
            // 超长路径要用前缀形式删，否则 Directory.Delete 会 PathTooLongException，
            // 测试目录就永久留在 %TEMP% 里。
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                else if (Directory.Exists(PathHelper.Long(dir))) Directory.Delete(PathHelper.Long(dir), true);
            }
            catch { }
        }

        static string Join(List<string> lines)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string l in lines) sb.AppendLine(l);
            return sb.ToString();
        }

        static string FindSevenZip()
        {
            // 只走环境变量和 exe 同目录，不写死某个人的机器路径 ——
            // 找不到就跳过依赖真实 7z 的那几条测试，不算失败。
            string env = Environment.GetEnvironmentVariable("GALGAMEKIT_TOOLS");
            string[] candidates = new[]
            {
                env == null ? null : Path.Combine(env, "7z.exe"),
                Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "7z.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7z.exe")
            };
            foreach (string c in candidates)
                if (c != null && File.Exists(c)) return c;
            return null;
        }
    }
}
