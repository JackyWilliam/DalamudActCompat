# Renders every preview board with headless Edge: first reads each page's measured size and
# layout problems (from #report via --dump-dom), then screenshots it at exactly that size.
#   powershell tools/ui-redesign/preview.ps1 <output-directory>
param([string]$Output = "artifacts/ui-redesign")
# Edge reports progress on stderr, which Windows PowerShell would treat as an error.
$ErrorActionPreference = "Continue"
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$preview = Join-Path (Resolve-Path $Output) "preview"
$boards = Get-Content -Raw -Encoding UTF8 (Join-Path $preview "boards.json") | ConvertFrom-Json
$problems = 0
foreach ($board in $boards) {
    $id = "{0:D2}" -f $board.index
    $page = Join-Path $preview "$id.html"
    $url = "file:///" + ($page -replace "\\", "/")
    $dom = & $edge --headless=new --disable-gpu --virtual-time-budget=3000 --window-size=1400,4000 --dump-dom $url 2>$null | Out-String
    if ($dom -notmatch '<pre id="report"[^>]*>(.*?)</pre>') { Write-Host "$id $($board.name): no report"; $problems++; continue }
    $report = [System.Net.WebUtility]::HtmlDecode($Matches[1]) | ConvertFrom-Json
    & $edge --headless=new --disable-gpu --hide-scrollbars --virtual-time-budget=3000 "--window-size=$($report.w),$($report.h)" "--screenshot=$(Join-Path $preview "$id.png")" $url 2>$null | Out-Null
    $status = if ($report.problems.Count) { "问题 $($report.problems.Count)" } else { "OK" }
    Write-Host "$id $($board.section) / $($board.name): $($report.w)x$($report.h) $status"
    foreach ($p in $report.problems) { Write-Host "    $p"; $problems++ }
}
exit $problems
