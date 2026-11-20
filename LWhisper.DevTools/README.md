# LWhisper.DevTools

Офлайн-стенд измерений поверх **боевого** `LWhisper.SpeechEngine`: тот же пайплайн распознавания,
что и в приложении, но без UI и без микрофона — на входе WAV-файлы, на выходе текст, тайминги и отчёт.

Нужен, чтобы менять параметры Whisper по замеру, а не на слух: свипы `audio_ctx`, потоков и beam
на одном и том же корпусе, посимвольное сравнение текстов между плечами.

> Инструмент разработчика. В релизный артефакт (`release.yml` публикует только `LWhisper.UI.WPF`)
> не попадает и на работу приложения не влияет.

## Требования

- .NET 8 SDK
- Модель Whisper в `%APPDATA%\LWhisper\Models\ggml-<id>.bin` (скачивается через Настройки приложения)
- CPU-режим: Vulkan-рантайм в проект намеренно не подключён — стенд считает так же, как прод у владельца

## Сборка

```powershell
dotnet build LWhisper.DevTools/LWhisper.DevTools.csproj -c Release
```

Исполняемый файл: `LWhisper.DevTools\bin\Release\net8.0\LWhisper.DevTools.exe`

## Команды

```
LWhisper.DevTools.exe <command> [options]

  transcribe    распознать один или несколько WAV
  sweep         прогнать сетку параметров по корпусу
  engine-info   напечатать разрешённую конфигурацию движка (JSON) и выйти
  mcp           запустить MCP-сервер по stdio (синоним — глобальный флаг --mcp)
```

### Общие опции

| Опция | Значение | По умолчанию |
|---|---|---|
| `--input <path>` | файл `.wav` или папка (рекурсивно `*.wav`), опция повторяемая | обязательна для `transcribe`/`sweep` |
| `--model <path\|id>` | путь к `ggml-*.bin` либо id модели (резолвится в `%APPDATA%\LWhisper\Models\ggml-{id}.bin`) | `WhisperModelSize` из `settings.json` (**только чтение**), иначе `large-v3-turbo` |
| `--language <ru\|en\|auto>` | язык распознавания | `ru` |
| `--ctx-floor <int>` | пол окна энкодера; `0` = не вызывать `WithAudioContextSize` вовсе | `WhisperTuning.AudioContextFloor` (448) |
| `--threads <int>` | число потоков | формула `WhisperTuning` |
| `--thread-mode <legacy\|divided>` | режим бюджета потоков | `legacy` |
| `--beam` | beam search вместо greedy | выкл (greedy) |
| `--prompt <текст>` | подсказка Whisper (`WithPrompt`) | без подсказки |
| `--prompt-file <путь>` | то же, текст берётся из файла **дословно** (UTF-8, обрезаются только крайние пробелы) | без подсказки |
| `--parallel <int>` | параллельных распознаваний | `1` |
| `--repeat <int>` | прогонов на файл | `1` |
| `--out <dir>` | каталог отчётов | `docs/superpowers/measurements/{yyyyMMdd-HHmmss}`, если корень репозитория не найден — `{DebugRoot}/reports/{yyyyMMdd-HHmmss}` |
| `--tag <string>` | метка прогона, попадает в отчёт | — |
| `--format <json\|md\|both>` | формат отчёта | `both` |
| `--quiet` | только итоговая сводка в stdout | выкл |
| `--max-duration <sec>` | файлы длиннее отбрасываются с предупреждением | `30` |

> Файл `session.wav` (полная запись сессии, которую кладёт дамп) из корпуса исключается **всегда**, независимо от `--max-duration`: его длительность до 600 с даёт окно энкодера заведомо больше полного, гарантированный аварийный fallback и часы прогона на каждое плечо.

### Опции `sweep`

