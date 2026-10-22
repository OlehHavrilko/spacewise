# Движок сканирования и безопасной очистки Cleaner

Срез исследования: 23 октября 2026 года. Исходная концепция: `PROJECT_PROMPT_RU.md`, прочитана полностью. Ниже «факт» означает подтверждённое документацией поведение, «предложение» — инженерное решение для Cleaner, которое ещё нужно реализовать и проверить. Проектируемый исполнитель очистки не следует считать уже реализованным в стартовом проекте. Текущий starter предполагается только для анализа.

## 1. Как достоверно измерять пространство и индексировать файлы

### Takeaway
Обычное перечисление каталогов под правами пользователя — надёжная первая реализация. Оптимизация через USN возможна позже; число из FileInfo.Length нельзя выдавать за физически занимаемое или гарантированно освобождаемое место.

### Cited Findings
- `FindFirstFileExW` поддерживает `FindExInfoBasic` и `FIND_FIRST_EX_LARGE_FETCH`; последний увеличивает буфер и может улучшить скорость. Перечисление не сортирует результаты, а метаданные NTFS могут отставать от актуального состояния; для актуализации Microsoft указывает запрос по handle. — [FindFirstFileExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfileexw)
- `FILE_STANDARD_INFO` содержит отдельные `AllocationSize`, `EndOfFile`, `NumberOfLinks`, `DeletePending`. — [FILE_STANDARD_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info)
- `GetCompressedFileSizeW` возвращает объём хранения файла, учитывая сжатие и sparse. — [GetCompressedFileSizeW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew)
- `FILE_ID_INFO` объединяет 128-битный идентификатор файла с серийным номером тома; эти значения позволяют сравнить два открытых handle. — [FILE_ID_INFO](https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_id_info)
- Sparse-файл хранит не все логические нулевые диапазоны; его номинальная длина не равна выделенному объёму. — [Sparse files](https://learn.microsoft.com/windows/win32/fileio/sparse-files)
- Официальная сравнительная таблица NTFS/exFAT подтверждает отсутствие у exFAT hard links, ADS, sparse, NTFS-компрессии и file change log при их наличии у NTFS. Таблица содержит исторические пределы; не переносить оттуда максимальные ёмкости в требования продукта. — [File system comparison](https://learn.microsoft.com/en-in/windows/win32/fileio/filesystem-functionality-comparison)
- Текущий обзор ReFS перечисляет file IDs, USN, sparse, reparse points, named streams, hard links и block clone; ряд функций зависит от Windows Server и версии. Поэтому старое безусловное «ReFS не поддерживает hard links» больше нельзя считать универсальным. — [ReFS overview](https://learn.microsoft.com/en-us/windows-server/storage/refs/refs-overview)
- `FSCTL_ENUM_USN_DATA` — документированный запрос DeviceIoControl к handle тома для перечисления записей MFT между границами; он не является чтением сырого диска. — [FSCTL_ENUM_USN_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)
- Журнал изменений позволяет избегать полного повторного обхода, тогда как уведомления каталогов требуют работы наблюдающего приложения. — [Change journals](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journals)
- Идентификатор USN-журнала меняется при удалении/пересоздании; несовпадение даёт ошибку. V1 структуры учитывает 128-битные идентификаторы ReFS. — [READ_USN_JOURNAL_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v0)
- Переполнение `ReadDirectoryChangesW` может возвращать успех с нулём байтов и потерянным содержимым буфера; необходим повторный обход. — [ReadDirectoryChangesW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw)
- Microsoft описывает отдельные признаки `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`, `OFFLINE`, `REPARSE_POINT`, `PINNED`, `UNPINNED`. — [File attributes](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants)
- Чтение placeholders с `RECALL_ON_DATA_ACCESS` способно инициировать ненужную гидратацию; рекомендации адресованы файловым фильтрам, но демонстрируют опасность превращения metadata-open в data-read. — [Handling placeholders](https://learn.microsoft.com/windows-hardware/drivers/ifs/placeholders_guidance)
- `FILE_FLAG_OPEN_REPARSE_POINT` открывает сам reparse object, а `OPEN_NO_RECALL` предназначен для remote storage systems: это не универсальная гарантия для произвольного cloud provider. — [CreateFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea?redirectedfrom=MSDN)
- Классический MAX_PATH и extended-length paths различаются; поддержка длинных путей требует согласованных настроек приложения и API. — [Maximum path length](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation)
- `GetDiskFreeSpaceExW` различает общий свободный объём и свободный объём для вызывающего пользователя. — [GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw)

### Inferences
#### Предлагаемая матрица поддержки
| Том | Первая версия | Позднее | Что нельзя обещать |
|---|---|---|---|
| Локальный NTFS | Metadata enumeration, карта, известные категории | USN, дубликаты, audited cleanup | Равенство суммы размеров изменению free space |
| Локальный ReFS | Только анализ с явными ограничениями; runtime capability probe | Отдельная валидация Windows client Dev Drive и server ReFS | Эквивалентность NTFS и поддержку всех API по одному имени FS |
| exFAT/FAT32 removable | Анализ выбранной папки; handle errors/removal | Только явно ограниченные пользовательские операции | Надёжный USN-index, ACL-защищённый карантин, одинаковую семантику корзины |
| UNC/SMB, виртуальные/cloud roots | В первой версии отклонять либо отображать как неподдерживаемые | Самостоятельные адаптеры | Перенос гарантий локального NTFS на сеть |

Capability detection: filesystem name, volume GUID/serial, flags GetVolumeInformation, drive type, availability/lock status; пробные безопасные metadata API. Не включать привилегии ради красивой полной диаграммы. BitLocker-том в заблокированном состоянии — unavailable, не пустой.

#### Учёт объёма
Хранить: `logicalBytes`, `allocatedBytes?`, `allocationQuality` = Measured/Estimate/Unavailable, `uniqueObjectBytes?`, `reclaimableEstimateBytes?`, `coverage`. Unknown — `null`, не ноль. Logical tree view можно суммировать по именам, однако объяснять hardlink double counting. Unique physical estimate агрегировать по `(volumeSerial,fileId)`; если ID недоступен, выводить нижнюю/неполную оценку с покрытием. На ReFS block clones, dedup и storage tiering даже уникальные file IDs не гарантируют непересекающиеся физические блоки. Не пытаться «восстановить» физическую точность округлением каждой длины до cluster size.

Три разных числа в UX: найдено логических данных, оценка потенциального освобождения, наблюдаемая разница свободного места. В конце измерять free space до/после, но подписывать как изменение на томе за интервал: фоновые процессы также меняют его. Отдельно учитывать ADS, filesystem metadata, snapshots, reserved space, inaccessible regions; карта каталогов закономерно не сходится с used space тома. Не относить разницу к мусору.

#### Обход
Предложение: iterative stack/queue, no recursive call stack; bounded producer-consumer, один writer индекса, coalesced progress 5–10 раз/с, paging результатов. UI не получает миллион ObservableCollection nodes. Несколько выбранных пересекающихся roots нормализуются по реальной идентичности, а не только строковым префиксам. Не считать C:\\Foo и C:\\Foobar отношением предок-потомок. Учитывать case-sensitive directories: нельзя lower-case все пути.

Не следовать directory reparse points по умолчанию, включая junction, symlink, mount point, unknown tags; показывать запись и причину пропуска. Корень, сам являющийся link, требует явного разрешения на отдельный resolved root; безопасный MVP его отклоняет. Cloud placeholder directories не обходить вслепую. Не читать контент, не строить thumbnails и не открывать документы для модели. `NO_RECALL` добавляется лишь как дополнительная защита в протестированном адаптере, не как оправдание content reads. Кандидатов cloud/offline исключить из дубликатов/очистки; pinned не означает разрешение удаления.

Coverage snapshot: start/end time, enumerated entries/directories, skippedByExclusion, skippedReparse, cloudSkipped, accessDenied, transientFailures, volumeDisconnected, cancelled; статус Partial при недоступных областях. Cancellation проверять до dequeue, до дорогого metadata open и перед каждым batch commit. Процент whole-drive scan неизвестен до обхода: показывать этап/счётчики/время, не фиктивные 99%.

#### Индексирование v2
SQLite catalog: Volumes, ScanRuns, Objects, DirectoryEntries, Measurements, Findings, Exclusions, RulesVersions. Разделение Objects/DirectoryEntries сохраняет несколько hardlinks. Не хранить файловое содержимое. Snapshot timestamps и версии правил обязательны.

USN — ускоритель обнаружения изменившихся объектов, а не разрешение удалить. Сохранить journalId + nextUsn + volume identity. Полный baseline, затем catch-up между USN checkpoint до/после baseline, иначе изменения во время обхода теряются. При wrap/gap/new journal/смене identity/full rescan. Пакет применённых изменений и checkpoint коммитятся одной DB-транзакцией. Не создавать журнал и не менять настройки тома без отдельной продуктовой функции. Raw $MFT parser/драйвер не нужны в MVP: высокий privilege/security burden и сопровождение on-disk structures. FSCTL_ENUM_USN_DATA всё равно требует разрешённого volume handle и does not supply all high-quality metadata; имя+parent FRN не заменяют полный snapshot.

### Gaps
- Ни документация, ни исследование не заменяют испытание ReFS на конкретных поддерживаемых Windows builds. В обзорной server-странице и старых Win32 API requirement tables возможны различия; runtime probing + OS matrix — обязательный релизный gate.
- Не установлены реальные цифры скорости/памяти Cleaner: пока нет измеренного зрелого движка. Любые throughput numbers далее — целевые критерии, не результаты теста.
- Полное отсутствие гидратации на всех third-party providers нельзя доказать общей ссылкой на API: нужен запрет обхода sync roots по умолчанию или проверенный provider-specific adapter, telemetry и сетевой тест.

## 2. Как спроектировать исполнение и восстановление без опасных полномочий

### Takeaway
Скан — устаревающее наблюдение; план — ограниченное намерение; исполнитель повторно проверяет объект. Операция по pathname, даже после canonicalization, не становится безопасной от гонок с переименованием и подменой junction.

### Cited Findings
- `OpenFileById` открывает файл по идентификатору, поддерживает выбор desired access/share flags и проверяет sharing conflicts. — [OpenFileById](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid)
- Метаданные можно запросить по существующему handle. — [GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex)
- `SetFileInformationByHandle` поддерживает rename/disposition; deletion требует DELETE access на открытом handle. Это основа handle-based действия, но не готовая политика безопасности приложения. — [SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)
- Shell `FOF_ALLOWUNDO` сохраняет undo только по возможности; undo state связан с Explorer/session. `FOFX_RECYCLEONDELETE` направляет удаление в корзину. — [IFileOperation flags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)
- При межтомовом MoveFileEx security descriptor не переносится, целевой файл получает default descriptor целевого каталога. — [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)
- `CopyFileEx` описывает сохранение EA, ADS и части файловых атрибутов; EFS имеет отдельные ограничения ключей. — [CopyFileEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfileexa)
- `CancelIoEx` запрашивает отмену, не ждёт завершения; запрос способен закончиться нормально, отменой либо другой ошибкой. — [CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)
- `FlushFileBuffers` отправляет buffered information устройству; частый flush может быть дорогим. Он не создаёт общую транзакцию между журналом и файловой операцией. — [FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)
- Default security descriptor named pipe даёт read доступ Everyone/anonymous; Microsoft рекомендует явные security descriptors для контроля доступа. — [Named pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- Named pipe server может impersonate токен клиента; уровень impersonation контролируется и клиентом. — [Impersonating named pipe client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client?redirectedfrom=MSDN)

### Inferences
#### План и повторная проверка
Предложение DTO-контрактов, не готовый исходный API:
```text
ScanRequest { roots[], exclusionsRevision, depth, budget, metadataOnly=true }
ScanRun { id, timestamps, coverage, state, rulePackVersion, volumeIdentities[] }
ObjectRef { volumeGuid, volumeSerial, fileId128?, scanEntryId, observedPath }
ObservedState { size, allocation?, mtime, changeTime?, attributes, reparseTag?, linkCount? }
Finding { id, scanId, ruleId, ruleVersion, evidence[], certainty, effects[], objectRefs[] }
CleanupPlan { id, scanId, rulesHash, exclusionsRevision, items[], mode, budget, expiresAt }
PlanItem { objectRef, expectedState, operationKind, category, recoveryContract }
ExecutionResult { itemId, status, nativeError?, actualOperation, recoveryRef?, measuredBytes? }
```
Поля bytes — Int64 с проверкой overflow (JSON при JS client — decimal strings или оговорённое safe-integer ограничение). Пути — display/debug evidence, не capability. Ни LLM, ни renderer не передают `shellCommand`, glob на произвольное удаление или elevation flag. План получает machine-checked hash и короткий срок актуальности; истёкший план требует пересмотра.

Порядок будущего executor: 1) validate schema/revision/user scope; 2) открыть и удерживать root/parent handles, проверить их тип/identity, запретить переход reparse/mount границ; 3) открыть конкретный target с минимальным access, без follow final reparse, подходящим share mode; 4) сверить volume/file ID, metadata, exclusions, hardlinks и category rules; 5) записать durable intent; 6) выполнить действие тем же handle, где API позволяет; 7) записать fact/result. При изменении размера/mtime/ID/type/cloud state — SkippedChanged и новая рекомендация, а не retry deletion по старому пути. File IDs могут переиспользоваться, поэтому ID не бессрочный токен доверия.

