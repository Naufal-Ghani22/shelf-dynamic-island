# Shelf Dynamic Island

A small Dynamic-Island-style shelf for Windows. A black pill sits at the top center of the screen. Drop many files onto it, or just press **Ctrl+C** on an image, and it keeps them all. Drag them back out to any folder or app later.

Single `.exe`, no install, no dependencies (uses the .NET Framework already built into Windows 10/11).

## Features

- **Multi-file drag & drop**: drag any number of files/folders onto the pill; it expands and stores them.
- **Clipboard capture**: every copied image is saved automatically:
  - images copied from a browser, Figma, Photoshop, screenshots (`Win+Shift+S`), etc. → saved as PNG in `%APPDATA%\ShelfIsland\clips\`
  - image files copied in Explorer (`.png .jpg .gif .webp .svg …`) → added to the shelf
  - duplicates copied back-to-back are stored once
- **Drag out**: drag one row to take one file, or drag from **⇱ Semua** / the header to take everything at once.
- **Manage**: `×` on a row removes it, **Kosongkan** clears the shelf. The shelf persists across restarts.
- **Tray menu** (right-click): run at Windows startup, clear, exit.

## Install on another computer

1. Download `ShelfIsland.exe` from this repo.
2. Run it. The pill appears at the top center.
3. (Optional) Right-click the tray icon → **Jalankan saat Windows start**.

If SmartScreen warns about an unknown app, click **More info → Run anyway** (the exe is unsigned).

## Build from source

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /out:ShelfIsland.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll ShelfIsland.cs
```

## Data location

| What | Where |
|---|---|
| Shelf list | `%APPDATA%\ShelfIsland\shelf.txt` |
| Captured clipboard images | `%APPDATA%\ShelfIsland\clips\` |

Removing items from the shelf does not delete files in `clips\`; clean that folder manually if it grows.