| Опция | Значение | По умолчанию |
|---|---|---|
| `--grid-ctx <csv>` | напр. `0,256,448,768` | значение `--ctx-floor` (одно плечо) |
| `--grid-threads <csv>` | напр. `4,6,8` | один прогон на формуле |
| `--grid-beam <csv>` | `false,true` | значение `--beam` (одно плечо) |
| `--baseline <report.json>` | сравнить тексты и метрики с предыдущим отчётом | — |
| `--max-runs <int>` | предохранитель | `200` (превышение = выход с кодом 2) |

### Коды возврата

`0` — успех, `1` — ошибка аргументов/входа, `2` — превышен `--max-runs`, `3` — модель не найдена / движок не поднялся.

## Примеры

```powershell
$devtools = ".\LWhisper.DevTools\bin\Release\net8.0\LWhisper.DevTools.exe"

# что движок вообще думает о своей конфигурации
& $devtools engine-info

# один файл
& $devtools transcribe --input .\fixtures\ru-short-01.wav --tag smoke

# A/B пола контекстного окна на корпусе, 3 прогона на файл.
# --max-runs обязателен: 15 файлов × 6 плеч × 3 повтора = 270 прогонов, а предохранитель по
# умолчанию 200 (превышение = выход с кодом 2, ничего не посчитав). Порядок времени — часы.
& $devtools sweep --input "$env:APPDATA\LWhisper\debug\20260812-101500" `
                  --grid-ctx 0,256,384,448,512,768 --repeat 3 --max-runs 400 --tag ctx-floor-ab

# A/B бюджета потоков. ВАЖНО: --thread-mode divided действует только когда число потоков НЕ задано
# явно — --threads/--grid-threads всегда жёсткий override, и режим тогда игнорируется. Поэтому здесь
# НЕТ --grid-threads: сравниваются два отдельных прогона с разным --parallel при divided/legacy.
& $devtools sweep --input "$env:APPDATA\LWhisper\debug\20260812-101500" `
                  --thread-mode divided --parallel 3 --repeat 3 --tag threads-divided-p3
& $devtools sweep --input "$env:APPDATA\LWhisper\debug\20260812-101500" `
                  --thread-mode legacy --parallel 3 --repeat 3 --tag threads-legacy-p3

# сравнение с предыдущим отчётом (посимвольная сверка текстов)
& $devtools sweep --input .\corpus --grid-ctx 448 --baseline .\docs\superpowers\measurements\20260812-101500\report.json
```

## Отчёты

В каталоге `--out` создаются `report.json` (`schemaVersion: 1`) и `report.md`.

- `rtf = elapsedMs / durationMs`
- `tailRate` — доля прогонов с `rtf > 1.5` (**главная метрика скорости**: распределение бимодально, медиана врёт)
- `p10 / p25` по `elapsedMs` — защита лучшего случая, считаются отдельно по коротким (`durationMs < 5120`) и по всем
- `distinctTexts` — число различных `textSha256` внутри плеча; больше 1 = недетерминизм

`report.md` содержит шапку окружения, таблицу по плечам, таблицу «текст по файлам» и, при `--baseline`,
раздел «Расхождения текста» с обеими версиями строк.

## Фикстура для смоука (без диктовки)

```powershell
$fx = Join-Path $env:TEMP 'lwhisper-fixtures'
powershell.exe -ExecutionPolicy Bypass -File .\LWhisper.DevTools\tools\make-fixture.ps1 -OutDir $fx
```

Параметр `-OutDir` обязателен. Скрипт синтезирует RU-WAV 16 кГц / моно / 16 бит через `System.Speech` (Windows SAPI); в `pwsh 7` эта сборка может не загрузиться — тогда и нужен запуск через `powershell.exe`.
Если русского голоса в системе нет — берётся любой доступный с предупреждением: фикстура проверяет
**пайплайн**, а не качество распознавания. Команды `make-fixture` в CLI нет намеренно —
иначе проект пришлось бы переводить на `net8.0-windows`.

### Смоук MCP-режима

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LWhisper.DevTools\tools\mcp-smoke.ps1 `
    -Exe .\LWhisper.DevTools\bin\Release\net8.0\LWhisper.DevTools.exe -Fixture $fx\ru-short-01.wav
