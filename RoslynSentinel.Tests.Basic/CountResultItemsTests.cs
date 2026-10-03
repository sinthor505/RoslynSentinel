using System.Text.Json.Nodes;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class CountResultItemsTests
{
    [TestCase("[1,2,3]", 3, TestName = "TopLevelArray_CountsElements")]
    [TestCase("""{ "results": [1,2,3], "failures": [4,5] }""", 5, TestName = "SiblingArrays_AreSummed")]
    [TestCase("""{ "a": { "b": [1,2] }, "c": [3] }""", 3, TestName = "ArraysInNestedObjects_AreCounted")]
    [TestCase("""{ "rows": [ { "items": [1,2,3] }, { "items": [4] } ] }""", 2, TestName = "InnerArraysOfArrayElements_AreNotDescendedInto")]
    [TestCase("""{ "results": [] }""", 0, TestName = "EmptyArray_CountsZeroNotNull")]
    public void CountResultItems_ReturnsExpectedTotal(string json, int expected)
    {
        Assert.That(LargeResultHelper.CountResultItems(JsonNode.Parse(json)), Is.EqualTo(expected));
    }

    [TestCase("""{ "a": 1, "b": { "c": "x" } }""", TestName = "ObjectWithoutArrays_ReturnsNull")]
    [TestCase("42", TestName = "Scalar_ReturnsNull")]
    [TestCase(null, TestName = "NullNode_ReturnsNull")]
    public void CountResultItems_NoArrays_ReturnsNull(string? json)
    {
        Assert.That(LargeResultHelper.CountResultItems(json is null ? null : JsonNode.Parse(json)), Is.Null);
    }
}
