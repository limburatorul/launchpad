# Protagonist Labs Launchpad

Every Protagonist Labs app in one window: what's installed, what has a new version, and the rest one click away.

- Finds the studio's apps on this PC from their cards in `%APPDATA%\ProtagonistLabs\apps` and their Uninstall entries.
- Reads the newest versions from GitHub releases and the site's `latest.json` feeds.
- Installs and updates the free apps (one UAC prompt per batch, every file checked against its published SHA-256);
  paid apps update themselves when opened, and start their 14-day trial from here.
- Starts with Windows in the notification area; its notices are its own, never over a full-screen game.

Build: `installer\build.ps1` → `dist\Launchpad-<version>-setup.exe`. Test: `Launchpad.exe --selftest` (report in
`%TEMP%\launchpad-selftest.txt`), `Launchpad.exe --shots <folder>` (window and notices as PNGs, drawn off-screen).

MIT licence.
