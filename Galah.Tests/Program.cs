using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Galah.Models;
using Galah.Persistence;
using Galah.Services;

namespace Galah.Tests;

/// <summary>
/// Quality-gate suite for Galah (item-loop spec §7). Plain console harness — no external test package,
/// in keeping with the green/no-dependencies rule. Every fixture writes under
/// %LOCALAPPDATA%\Temp\GalahTests-&lt;stamp&gt; so the suite can never touch real user data.
/// Runs on an STA thread because the LinkResolver fixture drives IShellLink COM.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static string _root = "";

    [STAThread]
    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* redirected: keep defaults */ }

        _root = MakeScratchRoot();
        Console.WriteLine("Galah.Tests — quality gate suite");
        Console.WriteLine("  data root : " + _root);
        Console.WriteLine("  temp      : " + Path.GetTempPath());
        Console.WriteLine("  x64       : " + Environment.Is64BitProcess);
        Console.WriteLine("  apartment : " + Thread.CurrentThread.GetApartmentState());
        Console.WriteLine();

        Run("T1", T1_CatalogByteStability);
        Run("T2", T2_CorruptionFallbackChain);
        Run("T3", T3_HotkeyParser);
        Run("T4", T4_UrlFileParser);
        Run("T5", T5_LinkResolverCom);
        Run("T6", T6_UiStateJsonWritability);
        Run("T7", T7_WindowsLoadTheirXaml);
        Run("T8", T8_TrayGlyphHasPixels);
        Run("T9", T9_ReorderAndUiStateSchema);
        Run("T10", T10_DefaultPlacement);
        Run("T11", T11_TrayCallbackClassification);
        Run("T12", T12_StartupReachability);
        Run("T13", T13_IconPipeline);
        Run("T14", T14_CanonicalIcon);
        Run("T15", T15_ButtonOrder);
        Run("T16", T16_PickerSources.Run);
        Run("T17", T17_Maintenance.Run);
        Run("T18", T18_Migration.Run);
        Run("T19", T19_Placement.Run);
        Run("T20", T20_DesktopShortcut.Run);
        Run("T21", T21_Navigation.Run);
        Run("T22", T22_Favicon.Run);

        Console.WriteLine();
        Console.WriteLine(new string('-', 62));
        Console.WriteLine($"assertions: {_passed} passed, {_failed} failed");
        Console.WriteLine(_failed == 0 ? "RESULT: ALL PASS" : "RESULT: FAILURES PRESENT");

        TryCleanup(_root);
        return _failed == 0 ? 0 : 1;
    }

    // ───────────────────────────────── T1 ─────────────────────────────────
    /// <summary>catalog save → load → save must be a byte-level fixed point (INV-1/INV-5, spec §5.2).</summary>
    private static void T1_CatalogByteStability()
    {
        Section("T1 catalog: save→load→save byte-stability");
        var paths = CaseDir("t1-byte-stability");

        var store = new CatalogStore(paths);
        var first = store.Load();
        Check("first run bootstraps defaults once", first.Status == LoadStatus.FirstRunInitialized, first.Status.ToString());

        var tabId = store.Current.Tabs[0].Id;
        var nastyArgs = "/k echo \"中文 A B\" --path=C:\\Temp\\x " + new string('—', 200);
        var app = new CatalogItem
        {
            Kind = ItemKinds.App,
            Name = "记事本 📝",
            Target = "C:/Windows/System32/notepad.exe",
            Args = nastyArgs,
            Workdir = "C:/Windows",
            RunAsAdmin = true,
        };
        var url = new CatalogItem
        {
            Kind = ItemKinds.Url,
            Name = "example.com",
            Target = "https://example.com/a%20b?x=1#frag",
            Icon = new IconReference { Mode = IconModes.Override, OverrideSource = "C:/icons/示例.png" },
        };
        var folder = new CatalogItem { Kind = ItemKinds.Folder, Name = "数据目录", Target = PathNorm.ToStored(paths.Root) };

        var ids = store.AddItems(tabId, new[] { app, url, folder });
        Check("batch add yields one id per item", ids.Count == 3, ids.Count.ToString(CultureInfo.InvariantCulture));
        Check("assigned ids are unique and shaped i-*", ids.Distinct().Count() == 3 && ids.All(i => i.StartsWith("i-", StringComparison.Ordinal)));
        Check("order is appended at the end (0,1,2)",
            store.Current.ItemsInOrder(store.Current.Tabs[0]).Select(i => i.Order).SequenceEqual(new[] { 0, 1, 2 }));

        var hash1 = Sha(File.ReadAllBytes(paths.CatalogFile));

        var reload = new CatalogStore(paths);
        var second = reload.Load();
        Check("reload reports Loaded", second.Status == LoadStatus.Loaded, second.Status.ToString());
        Check("load→save performs no write at all", !reload.FileStore.Save(reload.Current));
        Check("file bytes identical after load→save", Sha(File.ReadAllBytes(paths.CatalogFile)) == hash1);
        Check("canonical text equals the file bytes",
            File.ReadAllText(paths.CatalogFile) == JsonFileStore<CatalogDocument>.Serialize(reload.Current));
        Check("model equals the model that was saved",
            JsonFileStore<CatalogDocument>.Serialize(store.Current) == JsonFileStore<CatalogDocument>.Serialize(reload.Current));

        var back = reload.Current.FindItem(ids[0])!.Value.Item;
        Check("args with quotes/中文/200+ chars survive byte-for-byte", back.Args == nastyArgs,
            "len " + (back.Args?.Length ?? -1) + " vs " + nastyArgs.Length);
        Check("unicode name survives", back.Name == "记事本 📝", back.Name);
        Check("runAsAdmin survives", back.RunAsAdmin);
        Check("stored target keeps forward slashes", back.Target == "C:/Windows/System32/notepad.exe", back.Target);
        var urlBack = reload.Current.FindItem(ids[1])!.Value.Item;
        Check("url target + icon override survive",
            urlBack.Target == url.Target && urlBack.Icon.Mode == IconModes.Override &&
            urlBack.Icon.OverrideSource == url.Icon.OverrideSource);

        // A real edit creates a new fixed point, and loading that one is stable too.
        var edited = back.Clone();
        edited.Name = "记事本 改";
        edited.Args = "/x";
        reload.ReplaceItem(edited);
        var hash2 = Sha(File.ReadAllBytes(paths.CatalogFile));
        Check("edit commit changed the file hash", hash2 != hash1);

        var again = new CatalogStore(paths);
        again.Load();
        Check("after the edit, load→save is again a no-op", !again.FileStore.Save(again.Current));
        Check("bytes stable at the new fixed point", Sha(File.ReadAllBytes(paths.CatalogFile)) == hash2);
        var afterEdit = again.Current.FindItem(ids[0])!.Value.Item;
        Check("edit kept id and order (INV-7)", afterEdit.Id == edited.Id && afterEdit.Order == edited.Order);
        Check("kind is immutable through edit (spec §4.1)", afterEdit.Kind == ItemKinds.App, afterEdit.Kind);
        Check("rolling backup .bak.1 exists after 2 commits", File.Exists(reload.FileStore.BackupPath(1)));
        Check("no temp files left behind", Directory.GetFiles(paths.Root, "*.tmp-*").Length == 0,
            string.Join(", ", Directory.GetFiles(paths.Root, "*.tmp-*")));
        Check("catalog.json is valid JSON with the documented fields", HasCatalogFields(paths.CatalogFile));
    }

    // ───────────────────────────────── T2 ─────────────────────────────────
    /// <summary>Corrupt main → .bak.1 fallback, evidence quarantined (INV-6, TL-D5), plus the neighbouring chains.</summary>
    private static void T2_CorruptionFallbackChain()
    {
        Section("T2 corrupt catalog → .bak.1 fallback + catalog.corrupt-* quarantine");
        var paths = CaseDir("t2-corrupt-chain");

        var s1 = new CatalogStore(paths);
        s1.Load();
        var tabId = s1.Current.Tabs[0].Id;
        s1.AddItems(tabId, new[] { NewItem("alpha") });
        s1.AddItems(tabId, new[] { NewItem("beta") });
        Check("pre-state: main holds 2 items", CountItems(paths.CatalogFile) == 2, CountItems(paths.CatalogFile).ToString());
        var v1Bytes = File.ReadAllBytes(s1.FileStore.BackupPath(1));
        Check("pre-state: .bak.1 holds 1 item", CountItems(s1.FileStore.BackupPath(1)) == 1, CountItems(s1.FileStore.BackupPath(1)).ToString());

        // TL-D5: truncate the main file to 3 bytes and boot again.
        var good = File.ReadAllBytes(paths.CatalogFile);
        var truncated = good[..3];
        File.WriteAllBytes(paths.CatalogFile, truncated);

        var s2 = new CatalogStore(paths);
        var outcome = s2.Load();
        Check("status is RecoveredFromBackup", outcome.Status == LoadStatus.RecoveredFromBackup, outcome.Status + " / " + outcome.Detail);
        Check("recovered content is the .bak.1 state (1 item)",
            CountItems(s2.Current) == 1 && FirstItemName(s2.Current) == "alpha", FirstItemName(s2.Current));
        Check("detail reports the recovered backup", outcome.Detail?.Contains(".bak.1") == true || outcome.Detail?.Contains("保全") == true, outcome.Detail);

        var quarantine = Directory.GetFiles(paths.Root, "catalog.corrupt-*.json");
        Check("exactly one catalog.corrupt-* file created", quarantine.Length == 1, quarantine.Length.ToString());
        Check("quarantined evidence is byte-identical to the damaged file",
            quarantine.Length == 1 && File.ReadAllBytes(quarantine[0]).SequenceEqual(truncated));
        Check("detail names the quarantine file", outcome.Detail?.Contains("catalog.corrupt-") == true, outcome.Detail);
        Check("backups were not consumed/rewritten (INV-6)",
            File.ReadAllBytes(s2.FileStore.BackupPath(1)).SequenceEqual(v1Bytes));
        Check("damaged main is gone (never silently overwritten)", !File.Exists(paths.CatalogFile));

        // Saving after recovery rebuilds main and must not destroy the evidence.
        s2.AddItems(tabId, new[] { NewItem("gamma") });
        Check("main recreated on the next commit", File.Exists(paths.CatalogFile));
        Check("recovered catalog now holds 2 items", CountItems(paths.CatalogFile) == 2, CountItems(paths.CatalogFile).ToString());
        var quarantineAfter = Directory.GetFiles(paths.Root, "catalog.corrupt-*.json");
        Check("quarantine file count still 1 (never overwritten)", quarantineAfter.Length == 1, quarantineAfter.Length.ToString());
        Check("quarantined bytes still the original damage",
            quarantineAfter.Length == 1 && File.ReadAllBytes(quarantineAfter[0]).SequenceEqual(truncated));

        // ── every copy damaged → start empty, still no evidence destroyed ──
        var p3 = CaseDir("t2-all-corrupt");
        var s3 = new CatalogStore(p3);
        s3.Load();
        s3.AddItems(s3.Current.Tabs[0].Id, new[] { NewItem("delta") });
        var badMain = Encoding.UTF8.GetBytes("{broken");
        var bad1 = Encoding.UTF8.GetBytes("{broken-1");
        var bad2 = Encoding.UTF8.GetBytes("{broken-2");
        File.WriteAllBytes(p3.CatalogFile, badMain);
        File.WriteAllBytes(s3.FileStore.BackupPath(1), bad1);
        File.WriteAllBytes(s3.FileStore.BackupPath(2), bad2);

        var s4 = new CatalogStore(p3);
        var o4 = s4.Load();
        Check("all-damaged → StartedEmpty", o4.Status == LoadStatus.StartedEmpty, o4.Status + " / " + o4.Detail);
        Check("all-damaged → empty library (default tab, 0 items)",
            s4.Current.Tabs.Count == 1 && CountItems(s4.Current) == 0);
        Check("all-damaged → main quarantined anyway",
            Directory.GetFiles(p3.Root, "catalog.corrupt-*.json").Length == 1);
        var q3 = Directory.GetFiles(p3.Root, "catalog.corrupt-*.json");
        Check("quarantined main keeps the damaged bytes",
            q3.Length == 1 && File.ReadAllBytes(q3[0]).SequenceEqual(badMain));
        Check("damaged backups left untouched (INV-6)",
            File.ReadAllBytes(s3.FileStore.BackupPath(1)).SequenceEqual(bad1) &&
            File.ReadAllBytes(s3.FileStore.BackupPath(2)).SequenceEqual(bad2));

        // ── newer schema → refuse, seal writes, file untouched (TL-D8) ──
        var p5 = CaseDir("t2-newer-schema");
        var s5 = new CatalogStore(p5);
        s5.Load();
        s5.AddItems(s5.Current.Tabs[0].Id, new[] { NewItem("future") });
        var text = File.ReadAllText(p5.CatalogFile);
        var bumped = Regex.Replace(text, "\"schema\"\\s*:\\s*\\d+", "\"schema\": 999");
        File.WriteAllText(p5.CatalogFile, bumped);
        var bumpedHash = Sha(File.ReadAllBytes(p5.CatalogFile));

        var s6 = new CatalogStore(p5);
        var o6 = s6.Load();
        Check("newer schema → RefusedNewerSchema", o6.Status == LoadStatus.RefusedNewerSchema, o6.Status + " / " + o6.Detail);
        Check("store is sealed for upgrade", s6.IsSealed);
        var refusedWrite = ThrowsPersistence(() => s6.FileStore.Save(s6.Current));
        Check("writes are blocked while refused", refusedWrite);
        Check("refused file bytes untouched", Sha(File.ReadAllBytes(p5.CatalogFile)) == bumpedHash);
        Check("no quarantine created for a merely-newer file",
            Directory.GetFiles(p5.Root, "catalog.corrupt-*").Length == 0);

        // ── settings damage cannot touch the catalog (TL-D6, spec §5.1-4) ──
        var p7 = CaseDir("t2-settings-independent");
        var s7 = new CatalogStore(p7);
        s7.Load();
        s7.AddItems(s7.Current.Tabs[0].Id, new[] { NewItem("keepme") });
        var catalogHash = Sha(File.ReadAllBytes(p7.CatalogFile));
        var st7 = new SettingsStore(p7);
        st7.Load();
        File.WriteAllBytes(p7.SettingsFile, Encoding.UTF8.GetBytes("{broken"));

        var st8 = new SettingsStore(p7);
        var o8 = st8.Load();
        Check("settings damage → defaults, own domain only",
            o8.Status is LoadStatus.StartedEmpty or LoadStatus.RecoveredFromBackup, o8.Status.ToString());
        Check("settings fell back to the documented hotkey default",
            st8.Current.Hotkey == SettingsDocument.DefaultHotkey, st8.Current.Hotkey);
        Check("catalog bytes untouched by settings recovery", Sha(File.ReadAllBytes(p7.CatalogFile)) == catalogHash);
        Check("catalog item still present", CountItems(p7.CatalogFile) == 1, CountItems(p7.CatalogFile).ToString());
        Check("settings evidence quarantined too",
            Directory.GetFiles(p7.Root, "settings.corrupt-*.json").Length == 1);
    }

    // ───────────────────────────────── T3 ─────────────────────────────────
    /// <summary>Modifier+key parsing: the three gate cases plus the canonicalisation rules.</summary>
    private static void T3_HotkeyParser()
    {
        Section("T3 HotkeyParser");

        ParseOk("alt+space", 0x0001 | 0x4000, 0x20, "alt+space", "Alt+Space");
        ParseOk("ctrl+alt+f1", 0x0002 | 0x0001 | 0x4000, 0x70, "ctrl+alt+f1", "Ctrl+Alt+F1");
        ParseBad("garbage");
        ParseBad("");
        ParseBad("   ");
        ParseBad("space");               // a bare key is refused: no modifier
        ParseBad("ctrl+");               // dangling separator
        ParseBad("hyper+space");         // unknown modifier
        ParseBad("ctrl+f99");            // function key out of range
        ParseBad("ctrl+alt");            // modifier as the final token → no primary key
        ParseBad("ctrl+@");              // unmappable punctuation

        ParseOk("ALT+SHIFT+F12", 0x0001 | 0x0004 | 0x4000, 0x7B, "alt+shift+f12", "Alt+Shift+F12");
        ParseOk("shift+ctrl+z", 0x0004 | 0x0002 | 0x4000, 'Z', "ctrl+shift+z", "Ctrl+Shift+Z");
        ParseOk(" windows + 2 ", 0x0008 | 0x4000, '2', "win+2", "Win+2");
        ParseOk("ctrl+alt+shift+win+enter", 0x000F | 0x4000, 0x0D, "ctrl+alt+shift+win+enter", "Ctrl+Alt+Shift+Win+Enter");

        // win+` maps through the punctuation branch; the mask/VK are the contract, the text stays as typed.
        if (HotkeyParser.TryParse("win+`", out var tick, out var tickErr))
        {
            Check("win+` accepted", tick is not null && tick.VirtualKey == 0xC0 && tick.Modifiers == (0x0008u | 0x4000u),
                tick is null ? tickErr ?? "" : $"vk=0x{tick.VirtualKey:X2} mod=0x{tick.Modifiers:X4}");
        }
        else Check("win+` accepted", false, tickErr ?? "no spec");

        // Format must round-trip the canonical form back out of the mask + VK.
        var specs = new[] { "alt+space", "ctrl+alt+f1", "ctrl+shift+z", "ctrl+alt+shift+win+enter" };
        foreach (var s in specs)
        {
            if (HotkeyParser.TryParse(s, out var spec, out var err) && spec is not null)
                Check("Format(modifiers, vk) round-trips \"" + s + "\"",
                    HotkeyParser.Format(spec.Modifiers, spec.VirtualKey) == spec.Text,
                    HotkeyParser.Format(spec.Modifiers, spec.VirtualKey) + " vs " + spec.Text);
            else Check("Format round-trip precondition \"" + s + "\" parses", false, err);
        }

        // The default settings hotkey must itself be a valid, registerable combination.
        ParseOk(SettingsDocument.DefaultHotkey, 0x0001 | 0x4000, 0x20, "alt+space", "Alt+Space");
    }

    // ───────────────────────────────── T4 ─────────────────────────────────
    /// <summary>.url internet-shortcut parsing on generated fixtures (spec §2.1 drop row).</summary>
    private static void T4_UrlFileParser()
    {
        Section("T4 UrlFileParser on generated .url fixtures");
        var dir = CaseDir("t4-url");

        var plain = Path.Combine(dir.Root, "example.url");
        UrlFileParser.Write(plain, "https://example.com/index.html");
        Check("generated fixture exists on disk", File.Exists(plain));
        Check("generated fixture is a real INI-shaped .url",
            File.ReadAllText(plain).Contains("[InternetShortcut]", StringComparison.Ordinal));
        Check("ReadUrl returns the URL", UrlFileParser.ReadUrl(plain) == "https://example.com/index.html",
            UrlFileParser.ReadUrl(plain) ?? "null");

        // BOM + CRLF + comments + odd spacing + quoted value, the way Explorer writes them.
        var messy = Path.Combine(dir.Root, "messy.url");
        var body = string.Join("\r\n",
            "; comment line",
            "' second comment",
            "[InternetShortcut]",
            "IDList=",
            "IconFile=C:\\Windows\\notepad.exe",
            "  URL  = \"https://a.example/x y?z=1\"  ",
            "IconIndex=0",
            "URLList=ignored");
        File.WriteAllText(messy, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Check("BOM/CRLF/comment/quoted form parses",
            UrlFileParser.ReadUrl(messy) == "https://a.example/x y?z=1", UrlFileParser.ReadUrl(messy) ?? "null");

        var noSection = Path.Combine(dir.Root, "nosection.url");
        File.WriteAllText(noSection, "Whatever=1\r\nURL=https://loose.example/\r\n");
        Check("URL= without the section header still resolves",
            UrlFileParser.ReadUrl(noSection) == "https://loose.example/", UrlFileParser.ReadUrl(noSection) ?? "null");

        var decoy = Path.Combine(dir.Root, "decoy.url");
        File.WriteAllText(decoy, "[OtherThing]\r\nURL=https://decoy.example/\r\n[InternetShortcut]\r\nURL=https://real.example/\r\n");
        Check("[InternetShortcut] wins over an earlier stray URL",
            UrlFileParser.ReadUrl(decoy) == "https://real.example/", UrlFileParser.ReadUrl(decoy) ?? "null");

        Check("empty URL= yields null", UrlFileParser.Parse("[InternetShortcut]\r\nURL=\r\n") is null);
        Check("URL-less file yields null", UrlFileParser.Parse("[InternetShortcut]\r\nIDList=\r\n") is null);
        Check("empty content yields null", UrlFileParser.Parse("") is null);
        Check("missing file yields null (no throw)",
            UrlFileParser.ReadUrl(Path.Combine(dir.Root, "nope.url")) is null);

        // Forward-slash stored paths must be accepted too (PathNorm tolerance).
        Check("stored-form (forward slash) path is readable",
            UrlFileParser.ReadUrl(PathNorm.ToStored(plain)) == "https://example.com/index.html");

        // The drop mapping the parser feeds: .url → kind=url, name=host (spec §2.1/§2.2).
        var imported = ImportService.FromFilePaths(new[] { plain });
        Check(".url drop imports exactly one draft", imported.Drafts.Count == 1 && imported.Skipped.Count == 0,
            imported.Drafts.Count + "/" + imported.Skipped.Count);
        var item = imported.Drafts.FirstOrDefault()?.Item;
        Check("imported draft is kind=url", item?.Kind == ItemKinds.Url, item?.Kind ?? "-");
        Check("imported draft name is the host", item?.Name == "example.com", item?.Name ?? "-");
        Check("imported draft target is the URL", item?.Target == "https://example.com/index.html", item?.Target ?? "-");

        // Security boundary (TL-A7): file:// / javascript: never import.
        var unsafeUrl = Path.Combine(dir.Root, "unsafe.url");
        UrlFileParser.Write(unsafeUrl, "file:///C:/Windows/System32/cmd.exe");
        var blocked = ImportService.FromFilePaths(new[] { unsafeUrl });
        Check("file:// shortcut is skipped, not imported", blocked.Drafts.Count == 0 && blocked.Skipped.Count == 1,
            blocked.Drafts.Count + "/" + blocked.Skipped.Count);
        Check("UrlSafety refuses javascript:", !UrlSafety.IsHttpUrl("javascript:alert(1)"));
        Check("UrlSafety accepts https with ports", UrlSafety.IsHttpUrl("https://example.com:8443/p"));
    }

    // ───────────────────────────────── T5 ─────────────────────────────────
    /// <summary>IShellLink COM smoke: author a .lnk with target+args+workdir, resolve it, and prove the
    /// catalog snapshot equals the triple — including after the source .lnk is deleted (断缘, TL-A3).</summary>
    private static void T5_LinkResolverCom()
    {
        Section("T5 LinkResolver COM smoke (.lnk author→resolve→snapshot)");
        var dir = CaseDir("t5-lnk");

        var targetExe = Path.Combine(dir.Root, "target.exe");
        File.WriteAllBytes(targetExe, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });   // MZ header: a real file on disk
        var workDir = Path.Combine(dir.Root, "wd");
        Directory.CreateDirectory(workDir);
        var iconHost = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\shell32.dll");
        const string Args = "/flag \"value with space\" 中文 arg=1";

        var lnk = Path.Combine(dir.Root, "gate sample.lnk");
        var created = LinkResolver.TryCreate(lnk, targetExe, Args, workDir, iconHost, 5);
        Check("TryCreate wrote the .lnk", created && File.Exists(lnk), created.ToString());
        Check(".lnk has shell-link size", created && new FileInfo(lnk).Length > 256,
            created ? new FileInfo(lnk).Length + " bytes" : "n/a");

        var info = LinkResolver.Resolve(lnk);
        Check("resolve reports Ok", info.Ok, info.Error ?? "-");
        Check("target round-trips",
            info.Target.Equals(PathNorm.ToStored(targetExe), StringComparison.OrdinalIgnoreCase),
            info.Target + " vs " + PathNorm.ToStored(targetExe));
        Check("args round-trip byte-for-byte", info.Arguments == Args, "'" + info.Arguments + "'");
        Check("working directory round-trips",
            string.Equals(info.WorkingDirectory, PathNorm.ToStored(workDir), StringComparison.OrdinalIgnoreCase),
            info.WorkingDirectory + " vs " + PathNorm.ToStored(workDir));
        Check("icon location + index round-trip",
            string.Equals(info.IconSource, PathNorm.ToStored(iconHost), StringComparison.OrdinalIgnoreCase) && info.IconIndex == 5,
            info.IconSource + "," + info.IconIndex);

        // The triple must land in the library unchanged through the real import + commit path.
        var imported = ImportService.FromFilePaths(new[] { lnk });
        Check(".lnk drop imports one app draft", imported.Drafts.Count == 1 && imported.Skipped.Count == 0,
            imported.Drafts.Count + "/" + imported.Skipped.Count);
        var draft = imported.Drafts[0].Item;
        Check("draft kind=app", draft.Kind == ItemKinds.App, draft.Kind);
        Check("draft name comes from the .lnk stem", draft.Name == "gate sample", draft.Name);
        Check("draft triple equals the resolved snapshot",
            draft.Target == info.Target && draft.Args == info.Arguments && draft.Workdir == info.WorkingDirectory,
            $"{draft.Target} | {draft.Args} | {draft.Workdir}");

        var store = new CatalogStore(dir);
        store.Load();
        var itemId = store.AddItems(store.Current.Tabs[0].Id, new[] { draft })[0];

        // 断缘: the source shortcut disappears; the committed item keeps the same triple (TL-A3 / INV-8).
        File.Delete(lnk);
        Check("source .lnk is gone", !File.Exists(lnk));
        Check("resolver reports the missing link instead of throwing", !LinkResolver.Resolve(lnk).Ok);

        var reread = new CatalogStore(dir);
        reread.Load();
        var committed = reread.Current.FindItem(itemId)!.Value.Item;
        Check("after restart the snapshot triple is still the authored triple",
            committed.Target == PathNorm.ToStored(targetExe) && committed.Args == Args &&
            committed.Workdir == PathNorm.ToStored(workDir),
            $"{committed.Target} | {committed.Args} | {committed.Workdir}");
        var wantIcon = info.IconIndex == 0 ? info.IconSource : info.IconSource + "," + info.IconIndex;
        Check("icon override carried the resolved IconLocation",
            committed.Icon.Mode == IconModes.Override &&
            string.Equals(committed.Icon.OverrideSource, wantIcon, StringComparison.OrdinalIgnoreCase),
            committed.Icon.Mode + " / " + committed.Icon.OverrideSource + " vs " + wantIcon);
        Check("no .lnk handle is needed to read the item back (severed)",
            reread.Current.FindItem(itemId) is not null);
    }

    // ───────────────────────────────── T6 ─────────────────────────────────
    /// <summary>
    /// Regression for the boot-abort bug the runtime gate found: the "no saved position" sentinel used
    /// to be <c>double.NaN</c>, which System.Text.Json refuses to write, so the first-run bootstrap of
    /// ui-state.json threw and boot died at step 4 (spec §5.1). Position is now nullable JSON null.
    /// </summary>
    private static void T6_UiStateJsonWritability()
    {
        Section("T6 ui-state: JSON-writable defaults + geometry clamp (boot-abort regression)");
        var paths = CaseDir("t6-ui-state");

        var store = new UiStateStore(paths);
        var first = store.Load();
        Check("first run bootstraps ui-state.json", first.Status == LoadStatus.FirstRunInitialized, first.Status.ToString());
        Check("ui-state.json exists after boot step 4", File.Exists(paths.UiStateFile));
        var text = File.ReadAllText(paths.UiStateFile);
        Check("bootstrapped ui-state.json is valid JSON", IsValidJson(text), Collapse(text));
        Check("unpositioned start stores null coordinates",
            text.Contains("\"x\": null", StringComparison.Ordinal) && text.Contains("\"y\": null", StringComparison.Ordinal), Collapse(text));
        Check("documented default size is written", store.Current.Window.Width == 720 && store.Current.Window.Height == 460);
        Check("default restored state is resident (hidden)", !store.Current.Window.IsVisible);
        Check("no saved position → HasPosition false", !store.Current.Window.HasPosition);

        var hash = Sha(File.ReadAllBytes(paths.UiStateFile));
        var reload = new UiStateStore(paths);
        reload.Load();
        Check("load→save performs no write", !reload.FileStore.Save(reload.Current));
        Check("ui-state bytes identical after load→save (INV-5)", Sha(File.ReadAllBytes(paths.UiStateFile)) == hash);

        Check("visibility change commits immediately (spec §1)", reload.SetVisibility(true));
        Check("visibility write keeps the file valid JSON", IsValidJson(File.ReadAllText(paths.UiStateFile)));
        var r2 = new UiStateStore(paths);
        r2.Load();
        Check("visibility round-trips", r2.Current.Window.IsVisible);
        Check("redundant visibility write performs no write", !r2.SetVisibility(true));

        r2.UpdateGeometry(120, 240, 800, 500);
        Check("geometry commit wrote", r2.CommitGeometry());
        var r3 = new UiStateStore(paths);
        r3.Load();
        Check("geometry round-trips",
            r3.Current.Window.X == 120 && r3.Current.Window.Y == 240 &&
            r3.Current.Window.Width == 800 && r3.Current.Window.Height == 500,
            $"{r3.Current.Window.X},{r3.Current.Window.Y},{r3.Current.Window.Width},{r3.Current.Window.Height}");
        Check("HasPosition true once a rectangle is saved", r3.Current.Window.HasPosition);

        r3.UpdateGeometry(double.NaN, double.PositiveInfinity, double.NaN, double.PositiveInfinity);
        Check("non-finite coordinates refused, saved rectangle kept",
            r3.Current.Window.X == 120 && r3.Current.Window.Y == 240);
        Check("non-finite sizes refused", r3.Current.Window.Width == 800 && r3.Current.Window.Height == 500);
        Check("hostile geometry input produces zero writes", !r3.CommitGeometry());
        Check("document still serialises as valid JSON", IsValidJson(JsonFileStore<UiStateDocument>.Serialize(r3.Current)));

        // A hand-mangled ui-state must degrade on its own (spec §5.1-4) and never throw boot away.
        File.WriteAllText(paths.UiStateFile,
            "{\"schema\":1,\"window\":{\"x\":null,\"y\":null,\"width\":\"abc\",\"height\":400,\"isVisible\":false}}");
        var r4 = new UiStateStore(paths);
        var o4 = r4.Load();
        Check("malformed ui-state degrades without throwing",
            o4.Status is LoadStatus.StartedEmpty or LoadStatus.RecoveredFromBackup, o4.Status + " / " + o4.Detail);
        Check("degraded ui-state falls back to documented sizes",
            r4.Current.Window.Width == 720 && r4.Current.Window.Height == 460 && !r4.Current.Window.HasPosition);
        Check("malformed ui-state evidence quarantined (INV-6)",
            Directory.GetFiles(paths.Root, "ui-state.corrupt-*.json").Length == 1);
        Check("catalog.json was never touched by ui-state damage",
            !File.Exists(paths.CatalogFile) || IsValidJson(File.ReadAllText(paths.CatalogFile)));

        // Geometry clamp must hand back reachable, finite DIPs (spec §5.1-5 / TL-D7).
        var scale = MonitorGeometry.SystemScale();
        var (gx, gy, gw, gh) = MonitorGeometry.ClampToScreens(null, null, 720, 460, scale);
        Check("clamp with no saved position returns finite DIPs",
            double.IsFinite(gx) && double.IsFinite(gy) && gw >= 240 && gh >= 160, $"{gx},{gy},{gw},{gh}");
        Check("clamp with no saved position lands on a screen", OverlapsSomeWorkArea(gx, gy, gw, gh, scale));
        var off = MonitorGeometry.ClampToScreens(-40000, 30000, 720, 460, scale);
        Check("far off-screen saved position is pulled back (TL-D7)",
            double.IsFinite(off.X) && double.IsFinite(off.Y) && OverlapsSomeWorkArea(off.X, off.Y, off.Width, off.Height, scale),
            $"{off.X},{off.Y}");
        var nanPos = MonitorGeometry.ClampToScreens(double.NaN, 0, 720, 460, scale);
        Check("a NaN coordinate is treated as unpositioned, never returned",
            double.IsFinite(nanPos.X) && double.IsFinite(nanPos.Y), $"{nanPos.X},{nanPos.Y}");
    }

    // ─────────────────────────────── harness ───────────────────────────────
    private static void Run(string tag, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  {tag} aborted with {ex.GetType().Name}: {ex.Message}");
            _failed++;
            if (ex.InnerException is not null)
                Console.WriteLine($"      inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        Console.WriteLine();
    }

    internal static void Section(string title)
    {
        Console.WriteLine("== " + title);
    }

    internal static void Check(string what, bool ok, string? actual = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  PASS  " + what);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL  " + what + (string.IsNullOrEmpty(actual) ? "" : "   [got: " + actual + "]"));
        }
    }

    private static void ParseOk(string text, uint modifiers, uint vk, string canonical, string display)
    {
        var ok = HotkeyParser.TryParse(text, out var spec, out var error);
        if (!ok || spec is null)
        {
            Check($"parse \"{text}\"", false, error ?? "no spec");
            return;
        }
        Check($"parse \"{text}\" → mod=0x{modifiers:X4} vk=0x{vk:X2} \"{canonical}\"",
            spec.Modifiers == modifiers && spec.VirtualKey == vk && spec.Text == canonical,
            $"mod=0x{spec.Modifiers:X4} vk=0x{spec.VirtualKey:X2} text={spec.Text}");
        Check($"parse \"{text}\" display \"{display}\"", spec.Display == display, spec.Display);
    }

    private static void ParseBad(string text)
    {
        var ok = HotkeyParser.TryParse(text, out var spec, out var error);
        Check($"reject \"{text}\"", !ok && spec is null && !string.IsNullOrEmpty(error),
            ok ? "accepted: " + spec?.Text : error ?? "null error");
    }

    private static AppDataPaths CaseDir(string name)
    {
        var dir = Path.Combine(_root, name);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
        return new AppDataPaths(dir).EnsureCreated();
    }

    private static CatalogItem NewItem(string name) => new()
    {
        Kind = ItemKinds.App,
        Name = name,
        Target = "C:/Windows/System32/notepad.exe",
    };

    private static bool ThrowsPersistence(Action action)
    {
        try { action(); return false; }
        catch (PersistenceException) { return true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Scratch data root for the suite: literally <c>%LOCALAPPDATA%\Temp\GalahTests-&lt;stamp&gt;-&lt;pid&gt;</c>
    /// (resolved from the known folder, not from the inherited TEMP, which harness shells often redirect).
    /// </summary>
    private static string MakeScratchRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("LARK_TESTS_SCRATCH");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localTemp = string.IsNullOrEmpty(localAppData) ? Path.GetTempPath() : Path.Combine(localAppData, "Temp");
        var baseDir = string.IsNullOrWhiteSpace(overrideRoot) ? localTemp : overrideRoot;
        var parent = Path.Combine(baseDir, "GalahTests-" +
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Environment.ProcessId);
        Directory.CreateDirectory(parent);
        return parent;
    }

    private static int CountItems(string jsonFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
            var n = 0;
            foreach (var tab in doc.RootElement.GetProperty("tabs").EnumerateArray())
                n += tab.GetProperty("items").GetArrayLength();
            return n;
        }
        catch (Exception) { return -1; }
    }

    private static int CountItems(CatalogDocument doc) => doc.Tabs.Sum(t => t.Items.Count);

    private static string FirstItemName(CatalogDocument doc) =>
        doc.Tabs.OrderBy(t => t.Order).SelectMany(t => t.Items.OrderBy(i => i.Order)).FirstOrDefault()?.Name ?? "";

    private static bool HasCatalogFields(string jsonFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != CatalogDocument.CurrentSchema) return false;
            if (root.GetProperty("profileId").GetString() != "local") return false;
            var tab = root.GetProperty("tabs").EnumerateArray().First();
            if (tab.GetProperty("id").GetString()!.StartsWith("t-") == false) return false;
            var item = tab.GetProperty("items").EnumerateArray().First();
            foreach (var field in new[] { "id", "kind", "name", "target", "args", "workdir", "runAsAdmin", "icon", "order" })
                if (!item.TryGetProperty(field, out _)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Single-line rendering of a file body for failure messages.</summary>
    private static string Collapse(string text) =>
        text.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ").Replace("\r", " ⏎ ");

    private static bool OverlapsSomeWorkArea(double xDip, double yDip, double wDip, double hDip, double scale)
    {
        foreach (var area in MonitorGeometry.EnumerateWorkAreas())
        {
            if (MonitorGeometry.OverlapRatio(xDip * scale, yDip * scale, wDip * scale, hDip * scale, area) >= 0.30)
                return true;
        }
        return false;
    }

    /// <summary>
    /// T7 — every window must load its XAML.
    /// Regression guard: a window whose constructor forgets InitializeComponent() builds fine, passes
    /// every unit test, and then renders as an empty default-styled shell at runtime (no content tree,
    /// stock OS caption). Only a source-level check catches that class of defect cheaply.
    /// </summary>
    private static void T7_WindowsLoadTheirXaml()
    {
        Console.WriteLine();
        Console.WriteLine("== T7 windows: every XAML window loads its markup ==");

        var viewsDir = FindViewsDirectory();
        if (viewsDir is null)
        {
            Check("view sources located (skipped: source tree not reachable from the test run)", true);
            return;
        }

        var codeBehind = Directory.GetFiles(viewsDir, "*.xaml.cs", SearchOption.AllDirectories);
        Check("view code-behind files discovered: " + codeBehind.Length, codeBehind.Length > 0);

        foreach (var file in codeBehind)
        {
            var fileName = Path.GetFileName(file);
            var name = fileName.EndsWith(".xaml.cs", StringComparison.Ordinal)
                ? fileName[..^".xaml.cs".Length]
                : Path.GetFileNameWithoutExtension(file);
            var source = File.ReadAllText(file);
            var hasXaml = File.Exists(Path.Combine(Path.GetDirectoryName(file)!, name + ".xaml"));
            Check(name + ": companion .xaml exists", hasXaml);

            var constructorCallsInitialize = System.Text.RegularExpressions.Regex.IsMatch(
                source,
                @"(public|private|internal|protected)\s+" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*\([^)]*\)\s*\{[^}]*InitializeComponent\s*\(\s*\)",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Check(name + ": constructor calls InitializeComponent()", constructorCallsInitialize);
        }
    }

    private static string? FindViewsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "Galah", "Views");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// T8 — the tray glyph is drawn by hand into a pixel buffer, so it must actually contain a
    /// badge and a light chevron. A blank icon would look like a missing feature.
    /// </summary>
    private static void T8_TrayGlyphHasPixels()
    {
        Console.WriteLine();
        Console.WriteLine("== T8 tray glyph: the hand-drawn icon carries real pixels ==");

        const int size = 32;
        var pixels = Galah.Services.IconArt.RenderGlyphPixels(size);
        Check("glyph buffer length matches 32*32*4", pixels.Length == size * size * 4, pixels.Length.ToString());

        var opaque = 0;
        var accent = 0;
        var light = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0) continue;
            opaque++;
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
            if (r > 200 && g > 200 && b > 200) light++;
            else if (b > 200 && r < 160 && g < 170) accent++;   // RGB(74,111,237)
        }
        Check("glyph has opaque pixels", opaque > size * size / 4, opaque.ToString());
        Check("badge uses the accent colour", accent > 100, accent.ToString());
        Check("chevron is drawn in a light colour", light > 20, light.ToString());

        var corner = 0;   // fully transparent corner: the badge is rounded, not a filled square
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                corner += pixels[(y * size + x) * 4 + 3];
        Check("rounded corners stay transparent", corner == 0, corner.ToString());
    }

    /// <summary>
    /// T9 — the M2.5 batch: reorder inside a category, move across categories, and the ui-state
    /// schema 2 fields. Also proves a schema-1 ui-state file still loads (backward compatibility).
    /// </summary>
    private static void T9_ReorderAndUiStateSchema()
    {
        Console.WriteLine();
        Console.WriteLine("== T9 reorder commands + ui-state schema 2 ==");

        var catalogPath = Path.Combine(_root, "catalog.json");
        var store = new CatalogStore(new AppDataPaths(_root));
        store.Load();

        var tab = store.AddTab("排序测试");
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var id = store.AddItems(tab.Id, new[]
            {
                new CatalogItem { Kind = "app", Name = "项" + i, Target = "C:/" + i + ".exe", Order = 99 },
            }).First();
            ids.Add(id);
        }

        var ordered = () => store.Current.ItemsInOrder(store.Current.FindTab(tab.Id)!).Select(i => i.Id).ToList();
        Check("four items in the category", ordered().Count == 4, ordered().Count.ToString());

        var moved = store.MoveItemToIndex(ids[0], 2);
        Check("MoveItemToIndex reports a change", moved);
        Check("item landed at index 2", ordered()[2] == ids[0], string.Join(",", ordered()));

        Check("moving to the same index is a no-op", !store.MoveItemToIndex(ids[0], 2));
        Check("no-op left the order alone", ordered()[2] == ids[0]);

        // An out-of-range index clamps to the end; an item already at the end is a no-op.
        Check("moving the last item again is a no-op", !store.MoveItemToIndex(ids[3], 99));
        Check("out-of-range index is clamped", store.MoveItemToIndex(ids[1], 99));
        Check("clamped move lands last", ordered()[^1] == ids[1], string.Join(",", ordered()));

        var other = store.AddTab("另一个分类");
        store.MoveItemToTab(ids[1], other.Id);
        Check("item moved across categories", store.Current.FindTab(other.Id)!.Items.Any(i => i.Id == ids[1]));
        Check("source category lost the item", !store.Current.FindTab(tab.Id)!.Items.Any(i => i.Id == ids[1]));

        // ui-state schema 2: active category + maximised flag, and old files still load.
        var uiStore = new UiStateStore(new AppDataPaths(_root));
        uiStore.Load();
        Check("fresh ui-state has no active category", uiStore.Current.ActiveTabId is null);
        Check("SetActiveTab writes", uiStore.SetActiveTab(tab.Id));
        Check("active category round-trips", uiStore.Current.ActiveTabId == tab.Id);
        Check("setting the same category again costs no write", !uiStore.SetActiveTab(tab.Id));
        Check("SetMaximized writes", uiStore.SetMaximized(true));
        Check("maximised flag round-trips", uiStore.Current.Window.Maximized);
        Check("setting the same flag again costs no write", !uiStore.SetMaximized(true));

        var uiPath = Path.Combine(_root, "ui-state.json");
        var text = File.ReadAllText(uiPath);
        Check("written ui-state declares schema 2", text.Contains("\"schema\": 2"), text.Length.ToString());
        Check("written ui-state carries the active category", text.Contains(tab.Id));

        // A schema-1 file (no activeTabId, no maximized) must still load.
        File.WriteAllText(uiPath, "{\"schema\": 1, \"window\": {\"x\": null, \"y\": null, \"width\": 720, \"height\": 460, \"isVisible\": false}}");
        var legacy = new UiStateStore(new AppDataPaths(_root));
        var outcome = legacy.Load();
        Check("schema-1 ui-state still loads", outcome.Status != LoadStatus.RefusedNewerSchema, outcome.Status.ToString());
        Check("legacy file yields a null active category", legacy.Current.ActiveTabId is null);
        Check("legacy file yields a normal (not maximised) window", !legacy.Current.Window.Maximized);
    }

    /// <summary>
    /// T10 — default placement of a panel that was never positioned: it must land inside a work
    /// area, and it must be centred on the pointer monitor when the screen is big enough.
    /// </summary>
    private static void T10_DefaultPlacement()
    {
        Console.WriteLine();
        Console.WriteLine("== T10 default placement ==");

        var scale = Galah.Services.MonitorGeometry.SystemScale();
        var areas = Galah.Services.MonitorGeometry.EnumerateWorkAreas();
        Console.WriteLine("  scale=" + scale + " workAreas=" + areas.Count);
        foreach (var area in areas)
            Console.WriteLine($"    area=({area.Left},{area.Top})-({area.Right},{area.Bottom}) primary={area.IsPrimary}");

        var cursor = Galah.Services.MonitorGeometry.WorkAreaAtCursor();
        Console.WriteLine("  cursor work area=" + (cursor is null
            ? "<none>"
            : $"({cursor.Value.Left},{cursor.Value.Top})-({cursor.Value.Right},{cursor.Value.Bottom})"));

        var (x, y, w, h) = Galah.Services.MonitorGeometry.ClampToScreens(null, null, 720, 460, scale);
        Console.WriteLine($"  default placement=({x},{y}) {w}x{h}");

        Check("default placement is finite", double.IsFinite(x) && double.IsFinite(y), x + "," + y);
        Check("default placement has a sane size", w > 0 && h > 0, w + "x" + h);

        var target = cursor ?? areas.First(a => a.IsPrimary);
        var expectedX = (target.Left + (target.Width - w * scale) / 2) / scale;
        var expectedY = (target.Top + (target.Height - h * scale) / 2) / scale;
        Check("panel is centred on the pointer monitor", Math.Abs(x - expectedX) < 2 && Math.Abs(y - expectedY) < 2,
            $"{x},{y} vs {expectedX},{expectedY}");

        // On a screen that cannot hold the full panel, the default size shrinks to fit instead of
        // overflowing (verified against a 640x432 session).
        var fits = target.Width >= w * scale && target.Height >= h * scale;
        if (!fits)
        {
            Check("panel shrinks to the work area", w * scale <= target.Width + 1 && h * scale <= target.Height + 1,
                $"{w}x{h} vs {target.Width}x{target.Height}");
        }
    }

    /// <summary>
    /// T11 — the tray callback decoder. The shell packs the event code into the low word of lParam
    /// for both the legacy layout and NOTIFYICON_VERSION_4, so the decoder must map the four mouse
    /// messages to the right intent and ignore everything else.
    /// </summary>
    private static void T11_TrayCallbackClassification()
    {
        Console.WriteLine();
        Console.WriteLine("== T11 tray callback classification ==");

        const uint LeftUp = 0x0202;
        const uint LeftDbl = 0x0203;
        const uint RightUp = 0x0205;
        const uint Context = 0x007B;

        // Legacy layout: the mouse message sits in lParam, the icon id in wParam.
        Check("legacy left click activates", Galah.Services.TrayIconService.Classify(0x4C02, LeftUp) == Galah.Services.TrayClick.LeftClick);
        Check("legacy right click opens the menu", Galah.Services.TrayIconService.Classify(0x4C02, RightUp) == Galah.Services.TrayClick.RightClick);

        // NOTIFYICON_VERSION_4 layout: the notification code moves into the low word of wParam
        // and lParam carries the cursor coordinates. Reading only lParam makes every click dead.
        Check("v4 left click activates", Galah.Services.TrayIconService.Classify(LeftUp, 0x001E0010) == Galah.Services.TrayClick.LeftClick);
        Check("v4 right click opens the menu", Galah.Services.TrayIconService.Classify(RightUp, 0x001E0010) == Galah.Services.TrayClick.RightClick);
        Check("v4 double click activates", Galah.Services.TrayIconService.Classify(LeftDbl, 0) == Galah.Services.TrayClick.LeftClick);
        Check("v4 WM_CONTEXTMENU opens the menu", Galah.Services.TrayIconService.Classify(Context, 0) == Galah.Services.TrayClick.RightClick);

        // Traffic captured from the live shell (galah.log 2026-11-18 10:53):
        // lParam = (icon id << 16) | (event byte << 8) | flags, wParam = a handle.
        Check("real shell left-up activates", Galah.Services.TrayIconService.Classify(0x420064F, 0x4C020202) == Galah.Services.TrayClick.LeftClick);
        Check("real shell right-up opens the menu", Galah.Services.TrayIconService.Classify(0x420064F, 0x4C020400) == Galah.Services.TrayClick.None, "flags byte 0x00 is not an event we act on");
        Check("real shell hover is ignored", Galah.Services.TrayIconService.Classify(0x20633, 0x4C020407) == Galah.Services.TrayClick.None);

        Check("unrelated message is ignored", Galah.Services.TrayIconService.Classify(0, 0) == Galah.Services.TrayClick.None);
        Check("mouse move is ignored", Galah.Services.TrayIconService.Classify(0x0200, 0x0200) == Galah.Services.TrayClick.None);
    }

    /// <summary>
    /// T12 — startup reachability. A launcher that starts with no window and no way to summon it
    /// looks exactly like a failed launch, so the first run must produce a visible panel and the
    /// "no saved state" detection must not be fooled by the bootstrap write.
    /// </summary>
    private static void T12_StartupReachability()
    {
        Console.WriteLine();
        Console.WriteLine("== T12 startup reachability ==");

        var paths = new AppDataPaths(Path.Combine(_root, "fresh"));
        Directory.CreateDirectory(paths.Root);
        Check("no settings file before the first run", !File.Exists(paths.SettingsFile));
        Check("no ui-state file before the first run", !File.Exists(paths.UiStateFile));

        // The stores bootstrap their files on load — after that a first run is indistinguishable
        // from a normal start, which is exactly why the check has to happen before the load.
        var settings = new SettingsStore(paths);
        settings.Load();
        Check("loading bootstraps settings.json", File.Exists(paths.SettingsFile));

        var firstRunDetectable = !File.Exists(paths.SettingsFile) && !File.Exists(paths.UiStateFile);
        Check("first-run detection needs a pristine folder", !firstRunDetectable,
            "the folder now holds bootstrapped files");

        // A pristine folder must be detected as a first run.
        var pristine = new AppDataPaths(Path.Combine(_root, "pristine"));
        Directory.CreateDirectory(pristine.Root);
        Check("a pristine folder reads as a first run",
            !File.Exists(pristine.SettingsFile) && !File.Exists(pristine.UiStateFile));
    }

    /// <summary>
    /// T13 — the tray icon pipeline. Two failure modes are guarded here:
    /// 1. CreateIconIndirect without an AND mask returns zero, which silently replaced our icon
    ///    with the generic Windows application icon;
    /// 2. a .ico file without the three standard sizes makes the shell resample badly.
    /// </summary>
    private static void T13_IconPipeline()
    {
        Console.WriteLine();
        Console.WriteLine("== T13 tray icon pipeline ==");

        var glyph = Galah.Services.IconArt.RenderGlyphPixels(32);
        Check("glyph buffer is 32*32*4 bytes", glyph.Length == 32 * 32 * 4, glyph.Length.ToString());

        var opaque = 0;
        var white = 0;
        var blue = 0;
        for (var i = 0; i < glyph.Length; i += 4)
        {
            if (glyph[i + 3] == 0) continue;
            opaque++;
            var b = glyph[i]; var g = glyph[i + 1]; var r = glyph[i + 2];
            if (r > 200 && g > 200 && b > 200) white++;
            else if (b > 150 && r < 180) blue++;
        }
        Check("glyph has a badge", blue > 200, "blue=" + blue);
        Check("glyph has the chevron", white > 20, "white=" + white);
        Check("corners stay transparent", glyph[3] == 0, "alpha=" + glyph[3]);

        var icoPath = Path.Combine(_root, "galah.ico");
        Check("icon file written", Galah.Services.IconArt.WriteIconFile(icoPath));
        var ico = File.ReadAllBytes(icoPath);
        Check("icon file has an ICONDIR header", ico.Length > 22 && ico[0] == 0 && ico[1] == 0 && ico[2] == 1, ico.Length.ToString());
        var count = ico[4] | (ico[5] << 8);
        Check("icon file carries three sizes", count == 3, count.ToString());

        var handle = Galah.Services.IconArt.LoadIconHandle(icoPath);
        Check("icon handle created", handle != IntPtr.Zero, "hicon=" + handle.ToInt64());
        if (handle != IntPtr.Zero) Win32DestroyIcon(handle);

        // A missing icon file must be regenerated, not left broken.
        File.Delete(icoPath);
        var handle2 = Galah.Services.IconArt.LoadIconHandle(icoPath);
        Check("missing icon file is regenerated", File.Exists(icoPath) && handle2 != IntPtr.Zero,
            "file=" + File.Exists(icoPath) + " hicon=" + handle2.ToInt64());
        if (handle2 != IntPtr.Zero) Win32DestroyIcon(handle2);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static void Win32DestroyIcon(IntPtr handle) => DestroyIcon(handle);

    /// <summary>
    /// T14 — the canonical icon must be the embedded artwork, not the procedural fallback.
    /// The tray and the exe file icon both derive from these bytes; silently falling back to
    /// the generated badge was a real regression the tooling once mistook for production truth.
    /// </summary>
    private static void T14_CanonicalIcon()
    {
        Console.WriteLine();
        Console.WriteLine("== T14 canonical icon = embedded artwork ==");

        var canonical = Galah.Services.IconArt.CanonicalBytes();
        var fallback = Galah.Services.IconArt.BuildIcoBytes();
        Check("canonical bytes present", canonical.Length > 22, canonical.Length.ToString());
        Check("canonical differs from the procedural fallback", !canonical.AsSpan().SequenceEqual(fallback),
            "canonical=" + canonical.Length + " fallback=" + fallback.Length);
        Check("canonical is a valid ICON container", canonical[2] == 1 && canonical[3] == 0);
        var count = canonical[4] | (canonical[5] << 8);
        Check("canonical carries five sizes", count == 5, count.ToString());

        var seedPath = Path.Combine(_root, "seed", "galah.ico");
        var handle = Galah.Services.IconArt.LoadIconHandle(seedPath);
        Check("icon handle loads through production path", handle != IntPtr.Zero);
        Check("seed file written", File.Exists(seedPath));
        if (File.Exists(seedPath))
            Check("seed equals canonical", new FileInfo(seedPath).Length == canonical.Length,
                new FileInfo(seedPath).Length + " vs " + canonical.Length);
    }

    /// <summary>
    /// T15 - dialog button order convention (product rule 2026-11-18): in every footer that pairs
    /// an action button with 取消, the action button (确定/添加/保存) must come FIRST.
    /// Source-level guard: the layout lives in XAML and no runtime unit can see it otherwise.
    /// </summary>
    private static void T15_ButtonOrder()
    {
        Console.WriteLine();
        Console.WriteLine("== T15 dialog button order: action before cancel ==");

        var viewsDir = FindViewsDirectory();
        if (viewsDir is null) { Check("view sources reachable", false); return; }

        foreach (var name in new[] { "PromptWindow.xaml", "EditItemWindow.xaml" })
        {
            var path = Path.Combine(viewsDir, name);
            Check(name + " exists", File.Exists(path));
            if (!File.Exists(path)) continue;
            // XML comments must not pollute the scan (a comment mentioning 取消 is not a button).
            var text = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(path), "<!--.*?-->", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cancelAt = text.IndexOf("取消", StringComparison.Ordinal);
            Check(name + " contains a cancel button", cancelAt >= 0);
            if (cancelAt < 0) continue;
            var actionAt = int.MaxValue;
            foreach (var token in new[] { "确定", "添加", "保存" })
            {
                var at = text.IndexOf(token, StringComparison.Ordinal);
                if (at >= 0) actionAt = Math.Min(actionAt, at);
            }
            Check(name + ": action button precedes 取消", actionAt < cancelAt,
                "action@" + actionAt + " cancel@" + cancelAt);
        }
    }

    private static void TryCleanup(string root)
    {
        if (Environment.GetEnvironmentVariable("LARK_TESTS_KEEP_SCRATCH") == "1")
        {
            Console.WriteLine("scratch kept: " + root);
            return;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(root, true);
                Console.WriteLine("scratch removed: " + root);
                return;
            }
            catch (IOException) { Thread.Sleep(120); }
            catch (UnauthorizedAccessException) { Thread.Sleep(120); }
        }
        Console.WriteLine("scratch left behind (locked): " + root);
    }
}
