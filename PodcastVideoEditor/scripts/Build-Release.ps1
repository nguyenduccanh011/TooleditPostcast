param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot,
    [string]$FfmpegBinDir = $env:FFMPEG_BIN_DIR,
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

function Get-AssemblyVersion([string]$semanticVersion) {
    $parts = $semanticVersion.Split('.')
    while ($parts.Count -lt 4) {
        $parts += "0"
    }

    return ($parts[0..3] -join '.')
}

function Invoke-Step([string]$filePath, [string[]]$arguments, [string]$workingDirectory) {
    Write-Host ">> $filePath $($arguments -join ' ')"
    Push-Location $workingDirectory
    try {
        & $filePath @arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $filePath $($arguments -join ' ')"
    }
}

function Resolve-FFmpegDirectory([string]$repoRoot, [string]$requestedDirectory) {
    $candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($requestedDirectory)) {
        $candidates += $requestedDirectory
    }

    $candidates += (Join-Path $repoRoot "third_party\ffmpeg\bin")

    # Common Chocolatey layouts (path structure varies across package versions)
    $chocoBase = "C:\ProgramData\chocolatey"
    $candidates += (Join-Path $chocoBase "lib\ffmpeg\tools\ffmpeg\bin")
    $candidates += (Join-Path $chocoBase "lib\ffmpeg\tools")
    $candidates += (Join-Path $chocoBase "bin")

    # Recursively scan Chocolatey ffmpeg package directory
    $chocoFfmpegRoot = Join-Path $chocoBase "lib\ffmpeg\tools"
    if (Test-Path $chocoFfmpegRoot) {
        Get-ChildItem -Path $chocoFfmpegRoot -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $candidates += $_.DirectoryName }
    }

    $ffmpegCommand = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($ffmpegCommand) {
        $candidates += (Split-Path -Parent $ffmpegCommand.Source)
    }

    foreach ($candidate in $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
        $ffmpegPath = Join-Path $candidate "ffmpeg.exe"
        $ffprobePath = Join-Path $candidate "ffprobe.exe"
        if ((Test-Path $ffmpegPath) -and (Test-Path $ffprobePath)) {
            return (Resolve-Path $candidate).Path
        }
    }

    Write-Host "Searched the following candidate directories:"
    $candidates | ForEach-Object { Write-Host "  - $_" }
    throw "Could not resolve an FFmpeg bundle directory. Provide -FfmpegBinDir or place ffmpeg.exe and ffprobe.exe under third_party\ffmpeg\bin."
}

function Resolve-IsccPath() {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $commonPaths = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $commonPaths) {
        if (Test-Path $path) {
            return $path
        }
    }

    throw "Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 or pass -SkipInstaller."
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path
$solutionPath = Join-Path $repoRoot "src\PodcastVideoEditor.sln"
$uiProjectPath = Join-Path $repoRoot "src\PodcastVideoEditor.Ui\PodcastVideoEditor.Ui.csproj"
$installerScriptPath = Join-Path $repoRoot "installer\PodcastVideoEditor.iss"
$noticesPath = Join-Path $repoRoot "THIRD_PARTY_NOTICES.md"
$installGuidePath = Join-Path $repoRoot "docs\INSTALL-UPDATE-GUIDE.md"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $repoRoot "src\Directory.Build.props")
    $Version = $props.Project.PropertyGroup.Version
}

$assemblyVersion = Get-AssemblyVersion $Version
$outputRootPath = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "artifacts\release"
} else {
    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        $OutputRoot
    } else {
        Join-Path $repoRoot $OutputRoot
    }
}

$publishDir = Join-Path $outputRootPath "publish\$Runtime"
$packageDir = Join-Path $outputRootPath "packages"
$portableZipPath = Join-Path $packageDir "PodcastVideoEditor-$Runtime-v$Version.zip"
$checksumPath = Join-Path $packageDir "SHA256SUMS.txt"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