Важная сложность: `FILE_FLAG_OPEN_REPARSE_POINT` касается последнего элемента; простой `GetFullPathName`/`GetFinalPathNameByHandle` + строковый startsWith не устраняет race intermediate path components. Нужна отдельная проверенная реализация parent/root confinement. Для privileged cleanup user-writable paths запрещены до реализации и adversarial проверки этого ограничения. `OpenFileById` помогает идентичности объекта, но сам по себе не доказывает membership выбранного каталога и не выбирает нужную hardlink directory entry. Пользователь удаляет имя/ссылку, а не абстрактный ID.

Запрет recursive delete(directory path) в executor: каталог после preview может наполниться новыми файлами. Обрабатывать только утверждённые entries, потом удалить конкретный пустой каталог после повторной проверки. Не использовать ignore-readonly/ACL takeover/SeBackupPrivilege для обхода обычных отказов. Busy file => skip/retry after close, никогда silent delete-on-reboot.

#### Машина состояний
```text
Scan: Idle -> Enumerating -> Classifying -> Ready | Partial | Cancelled | Failed
Plan: Draft -> Validated -> Presented -> Approved -> Executing
      -> Completed | Partial | Cancelled | Failed | NeedsReconciliation
Item: Planned -> Revalidated -> IntentDurable -> InFlight
      -> Recycled | Quarantined | Deleted | Serviced | Skipped | Failed | Unknown
Restore: Available -> RestoreIntent -> Restoring -> Restored | Conflict | Failed
```
Cancel останавливает новые действия; InFlight завершается и отражается фактическим статусом. Timeout ≠ cancelled successfully. Crash между действием и записью completion => Unknown/NeedsReconciliation, не blind retry. Журнал содержит operationId/idempotency key, объект до/после, original identity/path, operation, metadata preservation policy, outcome, native error, timestamp, recovery deadline. Durable intent до мутации; recovery сопоставляет source/quarantine/target identities и при двусмысленности ничего не удаляет. Это saga с отдельными компенсациями, не атомарная транзакция всего плана.

