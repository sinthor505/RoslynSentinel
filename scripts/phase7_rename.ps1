param([switch]$DryRun)

$dir = "C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server"

# ── Rename map: old PascalCase → new PascalCase ────────────────────────────────
$renameMap = [ordered]@{
    # SentinelQualityTools.cs
    "DetectInefficientStringComparisons"    = "ScanInefficientStringComparisons"
    "FindBoxingAllocations"                 = "ScanBoxingAllocations"
    "FindPossibleDeadlocks"                 = "ScanPossibleDeadlocks"
    "DetectMemoryLeaks"                     = "ScanMemoryLeaks"
    "FindTaskVoidUsage"                     = "ScanTaskVoidUsage"
    "FindTaskYieldUsage"                    = "ScanTaskYieldUsage"
    "DetectReflectionUsage"                 = "ScanReflectionUsage"
    "FindTaskDelayUsage"                    = "ScanTaskDelayUsage"
    "FindTaskDelayZeroUsage"                = "ScanTaskDelayZeroUsage"
    "FindTaskWhenAllUsage"                  = "ScanTaskWhenAllUsage"
    "DetectAntiPatterns"                    = "ScanAntiPatterns"
    "FindPossibleInfiniteLoops"             = "ScanPossibleInfiniteLoops"
    "DetectMismatchedAwait"                 = "ScanMismatchedAwait"
    "FindHardcodedPaths"                    = "ScanHardcodedPaths"
    "FindMutablePublicProperties"           = "ScanMutablePublicProperties"
    "FindNamingViolations"                  = "ScanNamingViolations"
    "FindStringMagicValues"                 = "ScanStringMagicValues"
    "FindMissingCancellationTokens"         = "ScanMissingCancellationTokens"
    "FindConfigureAwaitMissing"             = "ScanConfigureAwaitMissing"
    "FindBlockingCallsInAsync"              = "ScanBlockingCallsInAsync"
    "FindAsyncInConstructor"                = "ScanAsyncInConstructor"
    "FindTaskRunInAsync"                    = "ScanTaskRunInAsync"
    "FindConcurrentCollectionOpportunities" = "ScanConcurrentCollectionOpportunities"
    "FindUnsafeLazyInit"                    = "ScanUnsafeLazyInit"
    "DetectValueTaskMisuse"                 = "ScanValueTaskMisuse"
    "FindMigrationCandidates"               = "ScanMigrationCandidates"
    "FindAsyncOverSync"                     = "ScanAsyncOverSync"
    "FindUnawaitedFireAndForget"            = "ScanUnawaitedFireAndForget"
    "FindLongParameterList"                 = "ScanLongParameterList"
    "FindPrimitiveObsession"                = "ScanPrimitiveObsession"
    "FindInconsistentAsyncSuffix"           = "ScanInconsistentAsyncSuffix"
    "DetectJsonAntiPatterns"                = "ScanJsonAntiPatterns"
    "DetectUnreachableCode"                 = "ScanUnreachableCode"
    "FindLinqN1Patterns"                    = "ScanLinqN1Patterns"
    "FindStringFormatInLoops"               = "ScanStringFormatInLoops"
    "FindMultipleEnumeration"               = "ScanMultipleEnumeration"
    "FindLinqRedundantWhere"                = "ScanLinqRedundantWhere"
    "FindImplicitNullableBoxing"            = "ScanImplicitNullableBoxing"
    "FindFinalizerOnDisposable"             = "ScanFinalizerOnDisposable"
    "FindUnboundedStaticCollections"        = "ScanUnboundedStaticCollections"
    "FindUnboundedRecursion"                = "ScanUnboundedRecursion"
    "FindMisboundOverloadChains"            = "ScanMisboundOverloadChains"
    "FindUnsafeLazyInitThread"              = "ScanUnsafeLazyInitThread"
    "FindCasLoopWithoutBackoff"             = "ScanCasLoopWithoutBackoff"
    "FindDoubleCheckedLocking"              = "ScanDoubleCheckedLocking"
    "FindCheckThenActOnDictionary"          = "ScanCheckThenActOnDictionary"
    "FindReDoSPatterns"                     = "ScanReDoSPatterns"
    "FindUnvalidatedRegexSource"            = "ScanUnvalidatedRegexSource"
    "FindRegexNewInLoop"                    = "ScanRegexNewInLoop"
    "FindSequentialIndependentAwaits"       = "ScanSequentialIndependentAwaits"
    "FindAsyncVoidWithoutTryCatch"          = "ScanAsyncVoidWithoutTryCatch"
    "FindUnawakedDisposeAsync"              = "ScanUnawakedDisposeAsync"
    "FindUnobservedTaskInField"             = "ScanUnobservedTaskInField"
    "FindCancellationTokenNotForwarded"     = "ScanCancellationTokenNotForwarded"
    "FindMutablePublicCollectionProperties" = "ScanMutablePublicCollectionProperties"
    "FindNonExhaustiveEnumSwitches"         = "ScanNonExhaustiveEnumSwitches"
    "FindMultipleOutParameterMethods"       = "ScanMultipleOutParameterMethods"
    "FindValueTypeMutationIntent"           = "ScanValueTypeMutationIntent"
    "FindObsoleteCallers"                   = "ScanObsoleteCallers"
    "FindNamespacePathMismatches"           = "ScanNamespacePathMismatches"
    # SentinelIntelligenceTools.cs
    "FindMethodsByReturnType"               = "ScanMethodsByReturnType"
    "FindUnusedPrivateMembers"              = "ScanUnusedPrivateMembers"
    "DetectUnusedPrivateFields"             = "ScanUnusedPrivateFields"
    "DetectUnusedLocalVariables"            = "ScanUnusedLocalVariables"
    "DetectLongParameterLists"              = "ScanLongParameterLists"
    "FindUninstantiatedTypes"               = "ScanUninstantiatedTypes"
    "FindCircularDependencies"              = "ScanCircularDependencies"
    "FindUnusedReferences"                  = "ScanUnusedReferences"
    "FindUnusedInterfaces"                  = "ScanUnusedInterfaces"
    "FindInternalClassesThatCouldBePrivate" = "ScanInternalClassesThatCouldBePrivate"
    "FindLargeSwitchStatements"             = "ScanLargeSwitchStatements"
    "FindStructuralSmells"                  = "ScanStructuralSmells"
    "FindUnusedConstructors"                = "ScanUnusedConstructors"
    "FindAllImplementations"                = "GetAllImplementations"
    "FindReadonlyFieldCandidates"           = "ScanReadonlyFieldCandidates"
    "FindDiRegistrations"                   = "GetDiRegistrations"
    "FindExtensionMethods"                  = "GetExtensionMethods"
    "FindAllThrowSites"                     = "ScanAllThrowSites"
    "FindObjectCreationSites"               = "GetObjectCreationSites"
    "FindServicesNotRegistered"             = "ScanServicesNotRegistered"
    "FindBestInsertionPoint"                = "GetBestInsertionPoint"
    "FindTodoFixmeComments"                 = "ScanTodoFixmeComments"
    "FindCallersSafe"                       = "GetCallers"
    "FindImplementationsSafe"               = "GetImplementations"
    "DetectBreakingChanges"                 = "ScanBreakingChanges"
    "DetectLayerViolations"                 = "ScanLayerViolations"
    "FindLargeTypes"                        = "ScanLargeTypes"
    "FindLargeMethods"                      = "ScanLargeMethods"
    "FindDuplicateMethods"                  = "ScanDuplicateMethods"
    "FindInterfaceExtractionCandidates"     = "ScanInterfaceExtractionCandidates"
    "FindCircularTypeReferences"            = "ScanCircularTypeReferences"
    "FindMissingGenericConstraints"         = "ScanMissingGenericConstraints"
    "FindTypesByAttribute"                  = "ScanTypesByAttribute"
    "FindAttributeUsages"                   = "GetAttributeUsages"
    "FindDuplicateBlocksInClass"            = "ScanDuplicateBlocksInClass"
    "FindDuplicateBlocksInHierarchy"        = "ScanDuplicateBlocksInHierarchy"
    # SentinelModernizationTools.cs
    "FindUseFrozenCollections"              = "ScanUseFrozenCollections"
}

