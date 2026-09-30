# Freebuff Multi-Instance Controller

**[中文](README.zh-CN.md) · [English](README.en.md) · [Русский](README-RU.md)**

Небольшая утилита для Windows, которая позволяет запускать несколько экземпляров настольного приложения [Freebuff](https://www.freebuff.com) одновременно — каждый со своим независимым аккаунтом.

A small Windows utility that lets the Freebuff desktop app run multiple instances simultaneously — each with its own independent account.

一个 Windows 桌面小工具：让 [Freebuff](https://www.freebuff.com) 桌面版支持**多开**，并且**每个窗口可以登录不同的账号**。

> Русская вилка форка [Ximmmmmmm/freebuff-controller](https://github.com/Ximmmmmmm/freebuff-controller) на базе тега `v1.9.7`: интерфейс переведён на русский, окно расширено, шрифты приведены к стандартным. Функционал не изменён.

> Русский перевод и геометрия — доработки этой вилки. Функциональных изменений относительно оригинала нет.

> 本仓库是 [Ximmmmmmm/freebuff-controller](https://github.com/Ximmmmmmm/freebuff-controller) 的俄语分支，基于 `v1.9.7`，仅做界面本地化。

## Что внутри кратко / At a glance

- **Мультиинстанс** — главный экземпляр плюс слоты 1–9, каждый со своим профилем и аккаунтом.
- **Квота** — Freebucks (остаток за сегодня / всего) по каждому аккаунту, запросы идут параллельно.
- **Общие сессии** — все окна видят одну историю чатов, аккаунты при этом изолированы.
- **Прокси** — автоопределение, приоритет «локальный → системный → прямой», запоминание удачного маршрута.
- **Автоочистка** — удаление старых установочных пакетов и осиротевших файлов в `%TEMP%`.
- **Оффлайн-самотест** — 29 проверок, включая самую рискованную операцию замены файлов.

## Скачать / Download

[Releases](https://github.com/Dima-Sor/freebuff-controller/releases) — готовый `FreebuffController.exe` и `sha512.txt` для проверки.

Не хотите компилировать? [Releases](https://github.com/Dima-Sor/freebuff-controller/releases) · [Releases](https://github.com/Ximmmmmmm/freebuff-controller/releases)

## Лицензия / License

MIT — оригинал © Ximmmmmmm, русская вилка © Dima-Sor. Лицензия оригинала сохранена: [LICENSE](LICENSE).

## Сторонний софт / Disclaimer

Чисто сторонний инструмент, не изменяет приложение Freebuff и не связан с официальным Freebuff.

A third-party tool. It does not modify the Freebuff app itself.
