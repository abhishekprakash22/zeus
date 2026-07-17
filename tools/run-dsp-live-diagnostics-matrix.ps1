param(
    [string]$BaseUrl = "http://localhost:6060",

    [string[]]$ScenarioIds = @(),

    [string]$ComparisonId = "current-zeus",

    [string]$Label = "",

    [int]$Samples = 15,

    [int]$IntervalMs = 1000,

    [int]$TimeoutSec = 5,

    [string]$BundleDir = "",

    [string]$OutputRoot = "",

    [string]$ReportPath = "",

    [string]$IndexPath = "",

    [string]$WatchScriptPath = "",

    [switch]$PlanOnly,

    [switch]$JsonOnly,

    [switch]$SkipCertificateCheck,

    [switch]$ContinueOnError
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
}

function Enable-ModernTls {
    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    }
    catch {
        # PowerShell 7+ uses platform defaults; older Windows PowerShell needs TLS 1.2 explicitly.
    }
}

function Enable-CertificateBypass {
    Enable-ModernTls

    if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey("SkipCertificateCheck")) {
        return
    }

    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = [System.Net.Security.RemoteCertificateValidationCallback]{
        param($sender, $certificate, $chain, $sslPolicyErrors)
        return $true
    }
}

function ConvertTo-SafeName {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $safe = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9._-]+", "-"
    $safe = $safe.Trim("-")
    if ($safe.Length -gt 64) {
        $safe = $safe.Substring(0, 64).Trim("-")
    }
    return $safe
}

function ConvertTo-ComparisonId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "candidate-under-test"
    }

    $normalized = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-"
    $normalized = $normalized.Trim("-")

    switch ($normalized) {
        "off" { return "off-baseline" }
        "baseline" { return "off-baseline" }
        "off-baseline" { return "off-baseline" }
        "thetis" { return "thetis-parity" }
        "thetis-parity" { return "thetis-parity" }
        "current" { return "current-zeus" }
        "current-zeus" { return "current-zeus" }
        "zeus-current" { return "current-zeus" }
        "zeus" { return "current-zeus" }
        "candidate" { return "candidate-under-test" }
        "candidate" { return "candidate-under-test" }
        "candidate-under-test" { return "candidate-under-test" }
        "candidate" { return "candidate-under-test" }
        "candidate-under-test" { return "candidate-under-test" }
        "external" { return "candidate-external-engine-opt-in" }
        "external-engine" { return "candidate-external-engine-opt-in" }
        "candidate-external-engine-opt-in" { return "candidate-external-engine-opt-in" }
        default { return $normalized }
    }
}

function Read-JsonText {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Source
    )

    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse JSON from '$Source': $($_.Exception.Message)"
    }
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Read-JsonText -Text (Get-Content -LiteralPath $Path -Raw) -Source $Path
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 64
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

function Get-JsonValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }
        foreach ($key in @($Object.Keys)) {
            if ([string]::Equals([string]$key, $Name, [StringComparison]::OrdinalIgnoreCase)) {
                return $Object[$key]
            }
        }
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    foreach ($candidate in @($Object.PSObject.Properties)) {
        if ([string]::Equals($candidate.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $candidate.Value
        }
    }

    return $null
}

function Get-JsonArray {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $value = Get-JsonValue $Object $Name
    if ($null -eq $value) {
        return @()
    }

    if ($value -is [System.Array]) {
        return @($value)
    }

    return @($value)
}

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return $Value
    }

    return [bool]$Value
}

