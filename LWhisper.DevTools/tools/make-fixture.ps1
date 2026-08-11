<#
.SYNOPSIS
    Генерирует синтетические RU WAV-фикстуры (Windows SAPI) для смоука LWhisper.DevTools.

.DESCRIPTION
    Фикстура проверяет ПАЙПЛАЙН ранера, а не качество распознавания: синтетическая речь
    звучит не как живая диктовка. Настоящая калибровка качества — только на корпусе,
    надиктованном владельцем (раздел «Осталось у владельца», 06-cp6-cp7.md).

    Формат вывода: PCM 16000 Гц, моно, 16 бит — ровно то, что читает LWhisper.DevTools
    и пишет дамп аудио CP1.

    Скрипту нужна сборка System.Speech. Если она не грузится в pwsh 7, запускай через
    Windows PowerShell: powershell.exe -ExecutionPolicy Bypass -File <путь> -OutDir <папка>

.PARAMETER OutDir
    Каталог для WAV-файлов. Создаётся при отсутствии.

.PARAMETER Phrases
    Свой список фраз. По умолчанию — 6 коротких (<5.12 с) и 2 длинных.

.PARAMETER Prefix
    Префикс имён файлов, по умолчанию fixture.

.PARAMETER Rate
    Скорость речи SAPI (-10..10). 0 = не менять.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File LWhisper.DevTools\tools\make-fixture.ps1 -OutDir C:\Temp\lwhisper-fixtures
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string[]]$Phrases,
    [string]$Prefix = 'fixture',
    [int]$Rate = 0
)

$ErrorActionPreference = 'Stop'

try {
    Add-Type -AssemblyName System.Speech
}
catch {
    throw "Не удалось загрузить System.Speech. Запусти скрипт через Windows PowerShell: powershell.exe -ExecutionPolicy Bypass -File <путь к скрипту> -OutDir <папка>"
}

if (-not $Phrases -or $Phrases.Count -eq 0) {
    $Phrases = @(
        'Проверка связи.',
        'Отметка низа трубы двенадцать пятьдесят.',
        'Пикет двенадцать плюс сорок.',
        'Уклон ноль целых пять тысячных.',
        'Колодец номер семь, глубина три метра.',
        'Диаметр трубы двести миллиметров.',
        'Продольный профиль участка от пикета десять до пикета двенадцать выполнен в масштабе один к двум тысячам, отметки низа трубы уточнить по рабочей документации.',
        'На листе четыре показан узел примыкания дождеприёмника к колодцу, отметка верха решётки сто двадцать пять сорок, отметка лотка сто двадцать три десять.'
    )
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $voices = $synth.GetInstalledVoices() | Where-Object { $_.Enabled }
    if (-not $voices) { throw 'В системе нет ни одного включённого голоса SAPI.' }

    $ru = $voices | Where-Object { $_.VoiceInfo.Culture.Name -like 'ru*' } | Select-Object -First 1
    if ($ru) {
        $synth.SelectVoice($ru.VoiceInfo.Name)
        Write-Host "Голос: $($ru.VoiceInfo.Name) ($($ru.VoiceInfo.Culture.Name))"
    }
    else {
        $any = $voices | Select-Object -First 1
        $synth.SelectVoice($any.VoiceInfo.Name)
        Write-Warning "RU-голос не найден — беру $($any.VoiceInfo.Name) ($($any.VoiceInfo.Culture.Name)). Фикстура проверяет пайплайн, не качество."
    }

    if ($Rate -ne 0) { $synth.Rate = $Rate }

    $format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(
        16000,
        [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
        [System.Speech.AudioFormat.AudioChannel]::Mono)

    for ($i = 0; $i -lt $Phrases.Count; $i++) {
        $name = '{0}-{1:d2}.wav' -f $Prefix, ($i + 1)
        $path = Join-Path $OutDir $name

        $synth.SetOutputToWaveFile($path, $format)
        $synth.Speak($Phrases[$i])
        $synth.SetOutputToNull()

        $bytes = (Get-Item $path).Length
        $seconds = [math]::Round(($bytes - 44) / 32000.0, 2)
        Write-Host ('{0}  {1} байт  ~{2} c' -f $name, $bytes, $seconds)
    }
}
finally {
    $synth.Dispose()
}

Write-Host "Готово: $OutDir"
