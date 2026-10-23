using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GalgameKit.Core
{
    public sealed class ProcessResult
    {
        public int ExitCode;
        public string LastLine = "";
        public List<string> Tail = new List<string>();
        public double Seconds;
        public bool Cancelled;
        public string CommandLine = "";

        public override string ToString()
        {
            return "exit=" + ExitCode + (Cancelled ? " (cancelled)" : "") +
                   " " + Seconds.ToString("N1") + "s" + (LastLine.Length > 0 ? " | " + LastLine : "");
        }
    }

    /// <summary>进度回调：0-100 与一行给用户看的说明。</summary>
    public delegate void ProgressHandler(int percent, string detail);

    /// <summary>
    /// 跑外部程序并解析进度。三条原则：
    /// 1. stdout / stderr 都重定向 —— 否则一部分工具会把输出写进不存在的控制台而丢日志
    /// 2. 用事件读，不 ReadToEnd —— 13 GB 的解压输出可能几十万行，ReadToEnd 会先填满管道再死锁
    /// 3. 取消时 Kill 整个进程树 —— java 可能自己拉子进程
    /// </summary>
    public static class ProcessRunner
    {
        /// <summary>外部程序的输出编码可能不是 UTF-8（7z 用 ANSI 代码页）。传 null 表示用系统默认。</summary>
        public static ProcessResult Run(
            string exe,
            string arguments,
            Encoding outputEncoding,
            ProgressHandler onProgress,
            Regex percentPattern,
            Action<string> onLine,
            System.Threading.CancellationToken ct,
            int tailKeep = 8)
        {
            ProcessResult res = new ProcessResult();
            res.CommandLine = "\"" + exe + "\" " + arguments;
            Stopwatch sw = Stopwatch.StartNew();

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            if (outputEncoding != null)
            {
                psi.StandardOutputEncoding = outputEncoding;
                psi.StandardErrorEncoding = outputEncoding;
            }

            object gate = new object();

            using (Process p = new Process())
            {
                p.StartInfo = psi;

                DataReceivedEventHandler handler = delegate (object s, DataReceivedEventArgs a)
                {
                    if (a.Data == null) return;
                    string line = a.Data.Trim();
                    if (line.Length == 0) return;

                    lock (gate)
                    {
                        res.LastLine = line;
                        res.Tail.Add(line);
                        while (res.Tail.Count > tailKeep) res.Tail.RemoveAt(0);
                    }

                    if (onLine != null) { try { onLine(line); } catch { } }

                    if (percentPattern != null && onProgress != null)
                    {
                        Match m = percentPattern.Match(line);
                        if (m.Success && m.Groups.Count > 1)
                        {
                            int v;
                            if (int.TryParse(m.Groups[m.Groups.Count - 1].Value, out v))
                            {
                                string detail = m.Groups.Count > 2 ? m.Groups[1].Value : line;
                                try { onProgress(v, detail); } catch { }
                            }
                        }
                    }
                };

                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (!p.WaitForExit(150))
                {
                    if (ct.IsCancellationRequested)
                    {
                        res.Cancelled = true;
                        KillTree(p);
                        break;
                    }
                }
                try { p.WaitForExit(); } catch { }
                try { res.ExitCode = p.ExitCode; } catch { res.ExitCode = -1; }
            }

            sw.Stop();
            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        /// <summary>
        /// 连子进程一起杀。只 Kill 父进程是不够的：`cmd /c ping ...` 里的 ping
        /// 会继续跑满整段时间，而 WaitForExit 会被继承的输出管道拖住，
        /// 让「取消」看起来过了 30 秒才生效。
        /// </summary>
        static void KillTree(Process p)
        {
            // Process.Kill(bool) 是 .NET 4.5 才有的 API，而本机只有 4.0 的 csc
            // 参照程序集，所以走 taskkill /T 这一步。
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill",
                    "/T /F /PID " + p.Id.ToString());
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process k = Process.Start(psi)) { try { k.WaitForExit(3000); } catch { } }
            }
            catch { }
            try { p.Kill(); } catch { }
        }
    }
}
