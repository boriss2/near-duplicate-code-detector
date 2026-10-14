using System.IO;
using Xunit;

namespace CsharpTokenizer.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void TokenizesASingleWellFormedFile()
        {
            var projectsDir = Path.Combine(Path.GetTempPath(), "cst-smoke-" + Path.GetRandomFileName());
            var projectDir = Path.Combine(projectsDir, "proj1");
            var outDir = Path.Combine(Path.GetTempPath(), "cst-smoke-out-" + Path.GetRandomFileName());
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(outDir);
            try
            {
                File.WriteAllText(Path.Combine(projectDir, "Good.cs"),
                    "namespace Demo { class A { void M() { int alpha = 1; } } }");

                var result = CliRunner.Run(projectsDir, outDir, "true");

                Assert.Equal(0, result.ExitCode);
                var lines = GzJsonl.ReadLines(Path.Combine(outDir, "proj1.jsonl.gz"));
                Assert.Single(lines);
                Assert.Contains("\"alpha\"", lines[0]);
            }
            finally
            {
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }
    }
}
