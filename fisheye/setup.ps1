# 鱼眼（陪玩陪看）安装：Python 环境、llama.cpp（CUDA 版）、视觉模型 Qwen3-VL-8B，都装进这个 fisheye 文件夹。
# 在 fisheye 文件夹里运行：
#   powershell -ExecutionPolicy Bypass -File setup.ps1
# 已经装好的会跳过（下载的文件都按 SHA-256 校验），可以放心重复运行。大约要下载 6.8GB。
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = $PSScriptRoot
function Ok($text) { Write-Host ('✓ ' + $text) -ForegroundColor Green }
function Step($text) { Write-Host ('… ' + $text) -ForegroundColor Cyan }
function Fail($text) { Write-Host ('✗ ' + $text) -ForegroundColor Red; exit 1 }

if (![Environment]::Is64BitOperatingSystem) { Fail '只支持 64 位 Windows。' }

# ---------- 显卡：要 NVIDIA 显卡（视觉模型跑在显卡上，大约占 8GB 显存） ----------
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if (!$smi) { Fail '没找到 NVIDIA 显卡驱动（nvidia-smi）。鱼眼的视觉模型要 NVIDIA 显卡，显存最好 12GB 以上。' }
$smiText = (& nvidia-smi 2>&1 | Out-String)
$cuda = [regex]::Match($smiText, 'CUDA (?:UMD )?Version:\s*(\d+)\.(\d+)')
if (!$cuda.Success) { Fail '读不出显卡驱动支持的 CUDA 版本，先把 NVIDIA 驱动更新到最新。' }
$cudaMajor = [int]$cuda.Groups[1].Value
$vram = [regex]::Match((& nvidia-smi --query-gpu=name,memory.total --format=csv,noheader 2>&1 | Out-String), '^(.*?),\s*(\d+)')
if ($vram.Success) {
    Ok ('显卡：' + $vram.Groups[1].Value.Trim() + '，' + [math]::Round([int]$vram.Groups[2].Value / 1024) + 'GB 显存，驱动支持 CUDA ' + $cuda.Groups[1].Value + '.' + $cuda.Groups[2].Value)
    if ([int]$vram.Groups[2].Value -lt 10000) { Write-Host '⚠ 显存不到 10GB：视觉模型要占大约 8GB，边玩游戏边开可能会卡。' -ForegroundColor Yellow }
}

# ---------- 下载并校验 ----------
function Hash($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Fetch($urls, $path, $sha) {
    if ((Test-Path -LiteralPath $path) -and (Hash $path) -eq $sha) { Ok ('已经有了：' + (Split-Path $path -Leaf)); return }
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    foreach ($url in $urls) {
        Step ('下载 ' + (Split-Path $path -Leaf) + '（' + ([Uri]$url).Host + '）')
        & curl.exe -L --fail --retry 5 --retry-delay 3 -C - -o $path $url
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $path) -and (Hash $path) -eq $sha) { Ok ('下载好了：' + (Split-Path $path -Leaf)); return }
        Write-Host '  这个地址没下成或校验不对，换一个……' -ForegroundColor Yellow
        Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
    }
    Fail ('下载失败：' + (Split-Path $path -Leaf) + '。网络好一点再运行一次，已经下好的不会重下。')
}

# ---------- llama.cpp（只用它的 llama-server），按驱动选 CUDA 13 或 12 的版本 ----------
$build = 'b12503'
if ($cudaMajor -ge 13) {
    $bins = @(@("llama-$build-bin-win-cuda-13.5-x64.zip", '3fe029b326ece8dfbded878009b6c5dab1d2a50e42e322605de722b4b6fc77a0'),
              @('cudart-llama-bin-win-cuda-13.5-x64.zip', 'c2bf4cc202bfbaaa7a0b75f5082457c3811326947bb216059b72839d844e9cba'))
} else {
    $bins = @(@("llama-$build-bin-win-cuda-12.4-x64.zip", '9df9b6316051ce258debb234b3475762eecd8721e6bb41474f0743d54aad08aa'),
              @('cudart-llama-bin-win-cuda-12.4-x64.zip', '8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6'))
}
$llama = Join-Path $root 'llama'
$server = Join-Path $llama 'llama-server.exe'
$haveCudart = (Test-Path -LiteralPath $llama) -and (Get-ChildItem -LiteralPath $llama -Filter 'cudart64_*.dll' -ErrorAction SilentlyContinue)
if ((Test-Path -LiteralPath $server) -and (Test-Path -LiteralPath (Join-Path $llama 'ggml-cuda.dll')) -and $haveCudart) {
    Ok 'llama.cpp 已经装好了'
} else {
    foreach ($bin in $bins) {
        $zip = Join-Path $llama $bin[0]
        Fetch @("https://github.com/ggml-org/llama.cpp/releases/download/$build/" + $bin[0]) $zip $bin[1]
        Expand-Archive -LiteralPath $zip -DestinationPath $llama -Force
        Remove-Item -LiteralPath $zip
    }
    Ok 'llama.cpp 装好了'
}
$previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$version = (& $server --version 2>&1 | Out-String)
$ErrorActionPreference = $previous
if ($LASTEXITCODE -ne 0) { Write-Host $version; Fail 'llama-server 开不起来，看上面的输出（多半是显卡驱动太旧）。' }

