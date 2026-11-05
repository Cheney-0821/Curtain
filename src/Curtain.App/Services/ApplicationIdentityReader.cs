using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Curtain.App.Interop;
using Curtain.Core.Protection;

namespace Curtain.App.Services;

internal sealed record ApplicationIdentityReadResult(
    bool Success,
    ProtectedAppIdentity? Identity,
    string ReasonCode,
    int Win32Error = 0,
    string? ProcessName = null);

/// <summary>
/// Reads stable application identity from a live top-level window's process. Results are
/// cached by PID + process start time so multi-window apps are inspected only once per run.
/// </summary>
internal static class ApplicationIdentityReader
{
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumCacheEntries = 512;
    private readonly record struct ProcessCacheKey(uint ProcessId, long StartTimeTicks);
    private static readonly ConcurrentDictionary<ProcessCacheKey, ApplicationIdentityReadResult> Cache = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetPackageFamilyName(nint process, ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetApplicationUserModelId(nint process, ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);

    internal static ApplicationIdentityReadResult TryRead(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return new(false, null, "WindowInvalid");
        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (threadId == 0 || processId == 0) return new(false, null, "ProcessIdUnavailable");

        string? processName = null;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            long startTicks = 0;
            try { startTicks = process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException) { }

            ProcessCacheKey? key = startTicks > 0 ? new ProcessCacheKey(processId, startTicks) : null;
            if (key is { } cacheKey && Cache.TryGetValue(cacheKey, out ApplicationIdentityReadResult? cached) && cached is not null)
                return cached;

            ApplicationIdentityReadResult result = ReadProcessIdentity(process, processName);
            if (key is { } writeKey)
            {
                Cache[writeKey] = result;
                if (Cache.Count > MaximumCacheEntries) Cache.Clear();
            }
            return result;
        }
        catch (ArgumentException ex)
        {
            return new(false, null, "ProcessExitedDuringIdentityRead", ex.HResult, processName);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, "ProcessUnavailable", ex.HResult, processName);
        }
        catch (Win32Exception ex)
        {
            return new(false, null, "ProcessAccessDenied", ex.NativeErrorCode, processName);
        }
        catch (System.Security.SecurityException ex)
        {
            return new(false, null, "ProcessAccessDenied", ex.HResult, processName);
        }
    }

    private static ApplicationIdentityReadResult ReadProcessIdentity(Process process, string processName)
    {
        string? executablePath = null;
        int imageReadError = 0;
        string displayName = processName;
        string? publisher = null;
        try
        {
            var mainModule = process.MainModule;
            string? mainModulePath = mainModule?.FileName;
            executablePath = string.IsNullOrWhiteSpace(mainModulePath) ? null : mainModulePath;
            var version = mainModule?.FileVersionInfo;
            if (!string.IsNullOrWhiteSpace(version?.FileDescription)) displayName = version.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(version?.ProductName)) displayName = version.ProductName.Trim();
            publisher = string.IsNullOrWhiteSpace(version?.CompanyName) ? null : version.CompanyName.Trim();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            imageReadError = ex is Win32Exception win32 ? win32.NativeErrorCode : ex.HResult;
        }

        string? packageFamilyName = null;
        string? applicationUserModelId = null;
        try
        {
            packageFamilyName = ReadPackageFamilyName(process.Handle);
            applicationUserModelId = ReadApplicationUserModelId(process.Handle);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            if (imageReadError == 0)
                imageReadError = ex is Win32Exception win32 ? win32.NativeErrorCode : ex.HResult;
        }

        var identity = new ProtectedAppIdentity
        {
            ExecutablePath = executablePath ?? string.Empty,
            PackageFamilyName = packageFamilyName,
            ApplicationUserModelId = applicationUserModelId,
            DisplayName = displayName,
            Publisher = publisher
        }.Normalize();

        return identity.IsValid
            ? new(true, identity, "IdentityRead", 0, processName)
            : new(false, null, imageReadError != 0 ? "ProcessImageAccessFailed" : "NoStableApplicationIdentity",
                imageReadError, processName);
    }

    private static string? ReadPackageFamilyName(nint process)
    {
        uint length = 0;
        int result = GetPackageFamilyName(process, ref length, null);
        if (result != ErrorInsufficientBuffer || length is 0 or > 1024) return null;
        var buffer = new StringBuilder((int)length);
        result = GetPackageFamilyName(process, ref length, buffer);
        return result == 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }

    private static string? ReadApplicationUserModelId(nint process)
    {
        uint length = 0;
        int result = GetApplicationUserModelId(process, ref length, null);
        if (result != ErrorInsufficientBuffer || length is 0 or > 2048) return null;
        var buffer = new StringBuilder((int)length);
        result = GetApplicationUserModelId(process, ref length, buffer);
        return result == 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }
}
