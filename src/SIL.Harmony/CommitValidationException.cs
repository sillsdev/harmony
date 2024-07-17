namespace SIL.Harmony;

public class CommitValidationException: Exception
{
    public CommitValidationException(string message) : base(message)
    {
    }
}