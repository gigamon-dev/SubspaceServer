using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    public enum ConfigScope
    {
        Global,
        Arena,
    }

    /// <summary>
    /// Attribute for providing help information about a config setting.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
    public class ConfigHelpAttribute : Attribute
    {
        public ConfigHelpAttribute(string section, string key, ConfigScope scope, Type type)
            : this(section, key, scope, type, null)
        {
        }

        public ConfigHelpAttribute(string section, string key, ConfigScope scope, Type type, string description)
            : this(section, key, scope, type, null, null, description)
        {
        }

        public ConfigHelpAttribute(string section, string key, ConfigScope scope, Type type, string range, string defaultValue, string description)
        {
            if (string.IsNullOrWhiteSpace(section))
                throw new ArgumentException("Cannot be null or white-space", nameof(section));

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cannot be null or white-space", nameof(key));

            Section = section;
            Key = key;
            Scope = scope;
            Type = type;
            Range = range;
            DefaultValue = defaultValue;
            Description = description;
        }

        public string Section { get; set; }
        public string Key { get; set; }
        public ConfigScope Scope { get; set; }
        public Type Type { get; set; }
        public string Range { get; set; }
        public string DefaultValue { get; set; }
        public string Description { get; set; }

    }
}
