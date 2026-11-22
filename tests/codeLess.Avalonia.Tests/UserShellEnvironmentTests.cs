using System;
using System.IO;
using System.Runtime.InteropServices;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class UserShellEnvironmentTests
    {
        [Fact]
        public void ParseEnvBlock_IgnoresNoiseOutsideMarkers_AndKeepsMultiLineValues()
        {
            string m = "__M__";
            string text = "Welcome to zsh!\nnvm: loaded\n" + m + "PATH=/opt/a:/usr/bin\nHOME=/Users/x\nMULTI=line1\nline2\nEMPTY=\n" + m + "trailing prompt %\n";
            var env = UserShellEnvironment.ParseEnvBlock(text, m);
            Assert.Equal("/opt/a:/usr/bin", env["PATH"]);
            Assert.Equal("/Users/x", env["HOME"]);
            Assert.Equal("line1\nline2", env["MULTI"]);
            Assert.Equal("", env["EMPTY"]);
            Assert.False(env.ContainsKey("nvm: loaded"));
            Assert.Null(UserShellEnvironment.ParseEnvBlock("no markers here", m));
        }

        [Fact]
        public void CaptureLoginShellEnvironment_ReturnsTheUsersRealShellEnvironment()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var env = UserShellEnvironment.CaptureLoginShellEnvironment(15000, out string status);
            Assert.True(env != null, status);
            Assert.Contains("/usr/bin", env["PATH"]);
            Assert.True(env.ContainsKey("HOME"));
        }

        [Fact]
        public void WaitForResolution_ReturnsImmediately_WhenNeverStarted()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(UserShellEnvironment.WaitForResolution(5000));
            Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(1000));
        }
    }
}
