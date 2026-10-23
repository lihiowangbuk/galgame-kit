using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using GalgameKit.Core;

namespace GalgameKit.Gui
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    /// <summary>
    /// 图形界面。所有实际逻辑都在 GalgameKit.Core，这里只负责摆控件和把回调切回 UI 线程。
    /// </summary>
    public class MainForm : Form
    {
        // ---------- 控件 ----------
        Panel dropZone;
        Label dropLabel;
        TextBox txtPath;
        Button btnBrowse;
        TextBox txtPassword;
        Button btnStart;
        Button btnStop;
        ProgressBar bar;
        Label lblStatus;
        TextBox txtLog;
        Button btnOpenDir;
        Button btnLaunch;
        CheckBox chkKeepTemp;

        // ---------- 状态 ----------
        string _launcher = null;
        string _gameDir = null;
        bool _running = false;
        CancellationTokenSource _cts;
        string[] _startupArgs;
        string _logFile;
        float _s = 1f;
        Settings _settings;

        int Px(float v) { return (int)Math.Round(v * _s); }

        public MainForm(string[] args)
        {
            _startupArgs = args ?? new string[0];
            _logFile = Path.Combine(Path.GetTempPath(), "galgame-kit.log");

            Text = "齿轮开箱器 —— .enc 解密 + 解压 + 启动";
            AutoScaleMode = AutoScaleMode.None;

            try
            {
                // 用窗口句柄取真实 DPI；CreateGraphics() 在主窗体还没句柄时会拿到 96
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _s = g.DpiX / 96f;
            }
            catch { }
            if (_s <= 0 || _s > 4) _s = 1f;

            BackColor = Color.FromArgb(30, 32, 36);
            ForeColor = Color.Gainsboro;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(Px(790), Px(580));
            MinimumSize = new Size(Px(660), Px(500));
            StartPosition = FormStartPosition.CenterScreen;

            _settings = Settings.Load(AppDir);

            BuildUi();

            FormClosing += delegate
            {
                _settings.KeepIntermediate = chkKeepTemp.Checked;
                _settings.Save(AppDir);
                if (_cts != null) { try { _cts.Cancel(); } catch { } }
            };

            Shown += delegate
            {
                AutoDetect();
                if (AutoMode) DoStart();
            };
        }

        static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        bool AutoMode
        {
            get { return HasArg("--auto"); }
        }

        /// <summary>自动跑完不关窗（用来截图 / 演示）。</summary>
        bool KeepOpen
        {
            get { return HasArg("--keep-open"); }
        }

        bool HasArg(string name)
        {
            foreach (string a in _startupArgs) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        string ArgValue(string name)
        {
            for (int i = 0; i < _startupArgs.Length - 1; i++)
                if (string.Equals(_startupArgs[i], name, StringComparison.OrdinalIgnoreCase)) return _startupArgs[i + 1];
            return null;
        }

        // ==================== UI ====================

        void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 8;
            root.Padding = new Padding(Px(12));
            root.BackColor = Color.FromArgb(30, 32, 36);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(84)));   // 拖放区
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(34)));   // 目录
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(34)));   // 口令
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(34)));   // 按钮 + 进度
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(24)));   // 状态
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));      // 日志
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(38)));   // 底栏
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(20)));   // 版本
            Controls.Add(root);

            // ---------- 拖放区 ----------
            dropZone = new Panel();
            dropZone.Dock = DockStyle.Fill;
            dropZone.Margin = new Padding(0, 0, 0, Px(8));
            dropZone.BackColor = Color.FromArgb(44, 47, 53);
            dropZone.BorderStyle = BorderStyle.FixedSingle;
            AllowDropOn(dropZone);
            dropZone.Click += Browse;

            dropLabel = new Label();
            dropLabel.Dock = DockStyle.Fill;
            dropLabel.TextAlign = ContentAlignment.MiddleCenter;
            dropLabel.Font = new Font("Microsoft YaHei UI", 10.5f);
            dropLabel.ForeColor = Color.Silver;
            dropLabel.Text = "把下载下来的文件夹（或 .enc 文件）拖到这里\n也可以点一下这里选择";
            AllowDropOn(dropLabel);
            dropLabel.Click += Browse;
            dropZone.Controls.Add(dropLabel);
            root.Controls.Add(dropZone, 0, 0);

            // ---------- 目录行 ----------
            TableLayoutPanel rowPath = NewRow(3);
            rowPath.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(48)));
            rowPath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rowPath.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(92)));

            rowPath.Controls.Add(MakeLabel("目录"), 0, 0);

            txtPath = MakeText();
            txtPath.Margin = new Padding(0, Px(5), Px(8), Px(5));
            AllowDropOn(txtPath);
            rowPath.Controls.Add(txtPath, 1, 0);

            btnBrowse = MakeButton("浏览…");
            btnBrowse.Margin = new Padding(0, Px(3), 0, Px(3));
            btnBrowse.Click += Browse;
            rowPath.Controls.Add(btnBrowse, 2, 0);
            root.Controls.Add(rowPath, 0, 1);

            // ---------- 口令行 ----------
            TableLayoutPanel rowPw = NewRow(3);
            rowPw.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(48)));
            rowPw.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(240)));
            rowPw.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            rowPw.Controls.Add(MakeLabel("口令"), 0, 0);

            txtPassword = MakeText();
            txtPassword.Margin = new Padding(0, Px(5), Px(8), Px(5));
            txtPassword.Text = _settings.DefaultPassword ?? "";
            rowPw.Controls.Add(txtPassword, 1, 0);

            Label hint = MakeLabel("没加密就留空；加密的 .enc 才需要");
            hint.ForeColor = Color.Gray;
            rowPw.Controls.Add(hint, 2, 0);
            root.Controls.Add(rowPw, 0, 2);

            // ---------- 开始 + 停止 + 进度 ----------
            TableLayoutPanel rowAct = newRow3_5();
            rowAct.ColumnCount = 3;
            rowAct.ColumnStyles.Clear();
            rowAct.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(110)));
            rowAct.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(78)));
            rowAct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            btnStart = MakeButton("开始开箱");
            btnStart.BackColor = Color.FromArgb(58, 108, 176);
            btnStart.ForeColor = Color.White;
            btnStart.Margin = new Padding(0, Px(3), Px(8), Px(3));
            btnStart.Click += delegate { DoStart(); };
            rowAct.Controls.Add(btnStart, 0, 0);

            btnStop = MakeButton("停止");
            btnStop.Enabled = false;
            btnStop.Margin = new Padding(0, Px(3), Px(10), Px(3));
            btnStop.Click += delegate
            {
                if (_cts != null) { try { _cts.Cancel(); } catch { } }
            };
            rowAct.Controls.Add(btnStop, 1, 0);

            bar = new ProgressBar();
            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0, Px(8), 0, Px(8));
            rowAct.Controls.Add(bar, 2, 0);
            root.Controls.Add(rowAct, 0, 3);

            // ---------- 状态行 ----------
            lblStatus = MakeLabel("待命");
            lblStatus.ForeColor = Color.Silver;
            lblStatus.AutoEllipsis = true;
            root.Controls.Add(lblStatus, 0, 4);

            // ---------- 日志 ----------
            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Margin = new Padding(0, Px(2), 0, Px(6));
            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.FromArgb(22, 24, 27);
            txtLog.ForeColor = Color.FromArgb(180, 220, 180);
            txtLog.Font = new Font("Consolas", 9f);
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.WordWrap = true;
            root.Controls.Add(txtLog, 0, 5);

            // ---------- 底栏 ----------
            TableLayoutPanel rowBt = NewRow(3);
            rowBt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(132)));
            rowBt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(120)));
            rowBt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            btnOpenDir = MakeButton("打开游戏目录");
            btnOpenDir.Enabled = false;
            btnOpenDir.Margin = new Padding(0, Px(3), Px(8), Px(3));
            btnOpenDir.Click += delegate { OpenGameDir(); };
            rowBt.Controls.Add(btnOpenDir, 0, 0);

            btnLaunch = MakeButton("启动游戏");
            btnLaunch.BackColor = Color.FromArgb(46, 122, 74);
            btnLaunch.ForeColor = Color.White;
            btnLaunch.Enabled = false;
            btnLaunch.Margin = new Padding(0, Px(3), Px(8), Px(3));
            btnLaunch.Click += delegate { LaunchGame(); };
            rowBt.Controls.Add(btnLaunch, 1, 0);

            chkKeepTemp = new CheckBox();
            chkKeepTemp.Text = "保留解密出的中间文件";
            chkKeepTemp.Checked = _settings.KeepIntermediate;
            chkKeepTemp.ForeColor = Color.Gray;
            chkKeepTemp.TextAlign = ContentAlignment.MiddleLeft;
            chkKeepTemp.Dock = DockStyle.Fill;
            rowBt.Controls.Add(chkKeepTemp, 2, 0);
            root.Controls.Add(rowBt, 0, 6);

            Label ver = MakeLabel(Version + "   ·   工具目录：" + ToolSet.Locate(AppDir).Root);
            ver.ForeColor = Color.FromArgb(90, 94, 100);
            ver.Font = new Font("Microsoft YaHei UI", 8f);
            root.Controls.Add(ver, 0, 7);
        }

        // newRow3_5 只是个名字，避免和上面的局部变量重名
        static TableLayoutPanel newRow3_5()
        {
            TableLayoutPanel t = NewRow(3);
            return t;
        }

        static TableLayoutPanel NewRow(int cols)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = cols;
            t.RowCount = 1;
            t.Margin = new Padding(0);
            t.Padding = new Padding(0);
            t.BackColor = Color.Transparent;
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            for (int i = 0; i < cols; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            return t;
        }

        Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.ForeColor = Color.Gainsboro;
            return l;
        }

        TextBox MakeText()
        {
            TextBox t = new TextBox();
            t.Dock = DockStyle.Fill;
            t.BackColor = Color.FromArgb(44, 47, 53);
            t.ForeColor = Color.Gainsboro;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }

        Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Height = Px(26);
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(52, 56, 63);
            b.ForeColor = Color.Gainsboro;
            b.FlatAppearance.BorderColor = Color.FromArgb(78, 84, 94);
            b.UseVisualStyleBackColor = false;
            return b;
        }

        static string Version
        {
            get
            {
                try
                {
                    System.Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    return "v" + v.Major + "." + v.Minor + "." + v.Build;
                }
                catch { return "v1.0.0"; }
            }
        }

        // ==================== 拖放与选择 ====================

        void AllowDropOn(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragLeave += delegate { dropZone.BackColor = Color.FromArgb(44, 47, 53); };
            c.DragDrop += OnDragDrop;
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                dropZone.BackColor = Color.FromArgb(56, 74, 98);
            }
            else e.Effect = DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            dropZone.BackColor = Color.FromArgb(44, 47, 53);
            string[] items = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (items == null || items.Length == 0) return;

            string p = items[0];
            // 拖进来的是文件时，用它所在目录
            if (PathHelper.IsFile(p)) p = Path.GetDirectoryName(p);
            SelectPath(p);
        }

        void Browse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择下载下来的那个文件夹";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) == DialogResult.OK) SelectPath(d.SelectedPath);
            }
        }

        void SelectPath(string p)
        {
            txtPath.Text = p;
            string name;
            try { name = new DirectoryInfo(p.TrimEnd('\\')).Name; }
            catch { name = p; }
            dropLabel.Text = "已选择：" + name + "\n（点「开始开箱」）";
            Log("--- 已选择：" + p);
        }

        /// <summary>命令行带了路径就直接填进去。</summary>
        void AutoDetect()
        {
            string p = ArgValue("--path");
            if (string.IsNullOrEmpty(p)) p = JoinPathArgs(_startupArgs);
            if (!string.IsNullOrEmpty(p) && (PathHelper.IsDir(p) || PathHelper.IsFile(p)))
            {
                if (PathHelper.IsFile(p)) p = Path.GetDirectoryName(p);
                SelectPath(p);
            }
            string pw = ArgValue("--password");
            if (!string.IsNullOrEmpty(pw)) txtPassword.Text = pw;
        }

        /// <summary>
        /// 把被空格拆散的命令行参数重新拼成一条路径。
        /// 从长到短尝试，第一个真实存在的就是它。
        /// </summary>
        static string JoinPathArgs(string[] args)
        {
            if (args == null || args.Length == 0) return null;
            List<string> parts = new List<string>();
            foreach (string a in args)
            {
                if (a.StartsWith("--")) break;
                parts.Add(a.Trim('"'));
            }
            if (parts.Count == 0) return null;

            for (int n = parts.Count; n >= 1; n--)
            {
                string cand = string.Join(" ", parts.GetRange(0, n).ToArray());
                if (PathHelper.IsDir(cand) || PathHelper.IsFile(cand)) return cand;
            }
            return string.Join(" ", parts.ToArray());
        }

        // ==================== 执行 ====================

        void DoStart()
        {
            if (_running) return;

            string dir = txtPath.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(dir) || !PathHelper.IsDir(dir))
            {
                Fail("先选一个存在的文件夹。");
                return;
            }

            ToolSet tools = ToolSet.Locate(AppDir);
            List<string> missing = tools.Missing();
            if (missing.Count > 0)
            {
                Fail("缺少工具：" + string.Join("、", missing.ToArray()) +
                     "\n请把它们和本程序放在同一个目录里。");
                return;
            }

            _running = true;
            _launcher = null;
            _gameDir = null;
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            btnLaunch.Enabled = false;
            btnOpenDir.Enabled = false;
            bar.Value = 0;

            Options opt = new Options();
            opt.Password = txtPassword.Text.Trim();
            opt.KeepIntermediate = chkKeepTemp.Checked;

            _cts = new CancellationTokenSource();
            opt.CancelToken = _cts.Token;

            Log("");
            Log("================ 开始 " + DateTime.Now.ToString("HH:mm:ss") + " ================");

            UnpackPipeline pipe = new UnpackPipeline(tools, opt);
            ThreadPool.QueueUserWorkItem(delegate
            {
                UiReporter rep = new UiReporter(this, _cts.Token);
                UnpackOutcome res = pipe.Run(dir, rep);
                Finish(res);
            });
        }

        void Fail(string message)
        {
            if (AutoMode)
            {
                Log("[错误] " + message.Replace("\n", " "));
                Log("[自动] 结束，退出码 1");
                Environment.ExitCode = 1;
                Close();
                return;
            }
            MessageBox.Show(this, message, "齿轮开箱器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void Finish(UnpackOutcome res)
        {
            try
            {
                Invoke((Action)delegate
                {
                    _running = false;
                    btnStart.Enabled = true;
                    btnStop.Enabled = false;
                    _launcher = res.Launcher;
                    _gameDir = res.GameDirectory;
                    btnLaunch.Enabled = res.Playable;
                    btnOpenDir.Enabled = !string.IsNullOrEmpty(_gameDir);

                    if (res.Success) bar.Value = 100;

                    if (res.Playable) Status("完成 —— 可以点「启动游戏」了");
                    else if (res.Success) Status("完成（但没找到启动程序）");
                    else Status("结束（有未完成的组，看上面的错误）");

                    if (AutoMode)
                    {
                        int code = res.Success ? 0 : 1;
                        Log("[自动] 结束，退出码 " + code);
                        Environment.ExitCode = code;
                        if (!KeepOpen) Close();
                    }
                });
            }
            catch { }
        }

        void OpenGameDir()
        {
            try
            {
                if (!string.IsNullOrEmpty(_launcher) && PathHelper.IsFile(_launcher))
                    Process.Start("explorer.exe", "/select,\"" + _launcher + "\"");
                else if (!string.IsNullOrEmpty(_gameDir) && PathHelper.IsDir(_gameDir))
                    Process.Start("explorer.exe", "\"" + _gameDir + "\"");
            }
            catch (Exception ex) { Log("[错误] " + ex.Message); }
        }

        void LaunchGame()
        {
            if (string.IsNullOrEmpty(_launcher) || !PathHelper.IsFile(_launcher)) return;
            try
            {
                Process.Start(new ProcessStartInfo(_launcher)
                {
                    WorkingDirectory = Path.GetDirectoryName(_launcher),
                    UseShellExecute = true
                });
                Log("已启动：" + _launcher);
            }
            catch (Exception ex) { Log("[错误] 启动失败：" + ex.Message); }
        }

        // ==================== UI 线程辅助 ====================

        internal void AppendLog(string s)
        {
            if (txtLog.InvokeRequired)
            {
                try { txtLog.BeginInvoke((Action)delegate { AppendLog(s); }); } catch { }
                return;
            }
            txtLog.AppendText(s + Environment.NewLine);
            try { File.AppendAllText(_logFile, s + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        }

        internal void SetStatus(string s)
        {
            if (lblStatus.InvokeRequired)
            {
                try { lblStatus.BeginInvoke((Action)delegate { lblStatus.Text = s; }); } catch { }
                return;
            }
            lblStatus.Text = s;
        }

        internal void SetBar(int v)
        {
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            if (bar.InvokeRequired)
            {
                try { bar.BeginInvoke((Action)delegate { try { bar.Value = v; } catch { } }); } catch { }
                return;
            }
            try { bar.Value = v; } catch { }
        }

        void Log(string s) { AppendLog(s); }
        void Status(string s) { SetStatus(s); }
    }

    /// <summary>把核心库的回调切到 UI 线程。</summary>
    sealed class UiReporter : IReporter
    {
        readonly MainForm _f;
        readonly CancellationToken _ct;
        public UiReporter(MainForm f, CancellationToken ct) { _f = f; _ct = ct; }

        public void Log(string message) { _f.AppendLog(message); }
        public void Status(string message) { _f.SetStatus(message); }
        public void Percent(int value) { _f.SetBar(value); }
        public void Error(string message) { _f.AppendLog("[错误] " + message); }
        public bool Cancelled { get { return _ct.IsCancellationRequested; } }
    }
}
