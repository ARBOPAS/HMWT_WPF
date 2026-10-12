<#
    Сборка установщика HMWT / Гримуар.
    Запуск:  powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform      = "x64",
    [string]$ProductName   = "HMWT Гримуар",
    [string]$FileDescription = "HMWT Гримуар — редактор персонажа и инструменты ведущего",
    [switch]$SkipBuild,
    [switch]$NoDesktopCopy,
    [switch]$IncludeSrdData,
    [string]$MsBuildPath = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# ── 0. Надежное определение путей ─────────────────────────────────────────
if ([string]::IsNullOrEmpty($PSScriptRoot)) {
    $PSScriptRoot = $PWD.Path
    Write-Warning "PSScriptRoot не определен. Используем текущую директорию: $PSScriptRoot"
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($repoRoot)) { $repoRoot = $PSScriptRoot }

$projectDir = Join-Path $repoRoot "HMWT_MUI"
$csproj     = Join-Path $projectDir "HMWT_MUI.csproj"
$issFile    = Join-Path $PSScriptRoot "HMWT_MUI.iss"
$stageDir   = Join-Path $PSScriptRoot "staging"
$outputDir  = Join-Path $PSScriptRoot "Output"

function Step([string]$text) { Write-Host ""; Write-Host ("=== " + $text) -ForegroundColor Cyan }
function Ok([string]$text)   { Write-Host ("  [ok] " + $text) -ForegroundColor Green }
function Warn([string]$text) { Write-Host ("  [!] " + $text) -ForegroundColor Yellow }
function Die([string]$text)  { Write-Host ("  [x] " + $text) -ForegroundColor Red; exit 1 }

# ── 1. Версия из AssemblyInfo или csproj ──────────────────────────────────
Step "Определяю версию"
$asmInfo = Join-Path $projectDir "Properties\AssemblyInfo.cs"
$version = "1.0.0.0"

if (Test-Path $asmInfo) {
    $m = Select-String -Path $asmInfo -Pattern 'AssemblyVersion\("([^"]+)"\)' | Select-Object -First 1
    if ($m) { $version = $m.Matches[0].Groups[1].Value }
} else {
    # Фоллбэк: читаем версию из самого .csproj (для современных .NET)
    $csprojContent = Get-Content $csproj -Raw -ErrorAction SilentlyContinue
    if ($csprojContent -match '<Version>([^<]+)</Version>') {
        $version = $Matches[1]
    } elseif ($csprojContent -match '<AssemblyVersion>([^<]+)</AssemblyVersion>') {
        $version = $Matches[1]
    } else {
        Warn "Версия не найдена в AssemblyInfo.cs и .csproj, беру $version по умолчанию"
    }
}
Ok "версия: $version"

# ── 2. Сборка приложения ──────────────────────────────────────────────────
if (-not $SkipBuild) {
    Step "Собираю $Configuration|$Platform"
    
    # Проект старого формата (WPF, .NET Framework 4.8): его собирает MSBuild из
    # Visual Studio. `dotnet build` такие проекты не берёт — он ищет SDK-стиль и
    # падает, поэтому dotnet используется только как `dotnet msbuild` и только
    # если полноценный MSBuild не найден.
    $msbuild = $null
    $msbuildArgs = @()

    # Приоритет 0: путь, переданный параметром -MsBuildPath
    if ($MsBuildPath -and (Test-Path $MsBuildPath)) {
        $msbuild = $MsBuildPath
        Ok "MSBuild из параметра: $msbuild"
    }

    # Приоритет 1: vswhere — официальный способ найти MSBuild нужной версии
    if (-not $msbuild) {
        $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $vswhere) {
            $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
            if ($found -and (Test-Path $found)) { $msbuild = $found }
        }
    }

    # Приоритет 2: известные пути (в том числе нумерованные выпуски вроде 18)
    if (-not $msbuild) {
        $patterns = @(
            "C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe",
            "C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe",
            "C:\Program Files\Microsoft Visual Studio\*\MSBuild\Current\Bin\MSBuild.exe"
        )
        foreach ($pattern in $patterns) {
            $found = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
            if ($found) { $msbuild = $found.FullName; break }
        }
    }

    # Приоритет 3: MSBuild из поставки dotnet (dotnet msbuild, а не dotnet build)
    if (-not $msbuild) {
        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($dotnetCmd) {
            $msbuild = $dotnetCmd.Source
            $msbuildArgs = @("msbuild")
            Warn "Полноценный MSBuild не найден, использую dotnet msbuild: $msbuild"
        }
    }

    if (-not $msbuild) { Die "Не найден MSBuild. Установите Visual Studio с компонентом MSBuild или укажите -MsBuildPath." }
    if ($msbuildArgs.Count -eq 0) { Ok "MSBuild: $msbuild" }

    # Запуск сборки. Важно: у нативной программы сообщения об ошибке идут в stderr,
    # и при $ErrorActionPreference = "Stop" PowerShell превращает их в исключение —
    # скрипт падал раньше, чем показывал причину. Поэтому на время вызова
    # ошибки не считаются прерывающими, а код возврата проверяем сами.
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $buildOut = & $msbuild @msbuildArgs "$csproj" /p:Configuration=$Configuration /p:Platform=$Platform /t:Rebuild /v:m /nologo 2>&1 | Out-String
        $buildCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }

    if ($buildCode -ne 0) {
        $lines = $buildOut -split "`r?`n"
        $errors = $lines | Where-Object { $_ -match ': error|error [A-Z]{2,}\d+' }
        if ($errors) {
            $errors | Select-Object -First 20 | ForEach-Object { Write-Host ("      " + $_.Trim()) -ForegroundColor Red }
        } else {
            Warn "MSBuild не вернул строк с пометкой error — показываю последние строки вывода:"
            $lines | Where-Object { $_.Trim() } | Select-Object -Last 15 | ForEach-Object { Write-Host ("      " + $_.Trim()) }
        }
        Die "сборка приложения не удалась (код $buildCode)"
    }
    Ok "приложение собрано"
} else {
    Warn "сборка пропущена (-SkipBuild)"
}

