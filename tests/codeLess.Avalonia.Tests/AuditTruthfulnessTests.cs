using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using codeLess.Core;
using codeLess.AvaloniaApp;
using Avalonia.Headless.XUnit;

namespace codeLess.Avalonia.Tests
{
    public class AuditTruthfulnessTests
    {
        [AvaloniaFact]
        public void Audit_AllFixedCommands_AreBound()
        {
            var auditPath = Path.Combine(AppContext.BaseDirectory, "../../../../../docs/KEYBINDING_AUDIT.md");
            if (!File.Exists(auditPath))
            {
                auditPath = Path.Combine(Environment.CurrentDirectory, "docs/KEYBINDING_AUDIT.md");
            }
            if (!File.Exists(auditPath))
            {
                auditPath = Path.Combine(Environment.CurrentDirectory, "../../docs/KEYBINDING_AUDIT.md");
            }
            if (!File.Exists(auditPath))
            {
                auditPath = Path.GetFullPath("/tmp/codeless-work/keys/docs/KEYBINDING_AUDIT.md");
            }

            var lines = File.ReadAllLines(auditPath);
            var mw = new MainWindow();
            
            var kmField = typeof(MainWindow).GetField("keybindings", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(kmField);
            var km = kmField.GetValue(mw);
            Assert.NotNull(km);

            var type = typeof(KeybindingManager);
            var field = type.GetField("actions", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            var actions = (System.Collections.IDictionary)field.GetValue(km);

            int checkedCount = 0;
            foreach (var line in lines)
            {
                if (line.Contains("|") && line.Contains("Fixed"))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 6)
                    {
                        var status = parts[5].Trim();
                        if (status.StartsWith("Fixed"))
                        {
                            var cmdRaw = parts[1].Trim();
                            var cmd = cmdRaw.Trim('`');
                            if (!string.IsNullOrEmpty(cmd) && cmd != "Command ID")
                            {
                                bool isBound = actions.Contains(cmd);
                                Assert.True(isBound, $"Command {cmd} is marked {status} but not bound in KeybindingManager.");
                                checkedCount++;
                            }
                        }
                    }
                }
            }
            Assert.True(checkedCount > 0, "No fixed commands found in audit file.");
        }
    }
}
