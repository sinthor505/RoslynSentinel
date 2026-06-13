// Battery 30 — Regression tests for the 4 bugs found during real-solution smoke testing
// + additional confirmation tests for tools exercised against ExpressRecipe.
//
// Bug 1: add_validation_to_poco — duplicated attributes on already-annotated properties
// Bug 2: class_to_record — positional syntax stripping attributes and initializers
// Bug 3: convert_lock_to_semaphore_slim — instance field emitted for static-method contexts
// Bug 4: use_field_backed_properties — semantically inverted (expanded instead of collapsed)
//                                     + handler threw on empty result instead of returning gracefully

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("Battery30")]
public class B30_RegressionTests
{
    private PersistentWorkspaceManager _ws  = null!;
    private SentinelConfiguration      _cfg = null!;
    private ApiIntegrationEngine       _apiEngine = null!;
    private ModernizationEngine        _modEngine = null!;
    private ThreadSafetyEngine         _tsEngine  = null!;
    private SyntaxUpgradeEngine        _suEngine  = null!;

    [SetUp]
    public void Setup()
    {
        _ws        = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _cfg       = new SentinelConfiguration();
        _apiEngine = new ApiIntegrationEngine(_ws);
        _modEngine = new ModernizationEngine(_ws, _cfg);
        _tsEngine  = new ThreadSafetyEngine(_ws);
        _suEngine  = new SyntaxUpgradeEngine(_ws, _cfg);
    }

