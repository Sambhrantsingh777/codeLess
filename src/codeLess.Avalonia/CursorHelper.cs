using Avalonia.Input;
namespace codeLess.AvaloniaApp {
    public static class CursorHelper {
        public static Cursor GetCursor(StandardCursorType type) {
            try { return new Cursor(type); } catch { return null; }
        }
    }
}
