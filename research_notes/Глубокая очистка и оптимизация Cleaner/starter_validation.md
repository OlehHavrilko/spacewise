# Исправления каркаса после ревью

Дата 23 октября 2026. Изменён только существующий read-only starter и его документация; продуктовые исследования глубокой очистки не означают реализацию нового executor. Проверялся локальный код, а не внешние anchors и не неподтверждённые числа WinSxS конкретного пользователя.

## Воспроизведение

На прежнем коде запущен новый `dotnet run --project tests/Cleaner.SmokeTests -c Release -- --regression`, exit 1:

```text
FAIL intentional exclusion should complete selected scope
FAIL missing root should report not found, not a link
FAIL Windows exclusion accepts alternate casing
FAIL trailing separator excludes root as one subtree
FAIL drive root exclusion covers descendants
FAIL queue truncation has explicit diagnostic
FAIL exact file limit is complete when nothing remains
Regression failures: 7
```

Широкое дерево воспроизведено реальной собственной fixture с 10 002 пустыми дочерними папками. Это проверка ширины, не бенчмарк WinSxS и не заявление об ускорении. Тест удаляет только собственные точно перечисленные временные файлы и пустые директории без рекурсивного удаления чужого дерева.

## Что исправлено

1. Вместо очереди до 10 000 ожидающих каталогов — depth-first stack открытых enumerators. Родительский enumerator приостанавливается на ребёнке, затем продолжается; широкая папка обходится полностью, siblings не теряются. Все enumerators освобождаются в finally при завершении, ошибке и отмене. Ограничена глубина, default 256 в ScanRequest; превышение отдельно считает UnvisitedDirectories и добавляет путь/diagnostic «Лимит глубины», state Partial. Это честный resource bound, не скрытый пропуск.
2. SkippedByPolicy отделён от реального недостающего покрытия. Указанные исключения и обнаруженные запрещённые deferred/link объекты не превращают завершённую разрешённую область в Partial; UI показывает число пропусков. Ошибки перечисления/доступа и настоящие лимиты дают Partial. Не путать Completed с измерением всего физического диска.
3. Ancestors проверяются сверху вниз через File.GetAttributes. Несуществующий путь даёт настоящий not-found diagnostic, а не `DirectoryInfo.Attributes == -1`, который ошибочно подходил под битовую link/cloud mask. Unsafe ancestor отклоняется до проверки дочернего объекта; прежний cloud/deferred mask сохранён. Snapshot race не устранён и всё ещё ограничение read-only starter.
4. Входные пути нормализуют Copy as path кавычки и trailing directory separator с сохранением корня диска. Exclusion matching учитывает directory boundary и root `C:\`/другого диска. Case comparison намеренно OrdinalIgnoreCase: для текущего read-only инструмента это консервативная политика, которая может исключить дополнительный case-вариант в case-sensitive дереве. Не выдаётся за универсальную семантику всех Windows каталогов.
5. Небольшое дополнительное исправление: достижение ровно MaximumFiles само по себе не означает неполноту; Partial возникает только при обнаружении следующего подходящего файла. В UI исправлено устаревшее описание регистра исключений. Форматирование размеров/LiveRegion и другие UI improvements отложены: scope — исправления 1–4.

## Реальная проверка после исправлений

Первая сборка расширенного harness выявила CS0136 для повторно названных локальных переменных `missing`/`exact`. Исправлены только имена regression-переменных. Два no-build запуска после той неуспешной сборки исполнили старый бинарник и не используются как evidence новых исправлений.

Затем заново выполнено:

```text
dotnet build Cleaner.slnx -c Release --no-restore
Build succeeded, warnings 0, errors 0, exit 0

dotnet run --project tests/Cleaner.SmokeTests -c Release --no-build
22 checks passed, exit 0

dotnet run --project tests/Cleaner.SmokeTests -c Release --no-build -- --regression
PASS intentional exclusion should complete selected scope
PASS missing root should report not found, not a link
PASS Windows exclusion accepts alternate casing
PASS trailing separator excludes root as one subtree
PASS drive root exclusion covers descendants
PASS wide directory with 10002 children is fully traversed
PASS exact file limit is complete when nothing remains
Regression failures: 0, exit 0

dotnet publish src/Cleaner.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64
Published successfully, exit 0

artifacts/win-x64/Cleaner.Desktop.exe --smoke-test
Published WPF XAML smoke exit code: 0
```

Smoke harness проверяет также прежние invariants/cloud root, quoted path, соседний directory-prefix, configurable depth limit и возврат к ancestors. Это проверка read-only scaffolding, не destructive-cleanup security certification. Реальный NTFS Offline-root fixture проверен; настоящие cloud providers/network shares, case-sensitive NTFS directory, ручной Narrator/DPI, race-free handle traversal и performance benchmarks остаются явно непроверенными.
