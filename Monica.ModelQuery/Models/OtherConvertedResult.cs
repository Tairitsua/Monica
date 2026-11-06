namespace Monica.ModelQuery.Models;

public class FieldResult
{
    public static ResultJumpThisField JumpThisField()
    {
        return new ResultJumpThisField();
    }
}


/// <summary>
/// Marker result indicating that this field condition should be skipped, for example when it always evaluates to false.
/// </summary>
public class ResultJumpThisField
{
    public override string ToString() => nameof(ResultJumpThisField);
}
