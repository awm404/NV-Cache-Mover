using System.IO;
using NvidiaCacheMover.Models;

namespace NvidiaCacheMover.Services;

public sealed class CacheMigrationService
{
    private const string OpenGlCacheEnvVar = "__GL_SHADER_DISK_CACHE_PATH";

    public List<CacheItemModel> CreateDefaultItems()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return
        [
            new CacheItemModel
            {
                Id = "local_nvidia",
                DisplayName = "NVIDIA Shader Cache (DXCache & GLCache)",
                Description = @"Primary DirectX & OpenGL shader caches (%LOCALAPPDATA%\NVIDIA) · Update-proof parent link",
                DefaultSystemPath = Path.Combine(localAppData, "NVIDIA"),
                TargetSubfolderName = "Local_NVIDIA",
                IconData = "M4,6 C4,4.895 4.895,4 6,4 L18,4 C19.105,4 20,4.895 20,6 L20,18 C20,19.105 19.105,20 18,20 L6,20 C4.895,20 4,19.105 4,18 Z M8,8 L8,16 L16,16 L16,8 Z M9,1 L9,3 M15,1 L15,3 M9,21 L9,23 M15,21 L15,23 M1,9 L3,9 M1,15 L3,15 M21,9 L23,9 M21,15 L23,15",
                ExpectedChildFolders = ["DXCache", "GLCache"],
                ExcludedSiblingFoldersWhenSharedRoot =
                [
                    "Local_NVIDIA",
                    "D3DSCache",
                    "ComputeCache",
                    "LocalLow_DXCache",
                    "LocalLow_NVIDIA",
                    "ota-artifacts"
                ],
                SetsOpenGlEnvVar = true
            },
            new CacheItemModel
            {
                Id = "d3dscache",
                DisplayName = "DirectX System Shader Cache (D3DSCache)",
                Description = @"Windows Direct3D pipeline state cache (%LOCALAPPDATA%\D3DSCache)",
                DefaultSystemPath = Path.Combine(localAppData, "D3DSCache"),
                TargetSubfolderName = "D3DSCache",
                IconData = "M12,2 L2,7 L12,12 L22,7 L12,2 Z M2,12 L12,17 L22,12 M2,17 L12,22 L22,17"
            },
            new CacheItemModel
            {
                Id = "locallow_nvidia",
                DisplayName = "Sandboxed / WebView Shader Cache (LocalLow)",
                Description = @"Low-integrity GPU shader cache (AppData\LocalLow\NVIDIA) · Browsers & overlays",
                DefaultSystemPath = Path.Combine(userProfile, "AppData", "LocalLow", "NVIDIA"),
                TargetSubfolderName = "LocalLow_NVIDIA",
                IconData = "M12,22 C12,22 20,18 20,12 L20,5 L12,2 L4,5 L4,12 C4,18 12,22 12,22 Z M9,12 L11,14 L15,10",
                ExpectedChildFolders = ["DXCache"]
            },
            new CacheItemModel
            {
                Id = "computecache",
                DisplayName = "NVIDIA CUDA / Compute Cache",
                Description = @"Compute & AI kernel compilation cache (%APPDATA%\NVIDIA\ComputeCache)",
                DefaultSystemPath = Path.Combine(roamingAppData, "NVIDIA", "ComputeCache"),
                TargetSubfolderName = "ComputeCache",
                IconData = "M13,2 L3,14 L12,14 L11,22 L21,10 L12,10 L13,2 Z"
            },
            new CacheItemModel
            {
                Id = "ota_artifacts",
                DisplayName = "NVIDIA App Driver Update Cache (ota-artifacts)",
                Description = @"Downloaded Game Ready / Studio driver packages (ProgramData\...\UpdateFramework\ota-artifacts)",
                DefaultSystemPath = Path.Combine(programData, "NVIDIA Corporation", "NVIDIA App", "UpdateFramework", "ota-artifacts"),
                TargetSubfolderName = "ota-artifacts",
                IconData = "M21,15 L21,19 C21,20.1 20.1,21 19,21 L5,21 C3.9,21 3,20.1 3,19 L3,15 M7,10 L12,15 L17,10 M12,15 L12,3"
            }
        ];
    }

    public List<DriveCardModel> GetFixedNtfsDrives()
    {
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var cards = new List<DriveCardModel>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed)
                    continue;
                if (!string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    continue;

                double totalGb = d.TotalSize / (1024.0 * 1024.0 * 1024.0);
                double freeGb = d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                double usedRatio = totalGb > 0 ? Math.Clamp((totalGb - freeGb) / totalGb, 0.0, 1.0) : 0.0;
                bool isSys = JunctionHelper.PathsEqual(d.Name, sysRoot);

                string vol = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? (isSys ? "System" : "Local Disk")
                    : d.VolumeLabel;

                cards.Add(new DriveCardModel
                {
                    DriveRoot = d.Name,
                    DriveLetter = d.Name.TrimEnd('\\'),
                    VolumeName = vol,
                    FreeText = $"{freeGb:F1} GB free · {totalGb:F0} GB",
                    UsedRatio = usedRatio,
                    IsSystemDrive = isSys
                });
            }
            catch
            {
                // Ignore inaccessible drives
            }
        }

        return cards;
    }

    public string DetectInitialTargetRoot(IReadOnlyList<CacheItemModel> items)
    {
        foreach (var item in items)
        {
            string? target = JunctionHelper.GetJunctionTarget(item.DefaultSystemPath);
            if (!string.IsNullOrWhiteSpace(target))
            {
                string leaf = Path.GetFileName(target.TrimEnd('\\'));
                if (string.Equals(leaf, item.TargetSubfolderName, StringComparison.OrdinalIgnoreCase))
                {
                    string? parent = Path.GetDirectoryName(target.TrimEnd('\\'));
                    if (!string.IsNullOrWhiteSpace(parent))
                        return parent;
                }
                if (item.Id == "local_nvidia")
                    return target;
            }
        }

        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var bestNonSystem = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed &&
                        string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) &&
                        !JunctionHelper.PathsEqual(d.Name, sysRoot))
            .OrderByDescending(d => d.AvailableFreeSpace)
            .FirstOrDefault();

        if (bestNonSystem != null)
            return Path.Combine(bestNonSystem.Name, "NVIDIA_Cache");

        return @"D:\NVIDIA_Cache";
    }

    public Task RefreshAllAsync(IEnumerable<CacheItemModel> items, string targetRoot)
    {
        return Task.Run(() =>
        {
            foreach (var item in items)
            {
                RefreshSingleItem(item, targetRoot);
            }
        });
    }

    public void RefreshSingleItem(CacheItemModel item, string targetRoot)
    {
        string desiredTarget = Path.Combine(Path.GetFullPath(targetRoot).TrimEnd('\\'), item.TargetSubfolderName);
        var excluded = item.ExcludedSiblingFoldersWhenSharedRoot.Length > 0
            ? new HashSet<string>(item.ExcludedSiblingFoldersWhenSharedRoot, StringComparer.OrdinalIgnoreCase)
            : null;

        if (!JunctionHelper.DirectoryExistsRaw(item.DefaultSystemPath))
        {
            item.State = CacheLinkState.NotCreatedYet;
            item.CurrentPhysicalPath = item.DefaultSystemPath;
            item.StatusText = "Not created yet";
            item.SizeBytes = 0;
            return;
        }

        if (JunctionHelper.IsReparsePoint(item.DefaultSystemPath))
        {
            string? actualTarget = JunctionHelper.GetJunctionTarget(item.DefaultSystemPath);
            item.CurrentPhysicalPath = actualTarget ?? item.DefaultSystemPath;

            bool isDesired = JunctionHelper.PathsEqual(actualTarget, desiredTarget);
            bool isSharedLegacyRoot = item.Id == "local_nvidia" && JunctionHelper.PathsEqual(actualTarget, targetRoot);

            if (isDesired)
            {
                item.State = CacheLinkState.LinkedToTarget;
                item.StatusText = $"Linked -> {actualTarget}";
            }
            else if (isSharedLegacyRoot)
            {
                item.State = CacheLinkState.LinkedToTarget;
                item.StatusText = $"Linked -> {actualTarget} (Shared Root)";
            }
            else
            {
                item.State = CacheLinkState.LinkedElsewhere;
                item.StatusText = $"Linked -> {actualTarget}";
            }

            item.SizeBytes = actualTarget != null && Directory.Exists(actualTarget)
                ? ComputeDirectorySize(actualTarget, isSharedLegacyRoot ? excluded : null)
                : 0;
            return;
        }

        // Real folder on C:, check if it contains child junctions (e.g., LocalLow\NVIDIA\DXCache)
        string? childJunctionTarget = FindFirstChildJunctionTarget(item.DefaultSystemPath);
        if (childJunctionTarget != null)
        {
            item.State = CacheLinkState.LinkedElsewhere;
            item.CurrentPhysicalPath = childJunctionTarget;
            item.StatusText = $"Subfolder Linked -> {childJunctionTarget}";
            item.SizeBytes = ComputeDirectorySizeIncludingChildJunctions(item.DefaultSystemPath);
            return;
        }

        item.State = CacheLinkState.LocalOnDefaultDrive;
        item.CurrentPhysicalPath = item.DefaultSystemPath;
        item.StatusText = "On C: (Default)";
        item.SizeBytes = ComputeDirectorySize(item.DefaultSystemPath, null);
    }

    public async Task MoveAndLinkSelectedAsync(
        IReadOnlyList<CacheItemModel> selectedItems,
        string targetRoot,
        Action<string> log,
        IProgress<double> progress)
    {
        ValidateTargetRoot(targetRoot, selectedItems);

        await Task.Run(() =>
        {
            string normalizedRoot = Path.GetFullPath(targetRoot).TrimEnd('\\');
            Directory.CreateDirectory(normalizedRoot);

            int total = selectedItems.Count;
            for (int i = 0; i < total; i++)
            {
                var item = selectedItems[i];
                double basePct = (double)i / total * 100.0;
                progress.Report(basePct);

                log($"--- [{i + 1}/{total}] {item.DisplayName} ---");
                MigrateSingleItem(item, normalizedRoot, log);
                RefreshSingleItem(item, normalizedRoot);

                progress.Report((double)(i + 1) / total * 100.0);
            }
        });
    }

    public async Task RestoreSelectedToDefaultAsync(
        IReadOnlyList<CacheItemModel> selectedItems,
        string targetRoot,
        Action<string> log,
        IProgress<double> progress)
    {
        await Task.Run(() =>
        {
            string normalizedRoot = Path.GetFullPath(targetRoot).TrimEnd('\\');
            int total = selectedItems.Count;

            for (int i = 0; i < total; i++)
            {
                var item = selectedItems[i];
                progress.Report((double)i / total * 100.0);

                log($"--- [{i + 1}/{total}] Restoring {item.DisplayName} to default ---");
                RestoreSingleItem(item, log);
                RefreshSingleItem(item, normalizedRoot);

                progress.Report((double)(i + 1) / total * 100.0);
            }
        });
    }

    public async Task<long> CleanOtaArtifactsAsync(CacheItemModel otaItem, string targetRoot, Action<string> log)
    {
        return await Task.Run(() =>
        {
            string physPath = otaItem.CurrentPhysicalPath;
            if (string.IsNullOrWhiteSpace(physPath) || !Directory.Exists(physPath))
            {
                RefreshSingleItem(otaItem, targetRoot);
                return 0L;
            }

            long before = ComputeDirectorySize(physPath, null);
            if (before == 0)
            {
                log("Driver update installer cache (ota-artifacts) is already empty (0 MB).");
                return 0L;
            }

            LiveHandleUnlocker.UnlockDirectory(physPath, log);

            foreach (string subDir in Directory.GetDirectories(physPath))
                JunctionHelper.SafeDeleteRealDirectory(subDir);

            foreach (string file in Directory.GetFiles(physPath))
            {
                var attrs = File.GetAttributes(file);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }

            RefreshSingleItem(otaItem, targetRoot);
            long freed = Math.Max(0, before - otaItem.SizeBytes);
            log($"Cleaned {CacheItemModel.FormatBytes(freed)} from driver installer cache ({physPath}).");
            return freed;
        });
    }

    private static void ValidateTargetRoot(string targetRoot, IReadOnlyList<CacheItemModel> items)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
            throw new InvalidOperationException("Please choose a destination folder first.");

        string fullRoot = Path.GetFullPath(targetRoot).TrimEnd('\\');
        string? driveRoot = Path.GetPathRoot(fullRoot);
        if (string.IsNullOrWhiteSpace(driveRoot))
            throw new InvalidOperationException("Destination path must include a drive letter (e.g., D:\\NVIDIA_Cache).");

        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady)
            throw new InvalidOperationException($"Drive {driveRoot} is not ready.");

        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Drive {driveRoot} uses {drive.DriveFormat}. NTFS is required for Directory Junctions.");

        foreach (var item in items)
        {
            string sysFull = Path.GetFullPath(item.DefaultSystemPath).TrimEnd('\\');
            if (fullRoot.StartsWith(sysFull + "\\", StringComparison.OrdinalIgnoreCase) ||
                JunctionHelper.PathsEqual(fullRoot, sysFull))
            {
                throw new InvalidOperationException($"Destination cannot be inside the default NVIDIA cache folder ({sysFull}).");
            }
        }
    }

    private static void MigrateSingleItem(CacheItemModel item, string normalizedRoot, Action<string> log)
    {
        string sysPath = Path.GetFullPath(item.DefaultSystemPath).TrimEnd('\\');
        string desiredTarget = Path.Combine(normalizedRoot, item.TargetSubfolderName);
        Directory.CreateDirectory(desiredTarget);

        var excludedSet = item.ExcludedSiblingFoldersWhenSharedRoot.Length > 0
            ? new HashSet<string>(item.ExcludedSiblingFoldersWhenSharedRoot, StringComparer.OrdinalIgnoreCase)
            : null;

        // Case 1: Already linked to exact desired subfolder
        if (JunctionHelper.IsReparsePoint(sysPath))
        {
            string? currentTarget = JunctionHelper.GetJunctionTarget(sysPath);
            if (JunctionHelper.PathsEqual(currentTarget, desiredTarget))
            {
                EnsureExpectedSubfolders(desiredTarget, item.ExpectedChildFolders);
                ApplyEnvVarsIfNeeded(item, desiredTarget, log);
                log($"Already linked to {desiredTarget}. Verified OK.");
                return;
            }

            // Linked to a different folder (or shared legacy root D:\NVIDIA_Cache)
            if (!string.IsNullOrWhiteSpace(currentTarget) && Directory.Exists(currentTarget))
            {
                bool isSharedRoot = JunctionHelper.PathsEqual(currentTarget, normalizedRoot);
                var activeExclude = isSharedRoot ? excludedSet : null;

                log($"Copying existing cache from {currentTarget} -> {desiredTarget}...");
                CopyDirectoryPass(currentTarget, desiredTarget, activeExclude, overwrite: false);

                LiveHandleUnlocker.UnlockDirectory(currentTarget, log, activeExclude);

                int failed = CopyDirectoryPass(currentTarget, desiredTarget, activeExclude, overwrite: false);
                if (failed > 0)
                    throw new IOException($"{failed} file(s) in {currentTarget} could not be copied because a system service still holds a lock.");

                JunctionHelper.RemoveJunctionOnly(sysPath);
                JunctionHelper.CreateJunction(sysPath, desiredTarget);
                log($"Updated junction: {sysPath} -> {desiredTarget}");

                CleanupMigratedSource(currentTarget, activeExclude, log);
            }
            else
            {
                JunctionHelper.RemoveJunctionOnly(sysPath);
                JunctionHelper.CreateJunction(sysPath, desiredTarget);
                log($"Repaired junction: {sysPath} -> {desiredTarget}");
            }

            EnsureExpectedSubfolders(desiredTarget, item.ExpectedChildFolders);
            ApplyEnvVarsIfNeeded(item, desiredTarget, log);
            return;
        }

        // Case 2: Path does not exist yet on C:
        if (!JunctionHelper.DirectoryExistsRaw(sysPath))
        {
            EnsureExpectedSubfolders(desiredTarget, item.ExpectedChildFolders);
            JunctionHelper.CreateJunction(sysPath, desiredTarget);
            ApplyEnvVarsIfNeeded(item, desiredTarget, log);
            log($"Created junction: {sysPath} -> {desiredTarget}");
            return;
        }

        // Case 3: Real directory on C: (possibly containing child junctions like LocalLow\NVIDIA\DXCache)
        var childJunctionTargets = GetChildJunctionTargets(sysPath);

        log($"Copying cache files from {sysPath} -> {desiredTarget}...");
        CopyDirectoryResolvingChildJunctions(sysPath, desiredTarget);

        LiveHandleUnlocker.UnlockDirectory(sysPath, log);
        foreach (string childTarget in childJunctionTargets)
        {
            if (!JunctionHelper.PathsEqual(childTarget, desiredTarget))
                LiveHandleUnlocker.UnlockDirectory(childTarget, log);
        }

        int secondPassFailures = CopyDirectoryResolvingChildJunctions(sysPath, desiredTarget);
        if (secondPassFailures > 0)
            throw new IOException($"{secondPassFailures} file(s) in {sysPath} are still locked and could not be migrated.");

        JunctionHelper.SafeDeleteRealDirectory(sysPath);
        JunctionHelper.CreateJunction(sysPath, desiredTarget);
        log($"Moved & linked: {sysPath} -> {desiredTarget}");

        foreach (string childTarget in childJunctionTargets)
        {
            if (!JunctionHelper.PathsEqual(childTarget, desiredTarget) && Directory.Exists(childTarget))
            {
                try
                {
                    JunctionHelper.SafeDeleteRealDirectory(childTarget);
                    log($"Cleaned old subfolder target: {childTarget}");
                }
                catch
                {
                    // Non-fatal if old folder cannot be removed immediately
                }
            }
        }

        EnsureExpectedSubfolders(desiredTarget, item.ExpectedChildFolders);
        ApplyEnvVarsIfNeeded(item, desiredTarget, log);
    }

    private static void RestoreSingleItem(CacheItemModel item, Action<string> log)
    {
        string sysPath = Path.GetFullPath(item.DefaultSystemPath).TrimEnd('\\');
        if (!JunctionHelper.IsReparsePoint(sysPath))
        {
            log($"{sysPath} is already a normal local folder on C:.");
            if (item.SetsOpenGlEnvVar)
                JunctionHelper.SetUserEnvironmentVariableAndBroadcast(OpenGlCacheEnvVar, null);
            return;
        }

        string? currentTarget = JunctionHelper.GetJunctionTarget(sysPath);
        string stagingPath = sysPath + "_restore_tmp";
        if (JunctionHelper.DirectoryExistsRaw(stagingPath))
            JunctionHelper.SafeDeleteRealDirectory(stagingPath);

        Directory.CreateDirectory(stagingPath);

        var excludedSet = item.ExcludedSiblingFoldersWhenSharedRoot.Length > 0
            ? new HashSet<string>(item.ExcludedSiblingFoldersWhenSharedRoot, StringComparer.OrdinalIgnoreCase)
            : null;

        if (!string.IsNullOrWhiteSpace(currentTarget) && Directory.Exists(currentTarget))
        {
            log($"Copying cache back from {currentTarget} -> C: ...");
            CopyDirectoryPass(currentTarget, stagingPath, excludedSet, overwrite: false);
            LiveHandleUnlocker.UnlockDirectory(currentTarget, log, excludedSet);

            int failed = CopyDirectoryPass(currentTarget, stagingPath, excludedSet, overwrite: false);
            if (failed > 0)
                throw new IOException($"{failed} file(s) in {currentTarget} could not be copied back to C:.");
        }

        JunctionHelper.RemoveJunctionOnly(sysPath);
        Directory.Move(stagingPath, sysPath);
        EnsureExpectedSubfolders(sysPath, item.ExpectedChildFolders);

        if (item.SetsOpenGlEnvVar)
            JunctionHelper.SetUserEnvironmentVariableAndBroadcast(OpenGlCacheEnvVar, null);

        if (!string.IsNullOrWhiteSpace(currentTarget) && Directory.Exists(currentTarget))
            CleanupMigratedSource(currentTarget, excludedSet, log);

        log($"Restored {sysPath} to local folder on C:.");
    }

    private static void EnsureExpectedSubfolders(string baseDir, string[] childNames)
    {
        Directory.CreateDirectory(baseDir);
        foreach (string child in childNames)
            Directory.CreateDirectory(Path.Combine(baseDir, child));
    }

    private static void ApplyEnvVarsIfNeeded(CacheItemModel item, string desiredTarget, Action<string> log)
    {
        if (!item.SetsOpenGlEnvVar)
            return;

        string glPath = Path.Combine(desiredTarget, "GLCache");
        Directory.CreateDirectory(glPath);
        JunctionHelper.SetUserEnvironmentVariableAndBroadcast(OpenGlCacheEnvVar, glPath);
        log($"Updated {OpenGlCacheEnvVar} = {glPath}");
    }

    private static void CleanupMigratedSource(string sourceDir, HashSet<string>? excludedTopSubdirs, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(sourceDir))
                return;

            if (excludedTopSubdirs is { Count: > 0 })
            {
                foreach (string subDir in Directory.GetDirectories(sourceDir))
                {
                    string name = Path.GetFileName(subDir);
                    if (excludedTopSubdirs.Contains(name))
                        continue;
                    JunctionHelper.SafeDeleteRealDirectory(subDir);
                }

                foreach (string file in Directory.GetFiles(sourceDir))
                {
                    var attrs = File.GetAttributes(file);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    File.Delete(file);
                }
            }
            else
            {
                JunctionHelper.SafeDeleteRealDirectory(sourceDir);
            }
        }
        catch (Exception ex)
        {
            log($"Note: Some old source files in {sourceDir} will be cleaned up on next run ({ex.Message}).");
        }
    }

    /// <summary>
    /// Copies files from sourceDir to destDir, returning count of files that failed to copy.
    /// </summary>
    private static int CopyDirectoryPass(
        string sourceDir,
        string destDir,
        HashSet<string>? excludedTopSubdirs,
        bool overwrite,
        bool isRoot = true)
    {
        int failures = 0;
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            try
            {
                if (!overwrite && File.Exists(destFile))
                {
                    var srcInfo = new FileInfo(file);
                    var dstInfo = new FileInfo(destFile);
                    if (srcInfo.Length == dstInfo.Length)
                        continue;
                }
                File.Copy(file, destFile, overwrite: true);
            }
            catch
            {
                failures++;
            }
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(subDir);
            if (isRoot && excludedTopSubdirs != null && excludedTopSubdirs.Contains(dirName))
                continue;

            if (JunctionHelper.IsReparsePoint(subDir))
                continue;

            failures += CopyDirectoryPass(subDir, Path.Combine(destDir, dirName), null, overwrite, isRoot: false);
        }

        return failures;
    }

    private static int CopyDirectoryResolvingChildJunctions(string sourceDir, string destDir)
    {
        int failures = 0;
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            try
            {
                if (File.Exists(destFile))
                {
                    var s = new FileInfo(file);
                    var d = new FileInfo(destFile);
                    if (s.Length == d.Length)
                        continue;
                }
                File.Copy(file, destFile, overwrite: true);
            }
            catch
            {
                failures++;
            }
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(subDir);
            string nextDest = Path.Combine(destDir, dirName);

            if (JunctionHelper.IsReparsePoint(subDir))
            {
                string? target = JunctionHelper.GetJunctionTarget(subDir);
                if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target) && !JunctionHelper.PathsEqual(target, nextDest))
                {
                    failures += CopyDirectoryPass(target, nextDest, null, overwrite: false);
                }
            }
            else
            {
                failures += CopyDirectoryResolvingChildJunctions(subDir, nextDest);
            }
        }

        return failures;
    }

    private static string? FindFirstChildJunctionTarget(string dirPath)
    {
        try
        {
            foreach (string sub in Directory.GetDirectories(dirPath))
            {
                string? target = JunctionHelper.GetJunctionTarget(sub);
                if (!string.IsNullOrWhiteSpace(target))
                    return target;
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    private static List<string> GetChildJunctionTargets(string dirPath)
    {
        var list = new List<string>();
        try
        {
            foreach (string sub in Directory.GetDirectories(dirPath))
            {
                string? target = JunctionHelper.GetJunctionTarget(sub);
                if (!string.IsNullOrWhiteSpace(target))
                    list.Add(target);
            }
        }
        catch
        {
            // Ignore
        }
        return list;
    }

    private static long ComputeDirectorySize(string dirPath, HashSet<string>? excludedTopSubdirs)
    {
        long total = 0;
        var stack = new Stack<(string Path, bool IsRoot)>();
        stack.Push((dirPath, true));

        while (stack.Count > 0)
        {
            var (cur, isRoot) = stack.Pop();
            try
            {
                var di = new DirectoryInfo(cur);
                foreach (var fi in di.EnumerateFiles())
                    total += fi.Length;

                foreach (var sub in di.EnumerateDirectories())
                {
                    if (isRoot && excludedTopSubdirs != null && excludedTopSubdirs.Contains(sub.Name))
                        continue;
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    stack.Push((sub.FullName, false));
                }
            }
            catch
            {
                // Ignore inaccessible subfolders
            }
        }

        return total;
    }

    private static long ComputeDirectorySizeIncludingChildJunctions(string dirPath)
    {
        long total = 0;
        try
        {
            var di = new DirectoryInfo(dirPath);
            foreach (var fi in di.EnumerateFiles())
                total += fi.Length;

            foreach (var sub in di.EnumerateDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    string? target = JunctionHelper.GetJunctionTarget(sub.FullName);
                    if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
                        total += ComputeDirectorySize(target, null);
                }
                else
                {
                    total += ComputeDirectorySize(sub.FullName, null);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return total;
    }
}
