param(
    [string]$SampleDir = "samples/render-benchmark-2min",
    [string]$OutDir = "samples/render-benchmark-2min/bench-results-components-1080",
    [int]$Width = 1080,
    [int]$Height = 1920,
    [int]$Fps = 30,
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

function Invoke-Render {
    param(
        [string]$Name,
        [string]$Ffmpeg,
        [string[]]$Arguments,
        [string]$OutputPath,
        [double]$TargetDurationSeconds,
        [string]$Component
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

    $elapsed = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
    $rt = [Math]::Round($TargetDurationSeconds / [Math]::Max($elapsed, 0.001), 3)

    return [PSCustomObject]@{
        Method = $Name
        Component = $Component
        ElapsedSeconds = $elapsed
        RealtimeFactor = $rt
        Output = $OutputPath
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
$results = New-Object System.Collections.Generic.List[object]

$staticFilter = "[0:v]fps=$Fps,scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$pixFmt[v]"
$zoomPanFilter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=$pixFmt[v]"
$w2 = [int]($Width * 2)
$h2 = [int]($Height * 2)
$superFilter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${w2}:${h2}:flags=lanczos,scale=${Width}:${Height}:flags=lanczos+accurate_rnd+full_chroma_int,format=$pixFmt[v]"
$vizOnlyFilter = "[0:v]fps=$Fps,scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=rgba[vbase];[1:a]asplit=2[aout][aviz];[aviz]showwaves=s=${Width}x${vizHeight}:mode=line:colors=White|DodgerBlue,format=rgba,colorchannelmixer=aa=0.75[viz];[vbase][viz]overlay=0:H-h-24:format=auto[v]"
$zoomPanVizFilter = "[0:v]fps=$Fps,scale=iw*1.15:ih*1.15,crop=iw/1.15:ih/1.15:x='(in_w-out_w)*(0.5+0.5*sin(2*PI*t/9))':y='(in_h-out_h)*(0.5+0.5*cos(2*PI*t/11))',scale=${Width}:${Height}:force_original_aspect_ratio=increase,crop=${Width}:${Height},format=rgba[vbase];[1:a]asplit=2[aout][aviz];[aviz]showwaves=s=${Width}x${vizHeight}:mode=line:colors=White|DodgerBlue,format=rgba,colorchannelmixer=aa=0.75[viz];[vbase][viz]overlay=0:H-h-24:format=auto[v]"

$m1Out = Join-Path $outRoot "c1_static_segments.mp4"
$m1Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$staticFilter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m1Out)
$results.Add((Invoke-Render -Name "c1_static_segments" -Component "image_segments" -Ffmpeg $ffmpeg -Arguments $m1Args -OutputPath $m1Out -TargetDurationSeconds $durationSec)) | Out-Null

$m2Out = Join-Path $outRoot "c2_zoompan_motion.mp4"
$m2Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$zoomPanFilter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m2Out)
$results.Add((Invoke-Render -Name "c2_zoompan_motion" -Component "zoom_pan" -Ffmpeg $ffmpeg -Arguments $m2Args -OutputPath $m2Out -TargetDurationSeconds $durationSec)) | Out-Null

$m3Out = Join-Path $outRoot "c3_zoompan_supersample.mp4"
$m3Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$superFilter,"-map","[v]","-map","1:a","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m3Out)
$results.Add((Invoke-Render -Name "c3_zoompan_supersample" -Component "zoom_pan_supersample" -Ffmpeg $ffmpeg -Arguments $m3Args -OutputPath $m3Out -TargetDurationSeconds $durationSec)) | Out-Null

$m4Out = Join-Path $outRoot "c4_visualizer_only.mp4"
$m4Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$vizOnlyFilter,"-map","[v]","-map","[aout]","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m4Out)
$results.Add((Invoke-Render -Name "c4_visualizer_only" -Component "audio_visualizer" -Ffmpeg $ffmpeg -Arguments $m4Args -OutputPath $m4Out -TargetDurationSeconds $durationSec)) | Out-Null

$m5Out = Join-Path $outRoot "c5_zoompan_visualizer.mp4"
$m5Args = @("-y","-f","concat","-safe","0","-i",$concatFile,"-i",$audio,"-filter_complex",$zoomPanVizFilter,"-map","[v]","-map","[aout]","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$m5Out)
$results.Add((Invoke-Render -Name "c5_zoompan_visualizer" -Component "zoom_pan + audio_visualizer" -Ffmpeg $ffmpeg -Arguments $m5Args -OutputPath $m5Out -TargetDurationSeconds $durationSec)) | Out-Null

$chunkDir = Join-Path $outRoot "chunks"
New-Item -ItemType Directory -Path $chunkDir | Out-Null
$chunkSeconds = 30
$chunkCount = [int][Math]::Ceiling($durationSec / $chunkSeconds)
$chunkTotal = 0.0
$chunkListLines = New-Object System.Collections.Generic.List[string]

for ($i = 0; $i -lt $chunkCount; $i++) {
    $start = $i * $chunkSeconds
    $chunkOut = Join-Path $chunkDir (("chunk_{0:D2}.mp4" -f $i))
    $chunkArgs = @("-y","-ss","$start","-t","$chunkSeconds","-f","concat","-safe","0","-i",$concatFile,"-ss","$start","-t","$chunkSeconds","-i",$audio,"-filter_complex",$zoomPanVizFilter,"-map","[v]","-map","[aout]","-c:v","libx264","-preset",$preset,"-crf",$crf,"-pix_fmt",$pixFmt,"-c:a","aac","-b:a","192k","-shortest",$chunkOut)
    $chunkResult = Invoke-Render -Name (("chunk_{0:D2}" -f $i)) -Component "chunk_part" -Ffmpeg $ffmpeg -Arguments $chunkArgs -OutputPath $chunkOut -TargetDurationSeconds $chunkSeconds
    $chunkTotal += $chunkResult.ElapsedSeconds
    $chunkListLines.Add("file '$($chunkOut.Replace("'", "''"))'")
}

$chunkList = Join-Path $chunkDir "chunks.txt"
Set-Content -Path $chunkList -Value $chunkListLines -Encoding ASCII

$chunkedOut = Join-Path $outRoot "c6_chunked_zoompan_visualizer.mp4"
$swConcat = [System.Diagnostics.Stopwatch]::StartNew()
& $ffmpeg -y -fflags +genpts -f concat -safe 0 -i $chunkList -c:v copy -c:a aac -b:a 192k -af "aresample=async=1:first_pts=0" -avoid_negative_ts make_zero $chunkedOut | Out-Null
$concatCode = $LASTEXITCODE
$swConcat.Stop()
if ($concatCode -ne 0) {
    throw "Chunk concat failed with exit code $concatCode"
}

$chunkedElapsed = [Math]::Round($chunkTotal + $swConcat.Elapsed.TotalSeconds, 3)
$results.Add([PSCustomObject]@{
    Method = "c6_chunked_zoompan_visualizer"
    Component = "chunking_overhead + zoom_pan + audio_visualizer"
    ElapsedSeconds = $chunkedElapsed
    RealtimeFactor = [Math]::Round($durationSec / [Math]::Max($chunkedElapsed, 0.001), 3)
    Output = $chunkedOut
}) | Out-Null

$final = $results | Sort-Object ElapsedSeconds
$staticBaseline = ($final | Where-Object { $_.Method -eq "c1_static_segments" } | Select-Object -First 1)

$enriched = foreach ($row in $final) {
    $delta = [Math]::Round($row.ElapsedSeconds - $staticBaseline.ElapsedSeconds, 3)
    $ratio = [Math]::Round($row.ElapsedSeconds / [Math]::Max($staticBaseline.ElapsedSeconds, 0.001), 3)
    [PSCustomObject]@{
        Method = $row.Method
        Component = $row.Component
        ElapsedSeconds = $row.ElapsedSeconds
        RealtimeFactor = $row.RealtimeFactor
        DeltaVsStaticSeconds = $delta
        SlowdownVsStatic = $ratio
        Output = $row.Output
    }
}

$csv = Join-Path $outRoot "benchmark-components-results.csv"
$enriched | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8

$md = Join-Path $outRoot "benchmark-components-results.md"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Render Component Benchmark Results")
$lines.Add("")
$lines.Add("- Resolution: ${Width}x${Height}")
$lines.Add("- FPS: $Fps")
$lines.Add("- Duration: $([Math]::Round($durationSec, 2))s")
$lines.Add("- Baseline: c1_static_segments = $($staticBaseline.ElapsedSeconds)s")
$lines.Add("")
$lines.Add("| Method | Component | Elapsed (s) | Realtime (x) | Delta vs static (s) | Slowdown vs static (x) | Output |")
$lines.Add("|---|---|---:|---:|---:|---:|---|")
foreach ($r in $enriched | Sort-Object ElapsedSeconds) {
    $lines.Add("| $($r.Method) | $($r.Component) | $($r.ElapsedSeconds) | $($r.RealtimeFactor) | $($r.DeltaVsStaticSeconds) | $($r.SlowdownVsStatic) | $([IO.Path]::GetFileName($r.Output)) |")
}
Set-Content -Path $md -Value $lines -Encoding UTF8

Write-Host "Component benchmark complete"
Write-Host "CSV: $csv"
Write-Host "MD : $md"
$enriched | Sort-Object ElapsedSeconds | Format-Table -AutoSize
