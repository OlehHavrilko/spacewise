# Cleaner starter

Проверяемый каркас Windows-приложения, не готовый очиститель. Реализованы WPF-окно, выбор локальной папки, чтение метаданных без повышения прав, отмена, исключения, до 100 крупнейших файлов, частичные результаты и отказ от любых мутаций. Модель — явно отключённый stub. Сканер ограничен 500 000 файлов и 256 уровнями каталогов, настраиваемыми в ScanRequest. Обход depth-first с приостановленными enumerators не теряет широкие каталоги из-за ширины очереди. Ссылки и выявленные deferred/cloud объекты пропускаются; физически выделенный объём неизвестен; hard links могут быть посчитаны повторно. Конкурентная подмена ссылок всё ещё требует отдельной проверки в production scanner. Исключения намеренно сравниваются без учёта регистра: это консервативная политика, которая в case-sensitive каталогах исключает и case-вариант пути. Это не утверждение, что все пути Windows case-insensitive. Завершающие разделители и Copy as path кавычки нормализуются. Completed означает завершённую проверку разрешённой области; исключённые политикой объекты подсчитываются отдельно от непроверенных из-за лимита глубины. Ошибки доступа и настоящие лимиты дают Partial. Содержимое файлов не читается.

## Сборка

Windows, .NET SDK 10.0.402 (закреплён в `global.json`). Выполнять из `starter`:

```powershell
dotnet restore Cleaner.slnx
dotnet build Cleaner.slnx -c Release --no-restore
dotnet run --project tests/Cleaner.SmokeTests -c Release --no-build
dotnet run --project tests/Cleaner.SmokeTests -c Release --no-build -- --regression
dotnet run --project src/Cleaner.Desktop -c Release --no-build
dotnet publish src/Cleaner.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

SmokeTests — самостоятельный исполняемый harness, а не `dotnet test`/xUnit. Он создаёт уникальные временные файлы, проверяет их сохранность после сканирования и удаляет только собственные fixture-файлы в конце. Release-бинарники без подписи; публикация self-contained требует наличия/загрузки runtime packs. Сборка UI не доказывает качество интерфейса: до релиза нужны отдельные ручные проверки клавиатуры, Narrator, DPI и High Contrast.

## Проекты и следующий шаг

- `Cleaner.Core`: контракты, состояния, analysis-only scanner, запрещающая политика, отключённая модель.
- `Cleaner.Desktop`: минимальный интерфейс; code-behind допустим для демонстрации, production перейти на MVVM.
- `Cleaner.SmokeTests`: проверки метаданных, исключений, отмены, лимита, неизменности fixture и политики.

В roadmap остаются SQLite/history, физические размеры и file IDs, категории, планирование, preview, broker/UAC, безопасная очистка, модельный worker, quarantine, обновления, installer, подпись, CI и расширенные тесты. Ни один из этих компонентов здесь не симулируется как готовый. Сначала реализовать handle-based scanner и policy tests, затем отдельный тщательно проверенный executor. До этого не добавлять кнопку удаления.

## Проверка 23 октября 2026

На Windows x64 с SDK 10.0.402 и runtime 10.0.13: финальный Release build — 0 warnings/errors; smoke harness — 22 checks passed; regression suite — 7/7 passed (включая реальный каталог с 10 002 дочерними папками); финальный self-contained win-x64 publish — success. Отдельный WPF smoke создаёт окно и проверяет загрузку XAML без показа: `Cleaner.Desktop.exe --smoke-test`, exit 0. При разработке smoke bootstrap сначала упал с `ArgumentNullException` на `StartupUri=null` (exit -532462766); исправлен явным запуском окна в OnStartup, повторная проверка прошла. Обычный скрытый запуск также создал responsive окно и был закрыт. Ручная проверка внешнего вида, клавиатуры и Narrator ещё не выполнена. Эти проверки не подтверждают безопасность будущей очистки.

Проверки облачных флагов используют отдельные `ReparsePoint`, `Offline`, `RecallOnOpen` (0x40000) и `RecallOnDataAccess` (0x400000); одна политика применяется к корню/родителям, очереди каталогов и элементам. Настоящая NTFS fixture с Offline на корне проверяет отказ до перечисления. UNC и drive-letter network mappings (`DriveType.Network`) отклоняются. Сетевая классификация проверена синтетически; реальный сетевой mapping и реальные облачные providers не тестировались. Нельзя гарантировать отсутствие любых сетевых/provider эффектов metadata API: races, изменения mount points и реализация провайдера требуют дальнейших тестов. Сканер не открывает содержимое и пропускает обнаруженные deferred объекты.

До исправлений regression suite воспроизвёл 7 ошибок: exclusions давали Partial, missing root определялся как link, разные регистры/завершающий separator/корень диска ломали исключения, широкое дерево теряло каталоги без diagnostic, ровно MaximumFiles давало ложный Partial. После исправлений все 7 проходят. Промежуточная сборка расширенных тестов упала с CS0136 (локальные missing/exact повторялись в nested scope); переменные переименованы и финальная сборка/тесты выполнены заново. Два no-build запуска после той неуспешной сборки использовали старый бинарник и не считаются проверкой исправлений. Подробности в исследовательской заметке starter_validation.md.
