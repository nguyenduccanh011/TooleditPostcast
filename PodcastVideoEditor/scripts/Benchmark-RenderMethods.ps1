param(
    [string]$SampleDir = "samples/render-benchmark-2min",
    [string]$OutDir = "samples/render-benchmark-2min/bench-results",
    [int]$Width = 720,
    [int]$Height = 1280,
    [int]$Fps = 30,
    [switch]$Sweep,
    [switch]$SweepOnly,
    [string[]]$SweepPresets = @("veryfast", "faster", "fast"),
    [int[]]$SweepCrfValues = @(23, 21, 19),
    [int[]]$SweepFpsValues = @(24, 30),
    [string[]]$SweepMethods = @("m2", "m4"),
    [switch]$Force
)

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
    }
}

function Get-SweepMethodFilter {
    param(
        [string]$Method,
        [int]$Fps,
        [int]$Width,
        [int]$Height,
        [int]$VizHeight,
        [string]$PixelFormat
    )

    if ($Method -eq "m2") {
        return "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$PixelFormat[v]"
    }

    if ($Method -eq "m4") {
        return "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=rgba[vbase];[1:a]asplit=2[aout][aviz];[aviz]showwaves=s=${Width}x${VizHeight}:mode=line:colors=White|DodgerBlue,format=rgba,colorchannelmixer=aa=0.75[viz];[vbase][viz]overlay=0:H-h-24:format=auto[v]"
    }

    throw "Unsupported sweep method '$Method'. Supported: m2, m4"
}

function Get-SweetSpotScore {
    param(
        [double]$RealtimeFactor,
        [int]$Crf,
        [int]$Fps
    )

    # Heuristic score: speed weighted higher, with quality proxy from CRF and FPS.
    $qualityIndex = ((51.0 - $Crf) / 28.0) * ($Fps / 30.0)
    return [Math]::Round(($RealtimeFactor * 0.7) + ($qualityIndex * 0.3), 3)
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
$nvencAvailable = Test-EncoderAvailable -Ffmpeg $ffmpeg -EncoderName "h264_nvenc"
$nvencLabel = if ($nvencAvailable) { "Yes" } else { "No" }

$results = New-Object System.Collections.Generic.List[object]

if ($SweepOnly) {
    $Sweep = $true
}

$normalizedSweepMethods = @()
foreach ($methodArg in $SweepMethods) {
    foreach ($part in ($methodArg -split ",")) {
        $trimmed = $part.Trim().ToLowerInvariant()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
            $normalizedSweepMethods += $trimmed
        }
    }
}

if ($normalizedSweepMethods.Count -eq 0) {
    $normalizedSweepMethods = @("m2", "m4")
}

$m1Out = Join-Path $outRoot "m1_static_segments.mp4"
$m1Filter = "[0:v]fps=$Fps,scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$pixFmt[v]"
$m1Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$m1Filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m1Out)
$results.Add((Invoke-RenderMethod -Name "m1_static_segments" -Ffmpeg $ffmpeg -Arguments $m1Args -OutputPath $m1Out -TargetDurationSeconds $durationSec)) | Out-Null

$m2Out = Join-Path $outRoot "m2_zoompan_motion.mp4"
$m2Filter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$pixFmt[v]"
$m2Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$m2Filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m2Out)
$results.Add((Invoke-RenderMethod -Name "m2_zoompan_motion" -Ffmpeg $ffmpeg -Arguments $m2Args -OutputPath $m2Out -TargetDurationSeconds $durationSec)) | Out-Null

$m3Out = Join-Path $outRoot "m3_zoompan_supersample.mp4"
$w2 = [int]($Width * 2)
$h2 = [int]($Height * 2)
$m3Filter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${w2}:${h2}:flags=lanczos,scale=${Width}:${Height}:flags=lanczos+accurate_rnd+full_chroma_int,format=$pixFmt[v]"
$m3Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$m3Filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m3Out)
$results.Add((Invoke-RenderMethod -Name "m3_zoompan_supersample" -Ffmpeg $ffmpeg -Arguments $m3Args -OutputPath $m3Out -TargetDurationSeconds $durationSec)) | Out-Null

