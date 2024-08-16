using System;

namespace SS.Core
{
    public enum ConfigScope
    {
        Global,
        Arena,
    }

    /// <summary>
    /// Common interface for all ConfigHelp attributes.
    /// </summary>
    public interface IConfigHelpAttribute
    {
        /// <summary>
        /// The section of the setting.
        /// </summary>
        string Section { get; }

        /// <summary>
        /// The key of the setting.
        /// </summary>
        string Key { get; }

        /// <summary>
        /// The scope of the setting which determines the location of the file to the setting is be read from.
        /// </summary>
        /// <remarks>
        /// The "/conf" folder for <see cref="ConfigScope.Global"/>
        /// The arena folder, "/arenas/&lt;arena group name&gt;", for <see cref="ConfigScope.Arena"/>
        /// </remarks>
        ConfigScope Scope { get; }

        /// <summary>
        /// The filename of the config the setting is in.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> means use the default file name based on the scope.
        /// "global.conf" for <see cref="ConfigScope.Global"/>.
        /// "arena.conf" for <see cref="ConfigScope.Global"/>
        /// </remarks>
        string? FileName { get; }

        /// <summary>
        /// The type of setting.
        /// </summary>
        Type Type { get; }

        /// <summary>
        /// The default value of the setting.
        /// </summary>
        string? DefaultValue { get; }

        string? Min { get; }
        string? Max { get; }

        /// <summary>
        /// The range (min-max) of the setting.
        /// </summary>
        string? Range { get; }

        /// <summary>
        /// Text describing the setting.
        /// </summary>
        string? Description { get; }
    }

    /// <summary>
    /// Provides help information about a config setting.
    /// </summary>
    /// <remarks>
    /// This attribute describes the value as a number, enum, or other unmanaged type. For string settings use <see cref="ConfigHelpAttribute"/>.
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    public class ConfigHelpAttribute<T> : Attribute, IConfigHelpAttribute where T : unmanaged
    {
        public ConfigHelpAttribute(string section, string key, ConfigScope scope)
        {
            Section = section;
            Key = key;
            Scope = scope;
        }
        public ConfigHelpAttribute(string section, string key, ConfigScope scope, string fileName)
            : this(section, key, scope)
        {
            FileName = fileName;
        }

        public string Section { get; set; }
        public string Key { get; set; }
        public ConfigScope Scope { get; set; }
        public string? FileName { get; set; }
        public Type Type => typeof(T);

        private T? _default;
        public T Default
        {
            get => _default ?? default;
            set => _default = value;
        }

        private T? _min;
        public T Min
        {
            get => _min ?? default;
            set => _min = value;
        }

        private T? _max;
        public T Max
        {
            get => _max ?? default;
            set => _max = value;
        }

        public string? Description { get; set; }

        string? IConfigHelpAttribute.DefaultValue => _default?.ToString();
        string? IConfigHelpAttribute.Min => _min?.ToString();
        string? IConfigHelpAttribute.Max => _max?.ToString();

        string? IConfigHelpAttribute.Range => _min is null && _max is null ? null : $"[{_min}..{_max}]";
    }

    /// <summary>
    /// Providing help information about a config setting.
    /// </summary>
    /// <remarks>
    /// This attribute describes a setting value as a <see cref="string"/>.
    /// Use <see cref="ConfigHelpAttribute{T}"/> for numbers, enums, and other unmanaged types.
    /// </remarks>
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    public class ConfigHelpAttribute : Attribute, IConfigHelpAttribute
    {
        public ConfigHelpAttribute(string section, string key, ConfigScope scope)
        {
            Section = section;
            Key = key;
            Scope = scope;
        }

        public ConfigHelpAttribute(string section, string key, ConfigScope scope, string fileName)
            : this(section, key, scope)
        {
            FileName = fileName;
        }

        public string Section { get; set; }
        public string Key { get; set; }
        public ConfigScope Scope { get; set; }
        public string? FileName { get; set; }
        public string? Default { get; set; }
        public string? Description { get; set; }

        Type IConfigHelpAttribute.Type => typeof(string);
        
        string? IConfigHelpAttribute.DefaultValue => Default;

        string? IConfigHelpAttribute.Min => null;

        string? IConfigHelpAttribute.Max => null;

        string? IConfigHelpAttribute.Range => null;
    }

    /// <summary>
    /// Attribute that instructs the source generator to write constants from <see cref="ConfigHelpAttribute"/>.
    /// </summary>
    /// <remarks>
    /// <param name="scope"></param>
    /// <param name="fileName"></param>
    /// <param name="rootNamespace"></param>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class GenerateConfigHelpConstantsAttribute(ConfigScope scope, string? fileName) : Attribute
    {
        public ConfigScope Scope { get; } = scope;
        public string? FileName { get; } = fileName;
    }
}
