# ═══════════════════════════════════════════════════════════════════════════
# AI Model Health Check & Performance Benchmark
# Tests all models across all API keys for availability, speed, and quality
# ═══════════════════════════════════════════════════════════════════════════

$BaseUrl = "https://api.yescale.vip/v1"

$Keys = @{
    "Default"  = "sk-jqDoJgfL2fBd98rwGRpwwq69TRjG8mLT4dhhozFqt5tNAol3"
    "gpt"      = "sk-rmnTAtUEZESA0L5ZM7HUktuBabGPhA90HBQjxOqdw6QRP8vw"
    "gpt_vip"  = "sk-EdsnP6g5zsK9eYAkayqDu4wMV3zlqB04gpS4EyAapgfBqtFp"
    "nornal"   = "sk-TRx6CC9f6QLymrsjXf4ptVTyscvNszggiz4VEsXVINvjz9Bs"
}

$Models = @(
    @{ Model = "gemini-2.5-flash-lite-nothinking";  Key = "Default" }
    @{ Model = "gemini-2.5-flash-lite-thinking";     Key = "Default" }
    @{ Model = "gemini-2.0-flash";                   Key = "Default" }
    @{ Model = "gemini-2.0-flash-lite";              Key = "Default" }
    @{ Model = "gpt-4.1-nano-2025-04-14";            Key = "gpt" }
    @{ Model = "gpt-4.1-mini-2025-04-14";            Key = "gpt" }
    @{ Model = "gpt-4o-mini";                        Key = "gpt" }
    @{ Model = "gpt-4o-mini-2024-07-18";             Key = "gpt" }
    @{ Model = "o4-mini-2025-04-16";                 Key = "gpt" }
    @{ Model = "chatgpt-4o-latest";                  Key = "gpt" }
    @{ Model = "gpt-5-nano";                         Key = "gpt_vip" }
    @{ Model = "glm-4.6";                            Key = "nornal" }
)

