using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace CsharpTokenizer.Tests
{
    public class EncodingTests
    {
        [Fact]
        public void NonUtf8File_IsSkippedWithWarningInsteadOfSilentlyCorrupted()
        {
            var projectsDir = Path.Combine(Path.GetTempPath(), "cst-encoding-" + Path.GetRandomFileName());
            var projectDir = Path.Combine(projectsDir, "proj1");
            var outDir = Path.Combine(Path.GetTempPath(), "cst-encoding-out-" + Path.GetRandomFileName());
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(outDir);
            try
            {
                var prefix = Encoding.ASCII.GetBytes("namespace Demo { class A { string S() { return \"");
                var suffix = Encoding.ASCII.GetBytes("\"; } } }");
                // 0xFF is never a valid byte anywhere in a UTF-8 sequence.
                var fileBytes = prefix.Concat(new byte[] { 0xFF }).Concat(suffix).ToArray();
                File.WriteAllBytes(Path.Combine(projectDir, "BadEncoding.cs"), fileBytes);

                var result = CliRunner.Run(projectsDir, outDir, "false");

                Assert.Equal(0, result.ExitCode);
                Assert.Contains("BadEncoding.cs", result.StdErr);

                var lines = GzJsonl.ReadLines(Path.Combine(outDir, "proj1.jsonl.gz"));
                Assert.Empty(lines);
                Assert.DoesNotContain(lines, l => l.Contains("�"));
            }
            finally
            {
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }

        [Fact]
        public void Utf8FileWithBom_IsStillTokenizedCorrectly()
        {
            var projectsDir = Path.Combine(Path.GetTempPath(), "cst-bom-" + Path.GetRandomFileName());
            var projectDir = Path.Combine(projectsDir, "proj1");
            var outDir = Path.Combine(Path.GetTempPath(), "cst-bom-out-" + Path.GetRandomFileName());
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(outDir);
            try
            {
                var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                File.WriteAllText(
                    Path.Combine(projectDir, "WithBom.cs"),
                    "namespace Demo { class A { void M() { int alpha = 1; } } }",
                    utf8Bom);

                var result = CliRunner.Run(projectsDir, outDir, "true");

                Assert.Equal(0, result.ExitCode);
                var lines = GzJsonl.ReadLines(Path.Combine(outDir, "proj1.jsonl.gz"));
                Assert.Single(lines);
                Assert.Contains("\"alpha\"", lines[0]);
                // Ordinal comparison is required here: xunit's default (culture-aware) string
                // comparison treats U+FEFF as a zero-weight/ignorable character, so
                // Assert.DoesNotContain("﻿", s) reports a false "match" at position 0 for
                // ANY string s, regardless of whether a BOM is actually present. Ordinal makes
                // this assertion actually test for a literal stray BOM character.
                Assert.DoesNotContain("﻿", lines[0], StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }
    }
}