# ---------- 视觉模型：Qwen3-VL-8B-Instruct（Q4_K_M）和它的视觉投影，先从魔搭下，不行再从 Hugging Face ----------
$models = Join-Path $root 'models'
foreach ($file in @(@('Qwen3VL-8B-Instruct-Q4_K_M.gguf', '67d1659bfe71b89d50b45a4ad1a9e5b997e5bb16ce5da66a6a6167abd569e9e2'),
                    @('mmproj-Qwen3VL-8B-Instruct-F16.gguf', 'ca524100ebf825c9a870db1c580d03879e0da0ab2541697e2458e64891cf9d38'))) {
    Fetch @("https://modelscope.cn/models/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/master/" + $file[0],
            "https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/main/" + $file[0]) (Join-Path $models $file[0]) $file[1]
}

# ---------- Python 环境（截屏、读字幕） ----------
$venvPython = Join-Path $root '.venv\Scripts\python.exe'
if (!(Test-Path -LiteralPath $venvPython)) {
    $python = $null
    foreach ($candidate in @(@('py', '-3'), @('python'))) {
        $command = Get-Command $candidate[0] -ErrorAction SilentlyContinue
        if (!$command) { continue }
        $arguments = @($candidate | Select-Object -Skip 1)
        $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $ok = & $command.Source @arguments -c 'import sys; print(sys.version_info >= (3, 10))' 2>$null
        $ErrorActionPreference = $previous
        if ($ok -eq 'True') { $python = @($command.Source) + $arguments; break }
    }
    if (!$python) { Fail '没找到 Python 3.10 以上。可以运行：winget install Python.Python.3.12，装完重新打开终端再运行这个脚本。' }
    Step '建 Python 环境'
    & $python[0] @($python | Select-Object -Skip 1) -m venv (Join-Path $root '.venv')
    if ($LASTEXITCODE -ne 0) { Fail '建 Python 环境失败。' }
}
Step '装 Python 依赖'
$previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& $venvPython -m pip install -q --disable-pip-version-check -r (Join-Path $root 'requirements.txt') 2>&1 | Out-Host
$pipCode = $LASTEXITCODE
$ErrorActionPreference = $previous
if ($pipCode -ne 0) { Fail '装 Python 依赖失败，看上面的输出。' }
# 读字幕用的 OCR 第一次用时要下它自己的小模型，这里先下好。
# Windows PowerShell 5.1 传给原生程序时会吃掉参数里的双引号，所以 Python 里只用单引号（PowerShell 里写成两个）；
# 它还会把原生程序往 stderr 写的日志当成错误抛出来，这几行先放宽。
$previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$ocr = (& $venvPython -c 'from rapidocr import RapidOCR; RapidOCR(params={''Global.log_level'': ''error''}); print(''OCR-OK'')' 2>&1 | Out-String)
$ErrorActionPreference = $previous
if ($ocr -notmatch 'OCR-OK') { Write-Host $ocr; Fail 'OCR 初始化失败，看上面的输出。' }
Ok 'Python 环境装好了'

# ---------- 配置 ----------
$config = Join-Path $root 'config.json'
if (!(Test-Path -LiteralPath $config)) { Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination $config; Ok '生成了 config.json' }
$key = ''
try { $key = (Get-Content -LiteralPath $config -Raw -Encoding UTF8 | ConvertFrom-Json).deepseek_api_key } catch { }
if (!$key) {
    try { $key = (Get-Content -LiteralPath (Join-Path $root '..\settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json).LlmApiKey } catch { }
}

Write-Host ''
Write-Host '鱼眼装好了。' -ForegroundColor Cyan
if (!$key) { Write-Host '还差一步：把 DeepSeek 的 API key 填进 fisheye\config.json 的 deepseek_api_key（不填的话她只看不说）。' -ForegroundColor Yellow }
Write-Host '托盘面板「更多设置 → 陪玩陪看」里打开开关就行。托盘里没有这一栏的话，先在插件文件夹里重新运行一次 setup.ps1，把托盘重新编译一下。'
