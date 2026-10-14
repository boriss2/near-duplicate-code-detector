using System.IO;
using Xunit;

namespace CsharpTokenizer.Tests
{
    public class CliValidationTests
    {
        [Fact]
        public void MalformedThirdArgument_PrintsUsageInsteadOfCrashing()
        {
            var projectsDir = Path.Combine(Path.GetTempPath(), "cst-cli-" + Path.GetRandomFileName());
            var projectDir = Path.Combine(projectsDir, "proj1");
            var outDir = Path.Combine(Path.GetTempPath(), "cst-cli-out-" + Path.GetRandomFileName());
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(outDir);
            try
            {
                File.WriteAllText(Path.Combine(projectDir, "Good.cs"),
                    "namespace Demo { class A { void M() { int alpha = 1; } } }");

                var result = CliRunner.Run(projectsDir, outDir, "yes");

                Assert.Equal(0, result.ExitCode);
                Assert.Contains("Usage", result.StdOut);
                Assert.False(File.Exists(Path.Combine(outDir, "proj1.jsonl.gz")));
            }
            finally
            {
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }
    }
}
