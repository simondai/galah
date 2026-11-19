$code = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class FT9 {
    [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    public static string Test(string path) {
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
        if (big == IntPtr.Zero) return "no big";
        ICONINFO ii;
        GetIconInfo(big, out ii);
        BITMAP bmp;
        GetObjectW(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bmp);
        int w = bmp.bmWidth, h = bmp.bmHeight;
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = w; bmi.bmiHeader.biHeight = -h; bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32; bmi.bmiHeader.biCompression = 0;
        var bits = new byte[w*h*4];
        IntPtr dc = GetDC(IntPtr.Zero);
        GetDIBits(dc, ii.hbmColor, 0, (uint)h, bits, ref bmi, 0);
        ReleaseDC(IntPtr.Zero, dc);
        // Count distinct colors more carefully: any pixel with high saturation or multi-color = real icon
        int distinct = 0;
        var seen = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < bits.Length; i += 4) {
            if (bits[i+3] > 10) seen.Add("" + (bits[i+2]/16) + (bits[i+1]/16) + (bits[i]/16));
        }
        distinct = seen.Count;
        DeleteObject(ii.hbmColor); DeleteObject(ii.hbmMask);
        DestroyIcon(big); DestroyIcon(small);
        return path.Substring(path.Length - 15) + " size=" + w + "x" + h + " distinctColorBuckets=" + distinct;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER { public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage; public int biXPels; public int biYPels; public uint biClrUsed; public uint biClrImportant; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType; public int bmWidth; public int bmHeight; public int bmWidthBytes; public ushort bmPlanes; public ushort bmBitsPixel; public IntPtr bmBits; }
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO ii);
    [DllImport("gdi32.dll", EntryPoint="GetObjectW")] public static extern bool GetObjectW(IntPtr obj, int cb, out BITMAP bmp);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
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
[FT9]::Test('C:\Windows\System32\devmgmt.msc')
[FT9]::Test('C:\Windows\System32\services.msc')
[FT9]::Test('C:\Windows\System32\taskmgr.exe')