# ── Helper: extract param block (handles multi-line) ─────────────────────────
function Get-ParamBlock([string]$content, [int]$openParenPos) {
    $depth = 0
    $i = $openParenPos
    while ($i -lt $content.Length) {
        $c = $content[$i]
        if ($c -eq '(') { $depth++ }
        elseif ($c -eq ')') {
            $depth--
            if ($depth -eq 0) { return @{ Start = $openParenPos + 1; End = $i } }
        }
        $i++
    }
    return $null
}

# ── Helper: extract param names from param block text ────────────────────────
function Get-ParamNames([string]$paramBlock) {
    $normalized = $paramBlock -replace '\s+', ' '
    if ([string]::IsNullOrWhiteSpace($normalized)) { return @() }
    $parts = $normalized -split ','
    $names = @()
    foreach ($p in $parts) {
        $p = $p.Trim()
        if ($p -eq '') { continue }
        # Remove "= default" or "= null" etc.
        if ($p -match '^(.+?)\s*=\s*\S+$') { $p = $matches[1].Trim() }
        # Remove "params " prefix
        $p = $p -replace '^params\s+', ''
        # Last word is the name
        $words = ($p -split '\s+') | Where-Object { $_ -ne '' }
        if ($words.Count -gt 0) { $names += $words[-1] }
    }
    return $names
}

# ── Helper: extract return type from the declaration up to the method name ────
function Get-ReturnType([string]$declSlice) {
    # declSlice = "    public async Task<List<X>> MethodName"
    $t = $declSlice.Trim() -replace '^public\s+async\s+', '' -replace '^public\s+', ''
    # Remove trailing method name (last word)
    $idx = $t.LastIndexOf(' ')
    if ($idx -lt 0) { return $t }
    return $t.Substring(0, $idx).Trim()
}