```

Как и `make-fixture.ps1`, запускать через `powershell.exe` (Windows PowerShell 5.1) — файл сохранён
с BOM специально под этот раннер.

## MCP-режим

Тот же пайплайн, доступный ассистенту по stdio (пакет `ModelContextProtocol`).

```powershell
claude mcp add lwhisper-transcribe --scope user -- "<АБСОЛЮТНЫЙ путь>\LWhisper.DevTools.exe" mcp
```

Конфигурация MCP в репозиторий не коммитится — регистрация делается один раз локально.

Инструменты:

| tool | вход | выход |
|---|---|---|
| `transcribe` | `path`, опц. `language`, `ctxFloor`, `threads`, `threadMode`, `beam`, `model` | `text`, `durationMs`, `elapsedMs`, `rtf`, `audioContextSize`, `threads`, `beam`, `usedFallback` |

> `transcribe` наследует ограничения CLI-предохранителей и не даёт их поднять: файлы длиннее 30 с
> (`--max-duration`, здесь не настраивается) и файл `session.wav` инструментом не обрабатываются —
> оба отбрасываются на этапе отбора корпуса с ошибкой инструмента. Используйте `seg-*.wav`.
| `sweep` | `paths[]`, опц. `ctxFloors[]`, `threads[]`, `beam[]`, `repeat`, `parallel`, `reportDir`, `maxRuns` | `reportJsonPath`, `reportMarkdownPath`, `summary` |
| `engine_info` | `{}` | конфигурация движка: модель, язык, `processorCount`, `defaultThreads`, `ctxFloorDefault`, `threadMode`, `whisperNet`, `runtimeInfo`, `gpu`, `dumpEnabled`, `dumpDirectory`, `promptChars` |

Правила режима:
- в `mcp` stdout занят JSON-RPC: console-логи отключены, лог пишется в `{DebugRoot}/mcp/log-*.txt`;
- движок один на процесс, вызовы сериализуются — native-процессор не готов к параллельным запросам;
- `sweep` с числом прогонов больше `maxRuns` (200) возвращает ошибку инструмента, а не считает полчаса.

## Переменные окружения

Читаются **только** в `LWhisper.SpeechEngine`. Отсутствие переменной = поведение прода.
Применённое переопределение логируется один раз на уровне `Information` со словом `override`.

| Переменная | Значения | Дефолт | Смысл |
|---|---|---|---|
| `LWHISPER_DEBUG_AUDIO` | `1`/`true`/`yes`/`on`, прочее = выкл | выкл | дамп PCM сегментов, сессии и метаданных |

> DevTools принудительно гасит `LWHISPER_DEBUG_AUDIO` для своего процесса, даже если переменная
> унаследована из окружения (владелец диктует корпус с этим флагом и тем же окружением запускает
> стенд) — иначе дамп писался бы ВНУТРИ измеряемого окна и портил замер. Следствие: `engine-info`
> и `engine_info` (MCP) стенда всегда показывают `dumpEnabled: false`, поле `dumpDirectory`
> отсутствует. Дамп пишет только приложение (`LWhisper.UI.WPF`), не стенд.
| `LWHISPER_DEBUG_AUDIO_DIR` | абсолютный путь | `%APPDATA%\LWhisper\debug` | корень дампов |
| `LWHISPER_AUDIO_CTX_FLOOR` | целое ≥ 0; `0` = не вызывать `WithAudioContextSize` | `448` | пол окна энкодера |
| `LWHISPER_THREAD_MODE` | `legacy` \| `divided` | `legacy` | режим бюджета потоков |
| `LWHISPER_WHISPER_THREADS` | целое > 0 | нет | жёсткое переопределение числа потоков |

## Приватность

- `LWHISPER_DEBUG_AUDIO=1` пишет **запись вашей речи** в `%APPDATA%\LWhisper\debug\{сессия}\`
  (`seg-*.wav`, `session.wav`, `meta.jsonl` с текстами). Флаг по умолчанию выключен.
- Отчёты в `docs/superpowers/measurements/` содержат транскрипты. Вся папка `docs/superpowers/`
  в `.gitignore` — в публичный репозиторий речь и тексты не утекают.
- Обе папки чистятся **вручную**, автоудаления нет.
- DevTools открывает `%APPDATA%\LWhisper\settings.json` **только на чтение** и никогда не пишет в него.

## Теневая проверка повторов (офлайн, только DevTools)

`dotnet <LWhisper.DevTools.dll> shadow-repeat <absolute-manifest.json> <absolute-new-result.json>`
сравнивает обычный и нейтральный beam на тех же сегментах. Использует исходник боевого
постфильтра, не взаимодействует с приложением и не вставляет текст. Каталог манифеста
содержит входные WAV; результат должен находиться вне него, новым файлом в существующем
подкаталоге `%LOCALAPPDATA%/LWhisper/bench` (папка LocalApplicationData на других ОС).
Git-деревья, Brain/Obsidian-хранилища и пути через ссылки запрещены. Используйте локальную
несинхронизируемую папку с доступом только для себя.
В JSON результата есть тексты, SHA-256, порядок, причины, подтверждённое покрытие и сборки
в порядке ID; stdout не предназначен для выдачи текста. Не переносите JSON/WAV в git.

**Проверка переносимости без моделей** (Python 3.11+, .NET 8, `$private` — существующий
локальный приватный подкаталог bench; `fixture-01` не должен существовать):

```powershell
dotnet build LWhisper.DevTools/LWhisper.DevTools.csproj -c Release
$dll = (Resolve-Path 'LWhisper.DevTools/bin/Release/net8.0/LWhisper.DevTools.dll').Path
python -I -X utf8 LWhisper.DevTools/tools/check-shadow.py $dll (Join-Path $private 'fixture-01')
```

Фикстура создаёт PCM и precomputed JSON с нуля; проверяет одну выбранную замену, влияние
на префикс следующего по завершению сегмента, точный порядок ID, отклонённые варианты
и неизменность входов. Это не измерение качества ASR.

**Свои локальные захваты.** Возьмите неизменяемую после копирования завершённую dev-запись
(`session.wav`, `seg-*.wav`, `meta.jsonl`) и сделайте отдельную рабочую копию в `$capture` внутри того же приватного bench.
Для простого подтверждающего прогона используйте запись с одним сегментом, сделанную на CPU
с beam и известной непустой эффективной подсказкой. При нескольких сегментах импорт покажет
диагностику `unknown-history`, если история не сертифицирована отдельным сравнением.
Из конфигурации **того прогона**, не из текущих настроек, возьмите модель, язык, tuning и
порог сжатия. `$effectivePrompt` — UTF-8 файл с уже объединённой эффективной подсказкой,
не сырой `vocabulary.txt` (см. обработку в WPF). Укажите абсолютные пути к уже установленной
модели Whisper, модулю `transcribe-cpp` (`dist/index.js`), совместимой локальной модели
Parakeet GGUF и `$capture`. Результаты обеих команд направляйте в приватные новые каталоги.

```powershell
# Пример для одного сегмента / CPU. Все переменные конфигурации относятся к захвату.
$wav = (Resolve-Path (Join-Path $capture 'seg-0001.wav')).Path
$flags = @('--input', $wav, '--model', $whisperModel, '--language', $language, '--beam',
           '--ctx-floor', $ctxFloor, '--thread-mode', $threadMode, '--parallel', $parallelism,
           '--format', 'json', '--quiet', '--repeat', '1')
