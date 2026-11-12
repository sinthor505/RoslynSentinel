using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Direct tests of MemberRefactoringNotFound (the shared not-found factories for MemberRefactoringEngine). The factories have no engine
// callers yet except through later sweep slices, so each one is driven here: a near-miss name gets the ranked "Did you mean ... Retry
// with <param>" hint, an alien name gets the no-similar-name fact, and neither carries policy wording. ToolCallHint renders a bare tool
// name when the tool catalog is not loaded in the test process, so the asserts name the tool, not its argument list.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberRefactoringNotFoundTests
{
    private const string FilePath = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Source = """
        using System;
        namespace Shop.Orders
        {
            [Serializable]
            public class OrderPricingCalculator : PricingBase, IPriced
            {
                private readonly int _rate;
                public OrderPricingCalculator(int rate, string currency) { _rate = rate; }
                public OrderPricingCalculator(int rate) { _rate = rate; }
                public static decimal CalcDisc(decimal total) => total;
                public decimal OrderCheckout(decimal total) => total;
            }
            public struct Money { }
            public enum OrderStatus { Pending, Shipped, Delivered }
            public class PricingBase { }
            public interface IPriced { }
        }
        namespace Shop.Billing { public class Invoice { } }
        """;

    private static readonly SyntaxNode Root = CSharpSyntaxTree.ParseText(Source).GetRoot();

    private static ClassDeclarationSyntax Calculator => Root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == "OrderPricingCalculator");

    private static EnumDeclarationSyntax Status => Root.DescendantNodes().OfType<EnumDeclarationSyntax>().First();

    private static MethodDeclarationSyntax CalcDisc => Calculator.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "CalcDisc");

    private static MethodDeclarationSyntax OrderCheckout => Calculator.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "OrderCheckout");

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    private static void AssertCandidate(string message, string candidate, string paramName)
    {
        Assert.That(message, Does.Contain($"Did you mean '{candidate}'? Retry with {paramName}: '{candidate}'."));
        AssertFactsAndOptionsOnly(message);
    }

    private static void AssertNoCandidate(string message)
    {
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public void Class_NearMissName_RanksTheClassAndKeepsKindAndName()
    {
        var ex = MemberRefactoringNotFound.Class("OrderPricingCalculater", Root, FilePath);
        AssertCandidate(ex.Message, "OrderPricingCalculator", "className");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("class"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("OrderPricingCalculater"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public void Class_AlienName_StatesNoSimilarNameAndOffersTheOutline()
    {
        var ex = MemberRefactoringNotFound.Class(AlienName, Root, FilePath);
        AssertNoCandidate(ex.Message);
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
    }

    [Test]
    public void Class_NameOfAStruct_StatesTheActualKind()
    {
        var ex = MemberRefactoringNotFound.Class("Money", Root, FilePath);
        Assert.That(ex.Message, Does.Contain("'Money' is declared in 'Calc.cs' as a struct, not as a class."));
    }

    [Test]
    public void Enum_NearMissName_RanksTheEnum()
    {
        var ex = MemberRefactoringNotFound.Enum("OrderStatu", Root, FilePath);
        AssertCandidate(ex.Message, "OrderStatus", "enumName");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("enum"));
    }

    [Test]
    public void Enum_NameOfAClass_StatesTheActualKindAndNoSimilarName()
    {
        var ex = MemberRefactoringNotFound.Enum("PricingBase", Root, FilePath);
        Assert.That(ex.Message, Does.Contain("'PricingBase' is declared in 'Calc.cs' as a class, not as an enum."));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
    }

    [Test]
    public void Method_NearMissName_RanksTheMethod()
    {
        var ex = MemberRefactoringNotFound.Method("CalcDsc", Root, FilePath);
        AssertCandidate(ex.Message, "CalcDisc", "methodName");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("method"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("CalcDsc"));
    }

    [Test]
    public void Method_AlienName_StatesNoSimilarNameAndOffersTheOutline()
    {
        var ex = MemberRefactoringNotFound.Method(AlienName, Root, FilePath);
        AssertNoCandidate(ex.Message);
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
    }

    [Test]
    public void Method_FileWithoutMethods_SaysNoMethodIsDeclared()
    {
        var emptyRoot = CSharpSyntaxTree.ParseText("public class Empty { }").GetRoot();
        var ex = MemberRefactoringNotFound.Method("Anything", emptyRoot, FilePath);
        Assert.That(ex.Message, Does.Contain("No method is declared in 'Calc.cs'"));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
    }

    [Test]
    public void Type_NearMissName_RanksTheTypeAndHonoursTheParamName()
    {
        var ex = MemberRefactoringNotFound.Type("PricingBas", Root, FilePath, "containerName");
        AssertCandidate(ex.Message, "PricingBase", "containerName");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("type"));
    }

    [Test]
    public void Type_AlienName_StatesNoSimilarName()
    {
        AssertNoCandidate(MemberRefactoringNotFound.Type(AlienName, Root, FilePath).Message);
    }

    [Test]
    public void Target_NearMissName_RanksAnyDeclarationAndKeepsTheMemberKind()
    {
        var ex = MemberRefactoringNotFound.Target("OrderChekout", Root, FilePath);
        AssertCandidate(ex.Message, "OrderCheckout", "targetName");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("member"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("OrderChekout"));
    }

    [Test]
    public void Target_AlienName_StatesNoSimilarName()
    {
        var ex = MemberRefactoringNotFound.Target(AlienName, Root, FilePath);
        AssertNoCandidate(ex.Message);
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
    }

    [Test]
    public void Namespace_NearMissName_RanksTheNamespaceAndListsThem()
    {
        var ex = MemberRefactoringNotFound.Namespace("Shop.Order", Root, FilePath);
        AssertCandidate(ex.Message, "Shop.Orders", "namespaceName");
        Assert.That(ex.Message, Does.Contain("Namespaces declared in 'Calc.cs': Shop.Orders, Shop.Billing."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("namespace"));
    }

    [Test]
    public void Namespace_AlienName_StatesNoSimilarNameAndListsThem()
    {
        var ex = MemberRefactoringNotFound.Namespace("Qqq.Zzz", Root, FilePath);
        AssertNoCandidate(ex.Message);
        Assert.That(ex.Message, Does.Contain("Namespaces declared in 'Calc.cs': Shop.Orders, Shop.Billing."));
    }

    [Test]
    public void EnumMember_NearMissName_RanksTheMemberOfThatEnum()
    {
        var ex = MemberRefactoringNotFound.EnumMember("Shiped", "OrderStatus", Status, FilePath);
        AssertCandidate(ex.Message, "Shipped", "memberName");
        Assert.That(ex.Message, Does.Contain("Declared in enum 'OrderStatus': Pending, Shipped, Delivered."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("enum member"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("Shiped"));
    }

    [Test]
    public void EnumMember_AlienName_StatesItIsNotAmongTheMembers()
    {
        var ex = MemberRefactoringNotFound.EnumMember(AlienName, "OrderStatus", Status, FilePath);
        AssertNoCandidate(ex.Message);
        Assert.That(ex.Message, Does.Contain($"'{AlienName}' is not among them, so it does not exist in enum 'OrderStatus'."));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
    }

    [Test]
    public void ConstructorParameter_NearMissName_RanksTheParameterAcrossConstructors()
    {
        var ex = MemberRefactoringNotFound.ConstructorParameter("curency", "OrderPricingCalculator", Calculator, FilePath);
        AssertCandidate(ex.Message, "currency", "paramName");
        Assert.That(ex.Message, Does.Contain("Declared in the constructors of 'OrderPricingCalculator': rate, currency."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("parameter"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("curency"));
    }

    [Test]
    public void ConstructorParameter_AlienName_StatesNoSimilarName()
    {
        var ex = MemberRefactoringNotFound.ConstructorParameter(AlienName, "OrderPricingCalculator", Calculator, FilePath);
        AssertNoCandidate(ex.Message);
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
    }

    [Test]
    public void SnippetOutsideConstructors_NamesTheConstructorLinesAndOffersAReadOfTheFirst()
    {
        var ex = MemberRefactoringNotFound.SnippetOutsideConstructors("rate", "OrderPricingCalculator", [8, 9], FilePath);
        Assert.That(ex.Message, Does.Contain("contextSnippet is not inside any constructor of 'OrderPricingCalculator' that declares parameter 'rate'."));
        Assert.That(ex.Message, Does.Contain("start at line 8, 9."));
        Assert.That(ex.Message, Does.Contain("ReadFile"));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("parameter"));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    public void StartLines_AreOneBasedLinesOfTheNodes()
    {
        var text = CSharpSyntaxTree.ParseText(Source).GetText();
        var ctorLines = MemberRefactoringNotFound.StartLines(text, Calculator.Members.OfType<ConstructorDeclarationSyntax>());
        Assert.That(ctorLines, Is.EqualTo(new[] { 8, 9 }));
    }

    [Test]
    public void Attribute_NearMissName_RanksTheAttributeOfTheTarget()
    {
        var ex = MemberRefactoringNotFound.Attribute("Serializabel", "OrderPricingCalculator", Calculator, FilePath, "oldAttributeName");
        AssertCandidate(ex.Message, "Serializable", "oldAttributeName");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("attribute"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("Serializabel"));
    }

    [Test]
    public void Attribute_TargetWithoutAttributes_SaysNoAttributesAreDeclared()
    {
        var money = Root.DescendantNodes().OfType<StructDeclarationSyntax>().First();
        var ex = MemberRefactoringNotFound.Attribute("Serializable", "Money", money, FilePath);
        Assert.That(ex.Message, Does.Contain("No attributes are declared in 'Money'."));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    public void Declaration_NearMissName_UsesTheGivenKindLabelAndParam()
    {
        var ex = MemberRefactoringNotFound.Declaration("interface", "IPrice", "interfaceName", ["IPriced", "IOther"], FilePath);
        AssertCandidate(ex.Message, "IPriced", "interfaceName");
        Assert.That(ex.Message, Does.StartWith("Interface 'IPrice' was not found in 'Calc.cs'."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("interface"));
    }

    [Test]
    public void Declaration_AlienName_StatesNoSimilarName()
    {
        var ex = MemberRefactoringNotFound.Declaration("interface", AlienName, "interfaceName", ["IPriced"], FilePath, "type");
        AssertNoCandidate(ex.Message);
        Assert.That(ex.MissingTargetKind, Is.EqualTo("type"));
    }

    [Test]
    public void BatchMessage_NearMissName_PrefixesTheIndexAndRendersTheRetryParameterAsBatchEntry()
    {
        var message = MemberRefactoringNotFound.BatchMessage(2, "OrderChekout", "declaration", "targetName", MemberRefactoringNotFound.MemberNames(Root), FilePath);
        Assert.That(message, Does.StartWith("batchEdits[2] (OrderChekout): "));
        AssertCandidate(message, "OrderCheckout", "batchEdits[2].targetName");
    }

    [Test]
    public void BatchMessage_AlienName_StatesNoSimilarName()
    {
        var message = MemberRefactoringNotFound.BatchMessage(0, AlienName, "type", "typeName", MemberRefactoringNotFound.TypeNames(Root), FilePath);
        Assert.That(message, Does.StartWith($"batchEdits[0] ({AlienName}): "));
        AssertNoCandidate(message);
    }

    [Test]
    public void ModifierMessage_NearMissModifier_RanksTheModifiersOfTheTarget()
    {
        var message = MemberRefactoringNotFound.ModifierMessage("Static", "CalcDisc", CalcDisc);
        AssertCandidate(message, "static", "modifier");
        Assert.That(message, Does.Contain("Declared in 'CalcDisc': public, static."));
    }

    [Test]
    public void ModifierMessage_AbsentModifier_StatesNoSimilarModifier()
    {
        var message = MemberRefactoringNotFound.ModifierMessage("sealed", "OrderCheckout", OrderCheckout);
        AssertNoCandidate(message);
        Assert.That(message, Does.Contain("Declared in 'OrderCheckout': public."));
    }

    [Test]
    public void BaseTypeMessage_NearMissBaseType_RanksTheBaseList()
    {
        var message = MemberRefactoringNotFound.BaseTypeMessage("IPriceed", "OrderPricingCalculator", Calculator);
        AssertCandidate(message, "IPriced", "baseTypeName");
        Assert.That(message, Does.Contain("Declared in the base list of 'OrderPricingCalculator': PricingBase, IPriced."));
    }

    [Test]
    public void BaseTypeMessage_TypeWithoutBaseList_SaysNoBaseTypesAreDeclared()
    {
        var money = Root.DescendantNodes().OfType<StructDeclarationSyntax>().First();
        var message = MemberRefactoringNotFound.BaseTypeMessage("IPriced", "Money", money);
        Assert.That(message, Does.Contain("No base types are declared in the base list of 'Money'."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public void Unavailable_NamesTheMissingPartAndOffersReadFile()
    {
        var ex = MemberRefactoringNotFound.Unavailable("syntax root", "C:\\work\\Calc.cs");
        Assert.That(ex.Message, Does.Contain("The syntax root of 'C:\\work\\Calc.cs' is not available"));
        Assert.That(ex.Message, Does.Contain("ReadFile"));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("document"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("C:\\work\\Calc.cs"));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    public void Unresolved_WithAPath_AppendsTheOutlineOption()
    {
        var ex = MemberRefactoringNotFound.Unresolved("class", "PricingBase", "Base class 'PricingBase' is declared in an external assembly.", FilePath);
        Assert.That(ex.Message, Does.StartWith("Base class 'PricingBase' is declared in an external assembly."));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("class"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("PricingBase"));
    }

    [Test]
    public void Unresolved_WithoutAPath_IsTheFactAlone()
    {
        var ex = MemberRefactoringNotFound.Unresolved("class", "PricingBase", "Base class 'PricingBase' is declared in an external assembly.");
        Assert.That(ex.Message, Is.EqualTo("Base class 'PricingBase' is declared in an external assembly."));
    }

    [Test]
    public void Collectors_AreNullTolerantAndDistinct()
    {
        Assert.That(MemberRefactoringNotFound.TypeNames(null), Is.Empty);
        Assert.That(MemberRefactoringNotFound.MethodNames(null), Is.Empty);
        Assert.That(MemberRefactoringNotFound.MemberNames(null), Is.Empty);
        Assert.That(MemberRefactoringNotFound.TypeNames(Root), Is.EquivalentTo(new[] { "OrderPricingCalculator", "Money", "OrderStatus", "PricingBase", "IPriced", "Invoice" }));
        Assert.That(MemberRefactoringNotFound.ClassNames(Root), Is.EquivalentTo(new[] { "OrderPricingCalculator", "PricingBase", "Invoice" }));
        Assert.That(MemberRefactoringNotFound.EnumNames(Root), Is.EquivalentTo(new[] { "OrderStatus" }));
        Assert.That(MemberRefactoringNotFound.MethodNames(Calculator), Is.EquivalentTo(new[] { "CalcDisc", "OrderCheckout" }));
        Assert.That(MemberRefactoringNotFound.MemberNames(Root), Does.Contain("Pending").And.Contain("_rate").And.Contain("OrderCheckout").And.Contain("Money"));
    }
}
