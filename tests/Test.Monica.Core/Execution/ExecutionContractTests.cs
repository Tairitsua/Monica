using System.Reflection;
using AwesomeAssertions;
using Monica.Core.Execution;
using Monica.Tool.Extensions;
using Xunit;

namespace Test.Monica.Core.Execution;

public sealed class ExecutionContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" mediator.request")]
    [InlineData("mediator.request ")]
    public void ExecutionPoint_WhenValueIsNotStable_ShouldRejectIt(string value)
    {
        Action create = () => _ = new ExecutionPoint(value);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExecutionFeatureCollection_ShouldUseTheExactFeatureContract()
    {
        var features = new ExecutionFeatureCollection();
        var feature = new ConcreteFeature("topic");

        features.Set(feature);

        features.GetRequired<ConcreteFeature>().Should().BeSameAs(feature);
        features.TryGet<IFeature>(out _).Should().BeFalse();
        features.Count.Should().Be(1);
        features.Remove<ConcreteFeature>().Should().BeTrue();
        features.Count.Should().Be(0);
    }

    [Fact]
    public void ExecutionContext_WhenDescriptorInputDoesNotMatch_ShouldRejectIt()
    {
        var descriptor = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.operation"),
            typeof(ExecutionContractTests),
            null,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        Action create = () => _ = new ExecutionContext<int>(descriptor, 42);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*does not match context input type*");
    }

    [Fact]
    public void ExecutionDescriptor_WhenMethodBoundaryIsEquivalent_ShouldReturnMemoizedInstance()
    {
        var point = new ExecutionPoint("test.memoized");

        var first = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            point,
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        for (var index = 0; index < 1_000; index++)
        {
            var repeated = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
                new ExecutionPoint("test.memoized"),
                typeof(ExecutionContractTests),
                entryMethod: null,
                isBusinessOperation: true,
                transactionMode: ExecutionTransactionMode.Automatic);

            repeated.Should().BeSameAs(first);
        }
    }

    [Fact]
    public void ExecutionDescriptor_WhenDefaultDisplayNameIsUsed_ShouldReturnMemoizedInstance()
    {
        var first = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.default-name"),
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        var second = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.default-name"),
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        second.Should().BeSameAs(first);
        second.DisplayName.Should().Be($"{typeof(ExecutionContractTests).GetCleanFullName()}.test.default-name");
    }

    [Fact]
    public void ExecutionDescriptor_ForInterface_ShouldMemoizeConcreteEntryMethod()
    {
        var first = ExecutionDescriptor.ForInterface<string, ExecutionUnit>(
            new ExecutionPoint("test.interface"),
            typeof(ExplicitComponent),
            typeof(IExplicitContract),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        for (var index = 0; index < 1_000; index++)
        {
            var repeated = ExecutionDescriptor.ForInterface<string, ExecutionUnit>(
                new ExecutionPoint("test.interface"),
                typeof(ExplicitComponent),
                typeof(IExplicitContract),
                isBusinessOperation: true,
                transactionMode: ExecutionTransactionMode.Automatic);

            repeated.Should().BeSameAs(first);
        }

        first.EntryMethod.Should().NotBeNull();
        first.EntryMethod!.DeclaringType.Should().Be(typeof(ExplicitComponent));
    }

    [Fact]
    public void ExecutionDescriptor_WhenBoundaryPolicyDiffers_ShouldReturnDistinctInstances()
    {
        var point = new ExecutionPoint("test.policy");
        var automatic = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            point,
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var nonTransactional = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            point,
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.None);
        var infrastructureOperation = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            point,
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: false,
            transactionMode: ExecutionTransactionMode.Automatic);

        nonTransactional.Should().NotBeSameAs(automatic);
        infrastructureOperation.Should().NotBeSameAs(automatic);
    }

    [Fact]
    public void ExecutionDescriptor_WhenOverloadOrExecutionPointDiffers_ShouldUseDistinctOperationKeys()
    {
        var stringOverload = GetOverload(typeof(string));
        var integerOverload = GetOverload(typeof(int));

        var first = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.identity.first"),
            typeof(ExecutionContractTests),
            stringOverload,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var overloaded = ExecutionDescriptor.ForMethod<int, ExecutionUnit>(
            new ExecutionPoint("test.identity.first"),
            typeof(ExecutionContractTests),
            integerOverload,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var otherPoint = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.identity.second"),
            typeof(ExecutionContractTests),
            stringOverload,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        first.DisplayName.Should().Be(overloaded.DisplayName);
        first.OperationKey.Should().NotBe(overloaded.OperationKey);
        first.OperationKey.Should().NotBe(otherPoint.OperationKey);
    }

    [Fact]
    public void ExecutionContext_WhenCreatedForSameDescriptor_ShouldUseDistinctInvocationIds()
    {
        var descriptor = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.invocation"),
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        var first = new ExecutionContext<string>(descriptor, "first");
        var second = new ExecutionContext<string>(descriptor, "second");

        first.InvocationId.Should().NotBe(Guid.Empty);
        second.InvocationId.Should().NotBe(Guid.Empty);
        first.InvocationId.Should().NotBe(second.InvocationId);
    }

    [Fact]
    public void ExecutionDescriptor_WhenContractDiffers_ShouldUseDistinctOperationKeys()
    {
        var first = ExecutionDescriptor.ForInterface<string, ExecutionUnit>(
            new ExecutionPoint("test.contract-identity"),
            typeof(DualContractComponent),
            typeof(IFirstContract),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var second = ExecutionDescriptor.ForInterface<string, ExecutionUnit>(
            new ExecutionPoint("test.contract-identity"),
            typeof(DualContractComponent),
            typeof(ISecondContract),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        first.EntryMethod.Should().BeSameAs(second.EntryMethod);
        first.ContractType.Should().Be(typeof(IFirstContract));
        second.ContractType.Should().Be(typeof(ISecondContract));
        first.OperationKey.Should().NotBe(second.OperationKey);
    }

    [Theory]
    [InlineData(nameof(NonTransactionalOperation), ExecutionTransactionMode.Automatic, ExecutionTransactionMode.None)]
    [InlineData(nameof(AutomaticOperation), ExecutionTransactionMode.None, ExecutionTransactionMode.Automatic)]
    public void ExecutionDescriptor_WhenEntryMethodDeclaresTransactionMode_ShouldOverrideAdapterDefault(
        string methodName, ExecutionTransactionMode adapterDefault, ExecutionTransactionMode expected)
    {
        var descriptor = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.method-transaction-policy"),
            typeof(ExecutionContractTests),
            typeof(ExecutionContractTests).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static),
            isBusinessOperation: true,
            transactionMode: adapterDefault);

        descriptor.TransactionMode.Should().Be(expected);
        descriptor.TransactionDbContextTypes.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(NonTransactionalOperation))]
    [InlineData(nameof(AutomaticOperation))]
    public void ExecutionDescriptor_WhenMethodOverridesDifferentAdapterDefaults_ShouldReturnMemoizedInstance(string methodName)
    {
        var entryMethod = typeof(ExecutionContractTests).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        var point = new ExecutionPoint("test.effective-method-transaction-policy");
        var automaticDefault = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            point,
            typeof(ExecutionContractTests),
            entryMethod,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var noneDefault = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            point,
            typeof(ExecutionContractTests),
            entryMethod,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.None);

        noneDefault.Should().BeSameAs(automaticDefault);
    }

    [Fact]
    public void ExecutionDescriptor_WhenConcreteInterfaceMethodSelectsContexts_ShouldCarryOrderedSelection()
    {
        var descriptor = ExecutionDescriptor.ForInterface<string, ExecutionUnit>(
            new ExecutionPoint("test.interface-transaction-contexts"),
            typeof(TransactionalExplicitComponent),
            typeof(IExplicitContract),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.None);

        descriptor.TransactionMode.Should().Be(ExecutionTransactionMode.Automatic);
        descriptor.TransactionDbContextTypes.Should().Equal(typeof(FirstTransactionContext), typeof(SecondTransactionContext));
        descriptor.EntryMethod!.DeclaringType.Should().Be(typeof(TransactionalExplicitComponent));

        var automaticDefault = ExecutionDescriptor.ForInterface<string, ExecutionUnit>(
            new ExecutionPoint("test.interface-transaction-contexts"),
            typeof(TransactionalExplicitComponent),
            typeof(IExplicitContract),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        automaticDefault.Should().BeSameAs(descriptor);

        Action mutateSelection = () => ((IList<Type>)descriptor.TransactionDbContextTypes!)[0] = typeof(SecondTransactionContext);
        mutateSelection.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ExecutionDescriptor_WhenNoneDeclaresContextSelection_ShouldRejectContradictoryPolicy()
    {
        Action create = () => _ = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.invalid-transaction-policy"),
            typeof(ExecutionContractTests),
            typeof(ExecutionContractTests).GetMethod(nameof(InvalidTransactionOperation), BindingFlags.NonPublic | BindingFlags.Static),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        create.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(nameof(NullArrayTransactionOperation), typeof(ArgumentNullException))]
    [InlineData(nameof(NullEntryTransactionOperation), typeof(ArgumentException))]
    [InlineData(nameof(DuplicateContextsTransactionOperation), typeof(ArgumentException))]
    [InlineData(nameof(UndefinedModeTransactionOperation), typeof(ArgumentOutOfRangeException))]
    [InlineData(nameof(NoneWithIsolationOperation), typeof(InvalidOperationException))]
    [InlineData(nameof(InvalidIsolationOperation), typeof(ArgumentOutOfRangeException))]
    public void ExecutionDescriptor_WhenMethodTransactionDeclarationIsInvalid_ShouldFailBeforeExecution(
        string methodName, Type exceptionType)
    {
        Action create = () => _ = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.invalid-method-transaction-declaration"),
            typeof(ExecutionContractTests),
            typeof(ExecutionContractTests).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        Assert.Throws(exceptionType, create);
    }

    [Fact]
    public void ExecutionDescriptor_WhenAdapterDefaultIsUndefined_ShouldRejectIt()
    {
        Action create = () => _ = ExecutionDescriptor.ForMethod<string, ExecutionUnit>(
            new ExecutionPoint("test.invalid-adapter-transaction-default"),
            typeof(ExecutionContractTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: (ExecutionTransactionMode)999);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(typeof(InheritedTransactionComponent), ExecutionTransactionMode.Automatic)]
    [InlineData(typeof(OverrideTransactionComponent), ExecutionTransactionMode.None)]
    public void ExecutionDescriptor_WhenEntryMethodOverridesAnnotatedBase_ShouldInheritOrReplaceItsDeclaration(
        Type componentType, ExecutionTransactionMode expected)
    {
        var descriptor = ExecutionDescriptor.ForInterface<string, ExecutionUnit>(
            new ExecutionPoint("test.inherited-method-transaction-declaration"),
            componentType,
            typeof(IExplicitContract),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.None);

        descriptor.TransactionMode.Should().Be(expected);
        descriptor.EntryMethod!.DeclaringType.Should().Be(componentType);
        if (expected == ExecutionTransactionMode.Automatic)
        {
            descriptor.TransactionDbContextTypes.Should().Equal(typeof(FirstTransactionContext));
        }
        else
        {
            descriptor.TransactionDbContextTypes.Should().BeNull();
        }
    }

    [ExecutionTransaction(ExecutionTransactionMode.None)]
    private static void NonTransactionalOperation(string value)
    {
    }

    [ExecutionTransaction(ExecutionTransactionMode.Automatic)]
    private static void AutomaticOperation(string value)
    {
    }

    [ExecutionTransaction(ExecutionTransactionMode.None, IsolationLevel = System.Data.IsolationLevel.Serializable)]
    private static void NoneWithIsolationOperation(string value) { }

    [ExecutionTransaction(ExecutionTransactionMode.Automatic, IsolationLevel = (System.Data.IsolationLevel)12345)]
    private static void InvalidIsolationOperation(string value) { }

    [ExecutionTransaction(ExecutionTransactionMode.None, DbContextTypes = new[] { typeof(FirstTransactionContext) })]
    private static void InvalidTransactionOperation(string value)
    {
    }

    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = null!)]
    private static void NullArrayTransactionOperation(string value)
    {
    }

    [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = new[] { typeof(FirstTransactionContext), null! })]
    private static void NullEntryTransactionOperation(string value)
    {
    }

    [ExecutionTransaction(ExecutionTransactionMode.Automatic,
        DbContextTypes = new[] { typeof(FirstTransactionContext), typeof(FirstTransactionContext) })]
    private static void DuplicateContextsTransactionOperation(string value)
    {
    }

    [ExecutionTransaction((ExecutionTransactionMode)999)]
    private static void UndefinedModeTransactionOperation(string value)
    {
    }

    private static MethodInfo GetOverload(Type parameterType)
    {
        return typeof(ExecutionContractTests)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(OverloadedOperation)
                && method.GetParameters() is [{ ParameterType: var type }]
                && type == parameterType);
    }

    private static void OverloadedOperation(string value)
    {
    }

    private static void OverloadedOperation(int value)
    {
    }

    private interface IFeature;

    private interface IExplicitContract
    {
        Task ExecuteAsync(string input);
    }

    private interface IFirstContract
    {
        Task ExecuteAsync(string input);
    }

    private interface ISecondContract
    {
        Task ExecuteAsync(string input);
    }

    private sealed class ExplicitComponent : IExplicitContract
    {
        Task IExplicitContract.ExecuteAsync(string input)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TransactionalExplicitComponent : IExplicitContract
    {
        [ExecutionTransaction(ExecutionTransactionMode.Automatic,
            DbContextTypes = new[] { typeof(FirstTransactionContext), typeof(SecondTransactionContext) })]
        Task IExplicitContract.ExecuteAsync(string input)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FirstTransactionContext;

    private sealed class SecondTransactionContext;

    private abstract class BaseTransactionComponent : IExplicitContract
    {
        [ExecutionTransaction(ExecutionTransactionMode.Automatic, DbContextTypes = new[] { typeof(FirstTransactionContext) })]
        public virtual Task ExecuteAsync(string input) => Task.CompletedTask;
    }

    private sealed class InheritedTransactionComponent : BaseTransactionComponent
    {
        public override Task ExecuteAsync(string input) => Task.CompletedTask;
    }

    private sealed class OverrideTransactionComponent : BaseTransactionComponent
    {
        [ExecutionTransaction(ExecutionTransactionMode.None)]
        public override Task ExecuteAsync(string input) => Task.CompletedTask;
    }

    private sealed class DualContractComponent : IFirstContract, ISecondContract
    {
        public Task ExecuteAsync(string input)
        {
            return Task.CompletedTask;
        }
    }

    private sealed record ConcreteFeature(string Value) : IFeature;
}
