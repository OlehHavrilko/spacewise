# Spacewise

Spacewise — настольное приложение для Windows 10/11, которое показывает, чем занят диск, анализирует, какими программами человек реально пользуется, и освобождает место с понятными последствиями. Освобождение измеряется на томе, а не суммой размеров найденных файлов. Анализ и локальный ИИ работают на компьютере; когда окно закрыто, процессов Spacewise нет.

**Текущее состояние:** проверяемый каркас только для анализа (.NET 10, WPF). Удаления и модели пока нет — это намеренно, см. план в [docs/spec](docs/spec).

## Документы

| Файл | Что это |
|---|---|
| [docs/spec/Решения и сравнение подходов.md](docs/spec/Решения%20и%20сравнение%20подходов.md) | Итоговые решения: сравнение подходов Codex и Claude, дополнения к ТЗ, план и следующие задачи. **Начинать отсюда.** |
| [docs/spec/ТЗ Spacewise.md](docs/spec/ТЗ%20Spacewise.md) | Полное техническое задание: функции, аналитика, ИИ, архитектура, данные, безопасность, интерфейс, поставка, тесты, этапы |
| [docs/concept/](docs/concept) | Исходная концепция владельца, схема пути пользователя, HTML-прототип дизайна |
| [docs/research/](docs/research) | Исследования: `codex/` и `claude/` |
| [docs/dev/starter-history_RU.md](docs/dev/starter-history_RU.md) | История и ограничения каркаса (пути внутри — по прежней структуре `starter/`) |

## Сборка и проверка

Windows x64, .NET SDK 10.0.402 (закреплён в `global.json`). Из корня репозитория:

```powershell
dotnet build Spacewise.slnx -c Release
dotnet run --project tests/Spacewise.SmokeTests -c Release --no-build
dotnet run --project tests/Spacewise.SmokeTests -c Release --no-build -- --regression
dotnet run --project src/Spacewise.Desktop -c Release --no-build
```

## Структура

```text
Spacewise.slnx, global.json, Directory.Build.props
src/Spacewise.Core/          контракты, сканер метаданных, запрещающая политика
src/Spacewise.Desktop/       окно WPF (только анализ)
tests/Spacewise.SmokeTests/  самостоятельный harness: smoke и --regression
docs/                        концепция, ТЗ, решения, исследования
```

## История

Тег `baseline-codex` — исходный каркас и ТЗ от Codex. Тег `claude-spec-original` — ТЗ Claude до слияния. Ветка `claude/spec` сохранена.
