param(
    [string]$NativeMethodsPath = "",

    [string]$NativeSourceRoot = "",

    [string]$BinaryPath = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [switch]$RequireBinaryExports,

    [switch]$FailOnMissingRequired,

    [switch]$NoMarkdown,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Read-TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $text = Get-Content -LiteralPath $Path -Raw
    if ($null -eq $text) {
        return ""
    }
    return $text
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

    $json = $Value | ConvertTo-Json -Depth 48
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

function ConvertTo-SafeFileName {
    param([string]$Value)

    $safe = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9._-]+", "-"
    $safe = $safe.Trim("-")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "wdsp-native-symbol-audit"
    }
    return $safe
}

function Get-LineNumber {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [int]$Index
    )

    if ($Index -le 0) {
        return 1
    }

    return 1 + ([regex]::Matches($Text.Substring(0, $Index), "`n")).Count
}

function Remove-CComments {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    $withoutBlock = [regex]::Replace(
        $Text,
        "(?s)/\*.*?\*/",
        {
            param($match)
            return ("`n" * ([regex]::Matches($match.Value, "`n")).Count)
        })

    return [regex]::Replace($withoutBlock, "(?m)//.*$", "")
}

function Get-ParameterList {
    param([string]$Parameters)

    if ([string]::IsNullOrWhiteSpace($Parameters)) {
        return @()
    }

    $clean = $Parameters.Trim()
    if ($clean -eq "void") {
        return @()
    }

    $items = New-Object System.Collections.Generic.List[object]
    foreach ($raw in ($clean -split ",")) {
        $value = $raw.Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        $tokens = @($value -split "\s+" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $name = ""
        $type = $value
        if ($tokens.Count -gt 1) {
            $name = $tokens[$tokens.Count - 1].Trim()
            $type = ($tokens[0..($tokens.Count - 2)] -join " ").Trim()
        }

        $items.Add([ordered]@{
            raw = $value
            type = $type
            name = $name
        }) | Out-Null
    }

    return @($items.ToArray())
}

function Parse-NativeMethods {
    param([Parameter(Mandatory = $true)][string]$Path)

    $text = Read-TextFile $Path
    $pattern = "(?ms)(?<attrs>(?:\s*\[[^\r\n]+\]\s*)+)internal\s+static\s+partial\s+(?<return>[A-Za-z0-9_<>\[\]\.\?]+)\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>.*?)\);"
    $matches = [regex]::Matches($text, $pattern)
    $imports = New-Object System.Collections.Generic.List[object]

    foreach ($match in $matches) {
        $attrs = [string]$match.Groups["attrs"].Value
        if ($attrs -notmatch "LibraryImport\s*\(\s*LibraryName") {
            continue
        }

        $method = [string]$match.Groups["method"].Value
        $entryPoint = $null
        $entryMatch = [regex]::Match($attrs, 'EntryPoint\s*=\s*"(?<entry>[^"]+)"')
        if ($entryMatch.Success) {
            $entryPoint = [string]$entryMatch.Groups["entry"].Value
        }

        $parameters = Get-ParameterList ([string]$match.Groups["params"].Value)
        $symbol = if ([string]::IsNullOrWhiteSpace($entryPoint)) { $method } else { $entryPoint }
        $imports.Add([pscustomobject][ordered]@{
            managedName = $method
            symbolName = $symbol
            entryPointOverride = $entryPoint
            returnType = [string]$match.Groups["return"].Value
            parameterCount = @($parameters).Count
            parameters = @($parameters)
            category = Get-SymbolCategory $method $symbol
            criticality = Get-SymbolCriticality $method $symbol
            nativeMethodsLine = Get-LineNumber $text $match.Index
        }) | Out-Null
    }

    return @($imports.ToArray())
}

function Get-SymbolCategory {
    param(
        [string]$ManagedName,
        [string]$SymbolName
    )

    $name = if ([string]::IsNullOrWhiteSpace($SymbolName)) { $ManagedName } else { $SymbolName }
    if ($name -in @("OpenChannel", "CloseChannel", "SetChannelState", "SetInputSamplerate", "SetOutputSamplerate", "fexchange0", "fexchange2")) {
        return "channel-lifecycle"
    }
    if ($name -match "^(XCreateAnalyzer|DestroyAnalyzer|SetAnalyzer|Spectrum0|GetPixels|SetDisplay)") {
        return "analyzer-display"
    }
    if ($name -match "^(SetRXA(?:ANR|ANF|EMNR|SBNR|SNB))") {
        return "rx-noise-reduction"
    }
    if ($name -match "^(CreateAnbEXT|DestroyAnbEXT|FlushAnbEXT|XanbEXT|CreateNobEXT|DestroyNobEXT|FlushNobEXT|XnobEXT|SetEXTNOB)") {
        return "external-noise-blanker"
    }
    if ($name -match "^(SetPS|GetPS|psccF|PSSave|PSRestore)") {
        return "puresignal"
    }
    if ($name -match "^(SetTXA|GetTXA|TXA)") {
        return "txa"
    }
    if ($name -match "^(SetRXA|GetRXA|RXA)") {
        return "rxa"
    }
    if ($name -match "wisdom|WDSPwisdom") {
        return "fftw-wisdom"
    }
    return "wdsp-core"
}

function Get-SymbolCriticality {
    param(
        [string]$ManagedName,
        [string]$SymbolName
    )

    $name = if ([string]::IsNullOrWhiteSpace($SymbolName)) { $ManagedName } else { $SymbolName }
    if ($name -match "^(SetRXASBNR)") {
        return "experimental-required"
    }
    if ($name -match "^(SetRXAEMNR|SetRXAANR|SetRXAANF)") {
        return "thetis-parity"
    }
    if ($name -match "^(OpenChannel|CloseChannel|SetChannelState|fexchange0|fexchange2|SetRXAMode|SetTXAMode|SetRXABandpass|RXANBP|SetRXASNBA|SetRXAAGC|GetRXAMeter|GetTXAMeter)") {
        return "hard-required"
    }
    if ($name -match "^(SetPS|GetPS|psccF|PSSave|PSRestore)") {
        return "tx-safety-required"
    }
    return "required"
}

function Get-NativeSourceHits {
    param(
        [object[]]$Imports,
        [Parameter(Mandatory = $true)][string]$SourceRoot
    )

    $result = @{}
    foreach ($import in @($Imports)) {
        $result[[string]$import.symbolName] = New-Object System.Collections.Generic.List[object]
    }

    $sourceFiles = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -Include *.c,*.h |
        Where-Object { $_.FullName -notmatch "\\build" })

    foreach ($file in $sourceFiles) {
        $text = Read-TextFile $file.FullName
        $signatureText = Remove-CComments $text
        foreach ($import in @($Imports)) {
            $symbol = [string]$import.symbolName
            if (-not $text.Contains($symbol)) {
                continue
            }

            $escaped = [regex]::Escape($symbol)
            $signaturePattern = "(?ms)(?:^|[\r\n])\s*(?:(?:extern\s+)?(?:PORT|AGCPORT|WDSP_EXPORT|__declspec\s*\([^)]*\))\s+)?(?<return>[A-Za-z_][A-Za-z0-9_\s\*\[\]]*?)\s+$escaped\s*\((?<params>[^;{}]*?)\)\s*(?<kind>[;{])"
            $signature = [regex]::Match($signatureText, $signaturePattern)
            if ($signature.Success) {
                $params = Get-ParameterList ([string]$signature.Groups["params"].Value)
                $result[$symbol].Add([pscustomobject][ordered]@{
                    file = Resolve-RelativePath $file.FullName
                    line = Get-LineNumber $signatureText $signature.Index
                    kind = if ([string]$signature.Groups["kind"].Value -eq "{") { "definition" } else { "prototype" }
                    returnType = ([string]$signature.Groups["return"].Value).Trim()
                    parameterCount = @($params).Count
                    parameters = @($params)
                }) | Out-Null
                continue
            }

            $mention = [regex]::Match($text, "(?m)\b$escaped\s*\(")
            if ($mention.Success) {
                $result[$symbol].Add([pscustomobject][ordered]@{
                    file = Resolve-RelativePath $file.FullName
                    line = Get-LineNumber $text $mention.Index
                    kind = "mention"
                    returnType = $null
                    parameterCount = $null
                    parameters = @()
                }) | Out-Null
            }
        }
    }

    return $result
}

