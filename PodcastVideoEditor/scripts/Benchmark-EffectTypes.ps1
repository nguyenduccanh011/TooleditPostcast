param(
    [string]$SampleDir = "samples/render-benchmark-2min",
    [string]$OutDir = "samples/render-benchmark-2min/bench-effect-types",
    [int]$Width = 1080,
    [int]$Height = 1920,
    [int]$Fps = 30,
    [switch]$Force
)

<#
.SYNOPSIS
Benchmark individual effect types (zoom, pan, visualize, image segments) in isolation.
Tests: m1 (no motion), m2 (zoom+pan), m2_zoom (zoom only), m2_pan (pan only), m4 (visualizer).

.PARAMETER SampleDir
    Directory containing sample assets (audio_main_120s.wav, bg_main_1080x1920.png, bg_alt_1080x1920.png)

.PARAMETER OutDir
    Output directory for benchmark results

.PARAMETER Width, Height, Fps
    Render resolution and frame rate

.PARAMETER Force
    Remove existing output directory if present
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-Tool([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($null -eq $cmd) {
        throw "$name not found in PATH."
    }
    return $cmd.Source
}

function Test-EncoderAvailable {
    param(
        [string]$Ffmpeg,
        [string]$EncoderName
    )

    $encoders = & $Ffmpeg -hide_banner -encoders 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return ($encoders -match ("\\b{0}\\b" -f [Regex]::Escape($EncoderName)))
}

function New-ConcatSegmentsFile {
    param(
        [string]$Path,
        [string[]]$Images,
        [int]$DurationSeconds
    )

    $lines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $DurationSeconds; $i++) {
        $img = $Images[$i % $Images.Length].Replace("'", "''")
        $lines.Add("file '$img'")
        $lines.Add("duration 1")
    }

    $last = $Images[($DurationSeconds - 1) % $Images.Length].Replace("'", "''")
    $lines.Add("file '$last'")

    Set-Content -Path $Path -Value $lines -Encoding ASCII
}

function Invoke-RenderMethod {
    param(
        [string]$Name,
        [string]$Ffmpeg,
        [string[]]$Arguments,
        [string]$OutputPath,
        [double]$TargetDurationSeconds
    )

    if (Test-Path $OutputPath) {
        Remove-Item -Force $OutputPath
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Ffmpeg @Arguments | Out-Null
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    if ($exitCode -ne 0) {
        throw "Render '$Name' failed with exit code $exitCode"
    }

    if (-not (Test-Path $OutputPath)) {
        throw "Render '$Name' produced no file: $OutputPath"
    }

    $elapsed = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
    $rt = [Math]::Round($TargetDurationSeconds / [Math]::Max($elapsed, 0.001), 3)

    return [PSCustomObject]@{
        Method = $Name
        Output = $OutputPath
        ElapsedSeconds = $elapsed
        RealtimeFactor = $rt
        TargetDuration = $TargetDurationSeconds
    }
}

$ffmpeg = Resolve-Tool "ffmpeg"
$ffprobe = Resolve-Tool "ffprobe"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sampleRoot = Join-Path $repoRoot $SampleDir

$audio = Join-Path $sampleRoot "audio_main_120s.wav"
$imgA = Join-Path $sampleRoot "bg_main_1080x1920.png"
$imgB = Join-Path $sampleRoot "bg_alt_1080x1920.png"

foreach ($required in @($audio, $imgA, $imgB)) {
    if (-not (Test-Path $required)) {
        throw "Missing required file: $required"
    }
}

$outRoot = Join-Path $repoRoot $OutDir
if (Test-Path $outRoot) {
    if (-not $Force) {
        throw "Output exists: $outRoot. Use -Force."
    }
    Remove-Item -Recurse -Force $outRoot
}
New-Item -ItemType Directory -Path $outRoot | Out-Null

$concatFile = Join-Path $outRoot "segments_120s.txt"
New-ConcatSegmentsFile -Path $concatFile -Images @($imgA, $imgB) -DurationSeconds 120

$durationRaw = & $ffprobe -v error -show_entries format=duration -of default=nk=1:nw=1 $audio
$durationSec = [double]::Parse($durationRaw, [Globalization.CultureInfo]::InvariantCulture)

$preset = "veryfast"
$crf = "23"
$pixFmt = "yuv420p"
$vizHeight = [Math]::Max(120, [int]($Height * 0.18))

Write-Host "=== Effect Type Benchmark ===" -ForegroundColor Cyan
Write-Host "Resolution: ${Width}x${Height}, FPS: ${Fps}, Duration: $([Math]::Round($durationSec, 2))s"
Write-Host ""

$results = New-Object System.Collections.Generic.List[object]

# Effect 1: STATIC (No motion) - Baseline
Write-Host "[1/5] Static (no motion)..." -ForegroundColor Yellow
$m1Out = Join-Path $outRoot "e1_static_baseline.mp4"
$m1Filter = "[0:v]fps=$Fps,scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$pixFmt[v]"
$m1Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$m1Filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m1Out)
$results.Add((Invoke-RenderMethod -Name "e1_static" -Ffmpeg $ffmpeg -Arguments $m1Args -OutputPath $m1Out -TargetDurationSeconds $durationSec)) | Out-Null

