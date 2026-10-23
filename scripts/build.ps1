# 用系统自带的 csc.exe 编译，不需要装 Visual Studio 或 .NET SDK。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -Test
#
# 产物在 dist\。

param(
    [switch]$Test,
    [switch]$Clean,
    [switch]$NoTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $root 'dist'
$srcCore = Join-Path $root 'src\Core'
$srcCli = Join-Path $root 'src\Cli'
$srcGui = Join-Path $root 'src\Gui'
$assets = Join-Path $root 'assets'

function Find-Csc {
    $candidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw '找不到 csc.exe。需要 .NET Framework 4.x（Windows 10/11 自带）。'
}

function Invoke-Csc {
    param([string]$Csc, [string[]]$ArgumentList)

    # 参数里含空格和中文，用数组传给 Start-Process 最稳，别拼成一个字符串。
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Csc
    $psi.Arguments = ($ArgumentList | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    if ($out.Trim()) { Write-Host $out.Trim() }
    if ($err.Trim()) { Write-Host $err.Trim() }
    if ($p.ExitCode -ne 0) { throw "csc 失败，退出码 $($p.ExitCode)" }
}

$csc = Find-Csc
Write-Host "csc: $csc"

if ($Clean -and (Test-Path $dist)) {
    Remove-Item $dist -Recurse -Force
    Write-Host '已清空 dist\'
}
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$common = @('/nologo', '/platform:anycpu', '/optimize+', '/warn:4', '/r:System.dll', '/r:System.Core.dll')

$coreSrc = @(Get-ChildItem (Join-Path $srcCore '*.cs') | ForEach-Object { $_.FullName }) + @(Join-Path $root 'src\AssemblyInfo.cs')

# ---- 1. 核心库 ----
Write-Host '--- 编译核心库 ---'
Invoke-Csc $csc ($common + @(
    '/target:library',
    ('/out:' + (Join-Path $dist 'GalgameKit.Core.dll'))
) + $coreSrc)

# ---- 2. 命令行 ----
Write-Host '--- 编译命令行 ---'
Invoke-Csc $csc ($common + @(
    '/target:exe',
    ('/out:' + (Join-Path $dist 'galgame-kit-cli.exe')),
    ('/r:' + (Join-Path $dist 'GalgameKit.Core.dll')),
    (Join-Path $srcCli 'Program.cs')
))

# ---- 3. 图形界面（单文件：把 Core 源码一起编进去，不再需要旁边放 dll）----
Write-Host '--- 编译图形界面 ---'
$guiSrc = @(Get-ChildItem (Join-Path $srcGui '*.cs') | ForEach-Object { $_.FullName }) + $coreSrc
$guiArgs = @(
    '/target:winexe',
    ('/out:' + (Join-Path $dist '游戏解压助手.exe')),
    ('/win32icon:' + (Join-Path $assets 'gear.ico')),
    '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll'
)
$manifest = Join-Path $assets 'app.manifest'
if (Test-Path $manifest) { $guiArgs += ('/win32manifest:' + $manifest) }
Invoke-Csc $csc ($common + $guiArgs + $guiSrc)

# ---- 4. 测试跑器 ----
$testSrc = Join-Path $root 'src\Tests\Program.cs'
$testExe = Join-Path $dist 'galgame-kit-tests.exe'
if (-not $NoTests) {
    Write-Host '--- 编译测试 ---'
    Invoke-Csc $csc ($common + @(
        '/target:exe',
        ('/out:' + $testExe),
        ('/r:' + (Join-Path $dist 'GalgameKit.Core.dll')),
        $testSrc
    ))
}

# ---- 5. 长路径开关：每个 exe 都要有同名 .exe.config 才生效 ----
# .NET Framework 默认拒绝超过 260 字符的路径，要靠 app.config 里的
# Switch.System.IO.UseLegacyPathHandling / BlockLongPaths 打开。
# 少了这一步，游戏目录层级一深就「文件数比预期少」，很难查。
Write-Host '--- 写入 app.config ---'
$appConfig = Join-Path $root 'src\app.config'
if (-not (Test-Path $appConfig)) { throw "缺少 $appConfig（长路径开关）" }
$exeNames = @('游戏解压助手', 'galgame-kit-cli')
if (Test-Path $testExe) { $exeNames += 'galgame-kit-tests' }
foreach ($n in $exeNames) {
    $target = Join-Path $dist ($n + '.exe.config')
    Copy-Item $appConfig $target -Force
    Write-Host ('  ' + $n + '.exe.config')
}

# ---- 6. 汇总 ----
Write-Host ''
Get-ChildItem $dist -File | Sort-Object Name | ForEach-Object {
    Write-Host ('  {0,-28} {1,10:N0} B' -f $_.Name, $_.Length)
}

if ($Test) {
    Write-Host ''
    Write-Host '--- 冒烟测试 ---'
    $cli = Join-Path $dist 'galgame-kit-cli.exe'
    & $cli --help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'CLI --help 失败' }
    Write-Host '  CLI 可执行 OK'

    if (Test-Path $testExe) {
        Write-Host '--- 单元测试 ---'
        & $testExe | Write-Host
        if ($LASTEXITCODE -ne 0) { throw '单元测试未通过' }
    }
}

Write-Host ''
Write-Host '完成。'