function Resolve-RelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $root = (Get-RepoRoot).TrimEnd("\", "/")
    $full = (Resolve-Path -LiteralPath $Path).Path
    if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($root.Length).TrimStart("\", "/") -replace "\\", "/"
    }
    return $full
}

function Resolve-WdspBinaryPath {
    param([string]$Requested)

    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        return (Resolve-Path -LiteralPath $Requested).Path
    }

    $repo = Get-RepoRoot
    $candidates = @(
        "native/build-candidate-live-ninja/wdsp.dll",
        "OpenhpsdrZeus/bin/Debug/net10.0/runtimes/win-x64/native/wdsp.dll",
        "OpenhpsdrZeus/bin/Release/net10.0/runtimes/win-x64/native/wdsp.dll",
        "OpenhpsdrZeus/bin/Debug/net10.0/runtimes/linux-x64/native/libwdsp.so",
        "OpenhpsdrZeus/bin/Release/net10.0/runtimes/linux-x64/native/libwdsp.so",
        "OpenhpsdrZeus/bin/Debug/net10.0/runtimes/osx-arm64/native/libwdsp.dylib",
        "OpenhpsdrZeus/bin/Release/net10.0/runtimes/osx-arm64/native/libwdsp.dylib"
    )

    foreach ($candidate in $candidates) {
        $path = Join-Path $repo $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    return $null
}

