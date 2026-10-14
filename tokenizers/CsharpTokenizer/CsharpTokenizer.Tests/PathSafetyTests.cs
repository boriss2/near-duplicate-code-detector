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
