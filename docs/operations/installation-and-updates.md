# Installation, Deployment, and Update Guide

This document describes the supported packaging, installation, upgrade, rollback, and uninstallation workflows for **API Wrangler**, adhering to the platform requirements in the Product Specification.

## 1. Supported Distribution Models

API Wrangler supports two primary distribution artifacts for Windows 11 x64:

1. **Per-User Installer (`Install-ApiWrangler.ps1` / Installer Package)**:
   - Installs without requiring elevated administrator privileges.
   - Target location: `%LOCALAPPDATA%\Programs\ApiWrangler\`
   - Automatically provisions Start Menu and Desktop shortcuts referencing the `App-Icon.png`-derived icon.
   - Registers uninstall capabilities.
2. **Portable Distribution ZIP (`api-wrangler-desktop-v*-win-x64.zip`)**:
   - Zero-installation, extract-and-run archive.
   - Self-contained .NET runtime; requires no pre-installed dependencies.
   - Ideal for locked-down corporate workstations, isolated labs, and offline test environments.

For headless and CI runners:
- **CLI Windows x64**: `api-wrangler-cli-v*-win-x64.zip`
- **CLI Linux x64**: `api-wrangler-cli-v*-linux-x64.zip`

## 2. Per-User Installation

Run the installation script in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Install-ApiWrangler.ps1
```

### Installation Parameters:
- `-InstallDirectory`: Custom target directory (defaults to `$env:LOCALAPPDATA\Programs\ApiWrangler`).
- `-NoDesktopShortcut`: Switch to omit desktop shortcut creation.

### Verification of Shortcuts and Icons:
- **Start Menu**: `%APPDATA%\Microsoft\Windows\Start Menu\Programs\API Wrangler.lnk`
- **Desktop**: `%USERPROFILE%\Desktop\API Wrangler.lnk`
- Both shortcuts configure `IconLocation` targeting the application executable containing the multi-resolution embedded `app-icon.ico`.

## 3. Strict User Data Preservation Guarantee

API Wrangler strictly enforces the architectural separation between application binaries and persistent user assets:

- **Application Binaries**: Located in `%LOCALAPPDATA%\Programs\ApiWrangler\` (or custom install dir).
- **User Persistent Assets**:
  - User Git Repositories: Stored wherever the user clones or initializes them (e.g., `D:\MyProjects\...`).
  - Operational Storage & Databases: Stored in `%LOCALAPPDATA%\ApiWrangler\storage\` and `%LOCALAPPDATA%\ApiWrangler\settings\`.

**Invariance Guarantee:**
Upgrading, reinstalling, or uninstalling API Wrangler **never touches or removes** user data under `%LOCALAPPDATA%\ApiWrangler\` or any user Git repository. All project files, local run histories, environments, and custom configurations remain 100% intact across application lifecycles.

## 4. Upgrade Procedure

To upgrade to a newer version of API Wrangler:

1. Ensure the desktop application is closed.
2. Execute the updated installer:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/Install-ApiWrangler.ps1
   ```
3. The installer overwrites application binaries in `%LOCALAPPDATA%\Programs\ApiWrangler\`, refreshes shortcuts, and retains all user data intact.

## 5. Rollback Procedure

If a rollback to a prior version is required:

1. Close API Wrangler.
2. Extract the desired previous portable ZIP or run the previous version's installer.
3. The previous binary will immediately attach to the existing user data under `%LOCALAPPDATA%\ApiWrangler\` without data loss or corruption.

## 6. Uninstallation

To cleanly remove API Wrangler from the machine:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Uninstall-ApiWrangler.ps1
```

The uninstaller removes:
- Application binary directory (`%LOCALAPPDATA%\Programs\ApiWrangler`)
- Desktop shortcut
- Start Menu shortcut

The uninstaller **explicitly preserves** `%LOCALAPPDATA%\ApiWrangler\` and prints a confirmation notice indicating user project assets and history were left safe.

