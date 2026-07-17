param(
    [Parameter(Mandatory = $true)]
    [string]$ReferenceDir,

    [string]$CandidateDir = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [string]$ClassificationMapPath = "",

    [string[]]$Extensions = @(".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".inc", ".txt"),

    [string[]]$ExcludeDirectoryNames = @(".git", "bin", "build", "obj"),

    [switch]$FailOnLikelyDefect,

    [switch]$NoMarkdown,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Resolve-InputPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return (Resolve-Path -LiteralPath $Path).Path
    }

    return (Resolve-Path -LiteralPath (Join-Path (Get-Location).Path $Path)).Path
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

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse JSON file '$Path': $($_.Exception.Message)"
    }
}

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $Path
    }

    try {
        $rootFull = [System.IO.Path]::GetFullPath($Root)
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $rootFull = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        }

        $rootUri = [System.Uri]::new($rootFull)
        $pathUri = [System.Uri]::new($pathFull)
        $relative = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
        if (-not $relative.StartsWith("..")) {
            return ($relative -replace "\\", "/")
        }
    }
    catch {
        # Keep the original path if it cannot be normalized.
    }

    return $Path
}

function Get-RelativeSourcePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $rootFull = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    }

    $rootUri = [System.Uri]::new($rootFull)
    $pathUri = [System.Uri]::new($pathFull)
    $relative = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
    return ($relative -replace "\\", "/")
}

