using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Tests for ConvertToNullCoalescing feature
/// Covers conversion of null checks to null coalescing operators (?? and ??=)
/// </summary>
[TestFixture]
public class CodeGenerationTests

{
    private IWorkspaceManager _workspaceManager;
    private CodeGenerationEngine _codeGenerationEngine;


    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateConstructorAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateConstructor_ClassWithPrivateReadonlyFields_AddsParameterizedCtor()
    {
        SetSource(@"
public class EmailService
{
    private readonly string _smtpHost;
    private readonly int _port;
}", "EmailService.cs");

        var result = await _codeGenerationEngine.GenerateConstructorAsync("EmailService.cs", "EmailService");

        Assert.That(result.UpdatedText, Does.Contain("EmailService("),
            "Generated constructor should have the class name.");
        Assert.That(result.UpdatedText, Does.Contain("smtpHost"),
            "Constructor parameter should be derived from _smtpHost field.");
        Assert.That(result.UpdatedText, Does.Contain("port"),
            "Constructor parameter should be derived from _port field.");
        Assert.That(result.UpdatedText, Does.Contain("this._smtpHost"),
            "Constructor body should assign this._smtpHost.");
    }

    [Test]
    public async Task GenerateConstructor_ClassAlreadyHasCtor_ReturnsUnchanged()
    {
        SetSource(@"
public class MyService
{
    private readonly string _name;

    public MyService(string name)
    {
        _name = name;
    }
}", "MyService.cs");

        var result = await _codeGenerationEngine.GenerateConstructorAsync("MyService.cs", "MyService");

        // Should return unchanged (constructor already exists)
        Assert.That(result.UpdatedText, Does.Contain("public MyService(string name)"),
            "Existing constructor should be preserved unchanged.");
        // No duplicate constructor
        Assert.That(result.UpdatedText!.IndexOf("public MyService("), Is.EqualTo(result.UpdatedText!.LastIndexOf("public MyService(")),
            "Should not add a duplicate constructor.");
    }

    [Test]
    public async Task GenerateConstructor_ClassWithNoFields_ReturnsUnchanged()
    {
        SetSource(@"
public class Empty
{
    public string Name { get; set; }
}", "Empty.cs");

        var result = await _codeGenerationEngine.GenerateConstructorAsync("Empty.cs", "Empty");

        Assert.That(result.UpdatedText, Does.Not.Contain("Empty("),
            "Class with no private/readonly fields should not get a generated constructor.");
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateToStringAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateToString_ExcludesSensitivePropertiesAutomatically()
    {
        SetSource(@"
public class UserAccount
{
    public string Name { get; set; }
    public string Email { get; set; }
    public string Password { get; set; }
}", "UserAccount.cs");

        var result = await _codeGenerationEngine.GenerateToStringAsync("UserAccount.cs", "UserAccount");

        Assert.That(result.IncludedProperties, Contains.Item("Name"),
            "Name should be included in ToString().");
        Assert.That(result.IncludedProperties, Contains.Item("Email"),
            "Email should be included in ToString().");
        Assert.That(result.ExcludedProperties, Contains.Item("Password"),
            "Password is sensitive and should be auto-excluded.");
        Assert.That(result.UpdatedContent, Does.Contain("override"),
            "Generated ToString should be an override.");
    }

    [Test]
    public async Task GenerateToString_ExplicitExcludeList_HonorsUserExclusions()
    {
        SetSource(@"
public class Product
{
    public string Name { get; set; }
    public decimal Price { get; set; }
    public string InternalCode { get; set; }
}", "Product.cs");

        var result = await _codeGenerationEngine.GenerateToStringAsync(
            "Product.cs", "Product",
            excludeProperties: ["InternalCode"]);

        Assert.That(result.ExcludedProperties, Contains.Item("InternalCode"),
            "Explicitly excluded property should not appear in ToString.");
        Assert.That(result.IncludedProperties, Contains.Item("Name"),
            "Non-excluded property Name should be included.");
        Assert.That(result.IncludedProperties, Does.Not.Contain("InternalCode"),
            "InternalCode should not appear in IncludedProperties list.");
        Assert.That(result.UpdatedContent, Does.Contain("Name"),
            "Name should appear in the formatted string.");
    }

    [Test]
    public async Task GenerateToString_AlreadyHasToString_ReturnsWarning()
    {
        SetSource(@"
public class Widget
{
    public string Name { get; set; }
    public override string ToString() => Name;
}", "Widget.cs");

        var result = await _codeGenerationEngine.GenerateToStringAsync("Widget.cs", "Widget");

        Assert.That(result.Warning, Does.Contain("already exists"),
            "Should warn that ToString() override already exists.");
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateDefaultConfigJsonAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateDefaultConfigJson_ExtractsConfigKeys()
    {
        SetSource(@"
public class Startup
{
    public void Configure(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var host = config[""SmtpHost""];
        var port = config[""SmtpPort""];
    }
}", "Startup.cs");

        var result = await _codeGenerationEngine.GenerateDefaultConfigJsonAsync("TestProj");

        Assert.That(result.UpdatedText, Does.Contain("SmtpHost"),
            "Config key SmtpHost should be extracted.");
        Assert.That(result.UpdatedText, Does.Contain("SmtpPort"),
            "Config key SmtpPort should be extracted.");
        Assert.That(result.UpdatedText, Does.Contain("{"),
            "Result should be valid JSON.");
    }

    [Test]
    public async Task GenerateDefaultConfigJson_EmptyProject_ReturnsEmptyJson()
    {
        SetSource(@"
public class Empty { }", "Empty.cs");

        var result = await _codeGenerationEngine.GenerateDefaultConfigJsonAsync("TestProj");

        Assert.That(result.UpdatedText, Does.Contain("{"),
            "Should return at least an empty JSON object.");
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateRepositoryInterfaceAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateRepositoryInterface_CrudClass_GeneratesInterface()
    {
        SetSource(@"
using System.Threading.Tasks;
public class UserRepository
{
    public async Task<string> GetByIdAsync(int id) => """";
    public async Task<int> CreateAsync(string name) => 0;
    public async Task UpdateAsync(int id, string name) { }
    public async Task DeleteAsync(int id) { }
}", "UserRepository.cs");

        var result = await _codeGenerationEngine.GenerateRepositoryInterfaceAsync("UserRepository.cs", "UserRepository");

        Assert.That(result.InterfaceName, Is.EqualTo("IUserRepository"),
            "Interface name should be I + ClassName.");
        Assert.That(result.InterfaceCode, Does.Contain("IUserRepository"),
            "Interface code should declare IUserRepository.");
        Assert.That(result.InterfaceCode, Does.Contain("GetByIdAsync"),
            "Interface should include GetByIdAsync.");
        Assert.That(result.InterfaceCode, Does.Contain("CreateAsync"),
            "Interface should include CreateAsync.");
        Assert.That(result.DiRegistrationSnippet, Does.Contain("IUserRepository"),
            "DI snippet should reference the interface.");
        Assert.That(result.DiRegistrationSnippet, Does.Contain("UserRepository"),
            "DI snippet should reference the concrete class.");
    }

    [Test]
    public async Task GenerateRepositoryInterface_IncludesMockSetupSnippet()
    {
        SetSource(@"
using System.Threading.Tasks;
public class ProductRepo
{
    public async Task<string> GetAsync(int id) => """";
}", "ProductRepo.cs");

        var result = await _codeGenerationEngine.GenerateRepositoryInterfaceAsync("ProductRepo.cs", "ProductRepo");

        Assert.That(result.MockSetupSnippet, Does.Contain("Mock<IProductRepo>"),
            "Mock snippet should include Mock<Interface>.");
    }

    [Test]
    public async Task GenerateRepositoryInterface_StaticMethodsExcluded()
    {
        SetSource(@"
public class HelperRepo
{
    public string GetById(int id) => """";
    public static string Utility() => """";
}", "HelperRepo.cs");

        var result = await _codeGenerationEngine.GenerateRepositoryInterfaceAsync("HelperRepo.cs", "HelperRepo");

        Assert.That(result.InterfaceCode, Does.Contain("GetById"),
            "Instance public method should be in the interface.");
        Assert.That(result.InterfaceCode, Does.Not.Contain("Utility"),
            "Static method should NOT be in the interface.");
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateFluentBuilderAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateFluentBuilder_ClassWithProperties_GeneratesBuilderClass()
    {
        SetSource(@"
public class Order
{
    public string CustomerId { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; }
}", "Order.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("Order.cs", "Order");

        Assert.That(result.BuilderClassName, Is.EqualTo("OrderBuilder"),
            "Builder class name should be ClassName + Builder.");
        Assert.That(result.BuilderCode, Does.Contain("OrderBuilder"),
            "Builder code should declare OrderBuilder.");
        Assert.That(result.BuilderCode, Does.Contain("WithCustomerId"),
            "Builder should have WithCustomerId method.");
        Assert.That(result.BuilderCode, Does.Contain("WithTotal"),
            "Builder should have WithTotal method.");
        Assert.That(result.BuilderCode, Does.Contain("Build()"),
            "Builder should have a Build() method.");
    }

    [Test]
    public async Task GenerateFluentBuilder_BuildMethodReturnsTargetType()
    {
        SetSource(@"
public class Customer
{
    public string Name { get; set; }
    public string Email { get; set; }
}", "Customer.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("Customer.cs", "Customer");

        Assert.That(result.BuilderCode, Does.Contain("public Customer Build()"),
            "Build() must return the target type.");
        Assert.That(result.UsageExample, Does.Contain("CustomerBuilder"),
            "Usage example should reference the builder.");
    }

    [Test]
    public async Task GenerateFluentBuilder_UsageExampleChains()
    {
        SetSource(@"
public class Item
{
    public string Name { get; set; }
    public int Qty { get; set; }
}", "Item.cs");

        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("Item.cs", "Item");

        Assert.That(result.UsageExample, Does.Contain(".WithName("),
            "Usage example should chain With* calls.");
        Assert.That(result.UsageExample, Does.Contain(".Build()"),
            "Usage example should end with .Build().");
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateDecoratorClassAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateDecorator_InterfaceWithTwoMethods_GeneratesDecoratorClass()
    {
        SetSource(@"
namespace MyApp;
public interface INotifier
{
    void Send(string message);
    bool IsConnected();
}", "INotifier.cs");

        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync("INotifier", "Logging");

        Assert.That(result, Is.Not.Null,
            "Decorator result should not be null for a found interface.");
        Assert.That(result!.ClassName, Does.Contain("Decorator"),
            "Decorator class name should include 'Decorator'.");
        Assert.That(result.SourceCode, Does.Contain("INotifier"),
            "Decorator should implement INotifier.");
        Assert.That(result.SourceCode, Does.Contain("_inner"),
            "Decorator should delegate to inner implementation.");
        Assert.That(result.SourceCode, Does.Contain("Send"),
            "Decorator should forward Send method to _inner.");
    }

    [Test]
    public async Task GenerateDecorator_PrefixAppearsInClassName()
    {
        SetSource(@"
public interface IEmailService
{
    void SendEmail(string to, string body);
}", "IEmailService.cs");

        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync("IEmailService", "Caching");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ClassName, Does.Contain("Caching"),
            "Decorator prefix should appear in class name.");
        Assert.That(result.SuggestedFileName, Does.Contain("Decorator"),
            "Suggested file name should include 'Decorator'.");
    }

    [Test]
    public async Task GenerateDecorator_UnknownInterface_ReturnsNull()
    {
        SetSource(@"
public class SomeClass { }", "SomeClass.cs");

        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync("INonExistent", "Logging");

        Assert.That(result, Is.Null,
            "Unknown interface should return null (not throw).");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ConvertPropertySafe
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ConvertPropertySafe_ToFullProperty_PreservesInitializer()
    {
        SetSource("""
            public class Foo
            {
                public int Count { get; set; } = 42;
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Count", "ToFullProperty");

        Assert.That(result.UpdatedText!, Contains.Substring("42"), "Initializer value should survive ToFullProperty conversion");
        Assert.That(result.UpdatedText!, Contains.Substring("get =>"), "Should produce expression-body getter");
        Assert.That(result.UpdatedText!, Contains.Substring("set =>"), "Should produce expression-body setter");
    }

    [Test]
    public async Task ConvertPropertySafe_ToAutoProperty_RemovesBackingField()
    {
        SetSource("""
            public class Foo
            {
                private int _count;
                public int Count
                {
                    get { return _count; }
                    set { _count = value; }
                }
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Count", "ToAutoProperty");

        Assert.That(result.UpdatedText!, Contains.Substring("{ get; set; }"), "Should produce auto-property");
    }

    [Test]
    public async Task ConvertPropertySafe_InvalidDirection_ThrowsOrReturnsError()
    {
        SetSource("""
            public class Foo { public int X { get; set; } }
            """);

        var ex = Assert.ThrowsAsync<ArgumentException>(
            () => _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "X", "BadDirection"));
        Assert.That(ex?.Message, Does.Contain("direction").IgnoreCase.Or.Contain("BadDirection"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // InterpolateStringSafe
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task InterpolateStringSafe_LiteralFormat_ProducesInterpolatedString()
    {
        SetSource("""
            public class Foo
            {
                public string Build(string name, int age)
                {
                    return string.Format("Hello {0}, you are {1}", name, age);
                }
            }
            """);

        var result = await _codeGenerationEngine.InterpolateStringAsync(
            "Test.cs",
            "string.Format(\"Hello {0}, you are {1}\", name, age)");

        Assert.That(result.UpdatedText!, Contains.Substring("$\""), "Should produce an interpolated string");
        Assert.That(result.UpdatedText!, Contains.Substring("{name}"), "First arg should be inlined");
        Assert.That(result.UpdatedText!, Contains.Substring("{age}"), "Second arg should be inlined");
    }

    [Test]
    public async Task InterpolateStringSafe_SnippetNotFound_ThrowsOrReturnsError()
    {
        SetSource("""
            public class Foo { }
            """);

        DocumentEditResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await _codeGenerationEngine.InterpolateStringAsync("Test.cs", "string.Format(\"missing\")"));
        Assert.That(result!.Message, Does.Contain("ErrorDetails:"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. ConvertPropertySafe -> modifier preservation + contextSnippet
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConvertPropertySafe_PreservesVirtualModifier_OnToFullProperty()
    {
        // ConvertPropertySafe promises to handle virtual/override/new -> this test enforces that.
        SetSource("""
            public class Base
            {
                public virtual int Count { get; set; } = 10;
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Count", "ToFullProperty");

        Assert.That(result.UpdatedText, Does.Contain("virtual"), "virtual modifier must survive ToFullProperty conversion");
        Assert.That(result.UpdatedText, Does.Contain("10"), "Initializer value must survive conversion");
    }

    [Test]
    public async Task ConvertPropertySafe_PreservesOverrideModifier_OnToFullProperty()
    {
        SetSource("""
            public class Base { public virtual int Size { get; set; } }
            public class Derived : Base
            {
                public override int Size { get; set; } = 99;
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Size", "ToFullProperty");

        Assert.That(result.UpdatedText, Does.Contain("override"), "override modifier must survive ToFullProperty conversion");
        Assert.That(result.UpdatedText, Does.Contain("99"), "Initializer 99 must survive conversion");
    }

    [Test]
    public async Task ConvertPropertySafe_ContextSnippet_PicksCorrectPropertyWhenNamesClash()
    {
        // Two classes each have a 'Name' property. contextSnippet must pick the right one.
        SetSource("""
            public class Person
            {
                public string Name { get; set; } = "Alice";
            }
            public class Company
            {
                public string Name { get; set; } = "Acme";
            }
            """);

        // Target only the Company.Name property via contextSnippet
        var result = await _codeGenerationEngine.ConvertPropertySafeAsync(
            "Test.cs", "Name", "ToFullProperty",
            contextSnippet: "\"Acme\"");

        // The result must expand Company.Name (initializer "Acme" should move to backing field)
        // Person.Name should remain an auto-property
        Assert.That(result.UpdatedText, Does.Contain("\"Acme\""),
            "Company's initializer Acme must appear in the backing field");
        // Person.Name should still be an auto-property (no _name backing for Alice)
        var personSection = result.UpdatedText!.Substring(0, result.UpdatedText!.IndexOf("Company", StringComparison.Ordinal));
        Assert.That(personSection, Does.Contain("{ get; set; }"),
            "Person.Name must remain an auto-property - context snippet should have limited the change");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. InterpolateStringSafe -> const format string (the exact MS built-in bug)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task InterpolateStringSafe_ConstFormatString_ResolvedViaSemanticModel()
    {
        // The MS built-in convert_to_interpolated_string fails when the format string is a
        // named const. Our implementation resolves it via the semantic model. This test
        // covers exactly that scenario.
        SetSource("""
            public class Logger
            {
                private const string MessageFmt = "User {0} logged in from {1}";

                public string BuildLog(string user, string ip)
                {
                    return string.Format(MessageFmt, user, ip);
                }
            }
            """);

        var result = await _codeGenerationEngine.InterpolateStringAsync(
            "Test.cs",
            "string.Format(MessageFmt, user, ip)");

        Assert.That(result.UpdatedText!, Contains.Substring("$\""), "Should produce an interpolated string");
        Assert.That(result.UpdatedText!, Contains.Substring("{user}"), "user arg should be inlined");
        Assert.That(result.UpdatedText!, Contains.Substring("{ip}"), "ip arg should be inlined");
        // The const itself should no longer appear as a format reference
        Assert.That(result.UpdatedText!, Does.Not.Contain("string.Format(MessageFmt"), "Original string.Format call must be replaced");
    }

    [Test]
    public async Task InterpolateStringSafe_FormatSpecifier_PreservedInInterpolation()
    {
        // {0:N2} format specifiers must survive conversion.
        SetSource("""
            public class Formatter
            {
                public string FormatPrice(decimal amount)
                {
                    return string.Format("Price: {0:N2}", amount);
                }
            }
            """);

        var result = await _codeGenerationEngine.InterpolateStringAsync(
            "Test.cs",
            "string.Format(\"Price: {0:N2}\", amount)");

        Assert.That(result.UpdatedText!, Contains.Substring("$\""), "Must produce interpolated string");
        Assert.That(result.UpdatedText!, Contains.Substring("{amount:N2}"), "Format specifier N2 must be preserved");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. ImplementInterfaceSafe -> partial implementation, property-only, no override
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ImplementInterfaceSafe_PartialImplementation_OnlyGeneratesMissingMembers()
    {
        // Class already implements one method -> only the missing one should be generated.
        const string source = """
            namespace App;

            public interface IWorker
            {
                void Start();
                void Stop();
            }

            public class Worker : IWorker
            {
                public void Start() { /* already done */ }
            }
            """;
        SetSource(source, "Worker.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Worker.cs", "Worker", "IWorker");

        // Stop() must be generated
        Assert.That(result.UpdatedText, Does.Contain("public void Stop"),
            "Missing Stop() method must be generated");
        // Start() must NOT be duplicated -> check 'public void Start' (not 'void Start' which also matches interface)
        var publicStartCount = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, @"public void Start").Count;
        Assert.That(publicStartCount, Is.EqualTo(1),
            "Start() must NOT be duplicated - it was already implemented");
    }

    [Test]
    public async Task ImplementInterfaceSafe_PropertyOnlyInterface_GeneratesPropertyStubs()
    {
        // Interface with only properties (no methods) must produce property stubs.
        const string source = """
            namespace App;

            public interface IConfig
            {
                string Host { get; set; }
                int Port { get; }
            }

            public class AppConfig : IConfig
            {
            }
            """;
        SetSource(source, "Config.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Config.cs", "AppConfig", "IConfig");

        Assert.That(result.UpdatedText, Does.Contain("public string Host"),
            "Host property stub must be generated");
        Assert.That(result.UpdatedText, Does.Contain("public int Port"),
            "Port property stub must be generated");
        Assert.That(result.UpdatedText, Does.Contain("NotImplementedException"),
            "Property stubs must throw NotImplementedException");
        Assert.That(result.UpdatedText, Does.Not.Contain("override"),
            "REGRESSION: interface property stubs must NOT have 'override' keyword");
    }

    [Test]
    public async Task ImplementInterfaceSafe_NeverAdds_OverrideKeyword_OnMethods()
    {
        // CRITICAL REGRESSION TEST: The MS built-in implement_interface incorrectly adds
        // 'override' to interface implementations. Ours must never do this.
        const string source = """
            namespace App;

            public interface ISerializer
            {
                string Serialize(object obj);
                T Deserialize<T>(string json);
            }

            public class JsonSerializer : ISerializer
            {
            }
            """;
        SetSource(source, "JsonSerializer.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync(
            "JsonSerializer.cs", "JsonSerializer", "ISerializer");

        Assert.That(result.UpdatedText, Does.Not.Contain("override"),
            "REGRESSION: interface method stubs must NEVER have 'override' keyword");
        Assert.That(result.UpdatedText, Does.Contain("public string Serialize"),
            "Serialize stub must be generated");
    }

    [Test]
    public async Task ImplementInterfaceSafe_ReadOnlyProperty_GeneratesGetterOnly()
    {
        // Read-only properties in an interface (get; only) must produce getter-only stubs.
        const string source = """
            namespace App;
            public interface IReadOnly { string Id { get; } }
            public class Impl : IReadOnly { }
            """;
        SetSource(source, "Impl.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Impl.cs", "Impl", "IReadOnly");

        Assert.That(result.UpdatedText, Does.Contain("public string Id"),
            "Id property must be generated");
        // A read-only stub should NOT have a setter accessor
        var idPropStart = result.UpdatedText!.IndexOf("public string Id", StringComparison.Ordinal);
        var afterId = result.UpdatedText!.Substring(idPropStart);
        var nextMemberOrEnd = afterId.IndexOf("\n    public ", StringComparison.Ordinal);
        var idBlock = nextMemberOrEnd > 0 ? afterId.Substring(0, nextMemberOrEnd) : afterId;
        Assert.That(idBlock, Does.Not.Contain("set"),
            "Read-only interface property must NOT generate a setter");
    }

}
