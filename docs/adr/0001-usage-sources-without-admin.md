# ADR 0001. Источники аналитики использования без прав администратора

- Статус: принято (по результатам spike)
- Дата: 2026-10-23
- Закрывает: Q2 и Q13 из §16 ТЗ (`docs/spec/ТЗ Spacewise.md`)
- Код spike: `spikes/UsageAccess/` (консольный проект, только чтение, в `Spacewise.slnx` не входит)

## Контекст

§5 ТЗ строит экран «Использование» на данных, которые Windows уже пишет: UserAssist, BAM, Prefetch, SRUM, USN-журнал. В ТЗ права доступа к BAM, Prefetch и USN системного тома помечены **[не верифицировано]**. От ответа зависит, что входит в MVP (обычный пользователь, без UAC), а что уходит в elevated-брокер v1. Q13 касается техники: можно ли из user mode распаковать Prefetch (`MAM\x04`, Xpress Huffman) через `RtlDecompressBufferEx` и управлять режимом placeholder'ов через `RtlSetProcessPlaceholderCompatibilityMode`.

Проверку делали на одной машине:

- ОС: `Microsoft Windows [Version 10.0.26300.9711]`, `[Environment]::OSVersion` = `Microsoft Windows NT 10.0.26300.0`, DisplayVersion `26H2`, ProductName в реестре `Windows 10 Pro` (так пишет и Windows 11).
- Пользователь: локальная учётная запись из группы администраторов, процесс **не повышен** (filtered token): `IsAdministrator(role): False; TokenElevated: False`; `whoami /groups`: `BUILTIN\Администраторы S-1-5-32-544 — Group used for deny only`, уровень целостности `S-1-16-8192` (Medium). Это типичный случай «запуск без UAC». Пользователь вне группы администраторов не проверялся, но у него прав не больше.
- Тома: `C:` (системный) и `D:`, оба NTFS.

## Что проверено

| Источник | Операция | Результат на этой машине | Точная ошибка |
|---|---|---|---|
| Prefetch | `CreateFileW("C:\Windows\Prefetch", FILE_LIST_DIRECTORY, …, FILE_FLAG_BACKUP_SEMANTICS)` | Отказ | `5 ERROR_ACCESS_DENIED` |
| Prefetch | `FindFirstFileW("C:\Windows\Prefetch\*.pf")` | Отказ, список файлов недоступен | `5 ERROR_ACCESS_DENIED` |
| Prefetch | Чтение `*.pf` | Не проверено: без списка нет имён файлов | — |
| BAM | `RegOpenKeyExW(HKLM\SYSTEM\CurrentControlSet\Services\bam\State\UserSettings, KEY_READ)` | Отказ | `5 ERROR_ACCESS_DENIED` |
| BAM | Прямое открытие `…\State\UserSettings\<свой SID>` (`KEY_QUERY_VALUE`) | Отказ, ACL дочернего ключа тоже не пускает | `5 ERROR_ACCESS_DENIED` |
| BAM | Чужие SID | Не проверить: нельзя перечислить подключи | (следствие отказа на родителе) |
| BAM | Старый путь `…\bam\UserSettings` (без `State`) | Ключа нет на этой сборке | `2 ERROR_FILE_NOT_FOUND` |
| USN, `\\.\C:` | `CreateFileW` с `GENERIC_READ` | Отказ | `5 ERROR_ACCESS_DENIED` |
| USN, `\\.\C:` | `CreateFileW` с `FILE_READ_ATTRIBUTES\|SYNCHRONIZE` или 0 | Handle открывается | — |
| USN, `\\.\C:` | `FSCTL_QUERY_USN_JOURNAL` / `FSCTL_READ_USN_JOURNAL` / `FSCTL_ENUM_USN_DATA` по такому handle | Отказ | `1 ERROR_INVALID_FUNCTION` (все три) |
| USN, handle корня `C:\` | `FSCTL_QUERY_USN_JOURNAL` | **Работает**: `UsnJournalID`, `FirstUsn`, `NextUsn`, `MaximumSize=32 MiB` | — |
| USN, handle корня `C:\` | `FSCTL_READ_USN_JOURNAL`, `FSCTL_ENUM_USN_DATA` | Отказ | `5 ERROR_ACCESS_DENIED` |
| USN, `D:` (несистемный NTFS) | Те же операции | Результат тот же, что на `C:` | те же коды |
| SRUM | `CreateFileW("C:\Windows\System32\sru\SRUDB.dat", FILE_READ_ATTRIBUTES)` | Отказ | `5 ERROR_ACCESS_DENIED` |
| SRUM | То же с `GENERIC_READ` | Отказ | `5 ERROR_ACCESS_DENIED` |
| SRUM | `FileInfo.Length` | Отказ | `UnauthorizedAccessException 0x80070005` |
| UserAssist | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}\Count` | **Работает**: 9 GUID, 32 значения, 19 записей по 72 байта с FILETIME последнего запуска | — |
| Q13: `RtlDecompressBufferEx` + `COMPRESSION_FORMAT_XPRESS_HUFF` | Реальный `.pf` | Не проверено: Prefetch недоступен | — |
| Q13: `RtlDecompressBufferEx` + `COMPRESSION_FORMAT_XPRESS_HUFF` | Синтетический контейнер `MAM\x04` (`RtlCompressBuffer` от поддельного заголовка SCCA v31) через тот же код разбора | **Работает** из user mode: 531 B → 16384 B, сигнатура `SCCA`, версия 31 | — |
| Q13: `RtlQueryProcessPlaceholderCompatibilityMode` | Запрос для процесса и потока | Работает, `0 PHCM_APPLICATION_DEFAULT` | — |
| Q13: `RtlSetProcessPlaceholderCompatibilityMode(2)` | Только для процесса probe | Работает: вернул прежний `0`, после установки `2 PHCM_EXPOSE_PLACEHOLDERS`; возврат к 0 вернул `2` | — |

