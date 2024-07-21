namespace SS.Core
{
    public delegate void ConfigChangedDelegate();
    public delegate void ConfigChangedDelegate<TState>(TState state);

    public abstract class ConfigHandle
    {
    }
}
