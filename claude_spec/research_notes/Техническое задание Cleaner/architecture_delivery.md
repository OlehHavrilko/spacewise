# Архитектура, стек и конвейер поставки Cleaner (дополнение к «Архитектура и сборка Cleaner»)

Дата: 2026-10-23. Этот файл дополняет `research_notes/Архитектура и сборка Cleaner/architecture.md`. Там уже есть: сравнение WPF/WinUI/Tauri/Electron, черновик схемы SQLite, broker allowlist, бюджеты производительности, оценка сроков 6–9 мес. Здесь не повторяю это. Добавляю: решения на октябрь 2026 с источниками, конкретику по broker/IPC/удалению по handle, catalog-модель, quarantine/recovery, подпись, CI, чек-лист безопасности, структуру решения и фазы MVP → v1 → v2.

Пометки: «Cited» = подтверждено источником в этой сессии или в предыдущих заметках. «Inferences» = мнение и проектное решение автора заметок. Подробности о библиотеках, которые в этой сессии не проверялись (CommunityToolkit.Mvvm, StreamJsonRpc, FlaUI, TUnit, Serilog), вынесены в Gaps. Версии нужно зафиксировать отдельным dependency spike.

Что видно в коде starter (`starter/src/Cleaner.Core`), проверено чтением 2026-10-23:
- `MetadataScanner` делает на каждую запись `Directory.EnumerateFileSystemEntries` → `File.GetAttributes(path)` → для файлов ещё `new FileInfo(path).Length`. Это 2 лишних path-based syscall на запись поверх данных, которые FindNextFile уже вернул.
- Лимит стека — это `MaximumDirectoryDepth = 256` (`Contracts.cs:5`), то есть глубина DFS, а не число каталогов. DFS идёт через стек enumerator'ов, поэтому плоские 28k подкаталогов WinSxS сами по себе лимит не задевают. В README/ранних заметках упоминается лимит 10 000. Нужно сверить README и код. Документированный багрепорт и код расходятся.
- `ScanPaths.IsExcluded` уже использует `StringComparison.OrdinalIgnoreCase` (`Contracts.cs:35-37`). Пункт «case-sensitive exclusions» в текущей версии кода уже исправлен либо относится к другой ветке. Правильная модель: исключения по file identity плюс case-insensitive сравнение по пути. Для каталогов с per-directory case sensitivity (WSL) нужно отдельное правило.
- `CheckAncestors` вызывает `File.GetAttributes` для корня. Если корня нет, бросается `FileNotFoundException`. Сообщение пользователю при этом — про «ссылки/Offline». Нужен отдельный код результата `RootNotFound`.
- `skipped` суммирует policy-skip и coverage gap. `SkippedByPolicy` и `UnvisitedDirectories` отделены, но UI/итог должны показывать три разные корзины: «исключено политикой», «недоступно (ACL/IO)», «не досканировано (лимит/отмена)».

---

## 1. UI-фреймворк на октябрь 2026: WPF (.NET 10 + Fluent) vs WinUI 3 / Windows App SDK vs Avalonia

### Takeaway
Оставить WPF на .NET 10 LTS. Fluent-тему включить через `ThemeMode`, но с запасным путём на системные кисти. Причины: существующий skeleton, зрелый UIA/Narrator, простая поставка unpackaged EXE/MSI и elevated broker, отсутствие зависимости от Windows App SDK runtime. WinUI 3 (WinAppSDK 2.x) — разумная альтернатива только при старте с нуля. Avalonia 12 имеет смысл только при реальном плане кроссплатформенности, а его нет: продукт Windows-only.

