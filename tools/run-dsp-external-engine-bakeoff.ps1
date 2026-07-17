param(
    [string]$BundleDir = "",

    [string]$CandidateId = "",

    [string[]]$ScenarioIds = @(),

    [string]$BaseUrl = "http://localhost:6060",

    [int]$Samples = 60,

    [int]$IntervalMs = 1000,

    [int]$TimeoutSec = 5,

    [string]$ValidationReportPath = "",

    [string]$TriageReportPath = "",

    [string]$BenchmarkPlanPath = "",

    [string]$MetricCatalogPath = "",

    [string]$FixtureMetricsPath = "",

    [string]$FixtureAudioIndexPath = "",

    [string]$FixtureSpectrumIndexPath = "",

    [string]$RuntimeAuditPath = "",

    [string]$StageTimingReportPath = "",

    [string]$FixtureComparisonReportPath = "",

    [string]$FixtureComparisonMarkdownPath = "",

    [string]$FixtureMatrixSummaryPath = "",

    [string]$LiveIndexPath = "",

    [string]$LiveReportPath = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [switch]$PlanOnly,

    [switch]$Execute,

    [switch]$SkipFixtureComparison,

    [switch]$SkipLiveMatrix,

    [switch]$SkipCertificateCheck,

    [switch]$AllowRegression,

    [switch]$ContinueOnError,

    [switch]$ForceFixtureEvidence,

    [switch]$AllowRuntimeAuditPreflight,

    [switch]$NoMarkdown,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path (Get-Location).Path $Path
}

function Resolve-BundlePath {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $BundlePath $Path
}

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
}

function ConvertTo-Id {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-")
}

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { return "" }
    if ([string]::IsNullOrWhiteSpace($Root)) { return $Path }

    try {
        $rootFull = [System.IO.Path]::GetFullPath($Root)
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $rootFull = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        }

        $relative = $null
        try {
            $relative = [System.IO.Path]::GetRelativePath($rootFull, $pathFull)
        }
        catch {
            $rootUri = [System.Uri]::new($rootFull)
            $pathUri = [System.Uri]::new($pathFull)
            $relative = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
        }

        if (-not $relative.StartsWith("..")) {
            return ($relative -replace "\\", "/")
        }
    }
    catch {
        # Keep the original path if it cannot be normalized.
    }

    return $Path
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse JSON file '$Path': $($_.Exception.Message)"
    }
}

function Read-OptionalJsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    return Read-JsonFile $Path
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

    $Value | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Get-JsonValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        foreach ($key in @($Object.Keys)) {
            if ([string]::Equals([string]$key, $Name, [StringComparison]::OrdinalIgnoreCase)) {
                return $Object[$key]
            }
        }
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
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
    if ($null -eq $value) { return @() }
    if ($value -is [System.Array]) { return @($value) }
    return @($value)
}

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return $Value }
    return [bool]$Value
}

function Get-AcceptanceActionById {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$ActionId
    )

    foreach ($action in @(Get-JsonArray $Report "acceptanceActionPlan")) {
        if ([string](Get-JsonValue $action "actionId") -eq $ActionId) {
            return $action
        }
    }

    return $null
}

function Get-StringList {
    param($Values)

    return @($Values | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function ConvertTo-FixtureScenarioId {
    param([string]$ScenarioId)

    $normalized = ConvertTo-Id $ScenarioId
    switch ($normalized) {
        "ssb-like-speech-post-demod" { return "ssb-like-speech" }
        "offline-ssb-like-speech" { return "ssb-like-speech" }
        "weak-ssb-speech-latency" { return "ssb-like-speech" }
        "weak-cw-carrier-bypass" { return "weak-cw-carrier" }
        "agc-disabled-no-pumping" { return "agc-level-step" }
        "service-unavailable-bypass" { return "noise-only-gating" }
        "ns-vad-only-post-demod" { return "noise-only-gating" }
        default { return $normalized }
    }
}

function ConvertTo-FixtureScenarioIds {
    param([string[]]$ScenarioIds)

    return @($ScenarioIds |
        ForEach-Object { ConvertTo-FixtureScenarioId ([string]$_) } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique)
}

function Add-CliArg {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Value = ""
    )

    $Arguments.Add($Name) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        $Arguments.Add($Value) | Out-Null
    }
}