    [TearDown]
    public void TearDown() => _ws?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _ws.SetTestSolution(solution);
    }

    // =========================================================================
    // Bug 1: add_validation_to_poco — duplicate attributes
    // =========================================================================

    [Test]
    public async Task AddValidationToPoco_WhenPropertyHasNoAttributes_AddsRequired()
    {
        const string code = @"
public class Product {
    public string Name { get; set; }
}";
        SetSource(code, "Product.cs");
        var result = await _apiEngine.AddValidationToPocoAsync("Product.cs", "Product");

        // [Required] should be present exactly once
        var count = CountOccurrences(result.UpdatedText!, "[Required]");
        Assert.That(count, Is.EqualTo(1), "Expected exactly 1 [Required] attribute on Name");
    }

    [Test]
    public async Task AddValidationToPoco_WhenPropertyAlreadyHasRequired_DoesNotDuplicate()
    {
        // This is the regression test for Bug 1 — running the tool on a class that already
        // has [Required] on a string property must NOT emit a second [Required].
        const string code = @"
using System.ComponentModel.DataAnnotations;
public class Product {
    [Required]
    public string Name { get; set; }
    public string Description { get; set; }
}";
        SetSource(code, "Product.cs");
        var result = await _apiEngine.AddValidationToPocoAsync("Product.cs", "Product");

        // Name already had [Required] — count must still be 1
        var requiredCount = CountOccurrences(result.UpdatedText!, "[Required]");
        Assert.That(requiredCount, Is.EqualTo(2),
            "Description should get [Required] but Name should NOT get a duplicate — total must be 2");
    }

    [Test]
    public async Task AddValidationToPoco_WhenPropertyAlreadyHasStringLength_DoesNotDuplicateStringLength()
    {
        const string code = @"
using System.ComponentModel.DataAnnotations;
public class Order {
    [StringLength(100)]
    public string Code { get; set; }
}";
        SetSource(code, "Order.cs");
        var result = await _apiEngine.AddValidationToPocoAsync("Order.cs", "Order");

        var count = CountOccurrences(result.UpdatedText!, "StringLength");
        Assert.That(count, Is.EqualTo(1), "Should not duplicate [StringLength]");
    }

    [Test]
    public async Task AddValidationToPoco_WhenPropertyAlreadyHasRange_DoesNotDuplicateRange()
    {
        const string code = @"
using System.ComponentModel.DataAnnotations;
public class Measurement {
    [Range(0, 100)]
    public int Value { get; set; }
}";
        SetSource(code, "Measurement.cs");
        var result = await _apiEngine.AddValidationToPocoAsync("Measurement.cs", "Measurement");

        var count = CountOccurrences(result.UpdatedText!, "[Range(");
        Assert.That(count, Is.EqualTo(1), "Should not duplicate [Range]");
    }

    [Test]
    public async Task AddValidationToPoco_RunTwiceOnSameClass_IsIdempotent()
    {
        const string code = @"
public class Customer {
    public string Email { get; set; }
    public int Age { get; set; }
}";
        SetSource(code, "Customer.cs");
        var first  = await _apiEngine.AddValidationToPocoAsync("Customer.cs", "Customer");
        SetSource(first.UpdatedText!, "Customer.cs");
        var second = await _apiEngine.AddValidationToPocoAsync("Customer.cs", "Customer");

        // Running twice must produce the same result — no extra attributes appended
        var req1 = CountOccurrences(first.UpdatedText!,  "[Required]");
        var req2 = CountOccurrences(second.UpdatedText!, "[Required]");
        Assert.That(req2, Is.EqualTo(req1), "Second run must not add duplicate attributes");
    }

    // =========================================================================
    // Bug 2: class_to_record — positional syntax strips attributes / initializers
    // =========================================================================

    [Test]
    public async Task ClassToRecord_SimpleClassNoAttributes_UsesPositionalSyntax()
    {
        const string code = @"
public class Point {
    public int X { get; init; }
    public int Y { get; init; }
}";
        SetSource(code, "Point.cs");
        var result = await _modEngine.ClassToRecordAsync("Point.cs", "Point");

        Assert.That(result.UpdatedText!, Contains.Substring("record Point("), "Simple class should use positional syntax");
        Assert.That(result.UpdatedText!, Contains.Substring("int X"), "X parameter should be present");
        Assert.That(result.UpdatedText!, Contains.Substring("int Y"), "Y parameter should be present");
    }

    [Test]
    public async Task ClassToRecord_WhenClassHasAnnotatedProperties_PreservesAttributes()
    {
        // Regression test for Bug 2: positional parameters would silently strip [Required].
        const string code = @"
using System.ComponentModel.DataAnnotations;
public class Product {
    [Required]
    public string Name { get; set; }
    public decimal Price { get; set; }
}";
        SetSource(code, "Product.cs");
        var result = await _modEngine.ClassToRecordAsync("Product.cs", "Product");

        // Must preserve the [Required] attribute
        Assert.That(result.UpdatedText!, Contains.Substring("[Required]"), "Attribute [Required] must survive ClassToRecord");
        // Should be a class-body record, not positional
        Assert.That(result.UpdatedText!, Does.Not.Contain("record Product("), "Should NOT use positional syntax when attributes exist");
        Assert.That(result.UpdatedText!, Contains.Substring("record Product"), "Should still be a record");
    }

    [Test]
    public async Task ClassToRecord_WhenClassHasInitializedProperties_PreservesInitializers()
    {
        // Regression test for Bug 2: positional parameters strip initializers.
        const string code = @"
public class Config {
    public string Host { get; set; } = ""localhost"";
    public int Port { get; set; } = 8080;
}";
        SetSource(code, "Config.cs");
        var result = await _modEngine.ClassToRecordAsync("Config.cs", "Config");

        // Must preserve both initializers
        Assert.That(result.UpdatedText!, Contains.Substring("localhost"), "Default value 'localhost' must survive ClassToRecord");
        Assert.That(result.UpdatedText!, Contains.Substring("8080"),      "Default value 8080 must survive ClassToRecord");
        // Should be a class-body record, not positional
        Assert.That(result.UpdatedText!, Does.Not.Contain("record Config("), "Should NOT use positional syntax when initializers exist");
    }

    [Test]
    public async Task ClassToRecord_WhenClassHasAttributesAndInitializers_ProducesValidOutput()
    {
        const string code = @"
using System.ComponentModel.DataAnnotations;
public class Item {
    [Required]
    public string Code { get; set; } = string.Empty;
    [Range(0, 999)]
    public int Qty  { get; set; } = 1;
}";
        SetSource(code, "Item.cs");
        var result = await _modEngine.ClassToRecordAsync("Item.cs", "Item");

        Assert.That(result.UpdatedText!, Contains.Substring("[Required]"),   "Must preserve [Required]");
        Assert.That(result.UpdatedText!, Contains.Substring("[Range(0, 999)]"), "Must preserve [Range]");
        Assert.That(result.UpdatedText!, Contains.Substring("string.Empty"), "Must preserve string.Empty initializer");
        Assert.That(result.UpdatedText!, Contains.Substring("= 1"),          "Must preserve numeric initializer");
        Assert.That(result.UpdatedText!, Contains.Substring("record Item"),  "Must be a record");
    }

    [Test]
    public async Task ClassToRecord_ConvertsSetToInit_OnClassBodyRecord()
    {
        const string code = @"
using System.ComponentModel.DataAnnotations;
public class Address {
    [Required]
    public string Street { get; set; }
}";
        SetSource(code, "Address.cs");
        var result = await _modEngine.ClassToRecordAsync("Address.cs", "Address");

        // set → init for records
        Assert.That(result.UpdatedText!, Contains.Substring("init"), "set accessor should become init in class-body record");
        Assert.That(result.UpdatedText!, Does.Not.Contain("{ get; set; }"), "Should not have bare { get; set; }");
    }

    // =========================================================================
    // Bug 3: convert_lock_to_semaphore_slim — wrong field modifier for static methods
    // =========================================================================

    [Test]
    public async Task ConvertLockToSemaphoreSlim_InstanceMethod_EmitsInstanceField()
    {
        const string code = @"
public class Service {
    private readonly object _lock = new();
    public void DoWork() {
        lock (_lock) { /* work */ }
    }
}";
        SetSource(code, "Service.cs");
        var result = await _tsEngine.ConvertLockToSemaphoreSlimAsync("Service.cs", "DoWork");

        // Instance method: field must be `private readonly`, NOT `private static readonly`
        Assert.That(result.UpdatedText!, Contains.Substring("private readonly SemaphoreSlim"),
            "Instance-method context must use private readonly field");
        Assert.That(result.UpdatedText!, Does.Not.Contain("private static readonly SemaphoreSlim"),
            "Instance-method context must NOT emit static field");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_StaticMethod_EmitsStaticField()
    {
        // Regression test for Bug 3: static method requires static semaphore field.
        const string code = @"
public class RateLimiter {
    private static readonly object _fallbackLock = new();
    public static void Throttle() {
        lock (_fallbackLock) { /* throttle */ }
    }
}";
        SetSource(code, "RateLimiter.cs");
        var result = await _tsEngine.ConvertLockToSemaphoreSlimAsync("RateLimiter.cs", "Throttle");

        // Static method: field must be `private static readonly`
        Assert.That(result.UpdatedText!, Contains.Substring("private static readonly SemaphoreSlim"),
            "Static-method context must use private static readonly field");
        Assert.That(result.UpdatedText!, Does.Not.Contain("private readonly SemaphoreSlim "),
            "Static-method context must NOT emit instance field");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MixedStaticAndInstance_UsesInstanceField()
    {
        // If at least one method using the lock is instance, the field must be instance.
        const string code = @"
public class Cache {
    private static readonly object _lock = new();
    public static void FlushAll() { lock (_lock) { /* flush */ } }
    public void Refresh()        { lock (_lock) { /* refresh */ } }
}";
        SetSource(code, "Cache.cs");
        var result = await _tsEngine.ConvertLockToSemaphoreSlimAsync("Cache.cs", "FlushAll");

        // Both methods share the lock; since Refresh is instance, field must be instance
        Assert.That(result.UpdatedText!, Contains.Substring("private readonly SemaphoreSlim"),
            "Mixed static+instance context must produce instance field");
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_StaticMethod_CompilesCorrectly()
    {
        // Static context: `await _semaphore.WaitAsync()` must compile — field must be static.
        const string code = @"
public class Counter {
    private static readonly object _lock = new();
    private static int _count;
    public static void Increment() {
        lock (_lock) { _count++; }
    }
}";
        SetSource(code, "Counter.cs");
        var result = await _tsEngine.ConvertLockToSemaphoreSlimAsync("Counter.cs", "Increment");

        // The converted code must reference _semaphore in a static method body
        Assert.That(result.UpdatedText!, Contains.Substring("_semaphore.WaitAsync()"), "Must use _semaphore.WaitAsync()");
        Assert.That(result.UpdatedText!, Contains.Substring("private static readonly SemaphoreSlim"),
            "Field must be static so static method can access it");
    }

    // =========================================================================
    // Bug 4: use_field_backed_properties — inverted direction + empty-string crash
    // =========================================================================

    [Test]
    public async Task UseFieldBackedProperties_WhenDocumentNotFound_ReturnsGracefulMessage()
    {
        // Regression test for Bug 4 crash: file not in workspace should not throw.
        SetSource("public class Dummy {}", "Dummy.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("NonExistentFile.cs");

        Assert.That(result, Is.Not.Null, "Must return a string, not null");
        Assert.That(result.UpdatedText!, Contains.Substring("not found").Or.Contains("No backing"),
            "Must return graceful message when file not found");
    }

    [Test]
    public async Task UseFieldBackedProperties_WhenNoBackingFieldPairs_ReturnsOriginalContent()
    {
        // No pairs means source is returned unchanged.
        const string code = @"
public class Simple {
    public string Name { get; set; }
    public int Value { get; set; }
}";
        SetSource(code, "Simple.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Simple.cs");

        // Auto-properties with no backing field → no change, but must return full source
        Assert.That(result.UpdatedText!, Is.Not.Null.And.Not.Empty, "Must return source, not empty string");
        Assert.That(result.UpdatedText!, Contains.Substring("public string Name"), "Original content must be preserved");
    }

    [Test]
    public async Task UseFieldBackedProperties_WhenBackingFieldPairExists_ConvertsToAutoProperty()
    {
        // Regression test for Bug 4: tool must collapse backing-field+property to auto-property.
        const string code = @"
public class Entity {
    private string _name;
    private int _count;
    public string Name { get => _name; set => _name = value; }
    public int Count   { get => _count; set => _count = value; }
}";
        SetSource(code, "Entity.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Entity.cs");

        // Backing fields must be removed
        Assert.That(result.UpdatedText!, Does.Not.Contain("private string _name"),  "Backing field _name must be removed");
        Assert.That(result.UpdatedText!, Does.Not.Contain("private int _count"),    "Backing field _count must be removed");
        // Properties must become auto-properties
        Assert.That(result.UpdatedText!, Contains.Substring("{ get; set; }"),       "Properties must become auto-properties");
    }

    [Test]
    public async Task UseFieldBackedProperties_WhenBackingFieldHasInitializer_TransfersInitializerToAutoProp()
    {
        const string code = @"
public class Settings {
    private string _host = ""localhost"";
    public string Host { get => _host; set => _host = value; }
}";
        SetSource(code, "Settings.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Settings.cs");

        Assert.That(result.UpdatedText!, Does.Not.Contain("private string _host"), "Backing field must be removed");
        Assert.That(result.UpdatedText!, Contains.Substring("localhost"), "Initializer must be transferred to auto-property");
    }

    [Test]
    public async Task UseFieldBackedProperties_WhenInitAccessorUsed_PreservesInitOnAutoProperty()
    {
        const string code = @"
public class Dto {
    private string _id;
    public string Id { get => _id; init => _id = value; }
}";
        SetSource(code, "Dto.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Dto.cs");

        Assert.That(result.UpdatedText!, Does.Not.Contain("private string _id"), "Backing field must be removed");
        // Should preserve init semantics
        Assert.That(result.UpdatedText!, Contains.Substring("init"), "init accessor must be preserved");
    }

    [Test]
    public async Task UseFieldBackedProperties_DoesNotExpandAutoPropertiesToBackingFields()
    {
        // Regression test ensuring the direction is correct: the tool must NOT expand auto-props.
        const string code = @"
public class Auto {
    public string Name  { get; set; }
    public int    Count { get; set; }
}";
        SetSource(code, "Auto.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Auto.cs");

        // Must not expand auto-properties to backing fields
        Assert.That(result.UpdatedText!, Does.Not.Contain("private string _name"),  "Must NOT expand auto-prop to backing field");
        Assert.That(result.UpdatedText!, Does.Not.Contain("private int _count"),    "Must NOT expand auto-prop to backing field");
        Assert.That(result.UpdatedText!, Contains.Substring("{ get; set; }"),       "Auto-props must remain unchanged");
    }

    [Test]
    public async Task UseFieldBackedProperties_ReadOnlyBackingField_IsNotConverted()
    {
        // readonly backing fields are not candidates (they need readonly auto-property semantics which is different)
        const string code = @"
public class Immutable {
    private readonly string _id;
    public string Id { get => _id; }
    public Immutable(string id) { _id = id; }
}";
        SetSource(code, "Immutable.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Immutable.cs");

        // readonly fields should NOT be candidates for this conversion
        Assert.That(result.UpdatedText!, Contains.Substring("private readonly string _id"),
            "readonly backing fields must not be converted");
    }

    [Test]
    public async Task UseFieldBackedProperties_StaticBackingField_IsNotConverted()
    {
        // static backing fields are not candidates
        const string code = @"
public class Registry {
    private static string _instance;
    public static string Instance { get => _instance; set => _instance = value; }
}";
        SetSource(code, "Registry.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Registry.cs");

        // static fields should NOT be converted
        Assert.That(result.UpdatedText!, Contains.Substring("private static string _instance"),
            "Static backing fields must not be converted");
    }

    // =========================================================================
    // Cross-cutting: the handler in SentinelModernizationTools must not throw
    // =========================================================================

    [Test]
    public async Task UseFieldBackedPropertiesEngine_WhenFeatureDisabled_ReturnsEmptyString()
    {
        _cfg.SetFeatureStatus("FieldBackedProperties", false);
        SetSource("public class Test { public string X { get; set; } }", "Test.cs");
        var result = await _suEngine.UseFieldBackedPropertiesAsync("Test.cs");
        Assert.That(result.UpdatedText!, Is.Empty, "Disabled feature must return string.Empty");
        _cfg.SetFeatureStatus("FieldBackedProperties", true);
    }

    // =========================================================================
    // Additional regression: class_to_record positional record must have semicolon
    // =========================================================================

    [Test]
    public async Task ClassToRecord_SimpleWithNoNonPropertyMembers_ProducesSemicolonOrBraceRecord()
    {
        const string code = @"
public class Vector { public double X { get; init; } public double Y { get; init; } }";
        SetSource(code, "Vector.cs");
        var result = await _modEngine.ClassToRecordAsync("Vector.cs", "Vector");

        // Positional form should end with ; OR contain { }
        bool hasSemicolon = result.UpdatedText!.Contains("record Vector(") && result.UpdatedText!.Contains(";");
        bool hasBraces    = result.UpdatedText!.Contains("record Vector(") && result.UpdatedText!.Contains("{") && result.UpdatedText!.Contains("}");
        Assert.That(hasSemicolon || hasBraces, Is.True,
            "Positional record must end with ; or contain braces");
    }

    [Test]
    public async Task ClassToRecord_WithNonPropertyMembers_IncludesMethodsInRecord()
    {
        const string code = @"
public class Calc {
    public int X { get; init; }
    public int Double() => X * 2;
}";
        SetSource(code, "Calc.cs");
        var result = await _modEngine.ClassToRecordAsync("Calc.cs", "Calc");

        // The Double() method must survive conversion
        Assert.That(result.UpdatedText!, Contains.Substring("Double()"), "Non-property members must be preserved");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
