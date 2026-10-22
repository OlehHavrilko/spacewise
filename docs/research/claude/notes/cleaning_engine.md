# Движок очистки Cleaner: источники места и механизмы освобождения

> Дополняет, а не дублирует `research_notes/Глубокая очистка и оптимизация Cleaner/optimization.md` (там уже: базовые команды DISM component store, Windows.old через Settings/10 дней, cleanmgr /sageset /sagerun, SHEmptyRecycleBinW, powercfg /h /type, CompactOS query/always/never, Reserved Storage state, vssadmin delete shadows, pagefile, OneDrive Free up space, DISM capabilities/features, winget uninstall, NuGet/npm/pip/Docker prune/WSL unregister) и `cleanup_power.md` (архитектура BleachBit: CleanerML actions, ActionProvider, Worker, DeepScan, Winapp.py, pending/release). Здесь — верификация, точные механизмы, побочные эффекты и новые источники. Дата среза: 2026-10-23. Пометка **[не верифицировано]** = инженерное знание без первичного источника в этой сессии.

## 1. Системные источники: Windows-хранилища, точный механизм и риски

### Takeaway
Крупнейшие системные источники (component store, VSS, hiberfil, Windows.old, DO cache, DriverStore) должны освобождаться только штатными интерфейсами (DISM, vssadmin/VSS COM, powercfg, pnputil, cleanmgr-handlers), а не удалением файлов; `C:\Windows\Installer` по позиции Microsoft удалять нельзя вовсе — orphan-логика PatchCleaner допустима только как «переместить в карантин на другой том» с исключениями.

### Cited Findings

#### WinSxS / component store
- Реальный overhead WinSxS = «Backups and Disabled Features» + «Cache and Temporary Data» из `Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore`; размер, показанный Explorer, не учитывает hard links (пример MS: Explorer 4.98 GB, реальный overhead 507 MB). Поля `Number of Reclaimable Packages` и `Component Store Cleanup Recommended` — готовый сигнал для движка «делать/не делать». Требуется elevated prompt. — [Determine the Actual Size of the WinSxS Folder](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/determine-the-actual-size-of-the-winsxs-folder?view=windows-11)
- `Dism.exe /online /Cleanup-Image /StartComponentCleanup /ResetBase` удаляет все superseded версии каждого компонента; **все существующие update packages после этого нельзя удалить** (будущие — можно). Это необратимо. — [Clean Up the WinSxS Folder](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11)
- `/SPSuperseded` относится к service pack и делает его неудаляемым — для Windows 10/11 практической ценности не имеет (service packs не выпускаются). — [Clean Up the WinSxS Folder](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11)

#### Delivery Optimization
- DO очищает свой кэш автоматически (по времени и при превышении объёма); ручной официальный путь — Disk Cleanup → «Delivery Optimization Files». Для peering у источника ограниченно 4 слота кэшированного контента. — [Delivery Optimization FAQ](https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-faq)

