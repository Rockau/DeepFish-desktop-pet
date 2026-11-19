# 大肥鱼「干活时的反应」：把 hooks\dsh\dafeiyu-hooks 装进 DeepSeek Harness 的 profile。
# 装法是本地目录链接（DSH 装本地插件也是这么做的），所以插件文件夹挪了位置，要重新跑一次这个脚本。
#   powershell -ExecutionPolicy Bypass -File install_hooks.ps1            装上（已经装好就只报告）
#   powershell -ExecutionPolicy Bypass -File install_hooks.ps1 -Status    只看状态：退出码 0 = 装好了，3 = 没装，2 = 找不到 DSH 或读不了配置
#   powershell -ExecutionPolicy Bypass -File install_hooks.ps1 -Remove    拆掉
#   powershell -ExecutionPolicy Bypass -File install_hooks.ps1 -DryRun    只说会做什么，不动手
#   ... -Profile web   指定 profile（默认 $env:DSH_PROFILE，其次 desktop，再次唯一那个 profile）
param([switch]$Status, [switch]$Remove, [switch]$DryRun, [string]$Profile = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$pluginRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bundleDir = Join-Path $PSScriptRoot 'dafeiyu-hooks'
$packageName = '@local/dafeiyu-hooks'
$apiJson = Join-Path $pluginRoot 'api.json'

function Fail($text, $code) { Write-Host $text; exit $code }

# DSH 的家目录和 profile
$dshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }
if (!(Test-Path -LiteralPath $dshHome)) { Fail ('没找到 DeepSeek Harness（' + $dshHome + ' 不存在）。没装的话这一步不用做。') 2 }
$profilesRoot = Join-Path $dshHome 'profiles'
$profileName = $Profile
if (!$profileName -and $env:DSH_PROFILE) { $profileName = $env:DSH_PROFILE }
if (!$profileName -and (Test-Path -LiteralPath (Join-Path $profilesRoot 'desktop\package.json'))) { $profileName = 'desktop' }
if (!$profileName -and (Test-Path -LiteralPath $profilesRoot)) {
    $found = @(Get-ChildItem -LiteralPath $profilesRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'package.json') })
    if ($found.Count -eq 1) { $profileName = $found[0].Name }
}
if (!$profileName) { Fail '有不止一个 DeepSeek Harness profile，请用 -Profile 指定一个（比如 -Profile desktop）。' 2 }
if ($profileName -match '[\\/]' -or $profileName -eq '..') { Fail ('profile 名字不对：' + $profileName) 2 }
$profileDir = Join-Path $profilesRoot $profileName
$manifestPath = Join-Path $profileDir 'package.json'
if (!(Test-Path -LiteralPath $manifestPath)) { Fail ('这个 profile 还没初始化（' + $manifestPath + ' 不存在）：先打开一次 DeepSeek Harness Desktop，再重新运行。') 2 }
if (!(Test-Path -LiteralPath (Join-Path $bundleDir 'package.json'))) { Fail ('找不到插件包：' + $bundleDir) 2 }

