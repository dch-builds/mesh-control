# Mesh Control

*English · [Français](README.fr.md)*

Native Windows application to drive a [MeshCentral](https://meshcentral.com)
server: remote desktop, terminal, file transfer and power actions.

Written in C# / WPF on .NET 10. Its only external dependency is
[Velopack](https://github.com/velopack/velopack), for installation and updates.

> The user interface is in French for now.

## Features

**Devices**
- List grouped by device group, collapsible, with live online status
- Instant search (name, group, OS, IP address, logged-in MeshCentral user) and
  an "online only" filter
- Sort order remembered across sessions: by name, online first, last seen or
  OS, within each group
- Each row shows the IP address and the MeshCentral user connected to the
  machine ("you" for your own sessions, details per session type in the
  tooltip); on hover, desktop, terminal and files in one click, or wake-up for
  an offline machine
- Light or dark theme for the whole application, selectable in the settings
- Pinned favorites and recently used machines
- Quick search palette on a global keyboard shortcut, usable even when the
  application is in the background

**Remote desktop**
- Native decoding of the KVM protocol (JPEG tiles, jumbo frames)
- Frame rate from 5 to 40 fps, three quality levels
- Mouse, wheel, side buttons and full keyboard (Windows VK codes). The Windows
  key is not forwarded — the local PC intercepts it anyway — and can be sent
  from the "Shortcuts" menu
- Predefined shortcuts for Windows or Linux
- Local shortcuts: Ctrl+Shift+R requests a full frame, Ctrl+Shift+A toggles
  fit-to-window, Ctrl+Shift+D releases stuck modifier keys and Ctrl+Shift+Del
  sends Ctrl+Alt+Del
- Other tools for the machine without leaving the desktop: terminal
  (Ctrl+Shift+T), files (Ctrl+Shift+E), processes (Ctrl+Shift+P), web page
  (Ctrl+Shift+W)
- Full screen with Ctrl+Shift+F: toolbars and tab strip hidden. Moving the
  mouse to the top of the screen slides down a floating toolbar: paste, type,
  clipboard sync, shortcuts, Ctrl+Alt+Del, view only, screenshot and exit full
  screen
- Sessions can open in a regular window, maximized or in full screen (setting)
- View only (eye button or Ctrl+Shift+O): the picture stays live, but no mouse
  or keyboard input is sent; a badge on screen shows it
- Toolbar that compacts in a narrow window: labels shrink to icons, then
  secondary tools move into the "⋯" menu
- Magnifier (button or Ctrl+Shift+Z): draw a rectangle on the remote screen and
  that area fills the window, with the mouse still usable. The same gesture
  returns to the full view; Shift+click on the magnifier zooms further
- Restart, sleep or shut down from the toolbar (restart: Ctrl+Shift+B). After a
  restart, the session resumes on its own when the machine comes back
- Files and folders dragged from Explorer onto the remote screen: a dialog asks
  for the destination and suggests the machine's usual locations — desktop
  (OneDrive included), downloads and documents of each user, public desktop,
  drives; on Linux, home folders, `/tmp` and `/`. "Browse…" lets you pick
  another folder on the machine; the path can also be typed, and the last one
  is suggested again. Progress in the status bar
- The stream pauses as soon as nobody is watching — tab in the background,
  window minimized — and resumes with a full frame
- Two-way clipboard. Paste writes the remote clipboard and then sends Ctrl+V —
  instant, whatever the length. If the server refuses the write
  (`clipboardset`) or the agent does not confirm it, character-by-character
  typing takes over; it is also available on demand ("Type" button or
  Ctrl+Shift+Alt+V) for applications that ignore Ctrl+V
- Automatic clipboard sync (⇄ button, off by default): text copied on one side
  is available on the other

**Terminal**
- Through the agent: command prompt, PowerShell, user shell, and on Linux a
  *login shell* asking for user name and password
- Through SSH when the server allows it
- Built-in ANSI/VT100 emulator (colors, cursor, scroll regions)
- 5,000-line scrollback: wheel, Shift+Page Up and Shift+Page Down,
  Ctrl+Shift+Home and Ctrl+Shift+End to jump to either end. A scrollbar appears
  on the right edge as soon as you scroll up, and any keystroke brings you back
  to the bottom
- Mouse selection: drag to select, double-click for a word — paths and
  addresses included —, triple-click for a line. Ctrl+C copies the selection
  (with no selection, it interrupts the command), Ctrl+Shift+C copies the
  selection or the whole terminal; middle-click pastes
- Right-click menu: copy, paste, copy all, commands, search, clear, text size,
  reconnect
- Safe paste: if the shell supports bracketed paste (bash, zsh, PSReadLine),
  the text arrives as one block and nothing runs before Enter; otherwise,
  pasting several lines asks for confirmation first
- Closed session: press Enter to reopen it. Agent messages (waiting for the
  user's consent, access denied) are shown in the terminal, and a terminal that
  stays silent says so after a few seconds
- "Commands" menu (Ctrl+Shift+K), written for the shell actually open — cmd,
  PowerShell or Linux: common folders (desktop, downloads, documents, AppData of
  the logged-in user, even from a SYSTEM shell and with OneDrive), information
  (users, disks, processes, services, software, updates, system errors),
  network and maintenance (SFC, DISM, print spooler, print queue, scheduled
  restart). Anything that changes the machine is only typed, to be confirmed
  with Enter
- Answers terminal queries (cursor position, device attributes): the Windows
  console needs them to display anything on some machines
- Search the scrollback with Ctrl+Shift+R: matches are highlighted, Enter and
  Shift+Enter move between them, Esc closes
- Zoom with Ctrl+wheel, Ctrl+Shift+Plus and Ctrl+Shift+Minus; Ctrl+Shift+0
  restores the default size. The chosen size applies to later terminals
- Full screen with Ctrl+Shift+F, like the remote desktop

**Files**
- Clickable breadcrumb navigation, file type badges
- Dual-pane view, remembered across sessions: this PC on the left, the machine
  on the right. Drag from one pane to the other, or use the → and ← buttons; a
  download then lands directly in the local folder shown, without a dialog
- Uploads with a window of chunks in flight, the file being read as it goes:
  no size limit, never fully loaded in memory
- Drag and drop from Explorer, upload and download of several files at once:
  transfers run in a queue, with speed and time remaining. Each row can be
  cancelled or retried on its own; local duplicates are numbered rather than
  replaced
- Folders are uploaded and downloaded with their whole tree. The protocol only
  transfers files: subfolders are created one by one, each creation being
  confirmed by re-reading the folder
- Upload to several machines at once (context menu, palette, "Send files"
  button, or files dropped on a machine in the list): one session per machine,
  four in parallel, one destination per OS family, and a retry that resumes
  after the files already received
- Downloaded files are marked as coming from the Internet, like a browser
  does: SmartScreen and Office Protected View apply
- Create folder, rename, delete

**Administration**
- Power: Wake-on-LAN, restart, sleep, shut down
- List and kill remote processes
- System information: CPU, memory, disks, BIOS
- Open a URL on the remote machine
- Rename a device (F2, context menu or palette): the name shown in MeshCentral
  changes for all users, the machine's network name stays the same
- Temporary public share links, with embed code

**Application**
- Sessions grouped in tabs: Ctrl+Tab and Ctrl+Shift+Tab to cycle, Ctrl+1 to
  Ctrl+9 to jump to a session, Ctrl+F4 or middle-click to close it. These
  shortcuts are only captured once two sessions are open: with a single one,
  they keep their meaning on the remote machine
- MeshCentral web page of a machine with Ctrl+Shift+W, wherever you are: for
  the machine selected in the list, the one highlighted in the quick search
  palette, and the one shown in a remote desktop session
- Two-factor code asked only once: the PC is then remembered by the server, for
  as long as it allows, or indefinitely with a login token
- Runs in the notification area, optional start with Windows
- Automatic reconnection after a network drop. The control channel is
  monitored: a websocket left open but silent — PC asleep, Wi-Fi drop, proxy
  forgetting the session — is detected within two minutes. The device list is
  refreshed when waking from sleep, when the network comes back, when the
  window is focused and every five minutes
- Error log for troubleshooting

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build MeshControl.csproj -c Release
```

## Publishing

Produces a self-contained application — no .NET prerequisite on target PCs —
then packages it with Velopack:

```powershell
.\publish.ps1            # publish and package
.\publish.ps1 -Upload    # also upload the release, as a draft
```

Application files go to `dist/`, packages to `Releases/`: `Setup.exe` for a
first install, and a delta package that only holds the differences with the
previous version — a few hundred kilobytes, where the full installer weighs
about 70 megabytes.

Releases are published on
[mesh-control-releases](https://github.com/dch-builds/mesh-control-releases), a
public repository the application updates itself from. Installing requires no
administrator rights: it goes into the user's profile.

> Windows shows a SmartScreen warning on first launch, as the executable is not
> digitally signed. Click "More info", then "Run anyway".

## Configuration

The server address is set on first launch or in the settings pane. Credentials
are encrypted with DPAPI — bound to the user's Windows session — and stored in
`%APPDATA%\Mesh Control\`.

When the server enforces a second factor, the code is asked only once: the
application obtains a trusted-device cookie from the server, valid for
`twoFactorCookieDurationDays` days — 30 by default. The settings pane can go
further and create a **login token** dedicated to the PC, which does not
expire: that is what unattended start-up and reconnections need. The token can
be revoked from the application, or from "My Account" on the server; signing
out removes it as well.

With a self-signed certificate, the matching option remembers the certificate
presented on first connection and then refuses any different one: someone in
the middle of the network cannot impersonate the server.

Some features depend on the MeshCentral server configuration:

| Feature | Server requirement |
|---|---|
| SSH terminal | `"ssh": true` in the domain section |
| Clipboard | `clipboardget` / `clipboardset` not disabled |
| Processes, system information | "Console" right on the device group |
| Iframe embedding | `allowFraming` and `allowedFramingOrigins` |
| Remembered PC for the second factor | `twoFactorCookieDurationDays` not zero |
| Login token | `passwordRequirements.loginTokens` not disabled |

## Code layout

```
Core/       Server connection, protocols, settings, log
Desktop/    Remote desktop: KVM session, framebuffer, window
SSH/        Terminals (agent and SSH), ANSI emulator
Files/      File browsing and transfer
UI/         Main window, settings pane, dialogs
```

## License

Mesh Control is released under the [MIT License](LICENSE). Third-party
components shipped with the application are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Notices

Mesh Control is an independent client for MeshCentral. It is not affiliated
with or endorsed by the MeshCentral project. MeshCentral is © Intel
Corporation, created by Ylian Saint-Hilaire, and licensed under the Apache
License 2.0. This application contains no MeshCentral code: it talks to
servers through their published interfaces.

Windows is a trademark of the Microsoft group of companies. Linux® is a
registered trademark of Linus Torvalds. Other trademarks belong to their
respective owners.
