using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NvidiaCacheMover.Services;

public static class JunctionHelper
{
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    public static bool DirectoryExistsRaw(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            return di.Exists || di.Attributes != (FileAttributes)(-1);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if ((int)di.Attributes == -1)
                return false;
            return di.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    public static string? GetJunctionTarget(string path)
    {
        try
        {
            if (!IsReparsePoint(path))
                return null;

            var di = new DirectoryInfo(path);
            if (!string.IsNullOrWhiteSpace(di.LinkTarget))
            {
                string raw = di.LinkTarget;
                if (raw.StartsWith(@"\??\", StringComparison.Ordinal))
                    raw = raw[4..];
                return Path.GetFullPath(raw).TrimEnd('\\');
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static void CreateJunction(string junctionPath, string targetPath)
    {
        string fullJunction = Path.GetFullPath(junctionPath).TrimEnd('\\');
        string fullTarget = Path.GetFullPath(targetPath).TrimEnd('\\');

        Directory.CreateDirectory(fullTarget);

        string? parent = Path.GetDirectoryName(fullJunction);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (DirectoryExistsRaw(fullJunction))
            throw new IOException($"Cannot create junction because path still exists: {fullJunction}");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{fullJunction}\" \"{fullTarget}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch cmd.exe for mklink.");
        string stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);

        if (proc.ExitCode != 0 || !IsReparsePoint(fullJunction))
            throw new IOException($"mklink /J failed ({proc.ExitCode}): {stdErr.Trim()}");
    }

    public static void RemoveJunctionOnly(string junctionPath)
    {
        string full = Path.GetFullPath(junctionPath).TrimEnd('\\');
        if (!IsReparsePoint(full))
            throw new InvalidOperationException($"Refusing to call RemoveJunctionOnly on non-reparse directory: {full}");

        Directory.Delete(full, recursive: false);
    }

    /// <summary>
    /// Safely deletes a real directory tree. Any child junctions encountered inside are unlinked
    /// WITHOUT traversing into their targets.
    /// </summary>
    public static void SafeDeleteRealDirectory(string dirPath)
    {
        string full = Path.GetFullPath(dirPath).TrimEnd('\\');
        if (!DirectoryExistsRaw(full))
            return;

        if (IsReparsePoint(full))
        {
            RemoveJunctionOnly(full);
            return;
        }

        foreach (string subDir in Directory.GetDirectories(full))
        {
            if (IsReparsePoint(subDir))
                RemoveJunctionOnly(subDir);
            else
                SafeDeleteRealDirectory(subDir);
        }

        foreach (string file in Directory.GetFiles(full))
        {
            var attrs = File.GetAttributes(file);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }

        Directory.Delete(full, recursive: false);
    }

    public static void SetUserEnvironmentVariableAndBroadcast(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 1500, out _);
    }

    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        return string.Equals(
            Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }
}
