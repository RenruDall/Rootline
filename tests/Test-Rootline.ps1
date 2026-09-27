#Requires -Version 7.0
<#
.SYNOPSIS
    Round-trip test: the real Rootline scanner (C#) against a fake Azure + Entra tenant served over HTTP.

.DESCRIPTION
    - fake_tenant.py  builds the fictional tenant and the API responses it would produce (raw.json) plus expected.json
    - fake_server.py  serves those responses as Resource Graph, ARM and Microsoft Graph (tokens, paging, 429s, 403s)
    - this script compiles src/Rootline.Core with the test host, scans the fake tenant and checks that every
      resource, connection, identity, role assignment, group member, policy and CA policy comes back exactly —
      nothing missing, nothing extra — and that the HTTP behaviour was right (right token per service, throttling
      retried, no unknown calls).

    Run:  python tests/fake_tenant.py tests/out
          pwsh tests/Test-Rootline.ps1
#>
param([string]$Fixtures = (Join-Path $PSScriptRoot 'out'), [string]$SaveScan)   # -SaveScan <file>: keep the scan result (for trying the page)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exp = Get-Content (Join-Path $Fixtures 'expected.json') -Raw | ConvertFrom-Json -Depth 20
$raw = Get-Content (Join-Path $Fixtures 'raw.json') -Raw | ConvertFrom-Json -Depth 50
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string]$Name, $Expected, $Actual) {
    $e = @($Expected | ForEach-Object { [string]$_ }); $a = @($Actual | ForEach-Object { [string]$_ })
    $missing = @([Linq.Enumerable]::Except([string[]]$e, [string[]]$a))
    $extra   = @([Linq.Enumerable]::Except([string[]]$a, [string[]]$e))
    if (-not $missing -and -not $extra -and $e.Count -eq $a.Count) { Write-Host ("  PASS  {0,-34} {1,6}" -f $Name, $e.Count) -ForegroundColor Green; return }
    $script:failures.Add($Name)
    Write-Host ("  FAIL  {0,-34} expected {1}, got {2}" -f $Name, $e.Count, $a.Count) -ForegroundColor Red
    $missing | Select-Object -First 8 | ForEach-Object { Write-Host "          missing: $_" -ForegroundColor Red }
    $extra   | Select-Object -First 8 | ForEach-Object { Write-Host "          extra:   $_" -ForegroundColor Red }
}
function Expect([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { Write-Host ("  PASS  {0,-34} {1}" -f $Name, $Detail) -ForegroundColor Green }
    else { $script:failures.Add($Name); Write-Host ("  FAIL  {0,-34} {1}" -f $Name, $Detail) -ForegroundColor Red }
}
# dates come back as DateTime from ConvertFrom-Json; compare them in the fixture's format
function Day($v) { if ($null -eq $v -or $v -eq '') { '' } elseif ($v -is [datetime]) { $v.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") } else { [string]$v } }
$edgeKey = { param($e) if ($e.rel -eq 'peered') { 'peered|' + ((@($e.s, $e.t) | Sort-Object) -join '|') } else { "$($e.rel)|$($e.s)|$($e.t)" } }
$ncKey = { param($nc) if (-not $nc) { '' } else { ($nc.PSObject.Properties | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ',' } }

# ------------------------------------------------------------ compile the engine + test host
Write-Host "`nCompiling src/Rootline.Core with the test host" -ForegroundColor White
$files = @(Get-ChildItem (Join-Path $root 'src/Rootline.Core/*.cs')) + @(Get-Item (Join-Path $PSScriptRoot 'TestHost.cs'))
$usings = [System.Collections.Generic.SortedSet[string]]::new()
$bodies = foreach ($f in $files) {
    $lines = Get-Content $f
    foreach ($l in $lines) { if ($l -match '^using [\w\.]+;') { [void]$usings.Add($l) } }
    ($lines | Where-Object { $_ -notmatch '^using [\w\.]+;' }) -join "`n"
}
$refs = 'System.Net.Http', 'System.Net.Primitives', 'System.Text.Json', 'System.Text.RegularExpressions', 'System.Linq', 'System.Collections',
        'System.Runtime', 'System.Private.CoreLib', 'System.Memory', 'System.Threading', 'System.Threading.Tasks', 'System.Private.Uri', 'System.Console'
Add-Type -TypeDefinition (($usings -join "`n") + "`n" + ($bodies -join "`n")) -Language CSharp -CompilerOptions '-langversion:9.0' -ReferencedAssemblies $refs
Write-Host '  compiled' -ForegroundColor DarkGray

# ------------------------------------------------------------ fake Azure + Entra
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
$python = (Get-Command python3 -ErrorAction SilentlyContinue) ?? (Get-Command python)
$server = Start-Process $python.Source -ArgumentList (Join-Path $PSScriptRoot 'fake_server.py'), (Join-Path $Fixtures 'raw.json'), $port -PassThru -NoNewWindow
$base = "http://127.0.0.1:$port"
try {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) { try { Invoke-RestMethod "$base/stats" -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 200 } }

    # ============================================================ scan
    Write-Host "`nScanning the fake tenant over HTTP" -ForegroundColor White
    $json = [Rootline.Tests.TestHost]::Run($base, $raw.tenantId, $true, 'fake-arm-token', 'fake-graph-token', $false)
    if ($SaveScan) { Set-Content -Path $SaveScan -Value $json -Encoding utf8 }
    $result = $json | ConvertFrom-Json -Depth 30
    $d = $result.data; $st = $result.stats
    Write-Host ("  scan took {0}s: {1} Resource Graph queries, {2} ARM calls, {3} Graph calls, {4} retries after throttling" -f `
        $st.seconds, $st.resourceGraphQueries, $st.armCalls, $st.graphCalls, $st.retries) -ForegroundColor DarkGray
    $srv = Invoke-RestMethod "$base/stats"

    Write-Host "`nResult" -ForegroundColor White
    Check 'Nodes (id, type, parent, name)' ($exp.nodes | ForEach-Object { "$($_.id)|$($_.type)|$($_.parent)|$($_.name)" }) `
                                           ($d.nodes   | ForEach-Object { "$($_.id)|$($_.type)|$($_.parent)|$($_.name)" })
    Check 'External nodes'                 ($exp.nodes | Where-Object external | ForEach-Object id) ($d.nodes | Where-Object external | ForEach-Object id)
    Check 'Connections'                    ($exp.edges | ForEach-Object { & $edgeKey $_ } | Sort-Object -Unique) ($d.edges | ForEach-Object { & $edgeKey $_ })
    Expect 'Excluded types filtered'       (-not ($d.nodes | Where-Object { $_.type -match '/extensions$|/virtualnetworklinks$' })) 'VM extensions, DNS links'
    Expect 'Route table → firewall links'  (@($d.edges | Where-Object { $_.rel -eq 'routes to' -and $_.s -match '/routetables/' }).Count -gt 0) 'resolved from next-hop IPs'
    Check 'Identities (id, kind, name)'    ($exp.principals | ForEach-Object { "$($_.id)|$($_.kind)|$($_.name)" }) ($d.access.principals | ForEach-Object { "$($_.id)|$($_.kind)|$($_.name)" })
    Check 'Guests'                         $exp.guests ($d.access.principals | Where-Object guest | ForEach-Object id)
    Check 'Azure roles (active + PIM)'     ($exp.azure | ForEach-Object { "$($_.p)|$($_.role)|$($_.scope)|$($_.status)|$(Day $_.until)" }) ($d.access.azure | ForEach-Object { "$($_.p)|$($_.role)|$($_.scope)|$($_.status)|$(Day $_.until)" })
    Check 'Entra roles (active + PIM)'     ($exp.entra | ForEach-Object { "$($_.p)|$($_.role)|$($_.status)|$($_.priv)|$(Day $_.until)" }) ($d.access.entra | ForEach-Object { "$($_.p)|$($_.role)|$($_.status)|$($_.priv)|$(Day $_.until)" })
    Check 'Access packages → groups'       ($exp.packages | ForEach-Object { "$($_.id)|$($_.name)|$($_.catalog)|" + ((@($_.groups) | Sort-Object) -join ',') }) `
                                           ($d.access.packages | ForEach-Object { "$($_.id)|$($_.name)|$($_.catalog)|" + ((@($_.groups) | Sort-Object) -join ',') })
    Check 'Access package assignments'     ($exp.packageAssignments | ForEach-Object { "$($_.p)|$($_.pkg)|$(Day $_.until)" }) ($d.access.packageAssignments | ForEach-Object { "$($_.p)|$($_.pkg)|$(Day $_.until)" })
    Check 'Pending package requests'       ($exp.packageRequests | ForEach-Object { "$($_.p)|$($_.pkg)" }) ($d.access.packageRequests | ForEach-Object { "$($_.p)|$($_.pkg)" })
    Check 'PIM for Groups (eligible)'      ($exp.groupEligible | ForEach-Object { "$($_.g)|$($_.p)|$(Day $_.until)" }) ($d.access.groupEligible | ForEach-Object { "$($_.g)|$($_.p)|$(Day $_.until)" })
    Check 'Group members'                  ($exp.members.PSObject.Properties | ForEach-Object { "$($_.Name)=" + ((@($_.Value) | Sort-Object) -join ',') }) `
                                           ($d.access.members.PSObject.Properties | ForEach-Object { "$($_.Name)=" + ((@($_.Value) | Sort-Object) -join ',') })
    Check 'Policies (+ non-compliance)'    ($exp.policies | ForEach-Object { "$($_.id)|$($_.name)|$($_.scope)|$($_.enforce)|$($_.initiative)|$(& $ncKey $_.nonCompliant)" }) `
                                           ($d.access.policies | ForEach-Object { "$($_.id)|$($_.name)|$($_.scope)|$($_.enforce)|$($_.initiative)|$(& $ncKey $_.nonCompliant)" })
    Check 'Conditional Access policies'    $exp.ca ($d.access.ca | ForEach-Object name)
    Expect 'Tenant name'                   ($d.meta.tenantName -eq $exp.tenantName) $d.meta.tenantName
    Expect 'Denied management group noted' (@($d.meta.warnings | Where-Object { $_ -match $exp.deniedWarning }).Count -gt 0) "$(@($d.meta.warnings).Count) scan notes"

    Write-Host "`nHTTP behaviour" -ForegroundColor White
    Expect 'Right token for each service'  ($srv.unauthorized -eq 0) "$($srv.requests) requests"
    Expect 'Throttling (429) retried'      ($srv.throttled -gt 0 -and $st.retries -eq $srv.throttled) "$($srv.throttled) throttled, $($st.retries) retried"
    Expect 'No unknown calls'              (@($srv.unknown).Count -eq 0) ((@($srv.unknown) | Select-Object -First 3) -join '; ')
    Expect 'Well-formed requests'          (@($srv.badRequests).Count -eq 0) ((@($srv.badRequests) | Select-Object -First 3) -join '; ')
    Expect 'Paging used'                   ($srv.arg -gt $st.resourceGraphQueries -and $srv.graph -gt 10) "$($srv.arg) Resource Graph pages for $($st.resourceGraphQueries) queries"

    Write-Host "`nFailure handling" -ForegroundColor White
    $err = $null
    try { [Rootline.Tests.TestHost]::Run($base, $raw.tenantId, $true, 'expired-token', 'fake-graph-token', $true) | Out-Null } catch { $err = $_.Exception.InnerException ?? $_.Exception }
    Expect 'Bad token stops with 401'      ($err -and $err.Message -match '401') $(if ($err) { $err.Message.Split("`n")[0] } else { 'no error' })
    $azOnly = [Rootline.Tests.TestHost]::Run($base, $raw.tenantId, $true, 'fake-arm-token', 'wrong-graph-token', $true) | ConvertFrom-Json -Depth 30
    Expect 'No Graph access: Azure only'   (@($azOnly.data.nodes).Count -eq @($d.nodes).Count -and @($azOnly.data.access.entra).Count -eq 0 -and
                                            @($azOnly.data.meta.warnings | Where-Object { $_ -match 'Directory role' }).Count -gt 0) 'resources kept, Entra parts noted as skipped'
} finally {
    if (-not $server.HasExited) { $server.Kill() }
}

Write-Host ''
if ($failures.Count) { Write-Host "$($failures.Count) check(s) failed: $($failures -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.' -ForegroundColor Green
