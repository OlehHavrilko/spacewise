# ADR 0006. Версии зависимостей (dependency spike)

- Статус: предложено (по результатам spike)
- Дата: 2026-10-23
- Закрывает: Q14 из §16 ТЗ (`docs/spec/ТЗ Spacewise.md`)
- Источник: `dotnet package search <id> --exact-match --format json` и NuGet registration API (дата публикации, лицензия); код проверки: `spikes/Storage/`

Таблица служит основой для последующего закрепления версий в `Directory.Packages.props`. Сам файл в этом spike не менялся.

| Пакет | Последняя стабильная | Дата | Лицензия | Заметки | Решение |
|---|---|---|---|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | 2026-03-25 | MIT | Уже закреплён, новее нет | Оставить 8.4.2 |
| `Microsoft.Extensions.Hosting` | 10.0.13 | 2026-10-13 | MIT | Уже закреплён, новее нет | Оставить 10.0.13 |
| `xunit.v3` | 4.0.2 (закреплён) | — | Apache-2.0 | В spike не проверялся | Оставить |
| `StreamJsonRpc` | 2.26.150 | 2026-10-19 | MIT | Тянет `MessagePack`, `Nerdbank.MessagePack`, `Nerdbank.Streams`, `Microsoft.VisualStudio.Threading.Only`, `Newtonsoft.Json` | Закрепить 2.26.150. Релизу 4 дня: перед закреплением прогнать IPC smoke; при проблемах откатиться на 2.25.29 |
| `Serilog` | 4.4.0 | 2026-07-10 | Apache-2.0 | — | Закрепить 4.4.0 |
| `Serilog.Extensions.Hosting` | 10.0.0 | 2025-11-28 | Apache-2.0 | Под .NET 10 / M.E.Hosting 10 | Закрепить 10.0.0 |
| `Serilog.Sinks.File` | 7.0.0 | 2025-04-28 | Apache-2.0 | — | Закрепить 7.0.0 |
| `Microsoft.Data.Sqlite.Core` | 10.0.13 | 2026-10-13 | MIT | Без нативного бандла | **Закрепить 10.0.13** (вместо meta-пакета `Microsoft.Data.Sqlite`, см. ADR 0005) |
| `Microsoft.Data.Sqlite` | 10.0.13 | 2026-10-13 | MIT | Транзитивно `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 → SQLite 3.53.3 (≥ 3.51.3, проходит) | Не использовать, если принят ADR 0005; иначе 10.0.13 |
| `SQLite3MC.PCLRaw.bundle` | 2.6.1 | 2026-10-12 | MIT | SQLite 3.54.0 + шифрование; тянет `SQLitePCLRaw.core` 3.0.2 | **Закрепить 2.6.1** (ADR 0005) |
| `SQLitePCLRaw.bundle_e_sqlite3` | 3.0.5 (линия 2.x: 2.1.14) | 2026-07-27 | Apache-2.0 | 3.0.5 → пакет `SQLite` 3.53.4; 2.1.14 → SQLite 3.54.0; оба работают с `Microsoft.Data.Sqlite.Core` 10.0.13 | Запасной вариант без шифрования: 2.1.14 |
| `SQLitePCLRaw.bundle_e_sqlcipher` | 2.1.11 | 2025-03-07 | Apache-2.0 | SQLite **3.39.2** (< 3.51.3), SQLCipher 4.5.2 community, в 3.x не выпускается | Отклонить |
| `SQLitePCLRaw.bundle_e_sqlite3mc` | 2.1.11 | — | Apache-2.0 | Старое имя, вытеснено `SQLite3MC.PCLRaw.bundle` | Не использовать |
| `System.Security.Cryptography.ProtectedData` | 10.0.13 | 2026-10-13 | MIT | Нужен для обёртки ключа БД (ADR 0005) | Закрепить 10.0.13 |

## Проверка SQLite ≥ 3.51.3

Все проверенные бандлы, кроме `bundle_e_sqlcipher`, проходят требование ТЗ. Во всех WAL включается (`journal_mode=WAL` → `wal`), `synchronous=FULL` читается как `2`. Подробности в ADR 0005, раздел «Измерения».

## Что сделать при закреплении

- Добавить в `Directory.Packages.props` версии из столбца «Решение».
- Добавить smoke-тест в `tests/`: `select sqlite_version()` ≥ 3.51.3, `pragma journal_mode=WAL` → `wal`, шифрование включено. Тест ловит тихую подмену нативной библиотеки транзитивной зависимостью.
- `StreamJsonRpc` 2.26.150 вышел 4 дня назад. Перед закреплением проверить release notes и известные проблемы.
