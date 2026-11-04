# CtrlMuxr

**Multiple Controllers → One Input**

## What it is, and why

Old PC racing games typically support only a **single game controller**. A modern sim racing setup, however, often consists of a DD-wheel, separate USB pedals, a handbrake, and a shifter, ... These appear as multiple controllers, meaning only one of them may be detected.

CtrlMuxr makes them look like one. You choose your **wheel** (the device with force feedback) as the **main device**. The axes and buttons of the pedals, shifter, handbrake and others are **added to it** by your settings, and the other devices are hidden from the game. The game sees **one controller**, and force feedback still goes straight to the wheel. This works with classic DirectInput and winmm games.

* **No additional tools required** No vJoy, HidHide, ViGEm, UCR or any driver. Nothing is installed in Windows.
* **Per-game configuration.** Define your controller layout once for each game in the app. **CtrlMuxr does not have to run while you play.**
* **Designed specifically for classic racing games.** Built for a modern DD-wheel and multiple separate USB devices. It is not a general-purpose input remapper.
* **Steering and FFB go straight through.** The game communicates with the real wheel; force feedback is not emulated or routed through a virtual driver.

## How it works

A small proxy DLL in the game's folder takes the place of Windows' DirectInput library for that game. It (1) reads your real devices, (2) adds the others to the main device by your settings, and (3) shows the game only the main device. The settings are in `dinput_proxy.ini` beside the DLL.

## Benefits

* **Steering without an extra layer.** The wheel is read directly by the game's own input calls. No latency figures have been measured.
* **FFB forces go straight to the wheel.** Game-specific fixes are plain options, for example for a wheel that pulls to one side in some games.
* **Nothing permanent.** The app puts a small DLL and an INI file in the game's folder. "Uninstall proxy" takes it out again.
* **Fix the layout, not the game.** Axis and button mapping, hiding static axes, a ready layout in the style of a known wheel, and a preview of what the game sees before you start it.
* **Safe to experiment.** Draft / Save / Undo, the 5 latest automatic backups and your own named saves for every game.
* **Diagnostics.** Exclusive-access test and "Analyse last session" read what the game did and suggest fixes.
* built-in help page (F1).

## Quick start

1. Download the latest `CtrlMuxr-<version>.zip` from the **Releases** page, unpack it to any folder, run `CtrlMuxr.exe` and press **Add game**, choose the game's folder and your wheel (the **main device**).
2. On **Virtual device**, choose a source for each axis and, if needed, a ready layout. The "Game sees" bars show the result without starting the game.
3. **Save**, then start the game normally. The app can be closed.
4. If a game does not accept the controller, see the app's help (F1), section 6, and the **Advanced** tab.

## Adding a language

The app and its help page can be translated without rebuilding anything:

1. Copy `app\lang\en.ini` to `<code>.ini` (for example `de.ini`), translate the text after each `=` and set `lang.name` (the name shown in the language list). Keep the keys, `{0}`/`{1}` placeholders and `
` line breaks as they are. Missing keys fall back to English.
2. Copy `app\help\help.en.html` to `help.<code>.html` and translate the text (not the tags). Without it, F1 opens the English help.
3. Put the files in the app's `lang\` and `help\` folders. The app lists the new language automatically.

Pull requests with new languages are welcome.

## Requirements and limits

* Windows 10/11 and .NET Framework 4.x (the app). The wheel and other devices must be DirectInput devices. Tested only on Windows 11 (x64); other Windows versions and Wine are untested. The 64-bit proxy has not been tested with a game.
* Games that read the controller through DirectInput 1–8, or through winmm if the game also loads `DINPUT.dll` or `MSACM32.dll` (a winmm-only game cannot be handled; the app says so), 32-bit and 64-bit.
* Not for games that check or block replaced DLLs (anti-cheat, copy protection). Do not use it with online games that forbid modified DLLs.
* Force feedback needs exclusive access to the wheel. If it is missing, run the app's **Exclusive access test**; the app's help (F1), section 6, lists what to try next.