# ── Иконка установщика ───────────────────────────────────────────────────
# Берём ту же иконку, что у программы: раньше в assets\app.ico лежала чужая
# картинка силуэта, и установщик в проводнике выглядел чужеродно.
$logoIcon = Join-Path $projectDir "Resources\HmwtLogo.ico"
$setupIcon = Join-Path $PSScriptRoot "assets\app.ico"
if (Test-Path $logoIcon) {
    Copy-Item $logoIcon $setupIcon -Force
    Ok "иконка установщика взята из Resources\HmwtLogo.ico"
} elseif (-not (Test-Path $setupIcon)) {
    Warn "нет иконки для установщика: $setupIcon"
}

# ── Умный поиск папки сборки (исправляет проблему bin\x64\Release\net8.0) ─
$binRoot = Join-Path $projectDir "bin"
$appDir = Join-Path $binRoot "$Platform\$Configuration"

# Если exe нет в стандартной папке (случай .NET Core/5+), ищем рекурсивно
if (-not (Test-Path (Join-Path $appDir "HMWT_MUI.exe"))) {
    $foundExe = Get-ChildItem -Path $binRoot -Filter "HMWT_MUI.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($foundExe) {
        $appDir = $foundExe.DirectoryName
        Warn "Используем найденную папку сборки: $appDir"
    } else {
        Die "Не найден HMWT_MUI.exe в $binRoot"
    }
}

# ── 3. Чистый staging ─────────────────────────────────────────────────────
Step "Готовлю staging"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$excluded = @("*.pdb", "*.xml", "*.vshost.exe", "*.vshost.exe.config", "*.vshost.exe.manifest")
$excludedLibs = @("Microsoft.Web.WebView2.*.dll", "WebView2Loader.dll")

$copied = 0; $skippedData = 0; $skippedLibs = 0

