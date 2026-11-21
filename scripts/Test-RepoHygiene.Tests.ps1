# Regression tests for scripts\Test-RepoHygiene.ps1.
#
# Run:  pwsh -NoProfile -File scripts\Test-RepoHygiene.Tests.ps1
#       (exit code 0 = all passed, 1 = at least one failure)
#
# Plain assert script in the style of roslynsentinel-instances-common.Tests.ps1. Every repo is a THROWAWAY created
# under the temp folder; the script under test runs in a child pwsh process against it. The real repo is never written.

$ErrorActionPreference = 'Stop'
$hygieneScript = Join-Path $PSScriptRoot 'Test-RepoHygiene.ps1'
$pwsh = (Get-Process -Id $PID).Path

$script:failures = 0
function Assert-That([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS  $Name" }
    else { Write-Host "FAIL  $Name"; $script:failures++ }
}

function Invoke-Git([string]$Dir, [string[]]$GitArgs) {
    $out = & git -C $Dir -c user.name=t -c user.email=t@example.invalid -c core.autocrlf=false @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in ${Dir}: $out" }
    return ($out -join "`n").Trim()
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-repo-hygiene-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$script:caseCount = 0

# A committed throwaway repo: README.md, src\a.cs and a .gitignore (*.json, *.csv, bin/, obj/). Returns its path.
function New-Fixture {
    $script:caseCount++
    $repo = Join-Path $tempRoot ("case" + $script:caseCount)
    New-Item -ItemType Directory -Path (Join-Path $repo 'src') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $repo 'README.md') -Value 'readme'
    Set-Content -LiteralPath (Join-Path $repo 'src\a.cs') -Value 'class A { }'
    Set-Content -LiteralPath (Join-Path $repo '.gitignore') -Value "*.json`n*.csv`nbin/`nobj/"
    $null = Invoke-Git $repo @('init', '-q', '-b', 'master')
    $null = Invoke-Git $repo @('add', '--', 'README.md', 'src/a.cs', '.gitignore')
    $null = Invoke-Git $repo @('commit', '-q', '-m', 'init')
    return $repo
}

# Runs the script in a child pwsh against $Repo. Returns Lines (non-blank output lines) and Exit (exit code).
function Invoke-Hygiene([string]$Repo, [string[]]$Extra = @()) {
    $out = & $pwsh -NoProfile -File $hygieneScript -RepoRoot $Repo @Extra 2>&1
    $code = $LASTEXITCODE
    $lines = @($out | ForEach-Object { "$_" } | Where-Object { $_.Trim() -ne '' })
    return [pscustomobject]@{ Lines = $lines; Exit = $code }
}

# True when Lines holds exactly Text.
function Test-Line([string[]]$Lines, [string]$Text) {
    return (@($Lines | Where-Object { $_ -eq $Text }).Count -gt 0)
}

# True when some line matches the -like Pattern.
function Test-LineLike([string[]]$Lines, [string]$Pattern) {
    return (@($Lines | Where-Object { $_ -like $Pattern }).Count -gt 0)
}

try {
    # --- 1: clean repo is silent and exits 0 ---
    $repo = New-Fixture
    $r = Invoke-Hygiene $repo
    Assert-That ($r.Lines.Count -eq 0 -and $r.Exit -eq 0) 'Clean repo: prints nothing and exits 0'

    # --- 2: stray root folder ---
    $repo = New-Fixture
    New-Item -ItemType Directory -Path (Join-Path $repo 'stray') | Out-Null
    Set-Content -LiteralPath (Join-Path $repo 'stray\note.txt') -Value 'x'
    $r = Invoke-Hygiene $repo
    Assert-That (Test-Line $r.Lines 'UNEXPECTED-ROOT stray') 'Stray root folder: UNEXPECTED-ROOT stray'

    # --- 3: stray root file (also untracked) ---
    $repo = New-Fixture
    Set-Content -LiteralPath (Join-Path $repo 'stray.txt') -Value 'x'
    $r = Invoke-Hygiene $repo
    Assert-That (Test-Line $r.Lines 'UNEXPECTED-ROOT stray.txt') 'Stray root file: UNEXPECTED-ROOT stray.txt'
    Assert-That (Test-Line $r.Lines 'UNTRACKED stray.txt') 'Stray root file: also UNTRACKED stray.txt'

    # --- 4: untracked source file, with the peer-session note ---
    $repo = New-Fixture
    Set-Content -LiteralPath (Join-Path $repo 'src\b.cs') -Value 'class B { }'
    $r = Invoke-Hygiene $repo
    Assert-That (Test-Line $r.Lines 'UNTRACKED src/b.cs') 'Untracked src\b.cs: UNTRACKED src/b.cs'
    Assert-That (Test-Line $r.Lines 'NOTE untracked files may belong to a peer session sharing this tree') 'Untracked src\b.cs: peer-session NOTE line printed'

    # --- 5: untracked file under an obj folder is filtered by the segment rule ---
    # .gitignore here drops the obj/ pattern on purpose, so src/obj/x.cs is untracked and only the filter excludes it.
    $repo = New-Fixture
    Set-Content -LiteralPath (Join-Path $repo '.gitignore') -Value "*.json`n*.csv`nbin/"
    New-Item -ItemType Directory -Path (Join-Path $repo 'src\obj') | Out-Null
    Set-Content -LiteralPath (Join-Path $repo 'src\obj\x.cs') -Value 'class X { }'
    $r = Invoke-Hygiene $repo
    Assert-That (-not (Test-LineLike $r.Lines '*obj*')) 'Untracked src\obj\x.cs: not reported'

    # --- 6: ignored root data file ---
    $repo = New-Fixture
    Set-Content -LiteralPath (Join-Path $repo 'data.json') -Value '{}'
    $r = Invoke-Hygiene $repo
    Assert-That (Test-LineLike $r.Lines 'IGNORED-DATA*data.json*') 'Ignored data.json: IGNORED-DATA line names data.json'
    Assert-That (Test-Line $r.Lines 'UNEXPECTED-ROOT data.json') 'Ignored data.json: UNEXPECTED-ROOT data.json'

    # --- 7: -Strict exit code ---
    $repo = New-Fixture
    Set-Content -LiteralPath (Join-Path $repo 'stray.txt') -Value 'x'
    $r = Invoke-Hygiene $repo @('-Strict')
    Assert-That ($r.Exit -eq 1) 'Strict with a finding: exits 1'
    $r = Invoke-Hygiene $repo
    Assert-That ($r.Exit -eq 0) 'Non-strict with a finding: exits 0'
    $clean = New-Fixture
    $r = Invoke-Hygiene $clean @('-Strict')
    Assert-That ($r.Exit -eq 0 -and $r.Lines.Count -eq 0) 'Strict on a clean repo: exits 0 and prints nothing'

    # --- 8: -Quiet drops the header, the default run keeps it ---
    $repo = New-Fixture
    Set-Content -LiteralPath (Join-Path $repo 'stray.txt') -Value 'x'
    $r = Invoke-Hygiene $repo
    Assert-That (Test-LineLike $r.Lines 'Repo hygiene: * finding(s) in *') 'Default run: header line present'
    $r = Invoke-Hygiene $repo @('-Quiet')
    Assert-That (-not (Test-LineLike $r.Lines 'Repo hygiene:*')) '-Quiet run: no header line'
    Assert-That (Test-Line $r.Lines 'UNEXPECTED-ROOT stray.txt') '-Quiet run: finding lines still printed'

    # --- 9: known ignored root and data files stay silent on a clean repo ---
    # RoslynSentinel.Server.Advanced is tracked here (placeholder README.md), as it is in the real repo, so the
    # launchSettings.json below is an ignored, untracked file inside a tracked folder.
    $repo = New-Fixture
    Set-Content -LiteralPath (Join-Path $repo 'global.json') -Value '{}'
    New-Item -ItemType Directory -Path (Join-Path $repo 'RoslynSentinel.Server.Advanced\Properties') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $repo 'RoslynSentinel.Server.Advanced\Properties\README.md') -Value 'placeholder'
    $null = Invoke-Git $repo @('add', '--', 'RoslynSentinel.Server.Advanced/Properties/README.md')
    $null = Invoke-Git $repo @('commit', '-q', '-m', 'placeholder')
    Set-Content -LiteralPath (Join-Path $repo 'RoslynSentinel.Server.Advanced\Properties\launchSettings.json') -Value '{}'
    $r = Invoke-Hygiene $repo
    Assert-That ($r.Lines.Count -eq 0 -and $r.Exit -eq 0) 'Clean repo + ignored global.json and launchSettings.json: silent, exits 0'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) FAILED"; exit 1 }
Write-Host 'All passed.'
exit 0
