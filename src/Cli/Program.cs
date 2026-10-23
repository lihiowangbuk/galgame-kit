using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GalgameKit.Core;

namespace GalgameKit.Cli
{
    /// <summary>
    /// 命令行前端。用法：
    ///   galgame-kit-cli.exe [选项] &lt;目录&gt;
    /// 选项：
    ///   -p, --password &lt;口令&gt;   解密口令
    ///   --dry-run               只体检，不动手
    ///   --clean                 解完删掉中间文件
    ///   -o, --output &lt;目录&gt;     指定输出根目录
    ///   --launch                结束后直接启动游戏
    ///   --json                  把结果打成 JSON（给脚本用）
    ///   -v, --verbose           把外部程序的每一行输出都打出来
    /// 退出码：0 成功 / 1 失败 / 2 参数错误
    /// </summary>
    static class Program
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Utf8; } catch { }

            Options opt = new Options();
            string target = null;
            bool launch = false, json = false, verbose = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h":
                    case "--help":
                        Usage();
                        return 0;
                    case "-v":
                    case "--verbose":
                        verbose = true;
                        break;
                    case "--dry-run":
                        opt.DryRun = true;
                        break;
                    case "--clean":
                        opt.KeepIntermediate = false;
                        break;
                    case "--launch":
                        launch = true;
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "-p":
                    case "--password":
                        if (++i >= args.Length) { Console.Error.WriteLine("缺少口令值"); return 2; }
                        opt.Password = args[i];
                        break;
                    case "-o":
                    case "--output":
                        if (++i >= args.Length) { Console.Error.WriteLine("缺少输出目录"); return 2; }
                        opt.OutputRoot = args[i];
                        break;
                    default:
                        if (a.StartsWith("-") && a.Length > 1) { Console.Error.WriteLine("不认识的选项：" + a); return 2; }
                        target = a;
                        break;
                }
            }

            if (target == null)
            {
                Usage();
                return 2;
            }

            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            ToolSet tools = ToolSet.Locate(exeDir);

            ConsoleReporter rep = new ConsoleReporter(verbose);
            UnpackPipeline pipe = new UnpackPipeline(tools, opt);
            UnpackOutcome res = pipe.Run(target, rep);

            if (json) Console.WriteLine(ToJson(res));

            if (launch && res.Playable)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(res.Launcher)
                    {
                        WorkingDirectory = Path.GetDirectoryName(res.Launcher),
                        UseShellExecute = true
                    });
                }
                catch (Exception ex) { Console.Error.WriteLine("启动失败：" + ex.Message); }
            }

            return res.Success ? 0 : 1;
        }

        static void Usage()
        {
            Console.WriteLine("齿轮开箱器 · 命令行版");
            Console.WriteLine();
            Console.WriteLine("  galgame-kit-cli.exe [选项] <目录>");
            Console.WriteLine();
            Console.WriteLine("  -p, --password <口令>   解密口令（.enc 容器需要）");
            Console.WriteLine("      --dry-run           只体检，不动手");
            Console.WriteLine("      --clean             解完删掉中间文件");
            Console.WriteLine("  -o, --output <目录>     指定输出根目录（默认 <目录>\\_unpack）");
            Console.WriteLine("      --launch            结束后直接启动游戏");
            Console.WriteLine("      --json              结果打成 JSON");
            Console.WriteLine("  -v, --verbose           打印外部程序每一行输出");
            Console.WriteLine();
            Console.WriteLine("  退出码：0 成功 / 1 失败 / 2 参数错误");
        }

        static string ToJson(UnpackOutcome r)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"success\":").Append(r.Success ? "true" : "false").Append(",");
            sb.Append("\"gameDirectory\":").Append(JsonStr(r.GameDirectory)).Append(",");
            sb.Append("\"launcher\":").Append(JsonStr(r.Launcher)).Append(",");
            sb.Append("\"engine\":").Append(JsonStr(EngineDetector.Name(r.Engine))).Append(",");
            sb.Append("\"totalFiles\":").Append(r.TotalFiles).Append(",");
            sb.Append("\"totalBytes\":").Append(r.TotalBytes).Append(",");
            sb.Append("\"seconds\":").Append(r.Seconds.ToString("0.0")).Append(",");
            sb.Append("\"failures\":").Append(JsonArr(r.Failures));
            sb.Append("}");
            return sb.ToString();
        }

        static string JsonStr(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }

        static string JsonArr(List<string> items)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(JsonStr(items[i]));
            }
            return sb.Append("]").ToString();
        }
    }

    /// <summary>
    /// 控制台汇报。不用 Console.Write("\r...") 画进度条 —— 在重定向到文件、
    /// 或被别的程序调用时会变成一堆噪声。这里只按需打印。
    /// </summary>
    sealed class ConsoleReporter : IReporter
    {
        readonly bool _verbose;
        int _lastBucket = -1;

        public ConsoleReporter(bool verbose) { _verbose = verbose; }

        public void Log(string message)
        {
            Console.WriteLine(message);
        }

        public void Status(string message)
        {
            if (_verbose) Console.WriteLine("  · " + message);
        }

        /// <summary>每 10% 打一行，避免刷屏。</summary>
        public void Percent(int value)
        {
            int bucket = value / 10;
            if (bucket == _lastBucket) return;
            _lastBucket = bucket;
            if (_verbose || value == 0) return;
            Console.WriteLine(string.Format("  · {0,3}%", value));
        }

        public void Error(string message)
        {
            Console.Error.WriteLine("[错误] " + message);
        }

        public bool Cancelled { get { return false; } }
    }
}
