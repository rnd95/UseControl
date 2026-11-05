using System.Reflection;

namespace UseControl.Core.Tests;

public class ErrorMessagesTests
{
    [Fact]
    public void Every_error_code_has_an_English_text()
    {
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!);
        Assert.All(codes, c => Assert.Contains(c, ErrorMessages.AllCodes));
    }

    [Fact]
    public void Exception_keeps_code_and_arguments_for_translation()
    {
        var e = new UcException(ErrorCodes.AlreadyInList, @"D:\X");
        Assert.Equal(ErrorCodes.AlreadyInList, e.Code);
        Assert.Equal([@"D:\X"], e.Args);
        Assert.Equal(@"D:\X is already in the list.", e.Message);
    }
}
