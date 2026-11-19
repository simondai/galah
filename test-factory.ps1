Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class FactoryTest {
    [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr pv, uint coInit);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, out IntPtr ppv);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hbm);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER { public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage; public int biXPels; public int biYPels; public uint biClrUsed; public uint biClrImportant; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    public static string Test(string path) {
        Guid iid = new Guid("bcc18b79-ba16-442f-80ce-2a178c116e08");
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IntPtr punk);
        if (hr != 0 || punk == IntPtr.Zero) return "create hr=0x" + hr.ToString("X");
        IntPtr vtbl = Marshal.ReadIntPtr(punk);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
        var del = (GetImageDel)Marshal.GetDelegateForFunctionPointer(fn, typeof(GetImageDel));
        int hrG = del(punk, 32, 32, 0, out IntPtr hbm);
        if (hrG != 0 || hbm == IntPtr.Zero) { Marshal.Release(punk); return "GetImage hr=0x" + hrG.ToString("X"); }
        // dump pixels
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = 32; bmi.bmiHeader.biHeight = -32; bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32; bmi.bmiHeader.biCompression = 0;
        var bits = new byte[32*32*4];
        IntPtr dc = GetDC(IntPtr.Zero);
        GetDIBits(dc, hbm, 0, 32, bits, ref bmi, 0);
        ReleaseDC(IntPtr.Zero, dc);
        DeleteObject(hbm);
        Marshal.Release(punk);
        // count non-transparent + sample colors
        int non = 0;
        var colors = new System.Collections.Generic.Dictionary<string,int>();
        for (int i = 0; i < bits.Length; i += 4) {
            if (bits[i+3] > 0) {
                non++;
                string key = bits[i+2] + "," + bits[i+1] + "," + bits[i];
                colors[key] = (colors.ContainsKey(key) ? colors[key] : 0) + 1;
            }
        }
        var top = new System.Text.StringBuilder();
        int n = 0;
        foreach (var kv in colors) { if (n++ >= 5) break; top.Append(kv.Key + ":" + kv.Value + " "); }
        return "GetImage OK nonTransparent=" + non + "/1024 colors: " + top.ToString();
    }
    delegate int GetImageDel(IntPtr self, int cx, int cy, uint flags, out IntPtr hbm);
}
'@
[FactoryTest]::Test('C:\Windows\System32\notepad.exe')
[FactoryTest]::Test('C:\Windows\System32\devmgmt.msc')
[FactoryTest]::Test('C:\Windows\System32\services.msc')
[FactoryTest]::Test('C:\Windows\System32\eventvwr.msc')
[FactoryTest]::Test('C:\Windows\System32\taskschd.msc')
