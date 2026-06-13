using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

[TestFixture]
public class CodeGenerationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private CodeGenerationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
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

        var result = await _engine.GenerateConstructorAsync("EmailService.cs", "EmailService");

        Assert.That(result, Does.Contain("EmailService("),
            "Generated constructor should have the class name.");
        Assert.That(result, Does.Contain("smtpHost"),
            "Constructor parameter should be derived from _smtpHost field.");
        Assert.That(result, Does.Contain("port"),
            "Constructor parameter should be derived from _port field.");
        Assert.That(result, Does.Contain("this._smtpHost"),
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

        var result = await _engine.GenerateConstructorAsync("MyService.cs", "MyService");

        // Should return unchanged (constructor already exists)
        Assert.That(result, Does.Contain("public MyService(string name)"),
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

        var result = await _engine.GenerateConstructorAsync("Empty.cs", "Empty");

        Assert.That(result, Does.Not.Contain("Empty("),
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

        var result = await _engine.GenerateToStringAsync("UserAccount.cs", "UserAccount");

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

        var result = await _engine.GenerateToStringAsync(
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

        var result = await _engine.GenerateToStringAsync("Widget.cs", "Widget");

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

        var result = await _engine.GenerateDefaultConfigJsonAsync("TestProj");

        Assert.That(result, Does.Contain("SmtpHost"),
            "Config key SmtpHost should be extracted.");
        Assert.That(result, Does.Contain("SmtpPort"),
            "Config key SmtpPort should be extracted.");
        Assert.That(result, Does.Contain("{"),
            "Result should be valid JSON.");
    }

    [Test]
    public async Task GenerateDefaultConfigJson_EmptyProject_ReturnsEmptyJson()
    {
        SetSource(@"
public class Empty { }", "Empty.cs");

        var result = await _engine.GenerateDefaultConfigJsonAsync("TestProj");

        Assert.That(result, Does.Contain("{"),
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

        var result = await _engine.GenerateRepositoryInterfaceAsync("UserRepository.cs", "UserRepository");

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

        var result = await _engine.GenerateRepositoryInterfaceAsync("ProductRepo.cs", "ProductRepo");

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

        var result = await _engine.GenerateRepositoryInterfaceAsync("HelperRepo.cs", "HelperRepo");

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

        var result = await _engine.GenerateFluentBuilderAsync("Order.cs", "Order");

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

        var result = await _engine.GenerateFluentBuilderAsync("Customer.cs", "Customer");

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

        var result = await _engine.GenerateFluentBuilderAsync("Item.cs", "Item");

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

        var result = await _engine.GenerateDecoratorClassAsync("INotifier", "Logging");

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

        var result = await _engine.GenerateDecoratorClassAsync("IEmailService", "Caching");

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

        var result = await _engine.GenerateDecoratorClassAsync("INonExistent", "Logging");

        Assert.That(result, Is.Null,
            "Unknown interface should return null (not throw).");
    }
}
