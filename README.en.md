# Freebuff Multi-Instance Controller

**[中文](README.zh-CN.md) · [English](README.en.md) · [Русский](README-RU.md)**

A small Windows utility that lets the [Freebuff](https://www.freebuff.com) desktop app run multiple instances simultaneously — each with its own independent account.

> A third-party tool. It does not modify the Freebuff app itself.

## What this fork changes

Branch `ru` is a fork of the original at tag `v1.9.7`. No functional changes — localization and layout only:

- The whole UI is translated into Russian, font is `Segoe UI`.
- Main window widened 580 → 660 px, column weights `13/14/40/33` → `20/20/30/30` so the "Slot" and "Status" columns are no longer clipped.
- Fonts normalized to the standard 9 pt.
- Statuses shortened to `● On` / `○ Off`.
- The language rule written to `~\.AGENTS.md` is in Russian, so the assistant answers in Russian regardless of the prompt language.
- The Chinese localization pack (`hanhua/`) is not shipped. The auto-restore logic is still in the code, but without a `hanhua/` directory it silently does nothing.

## Features

- **Multi-instance**: main instance plus slots 1–9, all independent. A second double-click on the shortcut doesn't say "already running" — it just brings the running window to the front.
- **Multi-account**: every instance has its own Chromium profile and login state, so each window can use a different account; "Reset account" switches it.
- **Shared sessions** (on by default): all slots read one chat history — when account A runs out of quota, account B's window opens the same history and keeps talking, no copying or picking sessions.
- **Quota display**: per-account **Freebucks** — today's remaining and total limit; hovering shows wallet balance and unit price. Since v1.9.6 accounts are fetched **concurrently** (the quota endpoint is slow, sequential requests add up to minutes); **with no proxy the whole round is skipped** — it never silently connects directly.
- **Russian replies by default**: on launch a language rule is written to `~\.AGENTS.md` (with anti-injection clauses), so the assistant answers in Russian whatever language it is asked in.
- **Network and proxy**: update checks, package downloads and quota queries go through "local proxy → system proxy → direct", remembering the route that worked. Common local proxy ports are detected by probing them functionally, so a dead proxy is caught too. "Proxy" in the window header lets you change the address, disable it, or leave it empty for auto-detection; a bubble points out which slot and which address dropped.
- **Almost no buttons**: only "Delete sessions" and "Proxy" on top, "Exit" in the tray — localization, updates, cleanup and quota are all automatic.
- **Stop is polite first**: the slot is asked to exit on its own (up to 2 seconds, so SQLite can close), then the process is killed together with its children.
- **Failure log**: errors on critical paths (file replacement, killing a process, writing `state.json`, creating the junction, raising the window) are appended to `%TEMP%\freebuff-controller-log.txt` — no UI noise, but there is evidence when "double-click did nothing".
- **Delete sessions**: permanently remove a session and its full history (Freebuff itself only offers "archive").
- **Automatic package cleanup**: electron-updater's cache and orphaned packages in `%TEMP%` are cleaned up automatically.
- **Update check**: shows the installed Freebuff version and offers the official installer when a new one exists (only runs with a valid SHA512). The controller can also update itself from this repository's Releases.
- **UI**: clean white design since v1.9.0 with subtle animations; single-file exe (~195 KB, no runtime dependencies), minimizes to the tray.

## Usage

1. Run `FreebuffController.exe` from [Releases](https://github.com/Dima-Sor/freebuff-controller/releases), or build it yourself — see below.
2. Double-click an uninitialized slot (or select it and press "Launch") and choose "Fresh login" in the dialog.
3. Log in from the Freebuff window that opens — the login state is bound to that slot.
4. Switching accounts: select a slot → "Reset account" → start logging in again.
5. Shared sessions are on by default: you're asked once, after that every window shows the same history.

## How it works

Freebuff is an Electron app that locks itself to a single instance via `requestSingleInstanceLock()`. This tool **touches no application files**; it gives every instance its own data directory instead:

- `--user-data-dir=<APPDATA>\Freebuff-slot-N` — separate Chromium profile and instance lock.
- `FREEBUFF_DESKTOP_STATE_PATH=<user>\.config\freebuff-desktop\slots\slot-N\state.json` — separate orchestrator state (avoids its SQLite file lock).
- `~\.config\freebuff-desktop\slots\slot-N\projects\<workspace>\desktop-v2.db` — local session store.

**How session sharing works**: each slot's `projects` directory is replaced with a junction to the main instance's session store (`mklink /J`, no administrator rights needed; since v1.8.30 a native `DeviceIoControl` call is preferred and `mklink` is only the fallback). So every instance reads and writes the same `desktop-v2.db` and the chat history is shared out of the box, while logins stay in their own `state.json` files, keeping accounts independent. On first enable, existing history is merged into the main store and the old directory is kept as `projects.pre-share-*` for manual rollback.

Freebuff app updates therefore don't break this tool.

## Localization

Freebuff updates overwrite the localization patch, and the controller **re-applies it automatically** in five situations: the installed version changed, you opened the controller, you pressed "Launch", a local `bash build.sh` finished, or a pack matching your version was fetched. Before touching anything it verifies that the installed copy is English or the pack is newer, that `targetVersion` matches the installed version, and that no slot is running; otherwise the step is skipped silently, and a run blocked by "an instance is running" is retried after 10 seconds.

File replacement **lands the file completely first, then swaps it in**: the whole thing is copied to `ui.new-*` → the resources referenced by `index.html` are verified → two same-level renames → the old copy is deleted. The installed directory always holds either the complete old or the complete new copy — never a half state where the controller thinks it applied but the screen is blank. If the installed copy is judged incomplete, it is reinstalled.

Only the two most recent complete backups of the English version are kept (partially written ones are removed too), cleaned after each application and 1.5 seconds after startup. The window has no standing status line — events and progress appear as one line under the buttons and clear after 8 seconds; standing information lives in the tray tooltip.

Localization directory lookup order: `hanhua/` next to the exe or one level up → `freebuff-zh/` next to it or one level up → a manually stored path in `%APPDATA%\FreebuffController\hanhua-path.txt`.

Offline verification (restores English temporarily and checks recovery; close Freebuff completely first): `bash tools/verify-autorestore.sh`

> The Russian build does not ship the `hanhua/` pack, so this section describes upstream behaviour and does nothing by default.

## Updater cache and `%TEMP%` cleanup

The official electron-updater accumulates installers in `%LOCALAPPDATA%\@codebufffreebuff-desktop-updater\` (around 300 MB in practice), and packages downloaded by the controller itself land in `%TEMP%` (ones that were never installed become orphans). Both places are swept, and **there is a single rule: a package may be deleted only if its version is not newer than the installed one**.

- Once **on startup**, once **after every Freebuff update install** (re-checked after 20 seconds, installers often still hold the file), and every **30 minutes** as a backstop.
- **Pending updates are never touched**: packages newer than the installed version and the one recorded in `pending-installer.txt` stay; `.exe` files whose version can't be read are left alone too.
- The root `current.blockmap` is always kept (needed for differential downloads); if it can't be deleted (in use / no permission) we try next time, and it never affects the app.
- After cleanup the line under the buttons reports how much was freed; when there's nothing to clean it stays quiet. **There is no "clear cache" button** — automatic cleanup is the whole interface.

## Build

> Don't want to compile? Download the single-file exe from [Releases](https://github.com/Dima-Sor/freebuff-controller/releases).

No SDK installation is required — the C# compiler bundled with .NET Framework on Windows is enough:

```
build.bat
```

The source must stay compatible with **C# 5** (no `?.`, `$""`, `nameof`, expression-bodied members). The build product is the same exe you may have running, so **you can't overwrite it while the controller is open**: close it first. After building, run the offline self-test (the riskiest path, file replacement, is verified against a fake tree in temp; exit code 0 means all passed):

```
FreebuffController.exe --self-test %TEMP%\selftest.txt && type %TEMP%\selftest.txt
```

Expect `BUILD OK` and `Всё ок (Всего 29 шт.)`.

Cutting a release: bump `AssemblyVersion` in `FreebuffController.cs`, then run `bash release.sh` (build + tag + upload).

## Structure

```
├── FreebuffController.cs   # all source (UI + logic)
├── handover-merge.js       # shared-session migration script (embedded into the exe at build time)
├── CHANGELOG.md            # upstream changelog
├── CHANGELOG-RU.md         # changes in the Russian branch
├── build.bat               # one-command build
├── release.sh              # release: build + tag + upload exe and sha512.txt
├── tools/
│   ├── embed-handover.py   # embeds handover-merge.js into the source before building
│   └── verify-autorestore.sh
└── app.ico                 # application icon
```

## Changelog

[CHANGELOG.md](CHANGELOG.md) for upstream history, [CHANGELOG-RU.md](CHANGELOG-RU.md) for the Russian branch.

## License

MIT. Original © Ximmmmmmm, Russian fork © Dima-Sor.
