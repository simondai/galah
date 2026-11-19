using System.Runtime.InteropServices;
using Galah.Persistence;
using Galah.Services.Interop;

namespace Galah.Services;

/// <summary>Snapshot of the four fields Galah imports from a shell link (spec §2.1 / design §3.4).</summary>
public sealed record ShellLinkInfo(
    string Target,
    string? Arguments,
    string? WorkingDirectory,
    string? IconSource,
    int IconIndex,
    string? Error)
{
    /// <summary>True when a usable target path was recovered.</summary>
    public bool Ok => Error is null && !string.IsNullOrWhiteSpace(Target);
}

/// <summary>
/// Reads (and, for test fixtures, writes) .lnk files through IShellLinkW. Import is a one-way
/// snapshot: after committing, the item has no relationship left with the source .lnk (design §3.4).
/// </summary>
public static class LinkResolver
{
    private const uint SlrNoUi = 0x00000001;
    private const int StgmRead = 0x00000000;
    private const int StgmReadWrite = 0x00000002;

    /// <summary>
    /// Resolve a .lnk into target / arguments / working directory / icon location, following the
    /// shell's own resolution (relative paths, drive substitution) but never prompting the user.
    /// </summary>
    /// <param name="lnkPath">Absolute or relative path of the .lnk file.</param>
    /// <param name="owner">Optional owner window handle (0 — the locator UI is suppressed).</param>
    /// <returns>A snapshot; <see cref="ShellLinkInfo.Ok"/> is false when the file could not be read.</returns>
    public static ShellLinkInfo Resolve(string lnkPath, IntPtr owner = default)
    {
        if (string.IsNullOrWhiteSpace(lnkPath))
            return new ShellLinkInfo("", null, null, null, 0, "空路径");

        var osPath = PathNorm.ToOs(lnkPath);
        if (!File.Exists(osPath))
            return new ShellLinkInfo("", null, null, null, 0, "快捷方式不存在");

        object? shellLink = null;
        try
        {
            shellLink = new ShellLinkCoClass();
            var link = (IShellLinkW)shellLink;
            var persist = (IPersistFile)shellLink;
            persist.Load(Path.GetFullPath(osPath), StgmRead);
            try { link.Resolve(owner, SlrNoUi); } catch (COMException) { /* keep whatever the file already says */ }

            var target = ComBuffer.Read(ComBuffer.MaxPath, p => link.GetPath(p, ComBuffer.MaxPath, IntPtr.Zero, 0));
            var args = ComBuffer.Read(ComBuffer.MaxPath, p => link.GetArguments(p, ComBuffer.MaxPath));
            var workdir = ComBuffer.Read(ComBuffer.MaxPath, p => link.GetWorkingDirectory(p, ComBuffer.MaxPath));
            var iconIndex = 0;
            var icon = ComBuffer.Read(ComBuffer.MaxPath, p => link.GetIconLocation(p, ComBuffer.MaxPath, out iconIndex));
            // Some authoring tools bake the index into the path string instead of the out parameter.
            if (icon.Length > 0)
            {
                var comma = icon.LastIndexOf(',');
                if (comma > 1 && int.TryParse(icon[(comma + 1)..], out var baked))
                {
                    icon = icon[..comma];
                    if (iconIndex == 0) iconIndex = baked;
                }
            }
            if (icon.Length == 0) iconIndex = 0;

            if (string.IsNullOrWhiteSpace(target))
                return new ShellLinkInfo("", null, null, null, 0, "快捷方式没有可用的目标路径");

            return new ShellLinkInfo(
                PathNorm.ToStored(target),
                string.IsNullOrWhiteSpace(args) ? null : args,
                string.IsNullOrWhiteSpace(workdir) ? null : PathNorm.ToStored(workdir),
                string.IsNullOrWhiteSpace(icon) ? null : PathNorm.ToStored(icon),
                iconIndex,
                null);
        }
        catch (COMException ex)
        {
            return new ShellLinkInfo("", null, null, null, 0, "快捷方式无法解析: " + ex.Message);
        }
        catch (InvalidCastException)
        {
            return new ShellLinkInfo("", null, null, null, 0, "快捷方式组件不可用");
        }
        finally
        {
            if (shellLink is not null) Marshal.ReleaseComObject(shellLink);
        }
    }

    /// <summary>
    /// Write a .lnk with the given triple. Used by the quality-gate test fixture so the tests need no
    /// external tooling; it exercises the same vtable as <see cref="Resolve"/>.
    /// </summary>
    /// <returns>True when the file was created.</returns>
    public static bool TryCreate(string lnkPath, string target, string? arguments, string? workingDirectory,
        string? iconLocation, int iconIndex)
    {
        object? shellLink = null;
        try
        {
            shellLink = new ShellLinkCoClass();
            var link = (IShellLinkW)shellLink;
            link.SetPath(PathNorm.ToOs(target));
            if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
            if (!string.IsNullOrEmpty(workingDirectory)) link.SetWorkingDirectory(PathNorm.ToOs(workingDirectory));
            if (!string.IsNullOrEmpty(iconLocation)) link.SetIconLocation(PathNorm.ToOs(iconLocation), iconIndex);
            ((IPersistFile)shellLink).Save(Path.GetFullPath(PathNorm.ToOs(lnkPath)), true);
            return File.Exists(PathNorm.ToOs(lnkPath));
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (shellLink is not null) Marshal.ReleaseComObject(shellLink);
        }
    }
}
