using System.IO;
using System.Text;
using CsharpTokenizer;
using Xunit;

namespace CsharpTokenizer.Tests
{
    public class StackOverflowGuardTests
    {
        private const int NestingDepth = 20000;

        [Fact]
        public void PathologicallyNestedExpression_IsSkippedInsteadOfCrashingTheProcess()
        {
            var projectsDir = Path.Combine(Path.GetTempPath(), "cst-overflow-" + Path.GetRandomFileName());
            var projectDir = Path.Combine(projectsDir, "proj1");
            var outDir = Path.Combine(Path.GetTempPath(), "cst-overflow-out-" + Path.GetRandomFileName());
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(outDir);
            try
            {
                var nested = new StringBuilder();
                nested.Append("namespace Demo { class N { int M() { return ");
                nested.Append('(', NestingDepth);
                nested.Append('1');
                nested.Append(')', NestingDepth);
                nested.Append("; } } }");
                File.WriteAllText(Path.Combine(projectDir, "Nested.cs"), nested.ToString());

                var result = CliRunner.Run(projectsDir, outDir, "true");

                Assert.Equal(0, result.ExitCode);
                Assert.Contains("Nested.cs", result.StdErr);
            }
            finally
            {
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }

        [Fact]
        public void ExceedsMaxNestingDepth_AllowsShallowNesting()
        {
            Assert.False(Program.ExceedsMaxNestingDepth("((((((((((1))))))))))", maxDepth: 20));
        }

        [Fact]
        public void ExceedsMaxNestingDepth_RejectsDeepNesting()
        {
            var deeplyNested = new string('(', 10) + "1" + new string(')', 10);
            Assert.True(Program.ExceedsMaxNestingDepth(deeplyNested, maxDepth: 5));
        }

        [Fact]
        public void ExceedsMaxNestingDepth_CountsBraceAndBracketNestingToo()
        {
            const string mixed = "{[({[({[1";
            Assert.True(Program.ExceedsMaxNestingDepth(mixed, maxDepth: 5));
        }

        [Fact]
        public void ExceedsMaxNestingDepth_UnbalancedClosersDoNotUnderflow()
        {
            Assert.False(Program.ExceedsMaxNestingDepth(")))))", maxDepth: 5));
        }
    }
}
