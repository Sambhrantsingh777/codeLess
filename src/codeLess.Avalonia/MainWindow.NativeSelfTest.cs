using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Hidden real-app check for things headless tests cannot see (`codeLess --native-self-test report.json`): starts the
    /// real app on a temp workspace, dumps <c>[NSApp mainMenu]</c> (every menu, item title and key equivalent, recursively),
    /// sends an in-process synthetic Ctrl+Tab NSEvent through <c>[NSApp sendEvent:]</c> with two editors open (checks
    /// <see cref="MacCtrlTabBridge"/>), writes a JSON report and exits by itself. A watchdog ends the process after 15 s.
    /// </summary>
    public partial class MainWindow
    {
        public async Task RunNativeSelfTestAsync(string reportPath)
        {
            var report = new Dictionary<string, object>
            {
                ["version"] = CurrentAppVersion,
                ["os"] = RuntimeInformation.OSDescription,
                ["arch"] = RuntimeInformation.OSArchitecture.ToString()
            };
            void Write()
            {
                try
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }
            try
            {
                await Task.Delay(1500); // let the native menu exporter run its queued layout reset
                report["mainMenu"] = OperatingSystem.IsMacOS() ? MacSelfTestNative.DumpMainMenu() : null;
                report["ctrlTabBridgeInstalled"] = MacCtrlTabBridge.IsInstalled;
                Write();

                string ws = workspaceRoot ?? "";
                string a = Path.Combine(ws, "a.txt"), b = Path.Combine(ws, "b.txt");
                var ta = File.Exists(a) ? OpenFileInTab(a) : null;
                await Task.Delay(150);
                var tb = File.Exists(b) ? OpenFileInTab(b) : null;
                await Task.Delay(300);
                report["openEditors"] = new[] { ta?.FilePath, tb?.FilePath };
                string before = activeTab?.FilePath;
                report["activeBefore"] = before;
                if (OperatingSystem.IsMacOS() && ta != null && tb != null)
                {
                    var handle = TryGetPlatformHandle();
                    IntPtr nsWindow = handle != null && handle.HandleDescriptor == "NSWindow" ? handle.Handle : IntPtr.Zero;
                    var info = MacSelfTestNative.PrepareKeyWindow(nsWindow);
                    foreach (var kv in info) report[kv.Key] = kv.Value;
                    await Task.Delay(400);
                    // like a real keyboard: Control goes down (FlagsChanged), Tab, then Control comes up
                    report["ctrlPressSent"] = MacSelfTestNative.SendControlFlags(nsWindow, true);
                    report["ctrlTabKeyDownSent"] = MacSelfTestNative.SendCtrlTab(nsWindow);
                    // -sendEvent: is synchronous: the picker is open now unless the Avalonia key pipeline never saw the key
                    report["switcherOpenedImmediately"] = IsEditorSwitcherOpen;
                    report["activeAfterKeyDown"] = activeTab?.FilePath;
                    await Task.Delay(300);
                    report["switcherOpenedAfterKeyDown"] = IsEditorSwitcherOpen;
                    report["ctrlReleaseSent"] = MacSelfTestNative.SendControlFlags(nsWindow, false);
                    await Task.Delay(400);
                    string after = activeTab?.FilePath;
                    report["activeAfter"] = after;
                    report["activeEditorChanged"] = !string.Equals(before, after, StringComparison.Ordinal);
                    if (IsEditorSwitcherOpen) { report["switcherStillOpenAfterRelease"] = true; CancelEditorSwitcher(); }
                }
                report["result"] = "completed";
            }
            catch (Exception ex)
            {
                report["error"] = ex.ToString();
            }
            Write();
            SuppressUnsavedPrompts = true;
            Close();
            Environment.Exit(0);
        }
    }

    /// <summary>ObjC runtime helpers for the native self-test (macOS only).</summary>
    internal static class MacSelfTestNative
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [StructLayout(LayoutKind.Sequential)]
        private struct NSPoint { public double X, Y; }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Msg(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgL(IntPtr r, IntPtr s, long a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgP(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgB(IntPtr r, IntPtr s, byte a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long MsgRetL(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong MsgRetUL(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte MsgRetB(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgStr(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgKeyEvent(IntPtr cls, IntPtr sel, ulong type, NSPoint location, ulong flags, double timestamp,
            long windowNumber, IntPtr context, IntPtr chars, IntPtr charsIgnoringModifiers, byte isARepeat, ushort keyCode);


        private static IntPtr S(string name) => sel_registerName(name);
        private static IntPtr App => Msg(objc_getClass("NSApplication"), S("sharedApplication"));

        private static string Str(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            IntPtr utf8 = Msg(nsString, S("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        public static object DumpMainMenu()
        {
            IntPtr menu = Msg(App, S("mainMenu"));
            if (menu == IntPtr.Zero) return null;
            return DumpMenu(menu, 0);
        }

        private static List<object> DumpMenu(IntPtr menu, int depth)
        {
            var list = new List<object>();
            long n = MsgRetL(menu, S("numberOfItems"));
            for (long i = 0; i < n && i < 400; i++)
            {
                IntPtr item = MsgL(menu, S("itemAtIndex:"), i);
                if (item == IntPtr.Zero) continue;
                if (MsgRetB(item, S("isSeparatorItem")) != 0) { list.Add("---"); continue; }
                var d = new Dictionary<string, object> { ["title"] = Str(Msg(item, S("title"))) };
                string key = Str(Msg(item, S("keyEquivalent")));
                if (!string.IsNullOrEmpty(key)) d["key"] = DescribeKey(key, MsgRetUL(item, S("keyEquivalentModifierMask")));
                if (MsgRetB(item, S("isEnabled")) == 0) d["enabled"] = false;
                if (MsgRetL(item, S("state")) == 1) d["checked"] = true;
                IntPtr sub = Msg(item, S("submenu"));
                if (sub != IntPtr.Zero && depth < 6) d["items"] = DumpMenu(sub, depth + 1);
                list.Add(d);
            }
            return list;
        }

        private static string DescribeKey(string key, ulong mask)
        {
            string m = "";
            if ((mask & (1UL << 18)) != 0) m += "Ctrl+";
            if ((mask & (1UL << 19)) != 0) m += "Alt+";
            if ((mask & (1UL << 17)) != 0) m += "Shift+";
            if ((mask & (1UL << 20)) != 0) m += "Cmd+";
            string k = key;
            if (key.Length == 1)
            {
                int c = key[0];
                if (c == 9) k = "Tab";
                else if (c == 13 || c == 3) k = "Enter";
                else if (c == 27) k = "Esc";
                else if (c == 8 || c == 127) k = "Backspace";
                else if (c >= 0xF700 && c <= 0xF8FF) k = "U+" + c.ToString("X4");
                else if (char.IsUpper(key[0])) { k = key; if (!m.Contains("Shift+")) m += "Shift+"; }
                else k = key.ToUpperInvariant();
            }
            return m + k;
        }

        /// <summary>Activates the app and makes the window key so NSApp routes key events to it.</summary>
        public static Dictionary<string, object> PrepareKeyWindow(IntPtr nsWindow)
        {
            var r = new Dictionary<string, object> { ["nsWindowFound"] = nsWindow != IntPtr.Zero };
            try
            {
                MsgB(App, S("activateIgnoringOtherApps:"), 1);
                if (nsWindow != IntPtr.Zero) MsgP(nsWindow, S("makeKeyAndOrderFront:"), IntPtr.Zero);
                r["appActive"] = MsgRetB(App, S("isActive")) != 0;
                r["keyWindowIsMain"] = Msg(App, S("keyWindow")) == nsWindow;
            }
            catch (Exception ex) { r["prepareError"] = ex.Message; }
            return r;
        }

        /// <summary>+[NSEvent keyEventWithType:...] Ctrl+Tab key-down, dispatched through -[NSApplication sendEvent:].</summary>
        public static bool SendCtrlTab(IntPtr nsWindow)
        {
            try
            {
                long windowNumber = nsWindow != IntPtr.Zero ? MsgRetL(nsWindow, S("windowNumber")) : 0;
                IntPtr tab = MsgStr(objc_getClass("NSString"), S("stringWithUTF8String:"), "\t");
                IntPtr evt = MsgKeyEvent(objc_getClass("NSEvent"), S("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
                    10 /* NSEventTypeKeyDown */, new NSPoint(), 1UL << 18 /* Control */, 0, windowNumber, IntPtr.Zero, tab, tab, 0, 48 /* kVK_Tab */);
                if (evt == IntPtr.Zero) return false;
                MsgP(App, S("sendEvent:"), evt);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Control down / up: a FlagsChanged event (kVK_Control) for the window.</summary>
        public static bool SendControlFlags(IntPtr nsWindow, bool down)
        {
            try
            {
                if (nsWindow == IntPtr.Zero) return false;
                long windowNumber = MsgRetL(nsWindow, S("windowNumber"));
                IntPtr empty = MsgStr(objc_getClass("NSString"), S("stringWithUTF8String:"), "");
                IntPtr fe = MsgKeyEvent(objc_getClass("NSEvent"), S("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
                    12 /* NSEventTypeFlagsChanged */, new NSPoint(), down ? (1UL << 18) | 0x1 /* Control + NX_DEVICELCTLKEYMASK */ : 0, 0, windowNumber, IntPtr.Zero, empty, empty, 0, 59 /* kVK_Control */);
                if (fe == IntPtr.Zero) return false;
                MsgP(App, S("sendEvent:"), fe);
                return true;
            }
            catch { return false; }
        }
    }
}
