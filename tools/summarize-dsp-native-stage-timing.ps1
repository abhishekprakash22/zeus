param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDir,

    [string]$MetricsPath = "",

    [string]$ReportPath = "",

    [double]$MaxStageElapsedMs = 250.0,

    [double]$MaxRunElapsedMs = 1000.0,

    [long]$MaxManagedAllocationBytes = 104857600,

    [switch]$FailOnBudget,

    [switch]$Force,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse JSON file '$Path': $($_.Exception.Message)"
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 32
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
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
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

function Get-NumericValueOrDefault {
    param($Value, [double]$Default = 0.0)

    if ($null -eq $Value) {
        return $Default
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $Default
    }

    $parsed = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $Default
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

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
        return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Resolve-BundlePath {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $BundlePath $Path
}

function ConvertTo-PortablePath {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullBundle = [System.IO.Path]::GetFullPath($BundlePath).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith($fullBundle, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $fullPath.Substring($fullBundle.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        return ($relative -replace "\\", "/")
    }

    return ($fullPath -replace "\\", "/")
}

function ConvertTo-FixtureEvidenceEngine {
    param($Metrics)

    $engine = [string](Get-JsonValue $Metrics "evidenceEngine")
    if (-not [string]::IsNullOrWhiteSpace($engine)) {
        return $engine
    }

    $tool = [string](Get-JsonValue $Metrics "tool")
    switch ($tool) {
        "dsp-fixture-evidence" { return "wdsp" }
        "run-dsp-wdsp-fixture-evidence" { return "wdsp" }
        "run-dsp-offline-fixture-evidence" { return "deterministic-synthetic" }
        default { return "" }
    }
}

function Add-StageBucket {
    param(
        [hashtable]$Buckets,
        [string]$ScenarioId,
        [string]$ComparisonId,
        $Stage
    )

    $stageId = [string](Get-JsonValue $Stage "stageId")
    if ([string]::IsNullOrWhiteSpace($stageId)) {
        $stageId = "unknown-stage"
    }

    $elapsedMs = [double](Get-NumericValueOrDefault (Get-JsonValue $Stage "elapsedMs"))
    $allocatedBytes = [long](Get-NumericValueOrDefault (Get-JsonValue $Stage "managedAllocatedBytes"))

    if (-not $Buckets.ContainsKey($stageId)) {
        $Buckets[$stageId] = [ordered]@{
            stageId = $stageId
            label = [string](Get-JsonValue $Stage "label")
            sampleCount = 0
            totalElapsedMs = 0.0
            maxElapsedMs = 0.0
            maxElapsedScenarioId = ""
            maxElapsedComparisonId = ""
            totalManagedAllocatedBytes = 0
            maxManagedAllocatedBytes = 0
            maxManagedAllocationScenarioId = ""
            maxManagedAllocationComparisonId = ""
        }
    }

    $bucket = $Buckets[$stageId]
    $bucket["sampleCount"] = [int]$bucket["sampleCount"] + 1
    $bucket["totalElapsedMs"] = [double]$bucket["totalElapsedMs"] + $elapsedMs
    $bucket["totalManagedAllocatedBytes"] = [long]$bucket["totalManagedAllocatedBytes"] + $allocatedBytes

    if ($elapsedMs -gt [double]$bucket["maxElapsedMs"]) {
        $bucket["maxElapsedMs"] = $elapsedMs
        $bucket["maxElapsedScenarioId"] = $ScenarioId
        $bucket["maxElapsedComparisonId"] = $ComparisonId
    }

    if ($allocatedBytes -gt [long]$bucket["maxManagedAllocatedBytes"]) {
        $bucket["maxManagedAllocatedBytes"] = $allocatedBytes
        $bucket["maxManagedAllocationScenarioId"] = $ScenarioId
        $bucket["maxManagedAllocationComparisonId"] = $ComparisonId
    }
}

$bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
if ([string]::IsNullOrWhiteSpace($MetricsPath)) {
    $MetricsPath = Join-Path $bundlePath "artifacts\offline-fixture-metrics.json"
}
else {
    $MetricsPath = Resolve-BundlePath $bundlePath $MetricsPath
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $bundlePath "artifacts\native-stage-timing-report.json"
}
else {
    $ReportPath = Resolve-BundlePath $bundlePath $ReportPath
}

if (-not (Test-Path -LiteralPath $MetricsPath -PathType Leaf)) {
    throw "Offline fixture metrics file not found: $MetricsPath"
}

if ((Test-Path -LiteralPath $ReportPath -PathType Leaf) -and -not $Force) {
    throw "Report already exists: $ReportPath (use -Force to overwrite)"
}

$metrics = Read-JsonFile $MetricsPath
$metricsSha256 = Get-FileSha256 $MetricsPath
$evidenceEngine = ConvertTo-FixtureEvidenceEngine $metrics
$wdspBacked = [string]::Equals($evidenceEngine, "wdsp", [System.StringComparison]::OrdinalIgnoreCase)

$stageBuckets = @{}
$runSummaries = New-Object System.Collections.Generic.List[object]
$scenarioIds = New-Object System.Collections.Generic.HashSet[string]
$comparisonIds = New-Object System.Collections.Generic.HashSet[string]
$missingStageRunCount = 0
$missingAllocationRunCount = 0
$budgetFailureCount = 0
$stageRecordCount = 0
$maxRunElapsedMsActual = 0.0
$maxStageElapsedMsActual = 0.0
$maxManagedAllocationBytesActual = 0L

foreach ($scenario in @(Get-JsonArray $metrics "scenarios")) {
    $scenarioId = [string](Get-JsonValue $scenario "scenarioId")
    if (-not [string]::IsNullOrWhiteSpace($scenarioId)) {
        [void]$scenarioIds.Add($scenarioId)
    }

    foreach ($comparison in @(Get-JsonArray $scenario "comparisons")) {
        $comparisonId = [string](Get-JsonValue $comparison "comparisonId")
        if (-not [string]::IsNullOrWhiteSpace($comparisonId)) {
            [void]$comparisonIds.Add($comparisonId)
        }

        $timing = Get-JsonValue $comparison "nativeStageTiming"
        $allocation = Get-JsonValue $comparison "managedAllocation"
        $stages = @(Get-JsonArray $timing "stages")
        $stageTimingReady = ($null -ne $timing -and $stages.Count -gt 0)
        $allocationReady = ($null -ne $allocation -and -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $allocation "nativeAllocationProbeStatus")))

        if (-not $stageTimingReady) {
            $missingStageRunCount++
        }
        if (-not $allocationReady) {
            $missingAllocationRunCount++
        }

        $runProcessingElapsedMs = [double](Get-NumericValueOrDefault (Get-JsonValue (Get-JsonValue $comparison "metrics") "processing elapsed ms"))
        $runTotalStageElapsedMs = [double](Get-NumericValueOrDefault (Get-JsonValue $timing "totalStageElapsedMs"))
        $runMaxStageElapsedMs = [double](Get-NumericValueOrDefault (Get-JsonValue $timing "maxStageElapsedMs"))
        $runTotalAllocatedBytes = [long](Get-NumericValueOrDefault (Get-JsonValue $allocation "totalManagedAllocatedBytes"))
        if ($runTotalAllocatedBytes -le 0) {
            $runTotalAllocatedBytes = [long](Get-NumericValueOrDefault (Get-JsonValue $timing "totalManagedAllocatedBytes"))
        }

        $maxRunElapsedMsActual = [Math]::Max($maxRunElapsedMsActual, [Math]::Max($runProcessingElapsedMs, $runTotalStageElapsedMs))
        $maxStageElapsedMsActual = [Math]::Max($maxStageElapsedMsActual, $runMaxStageElapsedMs)
        $maxManagedAllocationBytesActual = [Math]::Max($maxManagedAllocationBytesActual, $runTotalAllocatedBytes)

        foreach ($stage in $stages) {
            $stageRecordCount++
            Add-StageBucket -Buckets $stageBuckets -ScenarioId $scenarioId -ComparisonId $comparisonId -Stage $stage
        }

        $budgetFailures = @()
        if ($runMaxStageElapsedMs -gt $MaxStageElapsedMs) {
            $budgetFailures += "max-stage-elapsed"
        }
        if ([Math]::Max($runProcessingElapsedMs, $runTotalStageElapsedMs) -gt $MaxRunElapsedMs) {
            $budgetFailures += "run-elapsed"
        }
        if ($runTotalAllocatedBytes -gt $MaxManagedAllocationBytes) {
            $budgetFailures += "managed-allocation"
        }
        if ($budgetFailures.Count -gt 0) {
            $budgetFailureCount++
        }

        $runSummaries.Add([ordered]@{
            scenarioId = $scenarioId
            comparisonId = $comparisonId
            stageTimingReady = $stageTimingReady
            allocationProbeReady = $allocationReady
            processingElapsedMs = [Math]::Round($runProcessingElapsedMs, 6)
            totalStageElapsedMs = [Math]::Round($runTotalStageElapsedMs, 6)
            maxStageElapsedMs = [Math]::Round($runMaxStageElapsedMs, 6)
            totalManagedAllocatedBytes = $runTotalAllocatedBytes
            nativeCStageInstrumentationStatus = [string](Get-JsonValue $timing "nativeCStageInstrumentationStatus")
            nativeAllocationProbeStatus = [string](Get-JsonValue $timing "nativeAllocationProbeStatus")
            budgetFailureCount = $budgetFailures.Count
            budgetFailures = @($budgetFailures)
        }) | Out-Null
    }
}

