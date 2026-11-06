param([string]$ManifestPath = '', [switch]$Preview)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $ManifestPath) { $ManifestPath = Join-Path $repoRoot 'docs/planning/github-tickets.json' }
$plan = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($Preview) { $plan.tickets | Select-Object key, title, status, milestone | Format-Table -Wrap; return }
$scratch = Join-Path $repoRoot 'artifacts/github-plan'
New-Item -ItemType Directory -Force $scratch | Out-Null
$encoding = New-Object System.Text.UTF8Encoding($false)
function Invoke-Gh([string[]]$GhArgs) {
    $result = & gh @GhArgs
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($GhArgs[0]) $($GhArgs[1]). Re-run safely after resolving the error." }
    return ($result -join "`n")
}
function Invoke-GraphQL([string]$Query, [hashtable]$Variables) {
    $inputPath = Join-Path $scratch 'graphql-input.json'
    [IO.File]::WriteAllText($inputPath, (@{ query = $Query; variables = $Variables } | ConvertTo-Json -Depth 20), $encoding)
    return (Invoke-Gh @('api', 'graphql', '--input', $inputPath) | ConvertFrom-Json)
}
$project = Invoke-Gh @('project', 'view', "$($plan.projectNumber)", '--owner', $plan.owner, '--format', 'json') | ConvertFrom-Json
$labels = Invoke-Gh @('label', 'list', '--repo', $plan.repository, '--limit', '100', '--json', 'name') | ConvertFrom-Json
foreach ($label in @(
    @{ name = 'type:epic'; color = '5319e7'; description = 'Phase-level delivery and acceptance tracking' },
    @{ name = 'type:task'; color = '1d76db'; description = 'Implementable work with explicit acceptance criteria' },
    @{ name = 'deferred'; color = 'ededed'; description = 'Explicitly excluded from the current release scope' }
)) {
    if ($label.name -notin $labels.name) {
        Invoke-Gh @('label', 'create', $label.name, '--repo', $plan.repository, '--color', $label.color, '--description', $label.description) | Out-Null
    }
}
$milestones = Invoke-Gh @('api', "repos/$($plan.repository)/milestones?state=all&per_page=100") | ConvertFrom-Json
foreach ($title in ($plan.tickets.milestone | Select-Object -Unique)) {
    if ($title -notin $milestones.title) {
        $inputPath = Join-Path $scratch 'milestone.json'
        [IO.File]::WriteAllText($inputPath, (@{ title = $title; description = 'Acceptance-gated delivery; no invented due date. Windows 11 x64 desktop, Windows/Linux x64 headless. ARM64 is deferred.' } | ConvertTo-Json), $encoding)
        Invoke-Gh @('api', "repos/$($plan.repository)/milestones", '--method', 'POST', '--input', $inputPath) | Out-Null
    }
}
$fields = (Invoke-Gh @('project', 'field-list', "$($plan.projectNumber)", '--owner', $plan.owner, '--format', 'json') | ConvertFrom-Json).fields
if ('Phase' -notin $fields.name) {
    $options = ((0..13 | ForEach-Object { "Phase $_" }) + 'Later') -join ','
    Invoke-Gh @('project', 'field-create', "$($plan.projectNumber)", '--owner', $plan.owner, '--name', 'Phase', '--data-type', 'SINGLE_SELECT', '--single-select-options', $options) | Out-Null
    $fields = (Invoke-Gh @('project', 'field-list', "$($plan.projectNumber)", '--owner', $plan.owner, '--format', 'json') | ConvertFrom-Json).fields
}
$statusField = $fields | Where-Object name -eq 'Status'
$priorityField = $fields | Where-Object name -eq 'Priority'
$sizeField = $fields | Where-Object name -eq 'Size'
$phaseField = $fields | Where-Object name -eq 'Phase'
$existing = Invoke-Gh @('issue', 'list', '--repo', $plan.repository, '--state', 'all', '--limit', '200', '--json', 'id,number,title,url,state') | ConvertFrom-Json
$items = (Invoke-Gh @('project', 'item-list', "$($plan.projectNumber)", '--owner', $plan.owner, '--limit', '200', '--format', 'json') | ConvertFrom-Json).items
$mapping = @{}
$mappingPath = Join-Path $repoRoot 'docs/planning/github-ticket-map.json'
if (Test-Path -LiteralPath $mappingPath) {
    (Get-Content -LiteralPath $mappingPath -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $mapping[$_.Name] = $_.Value }
}
function Write-Mapping {
    $ordered = [ordered]@{}
    foreach ($key in ($mapping.Keys | Sort-Object)) { $ordered[$key] = $mapping[$key] }
    [IO.File]::WriteAllText($mappingPath, ($ordered | ConvertTo-Json -Depth 10) + "`n", $encoding)
}
function Get-Body($Ticket) {
    $lines = @('## Outcome', '', $Ticket.description, '', '## Acceptance criteria', '')
    $lines += $Ticket.acceptance | ForEach-Object { '- [ ] ' + $_ }
    $lines += @('', '## Specification and dependencies', '', ('- Product specification sections: ' + $Ticket.spec + '.'),
        '- Current desktop target: Windows 11 x64. Headless support: Windows/Linux x64. ARM64 is deferred.',
        ('- Project: ' + $plan.projectUrl), ('- Tracking key: `' + $Ticket.key + '`'))
    if ($Ticket.parent -and $mapping.ContainsKey($Ticket.parent)) { $lines += '- Parent phase: ' + $mapping[$Ticket.parent].url }
    foreach ($dependency in $Ticket.dependsOn) {
        $reference = if ($mapping.ContainsKey($dependency)) { $mapping[$dependency].url } else { $dependency }
        $lines += '- Depends on: ' + $reference
    }
    if ($Ticket.progress) { $lines += @('', '## Current evidence', '', $Ticket.progress) }
    $lines += @('', '## Delivery policy', '',
        'Move to In progress before implementation. Keep acceptance checkboxes and verification evidence current. Move to In review only when a reviewable PR exists. Done requires verified acceptance and merged changes; never equate local code or a cross-build with release completion.',
        '', 'Implementation plan tracking; no due date is implied by priority or size.')
    return ($lines -join "`n") + "`n"
}
$updateFields = @'
mutation($project:ID!,$item:ID!,$statusField:ID!,$status:String!,$priorityField:ID!,$priority:String!,$sizeField:ID!,$size:String!,$phaseField:ID!,$phase:String!) {
  status:updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$statusField,value:{singleSelectOptionId:$status}}) { projectV2Item { id } }
  priority:updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$priorityField,value:{singleSelectOptionId:$priority}}) { projectV2Item { id } }
  size:updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$sizeField,value:{singleSelectOptionId:$size}}) { projectV2Item { id } }
  phase:updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$phaseField,value:{singleSelectOptionId:$phase}}) { projectV2Item { id } }
}
'@
foreach ($ticket in $plan.tickets) {
    $issue = $null
    if ($mapping.ContainsKey($ticket.key)) {
        $knownNumber = $mapping[$ticket.key].number
        $issue = $existing | Where-Object number -eq $knownNumber | Select-Object -First 1
    }
    if (-not $issue) { $issue = $existing | Where-Object title -eq $ticket.title | Select-Object -First 1 }
    $created = $false
    if (-not $issue) {
        $bodyPath = Join-Path $scratch ($ticket.key + '.md')
        [IO.File]::WriteAllText($bodyPath, (Get-Body $ticket), $encoding)
        $arguments = @('issue', 'create', '--repo', $plan.repository, '--title', $ticket.title, '--body-file', $bodyPath,
            '--label', ('type:' + $ticket.kind), '--milestone', $ticket.milestone)
        if ($ticket.key -eq 'LATER-ARM64') { $arguments += @('--label', 'deferred') }
        $url = Invoke-Gh $arguments
        $issue = Invoke-Gh @('issue', 'view', $url.Trim(), '--repo', $plan.repository, '--json', 'id,number,title,url,state') | ConvertFrom-Json
        $existing = @($existing) + @($issue)
        $created = $true
    }
    $item = $items | Where-Object { $_.content.url -eq $issue.url } | Select-Object -First 1
    $added = $false
    if (-not $item) {
        $item = Invoke-Gh @('project', 'item-add', "$($plan.projectNumber)", '--owner', $plan.owner, '--url', $issue.url, '--format', 'json') | ConvertFrom-Json
        $items = @($items) + @($item)
        $added = $true
    }
    $alreadySynced = $mapping.ContainsKey($ticket.key) -and $mapping[$ticket.key].initialized
    $parentLinked = $mapping.ContainsKey($ticket.key) -and $mapping[$ticket.key].parentLinked
    $mapping[$ticket.key] = [ordered]@{ number = $issue.number; url = $issue.url; nodeId = $issue.id; itemId = $item.id; initialized = $alreadySynced; parentLinked = $parentLinked }
    Write-Mapping
    # Never reset an existing ticket's status when re-running the bootstrap.
    if ($created -or $added -or -not $alreadySynced) {
        $phaseName = if ($ticket.phase -eq 'Later') { 'Later' } else { "Phase $($ticket.phase)" }
        Invoke-GraphQL $updateFields @{
            project = $project.id; item = $item.id
            statusField = $statusField.id; status = ($statusField.options | Where-Object name -eq $ticket.status).id
            priorityField = $priorityField.id; priority = ($priorityField.options | Where-Object name -eq $ticket.priority).id
            sizeField = $sizeField.id; size = ($sizeField.options | Where-Object name -eq $ticket.size).id
            phaseField = $phaseField.id; phase = ($phaseField.options | Where-Object name -eq $phaseName).id
        } | Out-Null
        $mapping[$ticket.key].initialized = $true
        Write-Mapping
    }
    if ($ticket.parent -and -not $parentLinked) {
        Invoke-GraphQL 'mutation($parent:ID!,$child:ID!){addSubIssue(input:{issueId:$parent,subIssueId:$child}){issue{id}}}' @{
            parent = $mapping[$ticket.parent].nodeId; child = $issue.id
        } | Out-Null
        $mapping[$ticket.key].parentLinked = $true
        Write-Mapping
    }
    Write-Output "$($ticket.key): $($issue.url) [$($ticket.status) initial state]"
    if ($created) { Start-Sleep -Milliseconds 750 }
}
$readme = @'
# API Wrangler implementation board

Desktop release target: Windows 11 x64. Headless components: Windows/Linux x64. ARM64 is explicitly deferred to a later milestone.

## Workflow
- Backlog: planned work whose dependencies or priority do not permit starting yet.
- Ready: actionable work with prerequisites available.
- In progress: implementation or verification has started, including local uncommitted work.
- In review: a reviewable pull request exists and relevant checks have run.
- Done: acceptance criteria verified, changes merged, and documentation updated.

Phase epics own native sub-issues. Close epics only when their required children and exit criteria are complete. Priority and Size describe sequencing and rough scope, not due dates. Deferred ARM64 must not block current milestones.

Milestone A covers phases 0-5; Milestone B covers phases 6-13. Keep status, evidence, dependencies, and parent progress synchronized while implementing. Do not automatically reset active ticket statuses from the bootstrap manifest.
'@
Invoke-GraphQL 'mutation($project:ID!,$readme:String!){updateProjectV2(input:{projectId:$project,shortDescription:"Windows 11 x64 desktop and shared headless API testing platform",readme:$readme}){projectV2{id}}}' @{ project = $project.id; readme = $readme } | Out-Null
Write-Output "Synchronized $($plan.tickets.Count) tickets to $($plan.projectUrl)."
