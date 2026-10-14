# CsharpTokenizer Adversarial-Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the 7 findings from the adversarial code review of `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs` (a stack-overflow DoS, a crash-and-data-loss bug, a CLI crash, silent encoding corruption, unbounded nested parallelism, a Release-mode-dead assertion, and dead code), and add a new xUnit test project that proves each issue existed and proves each fix resolves it.

**Architecture:** `Program.cs` is a single-file static-method pipeline: `Main` → `ExtractForProjectFolder` (one call per project directory, writes one `.jsonl.gz`) → `GetJsonForFile` (one call per `.cs` file) → `GetFileIdentifierTokens` → `GetASTFromFile` (Roslyn parse). All fixes are localized to this file. Because two of the bugs are process-crashing (an uncatchable `StackOverflowException`, and a bug that only manifests through `Parallel.ForEach`/`Process` exit-code semantics), the proving tests must run the tool **out-of-process** (spawn the built `CsharpTokenizer.dll` as a child process and assert on its exit code/stdout/stderr) rather than calling its methods in-process, which would otherwise crash the test runner itself for the stack-overflow case.

**Tech Stack:** .NET 9 SDK (already installed), xUnit (`dotnet new xunit`), `System.Diagnostics.Process` for out-of-process test execution, `System.IO.Compression.GZipStream` for reading tool output in tests.

---

## Context: the 7 findings being fixed

| # | Finding | Severity |
|---|---|---|
| 1 | `CSharpSyntaxTree.ParseText` recurses on nested expressions with no depth limit — a crafted `.cs` file with ~60,000 nested parens crashes the whole process with an uncatchable `StackOverflowException`. Reproduced directly. | CONFIRMED |
| 2 | No exception handling around per-file tokenization — one unreadable/unparsable file crashes the whole process (`AggregateException`, exit 134) and leaves that project's `.jsonl.gz` as a 0-byte, invalid gzip file. Reproduced directly (chmod 000 repro). | CONFIRMED |
| 3 | `bool.Parse(args[2])` throws an unhandled exception for any value other than `true`/`false` instead of printing the usage message. Reproduced directly. | CONFIRMED |
| 4 | `StreamReader(filePath)` default UTF-8 decoding silently replaces invalid byte sequences with U+FFFD for non-UTF8 `.cs` files, corrupting tokens with no warning. | PLAUSIBLE |
| 5 | Nested parallelism (`Parallel.ForEach` over projects + inner `.AsParallel()` over files) has no shared degree-of-parallelism bound, risking thread-pool oversubscription on large corpora. | PLAUSIBLE |
| 6 | `Debug.Assert(filepath.StartsWith(baseDir))` is compiled out entirely in Release builds (no `DEBUG` symbol), providing zero protection in the shipped binary. | PLAUSIBLE |
| 7 | `GetFileTokens` is dead code — never called, diverges from the method actually used. | PLAUSIBLE |

---

## File Structure

- **Modify:** `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs` — all 7 fixes land here.
- **Modify:** `tokenizers/CsharpTokenizer/CsharpTokenizer.sln` — register the new test project.
- **Modify:** `CLAUDE.md` — document the new test project and `dotnet test` command.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj` — new xUnit test project, `ProjectReference`s the main project.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CliRunner.cs` — shared helper: spawns the built `CsharpTokenizer.dll` as a child process, captures exit code/stdout/stderr. Used by every test that must run out-of-process to safely exercise crash scenarios.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/GzJsonl.cs` — shared helper: decompresses a `.jsonl.gz` file produced by the tool into a `List<string>` of lines, for assertions.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/SmokeTests.cs` — sanity-checks the test infra itself against a known-good input before any bug-proving tests are added.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/FaultIsolationTests.cs` — proves and fixes Finding #2.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/StackOverflowGuardTests.cs` — proves and fixes Finding #1.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/EncodingTests.cs` — proves and fixes Finding #4.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CliValidationTests.cs` — proves and fixes Finding #3.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/PathSafetyTests.cs` — proves and fixes Finding #6.
- **Create:** `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/ParallelismCorrectnessTests.cs` — regression test for Finding #5 (see Task 7's note on what this test can and cannot prove).