function Get-PEExportNames {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        function ReadU16At([int64]$Offset) {
            $null = $stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
            return $reader.ReadUInt16()
        }
        function ReadU32At([int64]$Offset) {
            $null = $stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
            return $reader.ReadUInt32()
        }
        function ReadBytesAt([int64]$Offset, [int]$Count) {
            $null = $stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
            return $reader.ReadBytes($Count)
        }
        function ReadAsciiZAt([int64]$Offset) {
            $null = $stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
            $bytes = New-Object System.Collections.Generic.List[byte]
            while ($stream.Position -lt $stream.Length) {
                $b = $reader.ReadByte()
                if ($b -eq 0) {
                    break
                }
                $bytes.Add($b) | Out-Null
            }
            return [Text.Encoding]::ASCII.GetString($bytes.ToArray())
        }

        if ($stream.Length -lt 0x100 -or [Text.Encoding]::ASCII.GetString((ReadBytesAt 0 2)) -ne "MZ") {
            throw "Not a PE image: $Path"
        }

        $peOffset = [int](ReadU32At 0x3C)
        if ($peOffset -le 0 -or $peOffset + 0x18 -ge $stream.Length) {
            throw "Invalid PE header offset in $Path"
        }
        if ([Text.Encoding]::ASCII.GetString((ReadBytesAt $peOffset 4)) -ne "PE`0`0") {
            throw "Invalid PE signature in $Path"
        }

        $numberOfSections = [int](ReadU16At ($peOffset + 6))
        $optionalHeaderSize = [int](ReadU16At ($peOffset + 20))
        $optionalHeader = $peOffset + 24
        $magic = ReadU16At $optionalHeader
        $dataDirectory = if ($magic -eq 0x20B) { $optionalHeader + 112 } elseif ($magic -eq 0x10B) { $optionalHeader + 96 } else { throw "Unsupported PE optional header magic 0x$($magic.ToString("X")) in $Path" }
        $exportRva = [int](ReadU32At $dataDirectory)
        if ($exportRva -eq 0) {
            return @()
        }

        $sections = New-Object System.Collections.Generic.List[object]
        $sectionOffset = $optionalHeader + $optionalHeaderSize
        for ($i = 0; $i -lt $numberOfSections; $i++) {
            $offset = $sectionOffset + ($i * 40)
            if ($offset + 40 -gt $stream.Length) {
                break
            }
            $sections.Add([pscustomobject][ordered]@{
                virtualSize = [int](ReadU32At ($offset + 8))
                virtualAddress = [int](ReadU32At ($offset + 12))
                rawSize = [int](ReadU32At ($offset + 16))
                rawPointer = [int](ReadU32At ($offset + 20))
            }) | Out-Null
        }

        function Convert-RvaToOffset([int64]$Rva) {
            foreach ($section in $sections.ToArray()) {
                $start = [int64]$section.virtualAddress
                $length = [Math]::Max([int64]$section.virtualSize, [int64]$section.rawSize)
                if ($Rva -ge $start -and $Rva -lt ($start + $length)) {
                    return ([int64]$section.rawPointer + ($Rva - $start))
                }
            }
            return $Rva
        }

        $exportOffset = Convert-RvaToOffset $exportRva
        if ($exportOffset + 40 -gt $stream.Length) {
            throw "Invalid PE export directory in $Path"
        }

        $numberOfNames = [int](ReadU32At ($exportOffset + 24))
        $addressOfNamesRva = [int](ReadU32At ($exportOffset + 32))
        $addressOfNamesOffset = Convert-RvaToOffset $addressOfNamesRva

        $names = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $numberOfNames; $i++) {
            $nameRva = [int](ReadU32At ($addressOfNamesOffset + ($i * 4)))
            $nameOffset = Convert-RvaToOffset $nameRva
            if ($nameOffset -ge 0 -and $nameOffset -lt $stream.Length) {
                $names.Add((ReadAsciiZAt $nameOffset)) | Out-Null
            }
        }

        return @($names.ToArray() | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-ToolExportNames {
    param([Parameter(Mandatory = $true)][string]$Path)

    $tools = @("llvm-nm", "nm", "llvm-objdump", "objdump", "dumpbin")
    foreach ($tool in $tools) {
        $command = Get-Command $tool -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            continue
        }

        try {
            if ($tool -eq "dumpbin") {
                $output = & $command.Source /exports $Path 2>$null
            }
            elseif ($tool -match "objdump") {
                $output = & $command.Source -T $Path 2>$null
            }
            else {
                $output = & $command.Source -D --defined-only $Path 2>$null
            }
            if ($LASTEXITCODE -ne 0 -or $null -eq $output) {
                continue
            }

            $names = New-Object System.Collections.Generic.List[string]
            foreach ($line in @($output)) {
                foreach ($token in ($line -split "\s+")) {
                    if ($token -match "^[A-Za-z_][A-Za-z0-9_]*$" -and $token -notin @("Name", "ordinal", "hint", "RVA", "Type")) {
                        $names.Add($token) | Out-Null
                    }
                }
            }
            if ($names.Count -gt 0) {
                return [pscustomobject][ordered]@{
                    tool = $tool
                    names = @($names.ToArray() | Sort-Object -Unique)
                }
            }
        }
        catch {
            continue
        }
    }

    return $null
}

