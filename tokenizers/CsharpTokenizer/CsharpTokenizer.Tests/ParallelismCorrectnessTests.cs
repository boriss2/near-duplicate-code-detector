using System.IO;
using System.Linq;
using Xunit;

namespace CsharpTokenizer.Tests
{
    public class ParallelismCorrectnessTests
    {
        [Fact]
        public void ManyProjectsAndFiles_AllFilesAreTokenizedExactlyOnce()
        {
            const int ProjectCount = 10;
            const int FilesPerProject = 15;

            var projectsDir = Path.Combine(Path.GetTempPath(), "cst-scale-" + Path.GetRandomFileName());
            var outDir = Path.Combine(Path.GetTempPath(), "cst-scale-out-" + Path.GetRandomFileName());
            Directory.CreateDirectory(projectsDir);
            Directory.CreateDirectory(outDir);
            try
            {
                for (int p = 0; p < ProjectCount; p++)
                {
                    var projectDir = Path.Combine(projectsDir, $"proj{p}");
                    Directory.CreateDirectory(projectDir);
                    for (int f = 0; f < FilesPerProject; f++)
                    {
                        File.WriteAllText(
                            Path.Combine(projectDir, $"File{f}.cs"),
                            $"namespace Demo{p} {{ class C{f} {{ void M() {{ int v{f} = {f}; }} }} }}");
                    }
                }

                var result = CliRunner.Run(projectsDir, outDir, "true");

                Assert.Equal(0, result.ExitCode);

                var totalLines = Directory.GetFiles(outDir, "*.jsonl.gz")
                    .Sum(path => GzJsonl.ReadLines(path).Count);

                Assert.Equal(ProjectCount * FilesPerProject, totalLines);
            }
            finally
            {
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }
    }
}