$readyForReview = ($wdspBacked -and
    $runSummaries.Count -gt 0 -and
    $missingStageRunCount -eq 0 -and
    $missingAllocationRunCount -eq 0 -and
    $budgetFailureCount -eq 0)

$status = if (-not $wdspBacked) {
    "not-wdsp-backed"
}
elseif ($runSummaries.Count -eq 0) {
    "no-fixture-runs"
}
elseif ($missingStageRunCount -gt 0) {
    "missing-stage-timing"
}
elseif ($missingAllocationRunCount -gt 0) {
    "missing-allocation-probe"
}
elseif ($budgetFailureCount -gt 0) {
    "budget-failures"
}
else {
    "managed-wrapper-ready-native-c-pending"
}

$stageSummaries = @($stageBuckets.Values | Sort-Object -Property stageId | ForEach-Object {
    [ordered]@{
        stageId = [string]$_["stageId"]
        label = [string]$_["label"]
        sampleCount = [int]$_["sampleCount"]
        totalElapsedMs = [Math]::Round([double]$_["totalElapsedMs"], 6)
        averageElapsedMs = if ([int]$_["sampleCount"] -gt 0) { [Math]::Round([double]$_["totalElapsedMs"] / [int]$_["sampleCount"], 6) } else { 0.0 }
        maxElapsedMs = [Math]::Round([double]$_["maxElapsedMs"], 6)
        maxElapsedScenarioId = [string]$_["maxElapsedScenarioId"]
        maxElapsedComparisonId = [string]$_["maxElapsedComparisonId"]
        totalManagedAllocatedBytes = [long]$_["totalManagedAllocatedBytes"]
        maxManagedAllocatedBytes = [long]$_["maxManagedAllocatedBytes"]
        maxManagedAllocationScenarioId = [string]$_["maxManagedAllocationScenarioId"]
        maxManagedAllocationComparisonId = [string]$_["maxManagedAllocationComparisonId"]
    }
})