**Why this order:** Finding #1's fix throws a new (catchable) `NotSupportedException` instead of letting Roslyn overflow the stack — but that only becomes a *graceful skip* once the per-file `try`/`catch` from Finding #2's fix already exists. So Finding #2 is fixed **before** Finding #1. Finding #4's fix (strict UTF-8 decoding) reuses the same `try`/`catch`, so it comes right after. The remaining fixes (#3, #6, #5, #7) are independent and come last in order of simplicity.

---

### Task 1: Test project scaffold + shared helpers

**Files:**
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CliRunner.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/GzJsonl.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/SmokeTests.cs`
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer.sln`

- [ ] **Step 1: Generate the xUnit project**

Run:
```bash
cd /Users/borisshteynberg/near-duplicate-code-detector
dotnet new xunit -n CsharpTokenizer.Tests -o tokenizers/CsharpTokenizer/CsharpTokenizer.Tests
rm tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/UnitTest1.cs
```
Expected: creates `CsharpTokenizer.Tests.csproj` plus template files; `UnitTest1.cs` is removed since every test file in this plan is hand-written.

- [ ] **Step 2: Check the generated TargetFramework and reference the main project**

```bash
grep TargetFramework tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj
```
Expected: `<TargetFramework>net9.0</TargetFramework>` (the installed SDK is 9.0.300 and no `global.json` pins an older one). If it shows anything else, edit the csproj to set it to `net9.0` to match the sibling projects.

```bash
dotnet add tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj reference tokenizers/CsharpTokenizer/CsharpTokenizer/CsharpTokenizer.csproj
dotnet sln tokenizers/CsharpTokenizer/CsharpTokenizer.sln add tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj
```
Expected: both commands print a confirmation (`Reference ... added to the project.` / `Project ... added to the solution.`).

- [ ] **Step 3: Allow the test project to see internal members of the main project**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace CsharpTokenizer
{
    class Program
    {
```
New:
```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("CsharpTokenizer.Tests")]

namespace CsharpTokenizer
{
    class Program
    {
```

This is the only change to `Program.cs` in this task — no behavior change yet. Later tasks will add `internal` methods (for direct unit testing) that need this.

- [ ] **Step 4: Write the out-of-process runner helper**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CliRunner.cs`:
```csharp
using System;
using System.Diagnostics;
using System.IO;

namespace CsharpTokenizer.Tests
{
    internal static class CliRunner
    {
        public static CliResult Run(string projectsDir, string outputDir, string onlyIdentifiersArg)
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "CsharpTokenizer.dll");

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(dllPath);
            psi.ArgumentList.Add(projectsDir);
            psi.ArgumentList.Add(outputDir);
            psi.ArgumentList.Add(onlyIdentifiersArg);

            using var process = Process.Start(psi)!;
            string stdOut = process.StandardOutput.ReadToEnd();
            string stdErr = process.StandardError.ReadToEnd();
            bool exited = process.WaitForExit(60000);
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("CsharpTokenizer process did not exit within 60 seconds.");
            }

            return new CliResult(process.ExitCode, stdOut, stdErr);
        }
    }

    internal sealed class CliResult
    {
        public CliResult(int exitCode, string stdOut, string stdErr)
        {
            ExitCode = exitCode;
            StdOut = stdOut;
            StdErr = stdErr;
        }

        public int ExitCode { get; }
        public string StdOut { get; }
        public string StdErr { get; }
    }
}
```

Why `AppContext.BaseDirectory`: when the test project `ProjectReference`s `CsharpTokenizer.csproj`, MSBuild copies `CsharpTokenizer.dll` (plus its `.runtimeconfig.json`/`.deps.json`) into the test project's own output directory, so it's always sitting right next to the test assembly at run time.

Why `ArgumentList` instead of a concatenated command-line string: it passes each argument as its own OS-level argv entry, with no shell quoting involved — avoiding exactly the kind of argument-handling bug this review is about.

- [ ] **Step 5: Write the gzip/JSONL reader helper**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/GzJsonl.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CsharpTokenizer.Tests
{
    internal static class GzJsonl
    {
        public static List<string> ReadLines(string gzPath)
        {
            var lines = new List<string>();
            using var fileStream = File.OpenRead(gzPath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream);

            string line = reader.ReadLine();
            while (line != null)
            {
                lines.Add(line);
                line = reader.ReadLine();
            }
            return lines;
        }
    }
}
```

- [ ] **Step 6: Write a smoke test to validate the infra itself**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/SmokeTests.cs`:
```csharp
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
```

- [ ] **Step 7: Run the suite**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj
```
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`.

- [ ] **Step 8: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer.Tests tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs tokenizers/CsharpTokenizer/CsharpTokenizer.sln
git commit -m "test: scaffold CsharpTokenizer.Tests project with out-of-process runner"
```

---

### Task 2: Per-file fault isolation (fixes Finding #2)

**Files:**
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/FaultIsolationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/FaultIsolationTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails (proves Finding #2)**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~FaultIsolationTests
```
Expected: FAIL. `Assert.Equal(0, result.ExitCode)` fails with something like `Expected: 0, Actual: 134` — the child process is currently crashing with an unhandled `AggregateException`, exactly as reproduced manually during the review.

- [ ] **Step 3: Implement the fix**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
        public static void ExtractForProjectFolder(string projectDir, string outputDir, bool onlyIdentifiers, string baseDir)
        {
            var allFiles = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories);

            var projectDirName = Path.GetFileName(projectDir);

            using (var fileStream = File.Create(Path.Combine(outputDir, projectDirName + ".jsonl.gz")))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress, false))
            using (var textStream = new StreamWriter(gzipStream))
            {
                foreach (var fileJson in allFiles.AsParallel().Select(f => GetJsonForFile(f, onlyIdentifiers, baseDir)))
                {
                    textStream.WriteLine(fileJson);
                }
            }
        }

        private static string GetJsonForFile(string filepath, bool onlyIdentifiers, string baseDir)
        {
            var tokens = GetFileIdentifierTokens(filepath, onlyIdentifiers);
            Debug.Assert(filepath.StartsWith(baseDir));
            var relativePath = Path.GetRelativePath(baseDir, filepath);
            var tokenData = new TokenData()
            {
                tokens = tokens.ToArray(),
                filename = relativePath
            };

            return JsonConvert.SerializeObject(tokenData);
        }