# Effect 2: ZOOM ONLY (No pan, no visualizer)
Write-Host "[2/5] Zoom only..." -ForegroundColor Yellow
$e2Out = Join-Path $outRoot "e2_zoom_only.mp4"
$e2Filter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*0.5':y='(in_h-out_h)*(0.5+0.5*sin(2*PI*t/9))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$pixFmt[v]"
$e2Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$e2Filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$e2Out)
$results.Add((Invoke-RenderMethod -Name "e2_zoom_only" -Ffmpeg $ffmpeg -Arguments $e2Args -OutputPath $e2Out -TargetDurationSeconds $durationSec)) | Out-Null

# Effect 3: PAN ONLY (No zoom, no visualizer)
Write-Host "[3/5] Pan only..." -ForegroundColor Yellow
$e3Out = Join-Path $outRoot "e3_pan_only.mp4"
$e3Filter = "[0:v]fps=$Fps,scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height}:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',format=$pixFmt[v]"
$e3Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$e3Filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$e3Out)
$results.Add((Invoke-RenderMethod -Name "e3_pan_only" -Ffmpeg $ffmpeg -Arguments $e3Args -OutputPath $e3Out -TargetDurationSeconds $durationSec)) | Out-Null

# Effect 4: ZOOM + PAN (Both motion, no visualizer)
Write-Host "[4/5] Zoom + Pan..." -ForegroundColor Yellow
$e4Out = Join-Path $outRoot "e4_zoom_pan.mp4"
$e4Filter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$pixFmt[v]"
$e4Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$e4Filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$e4Out)
$results.Add((Invoke-RenderMethod -Name "e4_zoom_pan" -Ffmpeg $ffmpeg -Arguments $e4Args -OutputPath $e4Out -TargetDurationSeconds $durationSec)) | Out-Null

# Effect 5: ZOOM + PAN + VISUALIZER (Full feature)
Write-Host "[5/5] Zoom + Pan + Visualizer..." -ForegroundColor Yellow
$e5Out = Join-Path $outRoot "e5_zoom_pan_visualizer.mp4"
$e5Filter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=rgba[vbase];[1:a]asplit=2[aout][aviz];[aviz]showwaves=s=${Width}x${vizHeight}:mode=line:colors=White|DodgerBlue,format=rgba,colorchannelmixer=aa=0.75[viz];[vbase][viz]overlay=0:H-h-24:format=auto[v]"
$e5Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$e5Filter,"-map","[v]","-map","[aout]","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$e5Out)
$results.Add((Invoke-RenderMethod -Name "e5_zoom_pan_visualizer" -Ffmpeg $ffmpeg -Arguments $e5Args -OutputPath $e5Out -TargetDurationSeconds $durationSec)) | Out-Null

