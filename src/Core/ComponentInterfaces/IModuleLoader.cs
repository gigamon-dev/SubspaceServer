namespace SS.Core.ComponentInterfaces
{
    public interface IModuleLoader : IComponentInterface
    {
        bool LoadModulesFromConfig(string moduleConfigFilename);
    }
}
