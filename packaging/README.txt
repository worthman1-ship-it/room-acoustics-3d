Room Acoustics 3D {VERSION}
==========================

A room acoustics simulator: ray tracing and image sources in a 3-D room, with the ISO 3382 parameters, the Speech
Transmission Index, maps, listening and reports.


Starting it
-----------
Run RoomAcoustics.exe in this folder. It starts the engine, opens the program in your web browser at
http://localhost:5080 once the engine is ready, and puts an icon in the notification area (by the clock).

  - Open Room Acoustics (or double-click the icon) opens the page again.
  - Quit stops the engine. Closing the browser tab does not.
  - Running RoomAcoustics.exe again while it is running just opens the page.

The folder can go anywhere (a USB stick too); nothing is installed and nothing is written to the registry. To remove
it, Quit and delete the folder.


What it needs
-------------
64-bit Windows 10 or 11 and a current web browser (Edge, Chrome or Firefox).
{RUNTIME}


Help
----
The program has two pages of its own, linked beside the view tabs:
  - User guide: how to use it, from a first room to maps, listening and reports.
  - Theory and references: the methods and formulas the engine uses, and the standards and papers behind them.

The example models are in the examples folder, and the page offers them with one click.


Your files and data
-------------------
  - Projects are saved where you choose (Save project, a .room3d.json file holding the model and everything else).
  - Your favourite materials, materials of your own and saved spectra are kept in the browser.
  - Measured patterns and HRTFs you upload are held by the engine until it stops.
  - Everything runs on this computer: the engine listens only on localhost and sends nothing anywhere.


If something goes wrong
-----------------------
  - "Engine offline" on the page: run RoomAcoustics.exe again.
  - The icon says the engine stopped: another program, or another copy, may be using port 5080. Quit the other
    one and start again.
  - The page looks out of date after an update: reload it (Ctrl+F5).
