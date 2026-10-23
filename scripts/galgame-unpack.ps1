#requires -Version 5.1
<#
.SYNOPSIS
    Galgame 资源通用开箱器：识别包装 → 解密 .enc → 合并/解压分卷 → 报告引擎。

.DESCRIPTION
    面向从资源站下载的二次包装资源。典型形态：
      · SSEFE 头 + .enc    → Paranoia S.S.E. File Encryptor 加密容器（需口令，通常=站点域名）
      · 7z 头  + .001/.002 → 7-Zip 分卷（只需解第一卷，自动续读）
      · RAR 头 + part1.rar → WinRAR 分卷
      · ZIP 头 + .001      → ZIP 分卷
      · UnityFS / XP3 / CPK / NEKOPACK → 已是引擎资源，无需解压，直接跑主程序

.PARAMETER Path
    要处理的文件，或包含分卷的目录（可含多套资源，脚本会按组分别处理）。

.PARAMETER Password
    .enc 的口令。

.PARAMETER OutDir
    输出目录。默认在源文件同级建 _unpack。

.PARAMETER ListOnly
    只识别类型，不解密不解压。遇到不认识的格式先用这个。

.EXAMPLE
    .\galgame-unpack.cmd "D:\dl\Gamex-007718" yourpassword

.EXAMPLE
    .\galgame-unpack.ps1 -Path "D:\dl\PC.001.enc" -ListOnly
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Path,
    [Parameter(Position = 1)][string]$Password,
    [Parameter(Position = 2)][string]$OutDir,
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'

# 是不是在真正的控制台窗口里跑（双击 .cmd 的情况）——这时才允许交互提问
$Interactive = -not [Console]::IsInputRedirected

# 统一取值：真控制台用 Read-Host（带提示），管道/重定向就直读一行
function Read-Answer([string]$Prompt) {
    if ($Interactive) { return (Read-Host $Prompt) }
    try { return [Console]::In.ReadLine() } catch { return $null }
}

