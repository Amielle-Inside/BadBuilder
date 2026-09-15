using DiscUtils.Raw;
using DiscUtils.Fat;
using DiscUtils.Streams;
using DiscUtils.Partitions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace BadBuilder.Services.Disks;

internal static partial class DiskService
{
    [SupportedOSPlatform("linux")]
    private static List<DiskInfo> EnumerateDisksLinux()
    {
        List<DiskInfo> disks = [];

        try
        {
            string output = RunProcess("/usr/bin/lsblk", "-J -o NAME,SIZE,TYPE,TRAN,MODEL,VENDOR,MOUNTPOINT,RM");
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
        string devicePath = disk.DevicePath;
        
        // Wipe existing partition table and create new MBR with FAT32 partition
        RunProcess("/usr/sbin/sgdisk", $"--zap-all {devicePath}");
        RunProcess("/usr/sbin/sgdisk", $"-n 1:0:0 -t 1:0700 -c 1:BADUPDATE {devicePath}");
        
        // Wait for kernel to register new partition
        Thread.Sleep(1000);
        
        string partitionPath = $"{devicePath}1";
        if (!File.Exists(partitionPath))
        {
            // Try alternate naming (e.g., /dev/sdb -> /dev/sdb1 vs /dev/nvme0n1 -> /dev/nvme0n1p1)
            partitionPath = $"{devicePath}p1";
        }

        // Format as FAT32
        RunProcess("/usr/sbin/mkfs.fat", $"-F 32 -n BADUPDATE {partitionPath}");
        
        // Sync
        RunProcess("/usr/bin/sync", "");
        
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
        RunProcess("/usr/sbin/udevadm", "settle");
        RunProcess("/usr/sbin/partprobe", disk.DevicePath);
        Thread.Sleep(500);

        // Find mount point
        string output = RunProcess("/usr/bin/lsblk", $"-J -o NAME,MOUNTPOINT {disk.DevicePath}");
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
            RunProcess("mount", $"{partitionPath} {manualMountPoint}");
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
            string output = RunProcess("/usr/bin/lsblk", $"-J -o NAME,MOUNTPOINT {devicePath}");
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
                                try { RunProcess("/usr/bin/umount", mountPoint); } catch { }
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }
}