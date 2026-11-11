namespace DependencyAgent.Fixing.Tests;

public class TestTamperingDetectorTests
{
    private const string File = "tests/Foo.Tests/FooTests.cs";

    [Theory]
    [InlineData("tests/Foo.Tests/A.cs", true)]
    [InlineData("src/FooTests.cs", true)]
    [InlineData("src/UnitTest1.cs", true)]
    [InlineData("TESTS/x.cs", true)]
    [InlineData("src/Foo/Bar.cs", false)]
    public void IsTestFile(string path, bool expected) => Assert.Equal(expected, TestTamperingDetector.IsTestFile(path));

    [Theory]
    [InlineData("Fact")]
    [InlineData("Theory")]
    [InlineData("Test")]
    [InlineData("TestMethod")]
    [InlineData("TestCase(1)")]
    public void RemovedTestAttribute_IsFlagged(string attribute)
    {
        var diff = $"@@ -1,3 +1,2 @@\n-    [{attribute}]\n     public void M() {{ }}\n";
        var findings = TestTamperingDetector.Analyze(File, diff);
        Assert.Contains(findings, f => f.Reason.Contains("test attribute"));
    }

    [Fact]
    public void AttributeMovedOrReplaced_IsNotFlagged()
    {
        var diff = "-    [Fact]\n+    [Fact(DisplayName = \"x\")]\n";
        Assert.Empty(TestTamperingDetector.Analyze(File, diff));
    }

    [Theory]
    [InlineData("+    [Fact(Skip = \"flaky\")]")]
    [InlineData("+    [Ignore]")]
    [InlineData("+    [Skip]")]
    [InlineData("+        Assert.Skip(\"nope\");")]
    [InlineData("+    [Theory(Skip=\"x\")]")]
    public void AddedSkip_IsFlagged(string added)
    {
        var findings = TestTamperingDetector.Analyze(File, added + "\n");
        Assert.Contains(findings, f => f.Reason.Contains("skip"));
    }

    [Fact]
    public void RemovedAssertion_WithoutReplacement_IsFlagged()
    {
        var diff = "-        Assert.Equal(1, x);\n-        result.Should().Be(2);\n+        var y = 3;\n";
        var findings = TestTamperingDetector.Analyze(File, diff);
        Assert.Contains(findings, f => f.Reason.Contains("assertion"));
    }

    [Fact]
    public void ReplacedAssertion_IsNotFlagged()
    {
        var diff = "-        Assert.Equal(1, x);\n+        Assert.Equal(1, Convert.ToInt32(x));\n";
        Assert.Empty(TestTamperingDetector.Analyze(File, diff));
    }

    [Fact]
    public void FileHeaders_AreIgnored()
    {
        var diff = "--- a/tests/FooTests.cs\n+++ b/tests/FooTests.cs\n@@ -1 +1 @@\n-var a = 1;\n+var a = 2;\n";
        Assert.Empty(TestTamperingDetector.Analyze(File, diff));
    }

    [Fact]
    public void NonTestFile_IsNeverFlagged()
    {
        Assert.Empty(TestTamperingDetector.Analyze("src/Foo.cs", "-    [Fact]\n-    Assert.True(x);\n+    [Ignore]\n"));
    }

    [Fact]
    public void NewTestFileWithSkip_IsFlagged()
    {
        var findings = TestTamperingDetector.AnalyzeNewFile(File, "public class T\n{\n    [Fact(Skip = \"x\")]\n    public void M() {}\n}\n");
        Assert.NotEmpty(findings);
    }

    [Fact]
    public void Suppressions_AreFoundInAnyFile()
    {
        var diff = "+#pragma warning disable CS0618\n+    <NoWarn>$(NoWarn);CS1591</NoWarn>\n+[SuppressMessage(\"a\", \"b\")]\n+<TreatWarningsAsErrors>false</TreatWarningsAsErrors>\n+var ok = 1;\n-#pragma warning disable CS0001\n";
        var found = TestTamperingDetector.FindAddedSuppressions("src/Foo.cs", diff);
        Assert.Equal(4, found.Count);
        Assert.All(found, f => Assert.StartsWith("src/Foo.cs: ", f));
    }
}
