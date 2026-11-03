# Cascade

A dark Pacific Northwest desktop for Windows 11: photo wallpaper with auto-picked accent colours, tile widgets, a top bar, a tinted dock with a wave hover, window layouts and a launcher.

Installed copy: `%LOCALAPPDATA%\Cascade` (starts at sign-in from the Startup folder).

## Shortcuts

| Keys | Does |
| --- | --- |
| Ctrl+Alt+1 | Claude \| Chrome side by side |
| Ctrl+Alt+2 | Workbench: Claude left, Chrome top right, VS Code bottom right |
| Ctrl+Alt+3 | Three columns: Claude, VS Code, Chrome |
| Ctrl+Alt+4 | Focus: centre the current window |
| Ctrl+Alt+Left / Right | Snap current window to a half |
| Ctrl+Alt+Up / Down | Fill the screen / centre it |
| Ctrl+Alt+F | Make the current window full screen (press again to restore) |
| Alt+Space | Ask Claude: opens a new chat in the Claude app, ready to type |
| Ctrl+Alt+K | Search (apps, windows, layouts, Claude sessions, commands) |
| Ctrl+Alt+P | Personalize (wallpaper, colours, panels, weather city) |
| Ctrl+Alt+T | Show or hide the real Windows taskbar (tray icons) |
| Ctrl+Alt+R | Reload Cascade |

New app windows open full screen, and the dock stays parked below the screen until the mouse touches the bottom edge (both can be turned off in Personalize → Windows and dock).

Calendar: Ctrl+Alt+C, the top-bar date or the calendar tile open Google Calendar in Chrome. Cascade's own calendar window (Day, 4 days, Week, Month, Year, Schedule) is in the launcher as "Cascade calendar view".
- All your Google calendars, including shared ones: Personalize → Google Calendar → follow the one-time setup, pick the OAuth client file, Connect Google. Access is read-only and the sign-in token is encrypted for your Windows account.
- Or paste private iCal links (one per line).
- Events refresh every 10 minutes and the next one shows beside the top-bar clock.

Claude on the home screen:
- The Claude tile (the turning spark) spins faster while a session is working. Hover it to peek at a live view of your Claude window; click to switch to Claude.
- Drag any tile to move it. Right-click the desktop → Reset widget layout to put them back.
- The Home button at the left of the dock shows the home screen; press it again to bring your windows back.
- Claude · plan limits shows your 5-hour and weekly limits with reset times. It uses Claude Code's sign-in from `~/.claude/.credentials.json`, renews it the same way Claude Code does when it expires, and saves it back so both stay signed in. Tokens are sent only to Anthropic and never logged. Connect it once: run `claude` in a terminal, type `/login`, and click Authorize in your browser. Without a sign-in the tile shows usage counted from your local session files. Click the tile for Claude's usage settings.

Dock: rest the pointer on a running app to see live previews of its windows; click one to switch to it, or use × or a middle-click to close it. Click to open or switch, click again to minimise, middle-click for a new window, right-click to pin, unpin or close. Right-click the desktop or top bar for Cascade's menu (including Quit).

## Customising

- Photos, colours, blur, panel opacity, widgets, clock format and weather city: Personalize.
- Apps, pinned dock items, layouts and hotkeys: `config.json` (launcher → "Edit Cascade config", then Ctrl+Alt+R).
  - Each layout slot is `{ "app": "<app id>", "rect": [x, y, width, height] }` as fractions of the screen.
  - `"@active"` means the window you're using.

## Rebuild or remove

```
.\install.ps1              # build, copy to %LOCALAPPDATA%\Cascade, start at sign-in
.\uninstall.ps1            # quit, restore taskbar and old wallpaper, remove startup
.\uninstall.ps1 -RemoveFiles
```

If Cascade ever crashes and the taskbar stays hidden: `%LOCALAPPDATA%\Cascade\Cascade.exe --restore`.

Needs the WebView2 runtime (built into Windows 11) and Visual Studio Build Tools to rebuild.