### Cited Findings
- .NET 10 вышел в ноябре 2025. В WPF .NET 10 Fluent-стили получили DatePicker, GridSplitter, GridView, GroupBox, Hyperlink, Label, NavigationWindow, RichTextBox, TextBox. Исправлены падения в HighContrast, добавлен `RecognizesAccessKey`, исправлен RTL. Microsoft прямо пишет: «Fluent UI style support is still in progress» — [What's new in WPF .NET 10](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100).
- Производительность WPF 10: меньше аллокаций в UI Automation и file dialogs, font collection loader переведён на managed code, оптимизирован XAML parsing и dynamic resources. Новое: короткий синтаксис `Grid ColumnDefinitions="1*, Auto"`, расширенный `MessageBoxButton` — [там же](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100).
- `Application.ThemeMode`/`Window.ThemeMode` принимает значения None/System/Light/Dark, по умолчанию None. API помечено experimental (диагностика `WPF0001`). При установке ThemeMode не следует вручную подключать Fluent-словари — [ThemeMode API](https://learn.microsoft.com/dotnet/api/system.windows.application.thememode).
- .NET 10 LTS: patch 10.0.13 (2026-10-13), поддержка до 2028-11-14. .NET 8/9 заканчивают поддержку в ноябре 2026 — [Support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), уже в предыдущей заметке.
- Windows App SDK 2.0 стал стабильным весной 2026 и перешёл на semver. Официальные release notes перечисляют релизы до 2.4.0 (2026-08-13) — [WinAppSDK 2.0 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0). Дата 2026-04-29 для 2.0 взята из вторичного источника — [issoh.co.jp](https://www.issoh.co.jp/tech/details/10595/).
- Microsoft рекомендует WinUI 3 для новых Windows-приложений — [Windows apps](https://learn.microsoft.com/en-us/windows/apps/), из предыдущей заметки.
- Avalonia 12 вышла в 2026 году как «foundations release» с упором на performance/stability. Вендор заявляет встроенную accessibility через платформенные API на Windows. В 12.1 есть initial WinUI embed — [Avalonia 12 blog](https://avaloniaui.net/blog/avalonia-12), [12.1.0 notes](https://newreleases.io/project/github/AvaloniaUI/Avalonia/release/12.1.0). Подробностей по Windows UIA для 12.x не найдено.
- WPF UI virtualization не даёт data virtualization — [WPF controls perf](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls), из предыдущей заметки.
- Trimming для WPF отключён в SDK: «Almost no WPF apps are runnable after trimming» — [Trimming incompatibilities](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities).

### Inferences
| Критерий | WPF .NET 10 | WinUI 3 / WinAppSDK 2.x | Avalonia 12 |
|---|---|---|---|
| Зрелость/риск | 20 лет, предсказуемо. Fluent ещё «in progress», ThemeMode experimental | Быстро растёт, semver с 2.0. Истории багов и breaking changes больше | Стабильна, но для Windows-only даёт лишний слой |
| A11y (UIA/Narrator) | Зрелые AutomationPeer, в .NET 10 исправлен HighContrast | Нативная UIA, хорошая | Заявлена, деталей для Windows не нашёл |
| Большие списки | `VirtualizingStackPanel` + `Recycling` + paged ViewModel. Treemap рисовать в `DrawingVisual`/`WriteableBitmap` | `ItemsRepeater`, хорошая virtualization. Treemap через Win2D/Composition | Свой компостер на Skia, быстрый. Treemap через custom render |
| Поставка | Unpackaged EXE/MSI/Velopack без runtime-зависимостей, R2R, self-contained | Нужен WinAppSDK runtime: framework package либо self-contained mode. Unpackaged возможен, но сложнее | Как у .NET-приложения |
| Skeleton | Уже есть, переиспользуется | Переписать UI | Переписать UI |

Решение: WPF. Конкретные правила:
1. `ThemeMode="System"` с `<NoWarn>WPF0001</NoWarn>`, все цвета только через `DynamicResource`. Smoke-тест HighContrast в CI. Если в Fluent-стиле нужного контрола баг, свой стиль на базе системных кистей.
2. Списки: `ListView`/`DataGrid` с `VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`. Данные идут страницами из SQLite через `IItemsProvider` (data virtualization), в памяти держать не больше ~5–10k строк.
3. Treemap: squarified layout считать в фоне. Рендер в один `DrawingVisual` либо `WriteableBitmap`, а не тысячи `UIElement`. Hit-test по прямоугольникам. Обязательно дублирующая таблица для доступности (уже требование из предыдущей заметки).
4. `ElementHost`/WebView2 не нужны.

### Gaps
- Сравнимых бенчмарков WPF vs WinUI vs Avalonia на списках 1M+ и treemap в 2026 не найдено.
- Не проверено, выйдет ли WPF ThemeMode из experimental в .NET 11 (ноябрь 2026).
- Детали UIA Avalonia 12 на Windows не подтверждены первичным источником.

---

## 2. MVVM, DI, навигация

### Takeaway
CommunityToolkit.Mvvm (source generators `[ObservableProperty]`, `[RelayCommand]`, `IMessenger`) плюс `Microsoft.Extensions.Hosting` (Generic Host) в WPF. Навигация — простой `INavigationService` с ViewModel-first и `DataTemplate`-маппингом. Prism и ReactiveUI не нужны.

### Cited Findings
- В этой сессии первичные документы CommunityToolkit.Mvvm и Generic Host для WPF не перепроверялись. Выбор опирается на общеизвестную практику, см. Gaps.
- Velopack требует bootstrap в точке входа, для WPF рекомендован custom `Main` — [Velopack C#](https://docs.velopack.io/getting-started/csharp), из предыдущей заметки. Custom `Main` хорошо совпадает со сборкой Host до `App.Run()`.

### Inferences
- Точка входа: `Program.Main` → `VelopackApp.Build().Run()` (если выбран Velopack) → `Host.CreateApplicationBuilder()` → регистрации → `app.Run(mainWindow)`. `StartupUri` убрать, в starter уже есть явный `OnStartup`.
- Время жизни: ViewModel экранов — transient. Сервисы `IScanService`, `IPlanService`, `IBrokerClient`, `IDbWriter` — singleton. Сам scan job — scoped на время задачи.
- Hosted services в UI-процессе используются только для задач внутри жизненного цикла окна: writer queue, checkpoint. Никаких фоновых процессов после закрытия окна — требование владельца.
- Межмодульные события идут через `WeakReferenceMessenger`. Прогресс сканера идёт через `Channel<T>` с throttling 4 Hz, а не через `IProgress` на каждый файл.
- Code-behind starter перенести в ViewModel поэтапно: сначала MainViewModel, потом остальное.

### Gaps
- Актуальные версии CommunityToolkit.Mvvm и Microsoft.Extensions.Hosting на октябрь 2026 не проверены. Зафиксировать в `Directory.Packages.props` после spike.

---

## 3. Разделение привилегий: unelevated UI + elevated broker по требованию

### Takeaway
UI всегда `asInvoker`. Broker — отдельный exe из Program Files, запускается через `ShellExecuteEx` с verb `runas` на один план и завершается после него (idle timeout 30–60 с). Это не служба. IPC: именованный pipe с уникальным именем и явной DACL. Протокол — StreamJsonRpc (JSON-RPC) с закрытым набором типизированных методов. Аутентификация двусторонняя: UI передаёт broker'у одноразовый секрет через командную строку или наследуемый handle, broker проверяет PID/образ клиента. Каждая деструктивная операция выполняется по handle: открыть с `FILE_FLAG_OPEN_REPARSE_POINT`, сверить FileId/VolumeSerial с планом, удалить через `FileDispositionInfoEx` с POSIX-семантикой.

### Cited Findings
- По умолчанию DACL named pipe даёт read-доступ Everyone/anonymous. Нужна явная DACL — [Named pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), из предыдущей заметки.
- `PipeOptions.CurrentUserOnly` используется в Roslyn compiler server. Там же дополнительно сверяют identity клиента через `RunAsClient` и сравнивают elevation — [NamedPipeUtil.cs](https://source.dot.net/dotnet/NamedPipeUtil.cs.html).
- `FILE_DISPOSITION_FLAG_POSIX_SEMANTICS`: имя исчезает из каталога, когда закрывается delete-handle, даже если у других процессов остались открытые handle. Без флага файл «висит» до закрытия всех handle — [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex).
- Из user mode используется `SetFileInformationByHandle(FileDispositionInfoEx = 21)`. Ex-класс работает только на NTFS. Для FAT/exFAT нужен fallback на классический `FileDispositionInfo` при `ERROR_INVALID_PARAMETER` — [Go x/sys commit](https://go.googlesource.com/sys/+/d9b008d0a637a6daa9d387171bc2d206f4de1691%5E%21/), [Zig commit](https://shale-1.onrender.com/zig/commit/1ae378e7a253ec845d8ee8d52b5505c9aa177369?diff=split). Это вторичные источники. Первичная user-mode страница struct `FILE_DISPOSITION_INFO_EX` по угаданному URL вернула 404.
- MSIX: повышение прав через restricted capability `allowElevation`. Смешивать full trust и auto-elevation не нужно — [App capabilities](https://learn.microsoft.com/th-th/windows/apps/package-and-deploy/app-capability-declarations), из предыдущей заметки.

### Inferences
**Запуск broker:**
1. UI создаёт 32-байтный секрет (CSPRNG) и уникальное имя pipe `\\.\pipe\Cleaner.Broker.{sessionId}.{guid}`.
2. UI создаёт pipe-сервер сам. Тогда broker — клиент и не может угнать имя. DACL: SYSTEM + Administrators + SID текущего пользователя (logon SID), флаг `FILE_FLAG_FIRST_PIPE_INSTANCE`, `PIPE_REJECT_REMOTE_CLIENTS`.
3. `ShellExecuteEx(runas, "C:\Program Files\Cleaner\Cleaner.Broker.exe", "--pipe <name> --parent <pid>")` с `SEE_MASK_NOCLOSEPROCESS`. Секрет передаётся первым сообщением по pipe, а не в argv: argv виден другим процессам.
4. Broker при подключении: `GetNamedPipeServerProcessId` → совпадает с `--parent`. Образ процесса = `%ProgramFiles%\Cleaner\Cleaner.exe`, Authenticode совпадает с собственным. Session ID совпадает. Затем challenge с секретом. Broker завершается, если родитель умер (`WaitForSingleObject` на handle родителя).
5. Обратная схема «broker = сервер» хуже: unelevated malware в той же сессии может подключиться первым, если DACL допускает пользователя.

**Confused deputy / EoP:**
- Broker не принимает пути, которые не подтверждены его собственной политикой. Запрос — это `ExecutePlan(planId, planDigest)`. Plan содержит `(volumeGuid, fileId, expectedPath, expectedSize, expectedLastWrite, categoryId, ruleVersion)`.
- Broker заново вычисляет допустимость по своему protected-paths списку. Этот список вкомпилирован в broker и хранится в Program Files, не в LocalAppData.
- Broker не читает SQLite пользователя как источник истины. План приходит по pipe, каждая запись повторно проверяется.
- Отдельные «системные» действия — enum без параметров-строк: `DismStartComponentCleanup`, `DismResetBase` (необратимо, отдельное подтверждение), `DeleteShadowStorage(volumeGuid, keepNewest:int)`, `SetHibernation(off|reduced)`, `DriverStoreRemove(publishedName oemNN.inf)` (проверить, что пакет не используется устройствами), `CompactVhdx(vhdxPath)` — только файл ext4.vhdx, найденный по `HKCU\...\Lxss` пользователя-инициатора, и только после `wsl --shutdown`.
- Внешние процессы (`dism.exe`, `vssadmin`, `pnputil`, `powercfg`) запускать по абсолютному пути `%SystemRoot%\System32\...`. Аргументы — массив, без shell. `CreateProcess` с `lpApplicationName`, чтобы не было DLL/exe hijack через CWD/PATH. Для DISM лучше `DismApi.dll` или `Microsoft.Dism` API, а не парсинг stdout. Это нужно проверить.
- Broker загружает DLL только из своей папки и System32: `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32|APPLICATION_DIR)`. Папку установки обычный пользователь не может изменить.

**TOCTOU-безопасное удаление (алгоритм executor, общий для UI-процесса и broker):**
1. Открыть родительский каталог по пути с `FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT`. Проверить, что родитель не reparse point, а его FileId совпадает с сохранённым.
2. Открыть цель относительно handle родителя через `NtCreateFile` с `RootDirectory`. Это исключает подмену промежуточных компонентов пути. Флаги: `FILE_OPEN_REPARSE_POINT`, доступ `DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE`, share `FILE_SHARE_DELETE` (+READ/WRITE по политике).
3. `GetFileInformationByHandleEx(FileIdInfo)` → `VolumeSerialNumber + FileId (128-bit)` = ожидаемые. `FileBasicInfo`: атрибуты без `ReparsePoint/Offline/RecallOn*`. `FileStandardInfo`: `NumberOfLinks`. Если ссылок больше одной, место не освобождается — пометить «hardlink, 0 байт выгоды» и решать по политике.
4. Повторная проверка размера/времени против плана. При расхождении — skip с кодом `StaleObservation`.
5. `SetFileInformationByHandle(FileDispositionInfoEx, DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE)`. Fallback для не-NTFS — `FileDispositionInfo`.
6. Каталоги удалять bottom-up тем же способом, только пустые. Рекурсивное удаление дерева по пути не использовать никогда.
7. Занятые файлы: Restart Manager (`RmStartSession`/`RmRegisterResources`/`RmGetList`) только для показа «кем занят». Процессы не убивать автоматически. Вариант `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` — только в broker и только с явным пользовательским выбором.

**Канонизация путей:** `\\?\` + `GetFinalPathNameByHandle(VOLUME_NAME_GUID)` для журнала. Отображаемый путь хранится отдельно. Отказывать в 8.3-коротких именах в правилах. Отказывать в ADS (`:`), в `\\?\GLOBALROOT`, в device paths, в UNC. Сравнение case-insensitive через `CompareStringOrdinal(ignoreCase)`, кроме каталогов с `FILE_CASE_SENSITIVE_DIR`.

**Protected paths (deny-by-default, проверка в broker и в UI):** корни томов; `%SystemRoot%` кроме явно разрешённых подпапок (`Temp`, `SoftwareDistribution\Download`, `Logs\CBS` после проверки, `LiveKernelReports`, `Minidump`, `Prefetch` — по умолчанию нет); `WinSxS` — только через DISM; `System32\DriverStore` — только через pnputil; `Program Files*` и `ProgramData` кроме каталожных правил; профили других пользователей; `$Recycle.Bin` других SID; `System Volume Information`; pagefile/swapfile/hiberfil — только через powercfg; OneDrive/Dropbox и любые cloud-placeholder; папка самого Cleaner, включая quarantine.

### Gaps
- Не проверено, позволяет ли `ShellExecuteEx runas` надёжно передать наследуемый handle. По сути нет: ShellExecute не наследует handle, поэтому секрет передаётся через pipe-challenge.
- Не проверены актуальные StreamJsonRpc API для named pipe и MessagePack formatter. Рассмотреть MessagePack с жёсткими лимитами размера.
- Нет первичного user-mode документа по `FILE_DISPOSITION_INFO_EX`. Взять из `winbase.h` Windows SDK при реализации.
- DismApi из .NET (P/Invoke vs NuGet обёртки) не исследовался.

---

## 4. Plugin/catalog модель очистителей

### Takeaway
Декларативный каталог в собственном формате (JSON/YAML, schema-versioned), семантически близком к BleachBit CleanerML. Каждое правило: `id`, `version`, `detect`, `targets` (path pattern + filters), `action` (delete | truncate | registry-value | shell-op), `risk`, `requiresAdmin`, `evidence`, `tests`. Imperative-действия (DISM, VSS, WSL) — не в каталоге, а в коде broker под фиксированными `actionId`. Каталог может на них только ссылаться. Обновления каталога подписываются отдельным ключом (Ed25519/minisign или Authenticode-подписанный `.cat`). Broker принимает только каталог, встроенный в его версию.

### Cited Findings
- В этой сессии первичные описания CleanerML/Winapp2.ini не перечитывались. Общие сведения есть в `research_notes/Глубокая очистка и оптимизация Cleaner/cleanup_power.md`.

### Inferences
- Импорт Winapp2.ini разрешать только как read-only «предложения» с риском `community`, без авто-выбора. Winapp2 содержит wildcard-правила с `RECURSE`/`REMOVESELF`. Их безопасность не гарантирована.
- Версионирование: `catalogSchema` (major ломает парсер), `catalogVersion` (монотонный, downgrade запрещён), `ruleVersion` на правило. `ruleVersion` записывается в findings/journal.
- Подпись: `catalog.json` + `catalog.json.sig`. Публичный ключ вшит в app. Ротация ключа — через новый релиз app. Проверять подпись до парсинга, ограничивать размер файла.
- Каждое правило обязано иметь fixture-тест: «на этом дереве выбрать ровно это». CI валидирует схему и запускает fixtures.
- Code actions регистрировать через DI как `ICleanupAction` с атрибутом `[ActionId("dism.componentcleanup")]`. Сторонние DLL-плагины не поддерживать: это дыра в broker.

### Gaps
- Не найдено описание формата подписи каталогов у BleachBit/CCleaner. Сравнение не проведено.

---

## 5. Данные: SQLite-схема, доступ, миграции, размер

### Takeaway
Raw `Microsoft.Data.Sqlite` плюс тонкий слой (Dapper допустим) для горячих путей: bulk insert агрегатов и paged-запросы. EF Core не нужен. Две базы: `cache.db` (сканы, агрегаты, findings; регенерируемая, `synchronous=NORMAL`) и `journal.db` (планы, действия, quarantine-индекс, feedback, settings; `synchronous=FULL`). Миграции — нумерованные SQL-скрипты с `PRAGMA user_version`, бэкап через `VACUUM INTO` перед миграцией.

### Cited Findings
- Microsoft.Data.Sqlite — самостоятельный ADO.NET provider, EF Core необязателен — [Provider overview](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/), из предыдущей заметки.
- WAL: один writer, `SQLITE_BUSY` возможен, на сетевом диске не работает. WAL-reset bug исправлен в 3.51.3 и backports 3.50.7/3.44.6 — [SQLite WAL](https://www.sqlite.org/wal.html), из предыдущей заметки.

### Inferences
Расширение схемы из предыдущей заметки:
```sql
-- cache.db
scans(id INTEGER PK, started_utc, completed_utc, state, root_volume_guid, root_path, policy_version, catalog_version,
      files, dirs, logical_bytes, allocated_bytes, skipped_policy, skipped_access, unvisited, error_count);
folder_agg(scan_id, node_id, parent_id, volume_guid, file_id BLOB, name, depth, files, logical_bytes, allocated_bytes,
           newest_write_utc, oldest_access_utc, PRIMARY KEY(scan_id,node_id)) WITHOUT ROWID;
top_files(scan_id, path_id, file_id BLOB, logical_bytes, allocated_bytes, last_write_utc, links);
findings(id PK, scan_id, rule_id, rule_version, node_id, bytes_reclaimable, confidence, risk, source /*rule|model*/, evidence_json);
-- journal.db
plans(id PK, created_utc, scan_id, digest BLOB, state /*draft|approved|running|done|aborted*/, elevated INTEGER, expires_utc);
plan_items(plan_id, seq, action /*delete|quarantine|system_op*/, volume_guid, file_id BLOB, path, expected_size,
           expected_mtime, rule_id, state /*pending|in_progress|done|skipped|failed*/, PRIMARY KEY(plan_id,seq));
actions(id PK, plan_id, seq, started_utc, completed_utc, result_code, bytes_freed, detail);
quarantine(id PK, plan_id, seq, original_path, original_volume_guid, stored_path, sha256 BLOB, size, attrs, sddl,
           created_utc, expires_utc, restored_utc NULL, purged_utc NULL);
feedback(id PK, finding_id, verdict /*accept|reject|restore*/, created_utc);
settings(key PK, value_json);
```
- Пишет только один фоновый writer: `Channel<Batch>`, транзакции по 5–20k строк, prepared statements. Чтение — отдельные read-only соединения.
- Ограничение размера: хранить N последних полных сканов (по умолчанию 5). Старые урезать до `scans` + `folder_agg` с depth ≤ 3. `PRAGMA auto_vacuum=INCREMENTAL` и `incremental_vacuum` при закрытии. Лимит cache.db ~500 MB. `journal.db` не урезать автоматически, кроме purged quarantine старше 90 дней.
- Пути хранить через таблицу `paths(id, parent_id, name)` (дерево имён). Экономия ×5–10 на глубоких деревьях.

### Gaps
- Нет замеров insert-throughput Microsoft.Data.Sqlite vs Dapper на 5M строк. Нужен бенчмарк.
- Версию поставляемой native SQLite (`SQLitePCLRaw.bundle_e_sqlite3`) на октябрь 2026 не проверял. Требование ≥ 3.51.3 или backport.

---

## 6. Quarantine/restore между томами и восстановление после сбоя

### Takeaway
Quarantine всегда на том же томе, что исходный файл: `<volume>\.CleanerQuarantine\<planId>\` с DACL «только SYSTEM + Administrators + владелец». Перемещение — `SetFileInformationByHandle(FileRenameInfoEx, POSIX_SEMANTICS)` по уже проверенному handle. Это мгновенно и не занимает место. Кросс-томный quarantine (копирование) не делать: место не освобождается. Для системных операций (DISM, VSS, hiberfil) restore невозможен. Это показывать до подтверждения. Журнал write-ahead: каждый `plan_item` идёт `pending → in_progress (fsync) → done`. При старте незавершённый план откатывается или доводится.

### Cited Findings
- POSIX-семантика disposition/rename на NTFS — [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex). Rename-аналог в этой сессии не проверялся.

### Inferences
- Режимы: `delete` (без возврата; только кэши с `risk=low`); `quarantine` (по умолчанию для всего, что не кэш); `recycle` (`IFileOperation` с `FOFX_RECYCLEONDELETE`, для пользовательских документов; медленно, но привычно).
- Сроки: quarantine хранится 7–30 дней (настройка). Очистка — только при запуске UI или из one-shot задачи планировщика. Резидентного процесса нет.
- Restore: проверить, что `original_path` свободен (иначе суффикс ` (restored)`), что родитель существует (создать), что SHA-256 совпадает (если считался — только для файлов < N MB). Restore делается тем же rename по handle. Учитывать ACL: при переносе на том же томе DACL сохраняется.
- Recovery при старте: найти `plans.state='running'`. Для `in_progress`-элементов проверить, где сейчас объект по FileId (`OpenFileById`): в quarantine → done; на месте → pending; нигде → done с пометкой. Затем показать пользователю «план прерван: продолжить / откатить из quarantine».
- Освобождённое место показывать честно: после quarantine место ещё занято. Метрика «будет освобождено после очистки quarantine».

### Gaps
- Поведение quarantine на томах с BitLocker/дедупликацией (Data Dedup), ReFS, exFAT-флешках не проверено.
- `FileRenameInfoEx` POSIX-флаги и Windows 10-версии не перепроверены.

---

## 7. Упаковка, обновления, подпись, дистрибуция

### Takeaway
Канал 1 (основной): per-machine MSI (WiX 5/6) в `Program Files`, потому что broker должен лежать в каталоге, недоступном пользователю на запись. Обновление: проверка версии в UI, загрузка подписанного MSI, `msiexec` с UAC. Velopack в per-user режиме для этой архитектуры не подходит: setup.exe ставит в `%LocalAppData%`. Канал 2: winget manifest на тот же MSI. Канал 3 (опционально): Microsoft Store как MSI/EXE. Self-contained x64 + ReadyToRun, без trimming. Подпись: Azure Artifact Signing, если есть юрлицо в США/Канаде/ЕС/UK. Иначе OV-сертификат на HSM/облачном токене. EV ради SmartScreen больше не нужен.

### Cited Findings
- Velopack `Setup.exe` — per-user и без elevation, по умолчанию `%LocalAppData%\{packId}`. Per-machine install в Program Files возможен только через MSI-вариант `--msi --instLocation PerMachine`, с elevation. Updater при занятых файлах пытается убить процессы, а если они запущены от администратора, показывает ошибку и откатывается. Обновление заменяет всю папку `current` — [Velopack Windows](https://docs.velopack.io/packaging/operating-systems/windows).
- Azure Artifact Signing (бывший Trusted Signing) — GA с января 2026, ~$9.99/мес. Организации: США, Канада, ЕС, UK. Частные лица: только США и Канада. Мгновенной SmartScreen-репутации не даёт — [Code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options), [devclass, 2026-01-14](https://devclass.com/2026/01/14/code-signing-windows-apps-may-be-easier-and-more-secure-with-new-azure-artifact-service/).
- OV-сертификат: $150–300/год, ключ обязательно на HSM/токене (CA/B Forum, с июня 2023). EV: $400+/год, «That behavior was removed in 2024» — EV больше не обходит SmartScreen — [Code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options). Точная дата изменения EV в 2024 году в источниках расходится — [todesktop](https://www.todesktop.com/blog/posts/windows-apps-psa-ev-certs-do-not-grant-immediate-reputation-anymore), [MS Q&A](https://learn.microsoft.com/en-us/answers/questions/1846647/program-requirements-microsoft-trusted-root-progra).
- С 2026-03-01 максимальный срок публичных code signing сертификатов (включая EV) — 460 дней (Ballot CSC-31). DigiCert выдаёт на 459 — [DigiCert KB](https://knowledge.digicert.com/alerts/code-signing-certificates-459-day-validity), [Encryption Consulting](https://www.encryptionconsulting.com/shorter-code-signing-cert/).
- Store: MSIX переподписывается Microsoft бесплатно и показывается без SmartScreen. MSI/EXE в Store нужно подписать самому сертификатом из Trusted Root Program. Аккаунт разработчика бесплатный — [Code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options). Требования к MSI/EXE в Store: офлайн-установщик, silent-параметры, бинарник по URL не меняется после отправки — [Store MSI/EXE FAQ](https://learn.microsoft.com/windows/apps/publish/faq/submit-your-app).
- Для OSS-проектов SignPath Foundation даёт бесплатную OV-подпись — [Code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).
- winget: YAML-манифест с поддержкой MSI/EXE/MSIX, PR в `microsoft/winget-pkgs`, `wingetcreate`, `winget validate` — [winget manifest](https://learn.microsoft.com/windows/package-manager/package/manifest).
- Trimming для WPF отключён в SDK — [Trimming incompatibilities](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities).
- Self-contained включает runtime, его патчи становятся частью релиза приложения — [Publishing .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/), из предыдущей заметки.

### Inferences
- Почему не MSIX: elevated broker и DISM/VSS/powercfg из packaged-приложения требуют restricted capability `allowElevation` и ревью Store. Есть ограничения файловой/реестровой виртуализации и неудобная отладка. Возможный путь для v2 — отдельный «лёгкий» SKU без broker.
- Почему не Inno: MSI даёт стандартный per-machine lifecycle, GPO/Intune и `winget` без хаков. Inno допустим, но меньше подходит для enterprise.
- Почему не Velopack: требуется Program Files для broker. Velopack MSI per-machine существует, но его updater и сценарии с elevation — новая территория (см. ограничение про процессы администратора). Пересмотреть, если Velopack официально поддержит privileged dirs.
- Публикация: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:PublishSingleFile=false`. Single-file не нужен: MSI всё равно ставит папку, а single-file усложняет подпись внутренних DLL. Подписывать все свои `.exe/.dll` и `.msi`.
- Размер: self-contained WPF + R2R ≈ 150–200 MB на диске (оценка, нужно замерить). Framework-dependent меньше, но UAC-установщик должен ещё ставить Desktop Runtime. Для «чистильщика» самостоятельность важнее.
- Для владельца (физлицо вне США/Канады — предположение, не проверено): ближайшие варианты — OV-сертификат с облачным HSM (DigiCert KeyLocker / Sectigo / SSL.com eSigner) за $150–300/год с перевыпуском каждые ≤460 дней; либо регистрация юрлица в ЕС/UK для Artifact Signing; либо SignPath, если проект open source. Store MSI/EXE всё равно требует подпись.
- SmartScreen: репутация привязана к hash файла и копится по издателю. Подписывать каждый релиз одним и тем же identity, выпускать реже, не менять сертификат без нужды.
- Update-flow: `https://<host>/cleaner/releases.json`, подписанный тем же Ed25519-ключом, что и каталог. Проверка версии ≥ текущей, канал. Скачать MSI в `%LocalAppData%\Cleaner\updates`, проверить Authenticode + hash, запустить `msiexec /i ... /passive` и закрыть UI. Без фоновой службы обновлений.

### Gaps
- Сроки ревью Store для MSI-приложений с elevation/DISM и допустимость «system cleaner» по Store policies не проверены.
- Точный размер self-contained R2R сборки не замерен.
- Поддержка Program Files в Velopack после даты документации не подтверждена.

---

## 8. Тестирование и CI

### Takeaway
xUnit v3 (или TUnit) для unit/property-тестов. FsCheck/CsCheck для property-тестов политики: «ни один путь из protected-набора не проходит фильтр при любых комбинациях case/8.3/trailing dot/ADS/junction». Файловые fixtures — на реальном NTFS VHDX, который создаётся и монтируется в тесте. FlaUI (UIA3) для UI smoke и проверки accessibility-свойств. GitHub Actions `windows-2025`/`windows-2022` для build и unit. Destructive/elevated интеграционные тесты — на self-hosted Hyper-V VM со снапшотом.

### Cited Findings
- GitHub-образ Windows Server 2025 для hosted runners сначала был в public preview, набор инструментов отличается от 2022 — [GitHub changelog](https://github.blog/changelog/2024-12-18-windows-server-2025-is-now-in-public-preview/). Есть ли Hyper-V/`Mount-VHD` на hosted runner, не найдено.
- Предыдущая заметка: destructive-тесты только на VHD/VM со снапшотом, UI/UAC требуют интерактивной VM.

### Inferences
- Создание VHDX без модуля Hyper-V: `diskpart` скриптом (`create vdisk file=... maximum=512 type=expandable`, `attach vdisk`, `create partition primary`, `format fs=ntfs quick`, `assign mount=C:\t\mnt`), либо Win32 `CreateVirtualDisk`/`AttachVirtualDisk` (virtdisk.dll) из тестового кода. Это требует admin. На hosted runner процессы и так идут от администратора, но это нужно проверить.
- Fixture builder: junctions (`mklink /J`), symlinks, hardlinks, ADS, read-only, sparse, compressed, `FILE_CASE_SENSITIVE_DIR` (`fsutil file setCaseSensitiveInfo`), длинные пути > 260, Unicode/кириллица, 30k плоских подкаталогов (аналог WinSxS), файлы, открытые без `FILE_SHARE_DELETE`.
- Race-тесты: параллельный поток подменяет каталог на junction между plan и execute. Executor обязан отказать (`IdentityMismatch`).
- Crash-тесты: убить процесс после N-го `plan_item` (`Environment.FailFast` через тестовый hook), затем recovery.
- UI: FlaUI проверяет, что у каждого интерактивного элемента есть `Name`, порядок табуляции, HighContrast-скриншот. Accessibility Insights for Windows — вручную перед релизом.
- Конвейер: PR = build + unit + property + VHD-fixtures (non-destructive в tmp VHD). main = + FlaUI smoke. Tag = + подпись (secrets только в environment `release` с ручным approval) + MSI + `winget validate` + install/uninstall smoke на чистой VM.

### Gaps
- Наличие Hyper-V и прав на `AttachVirtualDisk` на GitHub-hosted Windows runners не подтверждено. Нужен короткий эксперимент.
- Состояние TUnit vs xUnit v3 на октябрь 2026 не исследовано.

---

## 9. Производительность и локальная наблюдаемость (без внешней телеметрии)

### Takeaway
Бюджеты из предыдущей заметки сохраняются. Добавляется: сканер через `FileSystemEnumerable<T>` с `FileSystemEntry`, чтобы атрибуты, размер и время брать из той же записи FindNextFile, без лишних syscall. Для полного тома под admin — опционально MFT/USN (`FSCTL_ENUM_USN_DATA`), в 5–20 раз быстрее (оценка, нужен замер). Логи: Serilog в rolling-файлы `%LocalAppData%\Cleaner\logs` (7 дней, 50 MB) плюс собственный `EventSource` для ETW. Никакой сетевой телеметрии. Пользователь сам экспортирует «диагностический пакет» с редактированием путей.

### Cited Findings
- Starter-сканер делает `File.GetAttributes` + `new FileInfo().Length` на запись — проверено в коде `MetadataScanner.cs:81,93`.
- WPF 10 снизил аллокации UIA — [What's new WPF .NET 10](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100).

### Inferences
- Параллелизм: по одному worker на физический том. Внутри тома NVMe — 4–8 параллельных поддеревьев (work-stealing очередь каталогов), HDD — 1. Очередь каталогов ограниченная, переполнение уходит на диск/в SQLite, а не в «лимит» с потерей покрытия.
- Allocated size: `FileSystemEntry` не даёт allocation. Использовать `GetFileInformationByHandleEx(FileStandardInfo)` только для top-N кандидатов, либо брать данные из MFT.
- Hardlinks: дедупликация по FileId — только при включённом режиме «точный подсчёт», он дороже.
- ETW: `EventSource("Cleaner-Scan")` с событиями ScanStart/DirBatch/ScanStop, `Cleaner-Exec` с ItemStart/ItemResult. Пользователь пишет трассу через `dotnet-trace`/WPR при поддержке.
- Логи не содержат полных путей пользователя на уровне Information. Пути — на Debug, хеширование имени профиля.

### Gaps
- Нет замера `FileSystemEnumerable` vs MFT-enum на WinSxS/C:\ целевых машин.
- Актуальная версия Serilog и sink-ов не проверялась.

---

## 10. Чек-лист безопасности для инструмента удаления

### Takeaway
Главный риск — не «удалить лишнее по ошибке правила», а заставить привилегированный компонент удалить чужое (confused deputy через junction, подмену пути, IPC). Чек-лист ниже — gate перед включением любой деструктивной функции.

### Cited Findings
- Явная DACL на pipe — [Named pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).
- POSIX delete по handle — [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex).

### Inferences
1. Каждое удаление идёт по handle с проверкой FileId/VolumeSerial. Удалений по строковому пути нет.
2. `FILE_FLAG_OPEN_REPARSE_POINT` на каждом open. Reparse points никогда не обходятся (traverse), ссылка удаляется только как ссылка.
3. Broker без строковых команд. Enum действий, план с digest, повторная проверка protected-списка внутри broker.
4. Pipe: уникальное имя, DACL, `REJECT_REMOTE_CLIENTS`, `FIRST_PIPE_INSTANCE`, проверка PID/образа/подписи/сессии, challenge-секрет, лимит сообщения (например 1 MB), таймауты.
5. Broker и правила лежат в Program Files. Broker ничего не загружает из LocalAppData. `SetDefaultDllDirectories`.
6. Внешние утилиты — абсолютные пути System32, без shell.
7. Каталог и манифест обновлений подписаны. Запрет downgrade. Подпись проверяется до парсинга.
8. Модель/LLM не имеет доступа к executor. Её вывод — только `finding` с confidence. Autopilot-коридор = правила `risk=low` + whitelisted категории + лимит байт за запуск + обязательный quarantine.
9. Quarantine с DACL admin+owner. Restore не позволяет записать в protected path.
10. Логи без секретов и без полных путей по умолчанию.
11. Fuzz IPC-протокола и парсера каталога (SharpFuzz или libFuzzer-подобные).
12. Threat model (STRIDE) пересматривается при каждой новой системной операции. Внешний security review перед v1.
13. Одноразовая задача Task Scheduler: создаётся явно пользователем, запускает `Cleaner.exe --autopilot` с `LUA` (не highest), удаляется кнопкой. Безлимитных триггеров нет.

### Gaps
- Нет независимого pentest-чек-листа для «cleaner»-класса. Список составлен по аналогии с известными EoP-классами (символические ссылки в привилегированных удалителях).

---

## 11. Структура решения и фазы реализации

### Takeaway
Расширить skeleton, а не переписать. Проекты: `Cleaner.Core` (domain + policy, без Win32) → `Cleaner.Platform.Windows` (scan, handle-ops, P/Invoke через CsWin32) → `Cleaner.Catalog` → `Cleaner.Data` → `Cleaner.Execution` → `Cleaner.Broker.Contracts` + `Cleaner.Broker` → `Cleaner.Desktop` (WPF + MVVM) → `Cleaner.AI` (позже). Тесты: `Cleaner.Tests.Unit`, `Cleaner.Tests.Fs` (VHD), `Cleaner.Tests.Ui` (FlaUI). Smoke-exe из starter переносится в `Tests.Fs`.

### Cited Findings
- В starter уже есть `Cleaner.Core`, `Cleaner.Desktop`, `Cleaner.SmokeTests`, `Directory.Build.props`, `Cleaner.slnx` — проверено по файловой системе.
- Предложенная там же граница из 12 проектов — [предыдущая заметка](../../../research_notes/Архитектура%20и%20сборка%20Cleaner/architecture.md).

### Inferences
Зависимости (направление ссылок):
```
Desktop → Core, Data, Execution, Catalog, Broker.Contracts, (AI)
Execution → Core, Platform.Windows, Data
Broker → Broker.Contracts, Core(policy), Platform.Windows, Catalog(read-only встроенный)
Platform.Windows → Core
AI → Core (только DTO findings), без ссылки на Execution/Broker — проверять архитектурным тестом (NetArchTest/ArchUnitNET)
```
Фазы (одна команда 2–3 разработчика; соло — умножить ~×2; оценка, не обещание):

| Фаза | Срок | Содержимое | Выход |
|---|---|---|---|
| MVP (analysis + safe user-level cleanup) | 10–14 недель | Фиксы сканера (FileSystemEnumerable, три корзины coverage, RootNotFound, безлимитная очередь), MVVM/DI, SQLite cache.db, treemap + таблица, 15–25 правил каталога (temp, браузерные кэши, crash dumps, Recycle Bin), handle-based delete + quarantine на томе + журнал + recovery, MSI unsigned→OV signed beta | Подписанная beta, без admin |
| v1 | +12–16 недель | Broker (pipe, auth, enum-действия): DISM StartComponentCleanup, VSS shadow trim, hiberfil, DriverStore old versions, WSL vhdx compact, Windows Update cache, Delivery Optimization; подписанный каталог + обновления; usage analytics (Prefetch/SRUM/UserAssist read-only); winget; FlaUI/a11y; внешнее security review | Публичный 1.0 |
| v2 | +12–20 недель | Локальный AI (ONNX-классификатор findings, опциональный quantized LLM для объяснений), autopilot-коридор + one-shot Task Scheduler, MFT-сканер, дубликаты, Store-листинг, ARM64 | 2.0 |

### Gaps
- Оценки — экспертные. Реальный темп нужно пересчитать после первой фазы.
- CsWin32 и актуальность генерации для `FILE_DISPOSITION_INFO_EX`/`NtCreateFile` не проверены.
