param(
    [Parameter(Mandatory = $true)][string]$Key,
    [Parameter(Mandatory = $true)][ValidateSet('Backlog', 'Ready', 'In progress', 'In review', 'Done')][string]$Status,
    [string]$EvidenceFile = '',
    [int]$PullRequest = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$plan = Get-Content -LiteralPath (Join-Path $root 'docs/planning/github-tickets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$map = Get-Content -LiteralPath (Join-Path $root 'docs/planning/github-ticket-map.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$ticket = $map.PSObject.Properties[$Key].Value
if (-not $ticket) { throw "Unknown tracking key: $Key" }
function Invoke-Gh([string[]]$GhArgs) {
    $result = & gh @GhArgs
    if ($LASTEXITCODE -ne 0) { throw 'GitHub operation failed; project status may need reconciliation.' }
    return ($result -join "`n")
}
$issue = Invoke-Gh @('issue', 'view', "$($ticket.number)", '--repo', $plan.repository, '--json', 'state,url') | ConvertFrom-Json
if ($Status -eq 'Done' -and $issue.state -ne 'CLOSED') {
    throw 'Done requires a closed issue with verified acceptance and merged changes. This script never closes work automatically.'
}
if ($Status -ne 'Done' -and $issue.state -eq 'CLOSED') { throw 'Reopen the issue deliberately before moving it back into active work.' }
if ($Status -eq 'In review') {
    if ($PullRequest -lt 1) { throw 'Supply -PullRequest for a reviewable open pull request.' }
    $pr = Invoke-Gh @('pr', 'view', "$PullRequest", '--repo', $plan.repository, '--json', 'state,isDraft,url') | ConvertFrom-Json
    if ($pr.state -ne 'OPEN' -or $pr.isDraft) { throw 'In review requires an open, non-draft pull request.' }
}
if ($EvidenceFile -and -not (Test-Path -LiteralPath $EvidenceFile -PathType Leaf)) { throw 'Evidence file does not exist.' }
Invoke-Gh @('project', 'item-edit', "$($plan.projectNumber)", '--owner', $plan.owner, '--url', $issue.url, '--field', 'Status', '--value', $Status) | Out-Null
if ($EvidenceFile) {
    Invoke-Gh @('issue', 'comment', "$($ticket.number)", '--repo', $plan.repository, '--body-file', ([IO.Path]::GetFullPath($EvidenceFile))) | Out-Null
}
Write-Output "$Key -> $Status ($($issue.url))"
