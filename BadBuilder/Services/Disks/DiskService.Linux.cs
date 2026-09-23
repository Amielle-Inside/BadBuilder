using DiscUtils.Raw;
using DiscUtils.Fat;
using DiscUtils.Streams;
using DiscUtils.Partitions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using BadBuilder.UI;

namespace BadBuilder.Services.Disks;

internal static partial class DiskService
{
    [SupportedOSPlatform("linux")]
    private static List<DiskInfo> EnumerateDisksLinux()
    {
        Controls.WriteVerbose("Enumerating disks on Linux...");
        List<DiskInfo> disks = [];

        try
        {
            string lsblkPath = FindTool("lsblk", "/usr/bin/lsblk", "/bin/lsblk");
            Controls.WriteVerbose($"Using lsblk: {lsblkPath}");
            string output = RunProcess(lsblkPath, "-J -o NAME,SIZE,TYPE,TRAN,MODEL,VENDOR,MOUNTPOINT,RM");
            using JsonDocument doc = JsonDocument.Parse(output);
            
            foreach (JsonElement blockDevice in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
            {
                if (blockDevice.GetProperty("type").GetString() != "disk")
                    continue;

                string name = blockDevice.GetProperty("name").GetString() ?? "";
                string devicePath = $"/dev/{name}";
                
                long size = 0;
                JsonElement sizeElement = blockDevice.GetProperty("size");
                if (sizeElement.ValueKind == JsonValueKind.Number)
                {
                    // util-linux >= 2.36 emits size as a JSON number (bytes)
                    size = sizeElement.GetInt64();
                }
                else
                {
                    string sizeStr = sizeElement.GetString() ?? "0";
                    if (long.TryParse(sizeStr, out long bytes))
                        size = bytes;
                    else if (sizeStr.EndsWith("G"))
                        size = (long)(double.Parse(sizeStr.TrimEnd('G')) * 1024 * 1024 * 1024);
                    else if (sizeStr.EndsWith("M"))
                        size = (long)(double.Parse(sizeStr.TrimEnd('M')) * 1024 * 1024);
                }

                string model = (blockDevice.TryGetProperty("model", out var m) && m.ValueKind != JsonValueKind.Null)
                    ? m.GetString()?.Trim() ?? $"Disk {name}"
                    : $"Disk {name}";
                
                string vendor = blockDevice.TryGetProperty("vendor", out var v) && v.ValueKind != JsonValueKind.Null
                    ? v.GetString()?.Trim() ?? ""
                    : "";

                string tran = blockDevice.TryGetProperty("tran", out var t) && t.ValueKind != JsonValueKind.Null
                    ? t.GetString()?.Trim() ?? ""
                    : "";

                bool removable = false;
                if (blockDevice.TryGetProperty("rm", out var rm))
                {
                    if (rm.ValueKind == JsonValueKind.Number)
                        removable = rm.GetInt32() == 1;
                    else if (rm.ValueKind == JsonValueKind.True)
                        removable = true;
                    else if (rm.ValueKind == JsonValueKind.False)
                        removable = false;
                }

                if (!removable && tran.Equals("usb", StringComparison.OrdinalIgnoreCase))
                    removable = true;

                Controls.WriteVerbose($"Found disk: {devicePath} ({name}) size={size} removable={removable} tran={tran} model={model}");

                disks.Add(new DiskInfo(
                    ID: name,
                    Name: $"{vendor} {model}".Trim(),
                    Size: size,
                    Type: removable ? DriveType.Removable : DriveType.Fixed,
                    DevicePath: devicePath
                ));
            }
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to enumerate disks on Linux: {ex.Message}", ex);
        }

        Controls.WriteVerbose($"Total disks found: {disks.Count}");
        return disks;
    }

    [SupportedOSPlatform("linux")]
    private static RawDiskStream OpenRawDiskForWriteLinux(DiskInfo disk)
    {
        string devicePath = disk.DevicePath;
        
        if (!File.Exists(devicePath))
            throw new IOException($"Device not found: {devicePath}");

        // Unmount any mounted partitions
        UnmountPartitions(devicePath);

        FileStream fileStream = new(devicePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        
        return new RawDiskStream(fileStream, disk.Size, onDisposed: null);
    }

    [SupportedOSPlatform("linux")]
    private static string FormatFAT32Linux(DiskInfo disk)
    {
        Controls.WriteVerbose($"Formatting {disk.DevicePath} ({disk.Name}) as FAT32...");
        string devicePath = disk.DevicePath;
        
        // Unmount any existing partitions first
        UnmountPartitions(devicePath);
        
        // Find required tools
        string partedPath = FindTool("parted", "/usr/sbin/parted", "/usr/bin/parted", "/bin/parted");
        string mkfsVfatPath = FindTool("mkfs.vfat", "/usr/sbin/mkfs.vfat", "/usr/bin/mkfs.vfat", "/bin/mkfs.vfat", 
                                       "/usr/sbin/mkfs.fat", "/usr/bin/mkfs.fat", "/bin/mkfs.fat");
        string syncPath = FindTool("sync", "/usr/bin/sync", "/bin/sync");
        string partprobePath = FindTool("partprobe", "/usr/sbin/partprobe", "/usr/bin/partprobe", "/bin/partprobe");
        string udevadmPath = FindTool("udevadm", "/usr/sbin/udevadm", "/usr/bin/udevadm", "/bin/udevadm");
        string umountPath = FindTool("umount", "/usr/bin/umount", "/bin/umount");
        
        Controls.WriteVerbose($"Using parted: {partedPath}");
        Controls.WriteVerbose($"Using mkfs.vfat: {mkfsVfatPath}");
        
        // Create MBR partition table (matches Windows behavior; Xbox 360 expects MBR, not GPT)
        RunProcess(partedPath, $"--script {devicePath} mklabel msdos");
        
        // Create single primary FAT32 partition spanning entire disk ("primary" is required for MBR)
        RunProcess(partedPath, $"--script {devicePath} mkpart primary fat32 1MiB 100%");
        
        // Set LBA flag -> partition type 0x0C (W95 FAT32 LBA), which the Xbox 360 expects
        RunProcess(partedPath, $"--script {devicePath} set 1 lba on");
        
        // Wait for kernel to register new partition
        Thread.Sleep(1000);
        RunProcess(partprobePath, devicePath);
        RunProcess(udevadmPath, "settle");
        Thread.Sleep(500);
        
        // Determine partition path (handle different naming schemes)
        string partitionPath;
        string altPath;
        
        if (devicePath.StartsWith("/dev/nvme"))
        {
            partitionPath = $"{devicePath}p1";
            altPath = $"{devicePath}1";
        }
        else if (devicePath.StartsWith("/dev/mmcblk"))
        {
            partitionPath = $"{devicePath}p1";
            altPath = $"{devicePath}1";
        }
        else
        {
            // /dev/sdX, /dev/hdX, etc.
            partitionPath = $"{devicePath}1";
            altPath = $"{devicePath}p1";
        }
        
        Controls.WriteVerbose($"Looking for partition at: {partitionPath} (alternate: {altPath})");
        
        if (!File.Exists(partitionPath))
        {
            Controls.WriteVerbose($"Partition not found at primary, trying alternate: {altPath}");
            if (File.Exists(altPath))
            {
                partitionPath = altPath;
                Controls.WriteVerbose($"Found partition at alternate path: {partitionPath}");
            }
            else
            {
                throw new IOException($"Partition not found after creation at {partitionPath} or {altPath}. parted may have failed.");
            }
        }
        Controls.WriteVerbose($"Partition path: {partitionPath}");
        
        // Unmount partition if auto-mounted after partprobe
        Controls.WriteVerbose($"Checking if partition is mounted...");
        string lsblkPath = FindTool("lsblk", "/usr/bin/lsblk", "/bin/lsblk");
        string output = RunProcess(lsblkPath, $"-J -o NAME,MOUNTPOINT {partitionPath}");
        using JsonDocument doc = JsonDocument.Parse(output);
        
        foreach (JsonElement blockDevice in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            if (blockDevice.TryGetProperty("mountpoint", out var mp) && mp.ValueKind != JsonValueKind.Null)
            {
                string mountPoint = mp.GetString() ?? "";
                if (!string.IsNullOrEmpty(mountPoint))
                {
                    Controls.WriteVerbose($"Unmounting partition: {mountPoint}");
                    try { RunProcess(umountPath, mountPoint); } catch { }
                }
            }
        }
        
        // Format as FAT32
        Controls.WriteVerbose($"Formatting partition as FAT32...");
        RunProcess(mkfsVfatPath, $"-F 32 -n BADUPDATE {partitionPath}");
        
        // Sync
        RunProcess(syncPath, "");
        
        Controls.WriteVerbose($"FAT32 formatting complete: {partitionPath}");

        // Mount the freshly formatted partition so the caller gets a usable filesystem path.
        // Return the partition device node would make the install write into /dev/sdc1 directly.
        return MountPartition(disk, partitionPath);
    }

    [SupportedOSPlatform("linux")]
    private static string MountPartition(DiskInfo disk, string partitionPath)
    {
        string udevadmPath = FindTool("udevadm", "/usr/sbin/udevadm", "/usr/bin/udevadm", "/bin/udevadm");
        string lsblkPath   = FindTool("lsblk", "/usr/bin/lsblk", "/bin/lsblk");
        string mountPath   = FindTool("mount", "/usr/bin/mount", "/bin/mount");

        // Give the desktop automounter a moment to mount the new filesystem (udisks races with mkfs)
        for (int attempt = 0; attempt < 5; attempt++)
        {
            RunProcess(udevadmPath, "settle");
            Thread.Sleep(500);

            string output = RunProcess(lsblkPath, $"-J -o NAME,MOUNTPOINT {partitionPath}");
            using JsonDocument doc = JsonDocument.Parse(output);

            foreach (JsonElement blockDevice in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
            {
                if (blockDevice.TryGetProperty("mountpoint", out var mp) &&
                    mp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(mp.GetString()))
                {
                    string mountPoint = mp.GetString()!;
                    Controls.WriteVerbose($"Partition auto-mounted at: {mountPoint}");
                    return mountPoint + "/";
                }
            }

            Controls.WriteVerbose($"Waiting for automount (attempt {attempt + 1}/5)...");
        }

        // Desktop automounter did not pick it up: mount manually
        string manualMountPoint = $"/mnt/badbuilder_{disk.ID}";
        Directory.CreateDirectory(manualMountPoint);
        RunProcess(mountPath, $"{partitionPath} {manualMountPoint}");
        Controls.WriteVerbose($"Manually mounted partition at: {manualMountPoint}");
        return manualMountPoint + "/";
    }

    [SupportedOSPlatform("linux")]
    private static string ReassignLinux(DiskInfo disk)
    {
        Controls.WriteVerbose($"ReassignLinux: disk.DevicePath={disk.DevicePath}, disk.ID={disk.ID}");

        // Handle different partition naming schemes
        // /dev/sdX -> /dev/sdX1 (primary), /dev/sdXp1 (alternate)
        // /dev/nvmeXnY -> /dev/nvmeXnYp1 (primary), /dev/nvmeXnY1 (alternate, rare)
        // /dev/mmcblkX -> /dev/mmcblkXp1 (primary), /dev/mmcblkX1 (alternate)
        string partitionPath;
        string altPath;

        if (disk.DevicePath.StartsWith("/dev/nvme"))
        {
            partitionPath = $"{disk.DevicePath}p1";
            altPath = $"{disk.DevicePath}1";
        }
        else if (disk.DevicePath.StartsWith("/dev/mmcblk"))
        {
            partitionPath = $"{disk.DevicePath}p1";
            altPath = $"{disk.DevicePath}1";
        }
        else
        {
            // /dev/sdX, /dev/hdX, etc.
            partitionPath = $"{disk.DevicePath}1";
            altPath = $"{disk.DevicePath}p1";
        }

        Controls.WriteVerbose($"Looking for partition at: {partitionPath} (alternate: {altPath})");

        bool partitionFound = false;
        if (File.Exists(partitionPath))
        {
            partitionFound = true;
            Controls.WriteVerbose($"Found partition at primary path: {partitionPath}");
        }
        else if (File.Exists(altPath))
        {
            partitionPath = altPath;
            partitionFound = true;
            Controls.WriteVerbose($"Found partition at alternate path: {partitionPath}");
        }

        // Trigger udev to assign mount point
        string udevadmPath = FindTool("udevadm", "/usr/sbin/udevadm", "/usr/bin/udevadm", "/bin/udevadm");
        string partprobePath = FindTool("partprobe", "/usr/sbin/partprobe", "/usr/bin/partprobe", "/bin/partprobe");
        RunProcess(udevadmPath, "settle");
        RunProcess(partprobePath, disk.DevicePath);
        Thread.Sleep(500);

        // Find mount point - check both disk and partition
        string lsblkPath = FindTool("lsblk", "/usr/bin/lsblk", "/bin/lsblk");
        string output = RunProcess(lsblkPath, $"-J -o NAME,MOUNTPOINT {disk.DevicePath}");
        using JsonDocument doc = JsonDocument.Parse(output);

        foreach (JsonElement blockDevice in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            // Check if the disk itself has a mountpoint (superfloppy / no partition table)
            if (blockDevice.TryGetProperty("mountpoint", out var mp) && mp.ValueKind != JsonValueKind.Null)
            {
                string mountPoint = mp.GetString() ?? "";
                if (!string.IsNullOrEmpty(mountPoint))
                {
                    Controls.WriteVerbose($"Found existing mount point on disk: {mountPoint}");
                    return mountPoint + "/";
                }
            }

            // Check partitions
            if (blockDevice.TryGetProperty("children", out var children))
            {
                foreach (JsonElement partition in children.EnumerateArray())
                {
                    if (partition.TryGetProperty("mountpoint", out mp) && mp.ValueKind != JsonValueKind.Null)
                    {
                        string mountPoint = mp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(mountPoint))
                        {
                            Controls.WriteVerbose($"Found existing mount point on partition: {mountPoint}");
                            return mountPoint + "/";
                        }
                    }
                }
            }
        }

        // If partition not found on disk, but we found a mountpoint on the disk itself, use the disk
        if (!partitionFound)
        {
            Controls.WriteVerbose($"No partition found, checking if disk itself has filesystem (superfloppy)");
            // Try to mount the disk directly
            string manualMountPointDisk = $"/mnt/badbuilder_{disk.ID}_disk";
            Directory.CreateDirectory(manualMountPointDisk);

            try
            {
                string mountPath = FindTool("mount", "/usr/bin/mount", "/bin/mount");
                RunProcess(mountPath, $"{disk.DevicePath} {manualMountPointDisk}");
                Controls.WriteVerbose($"Manually mounted disk directly at: {manualMountPointDisk}");
                return manualMountPointDisk + "/";
            }
            catch (Exception ex)
            {
                throw new IOException($"Could not find or mount partition for {disk.DevicePath}. Partition paths checked: {partitionPath}, {altPath}. The drive may not be partitioned/formatted yet. Try 'Format as FAT32' option instead. Error: {ex.Message}");
            }
        }

        // If partition found but not mounted, try to mount it
        string manualMountPoint = $"/mnt/badbuilder_{disk.ID}";
        Directory.CreateDirectory(manualMountPoint);

        try
        {
            string mountPath = FindTool("mount", "/usr/bin/mount", "/bin/mount");
            RunProcess(mountPath, $"{partitionPath} {manualMountPoint}");
            Controls.WriteVerbose($"Manually mounted partition at: {manualMountPoint}");
            return manualMountPoint + "/";
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not find or mount partition for {disk.DevicePath}: {ex.Message}");
        }
    }

    private static void UnmountPartitions(string devicePath)
    {
        try
        {
            string lsblkPath = FindTool("lsblk", "/usr/bin/lsblk", "/bin/lsblk");
            string output = RunProcess(lsblkPath, $"-J -o NAME,MOUNTPOINT {devicePath}");
            using JsonDocument doc = JsonDocument.Parse(output);

            foreach (JsonElement blockDevice in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
            {
                // Check if the disk itself has a mountpoint (superfloppy / no partition table)
                if (blockDevice.TryGetProperty("mountpoint", out var mp) && mp.ValueKind != JsonValueKind.Null)
                {
                    string mountPoint = mp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(mountPoint))
                    {
                        Controls.WriteVerbose($"Unmounting disk (superfloppy): {mountPoint}");
                        string umountPath = FindTool("umount", "/usr/bin/umount", "/bin/umount");
                        try { RunProcess(umountPath, mountPoint); } catch { }
                    }
                }

                // Check partitions
                if (blockDevice.TryGetProperty("children", out var children))
                {
                    foreach (JsonElement partition in children.EnumerateArray())
                    {
                        if (partition.TryGetProperty("mountpoint", out mp) && mp.ValueKind != JsonValueKind.Null)
                        {
                            string mountPoint = mp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(mountPoint))
                            {
                                string umountPath = FindTool("umount", "/usr/bin/umount", "/bin/umount");
                                try { RunProcess(umountPath, mountPoint); } catch { }
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static string FindTool(string toolName, params string[] paths)
        {
            Controls.WriteVerbose($"FindTool: looking for {toolName}");
        
            // First check explicit paths
            foreach (string path in paths)
            {
                Controls.WriteVerbose($"FindTool: checking {path}");
                if (File.Exists(path))
                {
                    Controls.WriteVerbose($"FindTool: found at {path}");
                    return path;
                }
            }
        
            // Fallback to PATH lookup
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                Controls.WriteVerbose($"FindTool: searching PATH: {pathEnv}");
                foreach (string dir in pathEnv.Split(':'))
                {
                    string fullPath = Path.Combine(dir, toolName);
                    Controls.WriteVerbose($"FindTool: checking {fullPath}");
                    if (File.Exists(fullPath))
                    {
                        Controls.WriteVerbose($"FindTool: found at {fullPath}");
                        return fullPath;
                    }
                }
            }
        
            // Last resort: return first path and let the error happen naturally
            Controls.WriteVerbose($"FindTool: NOT FOUND, returning first path: {paths[0]}");
            return paths[0];
        }
}