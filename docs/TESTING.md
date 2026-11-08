# Проверка TypeSwitcher

## Среда

Windows x64, .NET SDK из `global.json`, включая Windows Desktop Runtime 10.
Публикация self-contained содержит runtime.
Версии NuGet закреплены в `packages.lock.json` приложения и тестов.

## Автоматические проверки

Локализация и темы: [LOCALIZATION-THEMES.md](LOCALIZATION-THEMES.md).
Результаты автоматической сборки и тестов доступны в GitHub Actions.

```powershell
dotnet restore TypeSwitcher.sln --locked-mode
dotnet build TypeSwitcher.sln -c Release --no-restore -warnaserror
dotnet test TypeSwitcher.sln -c Release --no-build --logger trx --collect:"XPlat Code Coverage"
```

Проверяются:

- исходный RU/EN golden-набор, точный регистр и тип действия;
- UK-словарь, словоформы, опечатки, смешанные алфавиты и неоднозначность;
- полный путь клавиш на FakeKeyboard: границы, знаки, AltGr, Caps Lock, три языка,
  откат конкретного HKL и разделителя, восстановление после переполнения;
- исключения полей паролей, командные сочетания, неизвестные раскладки;
- импорт Hunspell/слов/частот, ошибочные файлы, отмена, хеши, пути и удаление;
- миграция 1.1.2, будущая/повреждённая схема, атомарная запись, области списков;
- построение и layout всех страниц WPF на STA-потоке.

Лингвистические тесты используют фиксированный TimeProvider, чтобы покрытие кода и
нагрузка CI не меняли ожидания. Отдельный тест проверяет отказ при исчерпании бюджета.
Опубликованный `--eval` работает с реальными часами и печатает p50/p95/max времени анализа.
UI smoke не показывает окна и не устанавливает хуки; FakeKeyboard не подтверждает
поведение конкретного редактора Windows.

## Проверка опубликованной программы

```powershell
dotnet publish src/TypeSwitcher/TypeSwitcher.csproj -c Release -o dist/TypeSwitcher -p:RestoreLockedMode=true
powershell -NoProfile -ExecutionPolicy RemoteSigned -File tools/verify-package.ps1 -Directory dist/TypeSwitcher -Reports artifacts/reports
powershell -NoProfile -ExecutionPolicy RemoteSigned -File tools/package.ps1
```

Скрипт сверяет комплектность и хеши UK-данных, копирует приложение во временную папку
с пробелами без пользовательской data/, запускает `--selftest`, `--eval --language all`
и `--check`. Затем удаляет только свою временную копию. Архив исключает data/ и PDB.

Дополнительные команды:

```text
TypeSwitcher.exe --eval --language uk
TypeSwitcher.exe --check привіт привт --language uk
TypeSwitcher.exe --settings 3 --offscreen
TypeSwitcher.exe --dictionary-settings --offscreen
```

Отчёты: `%TEMP%/typeswitcher_selftest.txt`, `typeswitcher_eval.txt`, `typeswitcher_word_probe.txt`.
`--selftest` использует фиксированный RU/EN-профиль 1.1.2. `--eval all` проверяет RU/EN,
UK/EN и трёхъязычную неоднозначность. Проверки зависят от выбранных словарей/порогов;
для сравнения релизов используются чистые настройки.

## Ручная приёмка перед стабильным релизом

Часть этой программы автоматизирована отдельным интерактивным стендом.
Каждый этап сохраняет результаты в Markdown и JSON в указанной папке отчётов.

```powershell
dotnet run --project tools/TypeSwitcher.Acceptance -c Release -- --app dist/TypeSwitcher/TypeSwitcher.exe --phase ui --report artifacts/acceptance/ui
dotnet run --project tools/TypeSwitcher.Acceptance -c Release -- --app dist/TypeSwitcher/TypeSwitcher.exe --phase native --report artifacts/acceptance/native
dotnet run --project tools/TypeSwitcher.Acceptance -c Release -- --app dist/TypeSwitcher/TypeSwitcher.exe --phase browser --report artifacts/acceptance/browser
dotnet run --project tools/TypeSwitcher.Acceptance -c Release -- --app dist/TypeSwitcher/TypeSwitcher.exe --phase corpus --report artifacts/acceptance/corpus
dotnet run --project tools/TypeSwitcher.Acceptance -c Release -- --app dist/TypeSwitcher/TypeSwitcher.exe --phase dpi --report artifacts/acceptance/dpi
dotnet run --project tools/TypeSwitcher.Acceptance -c Release -- --phase summary --report artifacts/acceptance
dotnet run --project tools/TypeSwitcher.Acceptance -c Release -- --app dist/TypeSwitcher/TypeSwitcher.exe --phase appearance --report artifacts/acceptance/appearance
```

Нужен интерактивный рабочий стол Windows; для browser — Edge. Native/browser открывают
собственные тестовые окна и временно переключают фокус. Перед отправкой ввода проверяется
владелец активного окна; при потере фокуса проверка прекращается. Browser использует
отдельный профиль и локальную HTML-страницу. Можно задать `--work-root` для временных
копий. Они сохраняются для разбора результатов. UI-прогон не использует пользовательскую data/.

`--low-integrity-probe` — внутренний режим отдельного helper-процесса: он понижает только
свой токен для проверки относительного integrity-гейта. Стенд не отключает LLKHF_INJECTED.

На Windows-стенде с физической клавиатурой проверить:

1. EN/RU, EN/UK, RU/UK и все три языка; украинскую обычную и расширенную раскладки.
2. Набор `привіт`, `ґанок` (включая AltGr), апострофы, опечатки, смену регистра и Pause.
3. Блокнот с отключённой собственной автозаменой, браузерные поля, редактор кода,
   мессенджер и используемые пользователями приложения.
4. Клики, стрелки, смену окна, Ctrl/Alt/Win-команды, парольные поля и повышенные процессы.
5. Импорт/отключение/обновление словаря во время набора и после перезапуска.
6. Отображение при DPI 100/150/200%, клавиатурную навигацию, несколько мониторов.
7. Обновление реальной копии 1.1.2 с резервной data/, сохранность списков и откат.
8. Продолжительный ввод и отсутствие снятия хуков; показатели задержек на эталонном ПК.

FlaUI/SendInput ввод помечается инъецированным и отбрасывается рабочим клавиатурным
хуком. Поэтому такие UI-тесты не заменяют физический сквозной прогон.
Лингвистическое качество дополнительно проверяется на реальном вводе носителями языка.
