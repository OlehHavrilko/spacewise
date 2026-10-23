# ADR 0004. Toast-уведомления из unpackaged WPF-приложения на .NET 10

- Статус: предварительно принято. API и регистрация проверены, **сам показ toast на экране не подтверждён**: у пользователя машины проверки уведомления Windows выключены глобально
- Дата: 2026-10-23
- Закрывает: Q9 из §16 ТЗ (`docs/spec/ТЗ Spacewise.md`), частично
- Код spike: `spikes/Toast/Raw/`, `spikes/Toast/Toolkit/`, `spikes/Toast/WinAppSdk/` (консольные проекты с `UseWPF`, в `Spacewise.slnx` не входят)

## Контекст

ТЗ (раздел про уведомления, FR-UI-003) разрешает не больше одного toast за запуск задачи и только при значимом событии. Пример: «Обслуживание выполнено: освобождено 6,2 ГБ на C:». Кнопки только навигационные («Открыть отчёт», «Отключить автопилот»). Процесс обслуживания после toast завершается, поэтому клик по toast должен уметь запустить Spacewise заново. Spacewise — unpackaged WPF, без MSIX и без package identity. Механизм (AUMID через ярлык в «Пуске» или `AppNotificationManager`) помечен **[не верифицировано]**.

Машина проверки:

| Параметр | Значение |
|---|---|
| ОС | Windows 10 Pro (по `ProductName`), `DisplayVersion` 26H2, build **26300.9711** (канал Insider) |
| .NET | SDK 10.0.402 |
| Windows App Runtime | установлены `Microsoft.WindowsAppRuntime.2` 2.5.1.0 (x64/x86), 1.7, 1.8 |
| Настройка уведомлений | `HKCU\...\PushNotifications\ToastEnabled = 0`: пользователь выключил все уведомления. Spike эту настройку **не менял** |

Версии пакетов по `dotnet package search --exact-match` на 2026-10-23:

| Пакет | Последняя стабильная | Примечание |
|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.5.1 | есть 2.5.4-experimental |
| `Microsoft.Toolkit.Uwp.Notifications` | 7.1.3 | пакет не развивается, в Windows Community Toolkit 8 уведомлений нет |
| `CommunityToolkit.WinUI.Notifications` | 7.1.2 | то же семейство 7.x |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.28000.2705 | для raw-варианта не нужен |

## Что проверено

Текст во всех вариантах: «Spacewise spike: тестовое уведомление».

| Вариант | Сборка | Регистрация | `Setting` | Результат `Show` | Чистка |
|---|---|---|---|---|---|
| Raw `Windows.UI.Notifications`, TFM `net10.0-windows10.0.19041.0`, только `SetCurrentProcessExplicitAppUserModelID`, без регистрации (`ToastRaw.exe noreg`) | 0 предупреждений, без пакетов, вывод 24,4 МБ (из них ~24 МБ `Microsoft.Windows.SDK.NET.dll`) | нет | `DisabledForUser` | `Show()` без исключения; событие `Failed` с `0x803E0114` (`WPN_E_NOTIFICATION_TYPE_DISABLED`); в истории 0 | `History.Clear` |
| Raw + временный ключ `HKCU\Software\Classes\AppUserModelId\Spacewise.Spike.Toast` (`DisplayName`) (`ToastRaw.exe hkcu`) | то же | 1 ключ HKCU | `DisabledForUser` | как выше, `Failed 0x803E0114` | ключ удалён кодом, проверено |
| `Microsoft.Toolkit.Uwp.Notifications` 7.1.3, `ToastContentBuilder().Show()` | 0 предупреждений, 0 уязвимых пакетов, вывод 24,6 МБ | автоматически, см. ниже | `DisabledForUser` | `Show()` без исключения; в истории 0 | `ToastNotificationManagerCompat.Uninstall()` удалил всё |
| WinAppSDK 2.5.1 `AppNotificationManager`, `WindowsPackageType=None`, **self-contained** (`WindowsAppSDKSelfContained=true`) | 0 предупреждений, вывод 151,4 МБ | `Register()` упал, но **оставил** 3 ключа HKCU | — | `Register()` → `COMException 0x8007007E` («Не найден указанный модуль», «Unable to load resource dll. Microsoft.WindowsAppRuntime.Insights.Resource.dll»), `IsSupported()=True` | ключи удалены вручную (см. ниже) |
| WinAppSDK 2.5.1, unpackaged, **framework-dependent** (используется установленный Runtime 2.5.1) | 0 предупреждений | `Register()` OK | `DisabledForUser` | `Show()` OK, `Id=5052`; `GetAllAsync` 0 | `UnregisterAll()` удалил 2 из 3 ключей, третий удалён вручную |

Во всех рабочих вариантах API отвечает корректно: `Setting = DisabledForUser` совпадает с глобальным выключателем. Платформа отбрасывает toast с `WPN_E_NOTIFICATION_TYPE_DISABLED`. Увидеть toast на экране и проверить клик (`Activated`, `OnActivated`, `NotificationInvoked`) нельзя, пока пользователь не включит уведомления.

### Что создаётся в HKCU (записано по снимкам до/после)

Снимок: подключи `HKCU\Software\Classes\AppUserModelId`, `HKCU\Software\Classes\CLSID`, `HKCU\Software\Classes\AppID`, `HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings`, ярлыки `%APPDATA%\Microsoft\Windows\Start Menu\Programs`, папка `%LOCALAPPDATA%\ToastNotificationManagerCompat`.

