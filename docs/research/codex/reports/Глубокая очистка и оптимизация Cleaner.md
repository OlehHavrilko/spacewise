# Глубокая очистка и оптимизация Cleaner

**Целевой Cleaner должен быть полноценным мощным очистителем: широкий каталог приложений и системных категорий, глубокий поиск, пакетные действия, пользовательские профили и реальная аналитика использования.** Ограничение одним `%TEMP%` не отвечает уточнённой задаче. Предыдущая архитектура сохраняется как основа проверяемого исполнения, но продуктовый объём расширяется: обслуживание Windows, browser/app caches и privacy, developer toolchains, Docker/WSL, игры, штатная деинсталляция, перенос и сжатие данных. Пользователь утверждает категории, последствия и ограничения одним планом или заранее настраивает расписание; отдельное подтверждение каждого файла не требуется. «Не использовалось» означает наблюдение с известным периодом и охватом, а не вывод из возраста файла. Оптимизация измеряет конкретный эффект — доступное пространство, время запуска, IO или фоновые ресурсы — и не обещает универсального FPS boost. **Все новые возможности здесь являются требованиями и планом реализации: существующий starter по-прежнему анализирует метаданные и не очищает файлы.**

Дополнение к `D:/Projects/codex/cleaner/reports/Архитектура и сборка Cleaner.md`. Дата исследования: **23 октября 2026 года**. Три новые исследовательские записки находятся в `D:/Projects/codex/cleaner/research_notes/Глубокая очистка и оптимизация Cleaner/`. Предлагаемые схемы и критерии ниже — инженерные решения; внешние факты подтверждаются первичными источниками. Испытания разрушительных операций и сравнительный benchmark готовых очистителей на компьютере пользователя не проводились.

## 1. Сила BleachBit задаёт каталог действий, а не разрешение удалять всё

### Мощность строится на знании приложений