Get-ChildItem -Path $appDir -Recurse -File | ForEach-Object {
    $skip = $false
    foreach ($pattern in $excluded) { if ($_.Name -like $pattern) { $skip = $true; break } }
    foreach ($pattern in $excludedLibs) { if ($_.Name -like $pattern) { $skip = $true; $script:skippedLibs++; break } }

    if (-not $IncludeSrdData) {
        $relCheck = $_.FullName.Substring($appDir.Length).TrimStart("\")
        if ($relCheck -like "Data\*") { $skip = $true; $script:skippedData++ }
    }

    if ($skip) { return }

    $rel  = $_.FullName.Substring($appDir.Length).TrimStart("\")
    $dest = Join-Path $stageDir $rel
    $destParent = Split-Path -Parent $dest
    if (-not (Test-Path $destParent)) { New-Item -ItemType Directory -Path $destParent -Force | Out-Null }
    Copy-Item $_.FullName $dest -Force
    $script:copied++
}
Ok "скопировано файлов: $copied"
if ($skippedData -gt 0) { Warn "пропущено файлов справочников: $skippedData (чужие тексты — по правам в установщик не входят)" }
if ($skippedLibs -gt 0)  { Warn "пропущено неиспользуемых библиотек: $skippedLibs" }

# Уведомление о лицензии набора иконок (Lucide, ISC) обязано ехать вместе с
# программой — это условие лицензии, а не украшение.
$iconLicense = Join-Path $projectDir "Resources\ICONS-LICENSE.txt"
if (Test-Path $iconLicense) {
    Copy-Item $iconLicense (Join-Path $stageDir "ICONS-LICENSE.txt") -Force
    Ok "уведомление о лицензии иконок добавлено в сборку"
} else {
    Warn "не найден Resources\ICONS-LICENSE.txt — уведомление о лицензии иконок не попало в сборку"
}

# Проверка обязательных файлов (с рекурсивным поиском для папки runtimes/)
$mustHave = @("HMWT_MUI.exe", "e_sqlite3.dll", "Microsoft.Data.Sqlite.dll",
              "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.dynamic_cdecl.dll",
              "SQLitePCLRaw.batteries_v2.dll", "System.Text.Json.dll", "Newtonsoft.Json.dll")
foreach ($rel in $mustHave) {
    $found = Get-ChildItem -Path $stageDir -Filter $rel -Recurse -ErrorAction SilentlyContinue
    if (-not $found) { Die "в staging нет обязательного файла: $rel (проверяли рекурсивно)" }
}
Ok "обязательные файлы на месте"

# ── 4. Версия продукта в exe ──────────────────────────────────────────────
Step "Проверяю версию продукта в exe"
$exePath = (Get-ChildItem -Path $stageDir -Filter "HMWT_MUI.exe" -Recurse | Select-Object -First 1).FullName
$appExeVersion = (Get-Item $exePath).VersionInfo
foreach ($field in @("FileVersion", "ProductVersion", "ProductName", "FileDescription")) {
    Ok ("$field = '" + $appExeVersion.$field + "'")
}

if ($appExeVersion.FileVersion -ne $version) {
    Die "версия в exe ($($appExeVersion.FileVersion)) не совпадает с AssemblyInfo ($version)"
}
Ok "версия в exe совпадает: $version"

# ── 5. Компиляция установщика ─────────────────────────────────────────────
Step "Компилирую установщик (ISCC)"
$isccCandidates = @(
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { Die "ISCC.exe (Inno Setup) не найден." }
Ok "ISCC: $iscc"

if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Убраны лишние кавычки внутри /D, PowerShell сам корректно передаст пути с пробелами через &
$issArgs = @(
    "/Qp",
    "/DStagingDir=$stageDir",
    "/DOutputDir=$outputDir",
    "/DMyAppVersion=$version",
    $issFile
)
$issOut = & $iscc @issArgs 2>&1
$issOut | ForEach-Object { Write-Host ("  " + $_) }
if ($LASTEXITCODE -ne 0) { Die "ISCC завершился с кодом $LASTEXITCODE" }

$setup = Get-ChildItem -Path $outputDir -Filter "*.exe" | Select-Object -First 1
if (-not $setup) { Die "установщик не найден в $outputDir" }
Ok ("готово: " + $setup.FullName + " (" + [math]::Round($setup.Length / 1MB, 1) + " МБ)")

# ── 6. Копия на рабочий стол ──────────────────────────────────────────────
if (-not $NoDesktopCopy) {
    Step "Кладу установщик на рабочий стол"
    
    $desktop = [Environment]::GetFolderPath("Desktop")
    if ([string]::IsNullOrEmpty($desktop) -or -not (Test-Path $desktop)) {
        $desktop = Join-Path $env:USERPROFILE "Desktop"
    }
    if (-not (Test-Path $desktop)) {
        $desktop = $PWD.Path
        Warn "Не удалось найти рабочий стол. Копирую в текущую директорию: $desktop"
    }
    
    $target = Join-Path $desktop $setup.Name
    Copy-Item $setup.FullName $target -Force
    Ok ("на рабочем столе: " + $target)
}

Write-Host ""
Write-Host "ГОТОВО. Установщик: $($setup.FullName)" -ForegroundColor Green
if (-not $NoDesktopCopy) { Write-Host ("Копия на рабочем столе: " + (Join-Path $desktop $setup.Name)) -ForegroundColor Green }