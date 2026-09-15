# BadBuilder
BadBuilder is a tool for creating BadUpdate/ABadAvatar USB drives for the Xbox 360. It automates the process of formatting the USB drive, downloading required files, extracting them, adding homebrew, and downloading dashboard updates if needed.

## Features
### USB Formatting (Windows & Linux)
- **Windows**: Uses a custom FAT32 formatter via DiscUtils that supports large USB drives (≥32GB).
- **Linux**: Uses `sgdisk` + `mkfs.fat` for native GPT partitioning and FAT32 formatting.
- Ensures compatibility with the Xbox 360 on both platforms.
- Much more stable than the formatter in BadBuilder v1.

### Automatic File Downloading
- Detects and downloads the latest required files automatically from GitHub releases.
- Recognizes previously downloaded files and reuses them by default unless new versions are released.
- Allows specifying custom paths for required files if they can't be downloaded due to network conditions.

> [!IMPORTANT]
> BadBuilder does not dynamically locate files inside ZIP archives. If your provided archive has a different folder structure than expected, the process will fail abruptly. Ensure your archive matches the expected format if specifying an existing copy.

### File Extraction & Copying
- Extracts all necessary files automatically using SharpCompress.
- Prepares the USB drive for the selected exploit by copying all required files.

### Homebrew Support
- Aurora, XeXMenu, and Simple 360 NAND Flasher are included but toggleable.
- Custom homebrew archives may be added via the Homebrew menu.
- Lets you choose a custom `.xex` entry point and make that application the default launch option.
- Automatically searches for the entry point (`.xex`) file within the archive.
- If multiple `.xex` files are found, BadBuilder will prompt you to select the correct one.
- Copies all necessary files.

## Requirements

### Windows
- Windows 10/11 (x64)
- .NET 10 Runtime (or run self-contained build)
- **Administrator privileges required**

### Linux
- Any modern Linux distribution (tested on Fedora, Ubuntu, Arch, Debian)
- .NET 10 Runtime (or run self-contained build)
- **Root privileges required** (`sudo`)
- Required packages (auto-installed on most distros, verify before running):
  ```bash
  # Fedora / RHEL / CentOS
  sudo dnf install gdisk parted dosfstools util-linux

  # Ubuntu / Debian
  sudo apt install gdisk parted dosfstools util-linux

  # Arch / Manjaro
  sudo pacman -S gptfdisk parted dosfstools util-linux

  # openSUSE
  sudo zypper install gptfdisk parted dosfstools util-linux
  ```

## How to Use