$report = [ordered]@{
    schemaVersion = 1
    tool = "summarize-dsp-native-stage-timing"
    generatedUtc = [DateTimeOffset]::UtcNow
    metricsPath = ConvertTo-PortablePath -BundlePath $bundlePath -Path $MetricsPath
    metricsSha256 = $metricsSha256
    evidenceEngine = $evidenceEngine
    wdspBackedEvidence = $wdspBacked
    wdspRuntimeRid = [string](Get-JsonValue $metrics "wdspRuntimeRid")
    wdspRuntimeSha256 = [string](Get-JsonValue $metrics "wdspRuntimeSha256")
    wdspRuntimeStatus = [string](Get-JsonValue $metrics "wdspRuntimeStatus")
    readyForReview = $readyForReview
    status = $status
    scenarioCount = $scenarioIds.Count
    scenarioIds = @($scenarioIds | Sort-Object)
    comparisonCount = $comparisonIds.Count
    comparisonIds = @($comparisonIds | Sort-Object)
    runCount = $runSummaries.Count
    stageRecordCount = $stageRecordCount
    missingStageTimingRunCount = $missingStageRunCount
    missingAllocationProbeRunCount = $missingAllocationRunCount
    budgetFailureCount = $budgetFailureCount
    maxStageElapsedMs = [Math]::Round($maxStageElapsedMsActual, 6)
    maxRunElapsedMs = [Math]::Round($maxRunElapsedMsActual, 6)
    maxManagedAllocationBytes = $maxManagedAllocationBytesActual
    budget = [ordered]@{
        maxStageElapsedMs = $MaxStageElapsedMs
        maxRunElapsedMs = $MaxRunElapsedMs
        maxManagedAllocationBytes = $MaxManagedAllocationBytes
    }
    nativeCStageInstrumentationReady = $false
    nativeCStageInstrumentationStatus = "not-instrumented"
    nativeAllocationProbeStatus = "managed-thread-delta-only"
    stageSummaries = @($stageSummaries)
    runSummaries = @($runSummaries.ToArray())
    limitations = @(
        "This report summarizes WDSP fixture wrapper stage timing and managed allocation deltas.",
        "It does not prove internal native C per-block timing or native heap allocation until WDSP exposes allocator/stage probes.",
        "It is offline fixture evidence only; G2 live, TX/PureSignal, and cross-radio validation remain separate gates."
    )
}

Write-JsonFile -Path $ReportPath -Value $report

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 32
}
else {
    Write-Host "DSP native stage timing report written."
    Write-Host "Report: $ReportPath"
    Write-Host "Status: $status"
    Write-Host "Ready for review: $readyForReview"
}

if ($FailOnBudget -and -not $readyForReview) {
    exit 1
}