function Get-BinaryExportNames {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject][ordered]@{
            evaluated = $false
            status = "binary-not-found"
            tool = $null
            names = @()
            error = $null
        }
    }

    $extension = [IO.Path]::GetExtension($Path)
    if ($extension -ieq ".dll") {
        try {
            return [pscustomobject][ordered]@{
                evaluated = $true
                status = "ready"
                tool = "pe-export-table"
                names = @(Get-PEExportNames $Path)
                error = $null
            }
        }
        catch {
            return [pscustomobject][ordered]@{
                evaluated = $false
                status = "pe-export-parse-failed"
                tool = "pe-export-table"
                names = @()
                error = $_.Exception.Message
            }
        }
    }

    $toolResult = Get-ToolExportNames $Path
    if ($null -ne $toolResult) {
        return [pscustomobject][ordered]@{
            evaluated = $true
            status = "ready"
            tool = $toolResult.tool
            names = @($toolResult.names)
            error = $null
        }
    }

    return [pscustomobject][ordered]@{
        evaluated = $false
        status = "export-tool-unavailable"
        tool = $null
        names = @()
        error = "No export parser was available for '$Path'. PE DLLs are parsed directly; ELF/Mach-O need nm/objdump."
    }
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# WDSP Native Symbol Audit") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $($Report.readyForReview)") | Out-Null
    $lines.Add("- Imported symbols: $($Report.importedSymbolCount)") | Out-Null
    $lines.Add("- Source missing required: $($Report.sourceMissingRequiredCount)") | Out-Null
    $lines.Add("- Signature mismatches: $($Report.signatureMismatchCount)") | Out-Null
    $lines.Add("- Binary exports evaluated: $($Report.binaryExportsEvaluated)") | Out-Null
    $lines.Add("- Binary missing required: $($Report.binaryMissingRequiredCount)") | Out-Null
    $lines.Add("") | Out-Null

    $problems = @($Report.symbols | Where-Object { (-not $_.sourcePresent) -or $_.signatureMatches -eq $false -or $_.binaryPresent -eq $false })
    if ($problems.Count -gt 0) {
        $lines.Add("## Findings") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Symbol | Category | Criticality | Source | Signature | Binary |") | Out-Null
        $lines.Add("|---|---|---|---|---|---|") | Out-Null
        foreach ($item in $problems) {
            $source = if ($item.sourcePresent) { "ok" } else { "missing" }
            $signature = if ($null -eq $item.signatureMatches) { "n/a" } elseif ($item.signatureMatches) { "ok" } else { "mismatch" }
            $binary = if ($null -eq $item.binaryPresent) { "not evaluated" } elseif ($item.binaryPresent) { "ok" } else { "missing" }
            $lines.Add("| $($item.symbolName) | $($item.category) | $($item.criticality) | $source | $signature | $binary |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $lines.Add("## Category Summary") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Category | Imported | Source missing | Binary missing | Signature mismatches |") | Out-Null
    $lines.Add("|---|---:|---:|---:|---:|") | Out-Null
    foreach ($item in @($Report.categorySummary)) {
        $lines.Add("| $($item.category) | $($item.importedCount) | $($item.sourceMissingCount) | $($item.binaryMissingCount) | $($item.signatureMismatchCount) |") | Out-Null
    }

    return @($lines.ToArray())
}

$repoRoot = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($NativeMethodsPath)) {
    $NativeMethodsPath = Join-Path $repoRoot "Zeus.Dsp\Wdsp\NativeMethods.cs"
}
if ([string]::IsNullOrWhiteSpace($NativeSourceRoot)) {
    $NativeSourceRoot = Join-Path $repoRoot "native\wdsp"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repoRoot "captures\dsp-modernization\wdsp-native-symbol-audit.json"
}
if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$nativeMethodsResolved = (Resolve-Path -LiteralPath $NativeMethodsPath).Path
$nativeSourceResolved = (Resolve-Path -LiteralPath $NativeSourceRoot).Path
$binaryResolved = Resolve-WdspBinaryPath $BinaryPath

$imports = @(Parse-NativeMethods $nativeMethodsResolved)
$sourceHits = Get-NativeSourceHits -Imports $imports -SourceRoot $nativeSourceResolved
$binaryExports = Get-BinaryExportNames $binaryResolved
$exportSet = @{}
foreach ($name in @($binaryExports.names)) {
    $exportSet[[string]$name] = $true
}

$symbols = New-Object System.Collections.Generic.List[object]
$sourceMissingRequiredCount = 0
$signatureMismatchCount = 0
$binaryMissingRequiredCount = 0
$categorySummaryMap = @{}

foreach ($import in @($imports | Sort-Object symbolName)) {
    $symbol = [string]$import.symbolName
    $hits = @($sourceHits[$symbol].ToArray())
    $bestHit = $null
    if ($hits.Count -gt 0) {
        $definitions = @($hits | Where-Object { $_.kind -eq "definition" })
        $prototypes = @($hits | Where-Object { $_.kind -eq "prototype" })
        if ($definitions.Count -gt 0) {
            $bestHit = $definitions[0]
        }
        elseif ($prototypes.Count -gt 0) {
            $bestHit = $prototypes[0]
        }
        else {
            $bestHit = $hits[0]
        }
    }

    $sourcePresent = $hits.Count -gt 0
    $signatureMatches = $null
    $nativeParameterCount = $null
    if ($null -ne $bestHit -and $null -ne $bestHit.parameterCount) {
        $nativeParameterCount = [int]$bestHit.parameterCount
        $signatureMatches = ($nativeParameterCount -eq [int]$import.parameterCount)
        if (-not $signatureMatches) {
            $signatureMismatchCount++
        }
    }

    $binaryPresent = $null
    if ($binaryExports.evaluated) {
        $binaryPresent = $exportSet.ContainsKey($symbol)
    }

    if (-not $sourcePresent) {
        $sourceMissingRequiredCount++
    }
    if ($binaryExports.evaluated -and -not $binaryPresent) {
        $binaryMissingRequiredCount++
    }

    $category = [string]$import.category
    if (-not $categorySummaryMap.ContainsKey($category)) {
        $categorySummaryMap[$category] = [ordered]@{
            category = $category
            importedCount = 0
            sourceMissingCount = 0
            binaryMissingCount = 0
            signatureMismatchCount = 0
        }
    }
    $categorySummaryMap[$category]["importedCount"] = [int]$categorySummaryMap[$category]["importedCount"] + 1
    if (-not $sourcePresent) {
        $categorySummaryMap[$category]["sourceMissingCount"] = [int]$categorySummaryMap[$category]["sourceMissingCount"] + 1
    }
    if ($binaryExports.evaluated -and -not $binaryPresent) {
        $categorySummaryMap[$category]["binaryMissingCount"] = [int]$categorySummaryMap[$category]["binaryMissingCount"] + 1
    }
    if ($signatureMatches -eq $false) {
        $categorySummaryMap[$category]["signatureMismatchCount"] = [int]$categorySummaryMap[$category]["signatureMismatchCount"] + 1
    }

    $symbols.Add([pscustomobject][ordered]@{
        managedName = [string]$import.managedName
        symbolName = $symbol
        entryPointOverride = $import.entryPointOverride
        category = $category
        criticality = [string]$import.criticality
        managedReturnType = [string]$import.returnType
        managedParameterCount = [int]$import.parameterCount
        nativeParameterCount = $nativeParameterCount
        signatureMatches = $signatureMatches
        sourcePresent = $sourcePresent
        sourceBestKind = if ($null -eq $bestHit) { $null } else { $bestHit.kind }
        sourceBestFile = if ($null -eq $bestHit) { $null } else { $bestHit.file }
        sourceBestLine = if ($null -eq $bestHit) { $null } else { $bestHit.line }
        sourceHitCount = $hits.Count
        binaryPresent = $binaryPresent
        nativeMethodsLine = [int]$import.nativeMethodsLine
    }) | Out-Null
}

$binaryReadyForAcceptance = if ($RequireBinaryExports) {
    [bool]$binaryExports.evaluated -and $binaryMissingRequiredCount -eq 0
}
else {
    (-not [bool]$binaryExports.evaluated) -or $binaryMissingRequiredCount -eq 0
}

$readyForReview = ($sourceMissingRequiredCount -eq 0 -and
    $signatureMismatchCount -eq 0 -and
    $binaryReadyForAcceptance)

$categorySummary = @($categorySummaryMap.Values | ForEach-Object { [pscustomobject]$_ } | Sort-Object category)
$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    tool = "audit-wdsp-native-symbols"
    generatedUtc = [DateTimeOffset]::UtcNow
    nativeMethodsPath = Resolve-RelativePath $nativeMethodsResolved
    nativeSourceRoot = Resolve-RelativePath $nativeSourceResolved
    binaryPath = if ([string]::IsNullOrWhiteSpace($binaryResolved)) { $null } else { Resolve-RelativePath $binaryResolved }
    requireBinaryExports = [bool]$RequireBinaryExports
    readyForReview = $readyForReview
    importedSymbolCount = $imports.Count
    sourceMatchedSymbolCount = $imports.Count - $sourceMissingRequiredCount
    sourceMissingRequiredCount = $sourceMissingRequiredCount
    signatureMismatchCount = $signatureMismatchCount
    binaryExportsEvaluated = [bool]$binaryExports.evaluated
    binaryExportStatus = [string]$binaryExports.status
    binaryExportTool = $binaryExports.tool
    binaryExportCount = @($binaryExports.names).Count
    binaryMissingRequiredCount = $binaryMissingRequiredCount
    binaryExportError = $binaryExports.error
    categorySummary = @($categorySummary)
    symbols = @($symbols.ToArray())
    recommendations = if ($readyForReview) {
        @("Store this report with DSP modernization evidence before changing WDSP imports, native exports, candidate DSP, TXA, or PureSignal behavior.")
    }
    else {
        @(
            "Do not treat DSP benchmark evidence as acceptance-ready until missing WDSP symbols or managed/native signature mismatches are resolved.",
            "If binary exports were not evaluated, pass -BinaryPath to the exact wdsp.dll/libwdsp artifact used by the run and rerun with -RequireBinaryExports."
        )
    }
}

Write-JsonFile -Path $ReportPath -Value $report
if (-not $NoMarkdown) {
    Set-Content -LiteralPath $MarkdownPath -Value (Build-MarkdownReport $report) -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 48
}
else {
    if ($readyForReview) {
        Write-Host "WDSP native symbol audit passed: $ReportPath"
    }
    else {
        Write-Host "WDSP native symbol audit found issues: $ReportPath"
    }
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Imports: $($imports.Count), Source missing: $sourceMissingRequiredCount, Signature mismatches: $signatureMismatchCount, Binary missing: $binaryMissingRequiredCount"
}

if ($FailOnMissingRequired -and -not $readyForReview) {
    exit 1
}
