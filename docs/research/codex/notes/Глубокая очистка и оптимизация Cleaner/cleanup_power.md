# Мощный движок очистки и расширений Cleaner

Дополнение к архитектуре: 23 октября 2026 года. Новое требование пользователя — глубокая очистка уровня BleachBit, анализ использования и содержательная оптимизация, а не набор temp-каталогов. Предыдущие файловые гарантии сохраняются как механизм исполнения; они не ограничивают продукт слабым набором функций. Никакие файлы продукта, предыдущий отчёт и starter в рамках этого исследования не изменялись.

## 1. Как устроена мощность BleachBit и что стоит перенять

### Takeaway
Главная сила BleachBit — большой каталог предметных правил и набор разных исполняющих действий. Для Cleaner предлагается такая же широта покрытия с удобным подтверждением категорий/профилей, но собственная реализация движка и более явно разделённые уровни доверия.

### Cited Findings
- CleanerML — XML-формат, описывающий не только удаление файлов, но и truncate/shred, изменение реестра, специальные действия; поиск включает одиночный путь, glob, walk и deep с regex-фильтрами, OS-detection и XSD. `deep` предназначен для рассеянных совпадений и объединяет поиски одного корня, в отличие от полного обхода каждого правила. — [CleanerML](https://docs.bleachbit.org/cml/cleanerml/)
- Документированные типы действий включают `ini`, `json`, `process`, `sqlite.vacuum`, `winreg` и специфические операции Chrome/Mozilla. Директива running позволяет остановить очистку работающего приложения. — [CleanerML actions](https://docs.bleachbit.org/cml/cleanerml/)
- В текущем исходнике Action.py существует реестр ActionProvider/plugin classes, в котором ключ действия сопоставляется реализации. Видны отдельные providers для selective cookies, JSON/INI, SQLite, Windows registry и process. — [Action.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Action.py)
- Command.py разделяет стандартные Delete/Truncate/Shred и special commands; общий интерфейс `execute(really_delete)` участвует и в preview, и в execution. Delete перед действием запрашивает размер через FileUtilities.getsize, а special Function поддерживает отдельную preview function. Следовательно, preview — не просто суммирование всем найденным файлам одинакового размера. — [Command.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Command.py)
- Worker.py организует preview/delete, агрегирует результаты отдельных команд, ошибки и special counts. — [Worker.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Worker.py)
- DeepScan.py компилирует search predicates и обходит деревья, отбрасывая whitelist/non-normal directories. Это подтверждает архитектуру общего discovery pass для множества фильтров. — [DeepScan.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/DeepScan.py)
- Реальное определение Chrome обнаруживает несколько профилей, учитывает запущенные процессы, разделяет опции; есть отдельная очистка browser AI models и GPU/shader-related caches. Это значительно шире `%TEMP%`. — [Chrome cleaner](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- Winapp2 импортируется как дополнительный каталог Windows-правил; документация BleachBit говорит о 2400+ дополнительных cleaners, но это заявленный размер каталога, не измеренное число проверенных операций на конкретном ПК. — [Winapp2 guide](https://docs.bleachbit.org/doc/winapp2ini/)
- Winapp.py парсит DetectFile/registry detection, FileKey/RegKey и ExcludeKey; FileKey поддерживает `RECURSE` и `REMOVESELF`. Это предметный importer с семантикой, а не произвольное копирование строк INI в удаление. — [Winapp.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Winapp.py)
- Официальная инструкция предлагает выбирать опции, закрыть обслуживаемые приложения, просмотреть preview и затем очистить. Она не требует отдельного подтверждения каждого файла. — [General usage](https://docs.bleachbit.org/doc/general-usage/)
- В отдельном CleanerML-репозитории `pending` означает ещё не подтверждённые определения, `release` — считающиеся безопасными и эффективными; авторы рекомендуют тестирование XSD и описание tested versions. — [CleanerML repository](https://github.com/bleachbit/cleanerml)
- Руководство автора правила рекомендует упражнять функциональность приложения, изучать реальное расположение профилей и использовать Process Monitor для поиска создаваемых данных. Это исследование поведения приложения, не угадывание по имени расширения. — [Finding files to delete](https://docs.bleachbit.org/cml/finding-files-to-delete/)
- Собственная FAQ BleachBit предупреждает: очистка кэшей может замедлить последующий запуск/загрузку; wipe empty space не увеличивает свободное место и плохо подходит для частого применения на SSD. — [FAQ](https://docs.bleachbit.org/doc/frequently-asked-questions/)

### Inferences
#### Продуктовый вывод
Не предлагать пользователю «безопасность» в форме бессильного анализатора. Целевой Cleaner должен уметь очистить десятки разных категорий одним планом после понятного выбора. Ограничения реализуются внутри адаптеров, а не заставляют человека просматривать сотни тысяч объектов вручную. Локальная модель объясняет происхождение, компромиссы и собирает профили; каталог и движок предоставляют реальные возможности исполнения.

Три режима одного движка: регулярное обслуживание, глубокая очистка, освобождение большого объёма. У каждого — разные допустимые категории, а не разные принципы достоверности. «Глубоко» означает больше источников и больше типов действий, включая большие caches/containers/rollback data, а не безадресное `*.tmp` по всем дискам.

#### Расширяемая архитектура
```text
InstalledAppInventory + ProfileDiscovery + VolumeIndex + UsageEvidence
                     -> RuleMatcher (trusted catalog and custom scopes)
                     -> Findings with provenance and consequences
                     -> PlanComposer (dedup/dependencies/budgets/presets)
                     -> SharedPreview (same selectors and adapters)
                     -> DeterministicExecutor -> Verification -> OutcomeLedger
```
Отдельные каталоги rule packs: Windows servicing, browsers, launchers/games, developer/build/package caches, creative/media, communication apps, uninstall residues, virtual disks и custom domains. У каждой family есть maintainer, changelog, version support range и regression fixtures. Компонент discovery находит реальные custom install/cache roots и несколько профилей. Буква C/D не влияет на принадлежность.

Интерфейсы предложения:
```text
ICleanerAdapter.Discover(context) -> Findings
ICleanerAdapter.Estimate(finding, policy) -> Estimate{range?, quality, sideEffects}
ICleanerAdapter.Prepare(planItem) -> PreparedOperation{preconditions, stopRequirements}
ICleanerAdapter.Execute(prepared, capability, cancellation) -> OperationOutcome
ICleanerAdapter.Verify(outcome) -> Verification
```
Типизированные операции: DeleteKnownCache, TruncateKnownLog, RemoveAppHistory, EditKnownJson/Ini, VacuumKnownSqlite, ClearPackageCache, PruneDockerBuildCache, ServiceWindowsComponents, UninstallRegisteredProduct, CompactSupportedVirtualDisk, OptimizeVolume. Registry action допустим для конкретной MRU/privacy category или подтверждённого app residue; blanket registry cleaner не нужен для мощности и не должен продаваться как ускорение.

Каталог определяет селекторы и параметры известных операций, а код адаптера реализует semantics. Нельзя выдавать `process arbitrary-command` всему каталогу только ради feature parity. При этом подписанный встроенный адаптер вправе запускать подтверждённый инструмент с фиксированным executable identity и типизированными аргументами: DISM, package manager, Docker CLI. Это полноценная сила, без shell eval в каждом XML/JSON.

### Gaps
- Эти источники не подтверждают безопасность каждой из тысяч community rules на текущих версиях ПО. Число записей нельзя превращать в рейтинг качества.
- Просмотрены текущие master-файлы; при реализации нужно закрепить commit/tag для воспроизводимости и сравнить изменения. Это архитектурный разбор исходника, не аудит релиза BleachBit.
- В документации и master source возможен временной разрыв: например, новые action keys появляются в исходнике раньше руководства. Каталог Cleaner должен иметь явную engine schema/capability version.

## 2. Какие мощные действия реализовать и как ими управлять без ручной возни

### Takeaway
Высокоэффективные цели — developer caches, контейнерные слои, виртуальные диски, browser/launcher caches и штатные категории обслуживания Windows. Действия получают заранее настроенную политику и массовый preview; только отдельные неоднозначные пользовательские данные требуют индивидуального решения.

### Cited Findings
- Docker `system prune` удаляет остановленные контейнеры, неиспользуемые сети, dangling images и build cache; volumes по умолчанию не удаляет. `--volumes` затрагивает unused anonymous volumes, named volumes требуют отдельного действия. Неиспользуемый volume может содержать нужные данные. — [Docker pruning](https://docs.docker.com/engine/manage-resources/pruning)
- `docker system df -v` показывает shared/unique/virtual size для images и отдельные counts/reclaimable, помогая избежать двойного счёта общих слоёв. — [Docker system df](https://docs.docker.com/reference/cli/docker/system/df/)
- BuildKit GC поддерживает упорядоченные policies и периодическую сборку, а builder/buildx prune выполняются разово. — [Build cache garbage collection](https://docs.docker.com/build/cache/garbage-collection/)
- NuGet различает HTTP/temp/plugins/global-packages caches; глобальный каталог используется проектами PackageReference непосредственно, а после очистки нужны restore/redownload. Открытая Visual Studio может удерживать файлы. — [NuGet caches](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders)
- Для NuGet есть штатные list/clear команды с точным видом cache. — [dotnet nuget locals](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-locals)
- pip предоставляет cache dir/info/remove/purge; layout кэша официально считается implementation detail и может меняться. Purge очищает wheel/HTTP caches; отключение caching обычно повышает время и сетевой трафик. — [pip caching](https://pip.pypa.io/en/stable/topics/caching/)
- npm cache считается восстанавливаемым и integrity checked; clear для освобождения места требует `--force`, доступна verify. — [npm-cache](https://docs.npmjs.com/cli/v11/commands/npm-cache/)
- SQLite VACUUM перестраивает БД и может требовать до двукратного размера исходной БД свободного места; это не бесплатная операция на почти заполненном диске. — [SQLite VACUUM](https://sqlite.org/lang_vacuum.html)
- `compact vdisk` уменьшает динамический VHD, который не сжимается автоматически при удалении файлов; требует detached либо attached read-only состояния. — [Compact vdisk](https://learn.microsoft.com/windows-server/administration/windows-commands/compact-vdisk)
- `Optimize-Volume` поддерживает Analyze/ReTrim/Defrag/SlabConsolidate/TierOptimize, а Storage WMI предоставляет соответствующий типизированный метод. — [Optimize-Volume](https://learn.microsoft.com/powershell/module/storage/optimize-volume); [MSFT_Volume Optimize](https://learn.microsoft.com/en-us/WINDOWS-HARDWARE/DRIVERS/storage/optimize-msft-volume)
- WinGet uninstall позволяет выбирать зарегистрированный продукт по id/product-code. Это supported uninstall route, а не удаление каталога приложения. — [WinGet uninstall](https://learn.microsoft.com/da-dk/windows/package-manager/winget/uninstall)
- Restart Manager может определять и закрывать/restart приложения и службы с ограничениями разрешений/сессии; критические службы не закрываются таким образом. — [Restart Manager](https://learn.microsoft.com/en-us/windows/win32/rstmgr/about-restart-manager)

### Inferences
#### Предлагаемая capability matrix
Все сроки/приоритеты ниже — предложения, не уже реализованные функции. Для общих Windows-категорий также используются первичные источники в предыдущем `filesystem.md`.

| Цель | Доказательство и операция | Масштаб/эффект | Последствия и режим |
|---|---|---|---|
| Browser HTTP/GPU/shader/cache | Обнаруженные профили + versioned rules | Очистка всех выбранных профилей одним действием | Повторная загрузка/компиляция; preset Maintenance |
| Browser cookies/site storage/history/AI models | Отдельные adapters и domains keep list | Privacy reset или освобождение models/cache | Logout, offline web app loss, redownload; отдельный preset Privacy/Deep |
| Telegram/Discord/media app caches | App-specific verified storage classification | Крупные повторно получаемые media caches | Offline copies могут быть важны; исключить drafts/export/private-only media; per-app policy |
| Steam/Epic/launcher download caches | Launcher adapter/cache inventories | Сотни/тысячи файлов одной категории | Retry login/redownload; не смешивать с сохранениями/mods |
| Game shader caches | Known owner + supported path/version | Удаление ненужных компилированных shader sets | Stutter/recompile при следующей игре; Deep по выбору, не ежедневный Maintenance |
| NuGet/npm/pip | Native list/info + exact cache type | Существенная очистка toolchain space | Offline rebuild потеряется; developer preset, retain recently used policy where supported |
| Build outputs | Project inventory + tool-specific generated dirs | bin/obj/target/build для выбранных неактивных проектов | Долгая пересборка, custom artifacts могут быть незаменимы; explicit project-set policy |
| Docker build cache/dangling layers | Engine APIs/CLI inventory + prune filters | Shared-aware reclaim | Пересборка и redownload; Developer Deep |
| Docker stopped containers/volumes | Отдельные inventory объекты | Иногда большой reclaim | Container writable data/database loss; никогда не спрятано в cache toggle, отдельно авторизованный policy tier |
| WSL/Docker/VM VHD | Сначала внутренний prune, затем supported compact | Гостевые free bytes могут перейти в host free bytes | Offline/shutdown window и adapter-specific support; Advanced, не сырое удаление VHDX |
| WinSxS/Windows.old/Delivery Optimization | Только штатные servicing adapters | Глубокая системная очистка | Потеря rollback/повторная доставка; Windows Deep, последствия общим планом |
| Dumps/log retention | Источник+retention+age+сохранить последние N | Фоновое обслуживание | Потеря диагностики; category policy |
| Приложения и подтверждённые остатки | Supported uninstall + provenance re-scan | Освобождается installation+точно owned residue | Shared components/saves/configs сохраняются по политике; Uninstall profile |
| Большие редко используемые пользовательские данные | Usage evidence + explicit relocation/archive plan | Перенос/архивация освобождает исходный диск | Это управление данными; не мусор. Допускается batch approval для выбранных libraries |
| SSD/HDD maintenance | Windows Analyze и подходящий Optimize operation | Состояние allocation/TRIM/fragmentation | Не обещать прирост FPS или GB; отдельные измерения до/после |

Пути Telegram/Discord/Epic и конкретные project dirs в таблице — области будущих adapters; в этом цикле они не квалифицированы по собственным официальным источникам. Не выдавать их за готовые очистители. Эффективный v1 обязан включать хотя бы несколько категорий большой потенциальной ёмкости, например NuGet/npm/pip + браузеры + Windows servicing, а не откладывать всю силу на неопределённый roadmap.

#### Пресеты и полномочия
Предлагаемые presets: Maintenance (bounded retention, cheap rebuild), Deep space (крупные redownload/rebuild caches), Developer (toolchain/Docker), Gaming (launcher/shader с исключениями saves/mods), Privacy (история/site data), Uninstall follow-up, Storage maintenance. У пользователя видны последствия и прогноз по категориям. Expand раскрывает отдельные файлы, но не обязателен. Команда «глубоко очистить выбранное» подтверждает один конкретный план из всех выбранных допустимых действий.

Permission tiers: P0 metadata/preview; P1 обычные восстановимые app caches в собственном профиле; P2 сброс приватности, rebuild/offline loss и закрытие приложений; P3 elevated servicing; P4 удаление volumes/rollback/единственных пользовательских копий. P2/P3/P4 можно включить в профиль заранее с лимитами, расписанием и consequences; не спрашивать о каждом объекте. Tier отражает последствия и права, не «плохое/запрещённое». Выбор P3 не разрешает модели произвольные команды. У действий явные capability token, allowed adapter ID, roots/products/object IDs, maximum scope и exclusions.

Остановка приложений — опциональный policy: Skip busy / Wait until idle / Graceful close selected apps now / Maintenance window. В preview показываются приложения, которые будут закрыты. После graceful close проверить processes/handles заново; force kill не default, так как несохранённые данные не являются cache. Restart Manager — инструмент detection/coordination, не обещание безопасно перезапустить любое приложение. Режим unattended не закрывает неизвестные новые процессы только из-за имени exe.

Нативный CLI adapter может иметь более широкий эффект, чем файловый preview: тогда preview должен показывать «операция по таким-то критериям», качество оценки и момент финальной eligibility проверки. Если native tool не умеет удалить точные утверждённые IDs, нельзя одновременно обещать immutable file manifest. Каждый adapter публикует capability `SnapshotBound` или `PolicyBound` и правила scope drift; для PolicyBound пользователь утверждает предикаты/лимиты, а при опасном выходе остановка до запуска.

#### Trusted packs и custom rules
Rule manifest: author/maintainer, packageId/version, supported app versions, actionKinds, effectClass, sources, fixtures, privilege tier, retirement/expiry, schema/engine requirement, preview accuracy, recoverability, signature, source license/provenance. Пакеты обновляются отдельно от приложения; подпись проверяется до загрузки, rollback версии доступен, новые права/категории не включаются старым расписанием автоматически.

Custom rules должны быть действительно полезны: конкретные roots, glob/regex с timeout, ages/retention, owned file types, keep list, action known executor, min/max bytes, exclusions. Импорт CleanerML/Winapp2 — компиляция поддерживаемого subset в собственный IR, затем lint/preview/tests; unsupported `process` или registry action не превращается в shell fallback. Режим trusted developer adapter — отдельный подписанный кодовый plugin после явной установки, а не скрытый action в data pack. Пользователь может задать мощные свои правила без публикации каждому файлу вопроса; ответственность scope показывается на уровне правила/профиля.

### Gaps
- Поддержка Windows VHDX/WSL/Docker Desktop shrink требует отдельной matrix версий: документация `compact vdisk` доказывает механизм VHD, не универсальное решение для всех WSL storage backends.
- Реальный освобождаемый объём и rebuild time зависят от машины. Нельзя честно назвать количество GB до inventory конкретного компьютера.
- Native package tools могут не иметь точных age filters: Cleaner не должен подменять selective retention обещанием, если adapter выполняет только clear-all.

## 3. Как измерить полезность, надёжность и правомерно расширять каталог

### Takeaway
Глубина продукта измеряется покрытием валидированных действий, реальным результатом и качеством решений о повторном использовании. Большое число XML-правил, возраст файла и искусственный «процент ускорения» не заменяют эти измерения.

### Cited Findings
- Актуальный advisory BleachBit GHSA-q5ww-c3j6-8j23 опубликован 17 августа 2026: затронуты версии <6.0.3, исправленная версия 6.0.3. Описан malicious CleanerML в user-writable каталоге с process action, GUI/CLI warning bypass и возможной elevation при elevated запуске. — [Maintainer security advisory](https://github.com/bleachbit/bleachbit/security/advisories/GHSA-q5ww-c3j6-8j23)
- Upstream BleachBit прямо лицензирует исходный код и cleaner definitions под GPL v3 or later; отдельный CleanerML repository также GPL3+. — [BleachBit README](https://github.com/bleachbit/bleachbit); [CleanerML license](https://github.com/bleachbit/cleanerml)
- GPLv3 текст в upstream COPYING содержит условия распространения modified/covered works и corresponding source; коммерческая продажа сама по себе не запрещена. — [GPLv3 COPYING](https://raw.githubusercontent.com/bleachbit/bleachbit/master/COPYING)
- У upstream Winapp2 сейчас указан CC BY-SA 4.0 и опубликован соответствующий license file, это другая лицензия, чем у BleachBit. — [Winapp2 repository](https://github.com/MoscaDotTo/Winapp2); [License.md](https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/License.md)
- CC BY-SA разрешает sharing/adaptation в том числе коммерчески с attribution, indication of changes и ShareAlike для adaptations; deed подчёркивает, что полный legal code важнее краткого резюме. — [Creative Commons BY-SA](https://creativecommons.org/licenses/by-sa/4.0/)
- Last access timestamp не является точным счётчиком фактического чтения: Microsoft описывает различия файловых систем, задержку NTFS до часа, а также возможность запретить handle-операциям обновлять timestamp. — [SetFileTime](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfiletime)

### Inferences
#### Доказательства использования
Оценка использования — подсистема, а не один timestamp. Нужны разные evidence: обнаруженные проекты/зависимости/lockfiles, package manager metadata, engine object references, launcher inventory, наблюдённая активность приложения в выбранном пользователем интервале, история роста и прошлое восстановление/повторная генерация. Не найден uninstall registration — лишь один сигнал; portable apps и custom profiles учитываются inventory. «Не наблюдалось N дней» отличается от «никогда не используется». Слабое evidence повышает неопределённость и влияет на preset, но не требует ручной возни со всеми хорошо классифицированными кешами.

Для high-confidence orphan residue нужны provenance цепочки: раньше известный product/profile владел root; confirmed uninstall event или сравнение inventory; нет действующего portable/app reference; содержимое классифицировано как cache/generated; исключены shared saves/licenses/user materials. Сохранить версии и human override. Если доказательства слабые, batch review группы «возможные остатки», а не тихая очистка.

Неочевидное улучшение относительно blanket cleanup: сохранять часто восстанавливаемые cache groups и измерять churn. Если очищенные 8 GB вернулись за сутки ценой 40 минут download/rebuild, рекомендовать cap/TTL/retention или оставить hot cache. План оптимизируется по net reclaimed, cost-to-rebuild, offline requirements, workload state и целевому запасу free space. ИИ объясняет компромисс и предлагает сценарии, deterministic eligibility остаётся основой.

#### Метрики результата
Каждый adapter возвращает: planned logical bytes, estimated allocated reclaim/range/quality, successful/failed/skipped operations, before/after relevant object count/size, free-space observations by volume, quarantine bytes retained, redownload/rebuild effects. Delete result bytes не равно free-space delta. Docker: отдельно inside engine freed и host VHD footprint; SQLite: до/после verified database size; servicing: native result + measured delta; ReTrim: завершённая операция/diagnostics без обещания дополнительного free GB. На фоне параллельных процессов free-space delta — наблюдение за интервал, а не causal accounting каждого байта.

Shared preview/execution predicate и one-pass matching уменьшают рассогласование, но native service operation может иметь неизвестный точный результат. UI: «оценка 3–5 GB, будет измерено», а не выдуманная точность. В отличие от суммирования размеров Command/Worker, подтверждённый итог Cleaner должен всегда отделять completed operation counters от измеренной экономии.

#### Качество каталога
Обязательные gates для каждой category family: clean profile -> exercise all app features -> snapshot -> preview -> execute -> reopen application -> functional checks -> compare preserved auth/saves/documents -> measure reclaimed and regeneration -> повторное выполнение (idempotence) -> version upgrade fixtures. Проверять custom profile/multiple user profiles, running/background app, partial errors, locked files, schemas DB, unknown versions. XSD/schema validation не доказывает правильность target paths. Пакет проходит staged testing, canary, подписанный release; rollback/revocation key доступен без принуждения к telemetry.

Security advisory даёт конкретный урок: warning/вычисленный hash в том же writable ini, где атакующий меняет правила, не образуют границу доверия. Доверенная подпись, trusted key location, отдельный elevated capability broker и одинаковая policy в GUI/CLI/automation должны проверяться раздельно. Это не причина отказаться от plugins; это причина различать data rules и исполняемый код.

Предлагаемые продуктовые acceptance criteria: (1) несколько реально работающих high-impact families кроме temp; (2) один подтверждённый preset обрабатывает много категорий без per-file prompts; (3) каждый выбранный high-impact adapter имеет информативный preview и post-verification; (4) повторный запуск не ухудшает сохранённые app state/data; (5) measured reclaim честно отражает VHD/карантин/shared blocks; (6) schedule не расширяет права после обновления rule pack; (7) неизвестная/неподдерживаемая категория видна как coverage gap, а не «ваш ПК полностью очищен».

#### Лицензионные варианты
1. Собственный движок и собственные определения, написанные по документации и наблюдаемому поведению приложений, с источниками и provenance. Это рекомендуемый базовый путь для свободного выбора лицензии Cleaner. Совместимость формата сама по себе не равна копированию реализации, но нельзя делать юридический вывод об отсутствии copyright/database прав без анализа конкретных материалов.
2. GPL3+-совместимый продукт или отдельная распространяемая GPL-часть при reuse BleachBit code/rules с выполнением obligations. Конкретная граница derivative/aggregate зависит от интеграции; вынесенный subprocess не является автоматически юридической индульгенцией.
3. Отдельный optional Winapp2 pack с attribution, license, отметкой изменений и соблюдением ShareAlike для заимствованного/адаптированного материала. Нельзя утверждать, что лицензия данных непременно распространяется на весь независимо написанный движок; также нельзя утверждать обратное для любой интеграции без анализа. Проверять исходный repo/file/commit, поскольку BleachBit-modified winapp2 variants могут иметь иной provenance и условия.

Не копировать GPL XML definitions, перевести их в JSON и объявить «новые собственные правила»: смена синтаксиса не устраняет происхождение. Сохранять SBOM/third-party notices и manifests источников; перед распространением определиться с licensing strategy и проверить выбранный способ reuse. Эти рекомендации — инженерная карта вопросов, не юридическое заключение.

### Gaps
- Не проводились destructive trials BleachBit/Cleaner на машине пользователя. Нет измеренных сравнений «Cleaner на X% мощнее/быстрее».
- Точные usage collectors, overhead/privacy budget и цифровая модель score требуют собственного дизайна и отдельной проверки источников; этот файл задаёт интеграцию evidence в cleanup decisions.
- В этом исследовании нет полной проверки всех community definition copyrights/истории upstream license changes. Для distribution нужен pinned provenance конкретных заимствованных материалов.