#### Корзина и карантин
Same-volume recycle/quarantine сохраняет данные на томе: освобождение места до purge обычно не происходит. UI не пишет «освобождено 10 GB», если всего лишь переместил 10 GB в локальный карантин. Recycle API удобен для обычных пользовательских файлов, однако не даёт универсального recovery contract и выполняется через Shell, а не тот же handle. Поэтому первый audited cleanup лучше ограничить app-managed quarantine, либо Shell recycle только unprivileged с явно ограниченными гарантиями; privileged executor не делегирует произвольные race-sensitive пути Shell.

Карантин: приватный каталог с ACL, random object names, manifest в защищённом журнале, no execute/no automatic preview, retention/capacity explicit. Не хранить приватные исходные данные в globally-readable ProgramData. На exFAT нет NTFS ACL: либо отказ, либо зашифрованный контейнер с отдельно определённым восстановлением. Same-volume rename проверяет target root и collision; cross-volume — copy to temp, проверка всех поддерживаемых streams/metadata/content, flush, commit target, затем повторная проверка исходника и удаление исходного имени; crash не должен оставить единственную непроверенную копию. Unsupported ADS/EFS/reparse/security fidelity => отказ от обещания полноценного restore. Потеря hardlink topology тоже документируется. Для MVP cross-volume quarantine лучше исключить.

