using System;
using System.IO;
using Xunit;

namespace CsharpTokenizer.Tests
{
    public class FaultIsolationTests
    {
        [Fact]
        public void UnreadableFile_IsSkippedWithoutLosingTheRestOfTheProject()
        {
            if (OperatingSystem.IsWindows())
            {
                // File.SetUnixFileMode has no Windows equivalent; this fix is validated on Unix (macOS/Linux).
                return;
            }

            var projectsDir = Path.Combine(Path.GetTempPath(), "cst-fault-" + Path.GetRandomFileName());
            var projectDir = Path.Combine(projectsDir, "proj1");
            var outDir = Path.Combine(Path.GetTempPath(), "cst-fault-out-" + Path.GetRandomFileName());
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(outDir);
            string badFile = Path.Combine(projectDir, "Zzz_Bad.cs");
            try
            {
                File.WriteAllText(Path.Combine(projectDir, "Good1.cs"),
                    "namespace Demo { class A { void M() { int alpha = 1; } } }");
                File.WriteAllText(Path.Combine(projectDir, "Good2.cs"),
                    "namespace Demo { class B { void M() { int beta = 2; } } }");
                File.WriteAllText(badFile,
                    "namespace Demo { class Bad { void M() { int gamma = 3; } } }");
                File.SetUnixFileMode(badFile, UnixFileMode.None);

                var result = CliRunner.Run(projectsDir, outDir, "true");

                Assert.Equal(0, result.ExitCode);
                Assert.Contains("Zzz_Bad.cs", result.StdErr);

                var lines = GzJsonl.ReadLines(Path.Combine(outDir, "proj1.jsonl.gz"));
                Assert.Equal(2, lines.Count);
                Assert.Contains(lines, l => l.Contains("\"alpha\""));
                Assert.Contains(lines, l => l.Contains("\"beta\""));
            }
            finally
            {
                File.SetUnixFileMode(badFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }
    }
}
