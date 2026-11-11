using DependencyAgent.Core.Model;
using DependencyAgent.Verification;

namespace DependencyAgent.Verification.Tests;

public class TestResultParserTests
{
    private const string Trx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <TestRun id="1" name="x" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult executionId="a" testId="1" testName="Ns.Tests.Passes" outcome="Passed" />
            <UnitTestResult executionId="b" testId="2" testName="Ns.Tests.Fails" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Equal() Failure
        Expected: 1
        Actual:   2</Message>
                  <StackTrace>   at Ns.Tests.Fails() in /src/Tests.cs:line 10
           at System.RuntimeMethodHandle.InvokeMethod()</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult executionId="c" testId="3" testName="Ns.Tests.Skipped" outcome="NotExecuted" />
          </Results>
          <ResultSummary outcome="Failed">
            <Counters total="3" executed="2" passed="1" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="1" />
          </ResultSummary>
        </TestRun>
        """;

    [Fact]
    public void Trx_TotalsAndFailedTests()
    {
        var s = TestResultParser.ParseTrx([Trx])!;
        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Passed);
        Assert.Equal(1, s.Failed);
        Assert.Equal(1, s.Skipped);
        var f = Assert.Single(s.FailedTests);
        Assert.Equal("Ns.Tests.Fails", f.Name);
        Assert.Contains("Expected: 1", f.Message);
        Assert.Contains("Tests.cs:line 10", f.Message);
    }

    [Fact]
    public void Trx_MultipleFilesAreMerged_AndInvalidOnesIgnored()
    {
        var s = TestResultParser.ParseTrx([Trx, Trx, "not xml"])!;
        Assert.Equal(6, s.Total);
        Assert.Equal(2, s.Failed);
    }

    [Fact]
    public void Trx_NoValidDocuments_ReturnsNull() => Assert.Null(TestResultParser.ParseTrx(["garbage"]));

    [Fact]
    public void Trx_FailedMessageIsCapped()
    {
        var longMessage = string.Join("\n", Enumerable.Range(0, 100).Select(i => "line " + i + new string('x', 100)));
        var trx = $"""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="T" outcome="Failed"><Output><ErrorInfo><Message>{longMessage}</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>""";
        var f = Assert.Single(TestResultParser.ParseTrx([trx])!.FailedTests);
        Assert.True(f.Message!.Length <= 2100);
        Assert.DoesNotContain("line 50", f.Message);
    }

    [Fact]
    public void Trx_FailedTestListIsCapped()
    {
        var results = string.Concat(Enumerable.Range(0, 250).Select(i => $"""<UnitTestResult testName="T{i}" outcome="Failed" />"""));
        var s = TestResultParser.ParseTrx([$"""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results></TestRun>"""])!;
        Assert.Equal(250, s.Failed);
        Assert.Equal(TestResultParser.MaxFailedTests, s.FailedTests.Count);
    }

    [Fact]
    public void Trx_DataDrivenParentIsNotDoubleCounted()
    {
        var trx = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
              <UnitTestResult testName="P" outcome="Failed" resultType="DataDrivenTest"><InnerResults>
                <UnitTestResult testName="P (1)" outcome="Passed" />
                <UnitTestResult testName="P (2)" outcome="Failed" />
              </InnerResults></UnitTestResult>
            </Results></TestRun>
            """;
        var s = TestResultParser.ParseTrx([trx])!;
        Assert.Equal(2, s.Total);
        Assert.Equal(1, s.Failed);
    }

    [Fact]
    public void Console_VsTestSummary_FailedAndPassed()
    {
        var failed = TestResultParser.ParseConsole("Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 12 ms - A.dll (net10.0)")!;
        Assert.Equal((3, 2, 1, 0), (failed.Total, failed.Passed, failed.Failed, failed.Skipped));

        var passed = TestResultParser.ParseConsole("Passed!  - Failed:     0, Passed:     5, Skipped:     1, Total:     6, Duration: 1 s - A.dll (net10.0)")!;
        Assert.Equal((6, 5, 0, 1), (passed.Total, passed.Passed, passed.Failed, passed.Skipped));
    }

    [Fact]
    public void Console_MultipleAssembliesAreSummed_AndFailedNamesCaptured()
    {
        var output = """
              Failed Ns.Tests.Fails [5 ms]
            Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 12 ms - A.dll (net10.0)
            Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 12 ms - B.dll (net10.0)
            """;
        var s = TestResultParser.ParseConsole(output)!;
        Assert.Equal(7, s.Total);
        Assert.Equal(1, s.Failed);
        Assert.Equal("Ns.Tests.Fails", Assert.Single(s.FailedTests).Name);
    }

    [Fact]
    public void Console_MtpRunSummaryBlock()
    {
        var output = """
            Test run summary: Failed!
              total: 4
              failed: 1
              succeeded: 2
              skipped: 1
              duration: 1s 214ms
            """;
        var s = TestResultParser.ParseConsole(output)!;
        Assert.Equal((4, 2, 1, 1), (s.Total, s.Passed, s.Failed, s.Skipped));
    }

    [Fact]
    public void Console_MtpAggregateWinsOverBlocks()
    {
        var output = """
            Test run summary: Passed!
              total: 1
              failed: 0
              succeeded: 1
              skipped: 0
            Test summary: total: 5, failed: 0, succeeded: 5, skipped: 0, duration: 2s
            """;
        Assert.Equal(5, TestResultParser.ParseConsole(output)!.Total);
    }

    [Fact]
    public void Console_NoSummary_ReturnsNull() => Assert.Null(TestResultParser.ParseConsole("nothing to see"));
}

public class WarningBaselineTests
{
    private static BuildDiagnostic W(string code, string file = "A.cs", string message = "m", int line = 1, DiagnosticSeverity sev = DiagnosticSeverity.Warning) =>
        new(sev, code, message, file, line, 1, "A.csproj");

    [Fact]
    public void Empty_WhenIdentical() => Assert.Empty(WarningBaseline.NewWarnings([W("CS1")], [W("CS1")]));

    [Fact]
    public void LineNumbersAreIgnored() => Assert.Empty(WarningBaseline.NewWarnings([W("CS1", line: 3)], [W("CS1", line: 300)]));

    [Fact]
    public void NewCodeFileOrMessage_IsReported()
    {
        var added = W("CS2");
        var result = WarningBaseline.NewWarnings([W("CS1")], [W("CS1"), added, W("CS1", file: "B.cs"), W("CS1", message: "other")]);
        Assert.Equal(3, result.Count);
        Assert.Contains(added, result);
    }

    [Fact]
    public void Multiset_ExtraOccurrenceIsNew()
    {
        var result = WarningBaseline.NewWarnings([W("CS1", line: 1), W("CS1", line: 2)], [W("CS1", line: 5), W("CS1", line: 6), W("CS1", line: 7)]);
        Assert.Single(result);
    }

    [Fact]
    public void Multiset_FewerOccurrencesNotReported() =>
        Assert.Empty(WarningBaseline.NewWarnings([W("CS1"), W("CS1")], [W("CS1")]));

    [Fact]
    public void ErrorsAreIgnoredOnBothSides()
    {
        var result = WarningBaseline.NewWarnings([W("CS1", sev: DiagnosticSeverity.Error)], [W("CS1"), W("CS9", sev: DiagnosticSeverity.Error)]);
        Assert.Equal("CS1", Assert.Single(result).Code);
    }

    [Fact]
    public void EmptyBaseline_ReportsAllCurrentWarnings() => Assert.Equal(2, WarningBaseline.NewWarnings([], [W("CS1"), W("CS2")]).Count);
}