function Test-ExcludedPath {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [string[]]$ExcludedNames
    )

    $parts = @($RelativePath -split "/")
    foreach ($part in $parts) {
        foreach ($excluded in @($ExcludedNames)) {
            if ([string]::Equals($part, $excluded, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }

    return $false
}

function Test-SourceFile {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
        [string[]]$AllowedExtensions
    )

    if ([string]::Equals($File.Name, "CMakeLists.txt", [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    foreach ($extension in @($AllowedExtensions)) {
        if ([string]::Equals($File.Extension, $extension, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-SourceFileMap {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$AllowedExtensions,
        [string[]]$ExcludedNames
    )

    $map = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File)) {
        $relative = Get-RelativeSourcePath -Root $Root -Path $file.FullName
        if (Test-ExcludedPath -RelativePath $relative -ExcludedNames $ExcludedNames) {
            continue
        }
        if (-not (Test-SourceFile -File $file -AllowedExtensions $AllowedExtensions)) {
            continue
        }

        $map[$relative.ToLowerInvariant()] = [ordered]@{
            relativePath = $relative
            fullPath = $file.FullName
        }
    }

    return $map
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-TextSha256 {
    param([string]$Text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace "-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Read-Text {
    param([Parameter(Mandatory = $true)][string]$Path)

    $text = Get-Content -LiteralPath $Path -Raw
    if ($null -eq $text) {
        return ""
    }

    return [string]$text
}

function Normalize-LineEndings {
    param([string]$Text)

    if ($null -eq $Text) {
        return ""
    }

    return (($Text -replace "`r`n", "`n") -replace "`r", "`n")
}

function Normalize-Whitespace {
    param([string]$Text)

    return ((Normalize-LineEndings $Text) -replace "\s+", " ").Trim()
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

function Get-ClassificationRules {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @()
    }

    $resolved = Resolve-InputPath $Path
    $json = Read-JsonFile $resolved
    $rules = @(Get-JsonArray $json "rules")
    if ($rules.Count -eq 0 -and $json -is [System.Array]) {
        $rules = @($json)
    }

    return @($rules)
}

function Get-RuleClassification {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Status,
        $Rules
    )

    foreach ($rule in @($Rules)) {
        $pattern = [string](Get-JsonValue $rule "pattern")
        if ([string]::IsNullOrWhiteSpace($pattern)) {
            continue
        }

        $statusPattern = [string](Get-JsonValue $rule "status")
        if (-not [string]::IsNullOrWhiteSpace($statusPattern) -and
            -not ([string]::Equals($statusPattern, $Status, [StringComparison]::OrdinalIgnoreCase))) {
            continue
        }

        if ($RelativePath.ToLowerInvariant() -like $pattern.ToLowerInvariant()) {
            $category = [string](Get-JsonValue $rule "category")
            $rationale = [string](Get-JsonValue $rule "rationale")
            if (-not [string]::IsNullOrWhiteSpace($category)) {
                return [ordered]@{
                    category = $category
                    rationale = $rationale
                    source = "classification-map"
                }
            }
        }
    }

    return $null
}

function Get-DefaultClassification {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Status
    )

    $path = $RelativePath.ToLowerInvariant()
    if ($Status -eq "match" -or $Status -eq "line-ending-only") {
        return [ordered]@{
            category = "thetis-parity"
            rationale = "The normalized source content matches the reference."
            source = "built-in"
        }
    }

    if ($Status -eq "whitespace-only") {
        return [ordered]@{
            category = "thetis-parity"
            rationale = "Only whitespace changed after line-ending normalization; review before treating as behavior drift."
            source = "built-in"
        }
    }

    if ($path -like "cmakelists.txt" -or
        $path -like "*linux_port*" -or
        $path -like "*resource.h" -or
        $path -like "stubs/*" -or
        $path -like "*.vcxproj" -or
        $path -like "*.filters") {
        return [ordered]@{
            category = "port-build-support"
            rationale = "The path matches known build, platform, or stub support files."
            source = "built-in"
        }
    }

    if ($path -like "*candidate*" -or
        $path -like "*nr4*" -or
        $path -like "*sbnr*" -or
        $path -like "*candidate*" -or
        $path -like "*emnr*") {
        return [ordered]@{
            category = "experimental-enhancement"
            rationale = "The path matches experimental NR4/candidate DSP modernization work."
            source = "built-in"
        }
    }

    if ($path -like "*zeus*" -or $path -like "*policy*") {
        return [ordered]@{
            category = "zeus-product-policy"
            rationale = "The path appears Zeus-specific."
            source = "built-in"
        }
    }

    return [ordered]@{
        category = "likely-defect"
        rationale = "The delta touches WDSP source content and needs explicit Thetis parity classification before import, deletion, or refactor."
        source = "built-in"
    }
}

function New-DriftRecord {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [string]$ReferencePath = "",
        [string]$CandidatePath = "",
        $Rules
    )

    $referenceExists = -not [string]::IsNullOrWhiteSpace($ReferencePath)
    $candidateExists = -not [string]::IsNullOrWhiteSpace($CandidatePath)
    $status = "unknown"
    $referenceRawSha = ""
    $candidateRawSha = ""
    $referenceNormalizedSha = ""
    $candidateNormalizedSha = ""
    $referenceLineCount = 0
    $candidateLineCount = 0

    if ($referenceExists -and $candidateExists) {
        $referenceRawSha = Get-FileSha256 $ReferencePath
        $candidateRawSha = Get-FileSha256 $CandidatePath
        $referenceText = Read-Text $ReferencePath
        $candidateText = Read-Text $CandidatePath
        $referenceNormalized = Normalize-LineEndings $referenceText
        $candidateNormalized = Normalize-LineEndings $candidateText
        $referenceNormalizedSha = Get-TextSha256 $referenceNormalized
        $candidateNormalizedSha = Get-TextSha256 $candidateNormalized
        $referenceLineCount = if ([string]::IsNullOrEmpty($referenceNormalized)) { 0 } else { ($referenceNormalized -split "`n").Count }
        $candidateLineCount = if ([string]::IsNullOrEmpty($candidateNormalized)) { 0 } else { ($candidateNormalized -split "`n").Count }

        if ($referenceRawSha -eq $candidateRawSha) {
            $status = "match"
        }
        elseif ($referenceNormalizedSha -eq $candidateNormalizedSha) {
            $status = "line-ending-only"
        }
        elseif ((Get-TextSha256 (Normalize-Whitespace $referenceText)) -eq (Get-TextSha256 (Normalize-Whitespace $candidateText))) {
            $status = "whitespace-only"
        }
        else {
            $status = "content-drift"
        }
    }
    elseif ($referenceExists) {
        $status = "reference-only"
        $referenceRawSha = Get-FileSha256 $ReferencePath
        $referenceText = Read-Text $ReferencePath
        $referenceNormalized = Normalize-LineEndings $referenceText
        $referenceNormalizedSha = Get-TextSha256 $referenceNormalized
        $referenceLineCount = if ([string]::IsNullOrEmpty($referenceNormalized)) { 0 } else { ($referenceNormalized -split "`n").Count }
    }
    elseif ($candidateExists) {
        $status = "candidate-only"
        $candidateRawSha = Get-FileSha256 $CandidatePath
        $candidateText = Read-Text $CandidatePath
        $candidateNormalized = Normalize-LineEndings $candidateText
        $candidateNormalizedSha = Get-TextSha256 $candidateNormalized
        $candidateLineCount = if ([string]::IsNullOrEmpty($candidateNormalized)) { 0 } else { ($candidateNormalized -split "`n").Count }
    }

    $classification = Get-RuleClassification -RelativePath $RelativePath -Status $status -Rules $Rules
    if ($null -eq $classification) {
        $classification = Get-DefaultClassification -RelativePath $RelativePath -Status $status
    }

    return [ordered]@{
        relativePath = $RelativePath
        status = $status
        delta = ($status -ne "match" -and $status -ne "line-ending-only")
        referencePresent = $referenceExists
        candidatePresent = $candidateExists
        category = [string](Get-JsonValue $classification "category")
        classificationSource = [string](Get-JsonValue $classification "source")
        rationale = [string](Get-JsonValue $classification "rationale")
        referenceLineCount = $referenceLineCount
        candidateLineCount = $candidateLineCount
        referenceRawSha256 = $referenceRawSha
        candidateRawSha256 = $candidateRawSha
        referenceNormalizedSha256 = $referenceNormalizedSha
        candidateNormalizedSha256 = $candidateNormalizedSha
    }
}

function Convert-CountsToRecords {
    param(
        [hashtable]$Counts,
        [string]$NameField
    )

    return @($Counts.Keys | Sort-Object | ForEach-Object {
            [ordered]@{
                $NameField = [string]$_
                count = [int]$Counts[$_]
            }
        })
}

function Add-Count {
    param(
        [hashtable]$Counts,
        [string]$Key
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        $Key = "unknown"
    }
    if (-not $Counts.ContainsKey($Key)) {
        $Counts[$Key] = 0
    }
    $Counts[$Key] = [int]$Counts[$Key] + 1
}

function Format-MarkdownCell {
    param($Value)

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return ""
    }

    return (($text -replace "\r?\n", " ") -replace "\|", "\\|")
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# WDSP Source Drift Report") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $(Get-JsonValue $Report "readyForReview")") | Out-Null
    $lines.Add("- Reference: $(Format-MarkdownCell (Get-JsonValue $Report "referenceDir"))") | Out-Null
    $lines.Add("- Candidate: $(Format-MarkdownCell (Get-JsonValue $Report "candidateDir"))") | Out-Null
    $lines.Add("- Files: $(Get-JsonValue $Report "fileCount")") | Out-Null
    $lines.Add("- Deltas: $(Get-JsonValue $Report "deltaCount")") | Out-Null
    $lines.Add("- Likely defects: $(Get-JsonValue $Report "likelyDefectCount")") | Out-Null
    $lines.Add("") | Out-Null

    $statusCounts = @(Get-JsonArray $Report "statusCounts")
    if ($statusCounts.Count -gt 0) {
        $lines.Add("## Status Counts") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Status | Count |") | Out-Null
        $lines.Add("|---|---:|") | Out-Null
        foreach ($status in $statusCounts) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $status "status")) | $(Get-JsonValue $status "count") |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $categoryCounts = @(Get-JsonArray $Report "categoryCounts")
    if ($categoryCounts.Count -gt 0) {
        $lines.Add("## Category Counts") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Category | Count |") | Out-Null
        $lines.Add("|---|---:|") | Out-Null
        foreach ($category in $categoryCounts) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $category "category")) | $(Get-JsonValue $category "count") |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $deltaRecords = @(Get-JsonArray $Report "fileDrift" | Where-Object { [string](Get-JsonValue $_ "status") -ne "match" -and [string](Get-JsonValue $_ "status") -ne "line-ending-only" })
    if ($deltaRecords.Count -gt 0) {
        $lines.Add("## Drift Records") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Path | Status | Category | Lines Reference/Candidate | Rationale |") | Out-Null
        $lines.Add("|---|---|---|---:|---|") | Out-Null
        foreach ($record in $deltaRecords) {
            $lineText = "$(Get-JsonValue $record "referenceLineCount") / $(Get-JsonValue $record "candidateLineCount")"
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $record "relativePath")) | $(Format-MarkdownCell (Get-JsonValue $record "status")) | $(Format-MarkdownCell (Get-JsonValue $record "category")) | $lineText | $(Format-MarkdownCell (Get-JsonValue $record "rationale")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    return $lines -join [Environment]::NewLine
}

if ([string]::IsNullOrWhiteSpace($CandidateDir)) {
    $CandidateDir = Join-Path (Get-RepoRoot) "native/wdsp"
}

$referencePath = Resolve-InputPath $ReferenceDir
$candidatePath = Resolve-InputPath $CandidateDir

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = "wdsp-source-drift-report.json"
}

$resolvedReportPath = if ([System.IO.Path]::IsPathRooted($ReportPath)) {
    $ReportPath
}
else {
    Join-Path (Get-Location).Path $ReportPath
}

if ([string]::IsNullOrWhiteSpace($MarkdownPath) -and -not $NoMarkdown) {
    $MarkdownPath = [System.IO.Path]::ChangeExtension($ReportPath, ".md")
}

$resolvedMarkdownPath = if ($NoMarkdown) {
    ""
}
elseif ([System.IO.Path]::IsPathRooted($MarkdownPath)) {
    $MarkdownPath
}
else {
    Join-Path (Get-Location).Path $MarkdownPath
}

$rules = @(Get-ClassificationRules -Path $ClassificationMapPath)
$referenceFiles = Get-SourceFileMap -Root $referencePath -AllowedExtensions $Extensions -ExcludedNames $ExcludeDirectoryNames
$candidateFiles = Get-SourceFileMap -Root $candidatePath -AllowedExtensions $Extensions -ExcludedNames $ExcludeDirectoryNames

$allKeys = New-Object System.Collections.Generic.HashSet[string]
foreach ($key in @($referenceFiles.Keys)) {
    $null = $allKeys.Add($key)
}
foreach ($key in @($candidateFiles.Keys)) {
    $null = $allKeys.Add($key)
}

$records = New-Object System.Collections.Generic.List[object]
foreach ($key in @($allKeys | Sort-Object)) {
    $referenceRecord = $referenceFiles[$key]
    $candidateRecord = $candidateFiles[$key]
    $relativePath = if ($null -ne $candidateRecord) {
        [string](Get-JsonValue $candidateRecord "relativePath")
    }
    else {
        [string](Get-JsonValue $referenceRecord "relativePath")
    }

    $records.Add((New-DriftRecord `
                -RelativePath $relativePath `
                -ReferencePath ([string](Get-JsonValue $referenceRecord "fullPath")) `
                -CandidatePath ([string](Get-JsonValue $candidateRecord "fullPath")) `
                -Rules $rules)) | Out-Null
}

$statusCounts = @{}
$categoryCounts = @{}
foreach ($record in @($records.ToArray())) {
    Add-Count -Counts $statusCounts -Key ([string](Get-JsonValue $record "status"))
    Add-Count -Counts $categoryCounts -Key ([string](Get-JsonValue $record "category"))
}

$deltaRecords = @($records.ToArray() | Where-Object {
        [string](Get-JsonValue $_ "status") -ne "match" -and
        [string](Get-JsonValue $_ "status") -ne "line-ending-only"
    })
$likelyDefectRecords = @($records.ToArray() | Where-Object { [string](Get-JsonValue $_ "category") -eq "likely-defect" })

$report = [ordered]@{
    schemaVersion = 1
    tool = "compare-wdsp-source-drift"
    generatedUtc = [DateTimeOffset]::UtcNow
    normalizedLineEndings = $true
    referenceDir = $referencePath
    candidateDir = $candidatePath
    reportPath = ConvertTo-PortablePath -Root (Get-Location).Path -Path $resolvedReportPath
    markdownPath = if ($NoMarkdown) { $null } else { ConvertTo-PortablePath -Root (Get-Location).Path -Path $resolvedMarkdownPath }
    classificationMapPath = if ([string]::IsNullOrWhiteSpace($ClassificationMapPath)) { "" } else { ConvertTo-PortablePath -Root (Get-Location).Path -Path (Resolve-InputPath $ClassificationMapPath) }
    referenceFileCount = $referenceFiles.Count
    candidateFileCount = $candidateFiles.Count
    fileCount = $records.Count
    deltaCount = $deltaRecords.Count
    likelyDefectCount = $likelyDefectRecords.Count
    readyForReview = ($likelyDefectRecords.Count -eq 0)
    statusCounts = @(Convert-CountsToRecords -Counts $statusCounts -NameField "status")
    categoryCounts = @(Convert-CountsToRecords -Counts $categoryCounts -NameField "category")
    fileDrift = @($records.ToArray())
}

Write-JsonFile -Path $resolvedReportPath -Value $report

if (-not $NoMarkdown) {
    Set-Content -LiteralPath $resolvedMarkdownPath -Value (Build-MarkdownReport $report) -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 64
}
else {
    Write-Host "WDSP source drift report: $resolvedReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $resolvedMarkdownPath"
    }
    Write-Host "Files: $($records.Count), Deltas: $($deltaRecords.Count), Likely defects: $($likelyDefectRecords.Count)"
}

if ($FailOnLikelyDefect -and $likelyDefectRecords.Count -gt 0) {
    exit 1
}
