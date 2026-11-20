<#
.SYNOPSIS
    Eval host table, host address resolution and liveness (plan_eval_host_table_and_liveness). Dot-source this file; it runs nothing by itself.

.DESCRIPTION
    The table is scripts\eval-hosts.json: one entry per model server with alias, machine, baseUrl, backend
    ("lmstudio" or "llama-server"), suffix, default (true on the one entry a bare machine key resolves to) and an
    optional note.

    Functions:
      Get-EvalHostTablePath        path of eval-hosts.json beside this file
      Read-EvalHostTable           loads and validates the table, returns the entries
      Resolve-EvalHostEntry        -HostAddress (alias, bare machine key, base URL or other URL) -> table entry
      Get-EvalHostModels           one server's reachability, model list and load state (never throws)
      Test-EvalModelMatch          requested model id vs one loaded id
      Get-EvalModelLoadState       Loaded / NotLoaded / Unknown for a requested model

    stdout rule: nothing here writes to the output stream except the documented return values.

    Compatible with Windows PowerShell 5.1.
#>

function Get-EvalHostTablePath {
    return Join-Path $PSScriptRoot 'eval-hosts.json'
}

function Read-EvalHostTable {
    param([string]$Path = (Get-EvalHostTablePath))

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Eval host table not found at '$Path'. Options: create the file, or pass -Path to another table file."
    }
    try {
        $doc = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Eval host table '$Path' is not valid JSON: $($_.Exception.Message)"
    }
    if (-not ($doc.PSObject.Properties.Name -contains 'hosts')) {
        throw "Eval host table '$Path' has no top-level 'hosts' array."
    }
    $raw = @($doc.hosts)
    if ($raw.Count -eq 0) {
        throw "Eval host table '$Path' lists no hosts. Options: add at least one entry under 'hosts'."
    }

    $backends = @('lmstudio', 'llama-server')
    $entries = @()
    $index = 0
    foreach ($h in $raw) {
        $index++
        if ($null -eq $h -or $h -isnot [pscustomobject]) {
            throw "Host entry #$index in '$Path' is not a JSON object."
        }
        $names = @($h.PSObject.Properties.Name)
        foreach ($field in 'alias', 'machine', 'baseUrl', 'backend', 'suffix', 'default') {
            $missing = ($names -notcontains $field) -or ($null -eq $h.$field) -or (($h.$field -is [string]) -and ($h.$field.Trim() -eq ''))
            if ($missing) { throw "Host entry #$index in '$Path' is missing field '$field'." }
        }
        if ($h.default -isnot [bool]) { throw "Host entry '$($h.alias)' in '$Path' has a 'default' value that is not true or false." }
        if ($backends -notcontains $h.backend) {
            throw "Host entry '$($h.alias)' in '$Path' has unknown backend '$($h.backend)'. Options: lmstudio, llama-server."
        }
        $note = $null
        if ($names -contains 'note') { $note = $h.note }
        $entries += [pscustomobject]@{
            Alias     = [string]$h.alias
            Machine   = [string]$h.machine
            BaseUrl   = [string]$h.baseUrl
            Backend   = [string]$h.backend
            Suffix    = [string]$h.suffix
            IsDefault = [bool]$h.default
            Note      = $note
        }
    }

    $aliasSeen = @{}
    foreach ($e in $entries) {
        $key = $e.Alias.ToLowerInvariant()
        if ($aliasSeen.ContainsKey($key)) {
            throw "Alias '$($e.Alias)' appears more than once in '$Path'. Options: give each entry a unique alias."
        }
        $aliasSeen[$key] = $true
    }

    $machineKeys = @{}
    foreach ($e in $entries) { $machineKeys[$e.Machine.ToLowerInvariant()] = $true }
    foreach ($e in $entries) {
        if ($machineKeys.ContainsKey($e.Alias.ToLowerInvariant())) {
            throw "Alias '$($e.Alias)' in '$Path' is the same as a machine key. Options: pick another alias; a bare machine key resolves to that machine's default entry."
        }
    }

    $urlSeen = @{}
    foreach ($e in $entries) {
        $key = $e.BaseUrl.TrimEnd('/').ToLowerInvariant()
        if ($urlSeen.ContainsKey($key)) {
            throw "baseUrl '$($e.BaseUrl)' is used by two entries ('$($urlSeen[$key])' and '$($e.Alias)') in '$Path'. Options: keep one entry per server."
        }
        $urlSeen[$key] = $e.Alias
    }

    $machineNames = @($entries | ForEach-Object { $_.Machine } | Sort-Object -Unique)
    foreach ($m in $machineNames) {
        $defaults = @($entries | Where-Object { $_.Machine -eq $m -and $_.IsDefault })
        if ($defaults.Count -ne 1) {
            throw "Machine '$m' has $($defaults.Count) default entries in '$Path'; exactly one is required. Options: set default true on one entry of this machine and false on the others."
        }
    }

    return $entries
}