# Если при захвате был реальный фиксированный override потоков, добавьте '--threads', $threads.
$a = Join-Path $private 'whisper-prompted-01'
$b = Join-Path $private 'whisper-neutral-01'
$w = Join-Path $private 'parakeet-01.jsonl'
$prov = Join-Path $private 'provenance-01.json'
if ((@($a, $b, $w, $prov) | Where-Object { Test-Path -LiteralPath $_ }).Count -ne 0) { throw 'Choose fresh paths' }
dotnet $dll shadow-decode @flags --prompt-file $effectivePrompt --out $a
if ($LASTEXITCODE) { throw 'Prompted run failed' }
dotnet $dll shadow-decode @flags --out $b
if ($LASTEXITCODE) { throw 'Neutral run failed' }
node LWhisper.DevTools/tools/parakeet-witness.mjs $dll $localTranscribeModule $parakeetModel $w $wav
if ($LASTEXITCODE) { throw 'Witness run failed' }
dotnet $dll shadow-check-output $prov
if ($LASTEXITCODE) { throw 'Unsafe provenance path' }
$effectiveHash = dotnet $dll shadow-prompt-hash $effectivePrompt
if ($LASTEXITCODE) { throw 'Effective prompt required' }
# $engineIdentity и $runtimeIdentity — известные идентификаторы сборки и среды захвата и плеч.
@{ engine = $engineIdentity; runtime = $runtimeIdentity; language = $language;
   captureBackend = 'cpu'; parallelism = $parallelism; compressionThreshold = $compressionThreshold;
   modelSha256 = (Get-FileHash $whisperModel).Hash.ToLowerInvariant();
   promptSha256 = $effectiveHash;
   effectivePromptFile = $effectivePrompt } | ConvertTo-Json | Set-Content -Encoding utf8 $prov
