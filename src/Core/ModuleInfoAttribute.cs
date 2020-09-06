using System;
using System.Reflection;

namespace SS.Core
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class ModuleInfoAttribute : Attribute
    {
        public ModuleInfoAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }

        public static bool TryGetAttribute(Type type, out ModuleInfoAttribute attribute)
        {
            if (type == null)
            {
                attribute = null;
                return false;
            }

            attribute = Attribute.GetCustomAttribute(type, typeof(ModuleInfoAttribute)) as ModuleInfoAttribute;
            return attribute != null;
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal class CoreModuleInfoAttribute : ModuleInfoAttribute
    {
        internal CoreModuleInfoAttribute()
            : base($"Subspace Server .NET ({Assembly.GetExecutingAssembly().GetName().Version})")
        {
        }
    }
}
