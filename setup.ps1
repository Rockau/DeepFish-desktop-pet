# 大肥鱼 Live2D 桌宠插件：检查、编译、自检。在插件文件夹里运行：
#   powershell -ExecutionPolicy Bypass -File setup.ps1          检查并编译
#   powershell -ExecutionPolicy Bypass -File setup.ps1 -Check   编译完再开一个看不见的桌宠试一遍（推荐）
# 缺什么就停下，告诉你缺什么、去哪里拿；不会自己下载 Live2D 的 SDK 或模型（它们要你自己同意各自的许可）。
#   加 -Hooks 直接接上「干活时的反应」（Claude Code 和 DeepSeek Harness 的 hook，不问）；加 -NoHooks 不接也不问
param([switch]$Check, [switch]$Hooks, [switch]$NoHooks)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = $PSScriptRoot
function Ok($text) { Write-Host ('✓ ' + $text) -ForegroundColor Green }
function Missing($text) { Write-Host ('✗ ' + $text) -ForegroundColor Red; exit 1 }
# 跑一条原生命令，输出存进 cache\setup.log；失败时把最后 40 行打出来。
# （以前这里直接 | Out-Null，失败了只说“看上面的输出”，可上面什么也没有。）
function Run($label, [scriptblock]$command) {
    $logDir = Join-Path $root 'cache'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $log = Join-Path $logDir 'setup.log'
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue' # Windows PowerShell 5.1 会把原生命令的 stderr 当成错误抛出来
    & $command 2>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath $log -Encoding utf8
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code -ne 0) { Get-Content -LiteralPath $log -Tail 40 | Out-Host; Missing "$label 失败了：上面是日志最后 40 行，完整的在 cache\setup.log。" }
}

if (![Environment]::Is64BitOperatingSystem) { Missing '只支持 64 位 Windows。' }

# Cubism SDK for Native：用户自己从 Live2D 官网下载，解压进 sdk\
# 找文件用 .NET 的枚举：解压出来的文件夹和链接过来的文件夹都找得到。
function Find($folder, $name) { if (Test-Path -LiteralPath $folder) { [IO.Directory]::EnumerateFiles($folder, $name, [IO.SearchOption]::AllDirectories) } }
$header = Find (Join-Path $root 'sdk') 'Live2DCubismCore.h' | Where-Object { (Split-Path (Split-Path $_ -Parent) -Leaf) -eq 'include' -and (Split-Path (Split-Path (Split-Path $_ -Parent) -Parent) -Leaf) -eq 'Core' } | Select-Object -First 1
if (!$header) { Missing '没找到 Cubism SDK for Native。到 https://www.live2d.com/sdk/download/native/ 下载（要同意它的许可），把压缩包解压到插件的 sdk 文件夹里。' }
$sdk = Split-Path (Split-Path (Split-Path $header -Parent) -Parent) -Parent
Ok ('Cubism SDK：' + $sdk)

# 模型：用户自己从作者那里获取，放进 model\
$model = Find (Join-Path $root 'model') '大肥鱼.model3.json' | Select-Object -First 1
if (!$model) { Missing '没找到模型。按 model\获取模型.md 去作者的视频里获取，解压到插件的 model 文件夹里（里面要有 大肥鱼.model3.json）。' }
Ok ('模型：' + (Split-Path $model -Parent))

# Visual Studio 生成工具（C++）里的编译器和 CMake
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
# 生成器的版本也按装了 C++ 的这一套来：另外装着没勾 C++ 的新版时，按最新版选会配置失败
$cpp = '-products', '*', '-latest', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
$studio = if (Test-Path -LiteralPath $vswhere) { & $vswhere @cpp -property installationPath }
if (!$studio) { Missing '没装 Visual Studio 生成工具的 C++ 部分。可以运行：winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"' }
$cmake = Join-Path $studio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source }
if (!$cmake) { Missing '没找到 CMake。在 Visual Studio Installer 里给生成工具勾上“用于 Windows 的 C++ CMake 工具”，或者 winget install Kitware.CMake。' }
$line = & $vswhere @cpp -property catalog_productLineVersion
$generator = switch ($line) { '18' { 'Visual Studio 18 2026' } '2026' { 'Visual Studio 18 2026' } '2019' { 'Visual Studio 16 2019' } default { 'Visual Studio 17 2022' } }
Ok ('编译工具：' + $generator)