python -I -X utf8 LWhisper.DevTools/tools/import-shadow.py $dll $capture (Join-Path $a 'report.json') (Join-Path $b 'report.json') $w $prov
if ($LASTEXITCODE) { throw 'Capture import failed' }
dotnet $dll shadow-repeat (Join-Path $capture 'shadow-manifest.json') (Join-Path $private 'shadow-01.json')
```

Для Vulkan штатный `transcribe` **не равноценен**; используйте предварительно записанные
локальные плечи одного движка/модели/фактического бэкенда и политики beam. Манифест версии 1
показан в сгенерированной фикстуре: `sessions` с `history`, `compressionThreshold`, `segments`;
каждый сегмент содержит `id`, `order`, путь относительно корня и `wavSha256`, `archivedRaw`,
`archivedPost`, `prompted`, `neutral` и `witness`. Режим `validated-replay` выбирает известный ноябрьский фильтр: программа заново вычисляет
старые postfilter в наблюдаемом порядке и сравнивает их посимвольно с архивом. Повреждённый WAV
или расхождение блокируют этот сегмент и остаток истории; новый фильтр оценивается отдельно.
Импорт одной записи использует `single-segment` и текущую проверку сохранённого postfilter.
Отсутствующее или неподтверждённое плечо/свидетель можно передать как `null`: это не голос за
замену. Файлы с декодами привязаны к WAV по хешу, но идентичность раннера и контекста должна
подтверждаться локальными данными оператора. Новые естественные записи и точность не проверяются
ни синтетикой, ни ретроспективным прогоном.

`shadow-decode` требует явный свежий `--out` под тем же приватным bench. Его отчёт фиксирует
`effectivePromptSha256`: SHA-256 UTF-8 фактической строки после декодирования файла и `.Trim()`
(без BOM в хеше); для нейтрального плеча — явный `null`. Импорт сравнивает этот хеш с заявленной
подсказкой, а не только длину. Старые отчёты с одной длиной не подтверждают выбор; вручную
переносить такие данные в подтверждённые плечи нельзя. Независимо проверенные архивные пары
могут подаваться отдельно в предвычисленном манифесте с установленными хешами и происхождением.