function Resolve-EvalHostEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Address,
        [object[]]$Table
    )
    if (-not $PSBoundParameters.ContainsKey('Table')) { $Table = @(Read-EvalHostTable) }
    $Table = @($Table)

    # 1. Entry alias (case-insensitive).
    $hit = @($Table | Where-Object { $_.Alias -eq $Address })
    if ($hit.Count -gt 0) {
        return $hit[0] | Select-Object Alias, Machine, BaseUrl, Backend, Suffix, IsDefault, Note, @{ Name = 'Known'; Expression = { $true } }
    }

    # 2. Bare machine key -> that machine's default entry.
    $hit = @($Table | Where-Object { $_.Machine -eq $Address -and $_.IsDefault })
    if ($hit.Count -gt 0) {
        return $hit[0] | Select-Object Alias, Machine, BaseUrl, Backend, Suffix, IsDefault, Note, @{ Name = 'Known'; Expression = { $true } }
    }

    # 3. Base URL, trailing '/' ignored, case-insensitive.
    $wanted = $Address.TrimEnd('/')
    $hit = @($Table | Where-Object { $_.BaseUrl.TrimEnd('/') -eq $wanted })
    if ($hit.Count -gt 0) {
        return $hit[0] | Select-Object Alias, Machine, BaseUrl, Backend, Suffix, IsDefault, Note, @{ Name = 'Known'; Expression = { $true } }
    }

    # 4. Unknown address, used verbatim (for example stub, or a server not in the table).
    $suffix = ($Address -replace '[^a-zA-Z0-9]', '_').Trim('_')
    if (-not $suffix) {
        $aliases = (@($Table | ForEach-Object { $_.Alias })) -join ', '
        $machines = (@($Table | ForEach-Object { $_.Machine } | Sort-Object -Unique)) -join ', '
        throw "-HostAddress '$Address' is not a known alias, machine key or base URL, and gives an empty folder suffix. Known aliases: $aliases. Known machine keys: $machines. Options: use one of these, or a base URL such as http://host:1234/v1."
    }
    return [pscustomobject]@{
        Alias   = $Address
        Machine = $suffix
        BaseUrl = $Address
        Backend = 'lmstudio'
        Suffix  = $suffix
        Known   = $false
    }
}