function Invoke-ChatCompletion {
    param(
        [string]$Model, [string]$ApiKey, [string]$System, [string]$User,
        [int]$MaxTokens = 100, [double]$Temp = 0.3, [int]$TimeoutSec = 30
    )
    $body = @{
        model       = $Model
        temperature = $Temp
        max_tokens  = $MaxTokens
        messages    = @(
            @{ role = "system"; content = $System }
            @{ role = "user";   content = $User }
        )
    } | ConvertTo-Json -Depth 5

    $headers = @{ "Authorization" = "Bearer $ApiKey"; "Content-Type" = "application/json" }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Uri "$BaseUrl/chat/completions" -Method Post -Headers $headers `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec $TimeoutSec
        $sw.Stop()
        $text = $resp.choices[0].message.content
        return @{ Ok = $true; Time = $sw.Elapsed.TotalSeconds; Chars = $text.Length; Text = $text; Error = $null }
    } catch {
        $sw.Stop()
        $msg = $_.Exception.Message
        if ($_.Exception.Response) {
            try { $msg = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch {}
        }
        return @{ Ok = $false; Time = $sw.Elapsed.TotalSeconds; Chars = 0; Text = $null; Error = $msg.Substring(0, [Math]::Min(120, $msg.Length)) }
    }
}

# ══════════════════════════════════════════════════════════════════════
# TEST 1: Health check — tiny prompt to each model
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n═══ TEST 1: MODEL HEALTH CHECK ═══" -ForegroundColor Cyan
Write-Host "Sending tiny prompt to each model (timeout 20s)...`n"

$healthResults = @()
foreach ($m in $Models) {
    $apiKey = $Keys[$m.Key]
    Write-Host -NoNewline "  $($m.Model.PadRight(40))"
    $r = Invoke-ChatCompletion -Model $m.Model -ApiKey $apiKey `
        -System "Reply with exactly: OK" -User "Health check" `
        -MaxTokens 10 -TimeoutSec 20
    if ($r.Ok) {
        Write-Host "OK  $([math]::Round($r.Time,1))s" -ForegroundColor Green
    } else {
        Write-Host "FAIL  $([math]::Round($r.Time,1))s  $($r.Error)" -ForegroundColor Red
    }
    $healthResults += @{ Model = $m.Model; Key = $m.Key; Ok = $r.Ok; Time = $r.Time; Error = $r.Error }
}

$healthy = $healthResults | Where-Object { $_.Ok }
$failed  = $healthResults | Where-Object { -not $_.Ok }
Write-Host "`n  Healthy: $($healthy.Count)/$($healthResults.Count)  |  Failed: $($failed.Count)" -ForegroundColor Yellow
if ($failed.Count -gt 0) {
    Write-Host "  Failed models: $( ($failed | ForEach-Object { $_.Model }) -join ', ' )" -ForegroundColor Red
}

# ══════════════════════════════════════════════════════════════════════
# TEST 2: AnalyzeScript — single chunk vs parallel chunks
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n═══ TEST 2: ANALYZESCRIPT CHUNK STRATEGIES ═══" -ForegroundColor Cyan

$scriptPath = "c:\Users\DUC CANH PC\Desktop\tooledit\Vì Sao Nhiều Người Trúng Vé Số Tiền Tỷ Vẫn Trắng Tay.txt"
if (-not (Test-Path $scriptPath)) {
    $scriptPath = Get-ChildItem "c:\Users\DUC CANH PC\Desktop\tooledit\*.txt" | Select-Object -First 1 -ExpandProperty FullName
}
$scriptContent = Get-Content $scriptPath -Raw -Encoding UTF8

$lines = ($scriptContent -split "`n") | Where-Object { $_.Trim() -ne "" }
$totalLines = $lines.Count
Write-Host "  Script: $totalLines lines`n"

$analyzeSystem = @"
You are a video editor AI. Analyze the podcast script. For each line, return a JSON array with objects containing:
- startTime (number): start time in seconds
- endTime (number): end time in seconds  
- text (string): the corrected text
- keywords (string[]): 2-3 search keywords for background image
Return ONLY the JSON array, no markdown fences.
"@

# Pick the best healthy model for testing (prefer fastest)
$testModel = ($healthy | Sort-Object Time | Select-Object -First 1).Model
if (-not $testModel) { $testModel = "gemini-2.5-flash-lite-nothinking" }
$testKey = ($Models | Where-Object { $_.Model -eq $testModel }).Key
$testApiKey = $Keys[$testKey]
Write-Host "  Using model: $testModel (fastest healthy)`n" -ForegroundColor Yellow

# Strategy A: Single chunk (current approach)
Write-Host "  [A] Single chunk ($totalLines lines)..." -NoNewline
$rA = Invoke-ChatCompletion -Model $testModel -ApiKey $testApiKey `
    -System $analyzeSystem -User "Segments: $totalLines. Return exactly $totalLines items.`n`n$scriptContent" `
    -MaxTokens 8192 -TimeoutSec 120
if ($rA.Ok) {
    # Count segments returned
    try { $countA = ([regex]::Matches($rA.Text, '"startTime"')).Count } catch { $countA = '?' }
    Write-Host "  OK  $([math]::Round($rA.Time,1))s  $countA segments $($rA.Chars) chars" -ForegroundColor Green
} else {
    Write-Host "  FAIL  $([math]::Round($rA.Time,1))s  $($rA.Error)" -ForegroundColor Red
}

# Strategy B: 2 parallel chunks
$half = [math]::Ceiling($totalLines / 2)
$chunk1 = ($lines[0..($half-1)] -join "`n")
$chunk2 = ($lines[$half..($totalLines-1)] -join "`n")

Write-Host "  [B] 2 parallel chunks $half + $($totalLines - $half) lines..." -NoNewline
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$job1 = Start-Job -ScriptBlock {
    param($BaseUrl,$Model,$ApiKey,$Sys,$Lines,$Count,$MaxTok)
    $body = @{
        model = $Model; temperature = 0.3; max_tokens = $MaxTok
        messages = @( @{role="system";content=$Sys}, @{role="user";content="Segments: $Count. Return exactly $Count items.`n`n$Lines"} )
    } | ConvertTo-Json -Depth 5
    $headers = @{ "Authorization" = "Bearer $ApiKey"; "Content-Type" = "application/json" }
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Invoke-RestMethod -Uri "$BaseUrl/chat/completions" -Method Post -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 90
        $t.Stop(); return @{ Ok=$true; Time=$t.Elapsed.TotalSeconds; Text=$r.choices[0].message.content }
    } catch { $t.Stop(); return @{ Ok=$false; Time=$t.Elapsed.TotalSeconds; Text=$_.Exception.Message } }
} -ArgumentList $BaseUrl,$testModel,$testApiKey,$analyzeSystem,$chunk1,$half,4096