function Get-IntValue {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    if ($Value -is [byte] -or $Value -is [int] -or $Value -is [long]) {
        return [int]$Value
    }

    $parsed = 0
    if ([int]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return 0
}

function Get-NullableDoubleValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [float] -or $Value -is [double] -or $Value -is [decimal]) {
        return [double]$Value
    }

    $parsed = 0.0
    if ([double]::TryParse([string]$Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

function Get-MixedWeakStrongHuntScore {
    param(
        [int]$WeakInputSampleCount,
        [int]$StrongInputSampleCount,
        $WeakStrongOutputGapDb,
        [bool]$ReadyForBenchmarkTrace,
        [bool]$WeakStrongOutputParityReady,
        [int]$ExpectedSampleCount
    )

    $denominator = [Math]::Max(1, $ExpectedSampleCount)
    $weakCoverage = [Math]::Min(1.0, [double]$WeakInputSampleCount / [double]$denominator)
    $strongCoverage = [Math]::Min(1.0, [double]$StrongInputSampleCount / [double]$denominator)
    $score = ($weakCoverage * 35.0) + ($strongCoverage * 35.0)

    if ($null -ne $WeakStrongOutputGapDb) {
        $gapScore = [Math]::Max(0.0, 20.0 - ([Math]::Min(12.0, [Math]::Abs([double]$WeakStrongOutputGapDb)) / 12.0 * 20.0))
        $score += $gapScore
    }

    if ($WeakStrongOutputParityReady) {
        $score += 5.0
    }

    if ($ReadyForBenchmarkTrace) {
        $score += 5.0
    }

    return [Math]::Round([Math]::Min(100.0, $score), 3)
}

function Add-Count {
    param(
        [hashtable]$Map,
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return
    }

    if (-not $Map.ContainsKey($Name)) {
        $Map[$Name] = 0
    }

    $Map[$Name] = [int]$Map[$Name] + 1
}

function ConvertTo-CountArray {
    param([hashtable]$Map)

    $items = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($Map.Keys | Sort-Object)) {
        $items.Add([ordered]@{
            name = [string]$key
            count = [int]$Map[$key]
        }) | Out-Null
    }

    return @($items.ToArray() | Sort-Object `
        @{ Expression = { [int](Get-JsonValue $_ "count") }; Descending = $true },
        @{ Expression = { [string](Get-JsonValue $_ "name") }; Ascending = $true })
}

function Test-StrictComparisonStateId {
    param([string]$ComparisonId)

    $id = ([string]$ComparisonId).Trim().ToLowerInvariant()
    return ($id -eq "candidate-under-test" -or $id -eq "off-baseline")
}

function Resolve-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd("\", "/")
    $fullPath = (Resolve-Path -LiteralPath $Path).Path
    if ($fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($rootPath.Length).TrimStart("\", "/") -replace "\\", "/"
    }

    return $fullPath -replace "\\", "/"
}

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not [string]::IsNullOrWhiteSpace($Root) -and (Test-Path -LiteralPath $Path)) {
        return Resolve-RelativePath -Root $Root -Path $Path
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path -replace "\\", "/"
    }

    return $Path -replace "\\", "/"
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    $stream = [System.IO.File]::OpenRead($resolvedPath)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hash) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-JsonGet {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][int]$RequestTimeoutSec,
        [switch]$SkipCertificateCheck
    )

    try {
        $webRequestCommand = Get-Command Invoke-WebRequest
        $requestArgs = @{
            Uri = $Uri
            TimeoutSec = $RequestTimeoutSec
        }
        if ($webRequestCommand.Parameters.ContainsKey("UseBasicParsing")) {
            $requestArgs["UseBasicParsing"] = $true
        }
        if ($SkipCertificateCheck -and $webRequestCommand.Parameters.ContainsKey("SkipCertificateCheck")) {
            $requestArgs["SkipCertificateCheck"] = $true
        }

        $response = Invoke-WebRequest @requestArgs
        if ($response.StatusCode -lt 200 -or $response.StatusCode -gt 299) {
            throw "HTTP $($response.StatusCode)"
        }

        return Read-JsonText -Text ([string]$response.Content) -Source $Uri
    }
    catch {
        throw "GET $Uri failed: $($_.Exception.Message)"
    }
}

function Get-ManifestScenarioIds {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][int]$RequestTimeoutSec,
        [switch]$SkipCertificateCheck
    )

    $manifest = Invoke-JsonGet -Uri "$Base/api/dsp/benchmark-capture-manifest" -RequestTimeoutSec $RequestTimeoutSec -SkipCertificateCheck:$SkipCertificateCheck
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($scenarioId in (Get-JsonArray $manifest "scenarioIds")) {
        $value = [string]$scenarioId
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $ids.Add($value) | Out-Null
        }
    }

    return @($ids.ToArray() | Select-Object -Unique)
}

function Expand-ScenarioIds {
    param([string[]]$Values)

    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($Values)) {
        foreach ($part in ([string]$item -split "[,;]")) {
            $value = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $ids.Add($value) | Out-Null
            }
        }
    }

    return @($ids.ToArray())
}

function Invoke-Watch {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][string]$Comparison,
        [string]$RunLabel,
        [Parameter(Mandatory = $true)][int]$SampleCount,
        [Parameter(Mandatory = $true)][int]$DelayMs,
        [Parameter(Mandatory = $true)][int]$RequestTimeoutSec,
        [Parameter(Mandatory = $true)][string]$JsonlPath,
        [Parameter(Mandatory = $true)][string]$SummaryPath,
        [switch]$SkipCertificateCheck
    )

    $labelParts = @($ScenarioId, $Comparison, $RunLabel) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    $watchLabel = ($labelParts -join "-")

    $watchArgs = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $ScriptPath,
        "-BaseUrl", $Base,
        "-Samples", ([string]$SampleCount),
        "-IntervalMs", ([string]$DelayMs),
        "-TimeoutSec", ([string]$RequestTimeoutSec),
        "-Label", $watchLabel,
        "-ScenarioId", $ScenarioId,
        "-ComparisonId", $Comparison,
        "-ReportPath", $SummaryPath,
        "-JsonlPath", $JsonlPath,
        "-JsonOnly"
    )
    if ($SkipCertificateCheck) {
        $watchArgs += "-SkipCertificateCheck"
    }

    $output = & powershell @watchArgs 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        return [ordered]@{
            ok = $false
            exitCode = $exitCode
            error = ($output | Out-String).Trim()
            report = $null
        }
    }

    $text = ($output | Out-String).Trim()
    $report = if ([string]::IsNullOrWhiteSpace($text)) { Read-JsonFile $SummaryPath } else { Read-JsonText -Text $text -Source $SummaryPath }

    return [ordered]@{
        ok = $true
        exitCode = 0
        error = $null
        report = $report
    }
}

if ($Samples -lt 1) {
    throw "Samples must be at least 1."
}
if ($IntervalMs -lt 0) {
    throw "IntervalMs must be greater than or equal to 0."
}
if ($TimeoutSec -lt 1) {
    throw "TimeoutSec must be at least 1."
}

$repoRoot = Get-RepoRoot
$base = Normalize-BaseUrl $BaseUrl
if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}
$comparison = ConvertTo-ComparisonId $ComparisonId
$comparisons = @($comparison)
$safeComparison = ConvertTo-SafeName $comparison
$safeLabel = ConvertTo-SafeName $Label

if ([string]::IsNullOrWhiteSpace($WatchScriptPath)) {
    $WatchScriptPath = Join-Path $repoRoot "tools\watch-dsp-live-diagnostics.ps1"
}
$resolvedWatchScript = (Resolve-Path -LiteralPath $WatchScriptPath).Path

if ($PlanOnly) {
    $acceptanceCommandSteps = @(
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId off-baseline -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.off-baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.off-baseline.json -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId thetis-parity -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.thetis-parity.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.thetis-parity.json -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId current-zeus -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.baseline.json -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId candidate-under-test -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.candidate.json -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-history.json',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -BaselineIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -CandidateIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -BaselineComparisonId current-zeus -CandidateComparisonId candidate-under-test -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-comparison.json -FailOnRegression',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -BaselineIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.thetis-parity.json -CandidateIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -BaselineComparisonId thetis-parity -CandidateComparisonId candidate-under-test -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-comparison.thetis-parity.json -FailOnRegression',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir captures\dsp-modernization\<timestamp> -AcceptanceManifest -RequireLiveAcceptanceArtifacts -Force',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir captures\dsp-modernization\<timestamp> -RequireArtifactFiles -ReportPath captures\dsp-modernization\<timestamp>\validation-report.json',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ReportPath captures\dsp-modernization\<timestamp>\validation-triage-report.json -MarkdownPath captures\dsp-modernization\<timestamp>\validation-triage-report.md -FailOnIssues'
    )
    $acceptanceExpectedArtifacts = @(
        'artifacts/live-diagnostics-trace-index.off-baseline.json',
        'artifacts/live-diagnostics-matrix-report.off-baseline.json',
        'artifacts/live-diagnostics-trace-index.thetis-parity.json',
        'artifacts/live-diagnostics-matrix-report.thetis-parity.json',
        'artifacts/live-diagnostics-trace-index.baseline.json',
        'artifacts/live-diagnostics-matrix-report.baseline.json',
        'artifacts/live-diagnostics-trace-index.candidate.json',
        'artifacts/live-diagnostics-matrix-report.candidate.json',
        'artifacts/live-diagnostics-history.json',
        'artifacts/live-diagnostics-trace-comparison.json',
        'artifacts/live-diagnostics-trace-comparison.thetis-parity.json',
        'artifact-manifest.json',
        'validation-report.json',
        'validation-triage-report.json',
        'validation-triage-report.md'
    )
    [ordered]@{
        schemaVersion = 3
        tool = "run-dsp-live-diagnostics-matrix"
        mode = "plan-only"
        baseUrl = $base
        endpoint = "$base/api/dsp/live-diagnostics"
        captureManifestEndpoint = "$base/api/dsp/benchmark-capture-manifest"
        scenarios = if ($ScenarioIds.Count -gt 0) { @($ScenarioIds) } else { @("from /api/dsp/benchmark-capture-manifest") }
        comparisonId = $comparison
        comparisonIds = @($comparisons)
        samples = $Samples
        intervalMs = $IntervalMs
        skipCertificateCheck = [bool]$SkipCertificateCheck
        outputs = @(
            "JSONL trace per scenario",
            "watch-dsp-live-diagnostics summary per scenario",
            "bundle-compatible trace index JSON",
            "matrix summary JSON with capture-readiness preflight rollups",
            "Candidate mixed weak/strong hunt scoring and best-window recommendation fields",
            "Candidate speech-artifact advisory rollups from low-evidence lift, audio alignment, and texture fill"
        )
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId current-zeus -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.baseline.json -Samples 60 -IntervalMs 1000"
        candidateExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId candidate-under-test -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.candidate.json -Samples 60 -IntervalMs 1000"
        desktopExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId current-zeus -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.baseline.json -Samples 60 -IntervalMs 500"
        acceptanceCommandStepCount = $acceptanceCommandSteps.Count
        acceptanceCommandSteps = @($acceptanceCommandSteps)
        acceptanceExpectedArtifacts = @($acceptanceExpectedArtifacts)
        notes = @(
            "Read-only: delegates to watch-dsp-live-diagnostics.ps1, which only calls GET /api/dsp/live-diagnostics.",
            "Use one ComparisonId per invocation; switch DSP state between baseline and candidate windows and run the tool again.",
            "When BundleDir is reused, pass separate IndexPath and ReportPath values for baseline and candidate runs to avoid overwriting evidence.",
            "Matrix runs copy captureReadinessWatch status, hardGatePass, strictPreflightPass, and top constraints into the trace index and matrix summary.",
            "Matrix schema v2 also copies strong-input counts, weak/strong output gap, mixedWeakStrongEvidenceStatus, and a mixed weak/strong hunt score into each run and trace-index entry.",
            "Matrix schema v3 also copies Candidate artifact-control advisory fields so operators can avoid selecting mixed weak+strong windows with low-evidence lift or unsupported texture-fill artifacts.",
            "The trace index is suitable for artifact-manifest entries with kind=trace.",
            "For G2 acceptance evidence, run the acceptanceCommandSteps after choosing the bundle directory and keep every acceptanceExpectedArtifacts path."
        )
    } | ConvertTo-Json -Depth 16
    exit 0
}

$scenarioList = New-Object System.Collections.Generic.List[string]
foreach ($scenarioId in (Expand-ScenarioIds $ScenarioIds)) {
    $value = [string]$scenarioId
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        $scenarioList.Add($value) | Out-Null
    }
}

if ($scenarioList.Count -eq 0) {
    foreach ($scenarioId in (Get-ManifestScenarioIds -Base $base -RequestTimeoutSec $TimeoutSec -SkipCertificateCheck:$SkipCertificateCheck)) {
        $scenarioList.Add([string]$scenarioId) | Out-Null
    }
}

$scenarios = @($scenarioList.ToArray() | Select-Object -Unique)
if ($scenarios.Count -eq 0) {
    throw "No scenario IDs were supplied and none could be read from /api/dsp/benchmark-capture-manifest."
}

$startedUtc = [DateTimeOffset]::UtcNow
$stamp = $startedUtc.ToString("yyyyMMddTHHmmssfffZ")

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
    $outputBase = Join-Path $bundlePath "artifacts\live-diagnostics-traces"
    if ([string]::IsNullOrWhiteSpace($IndexPath)) {
        $IndexPath = Join-Path $bundlePath "artifacts\live-diagnostics-trace-index.json"
    }
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $bundlePath "artifacts\live-diagnostics-matrix-report.json"
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path $repoRoot "captures\dsp-live-diagnostics-matrix"
    }

    $captureName = if ([string]::IsNullOrWhiteSpace($safeLabel)) { "$stamp-$safeComparison" } else { "$stamp-$safeComparison-$safeLabel" }
    $outputBase = Join-Path $OutputRoot $captureName
    if ([string]::IsNullOrWhiteSpace($IndexPath)) {
        $IndexPath = Join-Path $outputBase "live-diagnostics-trace-index.json"
    }
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $outputBase "live-diagnostics-matrix-report.json"
    }
}

New-Item -ItemType Directory -Force -Path $outputBase | Out-Null

$runs = New-Object System.Collections.Generic.List[object]
$files = New-Object System.Collections.Generic.List[object]

foreach ($scenarioId in $scenarios) {
    $safeScenario = ConvertTo-SafeName $scenarioId
    if ([string]::IsNullOrWhiteSpace($safeScenario)) {
        $safeScenario = "scenario"
    }

    foreach ($comparison in $comparisons) {
        $safeCurrentComparison = ConvertTo-SafeName $comparison
        if ([string]::IsNullOrWhiteSpace($safeCurrentComparison)) {
            $safeCurrentComparison = "comparison"
        }

        $scenarioDir = Join-Path (Join-Path $outputBase $safeScenario) $safeCurrentComparison
        New-Item -ItemType Directory -Force -Path $scenarioDir | Out-Null

        $jsonlPath = Join-Path $scenarioDir "live-diagnostics-trace.jsonl"
        $summaryPath = Join-Path $scenarioDir "live-diagnostics-watch.json"
        $watch = Invoke-Watch `
            -ScriptPath $resolvedWatchScript `
            -Base $base `
            -ScenarioId $scenarioId `
            -Comparison $comparison `
            -RunLabel $Label `
            -SampleCount $Samples `
            -DelayMs $IntervalMs `
            -RequestTimeoutSec $TimeoutSec `
            -JsonlPath $jsonlPath `
            -SummaryPath $summaryPath `
            -SkipCertificateCheck:$SkipCertificateCheck

        $watchOk = Test-Truthy $watch.ok
        if ((-not $watchOk) -and -not $ContinueOnError) {
            throw "Live diagnostics watch failed for scenario '$scenarioId' comparison '$comparison': $($watch.error)"
        }

        $report = $watch.report
        $readyTrace = if ($null -eq $report) { $false } else { Test-Truthy (Get-JsonValue $report "readyForBenchmarkTrace") }
        $trendStatus = if ($null -eq $report) { "watch-failed" } else { [string](Get-JsonValue $report "trendStatus") }
        $okSamples = if ($null -eq $report) { 0 } else { [int](Get-JsonValue $report "okSampleCount") }
        $failedSamples = if ($null -eq $report) { $Samples } else { [int](Get-JsonValue $report "failedSampleCount") }
        $hardBlockers = if ($null -eq $report) { 0 } else { [int](Get-JsonValue $report "hardBlockerSampleCount") }
        $captureReadiness = if ($null -eq $report) { $null } else { Get-JsonValue $report "captureReadinessWatch" }
        $captureReadinessStatus = if ($null -eq $captureReadiness) { "legacy-missing" } else { [string](Get-JsonValue $captureReadiness "status") }
        if ([string]::IsNullOrWhiteSpace($captureReadinessStatus)) {
            $captureReadinessStatus = "legacy-missing"
        }
        if ($null -eq $report) {
            $captureReadinessStatus = "watch-failed"
        }
        $hardGatePass = if ($null -eq $captureReadiness) { ($watchOk -and $failedSamples -eq 0 -and $hardBlockers -eq 0) } else { Test-Truthy (Get-JsonValue $captureReadiness "hardGatePass") }
        $strictPreflightPass = if ($null -eq $captureReadiness) { ($watchOk -and $failedSamples -eq 0 -and $hardBlockers -eq 0 -and $readyTrace) } else { Test-Truthy (Get-JsonValue $captureReadiness "strictPreflightPass") }
        $topCaptureConstraint = if ($null -eq $captureReadiness) { $null } else { Get-JsonValue $captureReadiness "topConstraint" }
        $topCaptureHardConstraint = if ($null -eq $captureReadiness) { $null } else { Get-JsonValue $captureReadiness "topHardConstraint" }
        $topCaptureStatus = if ($null -eq $captureReadiness) { $null } else { Get-JsonValue $captureReadiness "topStatus" }
        $topCaptureConstraintName = [string](Get-JsonValue $topCaptureConstraint "name")
        $topCaptureHardConstraintName = [string](Get-JsonValue $topCaptureHardConstraint "name")
        $topCaptureStatusName = [string](Get-JsonValue $topCaptureStatus "name")
        $topCaptureConstraintCount = Get-IntValue (Get-JsonValue $topCaptureConstraint "count")
        $topCaptureHardConstraintCount = Get-IntValue (Get-JsonValue $topCaptureHardConstraint "count")
        $topCaptureStatusCount = Get-IntValue (Get-JsonValue $topCaptureStatus "count")
        $candidateSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "candidateSampleCount") }
        $candidateWeak = if ($null -eq $report) { $null } else { Get-JsonValue $report "candidateWeakSignalWatch" }
        $candidateWeakInputs = Get-IntValue (Get-JsonValue $candidateWeak "weakInputSampleCount")
        $candidateWeakRecovered = Get-IntValue (Get-JsonValue $candidateWeak "weakRecoveredSampleCount")
        $candidateWeakDropouts = Get-IntValue (Get-JsonValue $candidateWeak "weakDropoutSampleCount")
        $candidateHotMakeup = Get-IntValue (Get-JsonValue $candidateWeak "hotMakeupSampleCount")
        $candidateStrongInputs = Get-IntValue (Get-JsonValue $candidateWeak "strongInputSampleCount")
        $candidateWeakStrongOutputGap = Get-NullableDoubleValue (Get-JsonValue $candidateWeak "weakStrongOutputGapDb")
        $candidateMixedWeakStrongEvidenceReady = Test-Truthy (Get-JsonValue $candidateWeak "mixedWeakStrongEvidenceReady")
        $candidateWeakStrongOutputParityReady = Test-Truthy (Get-JsonValue $candidateWeak "weakStrongOutputParityReady")
        $candidateMixedWeakStrongEvidenceStatus = [string](Get-JsonValue $candidateWeak "mixedWeakStrongEvidenceStatus")
        if ([string]::IsNullOrWhiteSpace($candidateMixedWeakStrongEvidenceStatus)) {
            $candidateMixedWeakStrongEvidenceStatus = if ($candidateSamples -le 0) {
                "no-candidate-samples"
            }
            elseif ($candidateWeakInputs -le 0 -and $candidateStrongInputs -le 0) {
                "missing-weak-and-strong-input"
            }
            elseif ($candidateWeakInputs -le 0) {
                "missing-weak-input"
            }
            elseif ($candidateStrongInputs -le 0) {
                "missing-strong-input"
            }
            elseif ($null -eq $candidateWeakStrongOutputGap) {
                "missing-output-gap"
            }
            elseif (-not $candidateWeakStrongOutputParityReady) {
                "weak-strong-output-gap-watch"
            }
            else {
                "ready"
            }
        }
        $candidateMixedWeakStrongHuntScore = Get-MixedWeakStrongHuntScore `
            -WeakInputSampleCount $candidateWeakInputs `
            -StrongInputSampleCount $candidateStrongInputs `
            -WeakStrongOutputGapDb $candidateWeakStrongOutputGap `
            -ReadyForBenchmarkTrace $readyTrace `
            -WeakStrongOutputParityReady $candidateWeakStrongOutputParityReady `
            -ExpectedSampleCount $okSamples
        $candidateTextureFillAverage = if ($null -eq $report) { $null } else { Get-NullableDoubleValue (Get-JsonValue (Get-JsonValue $report "candidateTextureFill") "average") }
        $candidateSignalProbabilityAverage = if ($null -eq $report) { $null } else { Get-NullableDoubleValue (Get-JsonValue (Get-JsonValue $report "candidateSignalProbability") "average") }
        $candidateLowEvidenceLiftWatch = if ($null -eq $report) { $null } else { Get-JsonValue $report "candidateLowEvidenceLiftWatch" }
        $candidateLowEvidenceLiftedSampleCount = Get-IntValue (Get-JsonValue $candidateLowEvidenceLiftWatch "liftedSampleCount")
        $candidateLowEvidenceLiftedPct = Get-NullableDoubleValue (Get-JsonValue $candidateLowEvidenceLiftWatch "liftedPct")
        $candidateLowEvidenceAlignmentMismatchPct = Get-NullableDoubleValue (Get-JsonValue $candidateLowEvidenceLiftWatch "alignmentMismatchPct")
        $candidateAudioAlignmentWatch = if ($null -eq $report) { $null } else { Get-JsonValue $report "candidateAudioAlignmentWatch" }
        $candidateAudioAlignmentMismatchPct = Get-NullableDoubleValue (Get-JsonValue $candidateAudioAlignmentWatch "mismatchPct")
        $candidateArtifactRiskScore = 0.0
        if ($candidateLowEvidenceLiftedSampleCount -gt 0) {
            $candidateArtifactRiskScore += 1.0
        }
        if ($null -ne $candidateLowEvidenceLiftedPct -and [double]$candidateLowEvidenceLiftedPct -gt 5.0) {
            $candidateArtifactRiskScore += 1.0
        }
        if ($null -ne $candidateAudioAlignmentMismatchPct -and [double]$candidateAudioAlignmentMismatchPct -gt 10.0) {
            $candidateArtifactRiskScore += 1.0
        }
        if ($null -ne $candidateTextureFillAverage -and [double]$candidateTextureFillAverage -gt 0.65 -and
            ($null -eq $candidateSignalProbabilityAverage -or [double]$candidateSignalProbabilityAverage -lt 0.30)) {
            $candidateArtifactRiskScore += 1.0
        }
        $candidateArtifactRiskScore = [Math]::Round($candidateArtifactRiskScore, 3)
        $candidateArtifactRiskStatus = if ($candidateSamples -le 0) {
            "no-candidate-samples"
        }
        elseif ($candidateArtifactRiskScore -gt 0.0) {
            "artifact-review"
        }
        else {
            "artifact-clear"
        }
        $comparisonState = if ($null -eq $report) { $null } else { Get-JsonValue $report "comparisonStateReadiness" }
        $comparisonStateStrict = if ($null -eq $comparisonState) { Test-StrictComparisonStateId $comparison } else { Test-Truthy (Get-JsonValue $comparisonState "strict") }
        $comparisonStateReady = if ($null -eq $comparisonState) { -not $comparisonStateStrict } else { Test-Truthy (Get-JsonValue $comparisonState "ready") }
        $comparisonStateStatus = if ($null -eq $comparisonState) {
            if ($comparisonStateStrict) { "legacy-missing" } else { "not-required" }
        }
        else {
            [string](Get-JsonValue $comparisonState "status")
        }
        $comparisonStateNextAction = if ($null -eq $comparisonState) { "" } else { [string](Get-JsonValue $comparisonState "nextAction") }
        $candidateAlignedSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "candidateAlignedSampleCount") }
        $candidateAgcDiagnosticSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "candidateAgcDiagnosticSampleCount") }
        $candidateProbabilityDiagnosticSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "candidateProbabilityDiagnosticSampleCount") }
        $candidatePeakDiagnosticSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "candidatePeakDiagnosticSampleCount") }
        $candidateRequestedSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "candidateRequestedSampleCount") }
        $candidateEffectiveSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "candidateEffectiveSampleCount") }
        $nrOffRequestedSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "nrOffRequestedSampleCount") }
        $nrOffEffectiveSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "nrOffEffectiveSampleCount") }
        $nrModeMismatchSamples = if ($null -eq $report) { 0 } else { Get-IntValue (Get-JsonValue $report "nrModeMismatchSampleCount") }

        $relativeRoot = if (-not [string]::IsNullOrWhiteSpace($bundlePath)) { $bundlePath } else { Split-Path -Parent $IndexPath }
        $jsonlRelative = ConvertTo-PortablePath -Root $relativeRoot -Path $jsonlPath
        $summaryRelative = ConvertTo-PortablePath -Root $relativeRoot -Path $summaryPath

        $files.Add([ordered]@{
            path = $jsonlRelative
            kind = "diagnostics-jsonl"
            scenarioId = $scenarioId
            comparisonId = $comparison
            label = $Label
            sampleCount = $Samples
            intervalMs = $IntervalMs
            summaryPath = $summaryRelative
            sha256 = Get-FileSha256 $jsonlPath
            summarySha256 = Get-FileSha256 $summaryPath
            candidateWeakInputSampleCount = $candidateWeakInputs
            candidateWeakRecoveredSampleCount = $candidateWeakRecovered
            candidateWeakDropoutSampleCount = $candidateWeakDropouts
            candidateHotMakeupSampleCount = $candidateHotMakeup
            candidateStrongInputSampleCount = $candidateStrongInputs
            candidateWeakStrongOutputGapDb = $candidateWeakStrongOutputGap
            candidateMixedWeakStrongEvidenceReady = $candidateMixedWeakStrongEvidenceReady
            candidateWeakStrongOutputParityReady = $candidateWeakStrongOutputParityReady
            candidateMixedWeakStrongEvidenceStatus = $candidateMixedWeakStrongEvidenceStatus
            candidateMixedWeakStrongHuntScore = $candidateMixedWeakStrongHuntScore
            candidateTextureFillAverage = $candidateTextureFillAverage
            candidateSignalProbabilityAverage = $candidateSignalProbabilityAverage
            candidateLowEvidenceLiftedSampleCount = $candidateLowEvidenceLiftedSampleCount
            candidateLowEvidenceLiftedPct = $candidateLowEvidenceLiftedPct
            candidateLowEvidenceAlignmentMismatchPct = $candidateLowEvidenceAlignmentMismatchPct
            candidateAudioAlignmentMismatchPct = $candidateAudioAlignmentMismatchPct
            candidateArtifactRiskScore = $candidateArtifactRiskScore
            candidateArtifactRiskStatus = $candidateArtifactRiskStatus
            captureReadinessStatus = $captureReadinessStatus
            hardGatePass = $hardGatePass
            strictPreflightPass = $strictPreflightPass
            topCaptureConstraintName = $topCaptureConstraintName
            topCaptureConstraintCount = $topCaptureConstraintCount
            topCaptureHardConstraintName = $topCaptureHardConstraintName
            topCaptureHardConstraintCount = $topCaptureHardConstraintCount
            topCaptureStatusName = $topCaptureStatusName
            topCaptureStatusCount = $topCaptureStatusCount
            comparisonStateStrict = $comparisonStateStrict
            comparisonStateReady = $comparisonStateReady
            comparisonStateStatus = $comparisonStateStatus
            comparisonStateNextAction = $comparisonStateNextAction
            candidateSampleCount = $candidateSamples
            candidateAlignedSampleCount = $candidateAlignedSamples
            candidateAgcDiagnosticSampleCount = $candidateAgcDiagnosticSamples
            candidateProbabilityDiagnosticSampleCount = $candidateProbabilityDiagnosticSamples
            candidatePeakDiagnosticSampleCount = $candidatePeakDiagnosticSamples
            candidateRequestedSampleCount = $candidateRequestedSamples
            candidateEffectiveSampleCount = $candidateEffectiveSamples
            nrOffRequestedSampleCount = $nrOffRequestedSamples
            nrOffEffectiveSampleCount = $nrOffEffectiveSamples
            nrModeMismatchSampleCount = $nrModeMismatchSamples
        }) | Out-Null

        $runs.Add([ordered]@{
            scenarioId = $scenarioId
            comparisonId = $comparison
            label = $Label
            ok = $watchOk
            exitCode = [int]$watch.exitCode
            error = $watch.error
            jsonlPath = $jsonlRelative
            reportPath = $summaryRelative
            trendStatus = $trendStatus
            readyForBenchmarkTrace = $readyTrace
            okSampleCount = $okSamples
            failedSampleCount = $failedSamples
            hardBlockerSampleCount = $hardBlockers
            captureReadinessStatus = $captureReadinessStatus
            hardGatePass = $hardGatePass
            strictPreflightPass = $strictPreflightPass
            topCaptureConstraintName = $topCaptureConstraintName
            topCaptureConstraintCount = $topCaptureConstraintCount
            topCaptureHardConstraintName = $topCaptureHardConstraintName
            topCaptureHardConstraintCount = $topCaptureHardConstraintCount
            topCaptureStatusName = $topCaptureStatusName
            topCaptureStatusCount = $topCaptureStatusCount
            comparisonStateStrict = $comparisonStateStrict
            comparisonStateReady = $comparisonStateReady
            comparisonStateStatus = $comparisonStateStatus
            comparisonStateNextAction = $comparisonStateNextAction
            candidateSampleCount = $candidateSamples
            candidateAlignedSampleCount = $candidateAlignedSamples
            candidateAgcDiagnosticSampleCount = $candidateAgcDiagnosticSamples
            candidateProbabilityDiagnosticSampleCount = $candidateProbabilityDiagnosticSamples
            candidatePeakDiagnosticSampleCount = $candidatePeakDiagnosticSamples
            candidateRequestedSampleCount = $candidateRequestedSamples
            candidateEffectiveSampleCount = $candidateEffectiveSamples
            nrOffRequestedSampleCount = $nrOffRequestedSamples
            nrOffEffectiveSampleCount = $nrOffEffectiveSamples
            nrModeMismatchSampleCount = $nrModeMismatchSamples
            candidateWeakInputSampleCount = $candidateWeakInputs
            candidateWeakRecoveredSampleCount = $candidateWeakRecovered
            candidateWeakDropoutSampleCount = $candidateWeakDropouts
            candidateHotMakeupSampleCount = $candidateHotMakeup
            candidateStrongInputSampleCount = $candidateStrongInputs
            candidateWeakStrongOutputGapDb = $candidateWeakStrongOutputGap
            candidateMixedWeakStrongEvidenceReady = $candidateMixedWeakStrongEvidenceReady
            candidateWeakStrongOutputParityReady = $candidateWeakStrongOutputParityReady
            candidateMixedWeakStrongEvidenceStatus = $candidateMixedWeakStrongEvidenceStatus
            candidateMixedWeakStrongHuntScore = $candidateMixedWeakStrongHuntScore
            candidateTextureFillAverage = $candidateTextureFillAverage
            candidateSignalProbabilityAverage = $candidateSignalProbabilityAverage
            candidateLowEvidenceLiftedSampleCount = $candidateLowEvidenceLiftedSampleCount
            candidateLowEvidenceLiftedPct = $candidateLowEvidenceLiftedPct
            candidateLowEvidenceAlignmentMismatchPct = $candidateLowEvidenceAlignmentMismatchPct
            candidateAudioAlignmentMismatchPct = $candidateAudioAlignmentMismatchPct
            candidateArtifactRiskScore = $candidateArtifactRiskScore
            candidateArtifactRiskStatus = $candidateArtifactRiskStatus
        }) | Out-Null
    }
}