# 更新时，正在运行的桥接（Agent 开着就一直在）和桌宠占着编好的文件，直接覆盖会失败。Windows 允许给正在用的文件改名：
# 占着的先挪成 .old，新的照常放进去，旧进程用旧文件跑到结束；上次留下的 .old，这次能删就删。
function Free($folder) {
    if (!(Test-Path -LiteralPath $folder)) { return }
    Get-ChildItem -LiteralPath $folder -Filter '*.old' -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
    foreach ($file in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Extension -in '.exe', '.dll' }) {
        try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() }
        catch { Rename-Item -LiteralPath $file.FullName -NewName ($file.Name + '.' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.old') }
    }
}

# 渲染器（链接 Cubism Core，只在你自己电脑上编译，不要把 renderer\build 分享给别人）
$renderer = Join-Path $root 'renderer'
Free (Join-Path $renderer 'build\Release')
Run '渲染器配置' { & $cmake -S $renderer -B (Join-Path $renderer 'build') -G $generator -A x64 "-DCUBISM_SDK=$($sdk -replace '\\', '/')" }
Run '渲染器编译' { & $cmake --build (Join-Path $renderer 'build') --config Release --parallel --target l2d-render }
Ok '渲染器编译好了'

# .NET 10 SDK
$dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$dotnet -or !(& $dotnet.Source --list-sdks | Where-Object { $_ -match '^(1[0-9])\.' })) { Missing '没装 .NET 10 SDK。可以运行：winget install Microsoft.DotNet.SDK.10' }
# 用便携 .NET 编译时，把运行库位置记在启动器里，Explorer 双击时也能找到同一套 .NET。
function Publish-DotNet($project, $output) {
    $publishArgs = @('publish', $project, '-c', 'Release', '-o', $output, '--nologo', '-v', 'q')
    $dotnetFolder = Split-Path -Parent $dotnet.Source
    if ($dotnetFolder -ne (Join-Path $env:ProgramFiles 'dotnet') -and
        (Test-Path -LiteralPath (Join-Path $dotnetFolder 'shared\Microsoft.WindowsDesktop.App'))) {
        $app = [Uri]::new($output.TrimEnd('\') + '\')
        $runtime = [Uri]::new($dotnetFolder.TrimEnd('\') + '\')
        $relative = $app.MakeRelativeUri($runtime)
        if (!$relative.IsAbsoluteUri) {
            $path = [Uri]::UnescapeDataString($relative.OriginalString).Replace('/', '\')
            $publishArgs += "-p:AppHostRelativeDotNet=$path"
        }
    }
    & $dotnet.Source @publishArgs
}

Free (Join-Path $root 'bin')
foreach ($project in 'FishPet', 'FishPet.Mcp') {
    Run "$project 编译" { Publish-DotNet (Join-Path $root "src\$project\$project.csproj") (Join-Path $root 'bin') }
}
Ok '桌宠和 MCP 接口编译好了'

# 托盘控制台（任务栏右下角的小鲸鱼图标，点开能调大小、衣服、开关哄睡和午饭提醒等）：编进 tray\app，桌宠出场时会把它带起来
$tray = Join-Path $root 'tray\FishTray\FishTray.csproj'
if (Test-Path -LiteralPath $tray) {
    Get-Process FishTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $root 'tray\*') } | Stop-Process -Force -ErrorAction SilentlyContinue
    Free (Join-Path $root 'tray\app')
    Run '托盘控制台编译' { Publish-DotNet $tray (Join-Path $root 'tray\app') }
    Ok '托盘控制台编译好了'
    # 托盘图标要从普通完整性（Medium）的进程发出去，Explorer 才收；Low 标签会让图标静默消失（本机加，2026-11-11）
    # 普通文件夹本来就是 Medium，改不改都一样；可普通用户多半没有改标签的权限，会报“拒绝访问”。以前这一下会让脚本停住，
    # 后面的桌面图标、测试、自检全被跳过（issue #1）。现在改不成也接着往下走，只有真带着 Low 标签时才提醒搬地方。
    foreach ($dir in @('bin', 'tray\app')) {
        $path = Join-Path $root $dir
        if (!(Test-Path -LiteralPath $path)) { continue }
        $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue' # Windows PowerShell 5.1 会把原生命令的 stderr 当成错误抛出来
        & icacls $path /setintegritylevel '(OI)(CI)M' /T /C /Q 2>&1 | Out-Null
        # 不看退出码：带 /C 时没改成也可能返回 0。改完再读一遍标签，还是 Low 才算没改成。
        $label = (& icacls $path 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $ErrorActionPreference = $previous
        if ($label -match 'Low Mandatory Level|S-1-16-4096') {
            Write-Host ('⚠ ' + $dir + ' 带着低完整性标签，又改不回来：托盘图标可能不出现，桌宠也可能连不上。把插件放到普通文件夹（不是 Agent 的工作区）重新拉一份再编译，见 AGENTS.md 第 2 步。') -ForegroundColor Yellow
        }
    }
}
# 桌面上放一个「DeepFish」图标：双击就能手动把她叫出来（不用在 Agent 里打字）。--manual 让她不管「跟随 Agent 一起启动」开没开，直接露面，
# 她已经在收着待命就叫醒。每次装/更新都会按现在的路径重写一次这个快捷方式。
try {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if ((Test-Path -LiteralPath $desktop) -and (Test-Path -LiteralPath (Join-Path $root 'bin\FishPet.exe'))) {
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut((Join-Path $desktop 'DeepFish.lnk'))
        $link.TargetPath = Join-Path $root 'bin\FishPet.exe'
        $link.Arguments = '--manual'
        $link.WorkingDirectory = $root
        $link.Description = '把 DeepFish 叫出来'
        $link.IconLocation = (Join-Path $root 'bin\FishPet.exe') + ',0'   # 图标编在 exe 里（FishPet.csproj 的 ApplicationIcon）：放在中文路径下的单独 .ico 文件，资源管理器读不出来，显示成空白
        $link.Save()
        Ok '桌面上放好了「DeepFish」图标，双击就能把她叫出来'
    }
} catch { Write-Host ('桌面图标没放成（不影响使用）：' + $_.Exception.Message) -ForegroundColor Yellow }

# 测试和自检要往临时目录里写文件：插件可能带着低完整性标签（Agent 的工作区、沙箱），系统的临时目录反而写不进去，
# 指到插件自己的 cache 下面最稳（本机修，2026-11-11）。
$tmp = Join-Path $root 'cache\tmp'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TMP = $tmp; $env:TEMP = $tmp
& dotnet run --project (Join-Path $root 'src\FishPet.Tests\FishPet.Tests.csproj') -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { Missing '动作逻辑测试没通过。' }

$mcp = Join-Path $root 'bin\FishPet.Mcp.exe'
if ($Check) {
    & $mcp --check | Out-Host
    if ($LASTEXITCODE -ne 0) { Missing '自检没通过，看上面的输出；还有问题看插件里的 errors.log。' }
}

# 干活时的反应（可选，AGENTS.md 第 9 步）：把 hooks\claude\fish_hook.py 接进 Claude Code 的 settings.json。
# 它是 Claude 的全局配置，所以不偷偷改：交互运行时问一句，-Hooks 直接接，-NoHooks 跳过。只往里追加，改之前自动备份。
$installer = Join-Path $root 'hooks\claude\install_hooks.py'
if (!$NoHooks -and (Test-Path -LiteralPath $installer) -and (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.claude'))) {
    $py = $null
    foreach ($name in 'python', 'py') { $c = Get-Command $name -ErrorAction SilentlyContinue; if ($c) { $py = $c.Source; break } }
    if (!$py) {
        Write-Host '想要「干活时的反应」（开工、进度、出错、做完她会在旁边演一下）要先装 Python 3：winget install Python.Python.3.12，装完重新运行 setup.ps1 -Hooks。' -ForegroundColor Yellow
    } else {
        $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & $py -X utf8 $installer --status | Out-Null
        $state = $LASTEXITCODE
        $ErrorActionPreference = $previous
        if ($state -eq 0) {
            Ok '干活时的反应已经接好了（Claude Code）'
        } elseif ($state -eq 3) {
            $go = $Hooks
            if (!$go -and [Environment]::UserInteractive -and ![Console]::IsInputRedirected) {
                Write-Host ''
                Write-Host '要接上「干活时的反应」吗？你用 Claude Code 干活时，她会在旁边演一下：开工、进度、出错、测试通过、做完。' -ForegroundColor Cyan
                Write-Host '会往 %USERPROFILE%\.claude\settings.json 里追加几条 hook（不动你原来的，改之前自动备份），不花 token。'
                $go = (Read-Host '接上？[Y/n]') -notmatch '^\s*[nN]'
            }
            if ($go) {
                $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
                & $py -X utf8 $installer | Out-Host
                $ErrorActionPreference = $previous
            } else {
                Write-Host '没有接。以后想接：重新运行 setup.ps1 -Hooks，或者运行 python hooks\claude\install_hooks.py。'
            }
        } else {
            Write-Host '没动 Claude 的 settings.json（读不了或格式不对）。想接的话看 AGENTS.md 第 9 步手动加。' -ForegroundColor Yellow
        }
    }
}

# 干活时的反应（DeepSeek Harness，可选，AGENTS.md 第 9 步）：把 hooks\dsh\dafeiyu-hooks 当本地插件包装进 DSH 的 profile。
# 它改的是 DSH 的全局配置，所以交互运行时问一句；-Hooks 直接装，-NoHooks 跳过。
# 用子进程调：setup 自己是 -ExecutionPolicy Bypass 起来的，用户机器上的策略不一定允许直接跑另一个脚本。
$dshInstaller = Join-Path $root 'hooks\dsh\install_hooks.ps1'
$dshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }
if (!$NoHooks -and (Test-Path -LiteralPath $dshInstaller) -and (Test-Path -LiteralPath (Join-Path $dshHome 'profiles'))) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dshInstaller -Status | Out-Null
    $state = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($state -eq 0) {
        Ok '干活时的反应已经接好了（DeepSeek Harness）'
    } elseif ($state -eq 3) {
        $go = $Hooks
        if (!$go -and [Environment]::UserInteractive -and ![Console]::IsInputRedirected) {
            Write-Host ''
            Write-Host '要接上 DeepSeek Harness 的「干活时的反应」吗？你在 DSH 里干活时，她会在旁边演一下：开工、进度、出错、测试通过、做完。' -ForegroundColor Cyan
            Write-Host '会往 DSH 的 profile 里加一个本地插件包（链接到这个文件夹，不动你原来的插件），不花 token。'
            $go = (Read-Host '接上？[Y/n]') -notmatch '^\s*[nN]'
        }
        if ($go) {
            $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dshInstaller | Out-Host
            $ErrorActionPreference = $previous
        } else {
            Write-Host 'DeepSeek Harness 的「干活时的反应」还没接；想接就运行 setup.ps1 -Hooks（或者 hooks\dsh\install_hooks.ps1）。'
        }
    } else {
        Write-Host '没动 DeepSeek Harness 的配置（读不了它的 profile）。想接的话看 AGENTS.md 第 9 步。' -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '编译和自检通过了，但还没接到 Agent 上。下一步：把下面这个 MCP 服务接进你的 Agent（stdio，没有参数）：' -ForegroundColor Cyan
Write-Host ('  ' + $mcp)
Write-Host '  Claude Code： claude mcp add dafeiyu -s user -- "' -NoNewline; Write-Host ($mcp + '"')
Write-Host '  其他 Agent：在它的 MCP 配置里加 { "command": "' -NoNewline; Write-Host (($mcp -replace '\\', '\\') + '" }')
Write-Host '用模型 API 自己搭的程序（没有 MCP）：开着 bin\FishPet.exe，按 api.json 里的地址和口令发 HTTP 请求。'
Write-Host '然后把 prompt\身体说明.md 填好称呼和名字，加进角色的人设里，再验收。详见 AGENTS.md 第 6、7 步。'