```
New:
```csharp
        public static void ExtractForProjectFolder(string projectDir, string outputDir, bool onlyIdentifiers, string baseDir)
        {
            var allFiles = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories);

            var projectDirName = Path.GetFileName(projectDir);

            using (var fileStream = File.Create(Path.Combine(outputDir, projectDirName + ".jsonl.gz")))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress, false))
            using (var textStream = new StreamWriter(gzipStream))
            {
                foreach (var filepath in allFiles)
                {
                    var fileJson = GetJsonForFile(filepath, onlyIdentifiers, baseDir);
                    if (fileJson != null)
                    {
                        textStream.WriteLine(fileJson);
                    }
                }
            }
        }

        private static string GetJsonForFile(string filepath, bool onlyIdentifiers, string baseDir)
        {
            try
            {
                var tokens = GetFileIdentifierTokens(filepath, onlyIdentifiers);
                Debug.Assert(filepath.StartsWith(baseDir));
                var relativePath = Path.GetRelativePath(baseDir, filepath);
                var tokenData = new TokenData()
                {
                    tokens = tokens.ToArray(),
                    filename = relativePath
                };

                return JsonConvert.SerializeObject(tokenData);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: skipping {filepath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
```

Two things changed: (1) `.AsParallel().Select(...)` became a plain `foreach` — PLINQ's fail-fast semantics were the reason a single failing file discarded every already-computed result for the whole project, not just the one that failed; (2) the per-file work is now wrapped in a broad `catch`, because this is a trust boundary (arbitrary files from an external corpus) and the goal is "log and skip," not "let one bad file take down the batch." This single broad catch is intentionally reused as-is by Tasks 3 and 4 below — no further changes to this `catch` block are needed for those.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~FaultIsolationTests
```
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/FaultIsolationTests.cs
git commit -m "fix: isolate per-file tokenization failures instead of crashing the whole batch"
```

---

### Task 3: Stack-overflow guard (fixes Finding #1)

**Files:**
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/StackOverflowGuardTests.cs`

- [ ] **Step 1: Write the failing test(s)**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/StackOverflowGuardTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~StackOverflowGuardTests
```
Expected: build FAILS — `error CS0117: 'Program' does not contain a definition for 'ExceedsMaxNestingDepth'`. This is the expected "red" state for brand-new code. (If it somehow compiled, `PathologicallyNestedExpression_IsSkippedInsteadOfCrashingTheProcess` would separately fail with a nonzero exit code, proving Finding #1 — the review reproduced this directly as `Stack overflow.` inside Roslyn's lexer.)

- [ ] **Step 3: Implement the fix**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
        private static SyntaxTree GetASTFromFile(string filePath)
        {
            using (var rawSource = new StreamReader(filePath))
            {
                return CSharpSyntaxTree.ParseText(rawSource.ReadToEnd());
            }
        }
```
New:
```csharp
        internal const int MaxNestingDepth = 1000;

        private static SyntaxTree GetASTFromFile(string filePath)
        {
            using (var rawSource = new StreamReader(filePath))
            {
                var source = rawSource.ReadToEnd();
                if (ExceedsMaxNestingDepth(source, MaxNestingDepth))
                {
                    throw new NotSupportedException(
                        $"File exceeds the maximum supported nesting depth of {MaxNestingDepth} and was skipped to avoid a parser stack overflow.");
                }
                return CSharpSyntaxTree.ParseText(source);
            }
        }

        internal static bool ExceedsMaxNestingDepth(string source, int maxDepth)
        {
            int depth = 0;
            foreach (var c in source)
            {
                if (c == '(' || c == '{' || c == '[')
                {
                    depth++;
                    if (depth > maxDepth)
                    {
                        return true;
                    }
                }
                else if (c == ')' || c == '}' || c == ']')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                }
            }
            return false;
        }
```

`MaxNestingDepth` is set to 1000 — well below the ~20,000–60,000 depth that reliably overflowed the stack during the review, leaving margin for environments with smaller thread stacks (e.g. worker threads from the thread pool, which `Parallel.ForEach` uses, can have a smaller stack than the main thread). The guard is a cheap single linear scan over text already loaded into memory — no extra I/O. `NotSupportedException` is caught by Task 2's broad `catch`, turning this into a logged skip instead of a crash.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~StackOverflowGuardTests
```
Expected: `Passed! - Failed: 0, Passed: 5, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/StackOverflowGuardTests.cs
git commit -m "fix: guard against pathological nesting depth to avoid uncatchable parser stack overflow"
```

---

### Task 4: Strict UTF-8 decoding (fixes Finding #4)

**Files:**
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/EncodingTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/EncodingTests.cs`:
```csharp
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
                Assert.DoesNotContain(lines, l => l.Contains("\uFFFD"));
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
                Assert.DoesNotContain("\uFEFF", lines[0]);
            }
            finally
            {
                Directory.Delete(projectsDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
            }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~EncodingTests
```
Expected: `NonUtf8File_IsSkippedWithWarningInsteadOfSilentlyCorrupted` FAILS — today's lenient `StreamReader` decode never throws, so the file is silently included with a token containing `�`; `Assert.Empty(lines)` fails because `lines` actually has 1 entry. `Utf8FileWithBom_IsStillTokenizedCorrectly` is expected to PASS already (today's `StreamReader` already strips BOMs) — it's here as a regression guard for the fix in Step 3, which switches to manual byte decoding and must not reintroduce a BOM-handling bug.

- [ ] **Step 3: Implement the fix**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
        private static SyntaxTree GetASTFromFile(string filePath)
        {
            using (var rawSource = new StreamReader(filePath))
            {
                var source = rawSource.ReadToEnd();
                if (ExceedsMaxNestingDepth(source, MaxNestingDepth))
                {
                    throw new NotSupportedException(
                        $"File exceeds the maximum supported nesting depth of {MaxNestingDepth} and was skipped to avoid a parser stack overflow.");
                }
                return CSharpSyntaxTree.ParseText(source);
            }
        }
```
New:
```csharp
        private static SyntaxTree GetASTFromFile(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            int offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                offset = 3; // skip the UTF-8 BOM so it doesn't show up as a stray character in the token stream
            }

            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var source = strictUtf8.GetString(bytes, offset, bytes.Length - offset);

            if (ExceedsMaxNestingDepth(source, MaxNestingDepth))
            {
                throw new NotSupportedException(
                    $"File exceeds the maximum supported nesting depth of {MaxNestingDepth} and was skipped to avoid a parser stack overflow.");
            }
            return CSharpSyntaxTree.ParseText(source);
        }
```

Also add `using System.Text;` to the using list at the top of the file (needed for `UTF8Encoding`).

Switching from `StreamReader`'s lenient default decoding to a `UTF8Encoding` configured with `throwOnInvalidBytes: true` means any file with invalid UTF-8 byte sequences now throws `DecoderFallbackException` (already caught by Task 2's broad `catch`) instead of silently substituting U+FFFD. The manual BOM check is needed because `StreamReader` auto-strips a BOM but `UTF8Encoding.GetString` does not — without it, every BOM-prefixed file would gain a stray U+FEFF character at the start of its token stream.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~EncodingTests
```
Expected: `Passed! - Failed: 0, Passed: 2, Skipped: 0`.

- [ ] **Step 5: Run the full suite so far**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj
```
Expected: all tests from Tasks 1–4 pass (9 tests total).

- [ ] **Step 6: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/EncodingTests.cs
git commit -m "fix: reject invalid UTF-8 instead of silently replacing it, without breaking BOM-prefixed files"
```

---

### Task 5: CLI boolean-argument validation (fixes Finding #3)

**Files:**
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CliValidationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CliValidationTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~CliValidationTests
```
Expected: FAIL. `Assert.Equal(0, result.ExitCode)` fails because `bool.Parse("yes")` currently throws an unhandled `FormatException`, matching the review's direct reproduction.

- [ ] **Step 3: Implement the fix**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
        static void Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.WriteLine("Usage <projectsFolder> <outputFolder> true|false");
                return;
            }
            Parallel.ForEach(
                Directory.EnumerateDirectories(args[0]),
                d=>ExtractForProjectFolder(d, args[1], bool.Parse(args[2]), args[0])
            );
        }
```
New:
```csharp
        static void Main(string[] args)
        {
            if (args.Length != 3 || !bool.TryParse(args[2], out var onlyIdentifiers))
            {
                Console.WriteLine("Usage <projectsFolder> <outputFolder> true|false");
                return;
            }
            Parallel.ForEach(
                Directory.EnumerateDirectories(args[0]),
                d => ExtractForProjectFolder(d, args[1], onlyIdentifiers, args[0])
            );
        }
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~CliValidationTests
```
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CliValidationTests.cs
git commit -m "fix: validate the onlyIdentifiers CLI argument instead of crashing on bool.Parse"
```

---

### Task 6: Replace Debug.Assert with a real, always-on check (fixes Finding #6)

**Files:**
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/PathSafetyTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/PathSafetyTests.cs`:
```csharp
using System;
using System.IO;
using CsharpTokenizer;
using Xunit;

namespace CsharpTokenizer.Tests
{
    public class PathSafetyTests
    {
        [Fact]
        public void ThrowsWhenFileIsNotUnderBaseDirectory()
        {
            Assert.Throws<InvalidOperationException>(() =>
                Program.GetRelativePathSafe("/tmp/other/File.cs", "/tmp/base"));
        }

        [Fact]
        public void ReturnsRelativePathWhenFileIsUnderBaseDirectory()
        {
            var result = Program.GetRelativePathSafe("/tmp/base/sub/File.cs", "/tmp/base");
            Assert.Equal(Path.Combine("sub", "File.cs"), result);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~PathSafetyTests
```
Expected: build FAILS — `error CS0117: 'Program' does not contain a definition for 'GetRelativePathSafe'`. This is the expected "red" state for a brand-new method; `Debug.Assert` today has no independently-callable unit to test at all, which is itself part of the problem being fixed — it's an inline check with no observable contract, silently compiled out in Release builds.

- [ ] **Step 3: Implement the fix**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
        private static string GetJsonForFile(string filepath, bool onlyIdentifiers, string baseDir)
        {
            try
            {
                var tokens = GetFileIdentifierTokens(filepath, onlyIdentifiers);
                Debug.Assert(filepath.StartsWith(baseDir));
                var relativePath = Path.GetRelativePath(baseDir, filepath);
                var tokenData = new TokenData()
                {
                    tokens = tokens.ToArray(),
                    filename = relativePath
                };

                return JsonConvert.SerializeObject(tokenData);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: skipping {filepath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
```
New:
```csharp
        private static string GetJsonForFile(string filepath, bool onlyIdentifiers, string baseDir)
        {
            try
            {
                var tokens = GetFileIdentifierTokens(filepath, onlyIdentifiers);
                var relativePath = GetRelativePathSafe(filepath, baseDir);
                var tokenData = new TokenData()
                {
                    tokens = tokens.ToArray(),
                    filename = relativePath
                };

                return JsonConvert.SerializeObject(tokenData);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: skipping {filepath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        internal static string GetRelativePathSafe(string filepath, string baseDir)
        {
            if (!filepath.StartsWith(baseDir, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"File '{filepath}' is not under base directory '{baseDir}'.");
            }
            return Path.GetRelativePath(baseDir, filepath);
        }
```

Also remove the now-unused `using System.Diagnostics;` line from the top of the file (nothing else in the file uses that namespace).

Because this is a plain `if`/`throw`, it runs in every build configuration — Debug or Release — unlike `Debug.Assert`, which the SDK's default build configuration compiles out entirely whenever the `DEBUG` symbol isn't defined (i.e. any Release build).

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~PathSafetyTests
```
Expected: `Passed! - Failed: 0, Passed: 2, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/PathSafetyTests.cs
git commit -m "fix: replace Debug.Assert (compiled out in Release) with an always-on path safety check"
```

---

### Task 7: Bound the outer degree of parallelism (addresses Finding #5)

**Files:**
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`
- Create: `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/ParallelismCorrectnessTests.cs`

**Honesty note before starting:** Task 2 already removed the *inner* `.AsParallel()` over files within a project, which was the other half of the nested-parallelism problem. This task only needs to bound the *outer* `Parallel.ForEach` over project directories. Thread-pool oversubscription is a performance characteristic, not a correctness property — there is no reliable, non-flaky way to unit-test "did this avoid oversubscription." The test below instead proves the refactor preserves **correctness at scale** (every file still gets tokenized exactly once, nothing hangs). It is expected to **pass both before and after** this task's change; it is a regression guard, not a red/green proof of the finding.

- [ ] **Step 1: Write the regression test**

Create `tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/ParallelismCorrectnessTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run the test — confirm it already passes**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~ParallelismCorrectnessTests
```
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`. (As noted above, this is expected to pass even before Step 3 — it's establishing a correctness baseline the upcoming refactor must not break.)

- [ ] **Step 3: Implement the fix**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
        static void Main(string[] args)
        {
            if (args.Length != 3 || !bool.TryParse(args[2], out var onlyIdentifiers))
            {
                Console.WriteLine("Usage <projectsFolder> <outputFolder> true|false");
                return;
            }
            Parallel.ForEach(
                Directory.EnumerateDirectories(args[0]),
                d => ExtractForProjectFolder(d, args[1], onlyIdentifiers, args[0])
            );
        }
```
New:
```csharp
        static void Main(string[] args)
        {
            if (args.Length != 3 || !bool.TryParse(args[2], out var onlyIdentifiers))
            {
                Console.WriteLine("Usage <projectsFolder> <outputFolder> true|false");
                return;
            }
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.ForEach(
                Directory.EnumerateDirectories(args[0]),
                options,
                d => ExtractForProjectFolder(d, args[1], onlyIdentifiers, args[0])
            );
        }
```

- [ ] **Step 4: Run the test to confirm it still passes**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj --filter FullyQualifiedName~ParallelismCorrectnessTests
```
Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`.

- [ ] **Step 5: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/ParallelismCorrectnessTests.cs
git commit -m "fix: bound outer Parallel.ForEach degree of parallelism now that inner AsParallel is gone"
```

---

### Task 8: Remove dead code (fixes Finding #7)

**Files:**
- Modify: `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`

- [ ] **Step 1: Delete the unused method**

Modify `tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs`:

Old:
```csharp
        private static IEnumerable<SyntaxToken> GetFileTokens(string filepath) =>
           GetASTFromFile(filepath).GetRoot().DescendantTokens();

```
New: *(delete these lines entirely — nothing replaces them)*

- [ ] **Step 2: Verify the project still builds and the full suite still passes**

```bash
dotnet build tokenizers/CsharpTokenizer/CsharpTokenizer/CsharpTokenizer.csproj
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj
```
Expected: build succeeds with 0 errors; all tests from Tasks 1–7 still pass (14 tests total: 1 smoke + 1 fault-isolation + 5 stack-overflow + 2 encoding + 1 CLI + 2 path-safety + 1 parallelism + 1 BOM).

- [ ] **Step 3: Commit**

```bash
git add tokenizers/CsharpTokenizer/CsharpTokenizer/Program.cs
git commit -m "chore: remove unused GetFileTokens (dead code, diverged from GetFileIdentifierTokens)"
```

---

### Task 9: Final verification and documentation

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Run the full test suite one more time**

```bash
dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj
```
Expected: `Passed! - Failed: 0, Passed: 14, Skipped: 0`.

- [ ] **Step 2: Re-run the original manual repros from the review to confirm they no longer crash**

```bash
cd /Users/borisshteynberg/near-duplicate-code-detector
dotnet build tokenizers/CsharpTokenizer/CsharpTokenizer/CsharpTokenizer.csproj -c Release

WORK=/tmp/csharp-tokenizer-final-check
rm -rf "$WORK" && mkdir -p "$WORK/projects/proj1" "$WORK/out"
printf 'namespace Demo { class A1 { void M() { int alpha = 1; } } }\n' > "$WORK/projects/proj1/Good1.cs"
printf 'namespace Demo { class A2 { void M() { int beta = 2; } } }\n' > "$WORK/projects/proj1/Good2.cs"
printf 'namespace Demo { class Bad { void M() { int gamma = 3; } } }\n' > "$WORK/projects/proj1/Zzz_Bad.cs"
chmod 000 "$WORK/projects/proj1/Zzz_Bad.cs"

dotnet run -c Release --project tokenizers/CsharpTokenizer/CsharpTokenizer/CsharpTokenizer.csproj -- "$WORK/projects" "$WORK/out" true
echo "EXIT: $?"
gzip -t "$WORK/out/proj1.jsonl.gz" && echo "gzip OK"
chmod 644 "$WORK/projects/proj1/Zzz_Bad.cs"
```
Expected: `EXIT: 0`, `gzip OK` printed, and a stderr warning naming `Zzz_Bad.cs` — contrast with the original review, where this exact scenario crashed with exit code 134 and produced a 0-byte, invalid gzip file. This also exercises the Release build configuration specifically, confirming Task 6's fix (the removed `Debug.Assert`) behaves identically in Release and Debug.

- [ ] **Step 3: Update CLAUDE.md**

Modify `CLAUDE.md`: in the "Tokenizers (`tokenizers/`)" section, find the line:
```
- **C#** (`CsharpTokenizer/`): `dotnet run --project tokenizers/CsharpTokenizer/CsharpTokenizer/CsharpTokenizer.csproj -- <projectsFolder> <outputFolder> true|false` — uses Roslyn (`Microsoft.CodeAnalysis.CSharp`) to tokenize each `.cs` file under each immediate subdirectory of `<projectsFolder>` (each subdirectory = one project → one output `.jsonl.gz`).
```
Replace it with:
```
- **C#** (`CsharpTokenizer/`): `dotnet run --project tokenizers/CsharpTokenizer/CsharpTokenizer/CsharpTokenizer.csproj -- <projectsFolder> <outputFolder> true|false` — uses Roslyn (`Microsoft.CodeAnalysis.CSharp`) to tokenize each `.cs` file under each immediate subdirectory of `<projectsFolder>` (each subdirectory = one project → one output `.jsonl.gz`). Per-file failures (unreadable files, invalid UTF-8, pathologically nested expressions) are logged to stderr and skipped rather than aborting the whole run. Tests: `dotnet test tokenizers/CsharpTokenizer/CsharpTokenizer.Tests/CsharpTokenizer.Tests.csproj` (runs the tool out-of-process per test, since some cases exercise crash/exit-code behavior that can't be safely triggered in-process).
```

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document CsharpTokenizer's fault-isolation behavior and test project"
```