Invoke-Step "dotnet" @("build", $solutionPath, "-c", $Configuration) $repoRoot

if (-not $SkipTests) {
    Invoke-Step "dotnet" @("test", (Join-Path $repoRoot "src\PodcastVideoEditor.Core.Tests\PodcastVideoEditor.Core.Tests.csproj"), "-c", $Configuration) $repoRoot
    Invoke-Step "dotnet" @("test", (Join-Path $repoRoot "src\PodcastVideoEditor.Ui.Tests\PodcastVideoEditor.Ui.Tests.csproj"), "-c", $Configuration) $repoRoot
}

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Invoke-Step "dotnet" @(
    "publish",
    $uiProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $publishDir,
    "/p:Version=$Version",
    "/p:AssemblyVersion=$assemblyVersion",
    "/p:FileVersion=$assemblyVersion",
    "/p:InformationalVersion=$Version"
) $repoRoot

$resolvedFfmpegDir = Resolve-FFmpegDirectory -repoRoot $repoRoot -requestedDirectory $FfmpegBinDir
Write-Host "Using FFmpeg bundle from $resolvedFfmpegDir"
$ffmpegOutputDir = Join-Path $publishDir "tools\ffmpeg"
New-Item -ItemType Directory -Force -Path $ffmpegOutputDir | Out-Null
Copy-Item (Join-Path $resolvedFfmpegDir "ffmpeg.exe") $ffmpegOutputDir -Force
Copy-Item (Join-Path $resolvedFfmpegDir "ffprobe.exe") $ffmpegOutputDir -Force

Copy-Item $noticesPath (Join-Path $publishDir "THIRD_PARTY_NOTICES.md") -Force

if (Test-Path $installGuidePath) {
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDir "docs") | Out-Null
    Copy-Item $installGuidePath (Join-Path $publishDir "docs\INSTALL-UPDATE-GUIDE.md") -Force
} else {
    Write-Host "Warning: INSTALL-UPDATE-GUIDE.md not found at $installGuidePath, skipping..."
}

if (Test-Path $portableZipPath) {
    Remove-Item -LiteralPath $portableZipPath -Force
}
Write-Host "Creating portable archive $portableZipPath"
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $portableZipPath -Force

$generatedFiles = [System.Collections.Generic.List[string]]::new()
$generatedFiles.Add($portableZipPath)

if (-not $SkipInstaller) {
    $isccPath = Resolve-IsccPath
    Write-Host "Building installer with $isccPath"
    Invoke-Step $isccPath @(
        "/Qp",
        "/DAppVersion=$Version",
        "/DPublishDir=$publishDir",
        "/DOutputDir=$packageDir",
        "/DOutputBaseFilename=PodcastVideoEditor-Setup-v$Version",
        $installerScriptPath
    ) $repoRoot

    $generatedFiles.Add((Join-Path $packageDir "PodcastVideoEditor-Setup-v$Version.exe"))
}

$hashLines = foreach ($file in $generatedFiles) {
    $hash = Get-FileHash -Path $file -Algorithm SHA256
    "{0} *{1}" -f $hash.Hash, (Split-Path -Leaf $file)
}
$hashLines | Set-Content -Path $checksumPath -Encoding ASCII
Write-Host "Wrote checksums to $checksumPath"

Write-Host ""
Write-Host "Release artifacts are ready:"
Get-ChildItem $packageDir | ForEach-Object { Write-Host " - $($_.FullName)" }

Write-Host ""
Write-Host "Pushing tag v$Version to GitHub..."
try {
    Push-Location $repoRoot
    & git tag "v$Version" 2>&1 | Out-Null
    & git push origin "v$Version" 2>&1 | Out-Null
    Write-Host "[OK] Tag v$Version pushed to GitHub successfully"
}
catch {
    Write-Host "[Warning] Could not push tag to GitHub: $_"
}
finally {
    Pop-Location
}