$job2 = Start-Job -ScriptBlock {
    param($BaseUrl,$Model,$ApiKey,$Sys,$Lines,$Count,$MaxTok)
    $body = @{
        model = $Model; temperature = 0.3; max_tokens = $MaxTok
        messages = @( @{role="system";content=$Sys}, @{role="user";content="Segments: $Count. Return exactly $Count items.`n`n$Lines"} )
    } | ConvertTo-Json -Depth 5
    $headers = @{ "Authorization" = "Bearer $ApiKey"; "Content-Type" = "application/json" }
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Invoke-RestMethod -Uri "$BaseUrl/chat/completions" -Method Post -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 90
        $t.Stop(); return @{ Ok=$true; Time=$t.Elapsed.TotalSeconds; Text=$r.choices[0].message.content }
    } catch { $t.Stop(); return @{ Ok=$false; Time=$t.Elapsed.TotalSeconds; Text=$_.Exception.Message } }
} -ArgumentList $BaseUrl,$testModel,$testApiKey,$analyzeSystem,$chunk2,($totalLines - $half),4096

$r1 = Wait-Job $job1 | Receive-Job; Remove-Job $job1
$r2 = Wait-Job $job2 | Receive-Job; Remove-Job $job2
$sw.Stop()

if ($r1.Ok -and $r2.Ok) {
    try { $c1 = ([regex]::Matches($r1.Text, '"startTime"')).Count; $c2 = ([regex]::Matches($r2.Text, '"startTime"')).Count } catch { $c1='?'; $c2='?' }
    Write-Host "  OK  $([math]::Round($sw.Elapsed.TotalSeconds,1))s  chunk1=$c1 segs/$([math]::Round($r1.Time,1))s chunk2=$c2 segs/$([math]::Round($r2.Time,1))s" -ForegroundColor Green
} else {
    Write-Host "  PARTIAL  $([math]::Round($sw.Elapsed.TotalSeconds,1))s  c1=$($r1.Ok)/$([math]::Round($r1.Time,1))s c2=$($r2.Ok)/$([math]::Round($r2.Time,1))s" -ForegroundColor Yellow
}

# Strategy C: 3 parallel chunks
$third = [math]::Ceiling($totalLines / 3)
$c3a = ($lines[0..($third-1)] -join "`n")
$c3b = ($lines[$third..(2*$third-1)] -join "`n")
$c3c = ($lines[(2*$third)..($totalLines-1)] -join "`n")
$counts = @($third, $third, ($totalLines - 2*$third))

Write-Host "  [C] 3 parallel chunks $($counts -join ' + ') lines..." -NoNewline
$sw3 = [System.Diagnostics.Stopwatch]::StartNew()
$chunks3 = @($c3a, $c3b, $c3c)
$jobs3 = @()
for ($i = 0; $i -lt 3; $i++) {
    $jobs3 += Start-Job -ScriptBlock {
        param($BaseUrl,$Model,$ApiKey,$Sys,$Lines,$Count,$MaxTok)
        $body = @{
            model = $Model; temperature = 0.3; max_tokens = $MaxTok
            messages = @( @{role="system";content=$Sys}, @{role="user";content="Segments: $Count. Return exactly $Count items.`n`n$Lines"} )
        } | ConvertTo-Json -Depth 5
        $headers = @{ "Authorization" = "Bearer $ApiKey"; "Content-Type" = "application/json" }
        $t = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $r = Invoke-RestMethod -Uri "$BaseUrl/chat/completions" -Method Post -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 90
            $t.Stop(); return @{ Ok=$true; Time=$t.Elapsed.TotalSeconds; Text=$r.choices[0].message.content }
        } catch { $t.Stop(); return @{ Ok=$false; Time=$t.Elapsed.TotalSeconds; Text=$_.Exception.Message } }
    } -ArgumentList $BaseUrl,$testModel,$testApiKey,$analyzeSystem,$chunks3[$i],$counts[$i],3072
}
$results3 = $jobs3 | ForEach-Object { Wait-Job $_ | Receive-Job; Remove-Job $_ }
$sw3.Stop()

