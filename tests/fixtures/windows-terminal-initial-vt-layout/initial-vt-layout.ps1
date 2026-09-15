$esc = [char]27
$width = [Console]::WindowWidth
$height = [Console]::WindowHeight

Write-Host "$esc[?1049h$esc[2J$esc[H" -NoNewline
Write-Host "$esc[2;4HWTIL_INITIAL geometry=${width}x${height}" -NoNewline
Write-Host "$esc[4;4HWTIL_UPDATE cursor-addressed output" -NoNewline
Write-Host "$esc[6;4HWTIL_INPUT type any key: " -NoNewline
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
Write-Host "$esc[8;4HWTIL_INPUT response rendered" -NoNewline
Write-Host "$esc[10;4HPress any key to return to the shell." -NoNewline
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
Write-Host "$esc[?1049l" -NoNewline
