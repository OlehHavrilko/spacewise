# Аналитика использования и быстрое сканирование для Cleaner (без фоновых процессов)

Контекст: дополнение к `research_notes\Глубокая очистка и оптимизация Cleaner\usage_analytics.md` (там: Amcache не доказывает запуск, LastAccess слабый сигнал, opt-in collector) и `research_notes\Архитектура и сборка Cleaner\filesystem.md` (там: FILE_STANDARD_INFO, GetCompressedFileSizeW, FILE_ID_INFO, sparse, атрибуты cloud placeholders, ReadDirectoryChangesW overflow). Здесь не повторяю их, а добавляю форматы, пути, права, сроки хранения и решения для режима «открыл – посчитал – закрыл». Дата: 2026-10-23. Объём проверки: ~15 запросов; многие форензик-детали подтверждены только вторичными источниками (DFIR-вендоры), это отмечено.

## 1. Ретроспективные сигналы запуска программ (Prefetch, BAM, UserAssist, SRUM, Amcache, Timeline, журналы)

### Takeaway
Windows уже хранит достаточно следов, чтобы при запуске Cleaner восстановить «что запускалось за последние недели», но каждый источник частичный: Prefetch (последние 8 запусков + счётчик, нужен admin, ≤1024 файлов), BAM (последний запуск, HKLM, около 7 дней по наблюдениям DFIR), UserAssist (per-user HKCU, ROT13, счётчик и focus time только для запусков через Explorer), SRUM (ESE-база, заблокирована, admin, 30–60 дней, почасовые срезы по приложениям). Amcache и ShimCache — доказательство существования файла, а не запуска. Timeline в Windows 11 убран. Итог: комбинировать с явной шкалой уверенности.

### Cited Findings

