using System;
using System.Collections.Generic;
using System.Linq;

namespace SS.Core.Configuration
{
    /// <summary>
    /// A configuration "document" which is loaded from 1 or more <see cref="ConfFile"/> objects
    /// starting from a base/root .conf file.
    /// 
    /// Whereas a <see cref="ConfFile"/> tokenizes a .conf file into a line by line object model.
    /// This interprets the tokens further by first handling preprocessor directives, 
    /// and then finally into a section, property representation.
    /// </summary>
    public class ConfDocument
    {
        private readonly string name;
        private readonly IConfFileProvider fileProvider;
        private readonly IConfigLogger logger = null;

        /// <summary>
        /// The base (root) file.
        /// </summary>
        private ConfFile baseFile = null;

        /// <summary>
        /// All of the files that the document consists of.
        /// </summary>
        private readonly HashSet<ConfFile> files = new HashSet<ConfFile>();

        /// <summary>
        /// Active lines
        /// </summary>
        private readonly List<LineReference> lines = new List<LineReference>();

        /// <summary>
        /// Active Properties
        /// </summary>
        private readonly Dictionary<string, PropertyInfo> propertyDictionary = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The file currently being updated.
        /// The purpose of this is to detect changes made by a document itself to one of its files, 
        /// such that the change isn't considered to be one that requires the document to be fully reloaded.
        /// </summary>
        private ConfFile updatingFile = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfDocument"/> class.
        /// </summary>
        /// <param name="name">Name of the base .conf file.</param>
        /// <param name="fileProvider">Service that provides files by name/path..</param>
        public ConfDocument(
            string name,
            IConfFileProvider fileProvider) : this(name, fileProvider, null)
        {
        }

        /// <summary>
        /// Whether the document needs to be reloaded because one of the files it depends on has changed.
        /// </summary>
        public bool IsReloadNeeded { get; private set; } = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfDocument"/> class.
        /// </summary>
        /// <param name="name">Name of the base .conf file.</param>
        /// <param name="fileProvider">Service that provides files by name/path..</param>
        /// <param name="logger">Service for logging errors.</param>
        public ConfDocument(
            string name,
            IConfFileProvider fileProvider,
            IConfigLogger logger)
        {
            this.name = name;
            this.fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
            this.logger = logger;
        }

        public void Load()
        {
            //
            // reset data members
            //

            baseFile = null;

            foreach (var file in files)
            {
                file.Changed -= File_Changed;
            }

            files.Clear();
            lines.Clear();
            propertyDictionary.Clear();
            updatingFile = null;
            IsReloadNeeded = false;

            //
            // read and pre-process
            //

            baseFile = fileProvider.GetFile(name);
            if (baseFile == null)
            {
                logger.Log(ComponentInterfaces.LogLevel.Error, $"Failed to load base conf file '{name}'.");
                return; // can't do anything without a file to start with
            }

            AddFile(baseFile);

            using (PreprocessorReader reader = new PreprocessorReader(fileProvider, baseFile, logger))
            {
                LineReference lineReference;
                while ((lineReference = reader.ReadLine()) != null)
                {
                    RawLine rawLine = lineReference.Line;

                    if (rawLine.LineType == ConfLineType.Section
                        || rawLine.LineType == ConfLineType.Property)
                    {
                        lines.Add(lineReference);
                    }
                }

                foreach (var file in reader.ProcessedFiles)
                {
                    AddFile(file);
                }
            }

            //
            // interpret - takes the lines and determine section, key/value pairs
            //

            string currentSection = null;

            foreach (LineReference lineRef in lines)
            {
                if (lineRef.Line.LineType == ConfLineType.Section)
                {
                    RawSection rawSection = (RawSection)lineRef.Line;
                    currentSection = rawSection.Name;
                }
                else if (lineRef.Line.LineType == ConfLineType.Property)
                {
                    RawProperty rawProperty = (RawProperty)lineRef.Line;
                    string section = rawProperty.SectionOverride ?? currentSection;

                    propertyDictionary[CreatePropertyDictionaryKey(section, rawProperty.Key)] =
                        new PropertyInfo()
                        {
                            Value = rawProperty.Value,
                            PropertyReference = lineRef,
                        };
                }
            }
        }

        private void AddFile(ConfFile file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            files.Add(file);
            file.Changed += File_Changed;
        }

        private static string CreatePropertyDictionaryKey(string section, string key)
        {
            return (section != null)
                ? section + ':' + key
                : key;
        }

        private void File_Changed(object sender, EventArgs e)
        {
            if (updatingFile != sender)
            {
                IsReloadNeeded = true;
            }
        }

