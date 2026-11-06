namespace Monica.ModelQuery.Models;

internal class FieldResult
{
    public static ResultJumpThisField JumpThisField()
    {
        return new ResultJumpThisField();
    }
}


/// <summary>
/// Marker result indicating that this field condition should be skipped, for example when it always evaluates to false.
/// </summary>
internal class ResultJumpThisField
{
    public override string ToString() => nameof(ResultJumpThisField);
}