function Get-EvalHostModels {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Entry,
        [int]$TimeoutSec = 5,
        [switch]$ReachabilityOnly
    )
    $base = ([string]$Entry.BaseUrl).TrimEnd('/')
    $modelsUrl = "$base/models"
    $result = [pscustomobject]@{ Reachable = $false; Models = @(); Error = $null }

    if ($ReachabilityOnly) {
        try {
            $null = Invoke-RestMethod -Uri $modelsUrl -Method Get -TimeoutSec $TimeoutSec
            $result.Reachable = $true
        }
        catch {
            $result.Error = "GET $modelsUrl failed: $($_.Exception.Message)"
        }
        return $result
    }

    if ($Entry.Backend -eq 'llama-server') {
        try {
            $body = Invoke-RestMethod -Uri $modelsUrl -Method Get -TimeoutSec $TimeoutSec
            if ($body -is [string]) { $body = $body | ConvertFrom-Json }
            if (-not ($body.PSObject.Properties.Name -contains 'data')) { throw 'the response has no data array' }
            $models = @(foreach ($m in @($body.data)) {
                $names = @($m.PSObject.Properties.Name)
                $loaded = $null
                if (($names -contains 'loaded') -and ($null -ne $m.loaded)) { $loaded = [bool]$m.loaded }
                elseif ($names -contains 'status') { $loaded = ($m.status.value -eq 'loaded') }
                $ctx = $null
                if ($names -contains 'context_length') { $ctx = $m.context_length }
                [pscustomobject]@{ Id = [string]$m.id; Loaded = $loaded; ContextLength = $ctx }
            })
            $result.Reachable = $true
            $result.Models = $models
        }
        catch {
            $result.Error = "GET $modelsUrl failed: $($_.Exception.Message)"
        }
        return $result
    }

    # lmstudio: /v1/models answering is the reachability signal; the native list adds load state.
    try {
        $null = Invoke-RestMethod -Uri $modelsUrl -Method Get -TimeoutSec $TimeoutSec
    }
    catch {
        $result.Error = "GET $modelsUrl failed: $($_.Exception.Message)"
        return $result
    }
    $result.Reachable = $true

    $root = $base -replace '/v1$', ''
    $nativeUrl = "$root/api/v1/models"
    try {
        $body = Invoke-RestMethod -Uri $nativeUrl -Method Get -TimeoutSec $TimeoutSec
        if ($body -is [string]) { $body = $body | ConvertFrom-Json }
        if (-not ($body.PSObject.Properties.Name -contains 'models')) { throw 'the response has no models array' }
        $models = @(foreach ($m in @($body.models)) {
            $instances = @($m.loaded_instances | Where-Object { $null -ne $_ })
            $ctx = $null
            if (($instances.Count -gt 0) -and ($null -ne $instances[0].config)) { $ctx = $instances[0].config.context_length }
            [pscustomobject]@{ Id = [string]$m.key; Loaded = ($instances.Count -gt 0); ContextLength = $ctx }
        })
        $result.Models = $models
    }
    catch {
        $result.Error = "GET $nativeUrl failed: $($_.Exception.Message)"
    }
    return $result
}

function Test-EvalModelMatch {
    param([string]$Requested, [string]$LoadedId)
    if ([string]::IsNullOrWhiteSpace($Requested) -or [string]::IsNullOrWhiteSpace($LoadedId)) { return $false }
    $want = $Requested.ToLowerInvariant()
    $want = $want.Substring($want.LastIndexOf('/') + 1)
    $have = $LoadedId.ToLowerInvariant()
    $have = $have.Substring($have.LastIndexOf('/') + 1)
    if (($want -eq '') -or ($have -eq '')) { return $false }
    return ($want -eq $have)
}

function Get-EvalModelLoadState {
    param([object[]]$Models, [string]$Requested)
    $reporting = @($Models | Where-Object { ($null -ne $_) -and ($null -ne $_.Loaded) })
    if ($reporting.Count -eq 0) {
        return [pscustomobject]@{ State = 'Unknown'; LoadedIds = @() }
    }
    $loadedIds = @($reporting | Where-Object { $_.Loaded -eq $true } | ForEach-Object { [string]$_.Id })
    $matched = @($loadedIds | Where-Object { Test-EvalModelMatch -Requested $Requested -LoadedId $_ })
    $state = 'NotLoaded'
    if ($matched.Count -gt 0) { $state = 'Loaded' }
    return [pscustomobject]@{ State = $state; LoadedIds = $loadedIds }
}