Restore никогда не overwrites: конфликт => пользователь выбирает новое имя/место/пропуск. Проверить original parent identity и reparse confinement заново. Истёкший retention не разрешает silent purge, если пользователь не включал такую политику. Disk-full не переключает quarantine на permanent delete.

#### UAC broker
Предложение: UI/scan/LLM работают обычным пользователем. Краткоживущий signed broker под UAC только для allowlisted Windows adapters; не постоянный SYSTEM service в первой версии. IPC named pipe: explicit DACL для user/session и администратора broker, reject remote clients, first-instance protection, random channel nonce, identity/session binding, bounded messages/timeouts/replay protection. Нельзя считать PID единственной аутентификацией; проверять OS token/connection, provenance и handoff. Broker повторяет policy validation независимо от UI. Только enum-команды с типизированными параметрами и фиксированными executable locations; никакого cmd.exe/PowerShell eval/string interpolation/произвольного `DeletePath`/`RunCommand`. Новая версия rule pack не добавляет code execution/elevation capabilities. Elevated rules не загружаются из user-writable папки.

### Gaps
- Без формального описания path-confinement и реализации не утверждать, что защита TOCTOU завершена. Наличие handle API не доказывает безопасность реального кода.
- Точное адресное восстановление произвольного Shell Recycle Bin item из собственного журнала требует дополнительной реализации/прототипа. Не обещать универсальную «Отмена» на основании FOF_ALLOWUNDO.
- Power-loss durability зависит от файловой системы, storage stack и поведения устройства: тест fault injection и VM power-cut нужен отдельно от обычного process kill.