function Read-Manifest { [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json }

# 依赖可能在 dependencies / devDependencies / optionalDependencies 里，哪份里有都算。
function Get-Dependency($manifest) {
    foreach ($field in 'dependencies', 'devDependencies', 'optionalDependencies') {
        $bag = $manifest.$field
        if ($bag) { $property = $bag.PSObject.Properties[$packageName]; if ($property) { return $property.Value } }
    }
    return $null
}

# 依赖记的如果是本地路径（link: / file:），取出来统一成完整路径，好和插件文件夹比较。
function Get-RecordedPath([string]$spec) {
    if ($spec -and $spec -match '^(?:link|file):(.+)$') {
        try { return [IO.Path]::GetFullPath(($Matches[1] -replace '/', '\')) } catch { return $null }
    }
    return $null
}

function Get-LinkPath { Join-Path $profileDir 'node_modules\@local\dafeiyu-hooks' }

# 链接还在不在、是不是指向这个插件文件夹。插件挪了位置时，依赖里记的旧路径和这个文件夹对不上，算没装好。
function Get-State {
    $manifest = Read-Manifest
    $dependency = Get-Dependency $manifest
    $linked = Test-Path -LiteralPath (Join-Path (Get-LinkPath) 'package.json')
    $samePlace = $true
    $recorded = Get-RecordedPath $dependency
    if ($recorded) { $samePlace = ($recorded.TrimEnd('\') -ieq ([IO.Path]::GetFullPath($bundleDir)).TrimEnd('\')) }
    $enabled = @($manifest.dsh.profile.bundles) -contains $packageName
    return [pscustomobject]@{ Manifest = $manifest; Dependency = $dependency; Linked = ($linked -and $samePlace); Enabled = $enabled }
}

# pnpm 删 link: 依赖时不一定会把 node_modules 里的链接一起删掉，自己收尾。
# 只删指向这个插件文件夹的那一个（连带空的 @local 目录），别人的东西不动。
function Clear-Link {
    $path = Get-LinkPath
    if (!(Test-Path -LiteralPath $path)) { return }
    $expected = [IO.Path]::GetFullPath($bundleDir).TrimEnd('\')
    $target = $null
    try { $target = @((Get-Item -LiteralPath $path -Force).Target)[0] } catch { }
    if (!$target) { return }
    $pointsHere = $false
    try { $pointsHere = ([IO.Path]::GetFullPath($target).TrimEnd('\') -ieq $expected) } catch { }
    if (!$pointsHere) { return }
    try { [IO.Directory]::Delete($path, $false) } catch { return }
    $scope = Split-Path $path -Parent
    try { if (@(Get-ChildItem -LiteralPath $scope -Force).Count -eq 0) { [IO.Directory]::Delete($scope, $false) } } catch { }
}

try { $state = Get-State }
catch { Fail ('读不了 ' + $manifestPath + '（' + $_.Exception.Message + '）。') 2 }

# 找 DSH 自带的命令行：Desktop 版用它自己的 desktop-cli.js（只有它能改 desktop profile），
# 装在 PATH 上的 dsh 只用来管别的 profile。
# 注意：app.asar 是个文件，PowerShell 看不进里面，所以只检查 asar 和 exe 在不在，里面的路径直接拼给 Electron。
function Find-Dsh {
    # 只有装了 app.asar 的 Desktop 版才有 desktop-cli.js；源码/命令行的 DSH_INSTALL_DIR 指向别处，不认。
    if ($env:DSH_INSTALL_DIR -and $env:DSH_NODE_EXECUTABLE -and $env:DSH_INSTALL_DIR -like '*app.asar') {
        if ((Test-Path -LiteralPath $env:DSH_INSTALL_DIR) -and (Test-Path -LiteralPath $env:DSH_NODE_EXECUTABLE)) {
            return [pscustomobject]@{ Exe = $env:DSH_NODE_EXECUTABLE; Cli = (Join-Path $env:DSH_INSTALL_DIR 'lib\desktop-cli.js') }
        }
    }
    foreach ($base in @((Join-Path $env:LOCALAPPDATA 'Programs'), $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (!$base -or !(Test-Path -LiteralPath $base)) { continue }
        foreach ($app in @(Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue)) {
            $asar = Join-Path $app.FullName 'resources\app.asar'
            if (!(Test-Path -LiteralPath $asar)) { continue }
            # 主程序是安装目录里最大的那个 exe，卸载程序不算。
            $exe = Get-ChildItem -LiteralPath $app.FullName -File -Filter '*.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notlike 'Uninstall*' -and $_.Length -gt 20MB } |
                Sort-Object Length -Descending | Select-Object -First 1
            if (!$exe) { continue }
            # 同目录下别的 Electron 程序也有 app.asar，认一下是 DSH 才用。
            $unpacked = Join-Path $app.FullName 'resources\app.asar.unpacked\node_modules\@deepseek-ai'
            $isDsh = (Test-Path -LiteralPath $unpacked) -or ($app.Name -match 'DSH|DeepSeek') -or ($exe.Name -match 'DSH|DeepSeek')
            if (!$isDsh) { continue }
            return [pscustomobject]@{ Exe = $exe.FullName; Cli = (Join-Path $asar 'lib\desktop-cli.js') }
        }
    }
    $command = Get-Command dsh -ErrorAction SilentlyContinue
    if ($command) { return [pscustomobject]@{ Command = $command.Source } }
    return $null
}

function Invoke-DshCli([string[]]$arguments) {
    $dsh = Find-Dsh
    if (!$dsh) {
        Fail ('没找到 DeepSeek Harness 的程序（常见的安装位置都找过了）。可以打开 DSH 的“插件”页，点“添加插件”，选这个文件夹：' + $bundleDir) 2
    }
    $common = @('plugin', '--profile', $profileName) + $arguments
    Write-Host ('运行：' + ($common -join ' ')) -ForegroundColor DarkGray
    if ($dsh.Command) {
        $lines = & $dsh.Command @common 2>&1
        $code = $LASTEXITCODE
        $lines | ForEach-Object { Write-Host $_ }
        return $code
    }
    # Electron 是 GUI 程序，直接用 & 调不会等它跑完，所以用 ProcessStartInfo 收输出、等退出。
    $previous = $env:ELECTRON_RUN_AS_NODE
    $env:ELECTRON_RUN_AS_NODE = '1'
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $dsh.Exe
        $quoted = @('"' + $dsh.Cli + '"') + @($common | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } })
        $psi.Arguments = $quoted -join ' '
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
        $psi.StandardErrorEncoding = New-Object System.Text.UTF8Encoding $false
        $process = [System.Diagnostics.Process]::Start($psi)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.Result
        if ($text) { Write-Host $text.TrimEnd() }
        $text = $stderr.Result
        if ($text) { Write-Host $text.TrimEnd() }
        return $process.ExitCode
    } finally {
        if ($null -eq $previous) { Remove-Item Env:\ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue }
        else { $env:ELECTRON_RUN_AS_NODE = $previous }
    }
}

if ($Remove) {
    if (!$state.Dependency -and !$state.Enabled) {
        if (!$DryRun) { Clear-Link }
        Write-Host ('DeepSeek Harness（profile：' + $profileName + '）里没有大肥鱼的 hook，不用拆。')
        exit 0
    }
    if ($DryRun) {
        Write-Host ('会从 profile「' + $profileName + '」里拆掉 ' + $packageName + '。')
        exit 0
    }
    $code = Invoke-DshCli @('remove', $packageName)
    if ($code -ne 0) { Fail ('拆的时候失败了（退出码 ' + $code + '）。看上面前面的输出。') 1 }
    $state = Get-State
    if ($state.Dependency -or $state.Enabled) { Fail '拆完以后配置里还看得到它，没拆干净，看上面的输出。' 1 }
    Clear-Link
    Write-Host ('✓ 拆掉了（DeepSeek Harness，profile：' + $profileName + '）。重启 DeepSeek Harness 后生效。') -ForegroundColor Green
    exit 0
}

$ready = $state.Dependency -and $state.Linked -and $state.Enabled
if ($Status) {
    if ($ready) {
        Write-Host ('✓ 干活时的反应已经接好了（DeepSeek Harness，profile：' + $profileName + '）') -ForegroundColor Green
        exit 0
    }
    if ($state.Dependency -and !$state.Linked) {
        Write-Host ('装了，但链接对不上（插件文件夹可能挪过位置）：' + $packageName)
    } elseif ($state.Dependency -and !$state.Enabled) {
        Write-Host ('装了，但没启用：dsh.profile.bundles 里没有 ' + $packageName)
    } else {
        Write-Host ('还没接上（DeepSeek Harness，profile：' + $profileName + '）')
    }
    exit 3
}

if ($ready) {
    Write-Host ('✓ 干活时的反应已经接好了（DeepSeek Harness，profile：' + $profileName + '），不用再装。') -ForegroundColor Green
    exit 0
}
if (!(Test-Path -LiteralPath $apiJson)) {
    Write-Host '提示：api.json 还没生成，第一次启动桌宠后才会生成；hook 每次发事件时都会重新读它，不影响现在装。'
}
if ($DryRun) {
    Write-Host ('会往 DeepSeek Harness 的 profile「' + $profileName + '」里装：')
    Write-Host ('  ' + $packageName + ' ← ' + $bundleDir)
    exit 0
}
Write-Host ('往 DeepSeek Harness 的 profile「' + $profileName + '」里装「干活时的反应」：')
Write-Host ('  ' + $packageName + ' ← ' + $bundleDir)
Write-Host '只加这一个本地插件包，不动你原来的插件。'
# 以前装过（关掉了、或者插件挪了位置）：先把旧的拆干净，再按现在的位置装。
# 直接 add 不会把已有的依赖重新加进 dsh.profile.bundles，关掉过的那种会一直回不来。
if ($state.Dependency) {
    Write-Host '检测到以前装过，先拆掉旧的再装。'
    $code = Invoke-DshCli @('remove', $packageName)
    if ($code -ne 0) { Fail ('拆旧的失败了（退出码 ' + $code + '）。看上面的输出。') 1 }
    Clear-Link
}
$code = Invoke-DshCli @('add', ('link:' + $bundleDir))
if ($code -ne 0) { Fail ('装的时候失败了（退出码 ' + $code + '）。看上面的输出；完整日志在 ' + (Join-Path $profileDir '.plugin-manager\logs') + '。') 1 }
$state = Get-State
if (!($state.Dependency -and $state.Linked -and $state.Enabled)) {
    Fail '装完以后配置里没看到它，没装好：看上面的输出。' 1
}
Write-Host ('✓ 接上了（DeepSeek Harness，profile：' + $profileName + '）。重启 DeepSeek Harness 后生效。') -ForegroundColor Green
exit 0
