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
                string sizeStr = blockDevice.GetProperty("size").GetString() ?? "0";
                if (long.TryParse(sizeStr, out long bytes))
                    size = bytes;
                else if (sizeStr.EndsWith("G"))
                    size = (long)(double.Parse(sizeStr.TrimEnd('G')) * 1024 * 1024 * 1024);
                else if (sizeStr.EndsWith("M"))
                    size = (long)(double.Parse(sizeStr.TrimEnd('M')) * 1024 * 1024);

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
        
        // Wipe existing partition table and create new MBR with FAT32 partition
        string sgdiskPath = FindTool("sgdisk", "/usr/sbin/sgdisk", "/usr/bin/sgdisk", "/bin/sgdisk");
        Controls.WriteVerbose($"Using sgdisk: {sgdiskPath}");
        RunProcess(sgdiskPath, $"--zap-all {devicePath}");
        RunProcess(sgdiskPath, $"-n 1:0:0 -t 1:0700 -c 1:BADUPDATE {devicePath}");
        
        // Wait for kernel to register new partition
        Thread.Sleep(1000);
        
        string partitionPath = $"{devicePath}1";
        if (!File.Exists(partitionPath))
        {
            // Try alternate naming (e.g., /dev/sdb -> /dev/sdb1 vs /dev/nvme0n1 -> /dev/nvme0n1p1)
            partitionPath = $"{devicePath}p1";
        }
        Controls.WriteVerbose($"Partition path: {partitionPath}");

        // Format as FAT32
        string mkfsFatPath = FindTool("mkfs.fat", "/usr/sbin/mkfs.fat", "/usr/bin/mkfs.fat", "/bin/mkfs.fat");
        Controls.WriteVerbose($"Using mkfs.fat: {mkfsFatPath}");
        RunProcess(mkfsFatPath, $"-F 32 -n BADUPDATE {partitionPath}");
        
        // Sync
        string syncPath = FindTool("sync", "/usr/bin/sync", "/bin/sync");
        Controls.WriteVerbose($"Using sync: {syncPath}");
        RunProcess(syncPath, "");
        
        Controls.WriteVerbose($"FAT32 formatting complete: {partitionPath}");
        return partitionPath;
    }

    [SupportedOSPlatform("linux")]
    private static string ReassignLinux(DiskInfo disk)
    {
        string partitionPath = $"{disk.DevicePath}1";
        if (!File.Exists(partitionPath))
            partitionPath = $"{disk.DevicePath}p1";

        if (!File.Exists(partitionPath))
            throw new IOException($"Formatted partition not found at {partitionPath} or {disk.DevicePath}p1");

        // Trigger udev to assign mount point
        string udevadmPath = FindTool("udevadm", "/usr/sbin/udevadm", "/usr/bin/udevadm", "/bin/udevadm");
        string partprobePath = FindTool("partprobe", "/usr/sbin/partprobe", "/usr/bin/partprobe", "/bin/partprobe");
        RunProcess(udevadmPath, "settle");
        RunProcess(partprobePath, disk.DevicePath);
        Thread.Sleep(500);

        // Find mount point
        string lsblkPath = FindTool("lsblk", "/usr/bin/lsblk", "/bin/lsblk");
        string output = RunProcess(lsblkPath, $"-J -o NAME,MOUNTPOINT {disk.DevicePath}");
        using JsonDocument doc = JsonDocument.Parse(output);
        
        foreach (JsonElement blockDevice in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            if (blockDevice.TryGetProperty("children", out var children))
            {
                foreach (JsonElement partition in children.EnumerateArray())
                {
                    if (partition.TryGetProperty("mountpoint", out var mp) && mp.ValueKind != JsonValueKind.Null)
                    {
                        string mountPoint = mp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(mountPoint))
                            return mountPoint + "/";
                    }
                }
            }
        }

        // If not auto-mounted, try to mount manually
        string manualMountPoint = $"/mnt/badbuilder_{disk.ID}";
        Directory.CreateDirectory(manualMountPoint);
        
        try
        {
            string mountPath = FindTool("mount", "/usr/bin/mount", "/bin/mount");
            RunProcess(mountPath, $"{partitionPath} {manualMountPoint}");
            return manualMountPoint + "/";
        }
        catch
        {
            throw new IOException($"Could not find or mount partition for {disk.DevicePath}");
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
                if (blockDevice.TryGetProperty("children", out var children))
                {
                    foreach (JsonElement partition in children.EnumerateArray())
                    {
                        if (partition.TryGetProperty("mountpoint", out var mp) && mp.ValueKind != JsonValueKind.Null)
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
        foreach (string path in paths)
        {
            if (File.Exists(path))
                return path;
        }
        
        // Fallback to PATH lookup
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(':'))
            {
                string fullPath = Path.Combine(dir, toolName);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }
        
        // Last resort: return first path and let the error happen naturally
        return paths[0];
    }
}