        public void UpdateOrAddProperty(string section, string key, string value, bool permanent, string comment = null)
        {
            //
            // try to update first
            //

            if (TryUpdateProperty(section, key, value, permanent))
                return;

            //
            // otherwise, add
            //

            if (permanent)
            {
                // try to find an existing section to insert it into
                var matchingSections =
                    from lineReference in lines
                    where lineReference.Line.LineType == ConfLineType.Section
                    let rawSection = (RawSection)lineReference.Line
                    where string.Equals(section, rawSection.Name, StringComparison.OrdinalIgnoreCase)
                    let docIndex = lines.IndexOf(lineReference)
                    where docIndex != -1 // sanity
                select (rawSection, lineReference.File, docIndex);

                if (matchingSections.Any())
                {
                    var (rawSection, file, docIndex) = matchingSections.Last();

                    // find the last property in the section, as long as it's in the same file
                    for (int i = docIndex + 1; i < lines.Count; i++)
                    {
                        if (lines[i].File != file || lines[i].Line.LineType == ConfLineType.Section)
                            break;

                        if (lines[i].Line.LineType == ConfLineType.Property)
                            docIndex = i;
                    }

                    // find the spot in the file to insert the property
                    int fileIndex = file.Lines.IndexOf(lines[docIndex].Line);
                    if (fileIndex != -1)
                    {
                        var propertyToInsert =
                            new RawProperty()
                            {
                                Key = key,
                                Value = value,
                                HasDelimiter = value != null,
                            };

                        // change the file
                        updatingFile = file;
                        // TODO: logic to also insert comment line
                        file.Lines.Insert(++fileIndex, propertyToInsert);
                        file.SetDirty();
                        updatingFile = null;

                        // add it to the lines we consider active
                        LineReference lineReference = new LineReference
                        {
                            Line = propertyToInsert,
                            File = file,
                        };

                        lines.Insert(docIndex + 1, lineReference);

                        // add it to the properties we know about
                        propertyDictionary[CreatePropertyDictionaryKey(section, key)] =
                            new PropertyInfo()
                            {
                                Value = value,
                                PropertyReference = lineReference,
                            };

                        return;
                    }
                }

                // otherwise add it to the end of the base file (effectively how ASSS saves conf changes)
                var propertyToAdd =
                    new RawProperty()
                    {
                        SectionOverride = section,
                        Key = key,
                        Value = value,
                        HasDelimiter = value != null,
                    };

                // TODO: logic to also add comment line
                baseFile.Lines.Add(propertyToAdd);

                LineReference lineRef = new LineReference
                {
                    Line = propertyToAdd,
                    File = baseFile,
                };

                lines.Add(lineRef);

                propertyDictionary[CreatePropertyDictionaryKey(section, key)] =
                    new PropertyInfo()
                    {
                        Value = value,
                        PropertyReference = lineRef
                    };
            }
        }

        public bool TryGetValue(string section, string key, out string value)
        {
            string keyString;

            if (section != null && key != null)
                keyString = section + ':' + key;
            else if (section != null)
                keyString = section;
            else if (key != null)
                keyString = key;
            else
            {
                value = default;
                return false;
            }

            if (!propertyDictionary.TryGetValue(keyString, out PropertyInfo propertyInfo))
            {
                value = default;
                return false;
            }

            value = propertyInfo.Value;
            return true;
        }

        public bool TryUpdateProperty(string section, string key, string value, bool permanent, string comment = null)
        {
            string keyString;

            if (section != null && key != null)
                keyString = section + ':' + key;
            else if (section != null)
                keyString = section;
            else if (key != null)
                keyString = key;
            else
                return false;

            if (!propertyDictionary.TryGetValue(keyString, out PropertyInfo propertyInfo))
            {
                return false;
            }

            propertyInfo.Value = value;

            if (permanent)
            {
                updatingFile = propertyInfo.PropertyReference.File;

                int fileIndex = updatingFile.Lines.IndexOf(propertyInfo.PropertyReference.Line);
                int docIndex = lines.IndexOf(propertyInfo.PropertyReference);

                if (fileIndex != -1 && docIndex != -1)
                {
                    RawProperty old = (RawProperty)propertyInfo.PropertyReference.Line;
                    RawProperty replacement = new RawProperty()
                    {
                        SectionOverride = old.SectionOverride,
                        Key = old.Key,
                        Value = value,
                        HasDelimiter = value != null || old.HasDelimiter,
                    };

                    updatingFile.Lines[fileIndex] = replacement;

                    LineReference lineReference = new LineReference
                    {
                        File = updatingFile,
                        Line = replacement,
                    };

                    lines[docIndex] = lineReference;
                    propertyInfo.PropertyReference = lineReference;

                    // TODO: logic to update comment line(s)

                    updatingFile.SetDirty();
                }

                updatingFile = null;
            }

            return true;
        }

        /// <summary>
        /// For keeping track of a property's value and what <see cref="ConfFile"/> line it came from.
        /// </summary>
        private class PropertyInfo
        {
            /// <summary>
            /// The value.  
            /// This normally is the value from <see cref="PropertyReference"/>, 
            /// but it can differ if a setting was changed to not be permanent.
            /// </summary>
            public string Value { get; set; }

            public LineReference PropertyReference { get; set; }
        }
    }
}
