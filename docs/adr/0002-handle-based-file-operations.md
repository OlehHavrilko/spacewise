# ADR 0002. Файловые операции executor по handle

- Статус: принято (spike), требует подтверждения на Windows 10 22H2
- Дата: 2026-10-23
- Связано: ТЗ §7.4 «Executor удаляет по handle», §16 Q7, Q8
- Код spike: `spikes/FileOps/` (вне `Spacewise.slnx`)

## Контекст

ТЗ §7.4 требует, чтобы executor удалял и перемещал в карантин объект, открытый по handle относительно проверенного родителя, а не по строковому пути. Открытые вопросы:

- Q7: работают ли `FileRenameInfoEx` с POSIX-флагами и `FILE_DISPOSITION_INFO_EX`, и генерирует ли их CsWin32.
- Q8: может ли обычный пользователь создать `.SpacewiseQuarantine` в корне не-системного тома.

Spike проверил примитивы на этой машине:

- ОС: Windows 11 Pro, DisplayVersion 26H2, build **26300.9711** (в реестре `ProductName` по-прежнему «Windows 10 Pro», это известная особенность).
- Процесс не elevated: `Administrators` в токене не активна (`IsInRole(Administrator) = False`).
- .NET 10.0.13, x64. Тома C: и D: оба NTFS, Fixed.
- Все опыты шли в `%TEMP%\SpacewiseFileOps-<guid>`, плюс один каталог `D:\.SpacewiseQuarantine` (создан и удалён программой).

## Что проверено