$m4Out = Join-Path $outRoot "m4_zoompan_visualizer.mp4"
$m4Filter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=rgba[vbase];[1:a]asplit=2[aout][aviz];[aviz]showwaves=s=${Width}x${vizHeight}:mode=line:colors=White|DodgerBlue,format=rgba,colorchannelmixer=aa=0.75[viz];[vbase][viz]overlay=0:H-h-24:format=auto[v]"
$m4Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$m4Filter,"-map","[v]","-map","[aout]","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m4Out)
$results.Add((Invoke-RenderMethod -Name "m4_zoompan_visualizer" -Ffmpeg $ffmpeg -Arguments $m4Args -OutputPath $m4Out -TargetDurationSeconds $durationSec)) | Out-Null

if ($nvencAvailable) {
    $m6Out = Join-Path $outRoot "m6_zoompan_visualizer_nvenc.mp4"
    $m6Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$m4Filter,"-map","[v]","-map","[aout]","-c:v","h264_nvenc","-preset","p4","-cq","23","-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m6Out)
    $results.Add((Invoke-RenderMethod -Name "m6_zoompan_visualizer_nvenc" -Ffmpeg $ffmpeg -Arguments $m6Args -OutputPath $m6Out -TargetDurationSeconds $durationSec)) | Out-Null
}

$chunkDir = Join-Path $outRoot "chunks"
New-Item -ItemType Directory -Path $chunkDir | Out-Null
$chunkSeconds = 30
$chunkCount = [int][Math]::Ceiling($durationSec / $chunkSeconds)
$chunkTotal = 0.0
$chunkListLines = New-Object System.Collections.Generic.List[string]