Выдержка из реального вывода (`dotnet build spikes/UsageAccess -c Release`: 0 предупреждений, 0 ошибок; затем `UsageAccess.exe`):

```text
OS: Microsoft Windows NT 10.0.26300.0; build UBR=26300.9711 (26H2, Windows 10 Pro)
User SID: S-1-5-21-…-1001; IsAdministrator(role): False; TokenElevated: False

== Q2a Prefetch ==
  Directory.Exists: True
  Open dir (FILE_LIST_DIRECTORY): FAIL 5 ERROR_ACCESS_DENIED
  FindFirstFileW(*.pf): FAIL 5 ERROR_ACCESS_DENIED
  Synthetic MAM round-trip (no real .pf readable): OK, MAM\x04 531 B -> 16384 B, SCCA v31

== Q2b BAM ==
  HKLM\SYSTEM\CurrentControlSet\Services\bam\State\UserSettings (KEY_READ): FAIL 5 ERROR_ACCESS_DENIED
    own SID direct (KEY_QUERY_VALUE): FAIL 5 ERROR_ACCESS_DENIED
  HKLM\SYSTEM\CurrentControlSet\Services\bam\UserSettings (KEY_READ): FAIL 2 ERROR_FILE_NOT_FOUND

== Q2c USN ==
  Fixed NTFS volumes: C:, D: (system: C:)
  \\.\C: open GENERIC_READ: FAIL 5 ERROR_ACCESS_DENIED
  \\.\C: open FILE_READ_ATTRIBUTES|SYNCHRONIZE: OK
    FSCTL_QUERY_USN_JOURNAL: FAIL 1 ERROR_INVALID_FUNCTION
    FSCTL_READ_USN_JOURNAL: FAIL 1 ERROR_INVALID_FUNCTION
    FSCTL_ENUM_USN_DATA: FAIL 1 ERROR_INVALID_FUNCTION
  C:\ root dir handle (FILE_READ_ATTRIBUTES): OK
    FSCTL_QUERY_USN_JOURNAL: OK (FirstUsn=4194304000, NextUsn=4232659040, MaximumSize=32 MiB)
    FSCTL_READ_USN_JOURNAL: FAIL 5 ERROR_ACCESS_DENIED
    FSCTL_ENUM_USN_DATA: FAIL 5 ERROR_ACCESS_DENIED

== Q2d SRUM ==
  CreateFileW(FILE_READ_ATTRIBUTES, share RWD): FAIL 5 ERROR_ACCESS_DENIED
  CreateFileW(GENERIC_READ, share RWD): FAIL 5 ERROR_ACCESS_DENIED

== Q2e UserAssist ==
  HKCU\...\UserAssist: OK, 9 GUID keys, 32 values, 19 72-byte entries with last-run FILETIME
  newest last-run FILETIME: 2026-10-23 01:17:16Z

== Q13 Placeholder compatibility mode ==
  Query process: 0 PHCM_APPLICATION_DEFAULT; query thread: 0 PHCM_APPLICATION_DEFAULT
  Set process EXPOSE(2): returned 0 PHCM_APPLICATION_DEFAULT; query after: 2 PHCM_EXPOSE_PLACEHOLDERS
  Restore process to 0: returned 2 PHCM_EXPOSE_PLACEHOLDERS; query: 0
```

