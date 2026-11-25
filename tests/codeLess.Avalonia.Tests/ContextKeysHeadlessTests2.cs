using System;
using Xunit;
using Avalonia.Headless.XUnit;
using codeLess.Core;
using codeLess.AvaloniaApp;
using System.Reflection;
using System.Linq;

namespace codeLess.Avalonia.Tests
{
    public class MockCtx : IContextKeyService
    {
        public bool Focus = false;
        public object GetValue(string key) { return key.ToLowerInvariant() == "editortextfocus" ? Focus : false; }
    }

    [Collection("SequentialWorkbench")]
    public class ContextKeysHeadlessTests2
    {
        [AvaloniaFact]
        public void DeleteLines_FiresInEditor_NotTerminal()
        {
            var mw = new MainWindow();
            try
            {
                var kmField = typeof(MainWindow).GetField("keybindings", BindingFlags.NonPublic | BindingFlags.Instance);
                var km = (KeybindingManager)kmField.GetValue(mw);

                var bindingsField = typeof(KeybindingManager).GetField("activeBindings", BindingFlags.NonPublic | BindingFlags.Instance);
                var activeBindings = (System.Collections.Generic.List<KeybindingEntry>)bindingsField.GetValue(km);
                var entry = activeBindings.FirstOrDefault(b => b.Command == "editor.action.deleteLines");
                Assert.NotNull(entry);
                string ks = entry.Key;

                var ctx = new MockCtx();
                ctx.Focus = true;

                var actionsField = typeof(KeybindingManager).GetField("actions", BindingFlags.NonPublic | BindingFlags.Instance);
                var actions = (System.Collections.Generic.Dictionary<string, Action>)actionsField.GetValue(km);
                Assert.True(actions.ContainsKey("editor.action.deleteLines"));

                bool editorHandled = km.Dispatch(ks, ctx, _ => { }, false);

                ctx.Focus = false;
                bool terminalHandled = km.Dispatch(ks, ctx, _ => { }, true);

                Assert.True(editorHandled, "Editor should have handled it");
                Assert.False(terminalHandled, "Terminal should not have handled it");
            }
            finally
            {
                mw.SuppressUnsavedPrompts = true;
                mw.Close();
            }
        }
    }
}