#### VSS / System Restore
- `vssadmin resize shadowstorage /for=<vol> /on=<vol> [/maxsize=<N MB|GB|%|UNBOUNDED>]` — штатный способ ограничить diff area (пример MS: `/For=C: /On=C: /MaxSize=20%`). Уменьшение maxsize может приводить к удалению самых старых shadow copies **[не верифицировано в этом источнике — проверить текст remarks]**. — [vssadmin resize shadowstorage](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/vssadmin-resize-shadowstorage)
- Программный путь без парсинга вывода vssadmin: управляющие COM-интерфейсы `IVssSnapshotMgmt`/`IVssSnapshotMgmt2`, `IVssDifferentialSoftwareSnapshotMgmt` (1/2/3), `IVssEnumMgmtObject` (enumerate/resize diff areas). — [Volume Shadow Copy API Interfaces](https://learn.microsoft.com/en-us/windows/win32/vss/volume-shadow-copy-api-interfaces)
- Побочный эффект на SSD: Storage Optimizer дефрагментирует SSD раз в месяц, если включены volume snapshots — из-за медленного copy-on-write volsnap на фрагментированном томе (цитата инженера Windows storage team в блоге Hanselman). — [Hanselman: Does Windows defragment your SSD?](https://www.hanselman.com/blog/the-real-and-complete-story-does-windows-defragment-your-ssd)

#### Disk Cleanup handlers (IEmptyVolumeCache)
- Обработчики Disk Cleanup — COM-объекты `IEmptyVolumeCache`/`IEmptyVolumeCache2` с методами `Initialize`/`InitializeEx`, `GetSpaceUsed`, `Purge`, `ShowProperties`, `Deactivate`. — [IEmptyVolumeCache](https://learn.microsoft.com/en-us/windows/win32/api/emptyvc/nn-emptyvc-iemptyvolumecache)
- `Purge(dwlSpaceToFree, picb)`: значение -1 = удалить все свои файлы; прогресс через `IEmptyVolumeCacheCallBack::PurgeProgress`, отмена через `E_ABORT`. — [IEmptyVolumeCache::Purge](https://learn.microsoft.com/en-us/windows/win32/api/emptyvc/nf-emptyvc-iemptyvolumecache-purge)
- Регистрация: подключи в `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\<Handler>` с CLSID и значениями; `cleanmgr /sageset:nnnn` пишет `StateFlagsnnnn` (0 — не запускать, 2 — включить), `/sagerun:nnnn` запускает профиль; обычный запуск пишет `StateFlags` 0/1. — [Creating a Disk Cleanup Handler](https://learn.microsoft.com/en-us/windows/win32/lwef/disk-cleanup)

#### DriverStore
- `pnputil /delete-driver <oem#.inf> [/uninstall] [/force] [/reboot]` (с Windows 10 1607): `/force` удаляет пакет, даже если он используется устройствами; большинство команд требуют admin. — [PnPUtil Command Syntax](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax); [PnPUtil examples](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-examples)
- DriverStore Explorer (RAPR): «Smart Cleanup» автоматически выбирает старые/неиспользуемые версии драйверов, есть Export (backup) и force delete; сам автор предупреждает, что последствия зависят от состояния системы и могут затронуть работу устройств. — [DriverStoreExplorer](https://github.com/lostindark/DriverStoreExplorer)

#### Windows Installer cache
- Microsoft: `C:\Windows\Installer` хранит уникальные файлы, нужные для uninstall/update/repair; **не должен удаляться**; проблемы проявляются отложенно (при патче/удалении); файлы нельзя скопировать с другой машины; в ряде случаев нужна переустановка ОС, поддержка MS не восстанавливает. — [Missing Windows Installer cache requires a computer rebuild](https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/missing-windows-installer-cache)
- PatchCleaner: сравнивает файлы в каталоге с тем, что Windows Installer сообщает как используемое; показывает in-use и orphaned; известная ложная классификация Adobe Acrobat Reader patches как ненужных (Adobe использует проприетарный механизм обновления) → введены exclusion filters (по title/subject/author/digital signature), по умолчанию исключён «Acrobat»; рекомендуемый режим — move, а не delete, чтобы можно было вернуть. — [PatchCleaner](https://www.homedev.com.au/Free/PatchCleaner)

#### Дампы и WER
- WER LocalDumps (`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps`): по умолчанию выключен; `DumpFolder` по умолчанию `%LOCALAPPDATA%\CrashDumps`, `DumpCount` по умолчанию 10 (ротация самых старых), `DumpType` 0/1/2 (custom/mini/full); есть per-app ключи. Для сервисов дампы пишутся в профиль служебной учётной записи. — [Collecting User-Mode Dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)

#### Storage Sense (как эталон политики)
- Policy CSP `ConfigStorageSenseRecycleBinCleanupThreshold`: 0–365 дней, по умолчанию 30, 0 = не удалять из корзины; реестр `Software\Policies\Microsoft\Windows\StorageSense`; Windows 10 1903+. Аналогичные политики для Downloads и cloud dehydration. — [Policy CSP – Storage](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-storage)
- Пользователь выбирает пороги «Recycle Bin старше N» и «Downloads не открывались N». — [Manage drive space with Storage Sense](https://support.microsoft.com/en-us/windows/manage-drive-space-with-storage-sense-654f6ada-7bfc-45e5-966b-e24aded96ad5)

#### Locked files / отложенное удаление
- Restart Manager: `RmStartSession` → `RmRegisterResources` → `RmGetList` возвращает `RM_PROCESS_INFO[]` (кто держит файл) и `RM_REBOOT_REASON`; shutdown только при наличии прав, кросс-сессионный shutdown не поддерживается; порядок остановки GUI → console → services → explorer. — [About Restart Manager](https://learn.microsoft.com/en-us/windows/win32/rstmgr/about-restart-manager); [RmGetList](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist)
- `MoveFileEx(src, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)` пишет в `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations` (REG_MULTI_SZ); возврат отражает только запись в реестр, не результат; директория удаляется только если пуста; нужен доступ к HKLM (admin). — [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)

### Inferences
- **Матрица системных источников** (размеры — типичные порядки, не измерены здесь):

| Источник | Механизм | Права | Обратимость | Главный риск |
|---|---|---|---|---|
| WinSxS overhead | `DISM /StartComponentCleanup` (без ResetBase по умолчанию) | Admin | Нет | Долго (минуты–десятки минут), не прерывать |
| WinSxS ResetBase | `/StartComponentCleanup /ResetBase` | Admin | Нет | Нельзя удалить текущие KB → только «Expert» |
| Windows.old / `$Windows.~BT` / `$Windows.~WS` | Disk Cleanup handler «Previous Windows installation(s)» / Settings | Admin | Нет (теряется rollback) | ACL TrustedInstaller — ручное удаление ломается |
| DO cache | handler «Delivery Optimization Files» | Admin | Перекачается | Нулевой |
| `SoftwareDistribution\Download` | handler «Windows Update Cleanup» либо stop `wuauserv`+`bits` → удалить → start | Admin | Перекачается | Удаление при активной установке обновления |
| VSS | `vssadmin resize/delete` или VSS mgmt COM | Admin | Нет | Потеря restore points/previous versions |
| hiberfil.sys | `powercfg /h off` или `/h /type reduced` | Admin | Да (включить обратно) | Off ломает Fast Startup и hibernate |
| Reserved Storage | `DISM /Set-ReservedStorageState /State:Disabled` | Admin | Да | Обновления могут не поставиться при нехватке места |
| DriverStore старые oem*.inf | `pnputil /delete-driver` без `/force` | Admin | Частично (export перед удалением) | Удаление пакета работающего устройства |
| `Windows\Installer` | Не удалять; orphan → move на другой том | Admin | Да при move | Сломанный uninstall/patch |
| MEMORY.DMP / Minidump / WER / LiveKernelReports | Удаление файлов; лучше через handlers «System error memory dump files», «Windows Error Reporting» | Admin | Нет | Потеря диагностики сбоя |
| CBS/DISM logs, `Windows\Logs`, `Panther` | Удаление вне активного servicing | Admin | Нет | Удалять при работающем TrustedInstaller нельзя |
| Thumbnail cache (`thumbcache_*.db`) | handler «Thumbnails» | User | Регенерируется | Explorer держит файлы → Restart Manager |
| DirectX Shader Cache | handler «DirectX Shader Cache» | User | Регенерируется | Первые запуски игр медленнее |
| Font cache (`FontCache` service files) | Только остановив `FontCache` | Admin | Регенерируется | Малый выигрыш, риск артефактов — не в MVP |

- **Вызов IEmptyVolumeCache программно** технически возможен: перечислить подключи VolumeCaches, `CoCreateInstance(CLSID)`, `Initialize(hkey, volume, ...)` → `GetSpaceUsed` → `Purge(-1)`. Это даёт per-volume scope и точный preview, чего нет у `cleanmgr /sagerun`. Но контракт описан для «Disk Cleanup manager», а не для сторонних хостов; часть системных handlers может проверять процесс/привилегии. Требуется прототип и тест каждого handler на 10 22H2 / 11 24H2 / 25H2. Это лучший кандидат для «системного» адаптера Cleaner.
- Ручное удаление Windows.old и `$Windows.~BT` файловыми API — анти-паттерн (TrustedInstaller ACL, reparse points, ресурсоёмкие takeown). Использовать handler.
- Для очистки `SoftwareDistribution` проверять отсутствие pending reboot (`RebootRequired`/CBS `RebootPending`) и активной установки; иначе — skip.
- Для DriverStore безопасный алгоритм (логика RAPR): группировать пакеты по (provider, class, original inf name), оставлять самую новую версию и любую версию, привязанную к присутствующему устройству; удалять без `/force`; export перед удалением.
- PatchCleaner-логика (MSI API `MsiEnumProductsEx`/`MsiEnumPatchesEx`/`MsiGetPatchInfoEx` → множество «нужных» LocalPackage; разница с содержимым каталога = orphan) должна быть только **move на другой том** + исключения (Acrobat и т.п.). Удаление на том же томе места не даёт, а удаление навсегда противоречит позиции MS.

### Gaps
- Не найдена актуальная официальная страница MS со списком встроенных VolumeCaches handlers для 24H2/25H2 и подтверждением, что сторонний процесс может их хостить; нужен эмпирический тест.
- Страница MS о хранилище Windows Update (`windows-update-storage`) вернула 404; точная официальная процедура очистки `SoftwareDistribution` не верифицирована.
- Справка powercfg (Learn) вернула 404 в этой сессии; размеры hiberfil (full ≈40%, reduced ≈20% RAM) взяты из предыдущих заметок.
- MSI API страницы (MsiEnumPatchesEx) вернули 404; сигнатуры не сверены.
- Не проверено, удаляет ли `vssadmin resize` старые snapshots при уменьшении maxsize (вероятно да, но без первичной цитаты).
- Нет данных о Windows.old в 25H2 (в т.ч. сохраняется ли поведение 10-дневного rollback).

## 2. Пользовательские и прикладные источники: браузеры, Electron, лаунчеры, dev-кэши, VHDX

### Takeaway
У большинства dev-инструментов есть официальная CLI-очистка с понятной семантикой (pnpm store prune, yarn cache clean, go clean -modcache, cargo clean, Gradle auto-retention) — её и надо вызывать; браузерные кэши делятся на безопасные (Cache, Code Cache, GPUCache) и logout-опасные (Network\Cookies, Local Storage, IndexedDB); VHDX (WSL/Docker) освобождает место только после `wsl --shutdown` и компактации (Optimize-VHD на Pro / diskpart на Home) или sparse-режима.

### Cited Findings

#### Браузеры
- Chromium-профили: `Cache`, `Code Cache` (V8 bytecode), `GPUCache` (shader data) временные и пересоздаются при закрытом браузере; cookies в новых версиях лежат в `<Profile>\Network\Cookies` — их удаление разлогинивает; не удалять каталог профиля целиком. Источники — вендоры клинеров, официальной документации Google по папкам не найдено. — [Mole: Free Up Browser Storage](https://mole.fit/blog/how-to-free-up-browser-storage-mac); [cloudinfra.net](https://cloudinfra.net/how-to-clear-google-chrome-cache-and-cookie-information/)
- BleachBit определение Chrome распознаёт несколько профилей, учитывает запущенные процессы, раздельные опции, отдельную очистку AI models и GPU/shader caches (подтверждено в предыдущей заметке по исходнику). — [cleanup_power.md, раздел 1] (см. `research_notes/Глубокая очистка и оптимизация Cleaner/cleanup_power.md`)

#### JS/Rust/Go/JVM
- `pnpm store prune` удаляет unreferenced пакеты (не используемые ни одним проектом системы); «не вредно, без побочных эффектов для проектов», при надобности скачаются снова; не запускать слишком часто (смена веток вызовет повторную загрузку); печатает размер удалённого. — [pnpm store](https://pnpm.io/cli/store)
- Yarn Berry: `yarn cache clean` — локальные архивы; `yarn cache clean --mirror` — глобальный кэш в `~/.yarn`. — [yarn cache clean](https://yarnpkg.com/cli/cache/clean)
- `cargo clean` без опций удаляет весь `target` каталог проекта. — [cargo-clean](https://doc.rust-lang.org/cargo/commands/cargo-clean.html)
- `go clean [-cache] [-testcache] [-modcache] [-fuzzcache]`; `-n` печатает команды без выполнения (dry-run). — [cmd/go](https://pkg.go.dev/cmd/go#hdr-Remove_object_files_and_cached_files)
- Gradle сам чистит `~/.gradle`: released distributions 30 дней, snapshot 7, downloaded resources 30, created resources 7, build cache 7, daemon logs 14 дней (настраивается init-скриптом). — [Gradle-managed Directories](https://docs.gradle.org/current/userguide/directory_layout.html)

#### WSL2 / Docker / Hyper-V
- `wsl --manage <distro> --resize` доступен с WSL 2.5+; любое обслуживание VHD начинается с `wsl.exe --shutdown`; поиск пути ext4.vhdx и `diskpart select vdisk file=...` описаны в официальном гайде. — [How to manage WSL disk space](https://learn.microsoft.com/en-us/windows/wsl/disk-space)
- Sparse VHD введён как experimental (WSL сентябрь 2023): `sparseVhd=true` в `[experimental]` `.wslconfig` для новых VHD, `wsl --manage <distro> --set-sparse true|false` для существующих. — [WSL September 2023 update](https://devblogs.microsoft.com/commandline/windows-subsystem-for-linux-september-2023-update/); [ElevenForum summary](https://www.elevenforum.com/t/windows-subsystem-for-linux-wsl-2-0-0-september-2023-update-released.18072/)
- Пользователи сообщают, что sparse VHD не всегда сжимается автоматически; ручная компактация Optimize-VHD требует несжатый, незашифрованный и **не-sparse** VHD (сначала снять sparse). — [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/1526083/in-wsl2-with-sparse-vhd-the-storage-usage-does-not)
- Утверждение, что auto-reclaim sparse VHD спрятан за `--allow-unsafe` из-за риска порчи данных, найдено только в стороннем alpha-проекте; официального подтверждения нет. — [wsldisk](https://github.com/wslkit/wsldisk)
- `Optimize-VHD -Mode`: для VHDX по умолчанию Quick; Full сканирует нулевые блоки и требует монтирования read-only; Pretrimmed/Prezeroed работают без read-only, но находят меньше. Модуль Hyper-V (не на Home). — [Optimize-VHD](https://learn.microsoft.com/en-us/powershell/module/hyper-v/optimize-vhd)
- Docker Desktop VHDX растёт, но не сжимается после удаления образов; процедура сообщества: `docker system prune`/`builder prune` → остановить Docker Desktop и `wsl --shutdown` → `Optimize-VHD -Mode Full` (Pro) или `diskpart: select vdisk / attach vdisk readonly / compact vdisk / detach vdisk` (Home); путь по умолчанию `%LOCALAPPDATA%\Docker\wsl\...\docker_data.vhdx`; есть отчёты, что compact ничего не даёт. Официальной Docker-документации не найдено. — [Nick Janetakis](https://nickjanetakis.com/blog/reclaiming-tons-of-diskspace-by-compacting-your-docker-desktop-wsl-2-vm); [Docker forum](https://forums.docker.com/t/docker-desktop-wsl2-vhdx-grows-by-1-5gb-after-every-update-no-way-to-reclaim-space/151729)

#### Игры
- Steam: официальная проверка целостности (Verify integrity of game files) восстанавливает удалённые/повреждённые файлы игры — это механизм восстановления после ошибочной очистки внутри `steamapps\common`. — [Steam Support](https://help.steampowered.com/en/faqs/view/4BD4-4528-6B2E-8327)

### Inferences
- **Каталог прикладных источников** (пути и безопасность — **[не верифицировано]** первичными источниками в этой сессии, нужна проверка на эталонной машине):

| Источник | Путь/команда | Safe-класс | Побочный эффект |
|---|---|---|---|
| Chromium cache (Chrome/Edge/Brave/Vivaldi/Opera) | `<UserData>\<Profile>\Cache\Cache_Data`, `Code Cache`, `GPUCache`, `<UserData>\ShaderCache`, `GrShaderCache` | Safe (браузер закрыт) | Медленнее первые загрузки |
| Chromium Service Worker CacheStorage | `<Profile>\Service Worker\CacheStorage` | Caution | PWA/офлайн-данные сайтов |
| Chromium cookies/sessions/storage | `Network\Cookies`, `Sessions`, `Local Storage`, `IndexedDB` | Privacy-only | Logout, потеря данных сайтов |
| Chromium AI model | `OptGuideOnDeviceModel` (Gemini Nano, ГБ) | Caution | Перекачается при включённой функции |
| Firefox | `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\<p>\cache2`, `startupCache` | Safe | — ; cookies.sqlite/places.sqlite — privacy-only |
| Electron (Discord, Slack, VS Code, new Teams) | `%APPDATA%\<app>\Cache`, `Code Cache`, `GPUCache`, `Service Worker\CacheStorage` | Safe при закрытом приложении | VS Code: `CachedExtensionVSIXs`, `CachedData` тоже кэш |
| Steam | `steamapps\shadercache` (Safe), `steamapps\downloading` (Safe если нет активной загрузки), `steamapps\workshop` (Caution: моды подписок перекачаются), `appcache`, `logs`, `dumps` | см. | Пересборка шейдеров |
| Epic / Battle.net / EA / Ubisoft | webcache лаунчеров, логи, `ProgramData\Battle.net\Agent` старые версии | Safe/Caution | Перекачка обновлений агента |
| GPU-драйверы | `C:\NVIDIA`, `C:\AMD` (распакованные инсталляторы), `ProgramData\NVIDIA Corporation\Downloader` | Safe | Нельзя «repair» без нового скачивания |
| `ProgramData\Package Cache` | Burn/WiX bundles (VS, .NET, VC++) | **Unsafe** | Ломает repair/uninstall, аналог Windows\Installer |
| npm | `npm cache clean --force` | Safe | Перекачка |
| pnpm | `pnpm store prune` | Safe | — |
| yarn | `yarn cache clean [--mirror]` (Berry), `yarn cache clean` (v1) | Safe | — |
| pip | `py -m pip cache purge` | Safe | — |
| NuGet | `dotnet nuget locals all --clear` | Safe/Caution | Offline-сборки упадут |
| Gradle | Встроенная retention; ручное — `~/.gradle/caches` при остановленных демонах (`gradle --stop`) | Caution | — |
| Maven | `~/.m2/repository` (`mvn dependency:purge-local-repository` per project) | Caution | Перекачка всего |
| Cargo | `cargo clean` per project; `~/.cargo/registry/cache` | Safe | Пересборка |
| Go | `go clean -cache -modcache` | Safe | Пересборка/перекачка |
| Android SDK/AVD | `sdkmanager --uninstall`, `avdmanager delete avd -n` | Expert | Потеря эмулятора/образов |
| Visual Studio | `%LOCALAPPDATA%\Microsoft\VisualStudio\<ver>\ComponentModelCache`, старые экземпляры — через VS Installer | Caution | — |
| Проектные артефакты | `node_modules`, `bin/obj`, `target`, `.venv` | Expert, только неактивные проекты (age-based) | Не запускать `dotnet clean` на чужом проекте |

- **Только через официальную CLI** там, где она есть: CLI знает layout своей версии, держит lock-файлы и не трогает активные процессы. Файловое удаление — fallback с проверкой процессов (Restart Manager).
- **VHDX-адаптер** — отдельная сущность «compact virtual disk»: (1) prune внутри (docker prune / `fstrim` в distro), (2) `wsl --shutdown` и проверка, что `vmmem`/`vmmemWSL` не держит файл, (3) Pro: `Optimize-VHD -Mode Full` после `Mount-VHD -ReadOnly`; Home: diskpart-скрипт; (4) измерить размер файла и `GetDiskFreeSpaceEx` до/после. Sparse-режим предлагать как опцию с предупреждением (experimental, сообщения об отсутствии эффекта). Перед любой операцией — обязательная проверка, что VHDX не sparse/compressed/encrypted.
- Hyper-V VHDX пользовательских VM: только при выключенной VM, Optimize-VHD; dynamic → compact; fixed — не уменьшить без конвертации.

### Gaps
- Нет официальной документации Google/Microsoft Edge/Mozilla о том, какие папки профиля безопасно удалять; таблица опирается на вендоров клинеров и исходник BleachBit.
- Не верифицированы текущие (2026) пути Electron-приложений, в частности new Teams (MSIX, `%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache`) и Discord.
- Нет официальной Docker-документации по компактации `docker_data.vhdx` (только community guides); не проверено, появилась ли в Docker Desktop встроенная функция reclaim в 2025–2026.
- Не найден официальный статус sparse VHD в WSL 2.6+ (вышел ли из experimental, есть ли риск порчи данных).
- Не проверены официальные команды Epic/EA/Ubisoft/Battle.net по очистке кэшей; Steam shadercache/downloading семантика — из инженерного опыта.

## 3. Сжатие вместо удаления, дубликаты, удаление программ

### Takeaway
WOF-сжатие (`compact /EXE:XPRESS4K..LZX`, CompactOS) — обратимое освобождение места без потери данных для read-mostly файлов; NTFS Data Deduplication на клиентской Windows не поддерживается, поэтому дедупликация делается приложением (size → partial hash → full hash → byte compare) с hard links только на том же томе и только для неизменяемых файлов.

### Cited Findings
- `compact /EXE[:algorithm]` — сжатие для исполняемых/редко изменяемых файлов; алгоритмы `XPRESS4K` (быстрый, по умолчанию), `XPRESS8K`, `XPRESS16K`, `LZX` (максимальное сжатие); без `/EXE` применяется классическое NTFS-сжатие (LZNT1), директории маркируются для новых файлов; `/CompactOs:query|always|never`. — [compact](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact)
- Программное API: файловое сжатие/распаковка (`FSCTL_SET_COMPRESSION` для NTFS; WOF через `WofSetFileDataLocation`/`FSCTL_SET_EXTERNAL_BACKING`). — [File Compression and Decompression](https://learn.microsoft.com/en-us/windows/win32/fileio/file-compression-and-decompression)
- Data Deduplication документирован как функция Windows Server (file servers, VDI, backup); на ReFS — с Server 2019; установка на Windows 11 сообществом признана неподдерживаемой. — [Data Deduplication Overview](https://learn.microsoft.com/en-us/windows-server/storage/data-deduplication/overview); [Interop](https://learn.microsoft.com/en-us/windows-server/storage/data-deduplication/interop); [ElevenForum thread](https://www.elevenforum.com/t/how-do-i-install-data-dedupe-on-windows-11.12236/post-252105)
- `CreateHardLink`: максимум 1023 hard links на файл; длинные пути требуют `\\?\`. Hard link возможен только в пределах одного тома NTFS. — [CreateHardLinkW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw)
- Удаление программ: `winget uninstall --id <id> --exact` — каталогизированный штатный путь; uninstall может вызвать vendor installer, диалог, reboot (из предыдущей заметки). — [optimization.md] (см. `research_notes/Глубокая очистка и оптимизация Cleaner/optimization.md`)

### Inferences
- **WOF-сжатие как действие Cleaner**: кандидаты — каталоги игр и программ, которые не обновляются часто (`Program Files`, неактивные Steam-игры), старые SDK, архивы логов. Не сжимать: активно записываемые файлы (БД, VHDX, pagefile/hiberfil, файлы в OneDrive, уже сжатые форматы — видео/zip/jpg, игры с anti-cheat/патчем дельтами). WOF-файл при записи распаковывается обратно (WOF — read-only backing) **[не верифицировано в этой сессии]**; поэтому годится только для read-mostly. LZX даёт максимум, но выше CPU при чтении; XPRESS4K — дефолт для SSD. Обратимость: `compact /u /exe`. Учёт выигрыша — через `GetCompressedFileSizeW` до/после.
- **Дубликаты — алгоритм**: (1) фильтр по размеру ≥ порога (напр. 1 MB) и одинаковому размеру; (2) исключить hard links (одинаковый FileId/VolumeSerial — это уже не дубль); (3) partial hash первых+последних 64 KB (xxHash3/BLAKE3); (4) полный hash; (5) опционально byte-compare перед действием. Действия: удалить копию (default), заменить hard link (только тот же том, только для неизменяемых файлов: фото/видео/архивы — изменение одной «копии» меняет все), не трогать системные/программные каталоги и OneDrive placeholders (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`).
- **Удаление программ**: читать `HKLM/HKCU\SOFTWARE\(WOW6432Node\)Microsoft\Windows\CurrentVersion\Uninstall\*` (`UninstallString`, `QuietUninstallString`, `EstimatedSize`, `InstallDate`, `SystemComponent`, `WindowsInstaller`); MSIX/AppX — `Get-AppxPackage`/`Remove-AppxPackage` (через PackageManager API); winget как унифицированный фасад. «Неиспользуемость» — из usage analytics (Prefetch/последний запуск). Leftovers — только после uninstall, только каталоги, совпадающие по `InstallLocation`/publisher, в карантин; реестровые leftovers не трогать (см. раздел 5, registry cleaners). `QuietUninstallString` запускать только с явным подтверждением: это исполнение произвольного кода вендора.

### Gaps
- Нет первичного бенчмарка скорости XPRESS/LZX на современных NVMe; проверить самостоятельно.
- Не подтверждено официально поведение WOF-файла при записи (распаковка) — сверить с документацией `WofSetFileDataLocation`.
- Документация `Uninstall` registry values (QuietUninstallString) в этой сессии не открывалась.

## 4. Как моделируют очистку ведущие инструменты: что копировать, что избегать

### Takeaway
Копировать: декларативные правила как данные (CleanerML с XSD, Winapp2 FileKey/RegKey/DetectFile/ExcludeKey), preview того же кода, что и execute, MFT-скан для аналитики (WizTree). Избегать: тысяч непроверенных community-правил без статуса, реестровых правил, «shred» как дефолта.

### Cited Findings
- CleanerML: XML + XSD; delete/truncate/shred файлов, удаление ключей/значений реестра, специализированные actions; поиск по glob, walk дерева, deep scan; regex-фильтр; детекция ОС; экспорт строк в gettext. — [CleanerML introduction](https://docs.bleachbit.org/cml/cleanerml.html)
- Winapp2 распространяется в нескольких «флейворах»: base Non-CCleaner (может пересекаться/конфликтовать с правилами CCleaner/BleachBit), CCleaner flavor (уменьшенное пересечение), отдельный CCleaner 7 формат, который подкачивает и патчит Winapp2ool; Winapp2ool умеет скачивать и **trim** (оставлять только правила для установленного ПО). — [Winapp2](https://github.com/MoscaDotTo/Winapp2)
- WizTree позиционирует себя как самый быстрый анализатор (treemap, файлы/папки по размеру); скорость за счёт чтения MFT NTFS напрямую — **[страница about проиндексирована, но цитата про MFT не извлечена]**. — [WizTree](https://diskanalyzer.com/)
- Предыдущие заметки: в BleachBit preview и execute идут одним путём `execute(really_delete)`; правила имеют статусы pending/release. — `research_notes/Глубокая очистка и оптимизация Cleaner/cleanup_power.md`

### Inferences
- Формат правил Cleaner: собственная схема (JSON/YAML + JSON Schema) с полями `detect` (файл/реестр/процесс), `targets` (glob/walk + regex + age/size), `action` (delete / cli-command / handler / compact / vhd-compact), `running_process` (блокировка), `risk_class`, `reversible`, `requires_admin`, `side_effects`, `verified_on` (версии ОС/приложения). Импортер Winapp2 — только FileKey с trim и статусом `community-unverified`, RegKey игнорировать.
- От CCleaner/Wise взять UX «категория → размер → галочка», но не «health score» и не registry cleaner. От PrivaZer — идею глубокого следа (MFT-записи, USN), но не wipe free space как способ «освободить место» (не освобождает). От TreeSize/WizTree — MFT-скан и treemap для аналитики; удаление из аналитики — только в карантин.

### Gaps
- Не открывались документы CCleaner, Wise Disk Cleaner, PrivaZer, TreeSize в этой сессии; сравнение сделано по известным публичным функциям, без цитат.

## 5. Верификация освобождённого места и исключение snake-oil

### Takeaway
Единственная честная метрика — `GetDiskFreeSpaceEx` (`lpTotalNumberOfFreeBytes`) тома до/после; Корзина и карантин на том же томе места не освобождают; registry cleaners, RAM optimizers и ручная дефрагментация SSD исключаются как бесполезные или вредные.

### Cited Findings
- `GetDiskFreeSpaceExW`: `lpFreeBytesAvailableToCaller` учитывает per-user quotas и может быть меньше `lpTotalNumberOfFreeBytes`; принимает любой каталог тома; нужен `FILE_LIST_DIRECTORY`. — [GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw)
- `SHFileOperation` с `FOF_ALLOWUNDO` переносит в Корзину (страница проиндексирована). Корзина — каталог `$Recycle.Bin` на том же томе, поэтому место не освобождается до очистки. — [SHFileOperationW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shfileoperationw)
- Storage Sense по умолчанию удаляет из корзины файлы старше 30 дней — т.е. «в корзину» = отложенное освобождение. — [Policy CSP – Storage](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-storage)
- Registry cleaners: Microsoft (KB 2563254) не поддерживает утилиты очистки реестра, не отвечает за вызванные ими проблемы, которые иногда лечатся только переустановкой Windows. — [Microsoft Support KB2563254](https://support.microsoft.com/en-us/topic/2563254)
- RAM optimizers: Russinovich (2003) — «в лучшем случае без эффекта, в худшем серьёзно ухудшают производительность»; механизмы — `EmptyWorkingSet` (выталкивает рабочие наборы в pagefile) или аллокация большого блока, вытесняющая cache/standby; «Available» = standby + zeroed + free, очистка standby не добавляет ёмкости. — [IT Pro Today: The Memory-Optimization Hoax](https://itprotoday.com/cloud-computing/memory-optimization-hoax); [How-To Geek](https://www.howtogeek.com/171424/why-memory-optimizers-and-ram-boosters-are-worse-than-useless)
- SSD defrag: Windows Storage Optimizer сам решает — retrim по расписанию и дефрагментация SSD раз в месяц только при включённых snapshots; чрезмерная фрагментация может упереться в лимит фрагментов файла. Сторонняя «дефрагментация SSD» дублирует или ломает эту логику. — [Hanselman](https://www.hanselman.com/blog/the-real-and-complete-story-does-windows-defragment-your-ssd)

### Inferences
- **Accounting contract**: для каждого действия фиксировать `planned_bytes` (сумма allocation size, не logical size; учесть hard links, WOF, sparse, cloud placeholders), `free_before`/`free_after` по `lpTotalNumberOfFreeBytes`, `delta_measured`; расхождение >10% — помечать (другие процессы пишут на том, VSS diff area растёт при удалении — удаление файлов на томе со snapshots может временно **уменьшать** свободное место за счёт copy-on-write).
- **Карантин** имеет смысл только на другом томе или как «удалить после N дней»; для системного тома C: при единственном диске честно показывать «освобождено: 0 до подтверждения».
- **Locked files**: сначала `RmGetList` → показать процесс → предложить закрыть; если нельзя — `MoveFileEx(DELAY_UNTIL_REBOOT)` только для файлов в разрешённых путях, с пометкой «освободится после перезагрузки», и проверить `PendingFileRenameOperations` после ребута.
- Исключить из продукта: registry cleaning, RAM boost, SSD defrag, «ускорение интернета», wipe free space как способ получения места, удаление `Prefetch` (Windows сам управляет; удаление замедляет запуск — **[не верифицировано в этой сессии]**).

### Gaps
- Оригинальная статья Russinovich 2003 — источник старый; современного MS-документа против RAM optimizers не найдено.
- Страница KB 2563254 найдена поиском, текст прочитан через сниппеты, не полностью.
- Не найдено официальное MS-подтверждение влияния удаления Prefetch.
