# Freebuff Multi-Instance Controller

**[中文](README.zh-CN.md) · [English](README.en.md) · [Русский](README-RU.md)**

Небольшая утилита для Windows, которая позволяет запускать несколько экземпляров настольного приложения [Freebuff](https://www.freebuff.com) одновременно — каждый со своим независимым аккаунтом.

A small Windows utility that lets the Freebuff desktop app run multiple instances simultaneously — each with its own independent account.

## О форке / About this fork

Русская вилка форка [Ximmmmmmm/freebuff-controller](https://github.com/Ximmmmmmm/freebuff-controller) на базе тега `v1.9.7`: интерфейс переведён на русский, окно расширено, шрифты приведены к стандартным. Функционал не изменён.

> Русский перевод и геометрия — доработки этой вилки. Функциональных изменений относительно оригинала нет.

This repository is the Russian fork of [Ximmmmmmm/freebuff-controller](https://github.com/Ximmmmmmm/freebuff-controller), based on tag `v1.9.7`. The binary in **Releases below is the Russian build**; the original Chinese build is published by the upstream author.

## Скачать / Download

Готовый `FreebuffController.exe` и `sha512.txt` для проверки — в [Releases](https://github.com/Dima-Sor/freebuff-controller/releases).

Download the ready-made `FreebuffController.exe` plus `sha512.txt` for verification from [Releases](https://github.com/Dima-Sor/freebuff-controller/releases).

Не хотите компилировать? Та же ссылка. Оригинальная китайская сборка: [upstream Releases](https://github.com/Ximmmmmmm/freebuff-controller/releases).

## Что внутри кратко / At a glance

- **Мультиинстанс** — главный экземпляр плюс слоты 1–9, каждый со своим профилем и аккаунтом.
- **Квота** — Freebucks (остаток за сегодня / всего) по каждому аккаунту, запросы идут параллельно.
- **Общие сессии** — все окна видят одну историю чатов, аккаунты при этом изолированы.
- **Прокси** — автоопределение, приоритет «локальный → системный → прямой», запоминание удачного маршрута.
- **Автоочистка** — удаление старых установочных пакетов и осиротевших файлов в `%TEMP%`.
- **Оффлайн-самотест** — 29 проверок, включая самую рискованную операцию замены файлов.

## Сборка / Build

```
build.bat
FreebuffController.exe --self-test %TEMP%\selftest.txt && type %TEMP%\selftest.txt
```

Ожидается `BUILD OK` и `Всё ок (Всего 29 шт.)`.

## Лицензия / License

MIT — оригинал © Ximmmmmmm, русская вилка © Dima-Sor. Лицензия оригинала сохранена: [LICENSE](LICENSE).

## Сторонний софт / Disclaimer

Чисто сторонний инструмент, не изменяет приложение Freebuff и не связан с официальным Freebuff.

A third-party tool. It does not modify the Freebuff app itself.
