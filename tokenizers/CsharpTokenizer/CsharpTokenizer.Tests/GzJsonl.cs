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