Блок `\\.\C:` с доступом 0 и блок `D:` в выдержке опущены: коды там те же.

## Решение для MVP

Без прав администратора доступны:

1. **UserAssist** (HKCU) — единственный источник «последнего запуска» в MVP. Подтверждает FR-USE-002 и FR-USE-005 (MVP).
2. **Метаданные USN-журнала** через handle корня тома (`CreateFileW("C:\", FILE_READ_ATTRIBUTES, FILE_FLAG_BACKUP_SEMANTICS)` + `FSCTL_QUERY_USN_JOURNAL`). Записи журнала так не прочитать. Зато `UsnJournalID` и `NextUsn` можно сохранять в снимок тома как контрольную точку. Смена `UsnJournalID` или `FirstUsn` больше сохранённой точки значит, что журнал пересоздан или перезаписан и инкремент невозможен. Если `NextUsn` не изменился, том не менялся и повторный полный скан можно пропустить.
3. **Распаковка MAM** (`RtlDecompressBufferEx`, `COMPRESSION_FORMAT_XPRESS_HUFF`) и **`RtlSetProcessPlaceholderCompatibilityMode`** вызываются из user mode без ошибок. Парсер Prefetch можно писать и тестировать без прав, на фикстурах. Режим placeholder'ов процесса сканера ставится в user mode.

В v1, через elevated-брокер (UAC по явному действию «Проверить с правами администратора»), уходят:

- **Prefetch**: нужно перечислить `C:\Windows\Prefetch` и прочитать файлы.
- **BAM**: читается `HKLM\…\bam\State\UserSettings`, включая собственный SID.
- **Чтение USN** (`FSCTL_READ_USN_JOURNAL`, `FSCTL_ENUM_USN_DATA`) — на **всех** томах, включая несистемный NTFS `D:`. Предположение FR-USE-004 «USN-инкремент для несистемных NTFS-томов без прав» на этой машине не подтвердилось.

В v2 остаётся **SRUM**: без прав файл не открывается даже на `FILE_READ_ATTRIBUTES`. Копию через `esentutl /vss` в spike не запускали; по документации VSS она тоже требует прав администратора **[не верифицировано]**.

Без прав Prefetch, BAM и SRUM дают статус `UnknownHistory` из §5.2 с кнопкой «Проверить с правами администратора», а не ошибку.

## Последствия

