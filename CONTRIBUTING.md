# 参与这个项目

谢谢你愿意花时间。这个项目很小，规则也就几条。

## 报问题之前

先看一眼 [README](README.md) 的「已知限制」和 [开箱流程](docs/开箱流程.md) 的故障对照表 ——
有一半的问题在那两张表里已经有答案了。

报的时候请带上：

- **这个包是哪一种**：把文件头前 8 个字节贴出来（十六进制），比说「打不开」有用一百倍。
  ```powershell
  Format-Hex -Path 'PC.001.enc' | Select-Object -First 1
  ```
- **完整日志**。图形界面的日志窗口全选复制即可；它同时也写进了 `%TEMP%\galgame-kit.log`。
- **做了什么**：拖的哪个目录、口令有没有填、是不是已经解压好的游戏目录。
- 命令行版加 `--dry-run` 跑一遍，把输出贴上——这一步不写盘，能直接看出它认成了什么。

**别把口令贴出来**，也别贴 `.enc` 的下载链接。

## 提代码

1. 先开个 issue 说一声你要改什么，避免两个人做同一件事。
2. Fork → 建分支 → 改 → 提 PR。
3. 改完请自己跑一遍：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Test
   ```

   **90 条测试必须全绿**。这个项目没有 CI，跑没跑全靠自觉。

### 代码上的几条硬约束

这几个坑都真踩过，不是洁癖：

- **不要用 C# 6 以上的语法**。编译用的是系统自带 `csc.exe`（.NET Framework 4.0 的编译器），
  字符串插值、`?.`、表达式体成员、`nameof` 全都不认。写不了就写不了，这是「双击就能编译」的代价。
- **改完 `.ps1` 必须存成 UTF-8 with BOM**。无 BOM 时 Windows PowerShell 5.1 按 GBK 解释，
  报一堆 `Unexpected token` / `The hash literal was incomplete`：
  ```powershell
  $p = 'scripts\build.ps1'
  $t = [System.IO.File]::ReadAllText($p, [System.Text.UTF8Encoding]::new($false))
  [System.IO.File]::WriteAllText($p, $t, [System.Text.UTF8Encoding]::new($true))
  ```
- **`.cmd` 文件只能是纯 ASCII**。写中文注释会被 cmd.exe 的代码页拆碎，报
  `'xxx' is not recognized as an internal or external command`。
- **核心逻辑不碰 UI**。`src/Core/` 里不许 `using System.Windows.Forms`，
  所有输出走 `IReporter`。这条一旦破了，命令行版和单元测试就全废。
- **不要往仓库里放第三方二进制**。`7z.exe` / `ssefenc.jar` / JRE 一律由用户自己准备，
  `.gitignore` 里已经排除了。

### 加一种新的容器格式

只需动两个地方：

1. `src/Core/FileSignatures.cs` 的 `Table` 里加一条魔数；
2. `UnpackPipeline.RunGroup` 里决定它要不要额外的解密/解压步骤。

顺手在 `src/Tests/Program.cs` 补三条测试：认得出来、认不出来时不误判、`Describe()` 文案非空。

### 加一种新引擎

`src/Core/EngineDetector.cs` 里加一个 `GameEngine` 枚举值、`Detect` 的判据、`Name` 的中文名、
`SaveHint` 的存档位置。测试里补一对「有特征文件 → 认出来」和「没有 → 不误认」。

## 文档

文档和代码一样要维护。改了行为就顺手改 README 和 `docs/开箱流程.md` ——
这个项目对外的价值有一半在文档里，用户看不懂命令行的时代已经过去了。

## 许可

提交的代码按 [MIT](LICENSE) 授权。
