# Словари поставки

## Украинский Hunspell

- ВЕСУМ / BrUK, Andriy Rysin, Vasyl Starko и соавторы.
- Версия: 6.8.6, вариант nolat (без латинских токенов).
- Источник: https://github.com/brown-uk/dict_uk/releases/tag/v6.8.6
- Лицензия Hunspell-производной: MPL 1.1, полный текст в `uk_UA.LICENSE`.
- Архив: `hunspell-uk_UA_6.8.6_nolat.zip`.
- SHA256 архива: `7648eb09e6a7d442090bdd2a396898facef048456e316d4c4df5beb19becd4aa`.
- SHA256 uk_UA.dic: `b73062ada50324d2575121c9734c18789d67eb4502af6ae12ed62def7d0528b4`.
- SHA256 uk_UA.aff: `3222e8d5f88fd1db291a74c59e8fc5541bb2cc3d3ac29c6882df4f3dbb051a32`.

## Украинские частоты

- FrequencyWords, Hermit Dave, данные OpenSubtitles 2018, первые 50 000 записей.
- https://github.com/hermitdave/FrequencyWords/blob/f8a65e6ddc17e0baa2e366a909986798d8dbe55b/content/2018/uk/uk_50k.txt
- Лицензия данных: CC BY-SA 4.0, https://creativecommons.org/licenses/by-sa/4.0/
- Файл скопирован без преобразований как `uk-50k.txt`.
- SHA256: `881686fe66a168b79f6a9ee2f6cbbf944cfb12bfb7040913ef535cb29bd86ec9`.

Повторное получение: `tools/update-uk-dictionary.ps1` проверяет контрольные суммы.
Словари RU/EN и их лицензии описаны в README.md и `hunspell/*.LICENSE`.
