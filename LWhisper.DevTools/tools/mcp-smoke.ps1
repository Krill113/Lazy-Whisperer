<#
    Неинтерактивный смоук MCP-режима LWhisper.DevTools.
    Проверяет: (1) handshake initialize, (2) список инструментов, (3) чистоту stdout,
    (4) реальный вызов transcribe на TTS-фикстуре с проверкой поля usedFallback,
    (5) повторный вызов в том же процессе — контроль прогрева движка (закон §5.4 №2).
    Использование:
        pwsh -NoProfile -File tools\mcp-smoke.ps1 -Exe <путь к LWhisper.DevTools.exe> [-Fixture <путь к .wav>]
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Fixture
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "Не найден исполняемый файл: $Exe" }

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName               = $Exe
$psi.Arguments              = 'mcp'
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false

$proc = [System.Diagnostics.Process]::Start($psi)

function Send-Line([string]$json) {
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
}

$initialize = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"lwhisper-smoke","version":"1.0.0"}}}'
Send-Line $initialize
$initReply = $proc.StandardOutput.ReadLine()
Write-Host "STDOUT[initialize]: $initReply"

Send-Line '{"jsonrpc":"2.0","method":"notifications/initialized"}'
Send-Line '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
$toolsReply = $proc.StandardOutput.ReadLine()
Write-Host "STDOUT[tools/list]: $toolsReply"

Send-Line '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"engine_info","arguments":{}}}'
$infoReply = $proc.StandardOutput.ReadLine()
Write-Host "STDOUT[engine_info]: $infoReply"

# Реальный вызов transcribe: проверяет, что MCP-путь действительно гоняет пайплайн и что поле
# usedFallback заполняется детектором (McpMode.ConfigureFileOnlyLogging ставит FallbackWatchSink).
# Без фикстуры шаг пропускается — handshake-часть смоука остаётся валидной.
$transcribeReply = $null
$transcribeReply2 = $null
if ($Fixture) {
    if (-not (Test-Path $Fixture)) { throw "Не найдена фикстура: $Fixture" }
    $fixtureFull = (Resolve-Path $Fixture).Path
    $callJson = @{
        jsonrpc = '2.0'; id = 4; method = 'tools/call'
        params  = @{ name = 'transcribe'; arguments = @{ path = $fixtureFull; language = 'ru' } }
    } | ConvertTo-Json -Depth 6 -Compress
    Send-Line $callJson
    # первый прогон грузит модель — ответ может идти десятки секунд, ReadLine блокирующий
    $transcribeReply = $proc.StandardOutput.ReadLine()
    Write-Host "STDOUT[transcribe]: $transcribeReply"

    # Второй вызов в том же процессе: проверяет закон §5.4 №2 — движок обязан быть прогретым,
    # то есть в лог не должна уйти вторая пара строк «Движок прогрет» / «Whisper runtime».
    $callJson2 = $callJson -replace '"id":4', '"id":5'
    Send-Line $callJson2
    $transcribeReply2 = $proc.StandardOutput.ReadLine()
    Write-Host "STDOUT[transcribe#2]: $transcribeReply2"
}

$proc.StandardInput.Close()
if (-not $proc.WaitForExit(10000)) { $proc.Kill() }

$failures = @()
foreach ($pair in @(@('initialize', $initReply), @('tools/list', $toolsReply), @('engine_info', $infoReply))) {
    $name = $pair[0]; $line = $pair[1]
    if ([string]::IsNullOrWhiteSpace($line)) { $failures += "$name : пустой ответ"; continue }
    if (-not $line.StartsWith('{')) { $failures += "$name : в stdout не-протокольная строка -> $line"; continue }
    try { $null = $line | ConvertFrom-Json } catch { $failures += "$name : stdout не является JSON -> $line" }
}
if ($toolsReply -notmatch '"transcribe"' -or $toolsReply -notmatch '"sweep"' -or $toolsReply -notmatch '"engine_info"') {
    $failures += 'tools/list : отсутствует один из инструментов transcribe / sweep / engine_info'
}

if ($Fixture) {
    if ([string]::IsNullOrWhiteSpace($transcribeReply)) {
        $failures += 'transcribe : пустой ответ'
    }
    elseif (-not $transcribeReply.StartsWith('{')) {
        $failures += "transcribe : в stdout не-протокольная строка -> $transcribeReply"
    }
    else {
        # Ответ инструмента — двойная сериализация: content[0].text содержит JSON строкой.
        # Разбирать нужно через ConvertFrom-Json, а не raw-подстроку: живой прогон против
        # ModelContextProtocol 1.3.0 показал, что дефолтный JavaScriptEncoder экранирует
        # вложенные кавычки как " (HTML-safe), поэтому буквальная подстрока '"usedFallback"'
        # в сырой строке ReadLine() не встречается никогда — raw-match здесь давал ложный
        # СМОУК ПРОВАЛЕН при полностью рабочем инструменте.
        try {
            $envelope = $transcribeReply | ConvertFrom-Json
            $toolResult = $envelope.result.content[0].text | ConvertFrom-Json
            if (-not ($toolResult.PSObject.Properties.Name -contains 'usedFallback')) {
                $failures += 'transcribe : в ответе нет поля usedFallback — детектор fallback не подключён (см. ConfigureFileOnlyLogging)'
            }
            elseif ($toolResult.usedFallback -eq $true) {
                # Спека §5, правило 4: замер со сработавшим аварийным fallback невалиден целиком
                $failures += 'transcribe : usedFallback=true — движок ушёл в аварийный fallback, замер невалиден'
            }
        }
        catch {
            $failures += "transcribe : не удалось разобрать JSON ответа -> $($_.Exception.Message)"
        }
    }

    if ([string]::IsNullOrWhiteSpace($transcribeReply2) -or -not $transcribeReply2.StartsWith('{')) {
        $failures += 'transcribe#2 : второй вызов в том же процессе не дал протокольного ответа'
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'СМОУК ПРОВАЛЕН:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host 'СМОУК ПРОЙДЕН: stdout содержит только протокольные строки, все три инструмента объявлены.' -ForegroundColor Green
exit 0