BleachBit полезен широтой предметных правил и исполнителей. CleanerML описывает обнаружение приложений, paths/globs, общие глубокие обходы, исключения и действия, выходящие далеко за удаление временных файлов: truncation, изменения известных JSON/INI, SQLite vacuum, registry и специальные операции. Исходники разделяют providers действий, команды, preview, worker и общий DeepScan. Это подходящая модель для Cleaner: общий discovery pass и расширяемый набор предметных adapters. Исследован текущий исходный код, а не выполнен security audit релиза BleachBit. ([CleanerML](https://docs.bleachbit.org/cml/cleanerml/), [Action.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Action.py), [Command.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Command.py), [DeepScan.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/DeepScan.py))

У BleachBit пользователь выбирает опции, закрывает обслуживаемые приложения, смотрит preview и запускает очистку. Следовательно, безопасность не требует сотен диалогов. Cleaner должен предлагать такой же цельный сценарий: выбрать профиль «Глубокая очистка», раскрыть категории при необходимости, увидеть суммарные последствия и исполнить один план. Разница между известным кэшем и неизвестной личной папкой сохраняется внутри политики и понятного UI. ([BleachBit usage](https://docs.bleachbit.org/doc/general-usage/))

| Требование после уточнения | Реализация в продукте | Что пользователь получает |
|---|---|---|
| Глубина уровня полноценного cleaner | Общий deep discovery, versioned app adapters, специализированные операции | Находки за пределами temp и простого списка крупных файлов |
| Массовая очистка | Один preview выбранных категорий, раскрываемые детали | Одно осмысленное решение на план |
| Реальное использование | Inventory + usage collector + coverage + ownership graph | Различие «запускался», «работал в фоне», «история неизвестна» |
| Оптимизация пространства | Uninstall, перенос, dehydration, compression, virtual disks | Альтернативы удалению с реальным per-volume эффектом |
| Оптимизация системы | Storage maintenance, автозагрузка, фон с измерениями | Проверяемые действия вместо условного рейтинга здоровья |
| Настраиваемая мощность | Профили, custom rules, доверенные packs, расписание | Широкие собственные правила без shell-доступа у модели |

### Первый релиз должен включать несколько больших семейств

Предлагается выпускать первый публичный cleanup-релиз с законченными вертикальными срезами: приложения/браузеры, Windows servicing, developer caches, Docker build cache, приложения/игры и usage analytics. WSL/VHD compaction и расширенные конфигурационные изменения требуют отдельного adapter spike и могут выйти следующим этапом. Это сильнее temp-only MVP: несколько категорий способны решить задачу нехватки пространства на конкретной машине, хотя на чистой системе экономия закономерно может быть нулевой.

| Семейство | Первые полезные операции | Последствия и границы |
|---|---|---|
| Browser и desktop apps | Disk/GPU caches, известные logs/crash data, отдельные privacy options | Повторная загрузка/компиляция; cookies, сеансы и история выбираются раздельно |
| Windows | Component store analysis/cleanup, корзина, Windows.old через штатный flow | Утрата содержимого корзины и rollback раскрывается отдельно |
| NuGet/npm/pip | Native inventory и granular cache maintenance | Следующий restore/download медленнее; офлайн-сборка может потерять зависимости |
| Docker | Инвентаризация и prune выбранного build cache | Проверенный local context; daemon bytes не равны host-volume bytes |
| Игры и программы | Точная inventory, штатный uninstall, launcher-supported move | Saves/mods/settings выделяются; данные не удаляются по имени vendor folder |
| Проекты и медиа | User-selected archive/move, известные regenerable outputs | Source/assets/lockfiles/secrets сохраняются; цена rebuild показана |
| Облако | Supported «Free up space» после sync checks | Локальная копия становится online-only; Delete имеет другую семантику |
| Storage maintenance | Analyze/retrim/поддержанная оптимизация тома | Это обслуживание, не «найдено ещё 20 GB мусора» |
| Следующий этап | WSL/VHD shrink, app SQLite vacuum, selective settings edits | Требуются версии, backup, shutdown и recovery fixtures |

Прямое удаление WinSxS, Installer, DriverStore и installation tree не заменяет поддержанную операцию. Windows component store обслуживается DISM; Windows.old удаляется штатным механизмом с потерей возврата к предыдущей Windows. `StartComponentCleanup /ResetBase` имеет более сильное необратимое последствие — невозможность удалить существующие обновления — и не включается скрыто в обычную очистку. ([WinSxS maintenance](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder), [DISM servicing](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options), [Windows.old](https://support.microsoft.com/en-us/windows/deployment/install-upgrade/delete-your-previous-version-of-windows))

NuGet позволяет узнать реальные cache locations и очищать отдельные caches; global-packages участвует в сборках. npm cache рассчитан на самопроверку и восстановление: очистка не должна рекламироваться как обязательное исправление; важны назначение и повторная загрузка. pip cache зависит от выбранного interpreter/user context и не является uninstall окружения. Не надо запускать найденные project scripts для «очистки сборки»: рабочая папка и tool configuration тоже могут содержать исполняемое поведение. ([NuGet caches](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders), [dotnet nuget locals](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-locals), [npm cache](https://docs.npmjs.com/cli/v11/commands/npm-cache/), [pip cache](https://pip.pypa.io/en/stable/cli/pip_cache/))

## 2. Пакеты правил расширяют возможности через доверенные исполнители

### Data pack задаёт предметную область, adapter реализует действие

Нужно отделить каталог от кода исполнения. Правило знает supported app versions, способ обнаружения профилей, категорию, selectors, keep lists, последствия и preconditions. Adapter знает, как корректно выполнить `DeleteKnownCache`, `TruncateKnownLog`, `VacuumKnownSqlite`, `ClearPackageCache`, `PruneDockerBuildCache`, `ServiceWindowsComponents`, `UninstallRegisteredProduct` или `OptimizeVolume`. Универсальный `process arbitrary-command` в data pack разрушает эту границу. При этом встроенный проверенный adapter вправе запускать конкретный native CLI с фиксированной схемой аргументов и проверенным абсолютным путём.

```text
ICleanerAdapter.Discover(context) -> Findings + Evidence
ICleanerAdapter.Estimate(finding, policy) -> Estimate + Effects + Quality
ICleanerAdapter.Prepare(planItem) -> PreparedOperation + ScopeContract
ICleanerAdapter.Execute(prepared, authorization) -> NativeOutcome
ICleanerAdapter.Verify(outcome) -> VerifiedResult | Partial | NeedsRecheck
```

Пример **проектной схемы**, не уже существующего rules engine:

```json
{
  "packageId": "cleaner.developer",
  "version": "1.0.0",
  "engineSchema": 1,
  "adapterId": "DockerBuildCache",
  "supportedVersions": "validated-range",
  "scopeBinding": "PolicyBound",
  "effectClass": "RebuildAndRedownload",
  "privilege": "CurrentUser",
  "preconditions": ["LocalContext", "NoActiveBuild", "KnownDaemonIdentity"],
  "limits": {"maxEstimatedBytes": "21474836480", "maxDurationMinutes": 20},
  "automationEligible": true,
  "sources": ["https://docs.docker.com/build/cache/garbage-collection/"],
  "fixtureSet": "docker-build-cache-v1",
  "sourceRevision": "pinned-at-release",
  "licenseId": "project-selected-license"
}
```

Manifest также содержит maintainer, action capabilities, recovery contract, tests, signature, minimum engine version, provenance и support expiry. Подписанный pack обновляется отдельно, может быть отозван или откатан; новые категории и более сильные права не включаются старым расписанием автоматически. Unknown app version переходит в analysis-only или требует нового проверенного адаптера. Подпись доказывает происхождение, а не правильность правила; fixtures и staged rollout всё равно обязательны.

Глубокие selectors компилируются в общий discovery plan: по одному проходу выбранной области для нескольких правил, с bounds, regex timeout и бюджетом IO. Пользовательские правила могут задавать реальные roots, glob, age/retention, keep lists, ограничения объёма и известные действия. Они не получают elevation только потому, что написаны пользователем. Кодовые расширения — отдельный явно устанавливаемый trusted plugin tier. Импорт CleanerML/Winapp2 — компиляция поддерживаемого subset в собственный IR с lint/preview; неизвестный action отклоняется, а не превращается в shell fallback.

### Настройка категорий позволяет работать без постоянных вопросов

Профили: «Обслуживание», «Глубокое освобождение места», «Разработчик», «Игровой компьютер», «Приватность», «После удаления программ», «Обслуживание накопителей». Каждый профиль хранит categories, retention, exclusions, resource budgets, app-state policy и permissions. Человек может разрешить очистку известных browser caches или Docker build cache по расписанию и задать предел за запуск. Это полноценная автоматизация, а не режим, спрашивающий разрешение на каждый файл.

Действия с потерей пользовательских данных, rollback или volume contents требуют особенно ясного выбора scope. Заранее настроенное полномочие может охватывать конкретную категорию и объекты, но не автоматически расширяться после обновления pack. Нельзя одновременно обещать запуск глобального native prune и точный неизменяемый список каждого файла: adapters публикуют `SnapshotBound` либо `PolicyBound`. В первом случае разрешён конкретный snapshot IDs; во втором — известные predicates, limits и проверенный context, с повторным preflight перед запуском.

Busy-app policy выбирается на уровне профиля: пропустить, ждать idle, мягко закрыть выбранные приложения или работать в maintenance window. Restart Manager помогает определить использующие ресурсы программы, но не доказывает отсутствие несохранённых данных. Force kill не является незаметной частью очистки. ([Restart Manager](https://learn.microsoft.com/en-us/windows/win32/rstmgr/about-restart-manager))

### Расширяемость требует явного лицензионного решения

Рекомендуемый базовый путь — собственные adapters и определения по документации приложений с сохранённым provenance. BleachBit engine распространяется под GPL; reuse кода и правил требует соблюдения условий конкретных файлов и ревизий. Winapp2 имеет собственную лицензию, включая ShareAlike для соответствующего материала. Перевод чужого XML в JSON не устраняет происхождение; отдельный subprocess сам по себе не снимает обязательств. Перед distribution нужны pinned license inventory, notices и проверка выбранной интеграции. Это карта инженерных решений, а не юридическое заключение. ([BleachBit COPYING](https://raw.githubusercontent.com/bleachbit/bleachbit/master/COPYING), [Winapp2 license](https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/License.md), [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/))

## 3. Аналитика использования строится на доказательствах и периоде наблюдения

### Возраст файла не показывает его ненужность

`LastAccessTime` зависит от политики обновления и файловой системы, может обновляться с задержкой и может быть изменён отдельно. USN фиксирует изменения, а не универсальную историю чтения. Поэтому «не менялось 180 дней» допустимо, а «не используется 180 дней» без дополнительного источника — недоказанный вывод. Cleaner сначала узнаёт состояние last-access policy и не меняет её незаметно ради своей аналитики. ([File times](https://learn.microsoft.com/en-us/windows/win32/sysinfo/file-times), [fsutil behavior](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior), [USN records](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records))

Нужны три слоя. Inventory определяет известные приложения, packages, launcher libraries и выбранные portable roots. Собственный opt-in collector с момента установки наблюдает процессы и foreground periods с учётом idle и gaps. Дополнительные read-only исторические источники дают ограниченный контекст с provenance, но не полную реконструкцию жизни ПК. Amcache, например, относится к evidence of presence и сам по себе не доказывает execution. ([Microsoft Incident Response guide](https://www.microsoft.com/en-us/security/blog/2024/04/23/new-microsoft-incident-response-guide-helps-simplify-cyberthreat-investigations/), [QueryFullProcessImageName](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew), [WinEvent hook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook), [Idle API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo))

| Статус наблюдения | Что известно | Корректная формулировка |
|---|---|---|
| `ObservedActive` | Наблюдалась foreground activity | «Использовалось в наблюдаемом периоде» |
| `ObservedBackground` | Процесс работал, foreground не отмечен | «Работало в фоне» |
| `NoActivityObservedWithinCoveredWindow` | Источники работали, событий не найдено | «Запуски не замечены за охваченный период» |
| `UnknownHistory` | Нет подходящей истории | «Использование неизвестно» |
| `InsufficientCoverage` | Collector пропускал существенные интервалы | «Данных недостаточно» |
| `ConflictingEvidence` | Источники расходятся | «Нужна проверка» |

Confidence отдельный: `DirectEvidence`, `Corroborated`, `Heuristic`, `Unknown`. Цифра «97% не нужна» без калиброванной разметки выдумывает точность. Даже надёжное отсутствие наблюдавшихся запусков не означает разрешение удалить приложение или данные. Collector хранит observation window, uptime coverage, источник и причину gaps. Process identity включает creation time, а не только переиспользуемый PID.

### Граф связывает данные с владельцем и возможным действием

Inventory читает HKCU/HKLM Uninstall с учётом 32/64-bit views, current-user MSIX, известные manifests и выбранные пользователем portable roots. `Win32_Product` не подходит для тихого регулярного inventory из-за consistency checks и возможного repair. `EstimatedSize` не равен фактическому allocated storage; MSI InstallDate может отражать последнее обслуживание. ([Software inventory](https://learn.microsoft.com/en-us/powershell/scripting/samples/working-with-software-installations?view=powershell-7.6), [Uninstall key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key), [Registry views](https://learn.microsoft.com/en-us/windows/win32/sysinfo/32-bit-and-64-bit-application-data-in-the-registry), [MSIX inventory](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser?view=winrt-26100))

Проектный evidence graph содержит `Application`, `Package`, `Executable`, `StorageObject`, `Project`, `Category`, `ObservationWindow`, `UserPreference`. Рёбра `InstalledBy`, `HasManifest`, `ObservedForeground`, `RegenerableFor`, `SharedWith`, `ContainsSaveData`, `ProjectDependency`, `PossiblyLeftBy` имеют source, observedAt, recheck requirement и counterevidence. Название каталога даёт более слабую связь, чем точное правило кэша подтверждённой версии. Shared vendor directory не превращается в «остаток» после удаления одной программы.

```text
UsageObservation(appIdentity, kind, start, end, source, sessionScope)
CoverageWindow(source, from, to, coveredSeconds, gaps[], retention)
OwnershipEdge(fromId, relation, toId, evidenceId, confidence, validUntil)
ActionCandidate(adapterId, scope, evidenceIds[], effects[], eligibility)
```

Карточка игры показывает размер installation и связанных данных, наблюдавшуюся активность и coverage, а затем «Оставить», «Открыть launcher», «Перенести», «Удалить штатно». WinGet может помогать exact-target uninstall, но vendor dialogs/reboot и последующая проверка сохраняются. Сначала подтверждается uninstall и повторный inventory, потом отдельно анализируются возможные остатки. Steamworks API нельзя выдавать за универсальный открытый доступ ко всем библиотекам пользователя; launcher adapters требуют собственных fixtures. ([WinGet uninstall](https://learn.microsoft.com/en-us/windows/package-manager/winget/uninstall), [ISteamApps](https://partner.steamgames.com/doc/api/isteamapps))

Для проектов пользователь отмечает active/archive roots. Parsers распознают manifests и generated outputs; sources, assets, lockfiles, secrets и `.git` не смешиваются с cache. Наличие `node_modules` не доказывает ненужность: offline/private dependencies и дорогой rebuild меняют цену решения. Privacy по умолчанию локальная: collector opt-in, без содержимого документов, текстов окон и keystrokes; retention и удаление истории доступны в настройках. Модель объясняет граф, но не создаёт факт владения или разрешение на удаление.

### Игровая установка защищается как связанная единица

**`SteamLibrary/steamapps/common/<game>` — целостная installation unit, а не набор независимых файлов, которые можно разбирать по возрасту, размеру или слову `cache`.** Внутри находятся assets, texture packs, shaders, executable dependencies и данные, назначение которых известно игре. Файл `temp_texture.cache` может быть обязательным ресурсом. Общая эвристика «старый», «крупный» или «похож на кэш» не должна давать право удалять такой объект. Это нормативное требование будущего Cleaner; текущий metadata-only scanner ещё не умеет устанавливать такое владение, но и не имеет операций записи.

Предлагается иерархия `Library → InstallUnit → ResourceGroup → File`. Library — конкретная библиотека launcher, InstallUnit — приложение или игра с identity и состоянием, ResourceGroup — installation assets, verified rebuildable cache, DLC, mods, workshop, saves, settings или shared runtime. Связи зависимостей many-to-many: redistributable, runtime или shared asset может обслуживать несколько install units и храниться вне одного дерева. Saves и userdata также бывают за пределами install directory. Поэтому protection строится по ownership graph, а не только по префиксу папки.

```json
{
  "schemaVersion": 1,
  "libraryId": "steam-library-local-1",
  "installUnit": {
    "provider": "Steam",
    "appId": "example-id",
    "installRootIdentity": "volume-and-directory-identity",
    "state": "Installed",
    "protection": "DenyGenericDescendantMutation"
  },
  "resourceGroup": {
    "kind": "InstalledAssets",
    "owners": ["example-id"],
    "sharedWith": [],
    "evidenceIds": ["manifest-1", "validated-discovery-1"],
    "allowedActions": ["LauncherMoveUnit", "LauncherUninstallUnit"]
  },
  "subsetOverride": {
    "default": "Deny",
    "requires": ["TrustedAdapter", "ValidatedGameVersion", "ExactCacheSemantics",
                 "RebuildCostDisclosed", "NoDependencyConflict", "RecheckBeforeRun"]
  }
}
```

Это проектная схема, не готовый Steam adapter. Состояния unit: `Installed`, `UninstalledConfirmed`, `Updating`, `Running`, `Unknown`. Для updating/running связанные меняющие действия откладываются по контракту операции. Unknown ownership и конфликт источников блокируют generic deletion, но не анализ, отображение объёма и предложение штатного launcher flow. Правило родителя наследуется вниз: глобальное temp/deep scan правило не обходит `DenyGenericDescendantMutation`. Полномочие на один ResourceGroup не распространяется на соседние группы и shared dependencies.

Manifest подтверждает определённый факт о регистрации и установке, но **не доказывает ненужность каждого файла** и не гарантирует полноту стороннего контента. Steamworks `GetAppInstallDir` документированно может вернуть ожидаемую папку даже для неустановленной игры. Отсутствие manifest, устаревший inventory или временно недоступная библиотека не означают «остатки можно удалить». Для `UninstalledConfirmed` нужны согласованные evidence завершённого uninstall, повторный inventory и проверка затронутых owners; mods, saves, workshop, custom assets и неизвестные файлы сохраняют собственную protection. Полнота launchers и их форматов проверяется отдельно. ([Steamworks ISteamApps](https://partner.steamgames.com/doc/api/isteamapps))

Мощная игровая очистка при этом остаётся возможной. Проверенный version-aware adapter может обслужить конкретный shader/download cache, если доказаны назначение, принадлежность, отсутствие shared/user-content конфликта и допустимость восстановления пересборкой или повторной загрузкой. Стоимость первого запуска и shader recompilation видна в preview. Нельзя разрешить subset только по имени «cache». Для целой игры предпочтительны launcher-native move/uninstall с выбором unit, поддержанным сохранением данных и последующим inventory. Steam предоставляет собственный flow переноса installation; конкретный callable interface и интеграцию Cleaner ещё предстоит проверить. ([Steam storage/move](https://help.steampowered.com/en/faqs/view/4BD4-4528-6B2E-8327))

| Обязательный сценарий | Ожидаемое решение политики |
|---|---|
| Установленная игра содержит старый `temp_texture.cache` | Generic temp/age/large rule запрещено; файл остаётся частью InstalledAssets |
| Redistributable принадлежит двум играм, одна удаляется | Shared dependency сохраняется; отсутствие одного owner не разрешает удаление |
| Проверенный shader cache поддержанной версии | Узкий adapter разрешён при preconditions; rebuild/redownload cost показан |
| Игра запущена или обновляется после preview | План перепроверяется и откладывается; не force-delete под работающим launcher |
| Manifest устарел или отсутствует, в папке mods/saves | Статус Unknown/ConflictingEvidence; данные сохранены, возможные остатки требуют новых evidence |

InstallUnit является **атомарной группой политики**, а не обещанием атомарной файловой транзакции. Launcher uninstall, move, несколько cache операций и power loss не дают универсального rollback. План сохраняет per-operation outcomes, `Partial` и `NeedsReconciliation`; нарушение целостности после частичного выполнения требует repair/verify средствами launcher, если они поддержаны. Критерий продукта — зависимости сохранены и последствия честно отражены, а не вымышленная гарантия «можно откатить всё одним кликом».

## 4. Реальное освобождение и оптимизация имеют разные метрики

### Docker и WSL требуют двух уровней измерения

`docker system df` показывает daemon accounting, включая shared/unique layers. `system prune` затрагивает stopped containers, unused networks, dangling images и build cache; расширяющие flags меняют scope. Volume может содержать единственную копию базы даже при статусе unused. Поэтому build cache, images, containers и volumes — отдельные категории с разными permissions. Проверяются local daemon/context и endpoint: случайное подключение к удалённому production Docker неприемлемо. ([Docker df](https://docs.docker.com/reference/cli/docker/system/df/), [Docker prune](https://docs.docker.com/reference/cli/docker/system/prune/), [Pruning](https://docs.docker.com/engine/manage-resources/pruning))

Удаление данных внутри Linux filesystem не гарантирует немедленного уменьшения VHD на Windows. WSL cleanup и compaction — разные фазы с версионными preconditions, backup и quiesce. Нельзя предлагать универсальную raw VHD команду по найденному пути: требуется конкретный поддержанный maintenance adapter. После операции отдельно измеряется host-volume свободное место. ([WSL disk space](https://learn.microsoft.com/en-us/windows/wsl/disk-space), [WSL commands](https://learn.microsoft.com/en-us/windows/wsl/basic-commands), [Docker backup](https://docs.docker.com/desktop/settings-and-maintenance/backup-and-restore/))

| Действие | Содержательный результат | Что нельзя выдавать за результат |
|---|---|---|
| Удаление eligible objects | Удалённые объекты и наблюдаемое изменение free bytes | Сумма logical sizes как точное reclaim |
| Корзина/карантин на том же томе | Данные перемещены, остаются восстановимыми | «Место освобождено» до purge |
| Перенос C→D | C gained, D consumed | Общая экономия того же объёма |
| Docker prune | Daemon-reported reclaimed и host delta | Автоматическое равенство этих величин |
| WSL/VHD compact | Host delta после поддержанной операции | Linux free bytes как уже возвращённые Windows GB |
| OneDrive Free up space | Убрана локальная копия, облачная остаётся | Delete из sync folder как безопасная dehydration |
| Compression/CompactOS | Изменилось allocated storage | Удалены ненужные файлы или гарантирован speedup |
| TRIM/defrag | Обслужен накопитель | Новое файловое свободное пространство |

OneDrive «Free up space» отличается от Delete: удаление синхронизированного файла распространяется на облако и другие устройства. Preconditions включают завершённую синхронизацию, отсутствие pending uploads и выбранные pin/exclusion policies. CompactOS — сжатие системных binaries с trade-off по времени/CPU; его обратимость как конфигурации не равна восстановлению удалённых пользовательских bytes. ([OneDrive](https://support.microsoft.com/en-us/onedrive/save-disk-space-with-onedrive-files-on-demand-for-windows), [CompactOS](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/compact-os), [compact](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact))

Accounting сохраняет `removedLogicalBytes`, `allocatedEstimate`, `nativeReportedReclaimed`, `movedSourceBytes`, `destinationConsumed`, `dehydratedBytes`, `compressedDeltaEstimate`, `observedFreeDelta`, `observedAvailableDelta`. Before/after связываются со стабильным volume ID, timestamps и quota context. `GetDiskFreeSpaceExW` различает total free и доступное вызывающему пользователю; delta может быть отрицательной из-за параллельной записи и не обрезается до нуля. Цель «На C доступно минимум X GiB» проверяется после выполнения, включая later verification при reboot-required. ([GetDiskFreeSpaceEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw))

### Система оптимизируется по наблюдаемой проблеме

Storage maintenance использует выбор Windows по типу и возможностям тома. `Optimize-Volume` и `MSFT_Volume.Optimize` предоставляют analyze/retrim/defrag и соответствующую семантику. TRIM относится к уже ненужным блокам, а не удаляет личные файлы. Повторяющийся blanket defrag всех SSD не является осмысленной стратегией. ([Optimize-Volume](https://learn.microsoft.com/en-us/powershell/module/storage/optimize-volume), [MSFT_Volume](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/optimize-msft-volume))

Автозагрузка и фон показывают publisher, источник запуска, enabled state и наблюдаемые CPU/IO/runtime. Сначала допустим штатный Windows Settings handoff с повторным обнаружением изменения; собственный setter появляется после проверки конкретного механизма и rollback. Background controls не являются универсальным переключателем любых Win32 процессов. Потеря sync, уведомлений и launcher updates должна быть видна. ([Startup apps](https://support.microsoft.com/en-us/windows/experience/startup-boot/configure-startup-applications-in-windows), [Background activity](https://support.microsoft.com/en-gb/windows/privacy/windows-background-apps-and-your-privacy), [Settings URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings))

Гибернация, reserved storage, VSS и optional capabilities относятся к advanced storage decisions, а не общей cache-кнопке. Отключение hiberfile меняет доступные power states; pagefile зависит от commit и crash dumps, поэтому остаётся system-managed без отдельного доказанного основания. Нет оснований продавать indiscriminate registry cleaner, purge RAM, отключение Defender/updates или массовый shutdown служб как универсальное ускорение. Мощность Cleaner определяется поддержанными полезными действиями, а не числом опасных переключателей. ([powercfg](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options), [Pagefile sizing](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows))

## 5. Реализация проходит через каталог, аналитику и измеряемые ворота

### Новые модули дополняют предыдущий каркас

Предлагаются `Cleaner.Catalog`, `Cleaner.Adapters`, `Cleaner.Inventory`, `Cleaner.Usage`, `Cleaner.Ownership`, `Cleaner.Optimization` и `Cleaner.Accounting` как логические границы; физическое дробление solution выполняется по необходимости. UI добавляет «Глубокая очистка», «Приложения и игры», «Разработка и контейнеры», «Использование», «Оптимизация», «Профили». Apple-inspired тема из первого отчёта сохраняется; основная задача новых экранов — объяснить scope, coverage и последствия, а не добавить декоративный score.

| Приоритет | Вертикальный срез | Критерий завершения |
|---|---|---|
| P0 | Контракты adapters/packs/accounting | Схема, trust levels, scope binding, version compatibility |
| P1 | Windows + app/browser + developer caches | Несколько семейств с реальным preview, execution и verification |
| P1 | Inventory и usage collector | Exact identity, opt-in, coverage, unknown и gaps |
| P1 | Profiles и пакетный план | Одно подтверждение выбранных категорий, сохранённые exclusions |
| P2 | Docker и games/uninstall/move | Local context, saves/dependencies, native outcomes, host accounting |
| P2 | Category automation | Limits, idle/power policy, signed-pack changes не расширяют consent |
| P3 | WSL/VHD, SQLite, compression | Versioned preflight, backup/recovery, измеренный результат |
| P3 | Доверенный импорт/плагины | Provenance, lint, unsupported actions rejected, staged rollout |

Сначала gates качества, затем количество правил. Для каждого adapter нужны positive/negative fixtures, актуальные app versions, scope drift, busy app, context switch, cancellation, crash, disk full и changed-object cases. Native operation может быть неотменяемой посередине: UI останавливает очередь и сообщает фактическое состояние, а не принудительно убивает servicing. Ложный успех хуже честного `Partial` или `NeedsRecheck`.

Benchmark выполняется на snapshot VM/VHD с известным набором Windows/app/dev/game данных. Сравнение с BleachBit требует одинаковых выбранных категорий и проверки побочных эффектов, а не максимальной цифры одного запуска. Измеряются eligible allocated bytes, observed per-volume delta, daemon delta, elapsed time, IO, peak RAM, preview error, rebuild/redownload penalty, сохранность saves/sessions/source и корректность restore. Для speedup отдельно фиксируется сценарий startup/app launch, одинаковый hardware и cold/warm conditions; результат очистки не переносится автоматически на FPS. WPR пригоден для resource-based анализа. ([WPR](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-resource-based-analysis))

Предлагаемые acceptance gates: ни одного изменения за пределами утверждённого scope в adversarial fixtures; все неизвестные adapters/schema/IDs отклонены; ни одного удаления saves/source в поддержанном наборе; отчёт явно различает daemon/moved/quarantine/host bytes; usage gaps всегда видны; новые pack permissions не наследуют старое consent; uninstall/servicing проверяется повторным inventory. Ноль дефектов в тестовой выборке не доказывает отсутствия всех ошибок, но без этих gates выпускать изменяющий движок нельзя.

### Текущие правки starter проверяются отдельно от продуктовой программы

Замечания code review воспроизведены и исправлены отдельным минимальным изменением analysis-only starter. На старом коде новый regression harness дал **7 FAIL, exit 1**. Исправления затронули обход, семантику покрытия, диагностику root и нормализацию исключений. Вместо очереди с ограничением ширины используется depth-first stack приостановленных enumerators; реальная собственная fixture с **10 002 дочерними папками завершена полностью, Skipped 0**. Ограничение глубины по умолчанию 256 остаётся явным resource bound с `UnvisitedDirectories`, diagnostic и `Partial`, а не скрытой потерей siblings.

Policy skips отделены от недостаточного покрытия: сознательно исключённая область не делает разрешённый scope незавершённым. Ancestors проверяются сверху вниз через `File.GetAttributes`, поэтому missing root получает not-found, а не ошибочный link/cloud статус. Paths нормализуют Copy-as-path кавычки и завершающие разделители с сохранением drive root и directory boundary. `OrdinalIgnoreCase` выбран консервативно для текущего read-only инструмента; это не универсальная семантика case-sensitive NTFS. Дополнительно исправлен false-Partial при достижении ровно MaximumFiles без следующего подходящего файла.

Финальная проверка: **Release build — 0 warnings/errors; smoke harness — 22 PASS; regression harness — 7 PASS; self-contained publish — успешно; опубликованный WPF XAML smoke — exit 0.** Во время расширения harness был CS0136 из-за повторных имён локальных переменных; имена исправлены, сборка выполнена заново. Запуски `--no-build` после неуспешной промежуточной сборки исключены из evidence, поскольку исполняли старый бинарник. Полный вывод сохранён в `research_notes/Глубокая очистка и оптимизация Cleaner/starter_validation.md` и актуальном `starter/README_RU.md`.

Эти результаты подтверждают исправленный **metadata-only каркас**, а не реализацию нового каталога. SQLite, executor, модель, игровой ownership protection и глубокие adapters не добавлены. Реальные cloud providers/network shares, case-sensitive directory fixtures, ручные Narrator/DPI, race-free handle traversal и performance benchmarks остаются непроверенными. Независимые size-format/LiveRegion/performance refactors в scope исправлений не входили.

## Что изменилось в критерии готовности

Первый готовый Cleaner должен уметь реально исполнить понятный пакет полезных действий за пределами temp и показать, куда ушли байты и чем пользователь за это заплатил — повторной загрузкой, rebuild, переносом или отказом от rollback. Одновременно аналитика обязана различать отсутствие наблюдений и отсутствие потребности. Эти требования усиливают продукт: безопасный механизм нужен, чтобы доверить ему мощные категории и автоматизацию, а не чтобы навсегда оставить его анализатором.

Новые исследования не подтверждают уже достигнутую скорость, точность usage classification или экономию пространства Cleaner. Они задают проверяемую программу сборки. Логичный следующий milestone — действующий catalog/adapters vertical slice с несколькими крупными семействами, category-level consent, ownership evidence и честным accounting; после него можно расширять набор по результатам одинаковых regressions и benchmarks.