| Шаг | Что делали | Результат | Коды |
|---|---|---|---|
| 1 | `CreateFileW(parent, FILE_LIST_DIRECTORY\|FILE_READ_ATTRIBUTES\|SYNCHRONIZE, share RWD, BACKUP_SEMANTICS\|OPEN_REPARSE_POINT)` + `GetFileInformationByHandleEx(FileIdInfo)` | OK, получены VolumeSerialNumber (64 бит) и FileId (128 бит), attrs `0x10` | — |
| 2 | `NtCreateFile(RootDirectory=parent, "child.bin", DELETE\|FILE_READ_ATTRIBUTES\|SYNCHRONIZE, share RWD, FILE_OPEN, FILE_OPEN_REPARSE_POINT\|FILE_SYNCHRONOUS_IO_NONALERT\|FILE_NON_DIRECTORY_FILE)` | OK. FileIdInfo совпал с открытием по пути. `FileBasicInfo` и `FileStandardInfo` читаются по handle без FILE_READ_DATA. При созданной hard link `NumberOfLinks=2` | NTSTATUS 0 |
| 3a | `SetFileInformationByHandle(FileDispositionInfoEx, DELETE\|POSIX)` для read-only файла | Отказ | `ERROR_ACCESS_DENIED` (5) |
| 3b | То же с `IGNORE_READONLY_ATTRIBUTE`, другой handle (GENERIC_READ) открыт | OK | — |
| 3c | Пока удаляющий handle открыт | Имя ещё в каталоге, `GetFileAttributesW` → 5 (delete pending) | 5 |
| 3d | Закрыт удаляющий handle, второй handle ещё открыт | Имя исчезло сразу, `GetFileAttributesW` → 2. Новый файл с тем же именем создаётся. Второй handle читает старые данные, `NumberOfLinks=0`, `DeletePending=1` | 2 |
| 4a | Win32 `SetFileInformationByHandle(FileRenameInfoEx)` с `RootDirectory=<handle каталога карантина>` и простым именем | **Отказ при любых флагах** | `ERROR_INVALID_PARAMETER` (87) |
| 4b | `NtSetInformationFile(FileRenameInformationEx=65)`, тот же буфер, `RootDirectory=<карантин>`, `POSIX`, цель существует | Отказ, как и должно быть | `STATUS_OBJECT_NAME_COLLISION` 0xC0000035 (183) |
| 4c | То же с `POSIX\|REPLACE_IF_EXISTS`, другой handle на исходный файл открыт | OK. FileId по handle не изменился, имя в источнике пропало, в карантине новые данные | — |
| 4d | Win32 `FileRenameInfoEx` с `RootDirectory=NULL` и полным DOS-путём, а также `\??\`-путём | Оба OK | — |
| 4e | Кросс-томно C: → D:: `NtSetInformationFile` с `RootDirectory=D:\.SpacewiseQuarantine` | Отказ | `STATUS_NOT_SAME_DEVICE` 0xC00000D4 (17) |
| 4f | Кросс-томно, Win32 с полным путём `D:\...` | Отказ, на D: ничего не появилось | `ERROR_NOT_SAME_DEVICE` (17) |
| 4g | Восстановление: `NtSetInformationFile(RootDirectory=<исходный каталог>, "q.bin")` | OK | — |
| 5a | Открыт handle `A\B`, затем `B` переименован (handle с `FILE_SHARE_DELETE` не мешает) и на его место поставлен junction `mklink /J A\B decoy` (без админа, exit 0). `NtCreateFile(Root=hB, "victim.txt")` | Открыт **исходный** объект, FileId == план | — |
| 5b | Наивное `CreateFileW("A\B\victim.txt")` | Открыт decoy, FileId != план | — |
| 5c | Повторное открытие родителя по пути с `OPEN_REPARSE_POINT` | Открыт сам junction: атрибут `REPARSE_POINT`, FileId != план. Подмена обнаружима | — |
| 5d | `NtCreateFile(Root=hA, "B\victim.txt", FILE_OPEN_REPARSE_POINT)` | Открыт decoy: промежуточный junction **проходится**, флаг защищает только последний компонент | — |
| Q8 | `CreateDirectoryW("D:\.SpacewiseQuarantine")` без elevation | OK, владелец `OLEH\olehh`. Каталог удалён `RemoveDirectoryW` → True | — |
| CsWin32 | Microsoft.Windows.CsWin32 0.3.360 в отдельном scratch-проекте | Генерируются `FILE_DISPOSITION_INFO_EX` (+ enum флагов), `FILE_RENAME_INFO` с union `ReplaceIfExists/Flags`, константы `FILE_RENAME_FLAG_*`, `FILE_ID_INFO`, `FILE_INFO_BY_HANDLE_CLASS` (21, 22), из `Windows.Wdk`: `NtCreateFile`, `NtSetInformationFile`, `FILE_INFORMATION_CLASS` (64, 65), `FILE_DISPOSITION_INFORMATION_EX`. Не найдены `FILE_RENAME_INFO_EX` и `FILE_RENAME_INFORMATION_EX` (PInvoke001): отдельного типа нет, используется `FILE_RENAME_INFO` | — |

Выдержка из вывода (`dotnet run` в `spikes/FileOps`):

```text
OS: 26H2 build 26300.9711 (Microsoft Windows 10.0.26300)
Elevated (Administrators active in token): False; runtime .NET 10.0.13
[S2] FileBasicInfo.attrs=0x20; FileStandardInfo: EOF=4096 Alloc=4096 NumberOfLinks=2 (hard link created: True)
[S3] DispositionEx(DELETE|POSIX) on readonly file -> ERR 5
[S3] DispositionEx(DELETE|POSIX|IGNORE_READONLY) -> OK
[S3] before close of deleting handle: GetFileAttributesW -> INVALID (err 5); listed in dir: True
[S3] after close of deleting handle (other still open): GetFileAttributesW -> INVALID (err 2); listed: False
[S3] read via surviving other handle: "payload-s3"; its NumberOfLinks=0 DeletePending=1
[Q8] CreateDirectoryW(D:\.SpacewiseQuarantine) as non-elevated user -> OK
[S4] RenameInfoEx(Root=quarantine, POSIX|REPLACE_IF_EXISTS) -> ERR 87
[S4] NtSetInformationFile(FileRenameInformationEx, Root=quarantine, POSIX) onto existing name -> NTSTATUS 0xC0000035 (win32 183)
[S4] NtSetInformationFile(FileRenameInformationEx, Root=quarantine, POSIX|REPLACE_IF_EXISTS) -> OK; other handle still open
[S4] FileId same after move: True; src listed: False; quarantine content: "payload-s4"
[S4] cross-volume NtSetInformationFile(FileRenameInformationEx, Root=D:\.SpacewiseQuarantine) -> NTSTATUS 0xC00000D4 (win32 17)
[S4] cross-volume RenameInfoEx(Root=NULL, D:\ full path) -> ERR 17; D: dir empty: True
[S5] (a) NtCreateFile(Root=hB opened before swap, "victim.txt") -> OK; FileId == plan: True
[S5] (b) CreateFileW(path A\B\victim.txt) FileId == plan: False (follows junction to decoy)
[S5] (c) re-open parent by path: REPARSE_POINT attr=True; FileId == plan: False -> IdentityMismatch detectable
[S5] (d) NtCreateFile(Root=hA, "B\victim.txt") -> OK; FileId == plan: False (OPEN_REPARSE_POINT protects only the last component)
[Q8] RemoveDirectoryW(D:\.SpacewiseQuarantine) -> True
cleanup work dir removed: True
```

### Минимальные версии Windows по документации Microsoft

| API / класс / флаг | Минимум | Источник |
|---|---|---|
| `FileIdInfo` (`FILE_ID_INFO`) в `GetFileInformationByHandleEx` | Windows 8 / Server 2012 | [FILE_INFO_BY_HANDLE_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ne-minwinbase-file_info_by_handle_class) |
| `FileDispositionInformationEx` (64) | Windows 10, version 1709 | [FILE_INFORMATION_CLASS](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ne-wdm-_file_information_class) |
| `FileRenameInformationEx` (65) | Windows 10, version 1709 | там же |
| Флаги `FILE_DISPOSITION_*`, включая `POSIX_SEMANTICS` и `IGNORE_READONLY_ATTRIBUTE` | Отдельная версия для флага в docs не указана | [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex) |
| Флаги `FILE_RENAME_*` (`REPLACE_IF_EXISTS`, `POSIX_SEMANTICS`, `IGNORE_READONLY_ATTRIBUTE` и др.) | Отдельная версия для флага в docs не указана; union `Flags` под `_WIN32_WINNT_WIN10_RS1` в заголовке | [FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information) |
| `FileDispositionInfoEx` (21), `FileRenameInfoEx` (22) в Win32 enum | Версия в таблице не указана; Win32 обёртки над классами 64/65 | [FILE_INFO_BY_HANDLE_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ne-minwinbase-file_info_by_handle_class) |

Из документации `FILE_RENAME_INFORMATION`: rename возможен только в пределах тома. Если задан `RootDirectory`, `FileName` должен быть простым именем. `FILE_RENAME_POSIX_SEMANTICS` вместе с `REPLACE_IF_EXISTS` разрешает заменить цель, даже если на неё есть открытые handle.

Windows 10 22H2 (build 19045) формально выше 1709, но на этой машине не тестировалась.

## Решение

1. **Стиль P/Invoke.** Ручные `[LibraryImport]` с blittable-структурами и указателями (`AllowUnsafeBlocks`), как в `spikes/FileOps/Native.cs`. Без runtime marshalling, без `SafeHandle` в сигнатурах ntdll. CsWin32 допустим как генератор структур и констант: нужные типы генерируются. Но буфер переменной длины для rename всё равно собирается вручную, так что выигрыш небольшой. Решение по CsWin32 для продукта: не обязателен, ручной слой короче и прозрачнее. При переходе на CsWin32 закрепить точную версию пакета в csproj.

2. **Набор API executor.**
   - Родитель: `CreateFileW(path, FILE_LIST_DIRECTORY|FILE_READ_ATTRIBUTES|SYNCHRONIZE, FILE_SHARE_READ|WRITE|DELETE, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT)`. Затем проверить `FileBasicInfo` (нет `REPARSE_POINT`) и `FileIdInfo` == план.
   - Цель: `NtCreateFile` с `RootDirectory=<родитель>` и **только однокомпонентным** именем. Доступ `DELETE|FILE_READ_ATTRIBUTES|SYNCHRONIZE`, share RWD, `FILE_OPEN`, опции `FILE_OPEN_REPARSE_POINT|FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE` (для каталога `FILE_DIRECTORY_FILE`).
   - Проверки по handle: `FileIdInfo`, `FileBasicInfo`, `FileStandardInfo.NumberOfLinks`.
   - Удаление: `SetFileInformationByHandle(FileDispositionInfoEx, DELETE|POSIX_SEMANTICS|IGNORE_READONLY_ATTRIBUTE)`. Fallback на `FileDispositionInfo` при `ERROR_INVALID_PARAMETER` (не-NTFS), как в ТЗ.
   - Карантин и восстановление: **`NtSetInformationFile(FileRenameInformationEx=65)`** с `RootDirectory=<handle каталога назначения>` и простым именем. Флаги `POSIX_SEMANTICS`, `REPLACE_IF_EXISTS` только по явной политике. Win32 `SetFileInformationByHandle(FileRenameInfoEx)` с ненулевым `RootDirectory` на этой сборке возвращает 87, поэтому его не использовать.
   - `ERROR_NOT_SAME_DEVICE` (17) / `STATUS_NOT_SAME_DEVICE` — штатный признак «карантин на этом томе невозможен», а не ошибка для retry.

3. **Обход дерева.** Каждый уровень пути открывается относительно handle предыдущего уровня одним компонентом и сверяется по FileId. Многокомпонентные относительные имена (`"B\victim.txt"`) запрещены: шаг 5d показал, что промежуточный junction проходится.

4. **Q8.** Создание `X:\.SpacewiseQuarantine` обычным пользователем возможно, если ACL корня это разрешает. На D: этой машины у `Authenticated Users` есть `(M)` на корень, поэтому создание прошло. Executor должен пробовать создать каталог и при `ERROR_ACCESS_DENIED` переходить к Корзине (ТЗ §7.4), без попыток elevation ради карантина.

## Последствия

- В executor появляется зависимость от `ntdll` (`NtCreateFile`, `NtSetInformationFile`, `RtlNtStatusToDosError`). Это недокументированный для user-mode, но стабильный ABI. Нужен единый слой маппинга NTSTATUS → доменные коды (`IdentityMismatch`, `SkippedChanged`, `NotSameVolume`).
- Читатели файла в процессе (хэширование, проверка) должны открывать с `FILE_SHARE_DELETE`, иначе конфликт с нашим DELETE handle. В spike это проявилось как sharing violation у `File.ReadAllText`.
- Для POSIX delete имя освобождается при закрытии удаляющего handle, даже если файл держит другой процесс. Место на диске освободится только после закрытия последнего handle. UI должен это учитывать в отчёте об освобождённом месте.
- Read-only файлы без `IGNORE_READONLY_ATTRIBUTE` не удаляются (5). Флаг ставить всегда, атрибуты не трогать отдельным вызовом.
- `FILE_SHARE_DELETE` на handle родителя позволяет постороннему переименовать каталог. Это не ломает безопасность (handle указывает на исходный объект), но путь в UI может устареть. Для журнала опираться на FileId.
- Каталог `.SpacewiseQuarantine` создаётся с наследуемым ACL корня. DACL «SYSTEM + Administrators + владелец» из ТЗ нужно задавать явно при создании (не проверено в spike).

## Что не проверено

- Windows 10 22H2 и любые сборки ниже 26300: на этой машине недоступны. Нужен прогон `spikes/FileOps` на Windows 10 22H2, особенно для Win32 `FileRenameInfoEx` с `RootDirectory` и флага `IGNORE_READONLY_ATTRIBUTE`.
- Тома не-NTFS (FAT32, exFAT, ReFS), сетевые и съёмные тома: fallback на `FileDispositionInfo` не проверялся.
- Удаление каталогов снизу вверх по handle, EFS, ADS, файлы с отображённой секцией (`STATUS_CANNOT_DELETE`), файлы без share delete у другого процесса.
- Явный DACL для `.SpacewiseQuarantine\<SID>\<planId>` и поведение на томе с ACL корня по умолчанию (только `AD` для `Authenticated Users`). Q8 проверен на одном томе с расширенным ACL.
- Причина `ERROR_INVALID_PARAMETER` у Win32 обёртки с `RootDirectory` не выяснена: предположительно kernelbase преобразует `FileName` как DOS-путь. Обойдено вызовом ntdll.
- Elevated broker и `MoveFileEx(DELAY_UNTIL_REBOOT)`.
- Гонка «подмена между plan и execute» в многопоточном виде: spike проверил последовательную подмену.
