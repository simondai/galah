$code = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class FT7 {
    [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    public static string Test(string path, string savePath) {
        Guid clsidHandler = new Guid("7A80E4A8-8005-11D2-BCF8-00C04F72C717");
        Guid iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        object obj;
        CoCreateInstance(ref clsidHandler, IntPtr.Zero, 1, ref iidUnknown, out obj);
        var pf = obj as IPersistFile;
        pf.Load(path, 0);
        var xi = obj as IExtractIconW;
        var sb = new StringBuilder(1024);
        int index; uint fl;
        xi.GetIconLocation(0, sb, 1024, out index, out fl);
        IntPtr big, small;
        xi.Extract(sb.ToString(), unchecked((uint)index), out big, out small, 0x20 | 0x10);
        if (big == IntPtr.Zero) return "no big icon";
        int size = 32;
        IntPtr bmp = CreateHBitmapFromIcon(big, size);
        SavePng(bmp, savePath);
        DeleteObject(bmp);
        DestroyIcon(big); DestroyIcon(small);
        return "saved " + savePath;
    }
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr flags, uint flags2);
    [DllImport("gdiplus.dll")] public static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr pal, out IntPtr bmp);
    [DllImport("gdiplus.dll")] public static extern int GdipSaveImageToFile(IntPtr img, [MarshalAs(UnmanagedType.LPWStr)] string file, ref Guid clsidEncoder, IntPtr encParams);
    [DllImport("gdiplus.dll")] public static extern int GdiplusStartup(out ulong token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")] public static extern int GdipDisposeImage(IntPtr img);
    public struct GdiplusStartupInput { public int GdiplusVersion; public IntPtr DebugEventCallback; public bool SuppressBackgroundThread; public bool SuppressExternalCodecs; }
    static IntPtr CreateHBitmapFromIcon(IntPtr icon, int size) {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr bmp = CreateCompatibleBitmap(dc, size, size);
        IntPtr old = SelectObject(dc, bmp);
        DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 0);
        SelectObject(dc, old);
        DeleteDC(dc);
        return bmp;
    }
    static void SavePng(IntPtr hbm, string path) {
        ulong token;
        var input = new GdiplusStartupInput(); input.GdiplusVersion = 1;
        GdiplusStartup(out token, ref input, IntPtr.Zero);
        IntPtr bmp;
        GdipCreateBitmapFromHBITMAP(hbm, IntPtr.Zero, out bmp);
        Guid pngEncoder = new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");
        GdipSaveImageToFile(bmp, path, ref pngEncoder, IntPtr.Zero);
        GdipDisposeImage(bmp);
    }
    [ComImport, Guid("000214FA-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExtractIconW {
        [PreserveSig] int GetIconLocation(uint flags, [Out] StringBuilder file, int cch, out int index, out uint flagsOut);
        [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPWStr)] string file, uint index, out IntPtr large, out IntPtr small, uint size);
    }
    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
'@
Add-Type -TypeDefinition $code
[FT7]::Test('C:\Windows\System32\devmgmt.msc', 'D:\Rolan_desktop\galah\handler-devmgmt.png')
[FT7]::Test('C:\Windows\System32\services.msc', 'D:\Rolan_desktop\galah\handler-services.png')