$final = $results | Sort-Object ElapsedSeconds

$csv = Join-Path $outRoot "effect-types-results.csv"
$final | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8

Write-Host ""
Write-Host "=== RESULTS (sorted by speed) ===" -ForegroundColor Cyan
$final | Format-Table -Property Method, @{Name="Elapsed (s)"; Expression={$_.ElapsedSeconds}}, @{Name="Realtime (x)"; Expression={$_.RealtimeFactor}} -AutoSize

# Calculate overhead per effect type
Write-Host ""
Write-Host "=== OVERHEAD ANALYSIS ===" -ForegroundColor Cyan
$baseline = ($final | Where-Object Method -eq "e1_static").ElapsedSeconds
foreach ($r in $final) {
    if ($r.Method -ne "e1_static") {
        $overhead = [Math]::Round($r.ElapsedSeconds - $baseline, 3)
        $overheadPct = [Math]::Round(($overhead / $baseline) * 100, 1)
        Write-Host "$($r.Method): +${overhead}s (+${overheadPct}%)"
    }
}

# Summary markdown
$md = Join-Path $outRoot "effect-types-results.md"
$mdLines = New-Object System.Collections.Generic.List[string]
$mdLines.Add("# Effect Type Benchmark Results")
$mdLines.Add("")
$mdLines.Add("## Configuration")
$mdLines.Add("- Resolution: ${Width}x${Height}")
$mdLines.Add("- FPS: ${Fps}")
$mdLines.Add("- Total Duration: $([Math]::Round($durationSec, 2))s")
$mdLines.Add("- Preset: $preset, CRF: $crf")
$mdLines.Add("")
$mdLines.Add("## Results (Fastest to Slowest)")
$mdLines.Add("")
$mdLines.Add("| Effect Type | Method | Elapsed (s) | Realtime (x) | Overhead vs Baseline |")
$mdLines.Add("|---|---|---:|---:|---|")

$baseline = ($final | Where-Object Method -eq "e1_static").ElapsedSeconds
foreach ($r in $final) {
    if ($r.Method -eq "e1_static") {
        $overhead = "baseline"
    }
    else {
        $ohVal = [Math]::Round($r.ElapsedSeconds - $baseline, 3)
        $ohPct = [Math]::Round(($ohVal / $baseline) * 100, 1)
        $overhead = "+${ohVal}s (+${ohPct}%)"
    }
    $mdLines.Add("| $($r.Method) | $([IO.Path]::GetFileName($r.Output)) | $($r.ElapsedSeconds) | $($r.RealtimeFactor) | $overhead |")
}

$mdLines.Add("")
$mdLines.Add("## Key Findings")
$mdLines.Add("")
$slowest = $final[-1]
$fastest = $final[0]
$overhead = [Math]::Round($slowest.ElapsedSeconds - $baseline, 3)
$mdLines.Add("- **Fastest effect**: $($fastest.Method) ($($fastest.ElapsedSeconds)s)")
$mdLines.Add("- **Slowest effect**: $($slowest.Method) ($($slowest.ElapsedSeconds)s)")
$mdLines.Add("- **Total overhead (slowest vs baseline)**: +${overhead}s")
$mdLines.Add("")
$mdLines.Add("## Per-Effect Overhead Breakdown")
foreach ($r in $final) {
    if ($r.Method -ne "e1_static") {
        $overhead = [Math]::Round($r.ElapsedSeconds - $baseline, 3)
        $overheadPct = [Math]::Round(($overhead / $baseline) * 100, 1)
        $mdLines.Add("- $($r.Method): **+${overhead}s** (+${overheadPct}%)")
    }
}

Set-Content -Path $md -Value $mdLines -Encoding UTF8

Write-Host ""
Write-Host "[COMPLETE] Benchmark complete. Results saved to: $outRoot" -ForegroundColor Green
Write-Host "  CSV: $(Split-Path -Leaf $csv)"
Write-Host "  MD:  $(Split-Path -Leaf $md)"
