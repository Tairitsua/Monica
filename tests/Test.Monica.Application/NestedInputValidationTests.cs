using System.ComponentModel.DataAnnotations;
using Monica.Application.Validation.Annotations;
using Xunit;

namespace Test.Monica.Application;

public sealed class NestedInputValidationTests
{
    [Fact]
    public void Validate_WhenChildrenAreValidOrCyclic_ShouldSucceed()
    {
        var input = new Input { Child = new Child { Name = "valid" } };
        input.Child.Next = input.Child;
        var errors = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(input, new ValidationContext(input), errors, true));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenChildOrCollectionItemIsInvalid_ShouldRetainFieldPaths()
    {
        var input = new Input { Child = new Child(), Children = [new Child()] };
        var errors = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(input, new ValidationContext(input), errors, true));
        var paths = errors.Cast<NestedValidationResult>().SelectMany(result => result.NestedResults).SelectMany(result => result.MemberNames);
        Assert.Contains("Child.Name", paths);
        Assert.Contains("Children[0].Name", paths);
    }

    private sealed class Input
    {
        [ValidateChildren] public Child? Child { get; set; }
        [ValidateChildren] public Child[] Children { get; set; } = [];
    }

    private sealed class Child
    {
        [Required] public string? Name { get; set; }
        [ValidateChildren] public Child? Next { get; set; }
    }
}
