# Архитектура Windows приложения Cleaner

Дата проверки источников и среды: 23 октября 2026. Исходный PROJECT_PROMPT_RU.md прочитан; он определяет поведение продукта, а не готовую реализацию. Далее явно разделены подтверждённые возможности платформ, предлагаемый production-дизайн и реально собранный starter. Это исследование не является доказательством безопасности ещё не написанного очистителя.

## Какой стек выбрать и почему

### Takeaway

Рекомендация: Windows-only приложение на C# и .NET 10 LTS, WPF/XAML, production MVVM, отдельный Windows filesystem adapter, локальная SQLite и изолированный модельный worker. Для первого каркаса достаточно BCL/WPF без внешних пакетов. Выбор WPF — инженерный вывод для конкретной команды и рисков, а не заявление о превосходстве над WinUI.

### Cited Findings

- WPF работает только на Windows, входит в современный .NET, поддерживает XAML, привязки, стили, векторное отображение. Это соответствует Windows-only концепции. [Microsoft WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/), доступ 2026-10-23.
- Microsoft рекомендует WinUI 3/Windows App SDK для новых Windows-приложений. Следовательно, выбирать WPF следует осознанно, а не на основании якобы заброшенного WinUI. [Windows apps](https://learn.microsoft.com/en-us/windows/apps/), доступ 2026-10-23.
- Официальная таблица на дату исследования: .NET 10 LTS, patch 10.0.13 от 2026-10-13, поддержка до 2028-11-14; .NET 8/9 заканчивают поддержку в ноябре 2026. [Support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), доступ 2026-10-23. Поэтому новый проект на 8/9 сейчас потребует почти немедленного перехода.
- Tauri 2 использует на Windows WebView2, требует Rust/MSVC build tools; ACL capabilities ограничивают доступ frontend к командам. [Prerequisites](https://v2.tauri.app/start/prerequisites/), [Capabilities](https://v2.tauri.app/security/capabilities/), доступ 2026-10-23. Capabilities не заменяют независимые проверки привилегированного Windows-executor.
- Electron поставляет Chromium и Node.js; официальный security guide требует регулярного обновления и ограничения возможностей web-контента. [Electron security](https://www.electronjs.org/docs/latest/tutorial/security), доступ 2026-10-23. Это дополнительный контур сопровождения, а не доказанный конкретный объём RAM Cleaner.
- Таблица поддержки .NET зависит от редакции и жизненного цикла Windows; «Windows 10+» без уточнения сейчас недостаточно. [Supported OS .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), доступ 2026-10-23.

### Inferences

Сравнение, оценки для Cleaner, без выдуманных бенчмарков:

| Кандидат | Сильная сторона | Цена для проекта | Вывод |
|---|---|---|---|
| WPF + .NET 10 | Один managed-стек для UI/Win32 integration, простая CLI-сборка, нет обязательного web runtime | Современную визуальную систему, data virtualization и MVVM надо собрать осознанно | Основной выбор при небольшой Windows-команде |
| WinUI 3 + Windows App SDK | Современные Windows controls и официальный приоритет Microsoft | Дополнительные deployment/runtime решения; нужен spike с broker, packaging и automation | Сильный альтернативный выбор, если команда уже умеет WinUI |
| Tauri 2 + Rust + web UI | Системный backend на Rust, удобная веб-вёрстка, явный frontend IPC | Два языка, WebView2, Rust/FFI/Windows rights, отдельный elevated broker | Выбирать при существующей Rust/frontend компетенции либо реальном плане cross-platform |
| Electron + TypeScript | Большая web-экосистема и быстрая сборка сложного UI | Chromium/Node patch cadence и дополнительная web IPC поверхность; native safety engine всё равно нужен | Допустим для сильной JS-команды, преимуществ для Windows cleaner недостаточно |

Не следует выбирать стек по маркетинговому размеру пустого bundle. Сравнивать нужно signed self-contained приложение с одинаковым scanner, SQLite, моделью и cold start, на одном слабом и одном среднем ПК. Локальная модель может потреблять гораздо больше ресурсов, чем разница UI стеков.

Предлагаемый support baseline продукта: поддерживаемые Windows 11 x64; ARM64 после проверки runtime/model provider и installer. Старые/ESU/LTSC варианты Windows — отдельная матрица, без обещания «всё с Windows 10». Runtime patch 10.0.13 подтверждён; SDK 10.0.402 подтверждён локально и закреплён в starter. Версии будущих NuGet-пакетов, SQLite native, WiX, model runtime не угадываются: фиксируются после отдельного dependency spike в Directory.Packages.props, packages.lock.json, tool manifest и SBOM.

### Gaps

Нет сопоставимого продуктового бенчмарка четырёх стеков на целевой машине, нет известных компетенций/численности будущей команды. Нет доказанной необходимости ARM64/Windows 10; это продуктовые решения. Выбранный стек пересматривать только после конкретного блокера, а не ради внешнего вида: WPF поддерживает кастомные стили.

## Как должны работать процессы и данные

### Takeaway

Production-архитектура: модульное ядро внутри обычного пользовательского процесса; отдельные процессы только для модели и краткоживущего привилегированного broker. Ни модель, ни UI, ни SQLite не становятся источником полномочий на удаление. База хранит наблюдения; executor повторно доказывает допустимость операции над текущим объектом.

### Cited Findings

- Windows named pipes имеют security descriptor; default ACL может давать чтение Everyone/anonymous, а generic write даёт дополнительные права. Нужна явно заданная DACL и минимальные права. [Named pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), доступ 2026-10-23.
- Microsoft.Data.Sqlite — отдельный lightweight ADO.NET provider, EF Core необязателен. [Provider overview](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/), доступ 2026-10-23.
- В SQLite WAL одновременно работает один writer; readers и writer обычно не блокируют друг друга, но SQLITE_BUSY всё равно возможен; WAL не подходит для базы на сетевом диске. Серьёзный WAL-reset bug исправлен в SQLite 3.51.3 и backports 3.50.7/3.44.6. Проверять `select sqlite_version()` именно поставляемой native-библиотеки. [SQLite WAL](https://www.sqlite.org/wal.html), доступ 2026-10-23.
- WPF UI virtualization сокращает число UI containers, но не даёт автоматическую data virtualization: можно всё равно удерживать миллионы записей в памяти. [Performance controls](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls), доступ 2026-10-23.

### Inferences

Ниже проектное предложение, а не уже реализованные компоненты.

```
Cleaner.Desktop (ordinary user, WPF + MVVM)
  → Application use cases / deterministic policy / plan builder
  → Scanner / category adapters / file identity / local persistence
  → ModelWorker (ordinary user, read-only sanitized DTOs, no executor)
  → BrokerClient → short-lived Cleaner.Broker (UAC, typed allowlist)

Model output → strict schema → policy validator → preview → user approval
            → current-object revalidation → executor → operation journal
```

Production solution boundary proposal:

```
src/
  Cleaner.Desktop/             WPF shell, views, viewmodels, accessibility
  Cleaner.Domain/              findings, plan, evidence, invariants, no Windows calls
  Cleaner.Application/         use cases, job orchestration, ports/interfaces
  Cleaner.Platform.Windows/    Win32 metadata, identity, volumes, shell adapters
  Cleaner.Persistence/         SQLite queries/migrations, bounded batch writer
  Cleaner.Categories/          versioned evidence rules, test fixtures
  Cleaner.Policy/              deny rules, exclusions, typed capabilities
  Cleaner.Execution/           journal and non-elevated operations, revalidation
  Cleaner.Broker.Contracts/    versioned protocol DTOs only
  Cleaner.Broker/              narrowly scoped elevated operations
  Cleaner.Model.Contracts/     schema/DTO, no command or filesystem interfaces
  Cleaner.ModelWorker/         chosen local inference runtime
tests/
  unit/ integration/ adversarial/ recovery/ ui/ performance/
packaging/ docs/ benchmarks/ fixtures/
```

Начинать с 3–5 projects и выделять новые при появлении реальных границ. Не создавать десяток пустых assemblies ради диаграммы. Starter уже содержит только Core/Desktop/SmokeTests.

**Processes and rights.** UI manifest `asInvoker`. Обычный анализ не требует UAC; недоступные каталоги — coverage gap. Повышение по конкретному системному действию после preview. На первом этапе broker отсутствует. Затем broker реализует закрытый перечень операций вроде `RunSupportedSystemMaintenance(categoryId, policyVersion, planDigest)`, без `ExecuteCommand`, `DeleteAnything(path)` или PowerShell от модели. Никаких произвольных shell strings. Сам executable и изменяемые policy/rules broker должны быть защищены от записи обычным пользователем, предпочтительно Program Files после per-machine install. Обычная SQLite из LocalAppData считается недоверенным входом для broker.

**IPC proposal.** Named pipe только local machine, случайный endpoint для конкретного сеанса, explicit DACL для SID/сеанса инициатора, отказ remote client, проверка token/session/PID/image и policy version. Nonce/handshake и correlation ID защищают от смешения запросов, но не служат единственным authorization. Проверять максимальную длину сообщения, глубину JSON, таймаут, enum allowlist, cancellation, replay и one-shot plan binding. Проверка Authenticode сама по себе не заменяет защищённый путь и проверку полномочий. В broker отсутствуют интернет и модель. Протокол документировать и fuzz-test до включения destructive capability. Конкретный механизм authenticated parent binding требует security spike: эти меры не означают, что готовое доказательство доверия уже существует.

**Data schema proposal.** `Scans(id, startedUtc, completedUtc, state, policyVersion, coverageJson)`; `Volumes(id, identity, filesystem, observedFreeBytes)`; `Nodes(scanId,nodeId,parentId,volumeId,fileId?,path,attributes,logicalBytes,allocatedBytes?,allocationQuality,errorCode?)`; `Findings(id,scanId,categoryId,confidence,evidenceJson,ruleVersion)`; `Exclusions(id,scope,canonicalIdentity?,displayPath)`; `Plans(id,scanId,state,policyVersion,digest,expiresUtc)`; `PlanItems(planId,nodeId,action,evidence,expectedIdentity,expectedState)`; `Operations(id,planId,itemId,state,startedUtc,completedUtc,actualBytes?,errorCode)`; `QuarantineEntries` only after recovery design. Paths are presentation and lookup data, not immutable file identity; no global lower-case normalisation on case-sensitive trees. Native identity mandatory for future destructive revalidation. Metadata observations may be stale immediately.

**SQLite proposal.** LocalAppData storage, schema migrations with backup and explicit downgrade policy, one bounded writer queue and chunked transactions, read-only query connections, foreign keys on, indexes on scanId/parentId/category/state. WAL checkpoint/retention limits and busy timeout; `synchronous` durability decision separate for regenerable scan cache and action journal. Disk-full is explicit failure; don't mark pending action successful when DB write failed. No full personal paths in crash diagnostics by default. Purging history is user's own visible action. Cache DB is not an authorization database and not a replacement for filesystem recovery journal.

**Performance goals, to be measured.** First useful UI under 2 seconds warm/3 seconds cold without model, cancellation acknowledgement under 500 ms except uninterruptible OS operations, visible progress at 4 Hz max, no >100 ms UI-thread work, stable memory with millions of entries using paged queries and top-k. These are proposed budgets, not measurements. Scanner streaming with bounded channels; parallelism tuned per physical device, not unlimited thread pool. Model idle unloaded or explicitly retained with visible RAM estimate; bulk filesystem facts stay in deterministic engine. Enumerating entire tree, hash-based duplicates and AI inference are separately scheduled jobs. Results show logical/allocated/reclaimable differently; hardlinks dedupe where identity known. Never claim scan sum equals disk's used space.

**UI quality gates.** Keyboard navigation, visible focus, named controls and meaningful live announcements, Narrator, 100/150/200% DPI, high contrast system colors, long paths/Russian labels, reduced motion, empty/loading/partial/error states. Custom treemap needs equivalent accessible table. Pixel-perfect appearance does not establish accessibility. Starter uses system brushes and a virtualised table but has not passed full manual accessibility review.

### Gaps

Privilege broker handshake, production file identity/handle design, trustworthy rule distribution, crash recovery and quarantine transactionality still require implementation/security review. No production SQLite implementation yet. Official accessibility page retrieval failed during research; accessibility items above are design acceptance criteria, not a quoted compliance certification. Model worker details belong to separate model research.

## Как собирать и доводить до выпуска

### Takeaway

В starter уже есть собираемое analysis-only приложение; дальнейшая разработка идёт через проверяемые вертикальные этапы. Для production первая доставка — подписанный обычный Windows installer, UI без admin rights, обновления подписанным installer; автоматическое обновление и MSIX проверять отдельно от функциональности очистки.

### Cited Findings

- `dotnet publish` поддерживает framework-dependent и self-contained; второй включает runtime, но обновление runtime становится частью выпуска приложения. [Publishing .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/), доступ 2026-10-23.
- SignTool входит в Windows SDK, подписывает/проверяет binaries и timestamp; актуальные SDK требуют явных digest parameters. [SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool), доступ 2026-10-23.
- Velopack поддерживает отдельные стадии check/download/apply и требует bootstrap в точке входа; для WPF официально рекомендован custom Main. [Velopack C#](https://docs.velopack.io/getting-started/csharp), доступ 2026-10-23. Это возможный updater, а не внедрённая зависимость starter.
- MSIX elevation использует restricted capability; обычный full trust не следует смешивать с автоматическим повышением прав. [App capabilities](https://learn.microsoft.com/th-th/windows/apps/package-and-deploy/app-capability-declarations), доступ 2026-10-23. Способ доставки нужно доказать spike-тестом в сочетании с broker и политиками Store.

### Inferences

**Release architecture proposal.** Per-machine installer (например WiX MSI, стабильную точную версию зафиксировать после проверки) размещает исполняемые файлы в защищённой папке. Подписываются app/broker/installer; SHA-256 digest и RFC3161 timestamp. Ключ не хранится в Git или обычном PFX на CI-диске: аппаратный/облачный signing provider после проверки доступности. Первый release можно обновлять ручной загрузкой подписанного installer с проверкой publisher. Для auto-update нужны authenticated metadata, артефакт hash/signature, строгая политика publisher/version/channel, запрет незаявленного downgrade, staged rollout, rollback совместимый с DB schema, pause while scans/actions run. Не писать собственный silent updater на скорую руку. Velopack — кандидат для per-user app, но размещение elevated broker и защита от подмены в writable install dir требуют отдельного решения. MSIX/Store — отдельный проверяемый deployment path, не обещание первой версии.

**Предлагаемые build/CI шаги.** Зафиксировать SDK, транзитивные зависимости и tool versions; restore locked mode после появления NuGet, build Release, unit/integration/negative tests, architecture checks на отсутствие Model→Execution ссылок, dependency vulnerability/license report, Windows VM fixtures, signed release, SBOM/checksums, smoke install/update/uninstall, clean-machine launch. CI service identity не получает signing privileges на обычном PR. Destructive tests только на отдельно созданном VHD/VM со snapshot; никакие тесты на пользовательском C: или D:. UI tests and elevated UAC prompts need appropriate interactive Windows VM; ordinary headless CI alone is insufficient.

**Оценка плана, не рыночная статистика и не гарантия сроков.** Предполагается 3–4 опытных инженера, QA 0.5–1 FTE, UX/product 0.25–0.5 FTE, без собственной фундаментальной модели. Calendar intervals overlap, team capacity is finite.

| Этап | Прикидка времени | Проверяемый выход |
|---|---|---|
| 0. Policy/filesystem/model spike | 2–3 недели | threat model, fixture matrix, stack/model ADR, category scope |
| 1. Analysis vertical slice | 4–6 недель | сканирование, карта/table, cancellation, coverage, tests |
| 2. Categories + plan/preview | 4–6 недель | несколько безопасно описанных категорий, typed plan, exclusions, evidence |
| 3. Executor + journal + revalidation | 6–10 недель | crash/failure tests, recovery semantics, safety review; broker лишь по необходимости |
| 4. Local assistant integration | 4–6 недель, частично параллельно | model download, resource profiles, constrained output, no-model fallback |
| 5. Installer, signing, UX QA, beta | 4–8 недель | Windows matrix, battery/disk-full tests, signed beta, support process |

Итого ограниченный, но прилично проверенный первый релиз — ориентир 6–9 месяцев и 24–40 человеко-месяцев всей команды. Полная концепция с broad application catalog, games, duplicates, scheduling, quarantine/history-growth — скорее 12–18 месяцев, 48–84 человеко-месяца, зависит от широты категорий. Не обещать все функции одним MVP. Если заложить условные fully-loaded $4k–$8k на человеко-месяц, бюджет первого релиза 24–40 × $4k–$8k = $96k–$320k; с резервом 25% $120k–$400k. Это формула для планирования, не исследование зарплат и не commercial quote. Модельный CDN, подпись, test hardware, legal/license review и поддержка оплачиваются отдельно, цены не проверялись. Соло-разработка может делать меньший scope, но не сжимает автоматически security/QA в то же число недель.

**Реально созданный starter и верификация.** Локальная среда 2026-10-23: Windows build 10.0.26300, win-x64; SDK 10.0.402; MSBuild 18.9.15; runtime/WindowsDesktop 10.0.13; Node v24.19.0; git 2.55.0.windows.5. Rust/cargo в PATH не найдены. Это результаты команд на машине, не web facts. Root: `D:/Projects/codex/cleaner/starter`.

Реализованы: Core contracts/state transitions; ordinary-user metadata scanner; ограничение 500 000 файлов / 10 000 pending directories / 100 top files / 50 diagnostic examples; throttled progress, cancellation, explicit partial/errors; точные case-sensitive path exclusions; skip ReparsePoint/Offline/RecallOnOpen/RecallOnDataAccess для корня/родителей, dequeued directories и entries; отклонение UNC и DriveType.Network mappings; nullable physical allocation; запрещающая любые мутации policy; disabled assistant stub; WPF UI и standalone smoke harness. Содержимое не читается, модели/SQLite/broker/delete/installer/CI ещё нет. Никакого опасного псевдо-executor.

Команды из `starter`:

```powershell
dotnet restore Cleaner.slnx
dotnet build Cleaner.slnx -c Release --no-restore
dotnet run --project tests/Cleaner.SmokeTests -c Release --no-build
dotnet run --project src/Cleaner.Desktop -c Release --no-build
dotnet publish src/Cleaner.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

Проверено после filesystem-review fixes: build success, 0 warnings/errors; 13 checks passed (totals, exclusions, pre-cancel, limit, allocation unknown, deny mutations/model disabled, session transition, original fixture preserved, независимые deferred flags, ordinary attributes, network drive classification, установка Offline на реальный fixture-корень, отказ такого корня до enumeration); self-contained publish success. WPF XAML smoke создаёт MainWindow без показа окна; первоначальный тестовый bootstrap с `StartupUri=null` упал (exit -532462766, ArgumentNullException); исправлен явной OnStartup логикой, повторный smoke exit 0. Скрытый обычный запуск создал responsive process с заголовком «Cleaner — только анализ», затем тестовое окно корректно закрыто. Это не заменяет ручной UI QA. Финальный republish после cloud/network fixes успешен; опубликованный self-contained executable также прошёл WPF XAML smoke с exit 0.

### Gaps

Starter — работающая точка начала анализа, не production-ready cleaner. Нет доказательства отсутствия race при конкурентной замене каталога на junction; для read-only каркаса это ограничение задокументировано, для destructive executor неприемлемо. Реальные cloud providers и сетевые mappings не проверялись: network classification тест синтетический, Offline-root тест реальный. Отсутствие любых provider/network effects metadata API не гарантируется; deferred flags и root checks уменьшают область, но provider/mount races требуют дополнительных тестов. Нет file IDs/hardlink dedupe/allocated sizes/real recovery. Нет installer/signing/update infrastructure и full automated CI. Нет измерений startup/RAM/scan throughput, нет ручного Narrator/DPI review и performance baseline на миллионах файлов. Стоимость команды, сертификатов и инфраструктуры зависит от региона и организации; предложения сроков нужно перепланировать после spikes.