$completedUtc = [DateTimeOffset]::UtcNow
$runArray = @($runs.ToArray())
$failedRunCount = @($runArray | Where-Object { -not (Test-Truthy $_.ok) }).Count
$notReadyTraceCount = @($runArray | Where-Object { -not (Test-Truthy $_.readyForBenchmarkTrace) }).Count
$hardBlockerRunCount = @($runArray | Where-Object { [int]$_.hardBlockerSampleCount -gt 0 }).Count
$hardGatePassRunCount = @($runArray | Where-Object { Test-Truthy $_.hardGatePass }).Count
$strictPreflightPassRunCount = @($runArray | Where-Object { Test-Truthy $_.strictPreflightPass }).Count
$captureReadinessStatusCounts = @{}
$topCaptureConstraintCounts = @{}
$topCaptureHardConstraintCounts = @{}
$topCaptureStatusCounts = @{}
$topCaptureConstraintSampleCounts = @{}
$topCaptureHardConstraintSampleCounts = @{}
$comparisonStateStatusCounts = @{}
foreach ($run in $runArray) {
    Add-Count $captureReadinessStatusCounts ([string]$run.captureReadinessStatus)
    Add-Count $topCaptureConstraintCounts ([string]$run.topCaptureConstraintName)
    Add-Count $topCaptureHardConstraintCounts ([string]$run.topCaptureHardConstraintName)
    Add-Count $topCaptureStatusCounts ([string]$run.topCaptureStatusName)
    Add-Count $comparisonStateStatusCounts ([string]$run.comparisonStateStatus)

    if (-not [string]::IsNullOrWhiteSpace([string]$run.topCaptureConstraintName)) {
        if (-not $topCaptureConstraintSampleCounts.ContainsKey([string]$run.topCaptureConstraintName)) {
            $topCaptureConstraintSampleCounts[[string]$run.topCaptureConstraintName] = 0
        }
        $topCaptureConstraintSampleCounts[[string]$run.topCaptureConstraintName] = [int]$topCaptureConstraintSampleCounts[[string]$run.topCaptureConstraintName] + (Get-IntValue $run.topCaptureConstraintCount)
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$run.topCaptureHardConstraintName)) {
        if (-not $topCaptureHardConstraintSampleCounts.ContainsKey([string]$run.topCaptureHardConstraintName)) {
            $topCaptureHardConstraintSampleCounts[[string]$run.topCaptureHardConstraintName] = 0
        }
        $topCaptureHardConstraintSampleCounts[[string]$run.topCaptureHardConstraintName] = [int]$topCaptureHardConstraintSampleCounts[[string]$run.topCaptureHardConstraintName] + (Get-IntValue $run.topCaptureHardConstraintCount)
    }
}
$captureReadinessStatusSummary = @(ConvertTo-CountArray $captureReadinessStatusCounts)
$topCaptureConstraintRunSummary = @(ConvertTo-CountArray $topCaptureConstraintCounts)
$topCaptureHardConstraintRunSummary = @(ConvertTo-CountArray $topCaptureHardConstraintCounts)
$topCaptureStatusRunSummary = @(ConvertTo-CountArray $topCaptureStatusCounts)
$topCaptureConstraintSampleSummary = @(ConvertTo-CountArray $topCaptureConstraintSampleCounts)
$topCaptureHardConstraintSampleSummary = @(ConvertTo-CountArray $topCaptureHardConstraintSampleCounts)
$comparisonStateStatusSummary = @(ConvertTo-CountArray $comparisonStateStatusCounts)
$comparisonStateStrictRunCount = @($runArray | Where-Object { Test-Truthy $_.comparisonStateStrict }).Count
$comparisonStateReadyRunCount = @($runArray | Where-Object { Test-Truthy $_.comparisonStateReady }).Count
$comparisonStateStrictFailureCount = @($runArray | Where-Object { (Test-Truthy $_.comparisonStateStrict) -and -not (Test-Truthy $_.comparisonStateReady) }).Count
$candidateWeakInputSampleCount = 0
$candidateWeakRecoveredSampleCount = 0
$candidateWeakDropoutSampleCount = 0
$candidateHotMakeupSampleCount = 0
$candidateStrongInputSampleCount = 0
$candidateMixedWeakStrongTraceCount = 0
$candidateMixedWeakStrongReadyTraceCount = 0
$candidateMixedWeakStrongGapWatchRunCount = 0
$candidateMixedWeakStrongMissingRunCount = 0
$candidateMixedWeakStrongStatusCounts = @{}
$candidateArtifactReviewRunCount = 0
$candidateArtifactRiskScoreMax = 0.0
$candidateLowEvidenceLiftedSampleCount = 0
$candidateLowEvidenceLiftedPctMax = $null
$candidateAudioAlignmentMismatchPctMax = $null
$candidateArtifactRiskStatusCounts = @{}
foreach ($run in $runArray) {
    $candidateWeakInputSampleCount += Get-IntValue $run.candidateWeakInputSampleCount
    $candidateWeakRecoveredSampleCount += Get-IntValue $run.candidateWeakRecoveredSampleCount
    $candidateWeakDropoutSampleCount += Get-IntValue $run.candidateWeakDropoutSampleCount
    $candidateHotMakeupSampleCount += Get-IntValue $run.candidateHotMakeupSampleCount
    $candidateStrongInputSampleCount += Get-IntValue $run.candidateStrongInputSampleCount

    $mixedStatus = [string]$run.candidateMixedWeakStrongEvidenceStatus
    if ([string]::IsNullOrWhiteSpace($mixedStatus)) {
        $mixedStatus = "not-evaluated"
    }
    Add-Count $candidateMixedWeakStrongStatusCounts $mixedStatus

    if ((Get-IntValue $run.candidateWeakInputSampleCount) -gt 0 -and (Get-IntValue $run.candidateStrongInputSampleCount) -gt 0) {
        $candidateMixedWeakStrongTraceCount++
    }
    else {
        $candidateMixedWeakStrongMissingRunCount++
    }

    if (Test-Truthy $run.candidateWeakStrongOutputParityReady) {
        $candidateMixedWeakStrongReadyTraceCount++
    }

    if ([string]::Equals($mixedStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateMixedWeakStrongGapWatchRunCount++
    }

    $artifactStatus = [string]$run.candidateArtifactRiskStatus
    if ([string]::IsNullOrWhiteSpace($artifactStatus)) {
        $artifactStatus = "not-evaluated"
    }
    Add-Count $candidateArtifactRiskStatusCounts $artifactStatus
    if ([string]::Equals($artifactStatus, "artifact-review", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateArtifactReviewRunCount++
    }

    $artifactRiskScore = Get-NullableDoubleValue $run.candidateArtifactRiskScore
    if ($null -ne $artifactRiskScore) {
        $candidateArtifactRiskScoreMax = [Math]::Max([double]$candidateArtifactRiskScoreMax, [double]$artifactRiskScore)
    }
    $candidateLowEvidenceLiftedSampleCount += Get-IntValue $run.candidateLowEvidenceLiftedSampleCount
    $liftedPct = Get-NullableDoubleValue $run.candidateLowEvidenceLiftedPct
    if ($null -ne $liftedPct) {
        if ($null -eq $candidateLowEvidenceLiftedPctMax) {
            $candidateLowEvidenceLiftedPctMax = [double]$liftedPct
        }
        else {
            $candidateLowEvidenceLiftedPctMax = [Math]::Max([double]$candidateLowEvidenceLiftedPctMax, [double]$liftedPct)
        }
    }
    $audioMismatchPct = Get-NullableDoubleValue $run.candidateAudioAlignmentMismatchPct
    if ($null -ne $audioMismatchPct) {
        if ($null -eq $candidateAudioAlignmentMismatchPctMax) {
            $candidateAudioAlignmentMismatchPctMax = [double]$audioMismatchPct
        }
        else {
            $candidateAudioAlignmentMismatchPctMax = [Math]::Max([double]$candidateAudioAlignmentMismatchPctMax, [double]$audioMismatchPct)
        }
    }
}
$candidateMixedWeakStrongStatusSummary = @(ConvertTo-CountArray $candidateMixedWeakStrongStatusCounts)
$candidateArtifactRiskStatusSummary = @(ConvertTo-CountArray $candidateArtifactRiskStatusCounts)
$bestMixedWeakStrongRun = $null
if ($runArray.Count -gt 0) {
    $bestMixedWeakStrongRun = @($runArray | Sort-Object `
            @{ Expression = { -1.0 * [double](Get-NullableDoubleValue $_.candidateMixedWeakStrongHuntScore) } }, `
            @{ Expression = { if (Test-Truthy $_.candidateWeakStrongOutputParityReady) { 0 } else { 1 } } }, `
            @{ Expression = { $value = Get-NullableDoubleValue $_.candidateWeakStrongOutputGapDb; if ($null -eq $value) { 999.0 } else { [Math]::Abs([double]$value) } } }, `
            @{ Expression = { [string]$_.scenarioId } }, `
            @{ Expression = { [string]$_.comparisonId } } | Select-Object -First 1)
}
$bestMixedWeakStrongRunSummary = if ($null -eq $bestMixedWeakStrongRun) {
    $null
}
else {
    [ordered]@{
        scenarioId = [string]$bestMixedWeakStrongRun.scenarioId
        comparisonId = [string]$bestMixedWeakStrongRun.comparisonId
        reportPath = [string]$bestMixedWeakStrongRun.reportPath
        readyForBenchmarkTrace = Test-Truthy $bestMixedWeakStrongRun.readyForBenchmarkTrace
        candidateWeakInputSampleCount = Get-IntValue $bestMixedWeakStrongRun.candidateWeakInputSampleCount
        candidateStrongInputSampleCount = Get-IntValue $bestMixedWeakStrongRun.candidateStrongInputSampleCount
        candidateWeakStrongOutputGapDb = Get-NullableDoubleValue $bestMixedWeakStrongRun.candidateWeakStrongOutputGapDb
        candidateMixedWeakStrongEvidenceStatus = [string]$bestMixedWeakStrongRun.candidateMixedWeakStrongEvidenceStatus
        candidateMixedWeakStrongHuntScore = Get-NullableDoubleValue $bestMixedWeakStrongRun.candidateMixedWeakStrongHuntScore
        candidateArtifactRiskScore = Get-NullableDoubleValue $bestMixedWeakStrongRun.candidateArtifactRiskScore
        candidateArtifactRiskStatus = [string]$bestMixedWeakStrongRun.candidateArtifactRiskStatus
    }
}
$candidateMixedWeakStrongHuntReady = ($candidateMixedWeakStrongReadyTraceCount -gt 0)

$recommendations = New-Object System.Collections.Generic.List[string]
if ($failedRunCount -gt 0) {
    $recommendations.Add("Repeat failed scenario windows before using this matrix as live DSP evidence.") | Out-Null
}
if ($hardBlockerRunCount -gt 0) {
    $recommendations.Add("Resolve hard live-diagnostics blockers before candidate acceptance review.") | Out-Null
}
if ($strictPreflightPassRunCount -lt $runArray.Count) {
    $topPreflightConstraint = @($topCaptureConstraintSampleSummary | Select-Object -First 1)
    if ($topPreflightConstraint.Count -gt 0) {
        $recommendations.Add("Top matrix preflight constraint is '$([string](Get-JsonValue $topPreflightConstraint[0] "name"))' across $([int](Get-JsonValue $topPreflightConstraint[0] "count")) sample(s); clear it before treating matrix evidence as final acceptance proof.") | Out-Null
    }
}
if ($hardBlockerRunCount -gt 0) {
    $topHardPreflightConstraint = @($topCaptureHardConstraintSampleSummary | Select-Object -First 1)
    if ($topHardPreflightConstraint.Count -gt 0) {
        $recommendations.Add("Top hard matrix gate is '$([string](Get-JsonValue $topHardPreflightConstraint[0] "name"))' across $([int](Get-JsonValue $topHardPreflightConstraint[0] "count")) sample(s); recapture after clearing that gate.") | Out-Null
    }
}
if ($notReadyTraceCount -gt 0) {
    $recommendations.Add("Pair not-ready traces with operator notes; they are useful preflight evidence but not acceptance evidence.") | Out-Null
}
if ($comparisonStateStrictFailureCount -gt 0) {
    $topState = @($comparisonStateStatusSummary | Select-Object -First 1)
    if ($topState.Count -gt 0) {
        $recommendations.Add("Comparison state proof failed for $comparisonStateStrictFailureCount strict run(s); top state status is '$([string](Get-JsonValue $topState[0] "name"))'. Recapture after the requested/effective NR mode matches the comparison label.") | Out-Null
    }
    else {
        $recommendations.Add("Comparison state proof failed for one or more strict runs; recapture after the requested/effective NR mode matches the comparison label.") | Out-Null
    }
}
if ($candidateWeakDropoutSampleCount -gt 0) {
    $recommendations.Add("Candidate weak-input dropouts appeared in the matrix; compare these windows against baseline traces before tuning recovery/makeup further.") | Out-Null
}
if ($candidateHotMakeupSampleCount -gt 0) {
    $recommendations.Add("Candidate hot-makeup samples appeared in the matrix; inspect the watch summaries before changing recovery attack/release.") | Out-Null
}
if ($candidateArtifactReviewRunCount -gt 0) {
    $recommendations.Add("Candidate artifact-control advisories appeared in the matrix; inspect low-evidence lift, audio-alignment mismatch, and texture-fill evidence before using these windows for tuning.") | Out-Null
}
if ($candidateWeakInputSampleCount -gt 0 -and $candidateStrongInputSampleCount -le 0) {
    $recommendations.Add("Matrix Candidate traces have weak-input samples but no strong-input samples; scan active SSB windows with both edge-of-readability and strong speech before claiming weak/strong volume parity.") | Out-Null
}
elseif ($candidateStrongInputSampleCount -gt 0 -and $candidateWeakInputSampleCount -le 0) {
    $recommendations.Add("Matrix Candidate traces have strong-input samples but no weak-input samples; include edge-of-readability speech before using this as mixed weak/strong parity evidence.") | Out-Null
}
elseif ($candidateMixedWeakStrongTraceCount -gt 0 -and $candidateMixedWeakStrongReadyTraceCount -eq 0) {
    $recommendations.Add("Matrix found mixed weak+strong Candidate samples but no parity-ready trace; inspect weakStrongOutputGapDb and tune only with opt-in settings until the gap is within 6 dB.") | Out-Null
}
elseif ($candidateMixedWeakStrongReadyTraceCount -gt 0 -and $null -ne $bestMixedWeakStrongRunSummary) {
    $recommendations.Add("Best mixed weak+strong matrix run is '$($bestMixedWeakStrongRunSummary.scenarioId)'/$($bestMixedWeakStrongRunSummary.comparisonId) with score $($bestMixedWeakStrongRunSummary.candidateMixedWeakStrongHuntScore); use that window for history and live comparison evidence.") | Out-Null
}
if ($recommendations.Count -eq 0) {
    $recommendations.Add("Store this trace index with the modernization bundle and compare candidate windows against baseline traces before changing DSP defaults.") | Out-Null
}

$index = [ordered]@{
    schemaVersion = 3
    tool = "run-dsp-live-diagnostics-matrix"
    artifactId = "live-diagnostics-trace-index"
    generatedUtc = $completedUtc
    baseUrl = $base
    comparisonId = $comparison
    comparisonIds = @($comparisons)
    label = $Label
    scenarioIds = @($scenarios)
    candidateMixedWeakStrongHuntReady = $candidateMixedWeakStrongHuntReady
    candidateMixedWeakStrongTraceCount = $candidateMixedWeakStrongTraceCount
    candidateMixedWeakStrongReadyTraceCount = $candidateMixedWeakStrongReadyTraceCount
    candidateMixedWeakStrongMissingRunCount = $candidateMixedWeakStrongMissingRunCount
    candidateMixedWeakStrongGapWatchRunCount = $candidateMixedWeakStrongGapWatchRunCount
    candidateMixedWeakStrongStatusCounts = @($candidateMixedWeakStrongStatusSummary)
    bestMixedWeakStrongRun = $bestMixedWeakStrongRunSummary
    candidateArtifactReviewRunCount = $candidateArtifactReviewRunCount
    candidateArtifactRiskScoreMax = $candidateArtifactRiskScoreMax
    candidateLowEvidenceLiftedSampleCount = $candidateLowEvidenceLiftedSampleCount
    candidateLowEvidenceLiftedPctMax = $candidateLowEvidenceLiftedPctMax
    candidateAudioAlignmentMismatchPctMax = $candidateAudioAlignmentMismatchPctMax
    candidateArtifactRiskStatusCounts = @($candidateArtifactRiskStatusSummary)
    files = @($files.ToArray())
}

Write-JsonFile -Path $IndexPath -Value $index

$reportObject = [ordered]@{
    schemaVersion = 3
    tool = "run-dsp-live-diagnostics-matrix"
    generatedUtc = $completedUtc
    startedUtc = $startedUtc
    completedUtc = $completedUtc
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    baseUrl = $base
    comparisonId = $comparison
    comparisonIds = @($comparisons)
    label = $Label
    samples = $Samples
    intervalMs = $IntervalMs
    skipCertificateCheck = [bool]$SkipCertificateCheck
    scenarioCount = $scenarios.Count
    failedRunCount = $failedRunCount
    notReadyTraceCount = $notReadyTraceCount
    hardBlockerRunCount = $hardBlockerRunCount
    hardGatePassRunCount = $hardGatePassRunCount
    strictPreflightPassRunCount = $strictPreflightPassRunCount
    captureReadinessStatusCounts = @($captureReadinessStatusSummary)
    topCaptureConstraintRunCounts = @($topCaptureConstraintRunSummary)
    topCaptureHardConstraintRunCounts = @($topCaptureHardConstraintRunSummary)
    topCaptureStatusRunCounts = @($topCaptureStatusRunSummary)
    topCaptureConstraintSampleCounts = @($topCaptureConstraintSampleSummary)
    topCaptureHardConstraintSampleCounts = @($topCaptureHardConstraintSampleSummary)
    comparisonStateStatusCounts = @($comparisonStateStatusSummary)
    comparisonStateStrictRunCount = $comparisonStateStrictRunCount
    comparisonStateReadyRunCount = $comparisonStateReadyRunCount
    comparisonStateStrictFailureCount = $comparisonStateStrictFailureCount
    candidateWeakInputSampleCount = $candidateWeakInputSampleCount
    candidateWeakRecoveredSampleCount = $candidateWeakRecoveredSampleCount
    candidateWeakDropoutSampleCount = $candidateWeakDropoutSampleCount
    candidateHotMakeupSampleCount = $candidateHotMakeupSampleCount
    candidateStrongInputSampleCount = $candidateStrongInputSampleCount
    candidateMixedWeakStrongHuntReady = $candidateMixedWeakStrongHuntReady
    candidateMixedWeakStrongTraceCount = $candidateMixedWeakStrongTraceCount
    candidateMixedWeakStrongReadyTraceCount = $candidateMixedWeakStrongReadyTraceCount
    candidateMixedWeakStrongMissingRunCount = $candidateMixedWeakStrongMissingRunCount
    candidateMixedWeakStrongGapWatchRunCount = $candidateMixedWeakStrongGapWatchRunCount
    candidateMixedWeakStrongStatusCounts = @($candidateMixedWeakStrongStatusSummary)
    bestMixedWeakStrongRun = $bestMixedWeakStrongRunSummary
    candidateArtifactReviewRunCount = $candidateArtifactReviewRunCount
    candidateArtifactRiskScoreMax = $candidateArtifactRiskScoreMax
    candidateLowEvidenceLiftedSampleCount = $candidateLowEvidenceLiftedSampleCount
    candidateLowEvidenceLiftedPctMax = $candidateLowEvidenceLiftedPctMax
    candidateAudioAlignmentMismatchPctMax = $candidateAudioAlignmentMismatchPctMax
    candidateArtifactRiskStatusCounts = @($candidateArtifactRiskStatusSummary)
    collectionReady = ($failedRunCount -eq 0)
    acceptanceReady = ($failedRunCount -eq 0 -and $notReadyTraceCount -eq 0 -and $hardBlockerRunCount -eq 0 -and $comparisonStateStrictFailureCount -eq 0)
    indexPath = if (-not [string]::IsNullOrWhiteSpace($bundlePath)) { Resolve-RelativePath -Root $bundlePath -Path $IndexPath } else { $IndexPath }
    indexSha256 = Get-FileSha256 $IndexPath
    runs = @($runArray)
    recommendations = @($recommendations.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $reportObject

if ($JsonOnly) {
    $reportObject | ConvertTo-Json -Depth 64
}
else {
    Write-Host "DSP live diagnostics matrix written: $ReportPath"
    Write-Host "Trace index: $IndexPath"
    Write-Host "Scenarios: $($scenarios.Count), Failed: $failedRunCount, Not ready: $notReadyTraceCount"
}

if ($failedRunCount -gt 0 -and -not $ContinueOnError) {
    exit 1
}
