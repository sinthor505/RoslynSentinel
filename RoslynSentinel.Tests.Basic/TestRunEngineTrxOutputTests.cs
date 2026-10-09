using System.Xml.Linq;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class TestRunEngineTrxOutputTests
{
    private static string BuildTrxWithTest(
        string testName,
        string outcome,
        string? stdOut = null,
        string? errorMessage = null,
        string? errorStackTrace = null)
    {
        var ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var output = new XElement(XName.Get("Output", ns));
        if (stdOut != null)
        {
            output.Add(new XElement(XName.Get("StdOut", ns), stdOut));
        }
        if (errorMessage != null || errorStackTrace != null)
        {
            var errorInfo = new XElement(XName.Get("ErrorInfo", ns));
            if (errorMessage != null)
            {
                errorInfo.Add(new XElement(XName.Get("Message", ns), errorMessage));
            }
            if (errorStackTrace != null)
            {
                errorInfo.Add(new XElement(XName.Get("StackTrace", ns), errorStackTrace));
            }
            output.Add(errorInfo);
        }

        var testResult = new XElement(
            XName.Get("UnitTestResult", ns),
            new XAttribute("testName", testName),
            new XAttribute("outcome", outcome),
            new XAttribute("duration", "00:00:01"),
            output);

        var doc = new XDocument(
            new XElement(
                XName.Get("TestRun", ns),
                new XElement(XName.Get("Results", ns), testResult)));

        return doc.ToString();
    }

    [Test]
    public void ParseTrx_FailedTestWithStdOut_CarriesOutput()
    {
        var trxContent = BuildTrxWithTest("TestA", "Failed", stdOut: "test output\nmarker-123");
        var trxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".trx");

        try
        {
            File.WriteAllText(trxPath, trxContent);
            var results = TestRunEngine.ParseTrx(trxPath);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Outcome, Is.EqualTo(TestOutcome.Failed));
            Assert.That(results[0].Output, Is.Not.Null.And.Contains("marker-123"));
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Test]
    public void ParseTrx_PassedTestWithStdOut_OutputIsNull()
    {
        var trxContent = BuildTrxWithTest("TestB", "Passed", stdOut: "some output");
        var trxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".trx");

        try
        {
            File.WriteAllText(trxPath, trxContent);
            var results = TestRunEngine.ParseTrx(trxPath);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Outcome, Is.EqualTo(TestOutcome.Passed));
            Assert.That(results[0].Output, Is.Null);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Test]
    public void ParseTrx_FailedTestWithoutStdOut_OutputIsNull()
    {
        var trxContent = BuildTrxWithTest("TestC", "Failed", stdOut: null, errorMessage: "boom");
        var trxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".trx");

        try
        {
            File.WriteAllText(trxPath, trxContent);
            var results = TestRunEngine.ParseTrx(trxPath);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Outcome, Is.EqualTo(TestOutcome.Failed));
            Assert.That(results[0].Output, Is.Null);
            Assert.That(results[0].ErrorMessage, Is.EqualTo("boom"));
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Test]
    public void ParseTrx_FailedTestWithWhitespaceOnlyStdOut_OutputIsNull()
    {
        var trxContent = BuildTrxWithTest("TestD", "Failed", stdOut: "   \n\t  ");
        var trxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".trx");

        try
        {
            File.WriteAllText(trxPath, trxContent);
            var results = TestRunEngine.ParseTrx(trxPath);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Outcome, Is.EqualTo(TestOutcome.Failed));
            Assert.That(results[0].Output, Is.Null);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Test]
    public void ParseTrx_FailedTestWithLargeStdOut_IsCappedAndPrefixed()
    {
        var largeOutput = new string('x', 5000);
        var trxContent = BuildTrxWithTest("TestE", "Failed", stdOut: largeOutput);
        var trxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".trx");

        try
        {
            File.WriteAllText(trxPath, trxContent);
            var results = TestRunEngine.ParseTrx(trxPath);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Outcome, Is.EqualTo(TestOutcome.Failed));
            Assert.That(results[0].Output, Is.Not.Null);
            Assert.That(results[0].Output, Does.StartWith("[truncated] "));
            Assert.That(results[0].Output!.Length, Is.LessThanOrEqualTo("[truncated] ".Length + 2000));
            Assert.That(results[0].Output, Does.EndWith("xxx"));
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Test]
    public void ParseTrx_ErrorMessageAndStackTraceStillPopulated_AlongsideOutput()
    {
        var trxContent = BuildTrxWithTest(
            "TestF",
            "Failed",
            stdOut: "captured output",
            errorMessage: "AssertionError",
            errorStackTrace: "at MyTest()");
        var trxPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".trx");

        try
        {
            File.WriteAllText(trxPath, trxContent);
            var results = TestRunEngine.ParseTrx(trxPath);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Output, Is.Not.Null.And.Contains("captured output"));
            Assert.That(results[0].ErrorMessage, Is.EqualTo("AssertionError"));
            Assert.That(results[0].ErrorStackTrace, Is.EqualTo("at MyTest()"));
        }
        finally
        {
            File.Delete(trxPath);
        }
    }
}
