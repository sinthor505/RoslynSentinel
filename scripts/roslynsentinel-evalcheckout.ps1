<#
.SYNOPSIS
    Pinned eval checkouts (plan_pinned_eval_checkouts, Step 2). Dot-source this file; it runs nothing by itself.

.DESCRIPTION
    Layout under the checkout root (default C:\rs-eval, override ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT):

      <sha12>\            published checkout of one commit (git clone --shared --no-checkout, then checkout --detach)
      <sha12>\complete    marker "sha=<full sha> createdUtc=<iso>"; a folder without it is never used
      <sha12>\lastused    touched by every Get-EvalCheckout; drives the retention sweep
      <sha12>.<token>     partial clone being built (renamed to <sha12> when done) or being swept

    Functions:
      Get-EvalCheckoutRoot     the checkout root; warns when the path is long (Windows MAX_PATH headroom)
      Resolve-EvalCommit       rev -> full 40-char sha in a repo
      Get-EvalCheckout         creates (or reuses) the checkout of a sha and returns its path
      Invoke-EvalCheckoutSweep removes old unreferenced checkouts and stale partials, returns the removed paths

    stdout rule: nothing here writes to the output stream except the documented return values; native git
    output is captured.

    Compatible with Windows PowerShell 5.1.
#>

. (Join-Path $PSScriptRoot 'roslynsentinel-instances-common.ps1')

# True when $Path is a direct child of $Root (not the root itself, not deeper). Every delete goes through this:
# a malformed name or a symlink-ish path must never let the sweep leave the checkout root.
function Test-EvalDirectChild {
    param([string]$Path, [string]$Root)
    if (-not (Test-PathUnderRoot -Path $Path -Root $Root)) { return $false }
    $parent = Split-Path -Path $Path.TrimEnd('\') -Parent
    return ($parent.TrimEnd('\') -ieq $Root.TrimEnd('\'))
}

# Deletes one direct child of $Root. Returns $true when the folder is gone. Never throws.
# .git pack files are read-only; Remove-Item -Force normally copes, `rd /s /q` is the fallback for what it cannot.
function Remove-EvalCheckoutDirectory {
    param([string]$Path, [string]$Root)

    if (-not (Test-EvalDirectChild -Path $Path -Root $Root)) {
        Write-Warning "Refusing to delete '$Path': not a direct child of the checkout root '$Root'."
        return $false
    }
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop } catch { }
    if (Test-Path -LiteralPath $Path) {
        try { $null = Invoke-NativeCaptured -File 'cmd.exe' -Arguments @('/c', 'rd', '/s', '/q', $Path) } catch { }
    }
    return (-not (Test-Path -LiteralPath $Path))
}

function Set-EvalCheckoutLastUsed {
    param([string]$CheckoutPath)
    # Best effort: a failed touch only makes the sweep treat the checkout as older than it is.
    try {
        $stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        [System.IO.File]::WriteAllText((Join-Path $CheckoutPath 'lastused'), $stamp, (New-Object System.Text.UTF8Encoding($false)))
    }
    catch { }
}

function Get-EvalCheckoutRoot {
    [CmdletBinding()]
    param()

    $root = $env:ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT
    if (-not $root) { $root = 'C:\rs-eval' }
    $root = [Environment]::ExpandEnvironmentVariables($root).TrimEnd('\')
    if (-not [System.IO.Path]::IsPathRooted($root) -or $root -match '%[^%]+%') {
        # A relative root would resolve against the current directory, i.e. nest checkouts inside the repo.
        throw "Eval checkout root '$root' is not an absolute path. Set ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT to a full path such as C:\rs-eval."
    }
    if ($root.Length -gt 16) {
        Write-Warning "Eval checkout root '$root' is $($root.Length) chars; a pinned run's deepest paths approach the Windows 260-char limit with a root longer than 16 chars."
    }
    return $root
}

function Resolve-EvalCommit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Rev
    )

    # --end-of-options is not used: a rev starting with '-' fails rev-parse --verify and surfaces below.
    $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $RepoRoot, 'rev-parse', '--verify', '--quiet', "$Rev^{commit}")
    $sha = $r.Output.Trim()
    if ($r.ExitCode -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') {
        $detail = ConvertTo-PullErrorText $r.Output
        if (-not $detail) { $detail = 'rev not found' }
        throw "Cannot resolve rev '$Rev' to a commit in repo '$RepoRoot' (git exit $($r.ExitCode)): $detail"
    }
    return $sha
}

