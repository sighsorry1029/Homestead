namespace Homestead;

internal sealed class HomesteadCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    public static HomesteadCommandResult Ok(string message)
    {
        return new HomesteadCommandResult
        {
            Success = true,
            Message = message
        };
    }

    public static HomesteadCommandResult Fail(string message)
    {
        return new HomesteadCommandResult
        {
            Success = false,
            Message = message
        };
    }
}
