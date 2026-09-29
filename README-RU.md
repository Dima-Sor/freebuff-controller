# Freebuff Controller — русская сборка (ветка `ru`)

Форк [Ximmmmmmm/freebuff-controller](https://github.com/Ximmmmmmm/freebuff-controller) на теге `v1.9.7`.
Лицензия MIT, автор оригинала — Ximmmmmmm. Функциональных изменений нет, только локализация и геометрия.

## Что изменено против v1.9.7

- Интерфейс переведён на русский, шрифт `Segoe UI`.
- Главное окно расширено 580 → 660, веса колонок `13/14/40/33` → `20/20/30/30` — колонки «Слот» и «Статус» больше не обрезаются.
- Шрифты приведены к стандартным 9pt.
- Статусы сокращены: `● Вкл` / `○ Выкл`.
- Правило автоответов в `~\.AGENTS.md` пишет русскую версию вместо китайской.

## Сборка

Нужны только Windows, .NET Framework (`csc.exe`) и `python3`:

```
build.bat
FreebuffController.exe --self-test %TEMP%\selftest.txt && type %TEMP%\selftest.txt
```

Ожидается `BUILD OK` и `Всё ок (Всего 29 шт.)`.

## Релизы

Готовый exe публикуется в Releases форка как `v1.9.7-ruN` вместе с `sha512.txt`.
Проверяйте хеш перед запуском:

```
(Get-FileHash FreebuffController.exe -Algorithm SHA512).Hash
```
