# ADR 0003. Хостинг обработчиков Disk Cleanup (`IEmptyVolumeCache`) в своём процессе

- Статус: принято для оценки размера (по результатам spike); очистка через обработчики не проверялась
- Дата: 2026-10-23
- Закрывает: Q1 из §16 ТЗ (`docs/spec/ТЗ Spacewise.md`), частично
- Код spike: `spikes/CleanupHandlers/` (консольный проект, только чтение, в `Spacewise.slnx` не входит)

## Контекст

В ТЗ («Решение по обработчикам Disk Cleanup») обработчики очистки Windows — это COM-объекты `IEmptyVolumeCache`/`IEmptyVolumeCache2`, зарегистрированные в `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches`. Если их можно хостить из своего процесса, Spacewise получает точный предварительный размер по каждой категории. У `cleanmgr /sagerun` такого нет. Контракт описан для Disk Cleanup manager, работа стороннего хоста помечена **[не верифицировано]**. Fallback по ТЗ — `cleanmgr /sageset:n` + `/sagerun:n` с замером тома.

Машина проверки:

| Параметр | Значение |
|---|---|
| ОС | Windows 10 Pro (по `ProductName`), `DisplayVersion` 26H2, build **26300.9711** (`Environment.OSVersion` = 10.0.26300.0). Это канал Insider / Windows 11 следующей ветки, не 24H2/25H2 |
| Пользователь | обычный, не elevated (`IsInRole(Administrator) = False`, spike отказывается работать elevated) |
| Рантайм | .NET SDK 10.0.402, `net10.0-windows`, x64 |
| Том | `C:\` |

## Что проверено

Порядок для каждого обработчика, каждый в отдельном дочернем процессе (STA), таймаут 60 с:

1. Ключ обработчика открыт только на чтение (`OpenSubKey(writable: false)`), его `HKEY` передан в `Initialize`/`InitializeEx`.
2. `CoCreateInstance` по CLSID из `(Default)` (через `Activator.CreateInstance(Type.GetTypeFromCLSID)`).
3. `QueryInterface` на `IEmptyVolumeCache2`. Если есть, вызывается `InitializeEx(hkey, "C:\", <имя ключа>, ...)`, иначе `IEmptyVolumeCache::Initialize`. Входные флаги `0`: `EVCF_OUTOFDISKSPACE` (0x20) и `EVCF_SETTINGSMODE` (0x40) не передаются.
4. Если `Initialize*` вернул успех (`S_OK` или `S_FALSE`), вызывается `GetSpaceUsed` с собственным callback (`ScanProgress` возвращает `S_OK`, `PurgeProgress` возвращает `E_ABORT`), затем `Deactivate`.
5. `Purge` и `ShowProperties` не вызывались. В интерфейсе они объявлены только как слоты vtable с именами `DoNotCall_*`.
6. До и после прогона снят снимок всего дерева `VolumeCaches` (все значения всех подключей). Результат: **дерево не изменилось**, значений `StateFlags*` нет ни до, ни после. `cleanmgr` не запускался.
7. UI: число видимых top-level окон своего процесса до и после. Ни один обработчик окна не показал.

### Результаты

Время: `Init` — `Initialize`/`InitializeEx`, `GetSpace` — `GetSpaceUsed`, «Процесс» — весь дочерний процесс вместе со стартом .NET. Флаги на выходе `Initialize*`: `0x1` `EVCF_HASSETTINGS`, `0x2` `EVCF_ENABLEBYDEFAULT`, `0x8` `EVCF_ENABLEBYDEFAULT_AUTO`, `0x10` `EVCF_DONTSHOWIFZERO`. У всех `Deactivate` вернул флаги `0x0`, новых окон 0, таймаутов не было.

| Обработчик | DLL | v2 | Init HRESULT | Флаги | GetSpaceUsed HRESULT | Байт | Init, мс | GetSpace, мс |
|---|---|---|---|---|---|---|---|---|
| Active Setup Temp Folders | DATACLEN.DLL | нет | S_OK | 0x10 | S_OK | 0 | 0 | 1 |
| BranchCache | PeerDistCleaner.dll | да | **0x80070005** (E_ACCESSDENIED) | 0x10 | не вызывался | — | 0 | — |
| Content Indexer Cleaner | DATACLEN.DLL | нет | S_FALSE | 0x0 | **0x80004005** (E_FAIL), Deactivate тоже 0x80004005 | 0 | 0 | 0 |
| D3D Shader Cache | D3DSCache.dll | да | S_OK | 0x0 | S_OK | 1 147 348 | 1 | 527 |
| Delivery Optimization Files | domgmt.dll | нет | S_OK | 0x0 | S_OK | 7 520 256 | 3 | 11 |
| Device Driver Packages | pnpclean.dll | да | S_FALSE | 0x0 | S_OK | 0 | 0 | 0 |
| Diagnostic Data Viewer database files | DATACLEN.DLL | нет | S_FALSE | 0x0 | S_OK | 0 | 1 | 2 |
| Downloaded Program Files | occache.dll | нет | S_OK | 0xB | S_OK | 0 | 0 | 1 |
| DownloadsFolder | DATACLEN.DLL | нет | S_OK | 0x0 | S_OK | 394 614 | 0 | 2 |
| Feedback Hub Archive log files | DATACLEN.DLL | нет | S_FALSE | 0x0 | S_OK | 0 | 1 | 1 |
| Internet Cache Files | ieframe.dll | да | S_OK | 0xB | S_OK | 449 853 | 10 | 1 |
| Language Pack | LanguagePackDiskCleanup.dll | да | S_FALSE | 0x0 | S_OK | 0 | 2 | 1 948 |
| Offline Pages Files | ieframe.dll | да | S_FALSE | 0x1 | S_OK | 0 | 12 | 2 |
| Old ChkDsk Files | DATACLEN.DLL | нет | S_OK | 0x10 | S_OK | 0 | 0 | 1 |
| Previous Installations | setupcln.dll | нет | S_FALSE | 0x10 | S_OK | 0 | 34 | 0 |
| Recycle Bin | shell32.dll | да | S_OK | 0x1 | S_OK | 1 762 344 090 | 0 | 109 |
| RetailDemo Offline Content | DATACLEN.DLL | нет | S_OK | 0x10 | S_OK | 0 | 0 | 1 |
| Setup Log Files | DATACLEN.DLL | нет | S_OK | 0x10 | S_OK | 0 | 0 | 1 |
| System error memory dump files | DATACLEN.DLL | нет | S_FALSE | 0x0 | S_OK | 0 | 1 | 1 |
| System error minidump files | DATACLEN.DLL | нет | S_FALSE | 0x0 | S_OK | 0 | 1 | 1 |
| Temporary Files | DATACLEN.DLL | нет | S_OK | 0x0 | S_OK | 2 260 992 | 19 | 1 718 |
| Temporary Setup Files | setupcln.dll | нет | S_FALSE | 0x10 | S_OK | 0 | 2 | 0 |
| Temporary Sync Files | wmp.dll | да | S_OK | 0x10 | S_OK | 0 | 0 | 1 |
| Thumbnail Cache | thumbcache.dll | да | S_OK | 0x2 | S_OK | 39 030 736 | 6 | 2 |
| Update Cleanup | scavengeui.dll | нет | S_FALSE | 0x10 | **0x800702E4** (ERROR_ELEVATION_REQUIRED) | 0 | 0 | 2 |
| Upgrade Discarded Files | setupcln.dll | нет | S_FALSE | 0x10 | S_OK | 0 | 3 | 1 |
| User file versions | fhcleanup.dll | да | S_OK | 0x10 | S_OK | 0 | 11 | 1 |
| Windows Defender | DATACLEN.DLL | нет | S_OK | 0x10 | S_OK | 0 | 0 | 1 |
| Windows Error Reporting Files | DATACLEN.DLL | нет | S_OK | 0x10 | S_OK | 0 | 0 | 2 |
| Windows ESD installation files | setupcln.dll | нет | S_FALSE | 0x10 | S_OK | 0 | 2 | 0 |
| Windows Reset Log Files | setupcln.dll | нет | S_FALSE | 0x10 | S_OK | 0 | 2 | 0 |
| Windows Upgrade Log Files | setupcln.dll | нет | S_FALSE | 0x10 | S_OK | 0 | 2 | 0 |

Итого: 32 обработчика, `CoCreateInstance` успешен у всех 32. `Initialize*` успешен у 31. `GetSpaceUsed` успешен у 29. Отказы: BranchCache (нет прав на `Initialize`), Update Cleanup (требует elevation), Content Indexer Cleaner (`E_FAIL`, ненужный устаревший обработчик). Один CLSID DATACLEN `{C0E13E61-...}` обслуживает 11 категорий, их различает переданный `HKEY`. `IEmptyVolumeCache2` поддерживают 10 обработчиков. Классические DATACLEN и setupcln его не поддерживают и не возвращают `DisplayName`: имя для UI надо брать из значения `Display` в реестре.

### Сверка с независимым замером

| Обработчик | Обработчик, байт | Независимо, байт | Как мерили | Вывод |
|---|---|---|---|---|
| Recycle Bin | 1 762 344 090 | 1 762 344 090 | `SHQueryRecycleBin("C:\")`, 23 объекта | совпадает точно |
| Thumbnail Cache | 39 030 736 | 39 030 736 | `thumbcache_*.db` (26 331 088) + `iconcache_*.db` (12 699 648) в `%LOCALAPPDATA%\Microsoft\Windows\Explorer` | совпадает точно; обработчик включает и icon cache |
| DownloadsFolder | 394 614 | 394 896 | все файлы `%USERPROFILE%\Downloads` | разница 282 байта = `desktop.ini`, обработчик его исключает |
| Internet Cache Files | 449 853 | 440 247 | файлы `%LOCALAPPDATA%\Microsoft\Windows\INetCache` (без доступа к части) | близко, +2% |
| D3D Shader Cache | 1 147 348 | 5 734 400 | `%LOCALAPPDATA%\D3DSCache` целиком | обработчик считает только удаляемую часть кэша |
| Temporary Files | 2 260 992 | 1 658 639 079 всего; 10 929 943 с `LastWriteTime` > 7 дней | `%TEMP%`, 22 648 файлов | обработчик консервативен: только давно не используемые и не заблокированные файлы. Сумма по `%TEMP%` в разы больше того, что Windows считает безопасным к удалению |

## Решение

1. Для **оценки размера** категорий Disk Cleanup Spacewise хостит системные обработчики в своём процессе: `CoCreateInstance` → `InitializeEx`/`Initialize` с `HKEY` ключа, открытым только на чтение, → `GetSpaceUsed` → `Deactivate`. Работает без прав администратора. Время — единицы миллисекунд, максимум ~2 с (Temporary Files, Language Pack), UI нет, реестр не меняется.
2. Каждый обработчик вызывается в отдельном worker-процессе (или хотя бы в отдельном STA-потоке с таймаутом 60 с и возможностью убить процесс). Падение или зависание чужой DLL не должно ронять Spacewise.
3. `S_FALSE` от `Initialize*` трактуется как «нечего чистить». Категория скрывается, если в выходных флагах есть `EVCF_DONTSHOWIFZERO` и размер 0.
4. `E_ACCESSDENIED` и `ERROR_ELEVATION_REQUIRED` означают «нужен elevated-брокер». В MVP такие категории (BranchCache, Update Cleanup) показываются как «требуют прав администратора», без цифры.
5. Отображаемое имя: `ppwszDisplayName`, если он есть, иначе значение `Display` из ключа обработчика (может быть ссылкой `@dll,-id`, её разрешать через `SHLoadIndirectString`).
6. **Очистка** (`Purge`) через свой хост в этом spike не проверялась. До отдельного spike на тестовой ВМ очистка выполняется через fallback из ТЗ (`cleanmgr /sageset:n` + `/sagerun:n` с замером тома), либо через собственные реализации для простых категорий (Корзина — `SHEmptyRecycleBin`).

## Последствия

- Экран «Системная очистка» получает точные цифры по категориям без UAC и без `cleanmgr`. Для Корзины и кэша эскизов цифры подтверждены побайтно.
- Обработчики DATACLEN считают меньше, чем «сырой» размер папок (Temporary Files: 2,3 МБ против 1,66 ГБ в `%TEMP%`). В UI нужна подпись: «по правилам Windows», без смешивания с собственным сканом `%TEMP%`.
- Нужна обёртка с изоляцией процесса и таймаутом, плюс маппинг HRESULT → статус категории.
- Категории, которые больше всего весят на старых системах (Update Cleanup, Previous Installations, Windows ESD), без прав администратора недоступны или пусты. Нужна проверка в elevated-брокере.
- Список обработчиков нельзя зашивать: на этой сборке 32 ключа, на других сборках набор отличается. Его надо перечислять из реестра при каждом запуске.

## Что не проверено

- Windows 10 22H2, Windows 11 24H2 и 25H2: проверена только build 26300 (канал Insider). Q1 в ТЗ остаётся открытым для этих сборок.
- Запуск от администратора / из elevated-брокера: Update Cleanup, BranchCache, Previous Installations и другие категории с реальными данными (на этой машине нет `C:\Windows.old`, `$Windows.~BT`, `MEMORY.DMP`).
- `Purge` из своего хоста, прогресс `PurgeProgress`, отмена через `E_ABORT`. Не вызывался намеренно.
- Флаги `EVCF_OUTOFDISKSPACE` и `EVCF_SETTINGSMODE`: не передавались, их влияние на размер не измерено.
- Сравнение с цифрами самого `cleanmgr` (для этого нужен его запуск, его не делали, чтобы не трогать `StateFlags`).
- Тома, кроме `C:\`; повторяемость времени (один прогон, «холодный» для каждого дочернего процесса).
- Поведение при вызове из MTA, из фоновой службы или из задачи Планировщика без интерактивной сессии.