## 3. Какие категории разрешать и как доказать качество реализации

### Takeaway
Факты происхождения и поддерживаемый API определяют допустимость операции. Ни возраст, ни отсутствие записи uninstall, ни высокий confidence модели не делают пользовательский или системный файл ненужным.

### Cited Findings
- Microsoft предостерегает от ручного удаления WinSxS; `StartComponentCleanup` — штатный способ обслуживания. DISM-вариант не имеет 30-дневного grace period запланированной задачи; `/ResetBase` лишает возможности удаления уже установленных обновлений. — [WinSxS cleanup](https://learn.microsoft.com/pt-pt/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11)
- `C:\Windows\Installer` содержит уникальные данные обслуживания продуктов; Microsoft прямо пишет, что этот cache нельзя удалять. — [Windows Installer cache](https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/missing-windows-installer-cache)
- Storage Sense имеет собственные правила, работает на системном диске и не трогает Downloads/cloud по умолчанию без настройки. — [Storage Sense](https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/manage-drive-space-with-storage-sense)
- Удаление Windows.old сокращает возможность отката к предыдущей Windows и необратимо; Microsoft направляет в Storage cleanup recommendations. — [Free up space](https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/free-up-drive-space-in-windows)
- Delivery Optimization предоставляет `Delete-DeliveryOptimizationCache` для очистки своего кэша. — [DO monitoring](https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-monitor)
- Chrome различает cache, cookies/site data, history, download history; удаление download history не удаляет скачанные файлы, а удаление синхронизируемой информации может затронуть Google Account/другие устройства. — [Chrome browsing data](https://support.google.com/chrome/answer/2392709?gad=1&hl=en)
- Даже штатная Steam Clear Download Cache требует повторного входа, хотя не затрагивает установленные игры. — [Steam clear download cache](https://help.steampowered.com/en/faqs/view/6AD7-820D-8BE5-E51F)

### Inferences
#### Предложенная политика первой версии
| Категория | Analysis | Действие | Автоматизация |
|---|---|---|---|
| Собственные логи/кэш Cleaner | Да | Versioned rule, закрытые/неактивные объекты | Можно отдельно разрешить |
| Известные user temp | Да | Узкий проверенный root + min age + no in-use + exclusions, файл за файлом | После валидации конкретной категории |
| Crash dumps/logs/thumbnails | Да | Отдельные эффекты и loss of diagnostics; adapter-specific | Не общий «safe all» |
| WinSxS | Да, с оговоркой hardlinks | Штатное обслуживание/Settings; никакой raw traversal deletion | Только отдельное разрешение, без ResetBase |
| Windows.old | Да | Settings/штатный механизм с объяснением потери отката | Нет в MVP |
| Windows Installer, DriverStore, WindowsApps, System32 | Карта и объяснение | No raw deletion, модели недоступен executor | Никогда raw rules |
| pagefile, hiberfil, restore points, shadow copies | Показывать отдельно | Не cleanup files; настройки ОС, если когда-либо отдельная функция | Нет |
| Delivery Optimization | Да | Official adapter, фиксированные параметры | Отдельная opt-in policy |
| Browser disk cache | Да | Только проверенные версии/профили, закрытый браузер | Возможна позже |
| Browser cookies/history/site storage/session/passwords | Раздельно и приватно | Предпочтительно открыть штатный браузерный UI | Нет по общему cache toggle |
| Steam/game cache | Только известные зоны | Launcher adapter и versioned rules; сообщить redownload/login/shader effects | Нет для guessed leftovers |
| Saves/mods/userdata/recordings/projects/downloads/installers | Да, review | Только явное item-level решение человека | Нет по косвенным признакам |
| Suspected leftovers | Evidence-only | Review, не auto-delete | Нет |

Не изобретать штатный programmatic API там, где найден только пользовательский Settings UI. Пока API не проверен — кнопка открывает соответствующую страницу Windows и ожидает отдельного действия пользователя; такой flow не выдавать за встроенную гарантированную очистку Cleaner. Также не считать переключатель browser closed достаточным: background processes и version layout проверяются адаптером; locked database пропускается, не правится напрямую.

#### Правила как данные
Предложение схемы (не исполняемый код):
```json
{
  "schemaVersion": 1,
  "ruleId": "user-temp-stale",
  "version": "1.0.0",
  "platform": {"os": "windows", "minBuild": 26100},
  "rootResolver": "KnownFolder.UserTemp",
  "include": ["files"],
  "minAgeHours": 168,
  "excludeKinds": ["reparse", "cloud", "hardlinked", "encrypted"],
  "requiredAppState": "not-running-if-owned",
  "operation": "quarantine-same-volume",
  "privilege": "user",
  "automationEligible": false,
  "effects": ["MayNeedRegeneration"],
  "evidenceRequirements": ["ResolvedRoot", "StableIdentity", "Age", "NoExclusion"],
  "sourceUrls": [],
  "testedAppVersions": [],
  "fixtureIds": [],
  "reviewedAt": "2026-10-23"
}
```
168 часов — предложенный conservative default для обсуждения, не доказательство безопасности. Age нужен как дополнительный фильтр внутри уже проверенной категории. Корни через Known Folders/validated app discovery; не arbitrary absolute globs. У правил обязательные versions, test fixtures, app version ranges, effects, support status; пустые sources/tests в примере означают, что правило ещё нельзя выпускать. Signed pack + schema validation; custom rules не получают elevation и не отменяют global protected roots/exclusions. Runtime adapter enum известен бинарнику, JSON не создаёт новую команду. При unknown app version правило переходит в analysis-only.

#### Дубликаты v2
Предложение: сначала группировка по логическому размеру, исключение same-object hardlinks, затем cheap sample только как отсев, затем полный streaming SHA-256, затем при необходимости byte-by-byte verification на удерживаемых handles. Filename, size и sample hash не доказательство. Сначала разрешить hashing только локальных обычных файлов; skip cloud/reparse/sparse huge/encrypted/unknown streams согласно policy. Два файла с равным unnamed stream, но разными ADS/metadata имеют различную restore/application семантику — показывать «совпадает основное содержимое», не «безусловно взаимозаменяемы». Не заменять дубликаты hardlinks в MVP.

Лимиты: настройка maxBytesRead/maxFileSize/maxDuration/perVolumeConcurrency; 1 sequential reader на HDD и осторожная адаптация SSD. Incomplete/changed/unreadable hash не хранится как verified. До/после сверять identity/size/change metadata, при concurrent writers отказаться от уверенного результата или получать стабильный snapshot по отдельной политике. SHA-256 cache keyed не одним path/mtime; изменение файла инвалидирует cache. Даже криптографическое совпадение не означает разрешение удаления резервной копии. Цель «освободи 20 GB» решается ограниченным выбором из eligible findings; недостаток допустимого объёма не расширяет scope.

#### Обязательные содержательные тесты
Все опасные тесты — disposable VHDX/VM/fixture root, никогда настоящий Windows/user profile. Gates:
1. Hardlinks в разных ветках: display entry count=2, unique object=1, удаление одной ссылки не обещает освобождение всей allocation. Link за пределами scan scope не теряется при расчёте.
2. Sparse 100 GB с малой allocation; compressed files; zero-length; ADS; ReFS block clone: logical/allocated/unique labels и unknown quality верны.
3. Junction loop A->B->A, mount to another volume, symlink вне root, неизвестный reparse tag: нет обхода границ и зависания.
4. OneDrive online-only/offline placeholder: metadata scan и duplicate preview не вызывают data reads/downloads; измерять сеть и hydration state до/после. Cloud sync-root enumeration itself проверяется отдельно.
5. Access denied, sharing violation, delete-pending, removable disconnect, locked BitLocker: Partial и отдельные ошибки, не «0 bytes/clean».
6. Adversary меняет target на symlink/junction между enumeration/preview/revalidation/action, переименовывает parent, меняет drive letter и подменяет volume, создаёт файл внутри выбранного каталога после preview: sentinel за scope и новые файлы остаются неизменны.
7. Case-sensitive twins, Unicode, surrogate handling, >260 paths, trailing-space/dot Win32 edge cases, reserved names через extended namespace: display/path identity не путаются; неподдерживаемое честно отклоняется.
8. Kill process/fault after every journal transition: reconcile без повторного удаления; corrupted DB/full disk/ACL-denied journal => fail closed до действия; crash between cross-volume copy/delete не теряет единственную копию.
9. Restore collision и подмена original parent на junction: no overwrite, no out-of-scope writes. Quarantine full никогда не включает permanent fallback.
10. Broker spoof client/replay/malformed huge DTO/remote pipe/path traversal/unapproved rule/replaced rule pack: reject. Обычный scan не показывает UAC.
11. Browser session/cookies/saves/mods fixtures: cache rule не пересекает защищённые категории; новые app versions отключают mutation.
12. Duplicate candidate изменён в середине hash; одинаковые samples при различном middle; same content разных резервных копий: нет false verified/auto delete.

Performance plan: reproducible dataset 10k/100k/1m directory entries, wide/deep layouts; 0-byte vs medium/large files; cold/warm cache; HDD/SATA SSD/NVMe; обычный пользователь/admin отдельно; Defender enabled; cancellation and UI input measured while scan. Метрики: first useful result, time-to-complete, entries/s, peak working set, metadata/content I/O bytes, CPU, disk active time, UI p95 latency, cancellation acknowledgement/actual stop, DB size, inaccessible coverage, hash throughput отдельно. Preliminary SLO proposals: first known-root result <=2s warm/<=5s cold; UI p95 input latency <100ms; cancel acknowledgement <200ms; no new work after observed cancellation, но текущий kernel I/O может завершаться дольше; <=300MB scanner/index working set при 1m entries благодаря streaming/paging. Значения — цели, не measured claims; сравнивать на зафиксированной машине с baseline enumeration и Explorer/Settings лишь при сопоставимых scope/rights/metrics.

### Gaps
- Не найден единый стабильный публичный API для безопасной очистки всех категорий Windows Storage settings. Нельзя обещать такой API; каждую категорию квалифицировать отдельно.
- Браузерные/launcher disk layouts не являются универсальным неизменным контрактом. Требуются versioned fixtures и официальный API либо явный переход в штатный UI.
- Данный материал — архитектура и критерии приёмки, не аудит готовой реализации и не подтверждение выполненных destructive tests.
