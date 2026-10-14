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