for ($i = 0; $i -lt $chunkCount; $i++) {
    $start = $i * $chunkSeconds
    $chunkOut = Join-Path $chunkDir (("chunk_{0:D2}.mp4" -f $i))
    $chunkArgs = @("-y","-ss","$start","-t","$chunkSeconds","-f","concat","-safe","0","-i",$concatFile,"-ss","$start","-t","$chunkSeconds","-i",$audio,"-filter_complex",$m4Filter,"-map","[v]","-map","[aout]","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$chunkOut)
    $chunkResult = Invoke-RenderMethod -Name (("m5_chunk_{0:D2}" -f $i)) -Ffmpeg $ffmpeg -Arguments $chunkArgs -OutputPath $chunkOut -TargetDurationSeconds $chunkSeconds
    $chunkTotal += $chunkResult.ElapsedSeconds
    $chunkListLines.Add("file '$($chunkOut.Replace("'", "''"))'")
}

$chunkList = Join-Path $chunkDir "chunks.txt"
Set-Content -Path $chunkList -Value $chunkListLines -Encoding ASCII

$m5Out = Join-Path $outRoot "m5_chunked_visualizer.mp4"
$swConcat = [System.Diagnostics.Stopwatch]::StartNew()
& $ffmpeg -y -fflags +genpts -f concat -safe 0 -i $chunkList -c:v copy -c:a aac -b:a 192k -af "aresample=async=1:first_pts=0" -avoid_negative_ts make_zero $m5Out | Out-Null
$concatCode = $LASTEXITCODE
$swConcat.Stop()
if ($concatCode -ne 0) {
    throw "Chunk concat failed with exit code $concatCode"
}

$m5Elapsed = [Math]::Round($chunkTotal + $swConcat.Elapsed.TotalSeconds, 3)
$results.Add([PSCustomObject]@{ Method = "m5_chunked_visualizer"; Output = $m5Out; ElapsedSeconds = $m5Elapsed; RealtimeFactor = [Math]::Round($durationSec / [Math]::Max($m5Elapsed, 0.001), 3) }) | Out-Null

$final = $results | Sort-Object ElapsedSeconds
$csv = Join-Path $outRoot "benchmark-results.csv"
$final | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8

$md = Join-Path $outRoot "benchmark-results.md"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Render Benchmark Results")
$lines.Add("")
$lines.Add("- Resolution: ${Width}x${Height}")
$lines.Add("- FPS: $Fps")
$lines.Add("- Target duration: $([Math]::Round($durationSec,2))s")
$lines.Add("- NVENC available: $nvencLabel")
$lines.Add("- Winner: $($final[0].Method) ($($final[0].ElapsedSeconds)s, x$($final[0].RealtimeFactor))")
$lines.Add("")
$lines.Add("| Method | Elapsed (s) | Realtime Factor (x) | Output |")
$lines.Add("|---|---:|---:|---|")
foreach ($r in $final) {
    $lines.Add("| $($r.Method) | $($r.ElapsedSeconds) | $($r.RealtimeFactor) | $([IO.Path]::GetFileName($r.Output)) |")
}
Set-Content -Path $md -Value $lines -Encoding UTF8

if ($Sweep) {
    $sweepResults = New-Object System.Collections.Generic.List[object]

    foreach ($method in $normalizedSweepMethods) {
        foreach ($presetItem in $SweepPresets) {
            foreach ($crfItem in $SweepCrfValues) {
                foreach ($fpsItem in $SweepFpsValues) {
                    $methodLower = $method.ToLowerInvariant()
                    $name = ("sweep_{0}_p-{1}_crf-{2}_fps-{3}" -f $methodLower, $presetItem, $crfItem, $fpsItem)
                    $output = Join-Path $outRoot ($name + ".mp4")

                    $filter = Get-SweepMethodFilter -Method $methodLower -Fps $fpsItem -Width $Width -Height $Height -VizHeight $vizHeight -PixelFormat $pixFmt
                    if ($methodLower -eq "m4") {
                        $args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$filter,"-map","[v]","-map","[aout]","-c:v","libx264","-preset",$presetItem,"-crf","$crfItem","-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$output)
                    }
                    else {
                        $args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$filter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$presetItem,"-crf","$crfItem","-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$output)
                    }

                    $run = Invoke-RenderMethod -Name $name -Ffmpeg $ffmpeg -Arguments $args -OutputPath $output -TargetDurationSeconds $durationSec
                    $score = Get-SweetSpotScore -RealtimeFactor $run.RealtimeFactor -Crf $crfItem -Fps $fpsItem
                    $sweepResults.Add([PSCustomObject]@{
                        Method = $methodLower
                        Preset = $presetItem
                        Crf = $crfItem
                        Fps = $fpsItem
                        ElapsedSeconds = $run.ElapsedSeconds
                        RealtimeFactor = $run.RealtimeFactor
                        SweetSpotScore = $score
                        Output = $output
                    }) | Out-Null
                }
            }
        }
    }

    $sweepFinal = $sweepResults | Sort-Object -Property @{ Expression = 'SweetSpotScore'; Descending = $true }, @{ Expression = 'ElapsedSeconds'; Descending = $false }
    $sweepCsv = Join-Path $outRoot "benchmark-sweep-results.csv"
    $sweepFinal | Export-Csv -Path $sweepCsv -NoTypeInformation -Encoding UTF8

    $sweepMd = Join-Path $outRoot "benchmark-sweep-results.md"
    $sweepLines = New-Object System.Collections.Generic.List[string]
    $sweepLines.Add("# Render Benchmark Sweep Results")
    $sweepLines.Add("")
    $sweepLines.Add("- Resolution: ${Width}x${Height}")
    $sweepLines.Add("- Target duration: $([Math]::Round($durationSec,2))s")
    $sweepLines.Add("- Swept methods: $($normalizedSweepMethods -join ', ')")
    $sweepLines.Add("- Swept presets: $($SweepPresets -join ', ')")
    $sweepLines.Add("- Swept CRF: $($SweepCrfValues -join ', ')")
    $sweepLines.Add("- Swept FPS: $($SweepFpsValues -join ', ')")
    $sweepLines.Add("- Best sweep profile: $($sweepFinal[0].Method) | preset=$($sweepFinal[0].Preset) | crf=$($sweepFinal[0].Crf) | fps=$($sweepFinal[0].Fps) | score=$($sweepFinal[0].SweetSpotScore)")
    $sweepLines.Add("")
    $sweepLines.Add("| Method | Preset | CRF | FPS | Elapsed (s) | Realtime (x) | Sweet Spot Score | Output |")
    $sweepLines.Add("|---|---|---:|---:|---:|---:|---:|---|")
    foreach ($r in $sweepFinal) {
        $sweepLines.Add("| $($r.Method) | $($r.Preset) | $($r.Crf) | $($r.Fps) | $($r.ElapsedSeconds) | $($r.RealtimeFactor) | $($r.SweetSpotScore) | $([IO.Path]::GetFileName($r.Output)) |")
    }
    Set-Content -Path $sweepMd -Value $sweepLines -Encoding UTF8
}

Write-Host "Benchmark complete"
Write-Host "CSV: $csv"
Write-Host "MD : $md"
$final | Format-Table -AutoSize

if ($Sweep) {
    Write-Host "Sweep complete"
    Write-Host "CSV: $(Join-Path $outRoot 'benchmark-sweep-results.csv')"
    Write-Host "MD : $(Join-Path $outRoot 'benchmark-sweep-results.md')"
}