**Prefetch**
- Формат Prefetch на Win10+ сжат: файл начинается с 8-байтного заголовка `MAM\x04` (сигнатура 0x4D 0x41 0x4D 0x04) и 4-байтного «total uncompressed data size»; после распаковки — обычный SCCA. — [libyal/libscca: Windows Prefetch File (PF) format](https://github.com/libyal/libscca/blob/main/documentation/Windows%20Prefetch%20File%20(PF)%20format.asciidoc)
- В версиях формата 26 и 30 в блоке File information хранятся 8 × FILETIME «Last run time(s)» (смещение 44, 64 байта; первый — самый свежий, 0 если не задан) и `Run count` (смещение 124 в v26/v30 variant 1; в другом варианте v30 — смещение 116). Есть поле «Hash string offset» в v30 variant 1. — [libscca format doc](https://github.com/libyal/libscca/blob/main/documentation/Windows%20Prefetch%20File%20(PF)%20format.asciidoc)
- Значит, смещения Run count плавают между вариантами v30; парсер должен определять вариант, а не жёстко зашивать смещение. — [libscca format doc](https://github.com/libyal/libscca/blob/main/documentation/Windows%20Prefetch%20File%20(PF)%20format.asciidoc)
- Лимит количества prefetch-файлов: 128 в XP/Vista/7, 1024 в Windows 8+; при достижении лимита старые удаляются. Настройка: `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters\EnablePrefetcher` (0 выкл, 1 приложения, 2 загрузка, 3 оба). — [GeeksforGeeks: Prefetch Files](https://www.geeksforgeeks.org/?p=494585); [Wikipedia: Prefetcher](https://en.wikipedia.org/wiki/Prefetcher); [Binalyze KB: Prefetch](https://kb.binalyze.com/air/features/acquisition/acquisition-profiles/windows-collections/prefetch)

**BAM / DAM**
- BAM (Background Activity Moderator, драйвер `bam.sys`) и DAM появились в Windows 10 и присутствуют в Windows 11; хранят путь исполняемого файла и FILETIME последнего запуска; запуски со съёмных носителей и сетевых шар не записываются; данные восстановимы «до 7 дней», пока запись существует (наблюдение аналитика, не документация Microsoft). — [IT-Connect: Forensic Windows part 5, BAM/DAM](https://www.it-connect.tech/forensic-windows-part-5-analyzing-bam-and-dam/)
- Путь расходится между источниками: `HKLM\SYSTEM\CurrentControlSet\Services\bam\State\UserSettings\<SID>` у PrivaZer против `...\Services\bam\UserSettings\<SID>` у Velociraptor (старые билды Win10 1709 без `State`). — [PrivaZer: BAM](https://privazer.com/how-to-delete-bam-background-activity-monitor-on-windows.php); [Velociraptor: Windows.Forensics.Bam](https://docs.velociraptor.app/artifact_references/pages/windows.forensics.bam)

**UserAssist**
- Значения в `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}\Count`; имена ROT13 (в бетах Win7 был Vigenère); формат данных с Win7 — 72 байта (версия 5) вместо 16. — [winreg-kb: User Assist key](https://winreg-kb.readthedocs.io/en/latest/sources/explorer-keys/User-assist.html); [SciTePress 2017](https://scitepress.org/papers/2017/64167/64167.pdf)
- Поля: run count (байты 4–7), focus count (8–11), focus time в мс (12–15), last run FILETIME (60–67) по Cyber Triage; winreg-kb считает focus count/time неподтверждёнными, Belkasoft предупреждает о ненадёжности; на Win10+ не каждая запись означает реальный запуск. — [Cyber Triage: UserAssist Forensics](https://www.cybertriage.com/?p=27721); [winreg-kb](https://winreg-kb.readthedocs.io/en/latest/sources/explorer-keys/User-assist.html); [Belkasoft: UserAssist](https://belkasoft.com/decoding-windows-registry-artifacts-with-belkasoft-x)

**SRUM**
- `C:\Windows\System32\sru\SRUDB.dat` — ESE (EDB) база. Таблицы-GUID: `{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}` Application Resource Usage (appsruprov.dll); `{973F5D5C-1D90-4944-BE8E-24B94231A174}` Network Data Usage; `{DD6636C4-8929-4683-974E-22C046A43763}` Network Connectivity; `{FEE4E14F-02A9-4550-B5CE-5FA2DA202E37}` Energy Usage (+ вариант `...LT` долгосрочный); `{5C8CF1C7-7257-4F13-B223-970EF5939312}` и ещё три — Energy Estimator (eeprov.dll), с колонками CPU/disk timeline, описание TODO. `SruDbIdMapTable` разрешает `AppId`/`UserId` (IdType 3 = SID). — [libyal/esedb-kb: SRUM](https://github.com/libyal/esedb-kb/blob/main/documentation/System%20Resource%20Usage%20Monitor%20(SRUM).asciidoc)
- Каждая таблица имеет TimeStamp, AppId, UserId. — [OpenText: SRUM Database Parser](https://security.opentext.com/appDetails/SRUM-Database-Parser)
- Velociraptor называет `{5C8CF1C7...}` «ExecutionGUID» (т.е. App Timeline / история выполнения). — [Velociraptor: Windows.Forensics.SRUM](https://docs.velociraptor.app/artifact_references/pages/windows.forensics.srum/)
- Запись в базу примерно раз в час и при выключении; на живой системе файл заблокирован. — [Magnet Forensics: SRUM](https://www.magnetforensics.com/blog/srum-forensic-analysis-of-windows-system-resource-utilization-monitor/)
- Хранение: «до 30 дней» по одному источнику, «до 60 дней (настраивается)» по Binalyze; противоречие не разрешено. — [Binalyze KB: SRUM](https://kb.binalyze.com/air/features/acquisition/supported-evidence/windows-collections-detail/srum); [Magnet Forensics](https://www.magnetforensics.com/blog/srum-forensic-analysis-of-windows-system-resource-utilization-monitor/)

**Amcache / ShimCache**
- Amcache — hive `C:\Windows\AppCompat\Programs\Amcache.hve`; в Win10 данные в `Root\InventoryApplication` и `Root\InventoryApplicationFile` (хранит SHA1 и показывает уже удалённые файлы); это evidence of existence, в Win10+ не доказывает запуск. — [Cyber Triage: ShimCache and Amcache 2026](https://www.cybertriage.com/blog/shimcache-and-amcache-forensic-analysis-2026/); [Securelist: Amcache](https://securelist.com/amcache-forensic-artifact/117622/)

**Windows Timeline**
- Timeline UI убран в Windows 11 (остаётся в Windows 10); per-user SQLite `%LOCALAPPDATA%\ConnectedDevicesPlatform\<AccountId>\ActivitiesCache.db`; в Insider-сборках 26120.2415 / 27818.1000 страница Activity History пропала из настроек. — [ElevenForum](https://www.elevenforum.com/t/clear-activity-history-in-windows-11.7814/latest); [PrivaZer: Timeline](https://privazer.com/how-to-clean-windows-timeline-activity-history.php); [Pureinfotech](https://pureinfotech.com/clear-disable-activity-history-windows-11/)

### Inferences
Матрица источников для v1 (права / scope / глубина / что даёт):

| Источник | Права | Scope | Глубина | Сигнал | Решение |
|---|---|---|---|---|---|
| UserAssist | обычный пользователь | HKCU текущего пользователя | без срока, пока не очищено | счётчик + last run запусков через Explorer/Start | v1, базовый, без UAC |
| BAM | чтение HKLM\SYSTEM\...\bam: обычно admin (проверить ACL) | per-SID | ~7 дней | last run любых exe (кроме USB/сети) | v1 при elevated-скане |
| Prefetch `C:\Windows\Prefetch\*.pf` | admin (каталог закрыт для обычного пользователя — проверить) | машина | ≤1024 exe, 8 запусков на exe | лучший ретро-сигнал «как часто» | v1 elevated, свой парсер MAM (Xpress Huffman через `RtlDecompressBufferEx` из ntdll) |
| SRUM | admin + обход блокировки | машина, по SID | 30–60 дней почасово | CPU/IO/foreground cycles по app, сеть | v2: копия через VSS/`esentutl /y /vss` или ESE API на копии |
| Amcache / ShimCache | admin | машина | долго | «файл был», хеш | только inventory, не usage |
| Timeline | user | per-user | — | на Win11 не опираться | не использовать |

- Распаковку MAM в .NET делать P/Invoke к `RtlDecompressBufferEx` с `COMPRESSION_FORMAT_XPRESS_HUFF` (так делают известные парсеры; источник не проверен в этой сессии).
- SRUM открывать только на копии: `esentutl.exe /y C:\Windows\System32\sru\SRUDB.dat /vss /d <tmp>` (требует admin), затем `esentutl /r` при dirty shutdown и чтение через ManagedEsent (`Microsoft.Isam.Esent.Interop`). Копию сразу удалять после разбора.
- Модель уверенности: `Prefetch/BAM/SRUM = Executed`, `UserAssist = LaunchedViaShell`, `Amcache/Uninstall = Present`. Агрегировать в «last seen running» = max по источникам с указанием источника.

### Gaps
- Не найдено официальных Microsoft-документов ни по одному артефакту (BAM, Prefetch-формат, UserAssist, SRUM-схема) — всё вторичные DFIR-источники; формат может меняться без уведомления.
- Не подтверждено, отключается ли Prefetch на SSD в Win10/11 (найдено только про ReadyBoost); нужен эмпирический тест (`EnablePrefetcher` и наличие свежих .pf на SSD-машине).
- Версия формата Prefetch на Win11 24H2/25H2 (30 или 31) не подтверждена.
- ACL на `bam\State\UserSettings` и `C:\Windows\Prefetch` для обычного пользователя не проверены.
- Event Log: MsiInstaller 11707/1033 (Application) и Kernel-Process / Security 4688 не проверены в этой сессии; 4688 требует включённой политики аудита (известно, но без источника здесь).
- Jump Lists / RecentDocs / LNK не исследованы в этой сессии.
- Точный срок хранения SRUM и настройка (ключ `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SRUM\Parameters`?) не подтверждены.

## 2. Игры, установленные программы и их размеры

### Takeaway
Steam надёжно даёт размер (`SizeOnDisk` в appmanifest), но LastPlayed/Playtime лежат в per-user `localconfig.vdf` — Valve формально не поддерживает чтение файла сторонними программами. Uninstall `EstimatedSize` (KB) часто устаревший или отсутствует, поэтому размер считать самим по `InstallLocation`.

### Cited Findings
- `steamapps\appmanifest_<appid>.acf` — текстовый KeyValues на каждую установленную игру; поля `SizeOnDisk` (байты), `LastUpdated`, `StateFlags`, `buildid`, `LastOwner`, `installdir`. — [Steam Community thread](https://steamcommunity.com/app/377160/discussions/0/1736589519991038228); [ACF Manager](https://gh.nn.ci/davispuh/ACF-Manager)
- `LastPlayed`/`Playtime` не найдены в appmanifest; клиентский API Steam описывает playtime в минутах и last played в Unix time. — [Steambrew docs: Playtime](https://docs.steambrew.app/plugins/ts/client/src/interfaces/Playtime)
- `localconfig.vdf` лежит в `<steam>\userdata\<accountid>\config\`; Valve заявляла, что файл не предназначен для использования другими программами и выгрузки третьим сторонам. — [SteamCategories](https://github.com/nfearnley/SteamCategories); [PC Gamer](https://www.pcgamer.com/uk/valve-doesnt-sound-too-happy-about-the-epic-store-copying-steam-data)
- `EstimatedSize` — DWORD в KB; без него Windows «гадает» хрупкими эвристиками; с Win7 размер не пересчитывается автоматически; сторонние деинсталляторы считают сами, т.к. значение «часто неверное». — [NSIS wiki](https://nsis.sourceforge.io/Add_uninstall_information_to_Add/Remove_Programs); [Revenera](https://community.revenera.com/s/article/size-not-displayed-in-add-remove-programs-on-windows-7); [CrystalIDEA forum](https://forums.crystalidea.com/viewtopic.php?p=6366)

### Inferences
- Inventory: `HKLM\...\Uninstall`, `HKLM\SOFTWARE\WOW6432Node\...\Uninstall`, `HKCU\...\Uninstall` + MSIX через `Windows.Management.Deployment.PackageManager.FindPackagesForUser("")` (+ `Package.InstalledLocation`). Для каждой записи: размер = собственный подсчёт по `InstallLocation` из скана (бесплатно, если дерево уже построено), `EstimatedSize` только как fallback с меткой «по данным установщика».
- Привязка exe→продукт: самый длинный префикс пути `InstallLocation`; для Prefetch-путей (`\VOLUME{serial}\...`) — сопоставить серийник тома через `GetVolumeInformation`.
- Steam: размер из acf, last played из `localconfig.vdf` только локально, read-only, с пометкой в UI. Epic (`C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item`), Battle.net, Xbox (`.GamingRoot`, MSIX) — не проверены, использовать Prefetch/BAM по exe в каталоге игры как универсальный last-played.

### Gaps
- Точные ключи `LastPlayed`/`Playtime` в `localconfig.vdf` не подтверждены источником (ожидается `UserLocalConfigStore\Software\Valve\Steam\apps\<appid>\LastPlayed` — проверить на живом файле).
- Форматы Epic/Battle.net/Xbox не исследованы.

## 3. LastAccessTime

### Takeaway
LastAccess нельзя считать «последним использованием»: обновление отложено до часа, может быть отключено политикой, на Win10 1803+ режим по умолчанию «system managed». Показывать только как слабый сигнал после проверки `fsutil behavior query disablelastaccess`.

### Cited Findings
- `disablelastaccess` пишет `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisableLastAccessUpdate`; NTFS может откладывать запись на диск до одного часа, но запросы возвращают точное значение из памяти; изменение требует перезагрузки. — [fsutil behavior](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior)

### Inferences
- Значения 0x80000000–0x80000003 (system managed / user managed) и порог «включено на томах до 128 GB» в текущей версии страницы fsutil отсутствуют; читать реестр и трактовать старший бит как «system managed», младший бит — «отключено».

### Gaps
- Официальное описание режима system managed (порог размера системного тома) в этой сессии не найдено на Microsoft Learn.

## 4. Быстрое сканирование: MFT, FileSystemEnumerable, размеры, placeholders

### Takeaway
Самый быстрый путь — `FSCTL_ENUM_USN_DATA` по handle тома (как Everything/WizTree), но он требует admin для системного тома и не возвращает размеры — их нужно доставать отдельно (чтение $MFT или по файлам). Без admin — `FileSystemEnumerable<T>` (.NET вызывает `NtQueryDirectoryFile` и отдаёт атрибуты/длину из буфера без лишних syscalls). Для cloud placeholders смотреть атрибуты из перечисления, а не открывать файлы.

### Cited Findings
- `FSCTL_ENUM_USN_DATA` перечисляет MFT-записи между границами; первый вызов с `MFT_ENUM_DATA.StartFileReferenceNumber = 0`, следующий старт — первые 8 байт выходного буфера; handle тома `\\.\X:`; только NTFS. — [FSCTL_ENUM_USN_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)
- Выходной буфер `FSCTL_READ_USN_JOURNAL` и `FSCTL_ENUM_USN_DATA` одинаковой структуры (USN_RECORD переменной длины; имя читать по длине, не по NUL). — [Walking a Buffer of Change Journal Records](https://learn.microsoft.com/en-us/windows/win32/fileio/walking-a-buffer-of-change-journal-records)
- Everything сначала пытается прочитать MFT напрямую как admin/standard user; стандартный пользователь получает низкоуровневый доступ только к несистемным NTFS-томам; иначе нужна служба Everything Service. — [voidtools forum](https://www.voidtools.com/forum/viewtopic.php?p=27615); [Everything Service](https://www.voidtools.com/support/everything/everything_service)
- `FileSystemEntry` — низкоуровневое представление find-результата с `Attributes`, `Length`, временными метками; `FileSystemEnumerable<TResult>` принимает predicate и transform. — [FileSystemEntry](https://learn.microsoft.com/en-ie/dotnet/api/system.io.enumeration.filesystementry.length?view=net-8.0); [System.IO.Enumeration](https://learn.microsoft.com/dotnet/api/system.io.enumeration)
- Атрибут 0x00400000 (`RECALL_ON_DATA_ACCESS`) помечает нелокальное содержимое; чтение данных такого placeholder вызывает гидратацию; в одном отчёте `FindFirstFile`/`GetFileAttributes` вернули 0x00500020 вместо реальных 0x00501620 — Windows может «маскировать» placeholders для некоторых процессов (disguised placeholders). — [Stack Overflow mirror 54560454](https://windows-hexerror.linestarve.com/q/so54560454-getting-full-file-attributes-for-files-managed-by-microsoft-onedrive)
- В CfAPI получение метаданных placeholder и получение данных — разные callbacks, т.е. перечисление и гидратация разделены. — [Cloud files enumerations (mirror)](https://star-sa1.southeastasia.cloudapp.azure.com/en-us/windows/win32/cfapi/cloud-files-enumerations)

### Inferences
- Архитектура сканера (две ветки):
  1. **Elevated / несистемный NTFS**: `FSCTL_ENUM_USN_DATA` (MFT_ENUM_DATA_V1, буфер 1–4 MB) строит дерево FRN→ParentFRN+имя за секунды; размеры — либо разбор `$MFT` напрямую (`$DATA` атрибут: real/allocated size; так делает WizTree), либо параллельный `GetFileInformationByHandleEx(FileStandardInfo)` по FRN через `OpenFileById` (медленнее). Хардлинки: одна MFT-запись = одна запись в учёте, считать один раз.
  2. **Без прав**: `FileSystemEnumerable<T>` с `EnumerationOptions { RecurseSubdirectories=false, AttributesToSkip=0, IgnoreInaccessible=true, BufferSize=64K }`, своя рекурсия очередью; `entry.Length` и `entry.Attributes` из буфера; не трогать `FileInfo`/`File.GetAttributes` (текущий `MetadataScanner.cs` делает два лишних syscalls на запись).
- Allocation size из MFT/`FILE_STANDARD_INFO` (см. filesystem.md) — для сжатых/sparse; для обычных файлов округлять вверх до кластера как приближение.
- Параллелизм: NVMe — 4–8 параллельных воркеров по поддеревьям; HDD — 1 поток (seek-bound). Тип определять `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceSeekPenaltyProperty`). Числа не измерены — нужен бенчмарк.
- Placeholders: не открывать файлы с `RECALL_ON_DATA_ACCESS|RECALL_ON_OPEN|OFFLINE`; учитывать их «логический» размер отдельно от «на диске». Для честных атрибутов вызывать `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)` (API ntdll, Win10 1803+; источник не проверен в этой сессии).

### Gaps
- Внутренности .NET (`NtQueryDirectoryFile` + `FILE_FULL_DIR_INFORMATION`, дефолтный буфер) не подтверждены фетчем исходника dotnet/runtime в этой сессии.
- Публичной технической записки WizTree о чтении $MFT не найдено; утверждение «как WizTree» — вывод.
- Нет бенчмарков SSD vs HDD для параллельного обхода.

## 5. USN-журнал для инкрементальных пересканов

### Takeaway
Сохранять `(VolumeSerial, UsnJournalID, NextUsn)` после каждого скана; при следующем открытии читать журнал с сохранённого USN. Если JournalID другой или сохранённый USN < `LowestValidUsn` — журнал пересоздан/перезаписан, делать полный скан.

### Cited Findings
- NTFS ведёт USN change journal, чтобы приложения не держали постоянный процесс с `ReadDirectoryChangesW`. — [Change Journals](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journals)
- Журнал получает новый идентификатор при остановке/перезапуске или удалении/пересоздании; чтение с ненулевого USN раньше первой записи журнала даёт ошибку. — [READ_USN_JOURNAL_DATA_V1](https://learn.microsoft.com/previous-versions/windows/desktop/legacy/hh802706(v=vs.85))
- Создание/изменение/удаление журнала требует прав администратора; `CREATE_USN_JOURNAL_DATA` задаёт `MaximumSize` и `AllocationDelta` (удаление/выделение с начала/конца журнала блоками этого размера); удалить журнал рекомендуется при приближении к `MaxUsn`; `FSCTL_DELETE_USN_JOURNAL` блокирует доступ на время работы. — [Creating, Modifying, and Deleting a Change Journal](https://learn.microsoft.com/en-us/windows/win32/fileio/creating-modifying-and-deleting-a-change-journal)
- Журнал ведёт драйвер NTFS, а не Everything; система обычно пересоздаёт его при следующей загрузке. — [voidtools forum](https://www.voidtools.com/forum/viewtopic.php?p=53547)

### Inferences
- Алгоритм при запуске: `FSCTL_QUERY_USN_JOURNAL` → сравнить JournalID; если `saved.NextUsn >= LowestValidUsn` → `FSCTL_READ_USN_JOURNAL` (READ_USN_JOURNAL_DATA_V1, `ReasonMask=All`, `ReturnOnlyOnClose=1`) → пометить затронутые каталоги грязными → перескан только их. Иначе полный скан.
- Не менять размер журнала по умолчанию (это системная настройка, нарушает принцип «ничего не менять»); если журнал часто переполняется между запусками — просто полный скан.
- Источник USN-записей одновременно даёт «что менялось с прошлого раза» для аналитики роста.

### Gaps
- Нужен ли admin для `FSCTL_QUERY/READ_USN_JOURNAL` на системном томе — точная формулировка Microsoft не найдена; по Everything — на системный том standard user не получает доступ.
- Размер журнала по умолчанию на Win11 (ориентировочно 32 MB) не подтверждён источником.

## 6. История роста в SQLite и прогноз заполнения

### Takeaway
Хранить не файлы, а агрегаты: snapshot по тому + top-N папок с размерами; прогноз даты заполнения — линейная регрессия по свободному месту за последние N снапшотов.

### Cited Findings
- Источников по конкретной схеме не искал; опираться на выводы ниже. `GetDiskFreeSpaceExW` различает общий свободный и доступный пользователю объём (см. filesystem.md). — [GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw)

### Inferences
- Схема:
  - `snapshot(id, volume_serial, taken_utc, total_bytes, free_bytes, usn_journal_id, next_usn, scan_mode)`
  - `folder_agg(snapshot_id, path_id, logical_bytes, allocated_bytes, file_count, depth)` — только папки ≥ 0.1% тома или top-500, глубина ≤ 4.
  - `path(id, parent_id, name)` — дедуп путей; `app_usage(app_id, source, last_seen_utc, run_count, snapshot_id)`.
- Прогноз: OLS по `(taken_utc, free_bytes)` за 30–90 дней, отбрасывать скачки от очисток (дельта > X% за день); выдавать дату с интервалом и «недостаточно данных», если < 3 точек.

### Gaps
- Нет измерений объёма базы и точности прогноза.

## 7. Без фоновой нагрузки: Task Scheduler, EcoQoS, приоритеты I/O

### Takeaway
Сканировать только при открытом окне; потоки сканера помечать EcoQoS + `THREAD_MODE_BACKGROUND_BEGIN` (низкие I/O и memory priority). Опциональная одноразовая задача Планировщика (триггер по событию нехватки места или раз в неделю с условием idle/AC) запускает `Cleaner.exe --snapshot`, который пишет снапшот и завершается.

### Cited Findings
- EcoQoS: явная метка процесса через `SetProcessInformation(ProcessPowerThrottling, PROCESS_POWER_THROTTLING_EXECUTION_SPEED)` или потока через `SetThreadInformation(ThreadPowerThrottling, THREAD_POWER_THROTTLING_EXECUTION_SPEED)`; Eco всегда выбирает самую экономичную частоту и E-ядра; уровень Eco — Windows 11; Utility — Win11 22H2; с 2025.05 Windows понижает foreground-приложение до Medium при бездействии пользователя на батарее (`DisableUserPresenceQos`). — [Quality of Service](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)
- `THREAD_MODE_BACKGROUND_BEGIN` снижает приоритеты планирования ресурсов потока для фоновой работы; только для текущего потока; не использовать в UI-потоках; избегать общих critical sections/heap с другими потоками из-за инверсии приоритетов. — [SetThreadPriority](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority)
- В тестах Chromium после background mode memory priority становится VERY_LOW, а `GetThreadPriority` на Win10+ продолжает показывать NORMAL. — [Chromium platform_thread_win_unittest.cc](https://chromium.googlesource.com/chromium/src/+/9e001703a2/base/threading/platform_thread_win_unittest.cc)
- Событие Srv 2013 пишется при падении свободного места ниже порога (по умолчанию 10%, `HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters\DiskSpaceThreshold`); Task Scheduler поддерживает триггер «On an event». — [EventTracker: Event 2013](https://kb.eventtracker.com/evtpass/evtPages/EventId_2013_Srv_46541.asp); [Microsoft blog archive: Trigger happy events](https://learn.microsoft.com/ja-jp/archive/blogs/doxley/trigger-happy-events)

### Inferences
- Задача Планировщика: только по opt-in, `LogonType=InteractiveToken`, `RunLevel=LeastPrivilege`, условия `RunOnlyIfIdle`, `DisallowStartIfOnBatteries`, `StopIfGoingOnBatteries`, `ExecutionTimeLimit=PT15M`, триггер Weekly + (опционально) Event-триггер. Процесс сам себя помечает EcoQoS и выходит; кнопка «удалить задачу» в настройках.
- При открытом приложении: UI-поток нормальный, воркеры — EcoQoS + background mode; на батарее — ограничить параллелизм до 1–2.
- Важно: Srv 2013 зависит от службы LanmanServer и порог машинно-общий — триггер ненадёжен на домашних ПК; weekly-триггер основной.

### Gaps
- Пишет ли Srv 2013 на Win11 24H2/25H2 без общих папок — не проверено.
- Точная документация, что background mode = Very Low I/O priority, не найдена в Microsoft Learn этой сессии (только косвенно).

## 8. Приватность

### Takeaway
Prefetch/BAM/SRUM/UserAssist — форензик-уровень: они раскрывают, что, когда и как долго запускал каждый пользователь. Читать локально, хранить агрегаты, ничего не отправлять без явного действия, давать удаление истории.

### Cited Findings
- Эти артефакты используются DFIR-инструментами именно как доказательства исполнения программ (Velociraptor, Binalyze, Cyber Triage, Magnet). — [Velociraptor SRUM](https://docs.velociraptor.app/artifact_references/pages/windows.forensics.srum/); [Magnet Forensics](https://www.magnetforensics.com/blog/srum-forensic-analysis-of-windows-system-resource-utilization-monitor/); [Cyber Triage](https://www.cybertriage.com/blog/windows-registry-forensics-cheat-sheet-2026/)
- Valve прямо против передачи `localconfig.vdf` третьим сторонам. — [PC Gamer](https://www.pcgamer.com/uk/valve-doesnt-sound-too-happy-about-the-epic-store-copying-steam-data)

### Inferences
- Хранить в БД только app-level агрегаты (exe/product, last_seen, run_count, source); не копировать сырые .pf, SRUDB, путь документов из Jump Lists.
- БД в `%LOCALAPPDATA%\Cleaner` с ACL текущего пользователя, шифрование DPAPI (`ProtectedData`, CurrentUser) или SQLCipher.
- Elevated-режим видит чужие SID в BAM/SRUM: по умолчанию фильтровать только текущий SID; другие пользователи — отдельная галочка.
- В локальный AI подавать только агрегаты, с превью «что уйдёт в модель»; кнопка «стереть историю Cleaner».

### Gaps
- Юридические требования (GDPR для корпоративных ПК) не исследовались.