- В ТЗ снять **[не верифицировано]** с прав для BAM и Prefetch (§5.1): нужны права администратора. Для USN записать, что без прав доступен только `FSCTL_QUERY_USN_JOURNAL` через handle каталога.
- Поправить FR-USE-004: USN-инкремент без прав невозможен ни на одном томе. В MVP «что изменилось» считается сравнением снимков полного скана. USN-контрольная точка используется только для того, чтобы понять, менялся ли том и цел ли журнал.
- Брокер v1 должен уметь три операции: перечислить и прочитать `*.pf`, прочитать ключи BAM, читать USN по handle `\\.\X:` с `GENERIC_READ`. Наружу брокер отдаёт уже разобранные агрегаты (exe, время, счётчик), а не сырые файлы.
- Открытие `\\.\C:` без `GENERIC_READ` «успешно», но FSCTL на таком handle даёт `ERROR_INVALID_FUNCTION`, а не `ERROR_ACCESS_DENIED`. Код должен считать любую ошибку FSCTL признаком «нет доступа» и не принимать удачный `CreateFileW` за доступ к журналу.
- Парсер Prefetch (FR-USE-003) пишется в MVP-ветке как библиотека с тестами на синтетических фикстурах `MAM\x04`. Включается он в v1 вместе с брокером.

## Проверка с правами администратора

Тот же probe (`spikes/UsageAccess`) запущен из процесса с повышенными правами на той же машине (26300, 23.10.2026). В первом прогоне нашлись две ошибки самой пробы: `WIN32_FIND_DATAW` был объявлен с `long` для FILETIME (выравнивание 8 сдвигало `cFileName`, отсюда `ERROR_FILE_NOT_FOUND` для всех `.pf`), а `StartUsn` для `FSCTL_READ_USN_JOURNAL` указывал не на границу записи (`ERROR_INVALID_PARAMETER`). После исправления (`Pack = 4`, `StartUsn = FirstUsn`):

| Источник | Результат с правами |
|---|---|
| Prefetch | Перечисление 309 `.pf`; `CreateFileW` с `FILE_READ_ATTRIBUTES` и `GENERIC_READ` — 309/309. Распаковка `RtlDecompressBufferEx` (XPRESS_HUFF): все 309 — `MAM\x04`, после распаковки **SCCA версии 31**. Пример: 86 189 Б → 382 996 Б. Вариант `MAM\x84` не встретился. |
| BAM | `bam\State\UserSettings` читается с `KEY_READ`, 7 SID-ов, включая чужие (S-1-5-18 и другие). Собственный SID — 112 значений. Ветки `bam\UserSettings` на этой сборке нет. |
| USN | `\\.\C:` и `\\.\D:` с `GENERIC_READ`: `QUERY`, `READ_USN_JOURNAL` (819 и 455 записей в первых 64 КиБ) и `ENUM_USN_DATA` работают. Handle корня каталога даже с правами даёт `ERROR_ACCESS_DENIED` на `READ`/`ENUM`: брокеру нужен именно handle тома. |
| SRUM | `SRUDB.dat` открывается с `GENERIC_READ` (share RWD), 20 МиБ. Разбор ESE не проверяли. |

Вывод: путь брокера v1 подтверждён для Prefetch, BAM и чтения USN. Парсер Prefetch пишется под SCCA v31 с сохранением ветки v30 для Windows 10. SRUM технически доступен брокеру, но остаётся в v2 из-за разбора ESE.

## Что не проверено

- **Windows 10 22H2 и другие сборки Windows 11** (23H2/24H2/25H2). Проверена только 26300.9711 (26H2). ACL Prefetch и BAM на старых сборках могут быть другими, на сборках без `State` BAM лежит в `bam\UserSettings`.
- Пользователь **вне** группы администраторов (стандартная учётная запись) и доменная учётная запись с групповыми политиками.
- Вариант `MAM\x84` с CRC: на этой сборке не встретился. Версия SCCA 30 (Windows 10) — только по документации.
- Разбор ESE-базы SRUM и копия через VSS.
- ReFS, FAT32/exFAT и съёмные тома. Работа UserAssist при отключённой настройке «Отслеживать запуски приложений».
- Поведение `RtlSetProcessPlaceholderCompatibilityMode` при реальном обходе OneDrive / Cloud Files: проверено только, что вызов работает и возвращает прежний режим.
