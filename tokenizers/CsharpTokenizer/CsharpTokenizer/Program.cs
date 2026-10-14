using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("CsharpTokenizer.Tests")]

namespace CsharpTokenizer
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 3 || !bool.TryParse(args[2], out var onlyIdentifiers))
            {
                Console.WriteLine("Usage <projectsFolder> <outputFolder> true|false");
                return;
            }
            // The per-file loop inside ExtractForProjectFolder is sequential (see GetJsonForFile),
            // so this is the only parallel region left to bound.
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.ForEach(
                Directory.EnumerateDirectories(args[0]),
                options,
                d => ExtractForProjectFolder(d, args[1], onlyIdentifiers, args[0])
            );
        }

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
                var relativePath = GetRelativePathSafe(filepath, baseDir);
                var tokenData = new TokenData()
                {
                    tokens = tokens.ToArray(),
                    filename = relativePath
                };

                return JsonConvert.SerializeObject(tokenData);
            }
            // Intentionally broad: filepath is a trust boundary (unreadable/permission-denied files,
            // invalid UTF-8, and pathologically nested input that would overflow the parser's stack
            // all surface as different exception types here) — log and skip rather than abort the batch.
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

        private static IEnumerable<string> GetFileIdentifierTokens(string filepath, bool onlyIdentifiers)
        {
            var tokens = GetASTFromFile(filepath).GetRoot().DescendantTokens();
            if (onlyIdentifiers) {
                tokens = tokens.Where(t => t.IsKind(SyntaxKind.IdentifierToken));
            }
            return tokens.Select(t => t.Text);
        }

        internal const int MaxNestingDepth = 1000;

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
    }

    public struct TokenData
    {
        public string filename;
        public string[] tokens;
    }
}
