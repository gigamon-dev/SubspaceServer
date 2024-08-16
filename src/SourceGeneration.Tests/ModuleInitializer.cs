using System.Runtime.CompilerServices;

namespace SS.SourceGeneration.Tests
{
    public class ModuleInitializer
    {
        [ModuleInitializer]
        public static void Init()
        {
            VerifySourceGenerators.Initialize();
        }
    }
}