function Get-EvalCheckout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Sha,
        [Parameter(Mandatory)][string]$CheckoutRoot
    )

    if ($Sha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Eval checkout needs a full 40-char commit sha, got '$Sha'. Use Resolve-EvalCommit first."
    }
    $Sha = $Sha.ToLowerInvariant()
    $CheckoutRoot = $CheckoutRoot.TrimEnd('\')
    $sha12 = $Sha.Substring(0, 12)
    $final = Join-Path $CheckoutRoot $sha12

    if (Test-Path -LiteralPath (Join-Path $final 'complete')) {
        Set-EvalCheckoutLastUsed -CheckoutPath $final
        return $final
    }

    $partial = Join-Path $CheckoutRoot "$sha12.$(New-RoslynSentinelToken)"
    if (-not (Test-EvalDirectChild -Path $partial -Root $CheckoutRoot)) {
        throw "Eval checkout path '$partial' is not a direct child of '$CheckoutRoot'."
    }
    try {
        if (-not (Test-Path -LiteralPath $CheckoutRoot)) {
            $null = New-Item -ItemType Directory -Path $CheckoutRoot -Force
        }

        # Global git config (autocrlf etc.) is inherited on purpose: the checkout should look like a normal clone.
        $r = Invoke-NativeCaptured -File 'git' -Arguments @('clone', '--shared', '--no-checkout', '--', $RepoRoot, $partial)
        if ($r.ExitCode -ne 0) { throw "git clone --shared of '$RepoRoot' failed (exit $($r.ExitCode)): $($r.Output)" }

        # A post-change commit's .gitignore no longer lists _scratchbuild_*/, so without this local exclude the
        # checkout looks dirty to the run stamp (ModelEvalAssemblySetUp.IsDirty counts untracked .cs files).
        $excludePath = Join-Path $partial '.git\info\exclude'
        $excludeDir = Split-Path $excludePath -Parent
        if (-not (Test-Path -LiteralPath $excludeDir)) { $null = New-Item -ItemType Directory -Path $excludeDir -Force }
        $existingExclude = if (Test-Path -LiteralPath $excludePath) { [System.IO.File]::ReadAllText($excludePath) } else { '' }
        $missingExclude = @('_scratchbuild_*/', 'complete', 'lastused') |
            Where-Object { $existingExclude -notmatch ('(?m)^' + [regex]::Escape($_) + '\r?$') }
        if ($missingExclude) {
            $lead = if ($existingExclude -and -not $existingExclude.EndsWith("`n")) { "`n" } else { '' }
            [System.IO.File]::AppendAllText($excludePath, $lead + (($missingExclude -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
        }

        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $partial, 'checkout', '--detach', $Sha)
        if ($r.ExitCode -ne 0) { throw "git checkout --detach $Sha failed (exit $($r.ExitCode)): $($r.Output)" }

        $r = Invoke-NativeCaptured -File 'git' -Arguments @('-C', $partial, 'rev-parse', 'HEAD')
        $head = $r.Output.Trim()
        if ($r.ExitCode -ne 0 -or $head -ne $Sha) {
            throw "Checkout HEAD is '$head' but $Sha was requested (git exit $($r.ExitCode))."
        }

        $createdUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText((Join-Path $partial 'lastused'), $createdUtc, $utf8)
        # Written last: its presence is what makes a folder usable.
        [System.IO.File]::WriteAllText((Join-Path $partial 'complete'), "sha=$Sha createdUtc=$createdUtc", $utf8)

        # First rename wins. A target without 'complete' is debris (crashed publisher), removed once; any other
        # failure is retried briefly (virus scanner / indexer holding a file).
        $staleRemoved = $false
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            try {
                Rename-Item -LiteralPath $partial -NewName $sha12 -ErrorAction Stop
                return $final
            }
            catch {
                $renameError = $_.Exception.Message
                if (Test-Path -LiteralPath (Join-Path $final 'complete')) {
                    Remove-EvalCheckoutDirectory -Path $partial -Root $CheckoutRoot | Out-Null
                    Set-EvalCheckoutLastUsed -CheckoutPath $final
                    return $final
                }
                if ((Test-Path -LiteralPath $final) -and -not $staleRemoved) {
                    $staleRemoved = $true
                    if (-not (Remove-EvalCheckoutDirectory -Path $final -Root $CheckoutRoot)) {
                        throw "Stale incomplete checkout '$final' could not be removed (a file is probably in use)."
                    }
                    continue
                }
                Start-Sleep -Milliseconds 500
            }
        }
        throw "Could not rename '$partial' to '$sha12' after 4 attempts: $renameError"
    }
    catch {
        $message = $_.Exception.Message
        Remove-EvalCheckoutDirectory -Path $partial -Root $CheckoutRoot | Out-Null
        throw "Could not create the eval checkout for $Sha under '$CheckoutRoot': $message"
    }
}

function Invoke-EvalCheckoutSweep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CheckoutRoot,
        [string[]]$KeepShas = @(),
        [TimeSpan]$MinimumAge = [TimeSpan]::FromHours(24),
        [int]$KeepNewest = 5
    )

    $removed = New-Object System.Collections.Generic.List[string]
    $CheckoutRoot = $CheckoutRoot.TrimEnd('\')
    if (-not (Test-Path -LiteralPath $CheckoutRoot -PathType Container)) { return @() }

    $keep = @($KeepShas | Where-Object { $_ } | ForEach-Object { "$_".Trim().ToLowerInvariant() })
    $now = [DateTime]::UtcNow
    $dirs = @(Get-ChildItem -LiteralPath $CheckoutRoot -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^[0-9a-f]{12}(\..+)?$' })

    # Listed when a keep entry (full sha or prefix) matches the recorded sha, or the folder's 12-char name.
    function Test-Listed([string]$FullSha, [string]$Name) {
        $prefix = $Name.Substring(0, 12)
        foreach ($k in $keep) {
            if ($FullSha -and $FullSha.StartsWith($k, [StringComparison]::Ordinal)) { return $true }
            if ($prefix.StartsWith($k, [StringComparison]::Ordinal) -or $k.StartsWith($prefix, [StringComparison]::Ordinal)) { return $true }
        }
        return $false
    }

    $published = @()
    $partials = @()
    foreach ($d in $dirs) {
        $marker = Join-Path $d.FullName 'complete'
        if ($d.Name.Length -eq 12 -and (Test-Path -LiteralPath $marker -PathType Leaf)) {
            $recorded = $null
            $text = [string](Get-Content -LiteralPath $marker -TotalCount 1 -ErrorAction SilentlyContinue)
            $m = [regex]::Match($text, 'sha=([0-9a-fA-F]{12,40})')
            if ($m.Success) { $recorded = $m.Groups[1].Value.ToLowerInvariant() }
            $usedFile = Get-Item -LiteralPath (Join-Path $d.FullName 'lastused') -Force -ErrorAction SilentlyContinue
            if (-not $usedFile) { $usedFile = Get-Item -LiteralPath $marker -Force }
            $published += [pscustomobject]@{ Dir = $d; Sha = $recorded; LastUsedUtc = $usedFile.LastWriteTimeUtc }
        }
        else {
            # '<sha12>.<token>' partials, plus a bare '<sha12>' without 'complete' (debris of a crashed publish).
            $partials += $d
        }
    }

    $protected = @{}
    if ($KeepNewest -gt 0) {
        foreach ($p in ($published | Sort-Object LastUsedUtc -Descending | Select-Object -First $KeepNewest)) {
            $protected[$p.Dir.FullName] = $true
        }
    }

    foreach ($p in $published) {
        $path = $p.Dir.FullName
        if ($protected.ContainsKey($path)) { continue }
        if (Test-Listed $p.Sha $p.Dir.Name) { continue }
        if (($now - $p.LastUsedUtc) -lt $MinimumAge) { continue }
        # Rename first: it takes the folder out of use atomically (a half-deleted folder never looks published) and
        # fails on Windows while a process still has a handle inside it, which protects a run in progress.
        $sweepPath = Join-Path $CheckoutRoot "$($p.Dir.Name).sweep-$(New-RoslynSentinelToken)"
        try { Rename-Item -LiteralPath $path -NewName (Split-Path $sweepPath -Leaf) -ErrorAction Stop }
        catch {
            Write-Warning "Eval checkout sweep: '$path' is in use or locked, skipped ($($_.Exception.Message))."
            continue
        }
        if (Remove-EvalCheckoutDirectory -Path $sweepPath -Root $CheckoutRoot) { $removed.Add($path) }
        else { Write-Warning "Eval checkout sweep: could not fully delete '$sweepPath'; the next sweep retries it." }
    }

    foreach ($d in $partials) {
        if (Test-Listed $null $d.Name) { continue }
        if (($now - $d.LastWriteTimeUtc) -lt $MinimumAge) { continue }
        if (Remove-EvalCheckoutDirectory -Path $d.FullName -Root $CheckoutRoot) { $removed.Add($d.FullName) }
        else { Write-Warning "Eval checkout sweep: could not fully delete '$($d.FullName)'; the next sweep retries it." }
    }

    # No leading comma: callers wrap the call in @() (an empty result then has Count 0).
    return $removed.ToArray()
}
