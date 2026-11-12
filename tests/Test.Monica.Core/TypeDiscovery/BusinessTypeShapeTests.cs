using AwesomeAssertions;
using Monica.Core.TypeDiscovery.Models;
using Xunit;

namespace Test.Monica.Core.TypeDiscovery;

public sealed class BusinessTypeShapeTests
{
    [Fact]
    public void MakeGenericType_WhenArgumentsRepeat_ShouldReuseClosedShapeWithinOwningDefinition()
    {
        var definition = new BusinessTypeShape(typeof(List<>));

        var text = definition.MakeGenericType(typeof(string));
        var number = definition.MakeGenericType(typeof(int));

        text.Type.Should().Be(typeof(List<string>));
        text.IsClosedType.Should().BeTrue();
        text.Should().BeSameAs(definition.MakeGenericType(typeof(string)));
        number.Type.Should().Be(typeof(List<int>));
        number.Should().NotBeSameAs(text);
        new BusinessTypeShape(typeof(List<>)).MakeGenericType(typeof(string)).Should().NotBeSameAs(text);
    }

    [Fact]
    public void MakeGenericType_WhenArgumentsRemainOpen_ShouldRejectConstruction()
    {
        var definition = new BusinessTypeShape(typeof(List<>));

        var construct = () => definition.MakeGenericType(typeof(List<>).GetGenericArguments());

        construct.Should().Throw<ArgumentException>().WithMessage("*must be closed*");
    }
}
