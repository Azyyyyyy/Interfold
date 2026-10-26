# Measures one published Interfold.Api.Host process (JIT or AOT).
# Records: time to "Now listening on", time to first /health/ready 200,
# working-set/private bytes, QueryProcessCycleTime, and request latencies.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][int]$Port,
    [Parameter(Mandatory = $true)][string]$Label,
    [Parameter(Mandatory = $true)][string]$OutJson
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProcessCycles {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryProcessCycleTime(IntPtr handle, out ulong cycleTime);
}
'@

function Get-Cycles([System.Diagnostics.Process]$p) {
    $p.Refresh()
    $cycles = [UInt64]0
    if (-not [ProcessCycles]::QueryProcessCycleTime($p.Handle, [ref]$cycles)) {
        throw "QueryProcessCycleTime failed: $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    return $cycles
}

function Get-Mem([System.Diagnostics.Process]$p) {
    $p.Refresh()
    [pscustomobject]@{
        WorkingSetMb = [math]::Round($p.WorkingSet64 / 1MB, 2)
        PrivateMb    = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
    }
}

function New-Es256Jwt([string]$pem, [string]$authority, [string]$sub) {
    $header = '{"alg":"ES256","typ":"JWT"}'
    $now = [DateTimeOffset]::UtcNow
    $payloadObj = [ordered]@{
        iss   = $authority
        sub   = $sub
        iat   = $now.ToUnixTimeSeconds()
        nbf   = $now.ToUnixTimeSeconds()
        exp   = $now.AddHours(2).ToUnixTimeSeconds()
        jti   = [guid]::NewGuid().ToString('N')
        scope = 'octocon:deeplink'
    }
    $payload = $payloadObj | ConvertTo-Json -Compress
    function B64Url([byte[]]$bytes) {
        [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    $h = B64Url([Text.Encoding]::UTF8.GetBytes($header))
    $b = B64Url([Text.Encoding]::UTF8.GetBytes($payload))
    $input = "$h.$b"
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    $ecdsa.ImportFromPem($pem)
    $sig = $ecdsa.SignData(
        [Text.Encoding]::UTF8.GetBytes($input),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $ecdsa.Dispose()
    return "$input.$(B64Url $sig)"
}

function Measure-Latencies([string]$url, [hashtable]$headers, [int]$warmup, [int]$n) {
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    foreach ($k in $headers.Keys) { $client.DefaultRequestHeaders.TryAddWithoutValidation($k, $headers[$k]) | Out-Null }
    for ($i = 0; $i -lt $warmup; $i++) {
        $r = $client.GetAsync($url).GetAwaiter().GetResult()
        $null = $r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $r.Dispose()
    }
    $samples = [System.Collections.Generic.List[double]]::new()
    $codes = @{}
    $sw = [System.Diagnostics.Stopwatch]::new()
    for ($i = 0; $i -lt $n; $i++) {
        $sw.Restart()
        $r = $client.GetAsync($url).GetAwaiter().GetResult()
        $null = $r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $sw.Stop()
        $samples.Add($sw.Elapsed.TotalMilliseconds)
        $key = [int]$r.StatusCode
        if (-not $codes.ContainsKey($key)) { $codes[$key] = 0 }
        $codes[$key]++
        $r.Dispose()
    }
    $client.Dispose()
    $arr = $samples.ToArray()
    [Array]::Sort($arr)
    $p = { param($pct) $arr[[Math]::Min($arr.Length - 1, [int][Math]::Floor(($arr.Length - 1) * $pct))] }
    [pscustomobject]@{
        Url      = $url
        N        = $n
        Statuses = ($codes.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ','
        MinMs    = [math]::Round($arr[0], 3)
        P50Ms    = [math]::Round((& $p 0.50), 3)
        P95Ms    = [math]::Round((& $p 0.95), 3)
        P99Ms    = [math]::Round((& $p 0.99), 3)
        MaxMs    = [math]::Round($arr[-1], 3)
        MeanMs   = [math]::Round(($arr | Measure-Object -Average).Average, 3)
    }
}

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$rsaPem = $rsa.ExportPkcs8PrivateKeyPem()
$rsa.Dispose()
$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
$esPem = $ecdsa.ExportECPrivateKeyPem()
$ecdsa.Dispose()

function Set-Env([string]$name, [string]$value) {
    [System.Environment]::SetEnvironmentVariable($name, $value, 'Process')
}
Set-Env 'OCTOCON_PERSISTENCE' 'inmemory'
Set-Env 'OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER' 'TEST'
Set-Env 'OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM' $esPem
Set-Env 'OCTOCON_INMEMORY_SECRETS_SEED__AUTH_DEEP_LINK_SECRET' 'test_deep_link_secret_0123456789abcdefghijklmnopqrstuv'
Set-Env 'OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_RSA256_PRIVATE_PEM' $rsaPem
Set-Env 'OCTOCON_JWT_AUTHORITY' 'octocon-local'
Set-Env 'OCTOCON_JWT_AUDIENCE' 'octocon'
Set-Env 'ASPNETCORE_ENVIRONMENT' 'Development'
Set-Env 'ASPNETCORE_URLS' "http://127.0.0.1:$Port"

$outDir = [IO.Path]::GetDirectoryName($OutJson)
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$stdoutPath = Join-Path $outDir "$Label.stdout.log"
$stderrPath = Join-Path $outDir "$Label.stderr.log"
if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }
if (Test-Path $stderrPath) { Remove-Item $stderrPath -Force }

$startUtc = [DateTimeOffset]::UtcNow
$proc = Start-Process -FilePath $Exe -WorkingDirectory ([IO.Path]::GetDirectoryName($Exe)) `
    -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath
if (-not $proc) { throw "Failed to start $Exe" }

function Read-LogText([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
}

function Find-Listening([string]$text) {
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match 'Now listening on') { return $line }
    }
    return $null
}

$readyAt = $null
$readyMs = $null
$listeningAt = $null
$listeningLine = $null
$deadline = $startUtc.AddSeconds(90)
$readyUrl = "http://127.0.0.1:$Port/health/ready"
$readyClient = [System.Net.Http.HttpClient]::new()
$readyClient.Timeout = [TimeSpan]::FromSeconds(2)

while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if ($proc.HasExited) {
        Start-Sleep -Milliseconds 200
        throw "$Label exited $($proc.ExitCode) before ready.`n$(Read-LogText $stdoutPath)`n$(Read-LogText $stderrPath)"
    }
    if ($null -eq $listeningLine) {
        $listeningLine = Find-Listening (Read-LogText $stdoutPath)
        if ($listeningLine) { $listeningAt = [DateTimeOffset]::UtcNow }
    }
    try {
        $resp = $readyClient.GetAsync($readyUrl).GetAwaiter().GetResult()
        if ([int]$resp.StatusCode -eq 200) {
            $readyAt = [DateTimeOffset]::UtcNow
            $readyMs = ($readyAt - $startUtc).TotalMilliseconds
            $resp.Dispose()
            break
        }
        $resp.Dispose()
    } catch {
        # connection refused until listen
    }
    Start-Sleep -Milliseconds 20
}
$readyClient.Dispose()

if ($null -eq $readyAt) {
    try { $proc.Kill() } catch { }
    throw "$Label never reached /health/ready 200.`n$(Read-LogText $stdoutPath)`n$(Read-LogText $stderrPath)"
}

if ($null -eq $listeningLine) {
    $listeningLine = Find-Listening (Read-LogText $stdoutPath)
    if ($listeningLine -and $null -eq $listeningAt) { $listeningAt = $readyAt }
}

$listenMs = $null
if ($null -ne $listeningAt) {
    $listenMs = ($listeningAt - $startUtc).TotalMilliseconds
}

$memReady = Get-Mem $proc
$cyclesReady = Get-Cycles $proc

$authority = 'octocon-local'
$sub = 'nam:perfbench0001'
$token = New-Es256Jwt $esPem $authority $sub
$authHeaders = @{ Authorization = "Bearer $token" }

$warmup = 30
$n = 200
$latReady = Measure-Latencies $readyUrl @{} $warmup $n
$latLive = Measure-Latencies "http://127.0.0.1:$Port/health/live" @{} $warmup $n
$latAlters = Measure-Latencies "http://127.0.0.1:$Port/api/systems/me/alters" $authHeaders $warmup $n

$memAfter = Get-Mem $proc
$cyclesAfter = Get-Cycles $proc
$cpuMs = $proc.TotalProcessorTime.TotalMilliseconds

$result = [ordered]@{
    Label                     = $Label
    Exe                       = $Exe
    Port                      = $Port
    ProcessId                 = $proc.Id
    ListeningLine             = $listeningLine
    StartUtc                  = $startUtc.ToString('o')
    ListeningUtc              = if ($listeningAt) { $listeningAt.ToString('o') } else { $null }
    ReadyUtc                  = $readyAt.ToString('o')
    MsToListening             = if ($null -ne $listenMs) { [math]::Round($listenMs, 1) } else { $null }
    MsToReady200              = [math]::Round($readyMs, 1)
    MemoryReadyWorkingSetMb   = $memReady.WorkingSetMb
    MemoryReadyPrivateMb      = $memReady.PrivateMb
    MemoryAfterWorkingSetMb   = $memAfter.WorkingSetMb
    MemoryAfterPrivateMb      = $memAfter.PrivateMb
    CpuCyclesAtReady          = $cyclesReady
    CpuCyclesAfterLoad        = $cyclesAfter
    CpuCyclesDuringLoad       = ($cyclesAfter - $cyclesReady)
    ProcessCpuTimeMs          = [math]::Round($cpuMs, 1)
    ReadyLatency              = $latReady
    LiveLatency               = $latLive
    AltersLatency             = $latAlters
}

$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutJson -Encoding utf8

try { $proc.Kill() } catch { }
$proc.Dispose()

Write-Host ($result | ConvertTo-Json -Depth 6)
