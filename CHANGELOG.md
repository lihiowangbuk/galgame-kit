# 更新记录

版本号跟 `src/AssemblyInfo.cs` 走。发布时 `scripts/build.ps1 -Clean -Test` 必须全绿。

## v1.1.0 — 2026-10-24

**这一版的主题：能认的东西变多了。**

原来只认 7z / zip / rar 三种压缩包，`demo.tar`、`demo.tar.gz` 之类的包会被判成「未知」然后跳过。
现在补上了 tar 系和单流压缩，并且把「两层结构」当成正经场景处理。

### 新增格式

| 格式 | 说明 |
|---|---|
| `tar` | 未压缩的 tar。魔数 `ustar` 在文件第 **257** 字节，不在开头 |
| `gzip` | `.gz` / `.tgz`，一层单流 |
| `bzip2` | `.bz2`，一层单流 |
| `xz` | `.xz`，一层单流 |
| `zstd` | `.zst` / `.zstd`，一层单流 |
| `lz4` | `.lz4`，一层单流 |

`.tar.gz` / `.tar.xz` / `.tar.bz2` / `.tar.zst` 会被**连着剥两层**：先解外层拿到 `.tar`，
再解 tar 拿到游戏目录，日志里两步都写出来。

### 改动细节

- **魔数表支持偏移**：`Signature` 加了 `Offset` 字段，`HeadBytes` 从 16 提到 264。
  这是 tar 能被认出来的前提 —— 原来只读前 16 字节，永远碰不到第 257 字节的 `ustar`。
- **区分「压缩包」和「单流压缩」**：新增 `FileSignatures.IsSingleStream()`。
  gzip / bzip2 / xz / zstd / lz4 刻意**不算** `IsArchive`，因为它们只解出一个文件，
  多半是 tar 的第一层，逻辑上是两个方向。
- **工具缺失按格式分别要求**：`7zr.exe` 只认 `.7z`。碰到 tar / zip / rar / gz 而只装了 `7zr.exe` 时，
  现在会在开工前就直说「需要完整 7z.exe」，而不是解到一半才报错。
- **测试 90 → 108 项**：新增 18 项，专测偏移识别（含「`ustar` 出现在开头不算 tar」的反向用例）、
  单流判定、各格式魔数。

### 修的问题

- 修复 README「参与」小节里 `build.ps1` 少了一个反引号的排版错误。
- 版本号从 `1.0.0` 提到 `1.1.0`，界面左下角显示的也是这个。

### 仍然不做

- **只解不压**。不会把 7z 转成 zip、不会重新打包。要转格式用 7-Zip 本体。
- **不代解引擎资源**：`.xp3`、`.cpk`、`UnityFS`、NEKOPACK 只识别并提示「不用解，直接玩」。

## v1.0.0 — 2026-10-23

首个公开版本。

- 解密 `.enc`（SSEFE / Paranoia S.S.E. File Encryptor 容器），口令走临时文件，不经过命令行
- 7z 分卷完整性**解压前**预检，缺卷直接报缺多少字节
- 解压 7z / zip / rar
- 引擎识别：Unity、Godot、RPG Maker VX Ace / XP / MV-MZ、KiriKiri、Ren'Py、Wolf、NScripter
- 启动器挑选：排除 `UnityCrashHandler` / `vcredist` / `unins000` 这类，取真正的游戏 exe
- 存档位置推断，目录确实存在才报真实路径
- 长路径支持（`app.config` 开关 + `\\?\` 前缀，枚举结果再去前缀交给 7z）
- 图形界面 + 命令行两个入口，共用 `GalgameKit.Core`
- 90 项单元测试
