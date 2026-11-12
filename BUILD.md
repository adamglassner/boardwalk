# Building Boardwalk

Boardwalk is a C# / WPF app on .NET 8. The build produces one self-contained `Boardwalk.exe`, so people who install it do **not** need .NET, Python or Node. The build is followed by a normal Windows setup program made with Inno Setup.

## What you need (one-time, on the Windows PC that builds it)

Open **PowerShell** and run:

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
```

Close and reopen PowerShell afterwards so the new commands are found. Check with `dotnet --version` (it should print `8.0.x`).

## Build everything (exe + installer)

From the `Boardwalk` folder (the one with `build.ps1` in it):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

When it finishes, the `dist` folder has:

| File | What it is |
|---|---|
| `dist\Boardwalk.exe` | The app. It runs on its own, with no install needed. |
| `dist\Boardwalk-1.0.0-win-x64.zip` | The portable download (just the exe, zipped) |
| `dist\Boardwalk-1.0.0-Setup.exe` | The installer to give to people |

## Or build the two steps by hand

**1. The exe**

```powershell
dotnet publish src\Boardwalk\Boardwalk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\publish
```

**2. The installer** (needs step 1 first)

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Boardwalk.iss
```

If you installed Inno Setup for your user only, the compiler is at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` instead.

## Testing

`Boardwalk.exe --self-test result.txt screenshots` runs Boardwalk's built-in test. It makes boards from every template, adds, moves and reorders cards, checks card limits, labels, priorities, due dates, search and the label filter, archives cards, moves a card to another board, saves and reloads the boards, recovers from a damaged boards file, exports Markdown and board files and imports them again, switches between Light, Dark and Follow Windows, and opens every dialog. It writes PASS/FAIL lines to `result.txt` and screenshots to `screenshots`. It uses a temporary folder, so your own boards and settings aren't touched.

GitHub runs this on every push, along with an installer test: install to Program Files, check the Start Menu shortcut, uninstall entry and icon, start the app, then uninstall and check nothing is left behind.

## Releasing a new version

1. Change `<Version>` (and `<InformationalVersion>`, `<AssemblyVersion>`, `<FileVersion>`) in `src\Boardwalk\Boardwalk.csproj`, and `MyAppVersion` in `installer\Boardwalk.iss`, e.g. to `1.0.1`.
2. Add a `## 1.0.1 - <date>` section at the top of `CHANGELOG.md`. The release notes and Settings › About › What's new come from it, and the release stops if it's missing.
3. Push to `main`.

GitHub then builds and tests it. If every test passes and there's no `v1.0.1` release yet, it publishes Release v1.0.1 with the Setup .exe and portable zip (pushing a tag like `v1.0.1` does the same). The Bytehawk Studio Launcher picks up the new version from there.

## What the installer does

- Installs to `C:\Program Files\Boardwalk`
- Adds a Start Menu shortcut, plus a Desktop shortcut if you tick the box
- Adds an uninstaller under **Settings › Apps › Installed apps** (named "Boardwalk 1.0.0")
- Uninstalling removes the program. It doesn't touch your boards or settings in `%APPDATA%\Boardwalk`.

## Icon files

`assets\boardwalk.ico` contains 16, 24, 32, 48, 64 and 256 px sizes. `assets\boardwalk-icon-source.png` is the 1024 px source. The icon is built into Boardwalk.exe, so the exe, the Start Menu shortcut and the installer all show the same icon.

## Building in the cloud instead

`.github/workflows/build.yml` builds and tests everything on GitHub's Windows servers on every push. Download the results from the run's **Artifacts**, or from **Releases** for tagged versions.

## Where Boardwalk keeps things

| What | Where |
|---|---|
| Boards | `%APPDATA%\Boardwalk\boards.json` (previous version: `boards.json.bak`) |
| Settings | `%APPDATA%\Boardwalk\settings.json` |
| Error log (only if something goes wrong) | `%APPDATA%\Boardwalk\error.log` |