function Add-CliValues {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Name,
        [string[]]$Values
    )

    $items = @(Get-StringList $Values)
    if ($items.Count -eq 0) { return }

    $Arguments.Add($Name) | Out-Null
    foreach ($item in $items) {
        $Arguments.Add([string]$item) | Out-Null
    }
}

function Format-Command {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptName,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    return "powershell -NoProfile -ExecutionPolicy Bypass -File tools\$ScriptName $($Arguments -join ' ')"
}

function Invoke-ToolScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$Quiet,
        [switch]$AllowNonZeroExit
    )

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
    if (-not $Quiet) {
        foreach ($line in @($output)) {
            Write-Host $line
        }
    }

    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowNonZeroExit) {
        throw "Tool failed with exit code ${exitCode}: $ScriptPath"
    }

    return $exitCode
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# External DSP/ML Bakeoff Cycle") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Status: $([string](Get-JsonValue $Report "status"))") | Out-Null
    $lines.Add("- Candidate: $([string](Get-JsonValue $Report "candidateId"))") | Out-Null
    $lines.Add("- Comparison: $([string](Get-JsonValue $Report "comparisonId"))") | Out-Null
    $lines.Add("- Ready to execute: $(Get-JsonValue $Report "readyToExecute")") | Out-Null
    $lines.Add("- Executed: $(Get-JsonValue $Report "executed")") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Safety") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("External engines stay post-demod, operator-opt-in, RX-only for this bakeoff. Raw WDSP IQ, TX audio, TX monitor, PureSignal feedback, and default behavior changes are not authorized by this tool.") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Command Steps") | Out-Null
    $lines.Add("") | Out-Null
    $index = 1
    foreach ($step in @(Get-JsonArray $Report "commandSteps")) {
        $lines.Add("$index. ``$step``") | Out-Null
        $index++
    }
    $lines.Add("") | Out-Null

    $missing = @(Get-JsonArray $Report "missingPrerequisites")
    if ($missing.Count -gt 0) {
        $lines.Add("## Missing Prerequisites") | Out-Null
        $lines.Add("") | Out-Null
        foreach ($item in $missing) {
            $lines.Add("- $([string](Get-JsonValue $item "path")): $([string](Get-JsonValue $item "reason"))") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    return ($lines -join [Environment]::NewLine)
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
if ($PlanOnly -and $Execute) {
    throw "Use either -PlanOnly or -Execute, not both."
}
if ([string]::IsNullOrWhiteSpace($BundleDir)) {
    throw "Specify -BundleDir so paths stay bundle-relative and auditable."
}

$repoRoot = Get-RepoRoot
$bundlePath = Resolve-RepoPath $BundleDir
$base = Normalize-BaseUrl $BaseUrl

if ([string]::IsNullOrWhiteSpace($ValidationReportPath)) {
    $ValidationReportPath = Join-Path $bundlePath "validation-report.json"
}
else {
    $ValidationReportPath = Resolve-BundlePath -BundlePath $bundlePath -Path $ValidationReportPath
}

if ([string]::IsNullOrWhiteSpace($TriageReportPath)) {
    $TriageReportPath = Join-Path $bundlePath "validation-triage-report.json"
}
else {
    $TriageReportPath = Resolve-BundlePath -BundlePath $bundlePath -Path $TriageReportPath
}

$validationReport = Read-OptionalJsonFile $ValidationReportPath
$triageReport = Read-OptionalJsonFile $TriageReportPath
$externalAction = Get-AcceptanceActionById -Report $triageReport -ActionId "run-first-safe-external-engine-bakeoff"

$candidate = ConvertTo-Id $CandidateId
if ([string]::IsNullOrWhiteSpace($candidate) -and $null -ne $validationReport) {
    $candidate = ConvertTo-Id ([string](Get-JsonValue $validationReport "externalEngineBakeoffFirstSafeCandidateId"))
}
if ([string]::IsNullOrWhiteSpace($candidate) -and $null -ne $externalAction) {
    foreach ($step in @(Get-JsonArray $externalAction "commandSteps")) {
        $match = [regex]::Match([string]$step, '-CandidateId\s+"?([A-Za-z0-9._-]+)"?')
        if ($match.Success) {
            $candidate = ConvertTo-Id $match.Groups[1].Value
            break
        }
    }
}
if ([string]::IsNullOrWhiteSpace($candidate)) {
    $candidate = "candidate-id"
}

$scenarioList = @(Get-StringList $ScenarioIds)
if ($scenarioList.Count -eq 0 -and $null -ne $validationReport) {
    $scenarioList = @(Get-StringList (Get-JsonArray $validationReport "externalEngineBakeoffFirstSafeScenarioIds"))
}
if ($scenarioList.Count -eq 0 -and $null -ne $validationReport) {
    $scenarioList = @(Get-StringList (Get-JsonArray $validationReport "externalEngineBakeoffPlanScenarioIds"))
}
if ($scenarioList.Count -eq 0) {
    $scenarioList = @("ssb-like-speech-post-demod", "agc-disabled-no-pumping", "noise-only-gating")
}
$fixtureScenarioList = @(ConvertTo-FixtureScenarioIds $scenarioList)
$fixtureComparisonIds = @("off-baseline", "current-zeus", "thetis-parity", "candidate-external-engine-opt-in")

if ([string]::IsNullOrWhiteSpace($BenchmarkPlanPath)) {
    $BenchmarkPlanPath = Join-Path $bundlePath "benchmark-plan.json"
}
else {
    $BenchmarkPlanPath = Resolve-BundlePath -BundlePath $bundlePath -Path $BenchmarkPlanPath
}
if ([string]::IsNullOrWhiteSpace($MetricCatalogPath)) {
    $MetricCatalogPath = Join-Path $bundlePath "benchmark-metric-catalog.json"
}
else {
    $MetricCatalogPath = Resolve-BundlePath -BundlePath $bundlePath -Path $MetricCatalogPath
}
if ([string]::IsNullOrWhiteSpace($FixtureMetricsPath)) {
    $FixtureMetricsPath = Join-Path $bundlePath "artifacts\offline-fixture-metrics.json"
}
else {
    $FixtureMetricsPath = Resolve-BundlePath -BundlePath $bundlePath -Path $FixtureMetricsPath
}
if ([string]::IsNullOrWhiteSpace($FixtureAudioIndexPath)) {
    $FixtureAudioIndexPath = Join-Path $bundlePath "artifacts\audio-render-before-after.json"
}
else {
    $FixtureAudioIndexPath = Resolve-BundlePath -BundlePath $bundlePath -Path $FixtureAudioIndexPath
}
if ([string]::IsNullOrWhiteSpace($FixtureSpectrumIndexPath)) {
    $FixtureSpectrumIndexPath = Join-Path $bundlePath "artifacts\spectrum-before-after.json"
}
else {
    $FixtureSpectrumIndexPath = Resolve-BundlePath -BundlePath $bundlePath -Path $FixtureSpectrumIndexPath
}
if ([string]::IsNullOrWhiteSpace($RuntimeAuditPath)) {
    $RuntimeAuditPath = Join-Path $bundlePath "artifacts\wdsp-runtime-artifact-audit.json"
}
else {
    $RuntimeAuditPath = Resolve-BundlePath -BundlePath $bundlePath -Path $RuntimeAuditPath
}
if ([string]::IsNullOrWhiteSpace($StageTimingReportPath)) {
    $StageTimingReportPath = Join-Path $bundlePath "artifacts\native-stage-timing-report.json"
}
else {
    $StageTimingReportPath = Resolve-BundlePath -BundlePath $bundlePath -Path $StageTimingReportPath
}
if ([string]::IsNullOrWhiteSpace($FixtureComparisonReportPath)) {
    $FixtureComparisonReportPath = Join-Path $bundlePath "artifacts\dsp-fixture-metric-comparison.json"
}
else {
    $FixtureComparisonReportPath = Resolve-BundlePath -BundlePath $bundlePath -Path $FixtureComparisonReportPath
}
if ([string]::IsNullOrWhiteSpace($FixtureComparisonMarkdownPath)) {
    $FixtureComparisonMarkdownPath = Join-Path $bundlePath "artifacts\dsp-fixture-metric-comparison.md"
}
else {
    $FixtureComparisonMarkdownPath = Resolve-BundlePath -BundlePath $bundlePath -Path $FixtureComparisonMarkdownPath
}
if ([string]::IsNullOrWhiteSpace($FixtureMatrixSummaryPath)) {
    $FixtureMatrixSummaryPath = Join-Path $bundlePath "artifacts\wdsp-fixture-matrix-summary.external-engine.$candidate.json"
}
else {
    $FixtureMatrixSummaryPath = Resolve-BundlePath -BundlePath $bundlePath -Path $FixtureMatrixSummaryPath
}
if ([string]::IsNullOrWhiteSpace($LiveIndexPath)) {
    $LiveIndexPath = Join-Path $bundlePath "artifacts\live-diagnostics-trace-index.external-engine.$candidate.json"
}
else {
    $LiveIndexPath = Resolve-BundlePath -BundlePath $bundlePath -Path $LiveIndexPath
}
if ([string]::IsNullOrWhiteSpace($LiveReportPath)) {
    $LiveReportPath = Join-Path $bundlePath "artifacts\live-diagnostics-matrix-report.external-engine.$candidate.json"
}
else {
    $LiveReportPath = Resolve-BundlePath -BundlePath $bundlePath -Path $LiveReportPath
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $bundlePath "artifacts\external-engine-bakeoff-cycle-summary.json"
}
else {
    $ReportPath = Resolve-BundlePath -BundlePath $bundlePath -Path $ReportPath
}
if ([string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $MarkdownPath = Join-Path $bundlePath "artifacts\external-engine-bakeoff-cycle-summary.md"
}
else {
    $MarkdownPath = Resolve-BundlePath -BundlePath $bundlePath -Path $MarkdownPath
}

$missingPrerequisites = New-Object System.Collections.Generic.List[object]
if (-not $SkipFixtureComparison) {
    if (-not (Test-Path -LiteralPath $BenchmarkPlanPath -PathType Leaf)) {
        $missingPrerequisites.Add([ordered]@{
                path = ConvertTo-PortablePath -Root $bundlePath -Path $BenchmarkPlanPath
                reason = "benchmark plan is required so the external bakeoff can generate WDSP fixture metrics"
            }) | Out-Null
    }
    if ($fixtureScenarioList.Count -eq 0) {
        $missingPrerequisites.Add([ordered]@{
                path = "scenarioIds"
                reason = "no external bakeoff scenario IDs could be mapped to offline fixture scenarios"
            }) | Out-Null
    }
    if ($Execute -and -not $ForceFixtureEvidence) {
        foreach ($outputPath in @(
                $FixtureMetricsPath,
                $FixtureAudioIndexPath,
                $FixtureSpectrumIndexPath,
                $RuntimeAuditPath,
                $StageTimingReportPath,
                $FixtureComparisonReportPath,
                $FixtureMatrixSummaryPath
            )) {
            if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
                $missingPrerequisites.Add([ordered]@{
                        path = ConvertTo-PortablePath -Root $bundlePath -Path $outputPath
                        reason = "fixture output already exists; pass -ForceFixtureEvidence or choose a new output path before executing"
                    }) | Out-Null
            }
        }
        if (-not $NoMarkdown -and (Test-Path -LiteralPath $FixtureComparisonMarkdownPath -PathType Leaf)) {
            $missingPrerequisites.Add([ordered]@{
                    path = ConvertTo-PortablePath -Root $bundlePath -Path $FixtureComparisonMarkdownPath
                    reason = "fixture comparison Markdown already exists; pass -ForceFixtureEvidence or choose a new output path before executing"
                }) | Out-Null
        }
    }
}

$validationReady = if ($null -eq $validationReport) { $false } else { Test-Truthy (Get-JsonValue $validationReport "externalEngineBakeoffReady") }
$defaultChangeReady = if ($null -eq $validationReport) { $false } else { Test-Truthy (Get-JsonValue $validationReport "externalEngineBakeoffPlanDefaultBehaviorChangeReady") }
$rawIqAllowed = if ($null -eq $validationReport) { $false } else { Test-Truthy (Get-JsonValue $validationReport "externalEngineBakeoffPlanRawWdspIqReplacementAllowed") }
$externalActionPresent = $null -ne $externalAction

if (-not $validationReady) {
    $missingPrerequisites.Add([ordered]@{
            path = ConvertTo-PortablePath -Root $bundlePath -Path $ValidationReportPath
            reason = "strict validation has not confirmed externalEngineBakeoffReady=true"
        }) | Out-Null
}
if (-not $externalActionPresent) {
    $missingPrerequisites.Add([ordered]@{
            path = ConvertTo-PortablePath -Root $bundlePath -Path $TriageReportPath
            reason = "validation triage has not emitted run-first-safe-external-engine-bakeoff"
        }) | Out-Null
}
if ($defaultChangeReady -or $rawIqAllowed) {
    $missingPrerequisites.Add([ordered]@{
            path = ConvertTo-PortablePath -Root $bundlePath -Path $ValidationReportPath
            reason = "external bakeoff plan must not authorize default behavior changes or raw WDSP IQ replacement"
        }) | Out-Null
}

$fixtureMatrixArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $fixtureMatrixArgs "-BundleDir" "`"$bundlePath`""
Add-CliArg $fixtureMatrixArgs "-BenchmarkPlanPath" "`"$BenchmarkPlanPath`""
if (Test-Path -LiteralPath $MetricCatalogPath -PathType Leaf) {
    Add-CliArg $fixtureMatrixArgs "-MetricCatalogPath" "`"$MetricCatalogPath`""
}
Add-CliArg $fixtureMatrixArgs "-MetricsPath" "`"$FixtureMetricsPath`""
Add-CliArg $fixtureMatrixArgs "-AudioIndexPath" "`"$FixtureAudioIndexPath`""
Add-CliArg $fixtureMatrixArgs "-SpectrumIndexPath" "`"$FixtureSpectrumIndexPath`""
Add-CliArg $fixtureMatrixArgs "-RuntimeAuditPath" "`"$RuntimeAuditPath`""
Add-CliArg $fixtureMatrixArgs "-StageTimingReportPath" "`"$StageTimingReportPath`""
Add-CliArg $fixtureMatrixArgs "-ComparisonReportPath" "`"$FixtureComparisonReportPath`""
Add-CliArg $fixtureMatrixArgs "-SummaryPath" "`"$FixtureMatrixSummaryPath`""
Add-CliValues $fixtureMatrixArgs "-ScenarioIds" $fixtureScenarioList
Add-CliValues $fixtureMatrixArgs "-ComparisonIds" $fixtureComparisonIds
if ($AllowRegression) { Add-CliArg $fixtureMatrixArgs "-AllowRegression" }
if ($AllowRuntimeAuditPreflight) { Add-CliArg $fixtureMatrixArgs "-AllowRuntimeAuditPreflight" }
if ($ForceFixtureEvidence) { Add-CliArg $fixtureMatrixArgs "-Force" }
if ($NoMarkdown) {
    Add-CliArg $fixtureMatrixArgs "-NoMarkdown"
}
else {
    Add-CliArg $fixtureMatrixArgs "-ComparisonMarkdownPath" "`"$FixtureComparisonMarkdownPath`""
}
if ($JsonOnly) { Add-CliArg $fixtureMatrixArgs "-JsonOnly" }

$liveArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $liveArgs "-BaseUrl" $base
Add-CliArg $liveArgs "-BundleDir" "`"$bundlePath`""
Add-CliArg $liveArgs "-ComparisonId" "candidate-external-engine-opt-in"
Add-CliValues $liveArgs "-ScenarioIds" $scenarioList
Add-CliArg $liveArgs "-IndexPath" "`"$LiveIndexPath`""
Add-CliArg $liveArgs "-ReportPath" "`"$LiveReportPath`""
Add-CliArg $liveArgs "-Samples" ([string]$Samples)
Add-CliArg $liveArgs "-IntervalMs" ([string]$IntervalMs)
Add-CliArg $liveArgs "-TimeoutSec" ([string]$TimeoutSec)
Add-CliArg $liveArgs "-ContinueOnError"
if ($SkipCertificateCheck) { Add-CliArg $liveArgs "-SkipCertificateCheck" }

$validateArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $validateArgs "-BundleDir" "`"$bundlePath`""
Add-CliArg $validateArgs "-RequireArtifactFiles"
Add-CliArg $validateArgs "-ReportPath" "`"$ValidationReportPath`""

$triageArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $triageArgs "-BundleDir" "`"$bundlePath`""
Add-CliArg $triageArgs "-ReportPath" "`"$TriageReportPath`""
Add-CliArg $triageArgs "-MarkdownPath" "`"$(Join-Path $bundlePath 'validation-triage-report.md')`""

$commandSteps = New-Object System.Collections.Generic.List[string]
if (-not $SkipFixtureComparison) {
    $commandSteps.Add((Format-Command -ScriptName "run-dsp-wdsp-fixture-matrix.ps1" -Arguments @($fixtureMatrixArgs.ToArray()))) | Out-Null
}
if (-not $SkipLiveMatrix) {
    $commandSteps.Add((Format-Command -ScriptName "run-dsp-live-diagnostics-matrix.ps1" -Arguments @($liveArgs.ToArray()))) | Out-Null
}
$commandSteps.Add((Format-Command -ScriptName "validate-dsp-modernization-bundle.ps1" -Arguments @($validateArgs.ToArray()))) | Out-Null
$commandSteps.Add((Format-Command -ScriptName "summarize-dsp-modernization-validation-report.ps1" -Arguments @($triageArgs.ToArray()))) | Out-Null

$expectedArtifacts = New-Object System.Collections.Generic.List[string]
if (-not $SkipFixtureComparison) {
    foreach ($artifactPath in @(
            $FixtureMetricsPath,
            $FixtureAudioIndexPath,
            $FixtureSpectrumIndexPath,
            $RuntimeAuditPath,
            $StageTimingReportPath,
            $FixtureComparisonReportPath,
            $FixtureMatrixSummaryPath
        )) {
        $expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $artifactPath)) | Out-Null
    }
    if (-not $NoMarkdown) {
        $expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $FixtureComparisonMarkdownPath)) | Out-Null
    }
}
if (-not $SkipLiveMatrix) {
    $expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $LiveIndexPath)) | Out-Null
    $expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $LiveReportPath)) | Out-Null
}
$expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $ValidationReportPath)) | Out-Null
$expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $TriageReportPath)) | Out-Null
$expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $ReportPath)) | Out-Null
if (-not $NoMarkdown) {
    $expectedArtifacts.Add((ConvertTo-PortablePath -Root $bundlePath -Path $MarkdownPath)) | Out-Null
}

$startedUtc = [DateTimeOffset]::UtcNow
$exitCodes = New-Object System.Collections.Generic.List[object]
$readyToExecute = $missingPrerequisites.Count -eq 0
$executed = $false
$status = if ($readyToExecute) { "ready" } else { "preflight-blocked" }

if ($Execute) {
    if ($missingPrerequisites.Count -gt 0 -and -not $ContinueOnError) {
        $status = "preflight-blocked"
    }
    else {
        $executed = $true
        $status = "running"
        $toolSpecs = New-Object System.Collections.Generic.List[object]
        if (-not $SkipFixtureComparison) {
            $toolSpecs.Add([ordered]@{ id = "fixture-matrix"; script = Join-Path $repoRoot "tools\run-dsp-wdsp-fixture-matrix.ps1"; args = @($fixtureMatrixArgs.ToArray()) }) | Out-Null
        }
        if (-not $SkipLiveMatrix) {
            $toolSpecs.Add([ordered]@{ id = "live-matrix"; script = Join-Path $repoRoot "tools\run-dsp-live-diagnostics-matrix.ps1"; args = @($liveArgs.ToArray()) }) | Out-Null
        }
        $toolSpecs.Add([ordered]@{ id = "strict-validation"; script = Join-Path $repoRoot "tools\validate-dsp-modernization-bundle.ps1"; args = @($validateArgs.ToArray()) }) | Out-Null
        $toolSpecs.Add([ordered]@{ id = "validation-triage"; script = Join-Path $repoRoot "tools\summarize-dsp-modernization-validation-report.ps1"; args = @($triageArgs.ToArray()) }) | Out-Null

        foreach ($tool in @($toolSpecs.ToArray())) {
            $exitCode = Invoke-ToolScript -ScriptPath ([string]$tool.script) -Arguments @($tool.args) -Quiet:$JsonOnly -AllowNonZeroExit:($ContinueOnError -or $AllowRegression)
            $exitCodes.Add([ordered]@{
                    id = [string]$tool.id
                    exitCode = $exitCode
                }) | Out-Null
            if ($exitCode -ne 0 -and -not $ContinueOnError -and -not $AllowRegression) {
                break
            }
        }

        $nonZeroExitCount = @($exitCodes.ToArray() | Where-Object { [int](Get-JsonValue $_ "exitCode") -ne 0 }).Count
        $status = if ($nonZeroExitCount -eq 0) { "succeeded" } else { "failed" }
    }
}

$completedUtc = [DateTimeOffset]::UtcNow
$nonZeroExitCountFinal = @($exitCodes.ToArray() | Where-Object { [int](Get-JsonValue $_ "exitCode") -ne 0 }).Count
$summary = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-external-engine-bakeoff"
    generatedUtc = $completedUtc
    startedUtc = $startedUtc
    completedUtc = $completedUtc
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    mode = if ($Execute) { "execute" } elseif ($PlanOnly) { "plan-only" } else { "plan" }
    status = $status
    bundleDir = $bundlePath
    candidateId = $candidate
    comparisonId = "candidate-external-engine-opt-in"
    scenarioIds = @($scenarioList)
    fixtureScenarioIds = @($fixtureScenarioList)
    fixtureComparisonIds = @($fixtureComparisonIds)
    baseUrl = $base
    samples = $Samples
    intervalMs = $IntervalMs
    timeoutSec = $TimeoutSec
    skipFixtureComparison = [bool]$SkipFixtureComparison
    skipLiveMatrix = [bool]$SkipLiveMatrix
    skipCertificateCheck = [bool]$SkipCertificateCheck
    allowRegression = [bool]$AllowRegression
    allowRuntimeAuditPreflight = [bool]$AllowRuntimeAuditPreflight
    forceFixtureEvidence = [bool]$ForceFixtureEvidence
    sourceValidationReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $ValidationReportPath
    sourceTriageReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $TriageReportPath
    sourceValidationReportPresent = $null -ne $validationReport
    sourceTriageReportPresent = $null -ne $triageReport
    sourceExternalBakeoffActionPresent = $externalActionPresent
    sourceExternalBakeoffReady = $validationReady
    readyToExecute = $readyToExecute
    executed = $executed
    exitCodes = @($exitCodes.ToArray())
    nonZeroExitCount = $nonZeroExitCountFinal
    missingPrerequisiteCount = $missingPrerequisites.Count
    missingPrerequisites = @($missingPrerequisites.ToArray())
    safetyPolicy = [ordered]@{
        optInOnly = $true
        postDemodOnly = $true
        rxOnly = $true
        defaultBehaviorChangeApproved = $false
        rawWdspIqAllowed = $false
        txPathAllowed = $false
        txMonitorAllowed = $false
        pureSignalAllowed = $false
        fallbackRequired = $true
    }
    benchmarkPlanPath = ConvertTo-PortablePath -Root $bundlePath -Path $BenchmarkPlanPath
    metricCatalogPath = if (Test-Path -LiteralPath $MetricCatalogPath -PathType Leaf) { ConvertTo-PortablePath -Root $bundlePath -Path $MetricCatalogPath } else { "" }
    fixtureMetricsPath = ConvertTo-PortablePath -Root $bundlePath -Path $FixtureMetricsPath
    fixtureAudioIndexPath = ConvertTo-PortablePath -Root $bundlePath -Path $FixtureAudioIndexPath
    fixtureSpectrumIndexPath = ConvertTo-PortablePath -Root $bundlePath -Path $FixtureSpectrumIndexPath
    runtimeAuditPath = ConvertTo-PortablePath -Root $bundlePath -Path $RuntimeAuditPath
    stageTimingReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $StageTimingReportPath
    fixtureComparisonReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $FixtureComparisonReportPath
    fixtureComparisonMarkdownPath = ConvertTo-PortablePath -Root $bundlePath -Path $FixtureComparisonMarkdownPath
    fixtureMatrixSummaryPath = ConvertTo-PortablePath -Root $bundlePath -Path $FixtureMatrixSummaryPath
    liveIndexPath = ConvertTo-PortablePath -Root $bundlePath -Path $LiveIndexPath
    liveReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $LiveReportPath
    reportPath = ConvertTo-PortablePath -Root $bundlePath -Path $ReportPath
    markdownPath = if ($NoMarkdown) { "" } else { ConvertTo-PortablePath -Root $bundlePath -Path $MarkdownPath }
    commandStepCount = $commandSteps.Count
    commandSteps = @($commandSteps.ToArray())
    expectedArtifactCount = $expectedArtifacts.Count
    expectedArtifacts = @($expectedArtifacts.ToArray())
    notes = @(
        "This tool plans or executes only the opt-in post-demod external DSP/ML bakeoff path.",
        "It does not integrate an external engine, route raw WDSP IQ, touch TX/PureSignal paths, or approve defaults.",
        "Fixture evidence is generated through the WDSP fixture matrix using a safe post-demod external bypass comparison.",
        "Use -Execute only after the operator has intentionally enabled the post-demod candidate path for live matrix capture."
    )
}

Write-JsonFile -Path $ReportPath -Value $summary
if (-not $NoMarkdown) {
    Set-Content -LiteralPath $MarkdownPath -Value (Build-MarkdownReport -Report $summary) -Encoding UTF8
}

if ($JsonOnly) {
    $summary | ConvertTo-Json -Depth 64
}
else {
    Write-Host "External DSP/ML bakeoff cycle summary: $ReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Status: $($summary.status), candidate: $($summary.candidateId), readyToExecute: $($summary.readyToExecute)"
}

if ($Execute -and $status -ne "succeeded") {
    exit 1
}
