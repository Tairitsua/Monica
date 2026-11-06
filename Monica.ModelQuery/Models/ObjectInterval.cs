namespace Monica.ModelQuery.Models;

public class ObjectInterval(dynamic? leftValue, dynamic? rightValue, bool? isLeftOpen, bool? isRightOpen)
{
    public dynamic? LeftValue { get; } = leftValue;
    public dynamic? RightValue { get; } = rightValue;
    public bool? IsLeftOpen { get; } = isLeftOpen;
    public bool? IsRightOpen { get; } = isRightOpen;
    public bool RightNotLimit => IsRightOpen == null && RightValue == null;
    public bool LeftNotLimit => IsLeftOpen == null && LeftValue == null;

}