# ============ 工具位置 ============
# 按顺序找：环境变量 GALGAMEKIT_TOOLS → 脚本所在目录 → 上级 → 上两级
#          → 同盘根的 galgame-tools\
# 这样整个目录搬到别处也不用改文件。
function Test-ToolDir([string]$dir) {
    if (-not $dir) { return $false }
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    foreach ($n in @('7z.exe', '7zr.exe')) {
        if (Test-Path -LiteralPath (Join-Path $dir $n)) { return $true }
    }
    return $false
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidates = New-Object System.Collections.Generic.List[string]
if ($env:GALGAMEKIT_TOOLS) { $candidates.Add($env:GALGAMEKIT_TOOLS) }
$candidates.Add($here)
$candidates.Add((Split-Path -Parent $here))
$candidates.Add((Split-Path -Parent (Split-Path -Parent $here)))
$candidates.Add([System.IO.Path]::Combine([System.IO.Path]::GetPathRoot($here), 'galgame-tools'))

$Tools = $null
foreach ($cand in $candidates) {
    if (Test-ToolDir $cand) { $Tools = $cand; break }
}
if (-not $Tools) {
    $Tools = $here
    Write-Host '[提示] 没找到 7z.exe / 7zr.exe。' -ForegroundColor Yellow
    Write-Host '       把工具放到脚本旁边，或设环境变量 GALGAMEKIT_TOOLS 指向工具目录。' -ForegroundColor Yellow
    Write-Host ('       试过：' + ($candidates -join '  |  ')) -ForegroundColor DarkGray
}

function Find-Tool {
    param([string]$Root, [string[]]$Relatives)
    foreach ($r in $Relatives) {
        $p = Join-Path $Root $r
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

$JavaExe = Find-Tool $Tools @(
    'jre\bin\java.exe',
    'admin\Paranoia File & Text Encryption\runtime\bin\java.exe'
)
$SsefJar = Find-Tool $Tools @(
    'ssefenc.jar',
    'S.S.E. File Encryptor for PC\ssefenc.jar'
)
$SevenZ  = Find-Tool $Tools @('7z.exe')    # 完整版：7z/zip/rar/xz/tar 全支持
$SevenZR = Find-Tool $Tools @('7zr.exe')   # 后备：只支持 7z
# ==================================================

# 调用外部程序：java 往 stderr 写 WARNING 时，PS 5.1 在 EAP=Stop 下会抛终止错误，这里统一吞掉
function Invoke-Native {
    param([string]$Exe, [string[]]$ArgList)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = @(& $Exe @ArgList 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
    [pscustomobject]@{ ExitCode = $code; Output = $out }
}

# ---------- 文件头指纹表（按最长匹配优先） ----------
$Signatures = @(
    @{ Hex = '53 53 45 46 45';             Kind = 'SSEFE';    Desc = 'Paranoia S.S.E. File Encryptor 加密容器（要口令解密）' }
    @{ Hex = '37 7A BC AF 27 1C';          Kind = '7Z';       Desc = '7-Zip 压缩包 / 分卷' }
    @{ Hex = '52 61 72 21 1A 07';          Kind = 'RAR';      Desc = 'RAR 压缩包 / 分卷' }
    @{ Hex = '50 4B 03 04';                Kind = 'ZIP';      Desc = 'ZIP 压缩包' }
    @{ Hex = '58 50 33 0D 0A 20 0A 1A 8B'; Kind = 'XP3';      Desc = 'KiriKiri 引擎资源包（不用解压，直接玩）' }
    @{ Hex = '55 6E 69 74 79 46 53';       Kind = 'UnityFS';  Desc = 'Unity 资源包（不用解压）' }
    @{ Hex = '43 50 4B 20';                Kind = 'CPK';      Desc = 'CRI CPK 资源包（不用解压）' }
    @{ Hex = '4E 45 4B 4F';                Kind = 'NEKOPACK'; Desc = 'NEKOPACK 资源包（不用解压）' }
    @{ Hex = '1F 8B';                      Kind = 'GZIP';     Desc = 'gzip 压缩流' }
    @{ Hex = '42 5A 68';                   Kind = 'BZIP2';    Desc = 'bzip2 压缩流' }
    @{ Hex = 'FD 37 7A 58 5A 00';          Kind = 'XZ';       Desc = 'xz 压缩流' }
    @{ Hex = '04 22 4D 18';                Kind = 'LZ4';      Desc = 'lz4 压缩流' }
    @{ Hex = '4D 5A';                      Kind = 'PE';       Desc = 'Windows 可执行文件（本体，直接跑）' }
    @{ Hex = '7F 45 4C 46';                Kind = 'ELF';      Desc = 'Linux 可执行文件' }
    @{ Hex = '89 50 4E 47';                Kind = 'PNG';      Desc = 'PNG 图片（可能是伪装扩展名的图片）' }
    @{ Hex = 'FF D8 FF';                   Kind = 'JPEG';     Desc = 'JPEG 图片（可能是伪装扩展名的图片）' }
)

function Read-Head([string]$File, [int]$Count = 16) {
    $fs = [System.IO.File]::OpenRead($File)
    try {
        $n = [Math]::Min($Count, [int]$fs.Length)
        $buf = New-Object byte[] $n
        $read = $fs.Read($buf, 0, $n)
        if ($read -lt $n) {
            $tmp = New-Object byte[] $read
            [Array]::Copy($buf, $tmp, $read)
            return , $tmp
        }
        return , $buf
    } finally { $fs.Close() }
}

function ConvertTo-Hex($Bytes) {
    (($Bytes | ForEach-Object { $_.ToString('X2') }) -join ' ').Trim()
}

# 人类可读的字节数：3.00 MB / 1.53 GB / 512 B
function Format-Size($Bytes) {
    if ($null -eq $Bytes) { return '0 B' }
    $b = [double]$Bytes
    if ($b -lt 1024) { return ('{0:N0} B' -f $b) }
    if ($b -lt 1MB) { return ('{0:N2} KB' -f ($b / 1KB)) }
    if ($b -lt 1GB) { return ('{0:N2} MB' -f ($b / 1MB)) }
    return ('{0:N2} GB' -f ($b / 1GB))
}

function Get-Kind([string]$File) {
    $head = Read-Head $File 16
    $hex = ConvertTo-Hex $head
    $hit = $null
    foreach ($s in $Signatures) {
        if ($hex.StartsWith($s.Hex, [StringComparison]::OrdinalIgnoreCase)) { $hit = $s; break }
    }
    $name = [System.IO.Path]::GetFileName($File)
    $ext  = [System.IO.Path]::GetExtension($File).ToLowerInvariant()

    # 文件头没命中时按扩展名兜底（有些资源站的 exe/xp3 头部被改写过）
    if (-not $hit) {
        switch ($ext) {
            '.exe' { $hit = @{ Kind = 'PE';  Desc = 'Windows 可执行文件（按扩展名判断）' } }
            '.dll' { $hit = @{ Kind = 'PE';  Desc = 'Windows 动态库（按扩展名判断）' } }
            '.xp3' { $hit = @{ Kind = 'XP3'; Desc = 'KiriKiri 资源包（按扩展名判断，不用解压）' } }
            '.7z'  { $hit = @{ Kind = '7Z';  Desc = '7-Zip 压缩包（按扩展名判断，头部可能被改写）' } }
            '.rar' { $hit = @{ Kind = 'RAR'; Desc = 'RAR 压缩包（按扩展名判断）' } }
            '.zip' { $hit = @{ Kind = 'ZIP'; Desc = 'ZIP 压缩包（按扩展名判断）' } }
            '.enc' { $hit = @{ Kind = 'ENC?'; Desc = '扩展名 .enc 但头部不是 SSEFE（别家封装）' } }
        }
    }

    $cont = $name -match '\.\d{3}$'
    [pscustomobject]@{
        Name = $name
        Full = $File
        Size = (Get-Item -LiteralPath $File).Length
        Hex  = if ($hex.Length -gt 23) { $hex.Substring(0, 23) } else { $hex }
        Kind = if ($hit) { $hit.Kind } elseif ($cont) { '续卷' } else { 'UNKNOWN' }
        Desc = if ($hit) { $hit.Desc }
               elseif ($cont) { '分卷续卷（000 号之后的裸数据，正常）' }
               else { '未识别（自定义封装 / 损坏 / 或缺卷）' }
    }
}

# 7z 分卷完整度预估：主头 NextHeaderOffset+Size 应等于合并总长
function Test-7zSplit([string]$FirstVolume, $Volumes) {
    $fs = [System.IO.File]::OpenRead($FirstVolume)
    try {
        $h = New-Object byte[] 32
        [void]$fs.Read($h, 0, 32)
    } finally { $fs.Close() }
    if ($h[0] -ne 0x37 -or $h[1] -ne 0x7A) { return $null }
    $off = [BitConverter]::ToUInt64($h, 12)
    $sz = [BitConverter]::ToUInt64($h, 20)
    $total = ($Volumes | Measure-Object Length -Sum).Sum
    $need = 32 + $off + $sz
    [pscustomobject]@{
        VolumeCount = @($Volumes).Count
        TotalSize   = $total
        NeedSize    = $need
        Complete    = ($total -ge $need)
        Missing     = if ($total -ge $need) { 0 } else { $need - $total }
    }
}

# 分组键：PC.001.enc / PC.002.enc -> PC     app.7z.001 -> app.7z     game.rar -> game
function Get-GroupKey([string]$FileName) {
    $n = $FileName
    if ($n -match '\.enc$') { $n = $n.Substring(0, $n.Length - 4) }
    if ($n -match '^(.+)\.\d{3}$') { $n = $Matches[1] }
    if ($n -match '^(.+?)[._-]part\d+$') { $n = $Matches[1] }
    $n
}

# ---------- 0. 收集输入 ----------
# 没给路径 = 双击启动，直接问
$AskedPath = $false
if (-not $Path) {
    $AskedPath = $true
    Write-Host "`n########## 齿轮开箱器 ##########" -ForegroundColor Cyan
    Write-Host "把下载下来的文件夹拖进这个窗口，或者直接粘贴路径。" -ForegroundColor Gray
    Write-Host "（带引号也行，会自动去掉）`n"
    do {
        $Path = Read-Answer "要处理的目录"
        if ($Path) { $Path = $Path.Trim().Trim('"').Trim("'") }
    } while (-not $Path)
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "`n[错误] 路径不存在：$Path" -ForegroundColor Red
    return
}
$all = if (Test-Path -LiteralPath $Path -PathType Container) {
    Get-ChildItem -LiteralPath $Path -File | Sort-Object Name
} else {
    $one = Get-Item -LiteralPath $Path
    Get-ChildItem -LiteralPath $one.DirectoryName -File |
        Where-Object { (Get-GroupKey $_.Name) -eq (Get-GroupKey $one.Name) } |
        Sort-Object Name
}
$all = @($all | Where-Object { $_.Extension -notin @('.txt', '.nfo', '.url', '.jpg', '.png') })

if ($all.Count -eq 0) { throw "目录里没有可用文件：$Path" }

# ---------- 提前判定：这本来就是解压好的游戏目录吗？ ----------
if (Test-Path -LiteralPath $Path -PathType Container) {
    $probe = @(Get-ChildItem -LiteralPath $Path -File -ErrorAction SilentlyContinue)
    $probeNames = $probe | Select-Object -ExpandProperty Name
    $looksPlayable = ($probeNames -contains 'UnityPlayer.dll') -or
                     ($probeNames -contains 'RGSS301.dll') -or
                     ($probeNames -contains 'RGSS202E.dll') -or
                     (@($probeNames | Where-Object { $_ -like '*.xp3' }).Count -gt 0) -or
                     # Godot：exe 旁边跟 .pck 或 .console.exe
                     (@($probeNames | Where-Object { $_ -like '*.pck' -or $_ -like '*.console.exe' }).Count -gt 0) -or
                     (@($probeNames | Where-Object { $_ -like '*.exe' }).Count -gt 0 -and
                      @(Get-ChildItem -LiteralPath $Path -Directory -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -like '*_Data' }).Count -gt 0)
    if ($looksPlayable) {
        if (-not $AskedPath) {
            Write-Host "`n########## 齿轮开箱器 ##########" -ForegroundColor Cyan
            Write-Host "输入：$Path"
        }
        Write-Host "`n这是已经解压好的游戏目录，不需要再解包。" -ForegroundColor Green
        $exe = $probe | Where-Object { $_.Extension -eq '.exe' -and $_.Name -notmatch 'UnityCrashHandler|CrashReport' } |
            Sort-Object Length -Descending | Select-Object -First 1
        if ($exe) { Write-Host "  双击启动：$($exe.FullName)" -ForegroundColor Green }
        $allFiles = @(Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue)
        $sum = ($allFiles | Measure-Object Length -Sum).Sum
        Write-Host ("  共 {0} 个文件，{1}" -f $allFiles.Count, (Format-Size $sum))
        Write-Host "`n########## 结束 ##########`n" -ForegroundColor Cyan
        return
    }
}

$groups = @($all | Group-Object { Get-GroupKey $_.Name })

if ($AskedPath) {
    Write-Host "输入：$Path"
} else {
    Write-Host "`n########## 齿轮开箱器 ##########" -ForegroundColor Cyan
    Write-Host "输入：$Path"
}
Write-Host "发现 $($groups.Count) 组资源`n"

foreach ($grp in $groups) {

    Write-Host "=== 组：$($grp.Name) （$($grp.Count) 个文件） ===" -ForegroundColor Cyan
    $report = foreach ($f in ($grp.Group | Sort-Object Name)) { Get-Kind $f.FullName }
    $report | Format-Table Name,
        @{ n = 'MB'; e = { [math]::Round($_.Size / 1MB, 2) } },
        Kind, Hex, Desc -AutoSize

    $kinds = @($report | Select-Object -ExpandProperty Kind -Unique)
    Write-Host "  识别结果：$($kinds -join ', ')"

    if ($ListOnly) { continue }

    $workDir = if ($OutDir) { $OutDir } else { Join-Path $grp.Group[0].DirectoryName '_unpack' }
    $cur = @($grp.Group | Sort-Object Name)

    # --- 1. SSEFE 解密 ---
    if ($kinds -contains 'SSEFE') {
        if (-not $Password) {
            Write-Host ""
            Write-Host "  这批是加密容器（SSEFE），需要口令。" -ForegroundColor Yellow
            Write-Host "  口令写在资源帖里，这类站点常直接写成站点域名" -ForegroundColor Gray
            $Password = Read-Answer "  口令（直接回车＝跳过）"
            if ($Password) { $Password = $Password.Trim() }
            if (-not $Password) {
                Write-Host "`n  跳过解密：没有口令。-Password <口令> 重跑。" -ForegroundColor Yellow
                Write-Host ""
                continue
            }
        }
        if (-not (Test-Path -LiteralPath $JavaExe)) { throw "找不到 java：$JavaExe（见脚本顶部工具位置）" }
        if (-not (Test-Path -LiteralPath $SsefJar)) { throw "找不到 ssefenc.jar：$SsefJar" }

        $decDir = Join-Path $workDir 'decrypted'
        New-Item -ItemType Directory -Force -Path $decDir | Out-Null
        Write-Host "`n--- 解密到 $decDir ---" -ForegroundColor Yellow

        $failed = $false
        foreach ($e in ($report | Where-Object Kind -eq 'SSEFE')) {
            Write-Host "  解密 $($e.Name) ..."
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $r = Invoke-Native $JavaExe @('-jar', $SsefJar, 'dec', '--password', $Password, '-o', $decDir, $e.Full)
            $code = $r.ExitCode
            $tail = ($r.Output | Where-Object { $_ -and $_ -notmatch 'WARNING|restricted method' } | Select-Object -Last 1)
            Write-Host ("    exit={0}  {1:N1}s  {2}" -f $code, $sw.Elapsed.TotalSeconds, $tail)
            switch ($code) {
                0 { }
                3 {
                    Write-Host "  [失败] 口令不对（exit 3）：$($e.Name)。换个口令用 -Password 重跑。" -ForegroundColor Red
                    $failed = $true
                }
                6 {
                    Write-Host "  [失败] 文件损坏（exit 6）：$($e.Name)。这一卷要重新下载。" -ForegroundColor Red
                    $failed = $true
                }
                default {
                    Write-Host "  [失败] exit=$code`n$($r.Output -join "`n")" -ForegroundColor Red
                    $failed = $true
                }
            }
            if ($failed) { break }
        }
        if ($failed) { Write-Host ""; continue }

        $cur = @(Get-ChildItem -LiteralPath $decDir -File | Sort-Object Name)
        $report = foreach ($f in $cur) { Get-Kind $f.FullName }
        Write-Host "`n  解密产物："
        $report | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Size / 1MB, 2) } }, Kind -AutoSize
        $kinds = @($report | Select-Object -ExpandProperty Kind -Unique)
    }

    # --- 2. 解压 ---
    $archKind = @($kinds | Where-Object { $_ -in @('7Z', 'RAR', 'ZIP') }) | Select-Object -First 1
    if ($archKind) {
        $exDir = Join-Path $workDir 'game'
        New-Item -ItemType Directory -Force -Path $exDir | Out-Null
        Write-Host "`n--- 解压（$archKind）到 $exDir ---" -ForegroundColor Yellow

        $first = $cur | Where-Object { $_.Name -match '\.001$' } | Select-Object -First 1
        if (-not $first) { $first = $cur | Where-Object { $_.Name -match 'part0?1\.rar$' } | Select-Object -First 1 }
        if (-not $first) { $first = $cur | Where-Object { $_.Name -notmatch '\.\d{3}$' } | Select-Object -First 1 }
        if (-not $first) { $first = $cur | Select-Object -First 1 }

        # 选解压器：完整版 7z.exe 支持 7z/zip/rar/xz/tar；退化时才用 7zr（只认 7z）
        $extractor = $null
        if (Test-Path -LiteralPath $SevenZ) { $extractor = $SevenZ }
        elseif ($archKind -eq '7Z' -and (Test-Path -LiteralPath $SevenZR)) { $extractor = $SevenZR }
        if (-not $extractor) {
            Write-Host "  [跳过] 找不到解压器：$SevenZ" -ForegroundColor Red
            Write-Host "  下载 7-Zip 后把 7z.exe + 7z.dll 放到 $Tools\ 即可。" -ForegroundColor DarkGray
            Write-Host ""
            continue
        }

        if ($archKind -eq '7Z') {
            $chk = Test-7zSplit $first.FullName $cur
            if ($chk) {
                $state = if ($chk.Complete) { '完整' } else { "缺 $([math]::Round($chk.Missing / 1MB, 2)) MB" }
                Write-Host ("  分卷 {0} 个，合并 {1:N0} B，需要 {2:N0} B -> {3}" -f $chk.VolumeCount, $chk.TotalSize, $chk.NeedSize, $state)
                if (-not $chk.Complete) {
                    Write-Host "  [停止] 卷不全，解压必然失败。把缺的那一卷下完再来。" -ForegroundColor Red
                    Write-Host "         判断依据：7z 主头声明需要 $($chk.NeedSize) B，目录里只有 $($chk.TotalSize) B。" -ForegroundColor DarkGray
                    Write-Host ""
                    continue
                }
            }
        }

        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r7 = Invoke-Native $extractor @('x', $first.FullName, "-o$exDir", '-y', '-bso0', '-bsp0')
        if ($r7.ExitCode -ne 0 -and $archKind -eq '7Z' -and $extractor -ne $SevenZR -and (Test-Path -LiteralPath $SevenZR)) {
            # 完整版 7z 解 7z 失败时，退回 7zr 再试一次（某些老分卷更认 7zr）
            Write-Host "  [重试] 7z.exe exit=$($r7.ExitCode)，改用 7zr.exe" -ForegroundColor DarkYellow
            $extractor = $SevenZR
            $r7 = Invoke-Native $extractor @('x', $first.FullName, "-o$exDir", '-y', '-bso0', '-bsp0')
        }
        if ($r7.ExitCode -ne 0) {
            Write-Host "  [失败] 解压器 exit=$($r7.ExitCode)" -ForegroundColor Red
            $r7.Output | Where-Object { $_ -and $_ -notmatch 'WARNING|restricted method' } | Select-Object -Last 8 | ForEach-Object { Write-Host "    $_" }
            Write-Host "  常见原因：卷不全 / 包有口令 / 磁盘不足 / 输出目录无写权限。" -ForegroundColor DarkGray
            Write-Host "  若报「拒绝访问」：工具被放在工作区内了，把 $Tools 搬到工作区外再跑。" -ForegroundColor DarkGray
            Write-Host ""
            continue
        }
        Write-Host ("  完成，{0:N1}s" -f $sw.Elapsed.TotalSeconds)

        # --- 3. 引擎与启动器 ---
        Write-Host "`n--- 产物分析 ---" -ForegroundColor Yellow
        $files = @(Get-ChildItem -LiteralPath $exDir -Recurse -File -ErrorAction SilentlyContinue)
        $stat = $files | Measure-Object Length -Sum
        Write-Host ("  {0} 个文件，共 {1}" -f $stat.Count, (Format-Size $stat.Sum))

        $names = $files | Select-Object -ExpandProperty Name
        $engine =
            if ($names -contains 'UnityPlayer.dll') { 'Unity' }
            elseif ($names -contains 'RGSS301.dll') { 'RPG Maker VX Ace' }
            elseif ($names -contains 'RGSS202E.dll') { 'RPG Maker XP' }
            elseif ($names -contains 'nw.dll' -or $names -contains 'package.json') { 'RPG Maker MV/MZ' }
            elseif (@($names | Where-Object { $_ -like '*.xp3' }).Count -gt 0) { 'KiriKiri' }
            elseif (@($names | Where-Object { $_ -like '*.pck' }).Count -gt 0) { 'Godot' }
            elseif (@($names | Where-Object { $_ -like '*.console.exe' }).Count -gt 0) { 'Godot' }
            elseif ($names -contains 'Game.exe') { '可能 KiriKiri / RPG Maker' }
            else { '未知' }
        Write-Host "  引擎：$engine"

        # 存档位置：优先读包里自带的"存档位置.txt"，否则按引擎惯例猜
        $title = [System.IO.Path]::GetFileNameWithoutExtension((@($files | Where-Object { $_.Extension -eq '.exe' }) |
            Sort-Object Length -Descending | Select-Object -First 1).Name)
        $saveRoot = $null
        $saveNote = $files | Where-Object { $_.Name -match '存档位置|save.?location|存档说明' } | Select-Object -First 1
        if ($saveNote) {
            # 说明文件编码不定（UTF-8 / GBK 都见过），PS 5.1 的 -Encoding 只认枚举名，
            # 所以直接用 .NET 解码，两种都试。文件里的用户名是占位符，要用本机真实目录替换。
            $raw = $null
            $encs = @([System.Text.UTF8Encoding]::new($false, $true), [System.Text.Encoding]::GetEncoding(936))
            foreach ($enc in $encs) {
                try { $raw = [System.IO.File]::ReadAllText($saveNote.FullName, $enc); break } catch { }
            }
            if ($raw) {
                $m = [regex]::Match($raw, 'AppData[\\/]+(Roaming|LocalLow)[\\/]+[^\r\n"'']+')
                if ($m.Success) {
                    $tail = $m.Value -replace '^AppData[\\/]+', ''
                    if ($tail -match '^LocalLow') {
                        $saveRoot = Join-Path (Join-Path $env:USERPROFILE 'AppData\LocalLow') ($tail -replace '^LocalLow[\\/]+', '')
                    } else {
                        $saveRoot = Join-Path $env:APPDATA ($tail -replace '^Roaming[\\/]+', '')
                    }
                }
            }
        }
        if (-not $saveRoot -and $engine -eq 'Godot' -and $title) { $saveRoot = Join-Path $env:APPDATA $title }
        if ($saveRoot) { Write-Host "  存档：$saveRoot" -ForegroundColor DarkGray }

        # 包内自带存档：投放到上面找到的位置，省得玩家自己翻
        $bundledSave = $files | Where-Object { $_.Name -like '*.save' } | Select-Object -First 1
        if ($bundledSave -and $saveRoot) {
            try {
                New-Item -ItemType Directory -Force -Path $saveRoot -ErrorAction Stop | Out-Null
                Copy-Item -LiteralPath $bundledSave.FullName -Destination $saveRoot -Force
                Write-Host "  已投放初始存档：$($bundledSave.Name) -> $saveRoot" -ForegroundColor Green
            } catch { }
        }

        $launcher = $files | Where-Object { $_.Extension -eq '.exe' } |
            Where-Object { $_.Name -notmatch 'UnityCrashHandler|vcredist|dxsetup|unins\d+|CrashReport' } |
            Sort-Object Length -Descending | Select-Object -First 1
        if ($launcher) { Write-Host "  启动：$($launcher.FullName)" -ForegroundColor Green }

        $notes = $files | Where-Object { $_.Name -match 'readme|说明|安装|必看|注意' } | Select-Object -First 3
        foreach ($n in $notes) { Write-Host "  附说明：$($n.FullName)" }

        $sub = @(Get-ChildItem -LiteralPath $exDir -Directory -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -match '_Data$|^data$|^assets$|^game$' })
        if ($sub) { Write-Host "  数据目录：$(($sub.Name) -join ', ')" }
        Write-Host ""
    }
    else {
        Write-Host "  无需解压：已是引擎资源或可执行文件（$($kinds -join ', ')）" -ForegroundColor Green
        $exe = $cur | Where-Object { $_.Extension -eq '.exe' } | Select-Object -First 1
        if ($exe) { Write-Host "  直接运行：$($exe.FullName)" }
        Write-Host ""
    }
}

Write-Host "########## 结束 ##########`n" -ForegroundColor Cyan
