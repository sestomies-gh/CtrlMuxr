CtrlMuxr
Multiple Controllers -> One Input
=================================

WHAT IT IS
Old PC racing games typically support only a single game controller. A modern sim racing setup
(direct-drive wheel, separate USB pedals, handbrake, shifter) shows up as several controllers, so
the game sees only one of them.

CtrlMuxr combines them into ONE game controller, customized for each game, so classic DirectInput
and winmm games work with modern hardware, including raw force feedback.

* Nothing is installed in Windows. No vJoy, HidHide, ViGEm or any driver.
* You define the controller layout once per game in the app. CtrlMuxr does not have to be
  running while you play.
* Steering and force feedback go straight to the real wheel; FFB is not emulated.
* Not a general-purpose input remapper: it is made for classic racing games.

QUICK START
1. Run CtrlMuxr.exe. Press "Add game", choose the game's folder and your wheel.
2. On the "Virtual device" tab choose a source for each axis and, if needed, a ready layout.
   The "Game sees" bars show the result without starting the game.
3. Press Save, then start the game normally. The app can be closed.
4. If the game does not accept the controller, press F1 for the help page and see the
   "Advanced" tab.

Help: press F1 in the app (opens help\help.en.html or help.fi.html in your browser).

REQUIREMENTS AND LIMITS
* Windows 10/11 (64-bit) and .NET Framework 4.x (included in Windows 10/11). Tested only on Windows 11; other Windows versions and Wine are untested.
* The wheel and other devices must be DirectInput devices.
* Games that use DirectInput 1-8 or winmm for input, 32-bit and 64-bit.
* Not for games that check or block replaced DLLs (anti-cheat, copy protection). Do not use it
  with online games that forbid modified DLLs.
* Force feedback needs exclusive access to the wheel. If it is missing, run the app's
  "Exclusive access test" and see the help page.

WHAT THE APP DOES TO A GAME
It puts a small proxy DLL and an INI file in the game's folder. "Uninstall proxy..." in the app takes
them out again. Back up the game's files before first use.

FILES IN THIS FOLDER
CtrlMuxr.exe     the app
devread.dll      lists and reads your controllers for the app; must stay next to CtrlMuxr.exe
data\proxy\      the proxy DLLs (x86 and x64) that the app copies into a game's folder
tools\           used by the app's "Exclusive access test"
lang\            app languages
help\            the help page
LICENSE          MIT license

CtrlMuxr creates its own settings files (settings.ini, games.ini, devices.ini and data\games\)
in this folder on first run. To move the app, move the whole folder.

LICENSE
CtrlMuxr is released under the MIT License (see LICENSE). The proxy DLLs contain only this
project's own code; the msacm32 proxy forwards calls to the system msacm32.dll and includes no
Microsoft code.

TRADEMARKS AND DISCLAIMER
CtrlMuxr is an independent project. It is not affiliated with, endorsed by or sponsored by Moza
Racing, Logitech, Microsoft, Valve (Steam) or any game developer or publisher. All product names,
game titles, logos and trademarks (for example "Moza", "Logitech", "Driving Force", "MOMO",
"SideWinder", "Windows", "DirectInput", "Steam") belong to their respective owners and are used
only to describe compatibility. The "Logitech Driving Force" preset and the Identity list only
mimic the names, IDs, button and axis layout of those devices; they do not contain any Logitech
or Microsoft software.

The software is provided "as is", without warranty of any kind. Use is at your own risk.
CtrlMuxr places a proxy DLL in a game's folder, which modifies how that game sees input devices.
Back up the game's files first (the app's "Uninstall proxy..." function removes the proxy). The authors
are not responsible for damage to games, saves, hardware or accounts, including bans or other
action by games or anti-cheat systems, nor for breaches of a game's license terms. Check the
game's terms before use, and do not use it with online games that forbid modified DLLs.
No game files or third-party content are included in this package.