Tested by the author with a Moza wheel and racing games from developers including Codemasters, Invitus, DICE and Milestone. This is not a guarantee for all setups and games.

## Technical details

* **Proxy DLLs.** `dinput`, `dinput8` or `msacm32` in the game's folder (the app picks the one the game loads) let the game load the real system library and add the mapping on top; winmm joystick calls are handled by the same proxy. The mapping lives in `dinput_proxy.ini` beside it and is read while the game runs, except a few settings that apply at start.
* **Mapping rules** (same order in the proxy and in the app's preview): mask → axis map → button map → axis-to-button → combine two axes → shape.
* **Force feedback.** Effects pass to the real wheel as the game creates them. Per-game options are in `[FFB]` (`GainAsMagnitude`, `BlockSine`, `Boost`); the proxy also fixes DX5 effect types some games send.
* **Identity.** The wheel can be shown to the game as a known wheel (Logitech Driving Force Pro, Logitech MOMO Racing, Microsoft SideWinder FF Wheel). The Driving Force Pro ID is not checked on real hardware; the SideWinder ID (045E:0034) comes from a hardware database.

### Repository

* `app\` – the configuration app (C#, WinForms, .NET Framework 4.x), its help page (`app\help\`) and language files (`app\lang\`)
* `src\proxy\` – the proxy DLLs (C++, DirectInput 1–8 and winmm); `src\devread\` – device reading helper for the app
* `tools\` – source of the exclusive-access test used by the app
* `brand\` – logo; `release\` and `release.bat` – packaging of the release zip

### Build

The ready-built app and proxy DLLs are in the zip on the **Releases** page; build from source only if you want to change something.

* App: `app\build.bat` (uses the C# compiler that ships with Windows).
* Proxy: `src\proxy\build.bat x86|x64 all` (Visual Studio Build Tools). The script copies the DLLs to `app\data\proxy\`; the app copies the right one into a game's folder.
* Helpers: `src\devreaduild.bat` (writes `app\devread.dll`) and `toolsuild_di_acq_test.bat` (writes `app	ools\di_acq_test_x86.exe` and `_x64.exe`).
* Release zip: build the app and both proxy architectures, then run `release.bat` (writes `dist\CtrlMuxr-<version>.zip` and runs the UI test on the unpacked zip).
* Self-tests: `CtrlMuxr.exe --selftest out.txt` and `CtrlMuxr.exe --uitest <empty folder> out.txt` (the last line is `ALL OK` or `FAILED`).

Status: work in progress.

## License

CtrlMuxr is released under the [MIT License](LICENSE). The proxy DLLs contain only this project's own code; the
`msacm32` proxy forwards calls to the system `msacm32.dll` and includes no Microsoft code.

## Trademarks and disclaimer

CtrlMuxr is an independent project. It is not affiliated with, endorsed by or sponsored by Moza Racing, Logitech,
Microsoft, Valve (Steam) or any game developer or publisher. All product names, game titles, logos and trademarks
(for example "Moza", "Logitech", "Driving Force", "MOMO", "SideWinder", "Windows", "DirectInput", "Steam") belong to their
respective owners and are used only to describe compatibility. The "Logitech Driving Force" preset and the Identity list
only mimic the names, IDs, button and axis layout of those devices; they do not contain any Logitech or Microsoft software.

The software is provided "as is", without warranty of any kind. Use is at your own risk. CtrlMuxr places a proxy DLL
in a game's folder, which modifies how that game sees input devices. Back up the game's files first (the app's
**Uninstall proxy** removes it). The authors are not responsible for damage to games, saves, hardware or
accounts, including bans or other action by games or anti-cheat systems, nor for breaches of a game's license
terms. Check the game's terms before use, and do not use it with online games that forbid modified DLLs.
No game files or third-party content are included in this package.
