# Аналитика использования и глубокая очистка

Исследование 23 октября 2026; ссылки проверены в этот день. Уточнённый продукт — мощная утилита анализа, обслуживания и освобождения пространства, включая программы, игры, рабочие проекты и системные категории. Ниже факты Windows/API отделены от предлагаемого дизайна. Существующий starter остаётся analysis-only демонстрацией; перечисленные возможности в нём не реализованы.

## Как доказать использование файлов и программ

### Takeaway

Можно надёжно показывать положительные наблюдения: приложение запущено сейчас, foreground наблюдался в определённый момент, ресурс занят процессом, пакет зарегистрирован, файл изменился. Для вывода «не использовалось» обязательно учитывать период и полноту наблюдения. Из сегодняшнего сканирования нельзя восстановить полную историю использования компьютера.

### Cited Findings

- NTFS может откладывать обновление LastAccessTime до часа; файловые системы имеют разные разрешения времени; SetFileTime позволяет изменять timestamps отдельно от содержимого. Это не доказательство пользовательской важности. [File Times](https://learn.microsoft.com/en-us/windows/win32/sysinfo/file-times), доступ 2026-10-23.
- Обновления last access могут быть включены/отключены политикой fsutil; сама выдача каталога также связана с last access behavior. Приложение должно сначала выяснять состояние и не менять его незаметно ради аналитики. [fsutil behavior](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior), доступ 2026-10-23.
- USN содержит изменения объектов и причины изменений; старые записи могут удаляться. Он не является журналом всех чтений или обратимым журналом. [Change journal records](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records), доступ 2026-10-23.
- QueryFullProcessImageName возвращает путь executable при наличии PROCESS_QUERY_INFORMATION либо PROCESS_QUERY_LIMITED_INFORMATION. Ошибка доступа должна сохраняться как неизвестность. [API](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew), доступ 2026-10-23.
- Win32_ProcessStartTrace описывает событие запуска с PID/parent PID/SID/session/time; security descriptor provider определяет, кто получает события. Это не обещание доступности всех процессов без прав. [WMI class](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/krnlprov/win32-processstarttrace), доступ 2026-10-23.
- SetWinEventHook позволяет подписаться на диапазон событий текущего desktop, включая foreground; out-of-context callback требует корректного message loop. GetLastInputInfo даёт idle state только вызывающей сессии. [WinEvent hook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook), [Idle API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo), доступ 2026-10-23.
- Microsoft Incident Response прямо относит Amcache к evidence of presence/existence: запись не доказывает execution. Guide также разбирает Prefetch и другие artefacts как ограниченные forensic источники. [Microsoft guide, стр. 3](https://cdn-dynmedia-1.microsoft.com/is/content/microsoftcorp/microsoft/final/en-us/microsoft-brand/documents/IR-Guidebook-Final.pdf), [официальное описание](https://www.microsoft.com/en-us/security/blog/2024/04/23/new-microsoft-incident-response-guide-helps-simplify-cyberthreat-investigations/), доступ 2026-10-23.
- ETW требует явной конфигурации session/providers, с собственными ограничениями совместного включения providers. Нельзя считать его бесплатным универсальным датчиком без прав и lifecycle. [ETW sessions](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-an-event-tracing-session), доступ 2026-10-23.

### Inferences

**Правильный результат аналитики.** Разделить оси: происхождение/владелец; тип данных и возможность повторного получения; наблюдаемая активность; стоимость хранения; допустимые действия. «Принадлежит приложению» не равно «кэш», «приложение не наблюдалось» не равно «пользователю не нужно». Общие библиотеки, лицензии, saves, mods и документы показываются отдельно даже внутри AppData.

| Сигнал | Что он поддерживает | Чего он не доказывает | Действие в UI |
|---|---|---|---|
| Живой процесс и image identity | Executable выполняется сейчас | Какие пользовательские проекты ему нужны позже | «Программа работает; закройте перед обслуживанием» |
| Foreground + idle-gated interval | Окно было foreground в наблюдавшейся сессии | Полезность background app или активность документа без чтения | «Использовалась в окне за наблюдаемый период» |
| Prospective process start | Наблюдавшийся запуск | Все запуски во время отключения collector | «Запуск замечен 12 октября» |
| LastAccessTime | Сохранённая файловой системой метка при известной policy | Последнее осознанное открытие человеком | Дополнительный фильтр, явно слабый сигнал |
| LastWriteTime / USN | Изменение с данной меткой/после checkpoint | Чтение/просмотр или отсутствие потребности | «Не менялось 180 дней», не «не нужно» |
| App uninstaller/package registration | Установленная/зарегистрированная программа данного scope | Наличие всех portable/launcher apps | Факт inventory с source/scope |
| Отсутствие app registration | Не найдена запись в доступных источниках | Что приложение удалено или папка бесхозна | «Возможный остаток, основание…» |
| Explicit user statement «проект архивный» | Предпочтение данного пользователя | Авторазрешение на любое удаление | Архивация/перенос в подходящем плане |

**Opt-in collector design.** Переключатель «Учитывать использование приложений с этого момента» выключен по умолчанию. Отдельный ordinary-user session collector: только foreground changes, user/session boundaries, idle/lock/suspend, app ID, агрегированная длительность; без window titles, keystrokes, screenshots, URL и command line. Фоновые службы не признаются неиспользуемыми из-за отсутствия foreground. Опциональная подписка process-start после отдельного permission spike; иначе редкие snapshots с явным sampling gap. Default история 30 дней, настраиваемая 7/30/90; остановка/удаление истории одним действием. Сроки — предложение продукта, не API requirement. Отмечать collector uptime, sessions missing, suspension, drops, permission denied, clock jumps. Не реконструировать время до opt-in. Показать «за 21 наблюдавшийся день из 30 запусков не замечено», а не «не запускалось месяц».

**Ретроспективные artefacts.** Не строить v1 на парсинге Prefetch/Amcache/UserAssist/BAM/SRUM и условном поле LastRun. Windows не предоставляет найденного в этом исследовании универсального стабильного «последнего использования всех программ». Forensic enrichment позже допустим как отдельный versioned адаптер, read-only, opt-in, с provenance, unknown при недоступности/изменении формата. Присутствие записи может быть полезным контекстом; отсутствие записи ничего не разрешает удалить. Не включать новые audit policies, не обходить ACL и не требовать постоянного admin ради usage timeline. Полный kernel file-I/O monitoring/driver не нужен первому релизу: это отдельная стоимость privacy, performance и поддержки.

**Coverage semantics.** `ObservedActive`, `ObservedBackground`, `NoActivityObservedWithinCoveredWindow`, `UnknownHistory`, `InsufficientCoverage`, `ConflictingEvidence`. Это категории наблюдения, не проценты истинности. Confidence отдельно `DirectEvidence / Corroborated / Heuristic / Unknown`, с перечнем supporting/contradicting facts. Числа вроде «97% не нужна» без калиброванного набора размеченных данных запрещены как выдуманная точность. Приоритет карточек может учитывать размер/рост/coverage, но eligibility удаления определяется action policy независимо от confidence.

### Gaps

Не проверялись права WMI/ETW collector на всех редакциях и cross-user sessions. Не найден стабильный Microsoft API полной истории использования всех desktop/portable apps. В исследовании нет измеренной точности forensic parsers или ML-классификации. Веб-источники не дают права обещать универсальную реконструкцию истории.

## Как привязать пространство к программам и полезным действиям

### Takeaway

Мощная аналитика должна строить evidence graph программ, пакетов, каталогов, проектов, caches и процессов. Тогда она предлагает не только temp cleanup, но и нормальный uninstall тяжёлых приложений/игр, перенос архивных материалов, управление regenerable caches и supported Windows servicing.

### Cited Findings

- Microsoft предупреждает: универсально найти все приложения нельзя; portable apps не обязательно зарегистрированы. `Win32_Product` имеет side effects: consistency checks/repair, поэтому не годится для тихого регулярного inventory. [Working with software installations](https://learn.microsoft.com/en-us/powershell/scripting/samples/working-with-software-installations?view=powershell-7.6), доступ 2026-10-23.
- Uninstall registry содержит product/display/publisher/install location/uninstall сведения; InstallDate для MSI может означать последнее обслуживание, а не первоначальную установку. [Uninstall key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key), доступ 2026-10-23.
- Windows имеет раздельные 32/64-bit registry views, доступные явными флагами. [Registry views](https://learn.microsoft.com/en-us/windows/win32/sysinfo/32-bit-and-64-bit-application-data-in-the-registry), доступ 2026-10-23.
- PackageManager.FindPackagesForUser получает packages конкретного пользователя, пустой SID означает текущего. Это отдельный источник от classic uninstall registry. [Package API](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser?view=winrt-26100), доступ 2026-10-23.
- WinGet list показывает и приложения, установленные иными способами; uninstall требует однозначного выбора, поддерживает id/product-code/version/scope фильтры. Это обёртка поддерживаемой деинсталляции, не разрешение вычищать installation tree вручную. [list](https://learn.microsoft.com/en-us/windows/package-manager/winget/list), [uninstall](https://learn.microsoft.com/en-us/windows/package-manager/winget/uninstall), доступ 2026-10-23.
- Restart Manager `RmRegisterResources` + `RmGetList` выявляет приложения/службы, использующие зарегистрированные ресурсы. Это положительная занятость конкретных ресурсов, не полная будущая dependency map. [Register](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmregisterresources), [Get list](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist), доступ 2026-10-23.
- Steamworks имеет GetAppInstallDir; документация отдельно предупреждает, что функция возвращает ожидаемую папку даже для неустановленной игры. Steamworks API не следует выдавать за доказанный unrestricted API всего library account пользователя. [ISteamApps](https://partner.steamgames.com/doc/api/isteamapps), доступ 2026-10-23.
- Windows Settings имеет документированный URI launch surface для apps/storage/startup pages, который можно открыть из desktop app. [Settings URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings), доступ 2026-10-23.

### Inferences

**Inventory pipeline.** Собирать HKLM/HKCU Uninstall через соответствующие 32/64 views; current-user MSIX registration; известные vendor manifests через versioned adapters; пользовательские явно выбранные portable roots; shortcuts и обнаруженные executable metadata как tentative evidence. Не запускать найденные executables для определения назначения. Объединять по product code/package family/vendor IDs/signature/path identity, а не по совпадению display name. Scope и source сохраняются; недоступный профиль другого пользователя — неизвестен. EstimatedSize не считать фактическими allocated bytes. Размер app вычислять из связанной storage graph без double-count shared libraries/hardlinks.

**Ownership graph.** Nodes `Application`, `Package`, `Executable`, `StorageObject`, `Category`, `Project`, `UserPreference`, `ObservationWindow`. Edges `InstalledBy`, `LocatedUnder`, `HasManifest`, `SignedBy`, `CurrentlyRunning`, `ObservedForeground`, `OpenedBy`, `RegenerableFor`, `SharedWith`, `ContainsSaveData`, `ProjectDependency`, `PossiblyLeftBy`. Каждое ребро имеет evidence source, observedAt, validUntil/recheck requirement, confidence и counterevidence. Пример: `Directory → PossiblyLeftBy(AppX)` слабее `Directory → ExactCacheRule(AppX,version)`; directory name alone не даёт ни uninstall, ни delete.

**Проекты.** Пользователь помечает roots как active/archive; local manifest parsers распознают package/build metadata и documented cache locations. Generated artifacts отделяются от source/assets/lockfiles/licenses/secrets; наличие `node_modules`, build output или `.git` само по себе не разрешает удаления. Восстановимость требует доступных lockfiles/manifests/tool versions/источников dependencies, офлайн-ограничения учитываются. Rebuild time, redownload bytes и excluded user paths показываются до выполнения. Разрешение проекта относится к его scope, а не всем каталогам с таким названием.

**Программы/игры.** Карточка «занимает 78 ГБ, 0 запусков замечено при coverage 29/30 дней, настройки и saves ещё 2 ГБ»: действия «Оставить», «Открыть launcher», «Удалить штатно», «Посмотреть данные». Сам uninstaller запускается только после выбора точной app identity; строка UninstallString не передаётся blindly в shell с admin, parser/target/trust validation и OS handoff либо native package APIs. Сначала подтверждённое завершение uninstall и повторный inventory, затем отдельная карточка возможных остатков. Shared vendor folder не удаляется целиком; saves/settings/default backups выделяются и сохраняются отдельно. Результат WinGet/installer и его exit code не подменяют проверку фактического состояния; reboot required/failed/partial различаются.

**Занятость.** Live processes/image metadata и targeted Restart Manager — быстрое объяснение «файл занят». Отсутствие handles в snapshot не значит «safe delete»; полного системного handle enumeration через недокументированные структуры для v1 не требуется. Открытый файл может принадлежать общему runtime, а процесс может завершиться/сменить PID, поэтому фиксировать process creation time и revalidate перед действием. Без force-close handles/process kill by default; пользователь видит, какое приложение нужно закрыть.

**Пример глубокого плана (все числа демонстрационные).** Цель 40 ГБ: 24 ГБ штатный uninstall выбранной игры с сохранением saves, 11 ГБ перенос подтверждённого пользователем архивного проекта на D, 8 ГБ versioned caches с повторным скачиванием, 3 ГБ supported Windows servicing estimate. План показывает overlapping storage, диапазон экономии, target-volume capacity, rollback/reinstall cost; предлагает уменьшить набор до достаточного объёма. Вариант «оставить игру, перенести медиа» равноправен. ML объясняет и ранжирует варианты, но executor получает только детерминированно допустимые typed operations.

### Gaps

Не подтверждены стабильные локальные manifest API всех launchers и portable managers. Steam/Epic/Xbox/Battle.net adapters должны пройти каждый собственные fixtures и license/API review; нельзя обещать поддержку всех по одному документу. Граф ownership — проектное предложение, не универсальный oracle зависимостей.

## Каким должен быть сильный первый релиз

### Takeaway

Первый коммерчески содержательный milestone включает фактическое освобождение пространства в нескольких независимых классах: приложения/игры, системное обслуживание, подтверждённые caches/build artifacts и крупные пользовательские материалы с переносом/архивацией. Параллельно отдельный контур измеряет причины нагрузки и startup impact; «оптимизация» должна означать измеряемое изменение, а не рейтинг здоровья.

### Cited Findings

- Microsoft запрещает ручное удаление WinSxS и описывает supported component cleanup; DISM StartComponentCleanup может удалять старые компоненты без 30-дневного ожидания scheduled task. Это отдельное действие с последствиями, не обычное удаление найденных файлов. [WinSxS cleanup](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11), доступ 2026-10-23.
- `compact` предоставляет NTFS compression и CompactOS, включая executable-oriented algorithms. Это освобождение allocated storage через компрессию, не удаление данных и не гарантированный speedup. [compact](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact), доступ 2026-10-23.
- Microsoft указывает, что startup applications могут влиять на startup/system performance; Task Manager показывает startup impact. [Startup apps](https://support.microsoft.com/en-gb/windows/experience/startup-boot/configure-startup-applications-in-windows), доступ 2026-10-23.
- WPR имеет CPU, disk/file I/O, memory, networking и power profiles — подходящие инструменты для измерений ресурсоёмкости Cleaner и изменений после обслуживания. [WPR profiles](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-resource-based-analysis), доступ 2026-10-23.

### Inferences

**Production modules, additions to .NET/WPF architecture.**

| Модуль | Основной контракт | Что отдаёт |
|---|---|---|
| Inventory | `EnumerateApplicationsAsync(scope)` | AppIdentity + SourceEvidence + coverage |
| Ownership | `ResolveOwners(nodes, inventory, rules)` | EvidenceEdges, shared/unknown flags |
| Usage | `ObserveSession(consent)` / `QueryUsage(app, window)` | observed events + covered intervals, не unused bool |
| Projects | `AnalyzeProject(root, adapterVersion)` | source/generated/user-assets/rebuild prerequisites |
| DeepCategories | `Discover(category, snapshot)` | findings + exact eligibility rule + consequences |
| NativeUninstall | `Prepare(appIdentity)` / `ExecuteApproved(planItem)` | exact target, native handoff/result, re-inventory |
| SystemMaintenance | `AnalyzeSupportedOperation(id)` / `RunApproved(id)` | servicing estimate/result, required rights/reboot |
| Optimization | `CaptureBaseline(scenario)` / `CompareRuns()` | resource/latency metrics and uncertainty |
| Planner | `BuildGoalPlan(goal, allowedActions, exclusions)` | alternative typed plans and shared-space accounting |
| ModelAdvisor | `ExplainAndRank(evidenceDto)` | schema-constrained explanations; no executor permission |

**Schema extensions.** `Applications(appId, identityKind, productCode?, packageFamily?, vendorId?, scope, version, installState, source)`; `Evidence(id, kind, subject, observedUtc, sourceVersion, rawReference?, strength, expiresUtc, coverageId, contradictionIds)`; `EvidenceEdges(fromId,toId,relation,evidenceId)`; `UsageWindows(id,consentId,startUtc,endUtc,observedIntervals,missingReasons)`; `UsageAggregates(appId,windowId,foregroundSeconds,backgroundObservedSeconds,observedLaunchCount,lastObservedUtc)`; `Projects(id,rootIdentity,userState,adapterVersion,rebuildPrerequisites)`; `MaintenanceCapabilities(id,osConstraint,actionVersion,previewSupport,rights,recoverySemantics)`; `Measurements(scenario,device,build,preState,postState,metrics,noiseEstimate)`. Nullable means unknown, never zero. App/usage facts stored separately from execution policy and personal-data classification.

**Сильный M1: ориентир 12–18 недель после утверждённых scanner/policy foundations, командой 3–4 инженера + QA (оценка, не гарантия).**

1. Карта allocated/logical пространства, app inventory и attributable/shared/unknown breakdown; фильтры крупных старых файлов и архивных проектов; deterministic evidence explanations.
2. Несколько хорошо проверенных browser/application cache adapters, crash reports, supported regenerable build caches для конкретных toolchains; версии и fixtures опубликованы в category catalog. Credentials/sessions/saves отдельно.
3. «Удалить штатно» для classic/MSIX приложений через approved OS/vendor path, 1 launcher integration; повторный scan и leftover review с ownership evidence. Это реально удаляет десятки ГБ там, где есть выбранная тяжёлая программа, без выдуманного автоматического «не нужна».
4. Windows component servicing через штатный механизм после анализа, без произвольной чистки protected trees; общий preview с ожидаемым объёмом и последствиями.
5. Перенос/архивация явно выбранных больших материалов с проверкой target capacity/integrity и сохранением source до проверки переноса. Перенос на тот же volume не записывать как освобождённое место.
6. Action journal, actual free-space delta с пометкой concurrent activity, partial/reboot/error statuses, restore options там, где действительно доступны. Опциональный local assistant в интерфейсе объясняет тот же typed plan.

Дальше M2: prospective usage collector и 30-дневные наблюдения, больше launcher/project adapters, growth attribution, confirmed duplicates, custom rules с sandbox/validation, расписание только разрешённых категорий. M3: measurement-driven startup/background optimization, продвинутые relocation/compression workflows и более широкий enterprise support. Карантин/дубликаты/компрессия не обязаны блокировать мощный M1; устанавливаемые программы, game ownership и system servicing уже выводят продукт за пределы temp cleanup. Предыдущая оценка 6–9 месяцев полного первого релиза с maturity/QA остаётся планировочным ориентиром; 12–18 недель — отдельный ограниченный functional milestone, не тот же объём.

**Оптимизация системы.** Показывать top consumers по sampled CPU/I/O и startup entry source, открывать штатные настройки, менять только выбранные reversible documented entries с точным backup. Не объявлять каждый background service лишним, не отключать защиту/updates, не чистить registry ради «скорости». Compression после benchmark sample и оценки CPU/read penalty; portable app relocation только при явно поддержанном механизме. Оптимизация может сохранить данные, ограничить/перенастроить кэш или переносить workload вместо удаления. Windows TRIM/SSD health/optimization — отдельная проверка состояния и поддерживаемое обслуживание, не обещание ускорения удалением истории браузера.

**Бенчмарки силы, а не числа поддержанных checkboxes.** Corpus: обычный ПК; игровой ПК с 2 launchers; рабочая станция с 3 projects; low-storage laptop; обновлённая Windows VM; cases portable/shared packages/unsupported roots. В каждом заранее размечены install/user/save/generated/shared объекты и реально допустимые действия. Метрики: (1) eligible bytes correctly discovered / ground truth eligible bytes, (2) incorrect eligible bytes и особенно personal/protected false positives, (3) reclaim forecast vs measured allocated/free delta, (4) net reclaim после redownload/rebuild, (5) ownership precision/unknown rate, (6) proposal acceptance и abort/restore rate, (7) action success/partial/error/reboot counts, (8) UI time-to-first-result, cancellation latency, CPU-seconds, peak RAM, disk reads/writes, battery/power. Нельзя оптимизировать recall ценой ложного удаления. Для production gate любое personal/protected false eligibility в known fixture — failure.

Power study proposal: identical AC/battery setting, model off/on отдельно, idle 30-minute baseline vs collector-on, repeated scans cold/warm, several repeats (например 5), median/p95, фон Windows Update/Defender учитывается. WPR traces/available battery telemetry, without claiming watt accuracy where hardware doesn't provide it. No blanket 2–3× scan speed claim without measured comparison. Startup scenario and app-launch time pre/post при одном и том же workload, показать отсутствие улучшения, если различие в пределах noise. Goal — удалённые байты + подтверждённая пригодность данных/обслуживания + затраты пользователя на восстановление, не искусственные проценты «health».

### Gaps

Показанные контракты/схема/сроки — рекомендации исследования, код не менялся. Нет измеренной reclaim/ownership/model accuracy, battery overhead и истинного «unused data» oracle. Baseline corpora, supported category list и success gates надо согласовать до разработки: иначе мощность нельзя проверить. Реальный объём освобождения определяется наполнением машины, обещание конкретных ГБ для каждого пользователя необоснованно.
