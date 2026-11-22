using System;
using System.Runtime.InteropServices;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// macOS: AppKit treats Control+Tab / Control+Shift+Tab as a key equivalent: NSApplication offers it to
    /// -performKeyEquivalent: of the key window's views and, when nobody claims it, NSWindow uses it for key-view
    /// navigation; the first responder's -keyDown: is never called. Avalonia's native view (AvnView, Avalonia 11.1)
    /// does not implement -performKeyEquivalent:, so Avalonia never raised KeyDown for Ctrl+Tab and the editor could not
    /// be switched (v3.3.1 bug report). This adds -performKeyEquivalent: to AvnView at runtime: Ctrl+Tab is forwarded to
    /// the view's own -keyDown: (normal Avalonia key pipeline, so the window's tunnel handler sees it) and claimed;
    /// every other key equivalent goes to NSView's implementation unchanged. Chromium/Electron (VS Code) do the same.
    /// </summary>
    public static class MacCtrlTabBridge
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const ulong NSEventTypeKeyDown = 10;
        private const ulong FlagControl = 1UL << 18;
        private const ulong FlagCommand = 1UL << 20;
        private const ushort KeyCodeTab = 48;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte PerformKeyEquivalentFn(IntPtr self, IntPtr sel, IntPtr evt);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr class_getMethodImplementation(IntPtr cls, IntPtr sel);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendULong(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ushort SendUShort(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

        private static bool installed;
        private static PerformKeyEquivalentFn impl;   // kept alive for the process lifetime
        private static PerformKeyEquivalentFn superImpl;
        private static IntPtr selType, selModifierFlags, selKeyCode, selKeyDown;

        /// <summary>True once the method was added (macOS desktop with the Avalonia native backend only).</summary>
        public static bool IsInstalled => installed;

        /// <summary>Pure decision used by the bridge (unit-tested): forward only Ctrl(+Shift)+Tab without Cmd.</summary>
        public static bool ShouldForward(ulong eventType, ushort keyCode, ulong modifierFlags)
        {
            return eventType == NSEventTypeKeyDown && keyCode == KeyCodeTab &&
                   (modifierFlags & FlagControl) != 0 && (modifierFlags & FlagCommand) == 0;
        }

        public static void Install()
        {
            if (installed || !OperatingSystem.IsMacOS()) return;
            try
            {
                IntPtr avnView = objc_getClass("AvnView");
                IntPtr nsView = objc_getClass("NSView");
                if (avnView == IntPtr.Zero || nsView == IntPtr.Zero) return; // headless / other backend
                IntPtr selPerform = sel_registerName("performKeyEquivalent:");
                selType = sel_registerName("type");
                selModifierFlags = sel_registerName("modifierFlags");
                selKeyCode = sel_registerName("keyCode");
                selKeyDown = sel_registerName("keyDown:");
                IntPtr superImp = class_getMethodImplementation(nsView, selPerform);
                if (superImp != IntPtr.Zero) superImpl = Marshal.GetDelegateForFunctionPointer<PerformKeyEquivalentFn>(superImp);
                impl = PerformKeyEquivalent;
                IntPtr fp = Marshal.GetFunctionPointerForDelegate(impl);
                installed = class_addMethod(avnView, selPerform, fp, "c@:@");
            }
            catch { installed = false; }
        }

        private static byte PerformKeyEquivalent(IntPtr self, IntPtr sel, IntPtr evt)
        {
            try
            {
                if (evt != IntPtr.Zero && ShouldForward(SendULong(evt, selType), SendUShort(evt, selKeyCode), SendULong(evt, selModifierFlags)))
                {
                    SendVoidPtr(self, selKeyDown, evt);
                    return 1;
                }
                return superImpl != null ? superImpl(self, sel, evt) : (byte)0;
            }
            catch { return 0; }
        }
    }
}
