using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class ChatFileMentionsM9cTests : IDisposable
    {
        private readonly List<string> _tempDirs = new List<string>();
        private readonly List<MainWindow> _windows = new List<MainWindow>();

        private string CreateTempWorkspace()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeless-m9c-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            dir = Path.GetFullPath(dir);
            TestTrust.Trust(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private MainWindow CreateWindow()
        {
            var win = new MainWindow();
            _windows.Add(win);
            win.Show();
            return win;
        }

        public void Dispose()
        {
            foreach (var win in _windows)
            {
                try { win.Close(); } catch { }
            }
            _windows.Clear();

            foreach (string dir in _tempDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        [Fact]
        public void ChatAtMentionCompleter_UsesQuickOpenFuzzyMatcher_And_ReplacesToken()
        {
            string ws = CreateTempWorkspace();
            var files = new List<string>
            {
                Path.Combine(ws, "src", "Controllers", "BillingController.cs"),
                Path.Combine(ws, "src", "Services", "AuthService.cs"),
                Path.Combine(ws, "README.md")
            };

            Assert.True(ChatAtMentionCompleter.TryExtractActiveAtQuery("Please inspect @bcon", 20, out int atIdx, out string query));
            Assert.Equal(15, atIdx);
            Assert.Equal("bcon", query);

            var suggestions = ChatAtMentionCompleter.GetSuggestions(query, ws, files, 10);
            Assert.NotEmpty(suggestions);
            Assert.Equal("src/Controllers/BillingController.cs", suggestions[0].RelativePath);
            Assert.Equal("@src/Controllers/BillingController.cs", suggestions[0].InsertToken);

            string replaced = ChatAtMentionCompleter.ApplySelection("Please inspect @bcon", 20, suggestions[0].InsertToken, out int newCaret);
            Assert.Equal("Please inspect @src/Controllers/BillingController.cs ", replaced);
            Assert.Equal(replaced.Length, newCaret);

            // Also verify 50 KB byte cap in FormatFileAttachmentWithReadCaps
            string hugeContent = string.Join("\n", Enumerable.Range(1, 1500).Select(i => $"Line {i:D4}: " + new string('x', 80)));
            string cappedByBytes = ChatAtMentionCompleter.FormatFileAttachmentWithReadCaps(hugeContent, null);
            Assert.Contains("[truncated at read_file cap:", cappedByBytes);
            Assert.True(Encoding.UTF8.GetByteCount(cappedByBytes) <= ChatAtMentionCompleter.MaxReadBytes + 256);
        }

        [AvaloniaFact]
        public void AvaloniaHeadless_TypingAtInChatInput_OpensFuzzyCompletion_And_AcceptAttachesPill()
        {
            string ws = CreateTempWorkspace();
            Directory.CreateDirectory(Path.Combine(ws, "src"));
            File.WriteAllText(Path.Combine(ws, "src", "AlphaEngine.cs"), "class AlphaEngine {}");
            File.WriteAllText(Path.Combine(ws, "src", "BetaWidget.cs"), "class BetaWidget {}");
            File.WriteAllText(Path.Combine(ws, "README.md"), "# Readme");

            var win = CreateWindow();
            win.LoadWorkspace(ws);

            // Typing @bet in chat input opens completion popup with src/BetaWidget.cs ranked first
            win.ChatPromptInput.Text = "Refactor @bet";
            win.ChatPromptInput.CaretIndex = win.ChatPromptInput.Text.Length;
            win.UpdateChatAtMentionPopup();

            Assert.True(win.IsChatAtMentionPopupOpen);
            Assert.NotEmpty(win.ChatAtMentionSuggestions);
            Assert.Equal("src/BetaWidget.cs", win.ChatAtMentionSuggestions[0].RelativePath);

            // Pressing Enter/Tab while popup is open accepts the selected file mention instead of sending
            bool accepted = win.AcceptSelectedChatAtMention();
            Assert.True(accepted);
            Assert.False(win.IsChatAtMentionPopupOpen);
            Assert.Equal("Refactor @src/BetaWidget.cs ", win.ChatPromptInput.Text);
            Assert.Contains("src/BetaWidget.cs", win.AttachedContextPills);
        }

        [AvaloniaFact]
        public async Task AvaloniaHeadless_SelectedAtFileContent_IsAttachedToPrompt_WithReadFileSizeCaps()
        {
            string ws = CreateTempWorkspace();
            Directory.CreateDirectory(Path.Combine(ws, "src"));

            // Create a 2,400-line file (under 50 KB total) to verify read_file's 2,000-line cap is enforced on @file attachments
            var sb = new StringBuilder();
            for (int i = 1; i <= 2400; i++)
            {
                sb.AppendLine($"// L{i:D4}");
            }
            File.WriteAllText(Path.Combine(ws, "src", "BigService.cs"), sb.ToString());

            var win = CreateWindow();
            win.LoadWorkspace(ws);

            win.ChatPromptInput.Text = "Summarize @BigServ";
            win.ChatPromptInput.CaretIndex = win.ChatPromptInput.Text.Length;
            win.UpdateChatAtMentionPopup();
            Assert.True(win.IsChatAtMentionPopupOpen);
            Assert.Equal("src/BigService.cs", win.ChatAtMentionSuggestions[0].RelativePath);
            Assert.True(win.AcceptSelectedChatAtMention());

            string capturedPromptPayload = null;
            win.HarnessEngine.CustomModelResponder = (sys, msgs) =>
            {
                capturedPromptPayload = msgs[0]["content"];
                return Task.FromResult("Summary complete.");
            };

            string reply = await win.SendChatMessageAsync(win.ChatPromptInput.Text);
            Assert.Equal("Summary complete.", reply);
            Assert.NotNull(capturedPromptPayload);
            Assert.Contains("<pinned_context_file file=\"src/BigService.cs\">", capturedPromptPayload);
            Assert.Contains("// L0001", capturedPromptPayload);
            Assert.Contains("// L2000", capturedPromptPayload);
            Assert.DoesNotContain("// L2001", capturedPromptPayload);
            Assert.Contains("[truncated at read_file cap: 2000 of", capturedPromptPayload);
        }
    }
}