| Вариант | Создано | Удалено |
|---|---|---|
| Raw `hkcu` | `HKCU\Software\Classes\AppUserModelId\Spacewise.Spike.Toast` (`DisplayName`) | кодом spike, `DeleteSubKeyTree` |
| Toolkit 7.1.3 | `HKCU\Software\Classes\AppUserModelId\D:/Projects/.../ToastToolkit.exe` (`DisplayName`, `IconUri`, `IconBackgroundColor`, `CustomActivator`, `Has7.0.1Fix`, `HasSentNotification`); `HKCU\Software\Classes\CLSID\{f456cb1e-dc32-3858-7668-cfaad6c725b1}\LocalServer32` = `"...ToastToolkit.exe" -ToastActivated`; файл `%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\F456CB1E-...\Icon.png` | `Uninstall()`, снимок после совпал с исходным |
| WinAppSDK (оба запуска, тот же путь exe) | `HKCU\Software\Classes\AppUserModelId\{2EADDB53-D731-492D-9AE4-8F15638DA622}` (`DisplayName`, `CustomActivator`); `HKCU\Software\Classes\CLSID\{55ADD2CE-64AF-423D-8B1D-F153B2C987E4}\LocalServer32`; `HKCU\Software\Classes\AppUserModelId\D:.Projects.codex.cleaner.spikes.Toast.WinAppSdk.bin.Release....ToastWinAppSdk.exe` (`NotificationGUID`) | первые два — `UnregisterAll()`; третий `UnregisterAll()` **не удалил**, удалён вручную (`Remove-Item`) |

После всех прогонов снимок совпадает с исходным. HKLM не трогали, ярлыков в «Пуске» не создавали, настройку уведомлений не меняли.

## Решение

1. Toast показывается через **raw `Windows.UI.Notifications`** (WinRT-проекция из TFM `net10.0-windows10.0.19041.0`), без сторонних пакетов.
2. Identity задаёт собственная регистрация в HKCU, без ярлыка в «Пуске». `HKCU\Software\Classes\AppUserModelId\Spacewise` со значениями `DisplayName` и `IconUri`. Создаётся при первом запуске (идемпотентно) и удаляется деинсталлятором или командой `--unregister`. AUMID фиксированный, не зависит от пути exe: Toolkit и WinAppSDK строят его из пути, и при переносе папки остаётся мусор.
3. Навигационные кнопки и клик по toast используют **protocol activation** (`activationType="protocol"`, `spacewise:report?id=...`) с регистрацией `HKCU\Software\Classes\spacewise`. Пока процесс жив, используется событие `Activated`. COM-активатор (`CustomActivator` + `LocalServer32`) не нужен.
4. Код toast вынесен в отдельную небольшую сборку/worker с TFM `...windows10.0.19041.0`. Тогда основной WPF-проект не тянет 24 МБ `Microsoft.Windows.SDK.NET.dll`, если это окажется критичным для размера.
5. Перед показом проверяется `notifier.Setting`. При `DisabledForUser`/`DisabledForApplication` toast не отправляется, событие пишется в журнал отчёта (результат всё равно виден в «Истории»).
6. Запасной вариант: `Microsoft.Toolkit.Uwp.Notifications` 7.1.3. Он рабочий на .NET 10 и сам делает COM-активацию, но пакет не развивается. WinAppSDK `AppNotificationManager` отклонён для v1: self-contained не заработал (0x8007007E), framework-dependent требует установленный Windows App Runtime, self-contained добавляет ~150 МБ, при ошибке `Register()` и в `UnregisterAll()` в HKCU остаётся мусор.

## Последствия

- Без новых NuGet-зависимостей. Нужен TFM с версией Windows SDK, хотя бы для одной сборки.
- Spacewise пишет в HKCU 2 ключа (AUMID и протокол). Их надо описать в документации деинсталляции и удалять.
- Пока `ToastEnabled = 0` или для Spacewise выключены уведомления, toast не показывается. UI не должен на него полагаться: итог обслуживания всегда есть в отчёте.
- Обработчик `spacewise:` URI — новая поверхность ввода. Параметры только навигационные, валидируются белым списком.

## Что не проверено

- **Фактический показ toast на экране и клик по нему.** Блокер: глобально выключенные уведомления у пользователя. Для проверки надо временно включить «Параметры → Система → Уведомления» и запустить `spikes/Toast/Raw` (`ToastRaw.exe hkcu`) и `spikes/Toast/Toolkit`.
- Показывается ли toast с AUMID **без** регистрации в HKCU (режим `noreg`). На этой машине неотличимо из-за глобального запрета.
- Protocol activation из toast для unpackaged AUMID (решение п. 3) — не запускалось.
- Вариант с ярлыком в «Пуске» (`System.AppUserModel.ID` + `ToastActivatorCLSID`) — не проверялся, решение его не требует.
- Windows 10 22H2, Windows 11 24H2 / 25H2: проверена только build 26300. На 22H2 надо отдельно проверить регистрацию через `AppUserModelId` без ярлыка.
- Почему WinAppSDK 2.5.1 self-contained не нашёл модуль (возможно, нужен `Microsoft.WindowsAppRuntime.Insights.Resource.dll` или иной компонент). Не расследовалось, так как вариант отклонён.
- Показ из задачи Планировщика / процесса без окна в интерактивной сессии; режим «Не беспокоить».