# ── Per-file processing ───────────────────────────────────────────────────────
$files = @(
    "SentinelQualityTools.cs",
    "SentinelIntelligenceTools.cs",
    "SentinelModernizationTools.cs"
)

foreach ($fileName in $files) {
    $filePath = "$dir\$fileName"
    $originalContent = [System.IO.File]::ReadAllText($filePath)
    $content = $originalContent

    # ── Step 1: Rename method declarations ───────────────────────────────────
    foreach ($old in $renameMap.Keys) {
        $new = $renameMap[$old]
        # Word-boundary replacement: catches "FindFoo(" but not "FindFooAsync("
        $content = [regex]::Replace($content, "\b$old\b(?=\()", $new)
    }

    if ($DryRun) {
        Write-Host "=== DRY-RUN: $fileName (rename pass preview) ===" -ForegroundColor Yellow
        $diff = Compare-Object ($originalContent -split "`n") ($content -split "`n") |
            Where-Object { $_.SideIndicator -ne '==' } |
            Select-Object -First 20
        $diff | ForEach-Object { Write-Host $_.InputObject }
        continue
    }

    # ── Step 2: Generate legacy alias methods ─────────────────────────────────
    # For each old→new rename, find the new method signature in the RENAMED content
    # and generate an alias that delegates: OldName(params) => NewName(params)
    $aliasMethods = [System.Text.StringBuilder]::new()
    $null = $aliasMethods.AppendLine()
    $null = $aliasMethods.AppendLine("    // ── Legacy aliases (deprecated — use scan_*/get_* names) ─────────────────────")

    # Track which new names have already been aliased (avoid duplicates for overloads)
    $aliasedNew = @{}

    foreach ($old in $renameMap.Keys) {
        $new = $renameMap[$old]
        $searchFor = "$new("
        $pos = 0
        $methodCount = 0

        while ($true) {
            $idx = $content.IndexOf($searchFor, $pos)
            if ($idx -lt 0) { break }

            # Verify it's a public method declaration (not a call or assignment)
            # Look back for "public" within ~120 chars
            $lookback = [Math]::Max(0, $idx - 120)
            $before = $content.Substring($lookback, $idx - $lookback)
            if ($before -notmatch 'public\s+(?:async\s+)?Task') {
                $pos = $idx + $searchFor.Length
                continue
            }

            # Find "public" keyword position before idx
            $publicIdx = $content.LastIndexOf("`n    public", $idx)
            if ($publicIdx -lt 0) {
                $pos = $idx + $searchFor.Length
                continue
            }
            $publicIdx++  # skip the \n

            # Extract declaration slice (from public to opening paren)
            $declSlice = $content.Substring($publicIdx, $idx - $publicIdx)

            # Extract param block
            $openParen = $idx + $new.Length
            $pb = Get-ParamBlock $content $openParen
            if ($pb -eq $null) {
                $pos = $idx + $searchFor.Length
                continue
            }
            $paramBlockText = $content.Substring($pb.Start, $pb.End - $pb.Start)
            $paramNames = Get-ParamNames $paramBlockText
            $paramNamesStr = $paramNames -join ", "

            # Extract return type
            $retType = Get-ReturnType $declSlice

            # Determine MCP tool name for deprecation message
            $snakeNew = [regex]::Replace($new, '(?<=[a-z0-9])(?=[A-Z])', '_').ToLower()
            $snakeOld = [regex]::Replace($old, '(?<=[a-z0-9])(?=[A-Z])', '_').ToLower()

            # Build the alias method
            # For multi-line params, preserve original param block (trimmed)
            $normalizedParamBlock = $paramBlockText.Trim() -replace '\s+', ' '

            $null = $aliasMethods.AppendLine()
            $null = $aliasMethods.AppendLine("    [McpServerTool]")
            $null = $aliasMethods.AppendLine("    [Description(""Deprecated: use $snakeNew instead. This alias will be removed in a future release."")]")
            $null = $aliasMethods.AppendLine("    public $retType $old($normalizedParamBlock)")
            $null = $aliasMethods.AppendLine("        => $new($paramNamesStr);")

            $methodCount++
            $pos = $pb.End + 1

            # For overloaded names (FindCircularDependencies), alias both overloads
            # Continue searching for more
        }

        if ($methodCount -eq 0) {
            Write-Warning "${fileName}: '${new}' not found for alias of '${old}'"
        }
    }

    # ── Step 3: Insert aliases before the final closing brace of the file ────
    $lastBrace = $content.LastIndexOf("`n}")
    if ($lastBrace -lt 0) { $lastBrace = $content.LastIndexOf("}") }
    $insertAt = $lastBrace  # insert before the \n}

    $finalContent = $content.Substring(0, $insertAt) + "`n" + $aliasMethods.ToString() + $content.Substring($insertAt)

    [System.IO.File]::WriteAllText($filePath, $finalContent, [System.Text.Encoding]::UTF8)
    Write-Host "✓ $fileName — renames applied + aliases appended" -ForegroundColor Green
}

Write-Host ""
Write-Host "Phase 7 complete." -ForegroundColor Cyan
