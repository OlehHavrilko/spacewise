# Spacewise

Spacewise — настольное приложение для Windows 10/11, которое показывает, чем занят диск, анализирует, какими программами человек реально пользуется, и освобождает место с понятными последствиями. Освобождение измеряется на томе, а не суммой размеров найденных файлов. Анализ и локальный ИИ работают на компьютере; когда окно закрыто, процессов Spacewise нет.

**Текущее состояние:** MVP-каркас (.NET 10, WPF): анализ метаданных, оценка по каталогу, подбор безопасных категорий под цель освобождения места и прямое необратимое удаление после подтверждения. Ранее помещённые в карантин файлы можно вернуть или удалить вручную; автоматически они не очищаются. Есть необязательная интеграция локальной Qwen 3.5 2B: загрузка с проверкой SHA-256 и отдельный CPU-процесс для объяснения категорий. Веса модели загружаются отдельно по согласию; реальный вывод модели ещё не проверен. Статус по этапам — в [docs/ROADMAP.md](docs/ROADMAP.md).

## Документы

| Файл | Что это |
|---|---|
| [docs/ROADMAP.md](docs/ROADMAP.md) | Дорожная карта от каркаса до выпуска с галочками выполненных шагов. |
| [docs/spec/Решения и сравнение подходов.md](docs/spec/Решения%20и%20сравнение%20подходов.md) | Итоговые решения: сравнение подходов Codex и Claude, дополнения к ТЗ, план и следующие задачи. **Начинать отсюда.** |
| [docs/spec/ТЗ Spacewise.md](docs/spec/ТЗ%20Spacewise.md) | Полное техническое задание: функции, аналитика, ИИ, архитектура, данные, безопасность, интерфейс, поставка, тесты, этапы |
| [docs/concept/](docs/concept) | Исходная концепция владельца, схема пути пользователя, HTML-прототип дизайна |
| [docs/research/](docs/research) | Исследования: `codex/` и `claude/` |
| [docs/adr/](docs/adr) | Архитектурные решения по итогам spike-ов спринта 0 (код проб — в `spikes/`, вне решения) |
| [docs/dev/starter-history_RU.md](docs/dev/starter-history_RU.md) | История и ограничения каркаса (пути внутри — по прежней структуре `starter/`) |

## Сборка и проверка

Windows x64, .NET SDK 10.0.402 (закреплён в `global.json`). Из корня репозитория:

```powershell
dotnet build Spacewise.slnx -c Release
dotnet test --solution Spacewise.slnx -c Release --no-build
dotnet run --project src/Spacewise.Desktop -c Release --no-build
```

Тесты идут через Microsoft.Testing.Platform (включён в `global.json`). `Spacewise.Tests.Fs` по умолчанию создаёт фикстуры в `%TEMP%`. Чтобы гонять их на отдельном томе NTFS, смонтируйте VHDX из PowerShell от имени администратора и укажите корень:

```powershell
tests/Spacewise.Tests.Fs/Mount-NtfsFixture.ps1 -Path D:\fixtures\spacewise.vhdx
$env:SPACEWISE_FS_ROOT = 'X:\'   # буква из вывода скрипта
dotnet test --project tests/Spacewise.Tests.Fs -c Release
tests/Spacewise.Tests.Fs/Mount-NtfsFixture.ps1 -Path D:\fixtures\spacewise.vhdx -Dismount
```

Тесты NTFS (allocated size, FileId, hard link, sparse) пропускаются, если том фикстур не NTFS.

## Структура

```text
Spacewise.slnx, global.json, Directory.Build.props, Directory.Packages.props
src/Spacewise.Core/          контракты, запрещающая политика, защищённые пути, IScanService, форматирование; без Win32
src/Spacewise.Catalog/       встроенный каталог правил очистки, загрузчик с проверкой, glob, preview без удаления
src/Spacewise.Data/          SQLite3MC: cache.db/journal.db, миграции с бэкапом, единый writer, ScanStore
src/Spacewise.Platform.Windows/  сканер метаданных, P/Invoke (allocated size, FileId, links), ключ БД под DPAPI, Restart Manager
src/Spacewise.Inference.Contracts/  установка закреплённой модели и проверка ответов без зависимости от runtime
src/Spacewise.Inference/     отдельный CPU-процесс LLamaSharp; веса модели загружаются отдельно
src/Spacewise.Desktop/       окно WPF: анализ, очистка и помощник, Generic Host, MVVM
tests/Spacewise.Tests.Unit/  xUnit v3: форматирование, политика и защищённые пути, ScanService, хранилище, каталог и fixture правил, архитектурные правила
tests/Spacewise.Tests.Fs/    xUnit v3: сканер на реальной ФС, NTFS-фикстуры, скрипт VHDX
docs/                        концепция, ТЗ, решения, исследования
```

## История

Тег `baseline-codex` — исходный каркас и ТЗ от Codex. Тег `claude-spec-original` — ТЗ Claude до слияния. Ветка `claude/spec` сохранена.

## Лицензия

MIT, см. [LICENSE](LICENSE). Сторонние компоненты и лицензии: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
