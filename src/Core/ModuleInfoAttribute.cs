using System;

namespace SS.Core
{
    /// <summary>
    /// For use on an <see cref="IModule"/> implementation to add a description.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class ModuleInfoAttribute : Attribute
    {
        public ModuleInfoAttribute(string description)
        {
            Description = description;
        }

        /// <summary>
        /// Use this to provide a description for a module.
        /// This can be include author and/or contact information.
        /// The ?modinfo command outputs this.
        /// </summary>
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

    /// <summary>
    /// The <see cref="ModuleInfoAttribute"/> for "core" (built-in) modules.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal class CoreModuleInfoAttribute : ModuleInfoAttribute
    {
        internal CoreModuleInfoAttribute()
            : base($"Subspace Server .NET")
        {
        }
    }
}
