using System.Runtime.InteropServices;

namespace Galah.Services.Interop;

/// <summary>
/// Hand-written COM declarations for the shell link (design §4: LinkResolver via COM interop, no
/// NuGet packages). VTable order follows the published IShellLink definition exactly.
/// </summary>
[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ShellLinkCoClass
{
}

/// <summary>IPersistFile (used to load and save the .lnk container).</summary>
[ComImport]
[Guid("0000010b-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    [PreserveSig]
    int GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);

    void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName);

    void GetCurFile(out IntPtr ppszFileName);
}

/// <summary>IShellLinkW — the wide shell link interface (shell32).</summary>
[ComImport]
[Guid("000214f9-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath(IntPtr pszPath, int cchMaxPath, IntPtr pfd, uint slgpFlags);

    void GetIDList(out IntPtr ppidl);

    void SetIDList(IntPtr pidl);

    void GetDescription(IntPtr pszName, int cchMaxName);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

    void GetWorkingDirectory(IntPtr pszDir, int cchMaxPath);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

    void GetArguments(IntPtr pszArgs, int cchMaxPath);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

    void GetHotkey(out ushort pwHotkey);

    void SetHotkey(ushort wHotkey);

    void GetShowCmd(out int piShowCmd);

    void SetShowCmd(int iShowCmd);

    void GetIconLocation(IntPtr pszIconPath, int cchMaxPath, out int piIcon);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

    /// <summary>Resolve the link; <paramref name="fFlags"/> = 1 (SLR_NOUI) never shows the locator UI.</summary>
    void Resolve(IntPtr hwnd, uint fFlags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

/// <summary>Helpers to read fixed-size wide buffers returned by the shell link vtable.</summary>
internal static class ComBuffer
{
    public const int MaxPath = 2048;

    /// <summary>Allocate a zeroed unicode buffer, run <paramref name="read"/>, return the text ("" when empty).</summary>
    public static string Read(int chars, Action<IntPtr> read)
    {
        var bytes = chars * 2;
        var ptr = Marshal.AllocHGlobal(bytes);
        try
        {
            for (var i = 0; i < bytes; i += IntPtr.Size) Marshal.WriteIntPtr(ptr, i, IntPtr.Zero);
            read(ptr);
            var text = Marshal.PtrToStringUni(ptr) ?? "";
            return text;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
