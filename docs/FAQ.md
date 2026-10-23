# 常见问题

## 双击没反应 / 一闪就没

`.exe.config` 丢了。它和 `游戏解压助手.exe` 必须放在同一目录，长路径开关在里面。
从 Releases 下的压缩包解压时如果只挑了一个文件出来，就会这样。

## 「缺少工具：java.exe 与 ssefenc.jar」

这个包是 `.enc`（SSEFE 加密容器），必须先解密才能解压。默认程序不做这段，要你把三个第三方东西放进来：

1. **7-Zip** — <https://www.7-zip.org/download.html>，取 `7z.exe` + `7z.dll`
2. **S.S.E. File Encryptor** — <https://paranoiaworks.mobi/download/>，取 `ssefenc.jar`
3. **Java 11+** — 没装的话，同一页面下 `Paranoia File & Text Encryption` 安装包自带运行时，
   装完从安装目录取出 `runtime\bin\java.exe`

放在 `游戏解压助手.exe` 旁边，或者设 `GALGAMEKIT_TOOLS` 指向工具目录。目录结构见
[README 的「准备工具目录」](README.md#1-准备工具目录)。

## 只是普通 7z 包，为什么还问我要 Java

只有真碰到 `.enc` 才需要 Java。如果程序坚持要 Java，那说明它把这个包判成了 SSEFE ——
用 `--dry-run` 看看它认成了什么：

```bat
galgame-kit-cli.exe --dry-run "D:\Downloads\Gamex-123456"
```

## 「口令不对（exit 3）」

口令错了，或者口令里带了空格/全角字符。这一类资源站的惯例是**站点域名**，
直接写在资源帖里。复制过来的时候注意别把前后空白一起粘进去。

## 「文件损坏（exit 6）」

这一卷真的坏了，重下那一卷。先跑一遍 `--dry-run` 确认是哪一卷。

## 「卷不全，缺 XX MB」

重下缺的那几卷。程序在解压**之前**就算出了应有长度，所以不会让你白等 ——
这是它比直接双击 7-Zip 强的地方。

## 解压后「文件数比预期少」

多半是路径太深。确认 `游戏解压助手.exe.config` 还在 exe 旁边。
Windows 的经典路径上限是 260 字符，超过之后 `Directory.GetFiles` 不报错、直接返回空，
所以症状是「少文件」而不是「报错」。

## 认不出引擎 / 找不到启动程序

引擎识别是启发式的。直接把目录打开，自己找那个最大的 `.exe` ——
带 `UnityPlayer.dll` 的目录挑 `游戏名.exe`，带 `*.pck` 的挑主 exe，
`UnityCrashHandler64.exe` 和 `vcredist` 一律不是启动器。

## 存档在哪

| 引擎 | 位置 |
| --- | --- |
| Unity | `%USERPROFILE%\AppData\LocalLow\<公司名>\<游戏名>\` |
| Godot / Ren'Py | `%USERPROFILE%\AppData\Roaming\<游戏名>\` |
| RPG Maker VX Ace / XP | 游戏目录下的 `Save\` |
| RPG Maker MV / MZ | 游戏目录下的 `save\` |
| KiriKiri | `%USERPROFILE%\Documents\<游戏名>\` |

程序跑完会报一个具体路径，但**只在那个目录确实存在时才报**。看到 `<公司名>` `<游戏名>`
这种尖括号，说明它没能确定，得你自己按上表去找。

## 会不会上传我的文件 / 偷口令

不会，也没有能力。整个程序没有任何网络代码 —— 你可以断网跑一遍验证。
口令写进临时文件后立刻删除，不经过命令行参数（命令行对整个系统可见），也不写进配置文件。

## 能处理 rar 吗

能解，但**不能预校验分卷完整性**。rar 的文件头里没有等价字段，只能解了才知道缺不缺。
7z 分卷能精确到「缺多少字节」，这是 7z 格式本身的优势。

## 为什么不用 Python / Node 写

目标用户是双击一个 exe 就能用的人。系统自带 .NET Framework，编译用系统自带 `csc.exe`，
零安装、零依赖、80 KB 单文件 —— 换成 Python 就得让每个人先装环境。

## 支持 Linux / macOS 吗

不支持，也不打算支持。启动器挑选、路径习惯、存档位置全是 Windows 的。
核心逻辑本身是纯 .NET，理论上能移植，但那等于重写 `EngineDetector` 和 `PathHelper`。

## 被杀了毒 / 报毒

单文件 exe + 自己打包的图标 + 没有签名，某些杀软会靠启发式报。源码全在仓库里，
可以自己编译一份对比哈希；也可以直接跑 `scripts\build.ps1` 自己生成。
