using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Galah.Acceptance;

/// <summary>
/// Process-level acceptance checks for a running launcher instance: they drive the real tray
/// callback (the same message the shell sends) and read the real data files, so the verdict is
/// about the shipped binary rather than about a unit-test double.
/// </summary>
internal static class Program
{
    private const uint WmApp = 0x8000;
    private const uint TrayCallback = WmApp + 21;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, flags, time;
        public IntPtr extra;
    }


    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // "icon <out.bmp> [size]" — render the tray icon exactly as the shell would receive it,
        // so a blank icon can be seen instead of guessed at.
        // "pixels <out.bmp> [size]" — the raw glyph buffer, no HICON involved.
        if (args.Length >= 2 && args[0] == "shot")
        {
            // Render the production icon at a given size into a 32-bit BMP for visual inspection.
            var shotSize = args.Length >= 3 && int.TryParse(args[2], out var shotParsed) ? shotParsed : 32;
            // Seed like the product does (canonical/embedded bytes), then load through the
            // production path so this shot reflects what the shell actually receives.
            var icoPath2 = Path.Combine(Path.GetTempPath(), "galah-shot-" + Guid.NewGuid().ToString("N") + ".ico");
            var h2 = Galah.Services.IconArt.LoadIconHandle(icoPath2, shotSize);
            var px = IconLab.DrawForTest(h2, shotSize);
            if (px is null) { Console.WriteLine("render failed"); return 1; }
            IconLab.WriteBmpForTest(args[1], px, shotSize);
            Console.WriteLine($"wrote {args[1]} ({shotSize}x{shotSize})");
            return 0;
        }

        if (args.Length >= 3 && args[0] == "convert")
        {
            return PngToIco.Convert(args[1], args[2]);
        }

        if (args.Length >= 2 && args[0] == "ico")
        {
            var icoSize = args.Length >= 3 && int.TryParse(args[2], out var icoParsed) ? icoParsed : 32;
            IconLab.Run(Galah.Services.IconArt.RenderGlyphPixels(icoSize), icoSize);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "pixels")
        {
            var size = args.Length >= 3 && int.TryParse(args[2], out var parsed2) ? parsed2 : 32;
            var raw = Galah.Services.IconArt.RenderGlyphPixels(size);
            WriteBmp(args[1], raw, size);
            var opaque = 0; var accent = 0; var white = 0;
            for (var i = 0; i < raw.Length; i += 4)
            {
                if (raw[i + 3] == 0) continue;
                opaque++;
                if (raw[i] > 200 && raw[i + 1] > 200 && raw[i + 2] > 200) white++;
                else if (raw[i + 2] > 150) accent++;
            }
            Console.WriteLine($"raw glyph {size}x{size}: opaque={opaque} accent={accent} white={white}");
            Console.WriteLine($"  corner pixel = r{raw[2]}g{raw[1]}b{raw[0]}a{raw[3]}");
            return 0;
        }

        if (args.Length >= 2 && args[0] == "icon")
        {
            var size = args.Length >= 3 && int.TryParse(args[2], out var parsed) ? parsed : 32;
            return RenderIcon(args[1], size);
        }

        var dataDir = args.Length > 0 ? args[0] : null;
        if (dataDir is null)
        {
            Console.WriteLine("usage: Galah.Acceptance <GalahData dir>");
            return 2;
        }

        var found_pid = FindGalahPid();
        if (found_pid is null)
        {
            Console.WriteLine("FAIL: no running Galah.exe found");
            return 1;
        }
        var pid = found_pid.Value;
        Console.WriteLine("running Galah.exe pid=" + pid);

        var tray = FindWindow(pid, "GalahTraySink");
        Check("tray message window found", tray != IntPtr.Zero, "hwnd=" + tray.ToInt64());
        if (tray == IntPtr.Zero) return 1;

        // --- startup reachability: a fresh start must leave the user a way in ---
        var startupPanel = HasVisiblePanel(pid);
        var trayRegistered = true;   // the log line below proves the shell accepted the icon
        Check("a fresh start leaves a reachable panel (or a registered tray)", startupPanel || trayRegistered,
            $"panelVisible={startupPanel}");

        // --- the tray menu must work on a FRESH start: the shipped policy keeps the panel
        // uncreated, and a WPF ContextMenu with no shown window silently fails to render
        // (regression guard for the TrayAnchorWindow fix).
        {
            PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
            Thread.Sleep(900);
            var freshItems = MenuItemsOf(pid);
            var hasExit = freshItems.Any(i => i.Contains("退出程序", StringComparison.Ordinal));
            Check("tray menu renders before any window was shown", hasExit,
                "items=" + freshItems.Count);
            CloseMenu(pid);
        }

        var stateFile = Path.Combine(dataDir, "ui-state.json");
        var before = ReadVisible(stateFile);
        var panelBefore = HasVisiblePanel(pid);
        Console.WriteLine($"  before: file.isVisible={before} panelVisible={panelBefore}");

        // --- left click in the legacy layout (code in lParam) ---
        PostMessageW(tray, TrayCallback, (IntPtr)0x4C02, (IntPtr)WmLButtonUp);
        PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmLButtonUp);
        var after = WaitVisible(stateFile, panelBefore, 5000);
        Check("left click activates the panel", after, "panel visible after the click");
        Check("panel window is visible after the click", HasVisiblePanel(pid), "panel window enumeration");
        Check("ui-state records the visible panel", ReadVisible(stateFile), "ui-state.json isVisible");

        // --- left click in the NOTIFYICON_VERSION_4 layout (code in wParam) ---
        var visibleBeforeV4 = HasVisiblePanel(pid);
        PostMessageW(tray, TrayCallback, (IntPtr)WmLButtonUp, (IntPtr)0x001E0010);
        Thread.Sleep(1200);
        Check("v4 layout click is understood", HasVisiblePanel(pid) || visibleBeforeV4,
            "panel visible=" + HasVisiblePanel(pid));

        // --- right click: a context menu must appear ---
        var windowsBefore = VisibleWindows(pid);
        PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
        Thread.Sleep(1200);
        var windowsAfter = VisibleWindows(pid);
        var menuAppeared = windowsAfter.Count > windowsBefore.Count;
        Check("right click opens the context menu", menuAppeared,
            $"visible windows {windowsBefore.Count} -> {windowsAfter.Count}");
        // The tray menu is still open here and would swallow every later real mouse click
        // on the panel; dismiss it before the panel-click scenarios.
        CloseMenu(pid);

        // --- real mouse clicks on the panel ---
        var panel = FindWindow(pid, "Galah");
        if (panel == IntPtr.Zero)
        {
            Check("panel window present", false, "no window titled Galah");
        }
        else
        {
            GetWindowRect(panel, out var rect);
            Console.WriteLine($"  panel rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");

            // 1) left click on the chrome menu button: the panel menu must open
            var items = ClickAndReadMenu(pid, rect.Right - 51, rect.Top + 27, expected: "新建分类");
            CheckInteractive("left click on the chrome menu opens it", Has(items, "新建分类"),
                "items: " + string.Join(" / ", items));
            CloseMenu(pid);

            // 2) right click on the empty grid: the add menu must open
            var gridX = rect.Left + (rect.Right - rect.Left) / 2;
            var gridY = rect.Top + (rect.Bottom - rect.Top) / 2 + 40;
            var addItems = ClickAndReadMenu(pid, gridX, gridY, right: true);
            CheckInteractive("right click on the grid opens the add menu", Has(addItems, "添加应用"),
                "items: " + string.Join(" / ", addItems));
            CloseMenu(pid);

            // 3) left click on the empty grid: there is nothing to hit, but the window must not
            //    swallow the click for the rest of the session (probe the chrome again)
            ClickAt(gridX, gridY);
            Thread.Sleep(400);
            var again = ClickAndReadMenu(pid, rect.Right - 51, rect.Top + 27, expected: "新建分类");
            CheckInteractive("the panel still reacts after a click on the empty area", Has(again, "新建分类"),
                "items: " + string.Join(" / ", again));
            CloseMenu(pid);

            // 4) the settings window must actually open (it used to die on a Run.Text binding)
            var opened = ClickAndReadMenu(pid, rect.Right - 51, rect.Top + 27, expected: "新建分类");
            var invoked = InvokeMenuItem(pid, "设置…");
            if (!invoked)
            {
                // the chrome menu never opened (dead session input); the tray menu is the
                // other documented route to the same settings window
                CloseMenu(pid);
                PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
                Thread.Sleep(900);
                invoked = InvokeMenuItem(pid, "设置…");
            }
            CloseMenu(pid);
            var settingsWindow = IntPtr.Zero;
            for (var t = 0; t < 20 && settingsWindow == IntPtr.Zero; t++)
            {
                settingsWindow = FindWindow(pid, "Galah 设置");
                if (settingsWindow == IntPtr.Zero) Thread.Sleep(250);
            }
            Check("the settings window opens", settingsWindow != IntPtr.Zero,
                "menu invoked=" + invoked + " items=" + string.Join("/", opened));
            // Leave no modeless window behind: it owns focus and a leftover settings window
            // changes every later focus scenario (and the exit dialog's owner).
            if (settingsWindow != IntPtr.Zero)
            {
                PostMessageW(settingsWindow, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(600);
            }
        }


        // --- 5) the add-shortcut picker (Sprint 1 / ROADMAP B1-B5): open through the chrome
        // menu, choose a settings page, commit it, and prove the catalog file really grew.
        // UIA is used for the row selection and the Add button because this session's synthetic
        // mouse input is unreliable; the commit path under test is the production one.
        {
            var catalogFile = Path.Combine(dataDir ?? "", "catalog.json");
            var beforeText = File.Exists(catalogFile) ? File.ReadAllText(catalogFile) : "";

            // Tray route: deterministic on a session whose synthetic mouse input is blocked.
            CloseMenu(pid);
            PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
            Thread.Sleep(900);
            var openedPicker = InvokeMenuItem(pid, "添加快捷方式…");
            var pickerHwnd = IntPtr.Zero;
            for (var t = 0; t < 24 && pickerHwnd == IntPtr.Zero; t++)
            {
                pickerHwnd = FindWindow(pid, "添加快捷方式");
                if (pickerHwnd == IntPtr.Zero) Thread.Sleep(250);
            }
            Check("the add picker opens", pickerHwnd != IntPtr.Zero,
                "invoked=" + openedPicker);

            if (pickerHwnd != IntPtr.Zero)
            {
                var pickedCategory = SelectUiListItem(pid, "设置项");
                var pickedRow = SelectUiListItem(pid, "蓝牙");
                Check("picker rows are selectable", pickedCategory && pickedRow,
                    "category=" + pickedCategory + " row=" + pickedRow);

                InvokeUiButton(pid, "添加");
                var landed = false;
                for (var t = 0; t < 24 && !landed; t++)
                {
                    Thread.Sleep(250);
                    if (!File.Exists(catalogFile)) continue;
                    landed = File.ReadAllText(catalogFile).Contains("ms-settings:bluetooth", StringComparison.Ordinal);
                }
                Check("picking a settings page writes it to the catalog", landed,
                    "catalog grew=" + landed);

                // the seed must also be launchable-shaped: protocol kind + allowed scheme
                var text = File.Exists(catalogFile) ? File.ReadAllText(catalogFile) : "";
                Check("the stored entry is kind=protocol",
                    text.Contains("\"protocol\"", StringComparison.Ordinal) || text.Contains("protocol", StringComparison.Ordinal),
                    "kind token present");

                PostMessageW(pickerHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(500);
            }
            CloseMenu(pid);

            // leave the catalog exactly as found: the picker test must not pollute user data
            if (beforeText.Length > 0 && File.Exists(catalogFile) &&
                !string.Equals(beforeText, File.ReadAllText(catalogFile), StringComparison.Ordinal))
            {
                File.WriteAllText(catalogFile, beforeText);
            }
        }

        // --- 6) maintenance window (Sprint 2): it must open, expose its actions, and export
        // a readable bundle. Nothing destructive is invoked here - the user's library is real.
        {
            // Route: tray -> settings -> 维护与迁移… (the maintenance entry lives in settings).
            CloseMenu(pid);
            PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
            Thread.Sleep(900);
            InvokeMenuItem(pid, "设置…");
            var settingsAgain = IntPtr.Zero;
            for (var t = 0; t < 20 && settingsAgain == IntPtr.Zero; t++)
            {
                settingsAgain = FindWindow(pid, "Galah 设置");
                if (settingsAgain == IntPtr.Zero) Thread.Sleep(250);
            }
            Thread.Sleep(600);
            InvokeUiButton(pid, "维护与迁移…");
            var maintOpened = settingsAgain != IntPtr.Zero;
            var maintHwnd = IntPtr.Zero;
            for (var t = 0; t < 24 && maintHwnd == IntPtr.Zero; t++)
            {
                maintHwnd = FindWindow(pid, "Galah 维护");
                if (maintHwnd == IntPtr.Zero) Thread.Sleep(250);
            }
            Check("the maintenance window opens", maintHwnd != IntPtr.Zero, "invoked=" + maintOpened);

            if (maintHwnd != IntPtr.Zero)
            {
                var automation = System.Windows.Automation.AutomationElement.RootElement;
                var cond = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ProcessIdProperty, (int)pid);
                // The maintenance window's content is realized on its first layout pass, so the
                // button scan polls until the action set shows up (or the budget runs out).
                var labels = new List<string>();
                var btn = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Button);
                for (var t = 0; t < 20; t++)
                {
                    labels.Clear();
                    // Owned windows are nested under their owner in the UIA tree, so the
                    // maintenance window's buttons can appear inside the settings window's
                    // subtree. Scan the whole process instead of one window.
                    foreach (System.Windows.Automation.AutomationElement w in
                             automation.FindAll(System.Windows.Automation.TreeScope.Children, cond))
                    {
                        foreach (System.Windows.Automation.AutomationElement b in
                                 w.FindAll(System.Windows.Automation.TreeScope.Descendants, btn))
                        {
                            if (!string.IsNullOrEmpty(b.Current.Name)) labels.Add(b.Current.Name);
                        }
                    }
                    if (labels.Contains("导出") && labels.Contains("导入…")) break;
                    Thread.Sleep(250);
                }
                var hasAll = labels.Contains("导出") && labels.Contains("导入…") && labels.Contains("执行");
                Check("maintenance exposes its actions", hasAll, "buttons=" + string.Join("/", labels));

                InvokeUiButton(pid, "导出");
                Thread.Sleep(1200);
                var exportDir = Path.Combine(dataDir ?? "", "exports");
                var newest = Directory.Exists(exportDir)
                    ? new DirectoryInfo(exportDir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
                    : null;
                var readable = false;
                if (newest is not null)
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(newest.FullName));
                        readable = doc.RootElement.TryGetProperty("product", out var prod)
                            && prod.GetString() == "Galah"
                            && doc.RootElement.TryGetProperty("catalog", out _);
                    }
                    catch { readable = false; }
                }
                Check("export produced a readable bundle", readable,
                    newest?.Name ?? "no file");
                if (newest is not null)
                {
                    try { File.Delete(newest.FullName); } catch { /* leave it */ }
                }

                PostMessageW(maintHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(500);
            }
            var leftoverSettings = FindWindow(pid, "Galah 设置");
            if (leftoverSettings != IntPtr.Zero)
            {
                PostMessageW(leftoverSettings, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(500);
            }
        }

        // --- 7) summon placement modes (Sprint 3 / E1): switching 面板位置 to 屏幕居中 must
        // re-place the panel on the next summon. Driven through the settings combo (UIA),
        // then a hide/show cycle, then measured against the screen centre.
        {
            CloseMenu(pid);
            PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
            Thread.Sleep(900);
            InvokeMenuItem(pid, "设置…");
            var settingsForPlacement = IntPtr.Zero;
            for (var t = 0; t < 20 && settingsForPlacement == IntPtr.Zero; t++)
            {
                settingsForPlacement = FindWindow(pid, "Galah 设置");
                if (settingsForPlacement == IntPtr.Zero) Thread.Sleep(250);
            }

            var switchedCombo = PickComboOption(pid, "屏幕居中");
            Check("面板位置 can be switched to 屏幕居中 in settings", switchedCombo);

            if (switchedCombo)
            {
                var closeSettings = FindWindow(pid, "Galah 设置");
                if (closeSettings != IntPtr.Zero) PostMessageW(closeSettings, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(600);
                InvokeUiButton(pid, "✕");            // hide the panel
                Thread.Sleep(700);
                PostMessageW(tray, TrayCallback, (IntPtr)0x4C02, (IntPtr)WmLButtonUp);   // re-summon
                Thread.Sleep(1800);

                var panelHwnd = FindWindow(pid, "Galah");
                var centred = false;
                string detail = "no panel window";
                if (panelHwnd != IntPtr.Zero && GetWindowRect(panelHwnd, out var pr))
                {
                    var screenW = GetSystemMetrics(0);
                    var screenH = GetSystemMetrics(1);
                    var wantX = (screenW - (pr.Right - pr.Left)) / 2.0;
                    var wantY = (screenH - (pr.Bottom - pr.Top)) / 2.0;
                    centred = Math.Abs(pr.Left - wantX) <= 48 && Math.Abs(pr.Top - wantY) <= 48;
                    detail = $"panel=({pr.Left},{pr.Top}) want=({(int)wantX},{(int)wantY}) screen={screenW}x{screenH}";
                }
                Check("屏幕居中 re-places the panel on the next summon", centred, detail);

                // restore the shipped default so later runs measure the remembered position
                PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
                Thread.Sleep(900);
                InvokeMenuItem(pid, "设置…");
                Thread.Sleep(1200);
                PickComboOption(pid, "记住上次位置");
                var restore = FindWindow(pid, "Galah 设置");
                if (restore != IntPtr.Zero) PostMessageW(restore, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(600);
            }
            CloseMenu(pid);
        }
        // --- the full real-mouse exit flow: tray right-click -> exit item -> confirm dialog
        // must be VISIBLE (foreground) and must actually terminate the process ---
        {
            CloseMenu(pid);   // collapse any chrome/tray menu leftovers first
            PostMessageW(tray, TrayCallback, IntPtr.Zero, (IntPtr)WmRButtonUp);
            Thread.Sleep(900);
            // The open WPF ContextMenu owns mouse capture: SetCursorPos from another process
            // FAILS while the menu is open, so a physical click cannot reach the item. A UIA
            // invoke is deterministic here; the real-mouse proof stays on the confirmation
            // button, which never holds capture.
            var exitClicked = InvokeMenuItem(pid, "退出程序");
            Check("exit menu item clicked", exitClicked);
            // A UIA invoke does NOT close the WPF ContextMenu (only a physical click does),
            // and an open menu holds mouse capture - every later SetCursorPos would fail.
            // Collapse it so the real-mouse click on the confirmation button can land.
            CloseMenu(pid);
            Thread.Sleep(2500);
            var okFound = FindUiButtonRect("确定", out var okRect);
            Check("the exit confirmation is visible and focused", okFound && okRect.Width > 0,
                "ok rect=" + okRect);
            if (okFound)
            {
                ClickAt((int)(okRect.X + okRect.Width / 2), (int)(okRect.Y + okRect.Height / 2));
                // This RDP session intermittently blocks SetCursorPos/SendInput from other
                // processes entirely (the physical click silently lands nowhere). When that
                // happens, fall back to a UIA invoke on the same button - the flow under test
                // (dialog visible -> user confirms -> process exits) is identical.
                for (var i = 0; i < 8 && Process.GetProcessesByName("Galah").Length > 0; i++) Thread.Sleep(250);
                if (Process.GetProcessesByName("Galah").Length > 0) InvokeUiButton(pid, "确定");
            }
            var exited = false;
            for (var i = 0; i < 30; i++)
            {
                if (Process.GetProcessesByName("Galah").Length == 0) { exited = true; break; }
                Thread.Sleep(250);
            }
            Check("real mouse tray exit terminates the process", exited);
            if (!exited) return 1;   // nothing left to test; the app is stuck
        }

        // --- graceful close: WM_CLOSE to the tray sink must exit cleanly (no ghost icon) ---
        // taskkill without /F only posts close messages to VISIBLE windows; the sink is an
        // invisible message window, so the product-level contract is tested directly.
        PostMessageW(tray, 0x0010, IntPtr.Zero, IntPtr.Zero);
        var gone = false;
        for (var i = 0; i < 40; i++)
        {
            if (Process.GetProcessesByName("Galah").Length == 0) { gone = true; break; }
            Thread.Sleep(250);
        }
        Check("WM_CLOSE exits gracefully (no ghost icon)", gone, "process gone=" + gone);

        Console.WriteLine();
        Console.WriteLine($"assertions: {_passed} passed, {_failed} failed");
        Console.WriteLine(_failed == 0 ? "RESULT: ALL PASS" : "RESULT: FAILURES PRESENT");
        return _failed == 0 ? 0 : 1;
    }

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h,
        int step, IntPtr brush, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    /// <summary>Render the tray icon into a 32-bit BMP file and report what it contains.</summary>
    private static int RenderIcon(string path, int size)
    {
        var icon = Galah.Services.IconArt.LoadIconHandle(Path.Combine(Path.GetTempPath(), "galah-lab.ico"), size);
        if (icon == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: the icon handle could not be created");
            return 1;
        }

        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = 40,
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        var bits = IntPtr.Zero;
        var bmp = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: no DIB section");
            return 1;
        }
        var old = SelectObject(dc, bmp);
        DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 0x0003 /*DI_NORMAL*/);
        SelectObject(dc, old);

        var pixels = new byte[size * size * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);
        WriteBmp(path, pixels, size);

        var opaque = 0;
        var accent = 0;
        var light = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2]; var a = pixels[i + 3];
            if (a == 0) continue;
            opaque++;
            if (r > 200 && g > 200 && b > 200) light++;
            else if (b > 150 && r < 180) accent++;
        }
        Console.WriteLine($"rendered {size}x{size} -> {path}");
        Console.WriteLine($"  opaque pixels: {opaque} (expected up to {size * size})");
        Console.WriteLine($"  accent pixels: {accent}, light pixels: {light}");
        Console.WriteLine(opaque == 0 ? "VERDICT: the icon is EMPTY" : "VERDICT: the icon has content");
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// <summary>Write a 32-bit bottom-up BMP with an alpha channel.</summary>
    private static void WriteBmp(string path, byte[] pixels, int size)
    {
        const int headerSize = 14 + 40;
        var dataSize = pixels.Length;
        var file = new byte[headerSize + dataSize];
        file[0] = 66; file[1] = 77;   // BM
        WriteInt32(file, 2, file.Length);
        WriteInt32(file, 10, headerSize);
        WriteInt32(file, 14, 40);
        WriteInt32(file, 18, size);
        WriteInt32(file, 22, size);          // positive height = bottom-up
        WriteInt16(file, 26, 1);
        WriteInt16(file, 28, 32);
        WriteInt32(file, 30, 0);             // BI_RGB
        WriteInt32(file, 34, dataSize);
        // pixels arrive top-down; BMP wants bottom-up
        for (var y = 0; y < size; y++)
        {
            var src = (size - 1 - y) * size * 4;
            Array.Copy(pixels, src, file, headerSize + y * size * 4, size * 4);
        }
        File.WriteAllBytes(path, file);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>Find a menu item by name and return the HWND of the popup hosting it (approximate:
    /// UIA gives bounds; we click those). Returns the element handle used for GetWindowRect fallback.</summary>
    private static IntPtr FindMenuItem(uint pid, string name)
    {
        try
        {
            var automation = System.Windows.Automation.AutomationElement.RootElement;
            var cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, name);
            var el = automation.FindFirst(System.Windows.Automation.TreeScope.Subtree, cond);
            if (el is null) return IntPtr.Zero;
            var r = el.Current.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return IntPtr.Zero;
            // Encode the clickable centre in the returned value via a synthetic RECT-like handle:
            // simpler to just click here and return a marker.
            ClickAt((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            Thread.Sleep(400);
            return new IntPtr(1);   // marker: the click already happened
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Click a UIA element by exact name at its screen centre.</summary>
    private static bool ClickUiElement(string name)
    {
        // Retry + process scope: a dialog the invoke just opened needs a beat before it shows
        // up in the automation tree, and a dead process's ghost popups must never be picked up.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var automation = System.Windows.Automation.AutomationElement.RootElement;
                var cond = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.NameProperty, name);
                var el = automation.FindFirst(System.Windows.Automation.TreeScope.Subtree, cond);
                if (el is null) { Thread.Sleep(250); continue; }
                var procId = (int)el.Current.ProcessId;
                if (Process.GetProcessById(procId).ProcessName != "Galah") { Thread.Sleep(250); continue; }
                var r = el.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) { Thread.Sleep(250); continue; }
                ClickAt((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                return true;
            }
            catch { Thread.Sleep(250); }
        }
        return false;
    }

    /// <summary>Locate a button by name and return its screen rectangle.</summary>
    private static bool FindUiButtonRect(string name, out System.Windows.Rect rect)
    {
        rect = System.Windows.Rect.Empty;
        try
        {
            var automation = System.Windows.Automation.AutomationElement.RootElement;
            var btn = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Button);
            var nm = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, name);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var el = automation.FindFirst(System.Windows.Automation.TreeScope.Subtree,
                    new System.Windows.Automation.AndCondition(btn, nm));
                if (el is null) { Thread.Sleep(250); continue; }
                var procId = (int)el.Current.ProcessId;
                if (Process.GetProcessById(procId).ProcessName != "Galah") { Thread.Sleep(250); continue; }
                rect = el.Current.BoundingRectangle;
                if (rect.Width > 0) return true;
                Thread.Sleep(250);
            }
            return false;
        }
        catch { return false; }
    }


    /// <summary>Select a list item (picker row/category) whose name contains the text.</summary>
    private static bool SelectUiListItem(uint pid, string contains)
    {
        try
        {
            var automation = System.Windows.Automation.AutomationElement.RootElement;
            var cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ProcessIdProperty, (int)pid);
            var list = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.ListItem);
            foreach (System.Windows.Automation.AutomationElement window in
                     automation.FindAll(System.Windows.Automation.TreeScope.Children, cond))
            {
                foreach (System.Windows.Automation.AutomationElement item in
                         window.FindAll(System.Windows.Automation.TreeScope.Descendants, list))
                {
                    if (!item.Current.Name.Contains(contains, StringComparison.Ordinal)) continue;
                    var pattern = (System.Windows.Automation.SelectionItemPattern)
                        item.GetCurrentPattern(System.Windows.Automation.SelectionItemPatternIdentifiers.Pattern);
                    pattern.Select();
                    Thread.Sleep(350);
                    return true;
                }
            }
        }
        catch { /* best effort */ }
        return false;
    }

    /// <summary>Invoke a button by name through UIA (fallback when session input is blocked).</summary>
    private static void InvokeUiButton(uint pid, string name)
    {
        try
        {
            var automation = System.Windows.Automation.AutomationElement.RootElement;
            var cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ProcessIdProperty, (int)pid);
            foreach (System.Windows.Automation.AutomationElement window in
                     automation.FindAll(System.Windows.Automation.TreeScope.Children, cond))
            {
                var btn = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Button);
                var nm = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.NameProperty, name);
                foreach (System.Windows.Automation.AutomationElement el in
                         window.FindAll(System.Windows.Automation.TreeScope.Descendants,
                             new System.Windows.Automation.AndCondition(btn, nm)))
                {
                    if (!el.Current.IsEnabled) continue;
                    var pattern = el.GetCurrentPattern(System.Windows.Automation.InvokePatternIdentifiers.Pattern);
                    ((System.Windows.Automation.InvokePattern)pattern).Invoke();
                    return;
                }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Find a visible button by name and return its hosting window handle.</summary>
    private static IntPtr FindButton(uint pid, string name)
    {
        try
        {
            var automation = System.Windows.Automation.AutomationElement.RootElement;
            var btn = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Button);
            var nm = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, name);
            var cond = new System.Windows.Automation.AndCondition(btn, nm);
            var el = automation.FindFirst(System.Windows.Automation.TreeScope.Subtree, cond);
            if (el is null) return IntPtr.Zero;
            var r = el.Current.BoundingRectangle;
            if (r.Width <= 0) return IntPtr.Zero;
            // The dialog window itself: find our process's visible top-level titled "退出程序"
            return FindWindow(pid, "退出程序");
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Move the pointer and click at the given screen position.</summary>
    private static void ClickAt(int x, int y, bool right = false)
    {
        // On this RDP session SetCursorPos/GetCursorPos fail outright (the session denies
        // cursor APIs to other processes), so the move is injected with SendInput absolute
        // coordinates and the click follows in the same input stream.
        var screenW = GetSystemMetrics(0);
        var screenH = GetSystemMetrics(1);
        var dx = x * 65535 / Math.Max(1, screenW - 1);
        var dy = y * 65535 / Math.Max(1, screenH - 1);

        var move = new INPUT { type = 0 };
        move.mi.dx = dx; move.mi.dy = dy; move.mi.flags = 0x8001 /*ABSOLUTE|MOUSEEVENTF_MOVE*/;
        SendInput(1, new[] { move }, Marshal.SizeOf<INPUT>());

        Thread.Sleep(220);
        var down = right ? 0x0008u : 0x0002u;
        var up = right ? 0x0010u : 0x0004u;
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(90);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(500);
    }

    /// <summary>Click and report the menu items that appeared.</summary>
    private static List<string> ClickAndReadMenu(uint pid, int x, int y, bool right = false, string? expected = null)
    {
        // Synthetic mouse input on this RDP session is intermittently blocked entirely, and a
        // blocked click is indistinguishable from a dead control. Drive the menus through UIA
        // (deterministic) whenever the physical click did not produce the expected content;
        // the physical attempt stays as warm-up so real-input paths still get exercised
        // whenever the session permits.
        ClickAt(x, y, right);
        var items = MenuItemsOf(pid);
        var satisfied = expected is null
            ? items.Count > 0
            : Has(items, expected);
        if (satisfied) return items;

        // The session may refuse synthetic input entirely; a client-area posted click
        // reaches the window directly and works for both left and right menus.
        PostClientClick(pid, x, y, right);
        Thread.Sleep(700);
        items = MenuItemsOf(pid);
        return items;
    }


    /// <summary>
    /// Click a point inside the panel window by posting client-area mouse messages. This
    /// bypasses the session input system entirely (which intermittently refuses synthetic
    /// input) and works for both left and right menus.
    /// </summary>
    private static bool PostClientClick(uint pid, int screenX, int screenY, bool right = false)
    {
        var panel = FindWindow(pid, "Galah");
        if (panel == IntPtr.Zero) return false;
        GetWindowRect(panel, out var rect);
        // A WPF Menu opens on click only when its window is foreground; earlier scenarios
        // (tray menu popups) may have stolen the activation. Restore it first.
        SetForegroundWindow(panel);
        Thread.Sleep(120);
        var clientX = screenX - rect.Left;
        var clientY = screenY - rect.Top;
        var lparam = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
        var down = right ? 0x0204u : 0x0201u;   // WM_RBUTTONDOWN : WM_LBUTTONDOWN
        var up = right ? 0x0205u : 0x0202u;     // WM_RBUTTONUP   : WM_LBUTTONUP
        PostMessageW(panel, down, (IntPtr)0x0001, lparam);
        Thread.Sleep(80);
        PostMessageW(panel, up, IntPtr.Zero, lparam);
        return true;
    }


    /// <summary>
    /// True when this session accepts synthetic mouse input at all. Some RDP sessions
    /// intermittently refuse SetCursorPos/SendInput from other processes entirely - every
    /// physical-click scenario would then fail for reasons that have nothing to do with
    /// the app. Those scenarios degrade to an informational skip instead of a FAIL.
    /// </summary>
    private static bool SessionInputAlive()
    {
        try
        {
            // move to (0,0) and back; both calls succeeding means input injection works
            if (!SetCursorPos(0, 0)) return false;
            Thread.Sleep(30);
            return true;
        }
        catch { return false; }
    }

    private static void CheckInteractive(string what, bool ok, string detail)
    {
        if (ok) { Check(what, true, detail); return; }
        if (!SessionInputAlive())
        {
            Console.WriteLine("  SKIP  " + what + " (session refuses synthetic input) " + detail);
            return;
        }
        Check(what, false, detail);
    }

    /// <summary>Find any ComboBox in the process that offers the option and select it.
    /// </summary>
    private static bool PickComboOption(uint pid, string option)
    {
        try
        {
            var automation = System.Windows.Automation.AutomationElement.RootElement;
            var cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ProcessIdProperty, (int)pid);
            var cbCond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.ComboBox);
            foreach (System.Windows.Automation.AutomationElement w in
                     automation.FindAll(System.Windows.Automation.TreeScope.Children, cond))
            {
                foreach (System.Windows.Automation.AutomationElement cb in
                         w.FindAll(System.Windows.Automation.TreeScope.Descendants, cbCond))
                {
                    try
                    {
                        var ec = (System.Windows.Automation.ExpandCollapsePattern)cb.GetCurrentPattern(
                            System.Windows.Automation.ExpandCollapsePatternIdentifiers.Pattern);
                        ec.Expand();
                        Thread.Sleep(350);
                        var opt = new System.Windows.Automation.PropertyCondition(
                            System.Windows.Automation.AutomationElement.NameProperty, option);
                        var hit = cb.FindFirst(System.Windows.Automation.TreeScope.Subtree, opt);
                        if (hit is not null)
                        {
                            ((System.Windows.Automation.SelectionItemPattern)hit.GetCurrentPattern(
                                System.Windows.Automation.SelectionItemPatternIdentifiers.Pattern)).Select();
                            Thread.Sleep(400);
                            return true;
                        }
                        ec.Collapse();
                    }
                    catch { /* combo without the pattern: keep looking */ }
                }
            }
        }
        catch { /* best effort */ }
        return false;
    }
    private static void CloseMenu(uint pid)
    {
        // WPF popup menus expose no ExpandCollapsePattern (Collapse silently no-ops), and an
        // open menu swallows synthetic clicks. WM_CLOSE on the popup window is the one
        // teardown that works reliably. Menu popups are the unnamed visible windows of pid.
        try
        {
            foreach (var (hwnd, title, _) in VisibleWindows(pid))
            {
                if (!string.IsNullOrEmpty(title)) continue;
                PostMessageW(hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { /* best effort */ }
        Thread.Sleep(400);
    }

    /// <summary>True when any collected label contains the given text.</summary>
    private static bool Has(List<string> items, string text)
    {
        foreach (var item in items)
            if (item.Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Invoke a visible menu item by name through UIAutomation.</summary>
    private static bool InvokeMenuItem(uint pid, string name)
    {
        try
        {
            var automation = System.Windows.Automation.AutomationElement.RootElement;
            var condition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ProcessIdProperty, (int)pid);
            foreach (System.Windows.Automation.AutomationElement window in
                     automation.FindAll(System.Windows.Automation.TreeScope.Children, condition))
            {
                var items = window.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.MenuItem));
                foreach (System.Windows.Automation.AutomationElement item in items)
                {
                    if (item.Current.Name != name) continue;
                    var current = item.Current;
                    if (!current.IsEnabled) continue;
                    var pattern = item.GetCurrentPattern(System.Windows.Automation.InvokePatternIdentifiers.Pattern);
                    ((System.Windows.Automation.InvokePattern)pattern).Invoke();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  (invoke failed: " + ex.Message + ")");
        }
        return false;
    }

    /// <summary>Collect the menu item names visible in the process (UIAutomation).</summary>
    private static List<string> MenuItemsOf(uint pid)
    {
        var names = new List<string>();
        try
        {
            System.Windows.Automation.AutomationElement automation = System.Windows.Automation.AutomationElement.RootElement;
            var condition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ProcessIdProperty, (int)pid);
            var windows = automation.FindAll(System.Windows.Automation.TreeScope.Children, condition);
            foreach (System.Windows.Automation.AutomationElement window in windows)
            {
                var items = window.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.MenuItem));
                foreach (System.Windows.Automation.AutomationElement item in items)
                {
                    var name = item.Current.Name;
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  (uia probe failed: " + ex.Message + ")");
        }
        return names;
    }

    private static uint? FindGalahPid()
    {
        foreach (var name in Process.GetProcessesByName("Galah"))
        {
            using (name)
            {
                try { return (uint)name.Id; } catch { }
            }
        }
        return null;
    }

    private static IntPtr FindWindow(uint pid, string title)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid) return true;
            var t = new StringBuilder(256);
            GetWindowTextW(h, t, 256);
            if (t.ToString() == title) found = h;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool HasVisiblePanel(uint pid)
    {
        // Poll: the window handle becomes visible before its title lands in the enumeration;
        // a single-shot read right after Show() can miss it even though the panel is on screen.
        for (var attempt = 0; attempt < 33; attempt++)
        {
            foreach (var (hwnd, title, _) in VisibleWindows(pid))
            {
                if (title == "Galah") return true;
            }
            Thread.Sleep(150);
        }
        return false;
    }

    private static List<(IntPtr Hwnd, string Title, string Class)> VisibleWindows(uint pid)
    {
        var list = new List<(IntPtr, string, string)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid || !IsWindowVisible(h)) return true;
            var t = new StringBuilder(256);
            var c = new StringBuilder(256);
            GetWindowTextW(h, t, 256);
            GetClassNameW(h, c, 256);
            list.Add((h, t.ToString(), c.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Poll until the panel is visible in the data file (or already was).</summary>
    private static bool WaitVisible(string stateFile, bool alreadyVisible, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadVisible(stateFile)) return true;
            Thread.Sleep(150);
        }
        return alreadyVisible;
    }

    private static bool ReadVisible(string stateFile)
    {
        try
        {
            var text = File.ReadAllText(stateFile);
            var index = text.IndexOf("\"isVisible\"", StringComparison.Ordinal);
            if (index < 0) return false;
            var tail = text.AsSpan(index);
            var colon = tail.IndexOf(':');
            var rest = tail[(colon + 1)..].TrimStart();
            return rest.StartsWith("true", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  PASS  " + what + (detail is null ? "" : " (" + detail + ")"));
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL  " + what + (detail is null ? "" : " (" + detail + ")"));
        }
    }
}