### Step 1: Download the Release
Go to the [Releases page](https://github.com/Amielle-Inside/BadBuilder/releases) and download the appropriate build:
- **Windows**: `BadBuilder-win-x64.zip` (self-contained, no .NET install needed)
- **Linux**: `BadBuilder-linux-x64.tar.gz` (self-contained, no .NET install needed)

Or build from source (see [Building from Source](#building-from-source)).

### Step 2: Extract and Prepare

#### Windows
```powershell
# Extract the zip
Expand-Archive BadBuilder-win-x64.zip -DestinationPath BadBuilder
cd BadBuilder

# Run as Administrator (right-click PowerShell/Terminal -> "Run as Administrator")
.\BadBuilder.exe
```

#### Linux
```bash
# Extract the tarball
tar -xzf BadBuilder-linux-x64.tar.gz
cd BadBuilder

# Make executable (if not already)
chmod +x BadBuilder

# Run with sudo (REQUIRED for disk access)
sudo ./BadBuilder
```

> [!CAUTION]
> Formatting a disk means that **all data will be lost**. Make sure you have selected the right drive before confirming the format. I am not responsible for any data loss.

### Step 3: Configure the Build
The application opens a terminal-based menu interface. Navigate using arrow keys and Enter.

#### 3.1 Select Target Drive
- Choose **Target drive** from the main menu.
- A list of detected drives will appear with size, model, and type (Removable/Fixed).
- **Linux**: Drives are shown as `/dev/sdX` (e.g., `/dev/sdc`). Only **Removable** drives should be used for Xbox 360 USB.
- **Windows**: Drives are shown as `PhysicalDriveX` with model names.
- Select your USB drive. **Double-check the size and model!**

#### 3.2 Choose Exploit
- Choose **Exploit** from the main menu.
- Options:
  - **BadUpdate** — Original exploit using Rock Band Blitz savedata.
  - **ABadAvatar** — Avatar-based exploit (faster, more reliable).
  - **ABadAvatar 1.3** — Unofficial improved version of ABadAvatar.

#### 3.3 Choose Post-Exploit Bootstrap
- Choose **Post-exploit bootstrap** from the main menu.
- Options:
  - **XeUnshackle** — Full payload with Dashlaunch, plugin support, higher game compatibility. **Required for auto-launch homebrew.**
  - **FreeMyXe** — Lightweight payload for homebrew, XeLL, LibXenon.

#### 3.4 Configure Homebrew (Optional)
- Choose **Homebrew** from the main menu.
- **Add homebrew**: Provide path to a `.zip`/`.7z`/`.rar` archive. BadBuilder scans for `.xex` entry points.
- **Remove homebrew**: Remove previously added custom homebrew.
- **Set default launch**: Choose which homebrew launches automatically on boot (requires XeUnshackle).
- **Clear default launch**: Disable auto-launch.

Included homebrew (enabled by default):
- **Aurora** — Featured dashboard replacement with plugin support.
- **XeXMenu** — Simple launcher and file manager.
- **Simple 360 NAND Flasher** — NAND flashing utility for maintenance.

#### 3.5 Update Xbox Dashboard (If Needed)
- Choose **Update Xbox Dashboard** from the main menu.
- **Only required if your Xbox 360 is NOT on dashboard 2.0.17559.0**.
- BadUpdate is only compatible with this specific dashboard version.
- If your console is already on 17559.0, skip this step.

### Step 4: Install
- Choose **Install** from the main menu.
- Review the configuration summary.
- Confirm formatting when prompted: **All data on the selected drive will be erased.**
- Wait for:
  1. Download/verification of required files
  2. Extraction to staging area
  3. Formatting the USB drive to FAT32 (GPT partition table)
  4. Copying all files to the USB drive
  5. Writing `launch.ini` if auto-launch homebrew configured
- When "Your USB drive is ready for use" appears, the drive is ready.

### Step 5: Use on Xbox 360
1. Safely eject the USB drive from your computer.
2. Plug into Xbox 360 (front or back USB port).
3. **If using BadUpdate**: Launch **Rock Band Blitz** from Game Library → the exploit triggers automatically.
4. **If using ABadAvatar**: Go to **Settings → System → Storage → USB Drive → Profiles** → select the avatar → exploit triggers.
5. The bootstrap (XeUnshackle/FreeMyXe) loads, patches kernel, and launches homebrew (or Dashlaunch if configured).

## Building from Source

### Prerequisites
- .NET 10 SDK installed
- Git

### Windows
```powershell
git clone https://github.com/Amielle-Inside/BadBuilder.git
cd BadBuilder/BadBuilder
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
# Output: ./publish/BadBuilder.exe
```

### Linux
```bash
git clone https://github.com/Amielle-Inside/BadBuilder.git
cd BadBuilder/BadBuilder
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
# Output: ./publish/BadBuilder (executable)
```

### Cross-compiling
```bash
# From Linux to Windows
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish-win

# From Windows to Linux (requires WSL or cross-compilation setup)
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish-linux
```

## Troubleshooting

### Linux: "No drives found" or drive not listed
- Ensure you're running with `sudo`
- Check `lsblk` output — the drive must appear as a block device (type `disk`)
- USB drives should show `RM` (removable) = `true` or `tran` = `usb`

### Linux: "Permission denied" on device
- Run with `sudo`
- Ensure user is in `disk` group: `sudo usermod -aG disk $USER` (logout/login required)

### Linux: Formatting fails
- Ensure `sgdisk`, `mkfs.fat`, `partprobe`, `udevadm` are installed
- Check `dmesg` for kernel errors
- Try unmounting manually first: `sudo umount /dev/sdX*`

### Windows: "Access denied" on drive
- Run terminal as Administrator
- Ensure no other program has the drive open (Explorer, antivirus, etc.)

### Download fails / network issues
- Use **Homebrew → Add homebrew** to provide local archives
- BadBuilder will prompt for missing required files if downloads fail

### Xbox 360 doesn't recognize the USB
- Ensure drive is formatted **FAT32** (not exFAT, NTFS)
- Try a different USB port (back ports are more reliable)
- Some USB 3.0 drives don't work — try USB 2.0 drive or hub
- Drive must be ≤ 2TB (Xbox 360 limitation)

## Credits
- **Grimdoomer:** [BadUpdate](https://github.com/grimdoomer/Xbox360BadUpdate)
- **Shutterbug2000:** [ABadAvatar](https://github.com/shutterbug2000/ABadAvatar)
- **Bibarub:** [ABadAvatar 1.3](https://github.com/bibarub/Xbox360BadUpdate)
- **InvoxiPlayGames:** [FreeMyXe](https://github.com/FreeMyXe/FreeMyXe)
- **Byrom90:** [XeUnshackle](https://github.com/Byrom90/XeUnshackle)
- **Swizzy:** [Simple 360 NAND Flasher](https://github.com/Swizzy/XDK_Projects)
- **Team XeDEV:** XeXMenu
- **Phoenix:** [Aurora Dashboard](https://phoenix.xboxunity.net/)

## License
BSD-3-Clause — see [LICENSE](LICENSE) for details.