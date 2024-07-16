using SS.Utilities;
using SS.Utilities.Collections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

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
        private readonly string _name;
        private readonly IConfFileProvider _fileProvider;
        private readonly IConfigLogger _logger = null;

        /// <summary>
        /// The base (root) file.
        /// </summary>
        private ConfFile _baseFile = null;

        /// <summary>
        /// All of the files that the document consists of.
        /// </summary>
        private readonly HashSet<ConfFile> _files = new();

        /// <summary>
        /// Active lines
        /// </summary>
        private readonly List<LineReference> _lines = new();

        /// <summary>
        /// Active settings
        /// </summary>
        private readonly Trie<SettingInfo> _settings = new(false);

        /// <summary>
        /// The file currently being updated.
        /// The purpose of this is to detect changes made by a document itself to one of its files, 
        /// such that the change isn't considered to be one that requires the document to be fully reloaded.
        /// </summary>
        private ConfFile _updatingFile = null;

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
            _name = name;
            _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
            _logger = logger;
        }

        /// <summary>
        /// Whether the document needs to be reloaded because one of the files it depends on has changed.
        /// </summary>
        public bool IsReloadNeeded { get; private set; } = false;

        /// <summary>
        /// Loads the document. Subsequent calls reload the document.
        /// </summary>
        public void Load()
        {
            //
            // reset data members
            //

            _baseFile = null;

            foreach (var file in _files)
            {
                file.Changed -= File_Changed;
            }

            _files.Clear();
            _lines.Clear();
            _settings.Clear();
            _updatingFile = null;
            IsReloadNeeded = false;

            //
            // read and pre-process
            //

            _baseFile = _fileProvider.GetFile(_name);
            if (_baseFile is null)
            {
                _logger?.Log(ComponentInterfaces.LogLevel.Error, $"Failed to load base conf file '{_name}'.");
                return; // can't do anything without a file to start with
            }

            AddFile(_baseFile);

            using (PreprocessorReader reader = new(_fileProvider, _baseFile, _logger))
            {
                LineReference lineReference;
                while ((lineReference = reader.ReadLine()) is not null)
                {
                    RawLine rawLine = lineReference.Line;

                    if (rawLine.LineType == ConfLineType.Section
                        || rawLine.LineType == ConfLineType.Property)
                    {
                        _lines.Add(lineReference);
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

            foreach (LineReference lineRef in _lines)
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

                    Settings_AddOrReplace(
                        section,
                        rawProperty.Key,
                        new SettingInfo()
                        {
                            Value = rawProperty.Value,
                            PropertyReference = lineRef,
                        });
                }
            }
        }

        private void AddFile(ConfFile file)
        {
            if (file is null)
                throw new ArgumentNullException(nameof(file));

            if (_files.Add(file))
            {
                file.Changed += File_Changed;
            }
        }

        private void File_Changed(object sender, EventArgs e)
        {
            if (_updatingFile != sender)
            {
                IsReloadNeeded = true;
            }
        }

        private void Settings_AddOrReplace(ReadOnlySpan<char> section, ReadOnlySpan<char> key, SettingInfo settingInfo)
        {
            if (section.IsEmpty && key.IsEmpty)
                throw new Exception("No section or key specified");

            if (settingInfo is null)
                throw new ArgumentNullException(nameof(settingInfo));

            Span<char> trieKey = stackalloc char[!section.IsEmpty && !key.IsEmpty ? section.Length + 1 + key.Length : (!section.IsEmpty ? section.Length : key.Length)];
            if (!section.IsEmpty && !key.IsEmpty)
            {
                bool success = trieKey.TryWrite($"{section}:{key}", out int charsWritten);
                Debug.Assert(success && charsWritten == trieKey.Length);
            }
            else if (!section.IsEmpty)
            {
                section.CopyTo(trieKey);
            }
            else
            {
                key.CopyTo(trieKey);
            }

            _settings.Remove(trieKey, out _);
            _settings.TryAdd(trieKey, settingInfo);    
        }

        private bool Settings_TryGetValue(ReadOnlySpan<char> section, ReadOnlySpan<char> key, out SettingInfo settingInfo)
        {
            if (section.IsEmpty && key.IsEmpty)
                throw new Exception("No section or key specified");

            Span<char> trieKey = stackalloc char[!section.IsEmpty && !key.IsEmpty ? section.Length + 1 + key.Length : (!section.IsEmpty ? section.Length : key.Length)];
            if (!section.IsEmpty && !key.IsEmpty)
            {
                bool success = trieKey.TryWrite($"{section}:{key}", out int charsWritten);
                Debug.Assert(success && charsWritten == trieKey.Length);
            }
            else if (!section.IsEmpty)
            {
                section.CopyTo(trieKey);
            }
            else
            {
                key.CopyTo(trieKey);
            }

            return _settings.TryGetValue(trieKey, out settingInfo);
        }

        /// <summary>
        /// Gets the value of a property.
        /// </summary>
        /// <param name="section">The section of the property to get.</param>
        /// <param name="key">The key of the property.</param>
        /// <param name="value">When this method returns, contains the value of the property if found; otherwise, null.</param>
        /// <returns><see langword="true"/> if the property was found; otherwise, <see langword="false"/>.</returns>
        public bool TryGetValue(ReadOnlySpan<char> section, ReadOnlySpan<char> key, out string value)
        {
            if (!Settings_TryGetValue(section, key, out SettingInfo settingInfo))
            {
                value = default;
                return false;
            }

            value = settingInfo.Value;
            return true;
        }

        /// <summary>
        /// Saves a copy of the document as a single standalone conf file.
        /// </summary>
        /// <param name="filePath">The complete file path to save the resulting file to.</param>
        /// <exception cref="Exception">Error writing to file.</exception>
        public void SaveAsStandaloneConf(string filePath)
        {
            ArgumentException.ThrowIfNullOrEmpty(filePath);

            if (File.Exists(filePath))
                throw new Exception("A file already exists at the specified path.");

            using StreamWriter writer = new(filePath, false, StringUtils.DefaultEncoding);
            using PreprocessorReader reader = new(_fileProvider, _baseFile, _logger);

            LineReference lineReference;
            while ((lineReference = reader.ReadLine()) is not null)
            {
                RawLine rawLine = lineReference.Line;
                rawLine.WriteTo(writer);
            }
        }

        /// <summary>
        /// Sets a property's value. An existing property will be updated, otherwise a new one will be added.
        /// </summary>
        /// <remarks>
        /// This method does not block.
        /// The appropriate underlying <see cref="ConfFile"/> will be modified for changes that are <paramref name="permanent"/>.
        /// However, this method does not save the modified <see cref="ConfFile"/> to disk.
        /// Writing dirty <see cref="ConfFile"/>s to disk is done separately.
        /// </remarks>
        /// <param name="section">The section of the property to add.</param>
        /// <param name="key">The key of the property to add.</param>
        /// <param name="value">The value of the property.</param>
        /// <param name="permanent"><see langword="true"/> if the change should be persisted to disk. <see langword="false"/> to only change it in memory.</param>
        /// <param name="comment">An optional comment for a change that is <paramref name="permanent"/>.</param>
        /// <param name="options">Options that affect how <paramref name="permanent"/> settings are saved to conf files.</param>
        public void UpdateOrAddProperty(string section, string key, string value, bool permanent, string comment = null, ModifyOptions options = ModifyOptions.None)
        {
            if (Settings_TryGetValue(section, key, out SettingInfo settingInfo))
            {
                // The setting exists, update it.
                settingInfo.Value = value;
            }
            else
            {
                // The setting does not exist yet.

                // Create the setting.
                settingInfo = new SettingInfo()
                {
                    Value = value,
                };

                // Add the setting to our in-memory collection of settings.
                Settings_AddOrReplace(section, key, settingInfo);
            }

            if (permanent)
            {
                // The setting is permanent, meaning it needs to be changed in a ConfFile.
                if (settingInfo.PropertyReference is not null
                    && (options & ModifyOptions.AppendOverrideOnly) != ModifyOptions.AppendOverrideOnly)
                {
                    // The setting has a reference to an existing ConfFile's RawProperty
                    // and we're not being told to override only.
                    // This means we can update the existing line.
                    UpdatePermanent(settingInfo, value, comment, options);
                }
                else
                {
                    // There is no reference to a ConfFile's RawProperty or we're being told override only.
                    // This means we only can add it.
                    // Add the setting as a RawProperty to the underlying conf file.
                    // This figures out which ConfFile to add it to and what line(s) to change within it.
                    settingInfo.PropertyReference = AddPermanent(section, key, value, comment, options);
                }
            }


            // Local function that adds a RawProperty to the underlying ConfFile.
            LineReference AddPermanent(string section, string key, string value, string comment, ModifyOptions options)
            {
                // First, try to add the setting to the proper section (if the section already exists).
                if ((options & ModifyOptions.AppendOverrideOnly) != ModifyOptions.AppendOverrideOnly
                    && TryAddToExistingSection(section, key, value, comment, out LineReference propertyReference))
                {
                    return propertyReference;
                }

                // Otherwise, add it to the end of the base file (effectively how ASSS saves conf changes).
                return AddToBaseAsOverride(section, key, value, comment);


                // Local function that tries to add a RawProperty to an existing section.
                bool TryAddToExistingSection(string section, string key, string value, string comment, out LineReference propertyReference)
                {
                    // Find the section.
                    int docIndex = IndexOfSection(section);
                    if (docIndex == -1)
                    {
                        // The section doesn't exist yet.
                        propertyReference = null;
                        return false;
                    }

                    ConfFile file = _lines[docIndex].File;

                    // Find the last property in the section, as long as it's in the same file.
                    for (int i = docIndex + 1; i < _lines.Count; i++)
                    {
                        if (_lines[i].File != file || _lines[i].Line.LineType == ConfLineType.Section)
                            break;

                        if (_lines[i].Line.LineType == ConfLineType.Property)
                            docIndex = i;
                    }

                    // Find the spot in the file to insert the property.
                    int fileIndex = file.Lines.IndexOf(_lines[docIndex].Line);
                    if (fileIndex == -1)
                    {
                        // Couldn't find the spot in the file to insert the property to.
                        propertyReference = null;
                        return false;
                    }

                    RawProperty propertyToInsert = new(
                        sectionOverride: null,
                        key: key,
                        value: value,
                        hasDelimiter: value is not null);

                    // Change the file
                    _updatingFile = file;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(comment))
                        {
                            file.Lines.Insert(
                                ++fileIndex,
                                new RawComment(RawComment.DefaultCommentChar, comment));
                        }

                        file.Lines.Insert(++fileIndex, propertyToInsert);
                        file.SetDirty();
                    }
                    finally
                    {
                        _updatingFile = null;
                    }

                    propertyReference = new LineReference(propertyToInsert, file);

                    // Add it to the lines we consider active.
                    _lines.Insert(docIndex + 1, propertyReference);

                    return true;


                    // Local function that finds the index of a section in _lines. -1 if not found.
                    int IndexOfSection(ReadOnlySpan<char> section)
                    {
                        for (int docIndex = _lines.Count - 1; docIndex >= 0; docIndex--)
                        {
                            LineReference lineReference = _lines[docIndex];
                            if (lineReference.Line.LineType == ConfLineType.Section
                                && lineReference.Line is RawSection rawSection
                                && section.Equals(rawSection.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                return docIndex;
                            }
                        }

                        return -1; // not found
                    }
                }

                // Local function that adds a RawProperty to the end of the base ConfFile with a section override.
                LineReference AddToBaseAsOverride(string section, string key, string value, string comment)
                {
                    RawProperty propertyToAdd = new(
                        sectionOverride: section,
                        key: key,
                        value: value,
                        hasDelimiter: value is not null);

                    _updatingFile = _baseFile;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(comment))
                        {
                            _baseFile.Lines.Add(new RawComment(RawComment.DefaultCommentChar, comment));
                        }

                        _baseFile.Lines.Add(propertyToAdd);
                        _baseFile.SetDirty();
                    }
                    finally
                    {
                        _updatingFile = null;
                    }

                    LineReference lineRef = new(propertyToAdd, _baseFile);
                    _lines.Add(lineRef);
                    return lineRef;
                }
            }

            // Local function that updates the value of an existing ConfFile's RawProperty.
            void UpdatePermanent(SettingInfo settingInfo, string value, string comment, ModifyOptions options)
            {
                _updatingFile = settingInfo.PropertyReference.File;

                try
                {
                    int fileIndex = _updatingFile.Lines.IndexOf(settingInfo.PropertyReference.Line);
                    int docIndex = _lines.IndexOf(settingInfo.PropertyReference);

                    if (fileIndex != -1 && docIndex != -1)
                    {
                        RawProperty old = (RawProperty)settingInfo.PropertyReference.Line;
                        RawProperty replacement = new(
                            sectionOverride: old.SectionOverride,
                            key: old.Key,
                            value: value,
                            hasDelimiter: value is not null || old.HasDelimiter);

                        _updatingFile.Lines[fileIndex] = replacement;

                        LineReference lineReference = new(replacement, _updatingFile);

                        _lines[docIndex] = lineReference;
                        settingInfo.PropertyReference = lineReference;

                        // update comment line(s)
                        if (!string.IsNullOrWhiteSpace(comment))
                        {
                            _updatingFile.Lines.Insert(
                                fileIndex,
                                new RawComment(RawComment.DefaultCommentChar, comment));

                            if ((options & ModifyOptions.LeaveExistingComments) != ModifyOptions.LeaveExistingComments)
                            {
                                // remove old comments (comment lines immediately before the property line)
                                int commentIndex = fileIndex;
                                while (--commentIndex >= 0 && _updatingFile.Lines[commentIndex].LineType == ConfLineType.Comment)
                                {
                                    _updatingFile.Lines.RemoveAt(commentIndex);
                                }
                            }
                        }

                        _updatingFile.SetDirty();
                    }
                }
                finally
                {
                    _updatingFile = null;
                }
            }
        }

        #region Helper types

        /// <summary>
        /// Information for one setting, including its value and optionally, what <see cref="ConfFile"/> line it is related to.
        /// </summary>
        private class SettingInfo
        {
            /// <summary>
            /// The current value of the setting.
            /// </summary>
            public string Value { get; set; }

            /// <summary>
            /// The underlying line and file that the setting is related to.
            /// <see langword="null"/> for a newly added, non-permanent setting.
            /// </summary>
            public LineReference PropertyReference { get; set; }
        }

        #endregion
    }
}
