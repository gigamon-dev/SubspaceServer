using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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
        /// Active Properties
        /// </summary>
        private readonly Trie<PropertyInfo> _propertyLookup = new(false);

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
            _propertyLookup.Clear();
            _updatingFile = null;
            IsReloadNeeded = false;

            //
            // read and pre-process
            //

            _baseFile = _fileProvider.GetFile(_name);
            if (_baseFile == null)
            {
                _logger?.Log(ComponentInterfaces.LogLevel.Error, $"Failed to load base conf file '{_name}'.");
                return; // can't do anything without a file to start with
            }

            AddFile(_baseFile);

            using (PreprocessorReader reader = new(_fileProvider, _baseFile, _logger))
            {
                LineReference lineReference;
                while ((lineReference = reader.ReadLine()) != null)
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

                    PropertyLookup_AddOrReplace(
                        section,
                        rawProperty.Key,
                        new PropertyInfo()
                        {
                            Value = rawProperty.Value,
                            PropertyReference = lineRef,
                        });
                }
            }
        }

        private void AddFile(ConfFile file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            _files.Add(file);
            file.Changed += File_Changed;
        }

        private void File_Changed(object sender, EventArgs e)
        {
            if (_updatingFile != sender)
            {
                IsReloadNeeded = true;
            }
        }

        private void PropertyLookup_AddOrReplace(ReadOnlySpan<char> section, ReadOnlySpan<char> key, PropertyInfo propertyInfo)
        {
            if (section.IsEmpty && key.IsEmpty)
                throw new Exception("No section or key specified");

            if (propertyInfo is null)
                throw new ArgumentNullException(nameof(propertyInfo));

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

            _propertyLookup.Remove(trieKey, out _);
            _propertyLookup.TryAdd(trieKey, propertyInfo);    
        }

        private bool PropertyLookup_TryGetValue(ReadOnlySpan<char> section, ReadOnlySpan<char> key, out PropertyInfo propertyInfo)
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

            return _propertyLookup.TryGetValue(trieKey, out propertyInfo);
        }

        /// <summary>
        /// Sets a property's value. An existing property will be updated, otherwise a new one will be added.
        /// </summary>
        /// <param name="section">The section of the property to add.</param>
        /// <param name="key">The key of the property to add.</param>
        /// <param name="value">The value of the property.</param>
        /// <param name="permanent"><see langword="true"/> if the change should be persisted to disk. <see langword="false"/> to only change it in memory.</param>
        /// <param name="comment">An optional comment for a change that is <paramref name="permanent"/>.</param>
        public void UpdateOrAddProperty(string section, string key, string value, bool permanent, string comment = null)
        {
            //
            // try to update first
            //

            if (TryUpdateProperty(section, key, value, permanent, comment))
                return;

            //
            // otherwise, add
            //

            if (permanent)
            {
                // try to find an existing section to insert it into
                (ConfFile file, int docIndex)? lastMatchingSection = null;

                foreach (LineReference lineReference in _lines)
                {
                    if (lineReference.Line.LineType != ConfLineType.Section
                        || lineReference.Line is not RawSection rawSection
                        || !string.Equals(section, rawSection.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int docIndex = _lines.IndexOf(lineReference);
                    if (docIndex == -1) // sanity
                    {
                        continue;
                    }

                    lastMatchingSection = (lineReference.File, docIndex);
                }

                if (lastMatchingSection != null)
                {
                    var (file, docIndex) = lastMatchingSection.Value;

                    // find the last property in the section, as long as it's in the same file
                    for (int i = docIndex + 1; i < _lines.Count; i++)
                    {
                        if (_lines[i].File != file || _lines[i].Line.LineType == ConfLineType.Section)
                            break;

                        if (_lines[i].Line.LineType == ConfLineType.Property)
                            docIndex = i;
                    }

                    // find the spot in the file to insert the property
                    int fileIndex = file.Lines.IndexOf(_lines[docIndex].Line);
                    if (fileIndex != -1)
                    {
                        var propertyToInsert =
                            new RawProperty(
                                sectionOverride: null, 
                                key: key, 
                                value: value,
                                hasDelimiter: value != null);

                        // change the file
                        _updatingFile = file;

                        if (!string.IsNullOrWhiteSpace(comment))
                        {
                            file.Lines.Insert(
                                ++fileIndex,
                                new RawComment(RawComment.DefaultCommentChar, comment));
                        }

                        file.Lines.Insert(++fileIndex, propertyToInsert);
                        file.SetDirty();

                        _updatingFile = null;

                        // add it to the lines we consider active
                        LineReference lineReference = new()
                        {
                            Line = propertyToInsert,
                            File = file,
                        };

                        _lines.Insert(docIndex + 1, lineReference);

                        // add it to the properties we know about
                        PropertyLookup_AddOrReplace(
                            section,
                            key,
                            new PropertyInfo()
                            {
                                Value = value,
                                PropertyReference = lineReference,
                            });

                        return;
                    }
                }

                // otherwise add it to the end of the base file (effectively how ASSS saves conf changes)
                var propertyToAdd =
                    new RawProperty(
                        sectionOverride: section,
                        key: key,
                        value: value,
                        hasDelimiter: value != null);

                _updatingFile = _baseFile;

                if (!string.IsNullOrWhiteSpace(comment))
                {
                    _baseFile.Lines.Add(new RawComment(RawComment.DefaultCommentChar, comment));
                }

                _baseFile.Lines.Add(propertyToAdd);
                _baseFile.SetDirty();
                
                _updatingFile = null;

                LineReference lineRef = new()
                {
                    Line = propertyToAdd,
                    File = _baseFile,
                };

                _lines.Add(lineRef);

                PropertyLookup_AddOrReplace(
                    section,
                    key,
                    new PropertyInfo()
                    {
                        Value = value,
                        PropertyReference = lineRef
                    });
            }
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
            if (!PropertyLookup_TryGetValue(section, key, out PropertyInfo propertyInfo))
            {
                value = default;
                return false;
            }

            value = propertyInfo.Value;
            return true;
        }

        /// <summary>
        /// Updates the value of an existing property.
        /// </summary>
        /// <param name="section">The section of the property to update.</param>
        /// <param name="key">The key of the property to update.</param>
        /// <param name="value">The value to set the property to.</param>
        /// <param name="permanent"><see langword="true"/> if the change should be persisted to disk. <see langword="false"/> to only change it in memory.</param>
        /// <param name="comment">An optional comment for a change that is <paramref name="permanent"/>.</param>
        /// <returns>True if the property was found and updated.  Otherwise, false.</returns>
        public bool TryUpdateProperty(ReadOnlySpan<char> section, ReadOnlySpan<char> key, string value, bool permanent, string comment = null)
        {
            if (!PropertyLookup_TryGetValue(section, key, out PropertyInfo propertyInfo))
            {
                return false;
            }

            propertyInfo.Value = value;

            if (permanent)
            {
                _updatingFile = propertyInfo.PropertyReference.File;

                int fileIndex = _updatingFile.Lines.IndexOf(propertyInfo.PropertyReference.Line);
                int docIndex = _lines.IndexOf(propertyInfo.PropertyReference);

                if (fileIndex != -1 && docIndex != -1)
                {
                    RawProperty old = (RawProperty)propertyInfo.PropertyReference.Line;
                    RawProperty replacement = new(
                        sectionOverride: old.SectionOverride,
                        key: old.Key,
                        value: value,
                        hasDelimiter: value != null || old.HasDelimiter);

                    _updatingFile.Lines[fileIndex] = replacement;

                    LineReference lineReference = new()
                    {
                        File = _updatingFile,
                        Line = replacement,
                    };

                    _lines[docIndex] = lineReference;
                    propertyInfo.PropertyReference = lineReference;

                    // update comment line(s)
                    if (!string.IsNullOrWhiteSpace(comment))
                    {
                        _updatingFile.Lines.Insert(
                            fileIndex,
                            new RawComment(RawComment.DefaultCommentChar, comment));

                        // remove old comments (comment lines immediately before the property line)
                        int commentIndex = fileIndex;
                        while (--commentIndex >= 0 && _updatingFile.Lines[commentIndex].LineType == ConfLineType.Comment)
                        {
                            _updatingFile.Lines.RemoveAt(commentIndex);
                        }
                    }

                    _updatingFile.SetDirty();
                }

                _updatingFile = null;
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