$allOk3 = ($results3 | Where-Object { $_.Ok }).Count
$maxT3 = ($results3 | Measure-Object -Property Time -Maximum).Maximum
Write-Host "  $allOk3/3 OK  wall=$([math]::Round($sw3.Elapsed.TotalSeconds,1))s  max=$([math]::Round($maxT3,1))s  per-chunk: $(($results3 | ForEach-Object { "$([math]::Round($_.Time,1))s" }) -join ', ')" -ForegroundColor $(if ($allOk3 -eq 3) { "Green" } else { "Yellow" })

# ══════════════════════════════════════════════════════════════════════
# TEST 3: Multi-model speed comparison for AnalyzeScript (small sample)
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n═══ TEST 3: MODEL SPEED - ANALYZE `(30 lines sample`) ═══" -ForegroundColor Cyan

$sampleLines = ($lines[0..29] -join "`n")
$sampleCount = [math]::Min(30, $totalLines)

foreach ($h in ($healthy | Sort-Object Time)) {
    $m = $h.Model
    $apiKey = $Keys[($Models | Where-Object { $_.Model -eq $m }).Key]
    Write-Host -NoNewline "  $($m.PadRight(40))"
    $r = Invoke-ChatCompletion -Model $m -ApiKey $apiKey `
        -System $analyzeSystem -User "Segments: $sampleCount. Return exactly $sampleCount items.`n`n$sampleLines" `
        -MaxTokens 4096 -TimeoutSec 60
    if ($r.Ok) {
        try { $cnt = ([regex]::Matches($r.Text, '"startTime"')).Count } catch { $cnt = "?" }
        Write-Host "OK  $([math]::Round($r.Time,1))s  $cnt segs $($r.Chars)ch" -ForegroundColor Green
    } else {
        Write-Host "FAIL  $([math]::Round($r.Time,1))s" -ForegroundColor Red
    }
}

# ══════════════════════════════════════════════════════════════════════
# TEST 4: Warmup / health-check-first strategy
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n═══ TEST 4: WARMUP STRATEGY ═══" -ForegroundColor Cyan
Write-Host "  Testing: tiny health-check → if OK → real request (avoids cold timeout)`n"

# Without warmup
Write-Host '  [No warmup] Cold start analyze (30 lines)...' -NoNewline
$rCold = Invoke-ChatCompletion -Model $testModel -ApiKey $testApiKey `
    -System $analyzeSystem -User "Segments: $sampleCount. Return exactly $sampleCount items.`n`n$sampleLines" `
    -MaxTokens 4096 -TimeoutSec 60
Write-Host "  $([math]::Round($rCold.Time,1))s  ok=$($rCold.Ok)" -ForegroundColor $(if($rCold.Ok){"Green"}else{"Red"})

# With warmup
Write-Host "  [With warmup] Warmup ping..." -NoNewline
$rWarm = Invoke-ChatCompletion -Model $testModel -ApiKey $testApiKey `
    -System "Reply OK" -User "ping" -MaxTokens 5 -TimeoutSec 10
Write-Host "  $([math]::Round($rWarm.Time,1))s" -ForegroundColor $(if($rWarm.Ok){"Green"}else{"Red"})
Write-Host '  [With warmup] Hot analyze (30 lines)...' -NoNewline
$rHot = Invoke-ChatCompletion -Model $testModel -ApiKey $testApiKey `
    -System $analyzeSystem -User "Segments: $sampleCount. Return exactly $sampleCount items.`n`n$sampleLines" `
    -MaxTokens 4096 -TimeoutSec 60
Write-Host "  $([math]::Round($rHot.Time,1))s  ok=$($rHot.Ok)" -ForegroundColor $(if($rHot.Ok){"Green"}else{"Red"})

# ══════════════════════════════════════════════════════════════════════
# SUMMARY
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n═══ SUMMARY ═══" -ForegroundColor Cyan
Write-Host "  Healthy models:" -ForegroundColor Green
$healthy | Sort-Object Time | ForEach-Object { Write-Host "    $($_.Model.PadRight(40)) $([math]::Round($_.Time,1))s  [$($_.Key)]" }
if ($failed.Count -gt 0) {
    Write-Host "  Failed models (should remove from fallback):" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "    $($_.Model.PadRight(40)) [$($_.Key)]  $($_.Error)" }
}
Write-Host ""
