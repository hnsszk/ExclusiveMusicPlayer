namespace ExclusiveMusicPlayer.Services;

public sealed class NeteaseApiException : Exception
{
    public NeteaseApiException(string message)
        : base(message)
    {
    }

    public NeteaseApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
