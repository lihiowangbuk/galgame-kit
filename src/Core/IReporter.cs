using System;
using System.Collections.Generic;

namespace GalgameKit.Core
{
    /// <summary>
    /// 进度汇报接口。核心逻辑只认这个接口，不认 WinForms ——
    /// 所以同一套流程可以接图形界面、接命令行、接单元测试。
    /// </summary>
    public interface IReporter
    {
        /// <summary>一段正常日志。</summary>
        void Log(string message);

        /// <summary>当前在做什么（会显示在状态行）。</summary>
        void Status(string message);

        /// <summary>进度 0-100。</summary>
        void Percent(int value);

        /// <summary>出错。返回之后流程会停下当前这一组，继续处理下一组。</summary>
        void Error(string message);

        /// <summary>用户点了取消。</summary>
        bool Cancelled { get; }
    }

    /// <summary>把 IReporter 收进一个 Null 实现，方便测试与库调用。</summary>
    public sealed class NullReporter : IReporter
    {
        public readonly List<string> Lines = new List<string>();
        public string LastStatus = "";
        public int LastPercent;
        public readonly List<string> Errors = new List<string>();

        public void Log(string message) { Lines.Add(message); }
        public void Status(string message) { LastStatus = message; }
        public void Percent(int value) { LastPercent = value; }
        public void Error(string message) { Errors.Add(message); Lines.Add("[错误] " + message); }
        public bool Cancelled { get { return false; } }
    }
}
