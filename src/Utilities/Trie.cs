using Microsoft.Extensions.ObjectPool;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#nullable enable

namespace SS.Utilities
{
    /// <summary>
    /// An implementation of the trie data structure for storing string keys as <see cref="char"/> symbols.
    /// </summary>
    /// <remarks>
    /// This collection was designed to support ASCII characters, or rather extended ASCII.
    /// The Subspace game protocol uses a subset of Windows-1252.
    /// It is unlikely that the extended characters would be used as keys in the collection since
    /// config sections/keys, command names, and arena names are all plain ASCII.
    /// Player names are expected to be plain ASCII too, though a biller could technically assign a name with extended characters.
    /// However, since chat messages, and therefore commands, can contain extended characters,
    /// the collection needs to support searches for keys containing extended characters.
    /// Rather than limit the collection to just ASCII or extended ASCII, it supports the Unicode characters that can be represented by a single Char struct.
    /// In other words, it only supports code points in the Basic Multilingual Plane.
    /// If the Subspace game protocol were ever to be extended to support Unicode, then it might make sense to change this collection to use System.Text.Rune.
    /// </remarks>
    public class Trie : IEnumerable<ReadOnlyMemory<char>>, IReadOnlyTrie
    {
        private readonly Trie<byte> _trie;

        /// <summary>
        /// Initializes a new case sensitive instance of the <see cref="Trie"/> class.
        /// </summary>
        public Trie() : this(true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Trie"/> class.
        /// </summary>
        /// <param name="caseSensitive">Whether the trie is case sensitive.</param>
        public Trie(bool caseSensitive)
        {
            _trie = new(caseSensitive);
        }

        /// <summary>
        /// Gets the number of elements that are contained in the trie.
        /// </summary>
        public int Count => _trie.Count;

        /// <summary>
        /// Attempts to add the specified <paramref name="key"/> to the trie.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <returns><see langword="true"/> if the key was added; otherwise <see langword="false"/>.</returns>
        public bool Add(ReadOnlySpan<char> key)
        {
            return _trie.TryAdd(key, 0);
        }

        /// <summary>
        /// Removes the specified <paramref name="key"/> from the trie.
        /// </summary>
        /// <param name="key">The key of the element to remove.</param>
        /// <returns><see langword="true"/> if the element was removed; otherwise <see langword="false"/>.</returns>
        public bool Remove(ReadOnlySpan<char> key)
        {
            return _trie.Remove(key, out _);
        }

        public bool Contains(ReadOnlySpan<char> key)
        {
            return _trie.ContainsKey(key);
        }

        public Trie<byte>.KeyEnumerator StartsWith(ReadOnlySpan<char> prefix)
        {
            return new Trie<byte>.KeyEnumerator(_trie, prefix);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        public Trie<byte>.KeyEnumerator GetEnumerator()
        {
            return _trie.Keys;
        }

        IEnumerator<ReadOnlyMemory<char>> IEnumerable<ReadOnlyMemory<char>>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Removes all elements from the trie.
        /// </summary>
        public void Clear()
        {
            _trie.Clear();
        }

        /// <summary>
        /// Returns a read-only <see cref="ReadOnlyTrie"/> wrapper for the current collection.
        /// </summary>
        /// <returns>An object that acts as a read-only wrapper around the current <see cref="Trie"/>.</returns>
        public ReadOnlyTrie AsReadOnly()
        {
            return new ReadOnlyTrie(this);
        }
    }

    /// <summary>
    /// An implementation of the trie data structure for storing string keys as <see cref="char"/> symbols, with a <typeparamref name="TValue"/> value.
    /// </summary>
    /// <remarks>
    /// This collection was designed to support ASCII characters, or rather extended ASCII.
    /// The Subspace game protocol uses a subset of Windows-1252.
    /// It is unlikely that the extended characters would be used as keys in the collection since
    /// config sections/keys, command names, and arena names are all plain ASCII.
    /// Player names are expected to be plain ASCII too, though a biller could technically assign a name with extended characters.
    /// However, since chat messages, and therefore commands, can contain extended characters,
    /// the collection needs to support searches for keys containing extended characters.
    /// Rather than limit the collection to just ASCII or extended ASCII, it supports the Unicode characters that can be represented by a single Char struct.
    /// In other words, it only supports code points in the Basic Multilingual Plane.
    /// If the Subspace game protocol were ever to be extended to support Unicode, then it might make sense to change this collection to use System.Text.Rune.
    /// </remarks>
    public class Trie<TValue> : IEnumerable<(ReadOnlyMemory<char> Key, TValue? Value)>, IReadOnlyTrie<TValue>
    {
        private static readonly DefaultObjectPool<TrieNode> s_caseSensitiveTrieNodePool = new(new TrieNodePooledObjectPolicy(true), int.MaxValue);
        private static readonly DefaultObjectPool<TrieNode> s_caseInsensitiveTrieNodePool = new(new TrieNodePooledObjectPolicy(false), int.MaxValue);
        private static readonly DefaultObjectPool<EnumeratorNode> s_enumeratorNodePool = new(new EnumeratorNodePooledObjectPolicy(), int.MaxValue);

        private readonly ObjectPool<TrieNode> _trieNodePool;
        private readonly TrieNode _root;

        /// <summary>
        /// Initializes a new case sensitive instance of the <see cref="Trie{TValue}"/> class.
        /// </summary>
        public Trie() : this(true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Trie{TValue}"/> class.
        /// </summary>
        /// <param name="caseSensitive">Whether the trie is case sensitive.</param>
        public Trie(bool caseSensitive)
        {
            _trieNodePool = caseSensitive ? s_caseSensitiveTrieNodePool : s_caseInsensitiveTrieNodePool;
            _root = _trieNodePool.Get();
        }

        /// <summary>
        /// Gets the number of elements that are contained in the trie.
        /// </summary>
        public int Count { get; private set; } = 0;

        /// <summary>
        /// Gets or sets the value associated with the specified <paramref name="key"/>.
        /// </summary>
        /// <remarks>
        /// When setting, if the <paramref name="key"/> is already in the trie, the existing value is replaced with the specified value.
        /// </remarks>
        /// <param name="key">The key.</param>
        /// <returns>The value associated with the <paramref name="key"/>.</returns>
        /// <exception cref="KeyNotFoundException">The <paramref name="key"/> does not exist in the trie.</exception>
        public TValue this[ReadOnlySpan<char> key]
        {
            get
            {
                if (TryGetValue(key, out TValue? value))
                    return value;

                throw new KeyNotFoundException();
            }

            set
            {
                if (!TryAdd(key, value))
                {
                    // Replace the existing value.
                    TrieNode node = FindNode(key)!;
                    node.Value = value;
                }
            }
        }

        /// <summary>
        /// Adds the specified <paramref name="key"/> and <paramref name="value"/> to the trie.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="value">The value of the element to add.</param>
        /// <exception cref="ArgumentException">An element with the same key already exists.</exception>
        public void Add(ReadOnlySpan<char> key, TValue value)
        {
            if (!TryAdd(key, value))
                throw new ArgumentException("An element with the same key already exists.", nameof(key));
        }

        /// <summary>
        /// Attempts to add the specified <paramref name="key"/> and <paramref name="value"/> to the trie.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="value">The value of the element to add.</param>
        /// <returns><see langword="true"/> if the key/value pair was added; otherwise <see langword="false"/>.</returns>
        public bool TryAdd(ReadOnlySpan<char> key, TValue value)
        {
            ThrowIfContainsSurrogateChar(key);

            TrieNode current = _root;

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (!current.Children.TryGetValue(c, out TrieNode? node))
                {
                    node = _trieNodePool.Get();
                    current.Children.Add(c, node);
                }

                current = node;
            }

            if (current.IsLeaf)
            {
                // There is already a value.
                return false;
            }

            // Add it.
            current.IsLeaf = true;
            current.Value = value;
            Count++;
            return true;
        }

        /// <summary>
        /// Removes the element with the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key of the element to remove.</param>
        /// <param name="value">The value removed.</param>
        /// <returns><see langword="true"/> if the element was removed; otherwise <see langword="false"/>.</returns>
        public bool Remove(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
        {
            ThrowIfContainsSurrogateChar(key);

            return RemoveFromNode(_root, key, out value);


            bool RemoveFromNode(TrieNode node, ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
            {
                if (key.IsEmpty)
                {
                    if (node.IsLeaf)
                    {
                        // Remove it.
                        value = node.Value!;
                        node.Value = default;
                        node.IsLeaf = false;
                        Count--;
                        return true;
                    }
                    else
                    {
                        value = default;
                        return false;
                    }
                }

                if (!node.Children.TryGetValue(key[0], out TrieNode? child))
                {
                    value = default;
                    return false;
                }

                if (RemoveFromNode(child, key[1..], out value))
                {
                    if (!child.IsLeaf && child.Children.Count == 0)
                    {
                        node.Children.Remove(key[0]);
                        _trieNodePool.Return(child);
                    }

                    return true;
                }

                return false;
            }
        }

        public bool ContainsKey(ReadOnlySpan<char> key)
        {
            ThrowIfContainsSurrogateChar(key);

            TrieNode? node = FindNode(key);
            return node is not null && node.IsLeaf;
        }

        public bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
        {
            ThrowIfContainsSurrogateChar(key);

            TrieNode? node = FindNode(key);
            if (node is not null && node.IsLeaf)
            {
                value = node.Value!;
                return true;
            }
            else
            {
                value = default;
                return false;
            }
        }

        public Enumerator StartsWith(ReadOnlySpan<char> prefix)
        {
            return new Enumerator(this, prefix);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator<(ReadOnlyMemory<char> Key, TValue? Value)> IEnumerable<(ReadOnlyMemory<char> Key, TValue? Value)>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Gets an enumerator for accessing all of the keys in the trie.
        /// </summary>
        public KeyEnumerator Keys => new(this);

        /// <summary>
        /// Gets an enumerator for accessing all of the values in the trie.
        /// </summary>
        public ValueEnumerator Values => new(this);

        /// <summary>
        /// Removes all elements from the trie.
        /// </summary>
        public void Clear()
        {
            RemoveAllChildren(_root);

            void RemoveAllChildren(TrieNode node)
            {
                foreach (TrieNode childNode in node.Children.Values)
                {
                    RemoveAllChildren(childNode);
                    _trieNodePool.Return(childNode);
                }

                node.Children.Clear();
            }

            Count = 0;
        }

        /// <summary>
        /// Returns a read-only <see cref="ReadOnlyTrie{TValue}"/> wrapper for the current collection.
        /// </summary>
        /// <returns>An object that acts as a read-only wrapper around the current <see cref="Trie{TValue}"/>.</returns>
        public ReadOnlyTrie<TValue> AsReadOnly()
        {
            return new ReadOnlyTrie<TValue>(this);
        }

        private TrieNode? FindNode(ReadOnlySpan<char> key)
        {
            TrieNode current = _root;
            for (int i = 0; i < key.Length; i++)
            {
                if (current.Children.TryGetValue(key[i], out TrieNode? node))
                {
                    current = node;
                }
                else
                {
                    return null;
                }
            }

            return current;
        }

        private static void ThrowIfContainsSurrogateChar(ReadOnlySpan<char> value, [CallerArgumentExpression(nameof(value))] string? caller = null)
        {
            foreach (char c in value)
            {
                if (char.IsSurrogate(c))
                {
                    throw new ArgumentException("The value contained a character that was a surrogate, but the collection only supports code points in the Basic Multilingual Plane.", caller);
                }
            }
        }

        #region Types

        private class TrieNode
        {
            public readonly Dictionary<char, TrieNode> Children; // TODO: Use Rune instead of char to support all Unicode characters
            public bool IsLeaf = false;
            public TValue? Value = default;

            public TrieNode()
            {
                Children = new();
            }

            public TrieNode(IEqualityComparer<char> equalityComparer)
            {
                Children = new(equalityComparer);
            }
        }

        private class CaseInsensitiveCharEqualityComparer : IEqualityComparer<char>
        {
            public static readonly CaseInsensitiveCharEqualityComparer Instance = new();

            public bool Equals(char x, char y)
            {
                return char.ToLowerInvariant(x) == char.ToLowerInvariant(y);
            }

            public int GetHashCode([DisallowNull] char obj)
            {
                return char.ToLowerInvariant(obj);
            }
        }

        private class TrieNodePooledObjectPolicy : IPooledObjectPolicy<TrieNode>
        {
            private readonly bool _caseSensitive;

            public TrieNodePooledObjectPolicy(bool caseSensitive)
            {
                _caseSensitive = caseSensitive;
            }

            public TrieNode Create()
            {
                return _caseSensitive ? new() : new(CaseInsensitiveCharEqualityComparer.Instance);
            }

            public bool Return(TrieNode obj)
            {
                if (obj == null)
                    return false;

                obj.Children.Clear();
                obj.IsLeaf = false;
                obj.Value = default;

                return true;
            }
        }

        private class EnumeratorNode
        {
            public Dictionary<char, TrieNode>.Enumerator Enumerator;
            public EnumeratorNode? Previous;
        }

        private class EnumeratorNodePooledObjectPolicy : IPooledObjectPolicy<EnumeratorNode>
        {
            public Trie<TValue>.EnumeratorNode Create()
            {
                return new EnumeratorNode();
            }

            public bool Return(Trie<TValue>.EnumeratorNode obj)
            {
                if (obj is null)
                    return false;

                obj.Enumerator = default;
                obj.Previous = null;
                return true;
            }
        }

        public struct KeyEnumerator : IEnumerator<ReadOnlyMemory<char>>
        {
            private Enumerator _enumerator;

            internal KeyEnumerator(Trie<TValue> trie)
            {
                _enumerator = new Enumerator(trie);
            }

            internal KeyEnumerator(Trie<TValue> trie, ReadOnlySpan<char> prefix)
            {
                _enumerator = new Enumerator(trie, prefix);
            }

            public KeyEnumerator GetEnumerator() => this;

            public ReadOnlyMemory<char> Current => _enumerator.Current.Key;

            object? IEnumerator.Current => _enumerator.Current.Key;

            public void Dispose()
            {
                _enumerator.Dispose();
            }

            public bool MoveNext()
            {
                return _enumerator.MoveNext();
            }

            void IEnumerator.Reset()
            {
                throw new NotSupportedException();
            }
        }

        public struct ValueEnumerator : IEnumerator<TValue?>
        {
            private Enumerator _enumerator;

            internal ValueEnumerator(Trie<TValue> trie)
            {
                _enumerator = new Enumerator(trie);
            }

            public ValueEnumerator GetEnumerator() => this;

            public TValue? Current => _enumerator.Current.Value;

            object? IEnumerator.Current => _enumerator.Current.Value;

            public void Dispose()
            {
                _enumerator.Dispose();
            }

            public bool MoveNext()
            {
                return _enumerator.MoveNext();
            }

            void IEnumerator.Reset()
            {
                throw new NotSupportedException();
            }
        }

        public struct Enumerator : IEnumerator<(ReadOnlyMemory<char> Key, TValue? Value)>
        {
            // next
            private TrieNode? _nextNode = null;
            private EnumeratorNode? _nextEnumerator = null; // stack

            // current
            private TrieNode? _current = null;
            private char[]? _currentKeyArray = null;
            private int _currentKeyLength = 0;

            private const int MininumInitialKeyArrayLength = 16;

            /// <summary>
            /// Initializes an <see cref="Enumerator"/> over an entire Trie.
            /// </summary>
            /// <param name="trie">The trie to iterate over.</param>
            /// <exception cref="ArgumentNullException">The <paramref name="trie"/> was null.</exception>
            internal Enumerator(Trie<TValue> trie) : this(trie, ReadOnlySpan<char>.Empty)
            {
            }

            /// <summary>
            /// Initializes an <see cref="Enumerator"/> for items starting with a specified key in a Trie.
            /// </summary>
            /// <param name="trie">The trie to iterate over.</param>
            /// <param name="prefix">The prefix of the items to iterate over.</param>
            /// <exception cref="ArgumentNullException">The <paramref name="trie"/> was null.</exception>
            internal Enumerator(Trie<TValue> trie, ReadOnlySpan<char> prefix)
            {
                if (trie is null)
                    throw new ArgumentNullException(nameof(trie));

                TrieNode? node = trie.FindNode(prefix);
                if (node is not null)
                {
                    _nextNode = node;

                    _currentKeyArray = ArrayPool<char>.Shared.Rent(Math.Max(prefix.Length, MininumInitialKeyArrayLength));
                    prefix.CopyTo(_currentKeyArray);
                    _currentKeyLength = prefix.Length;
                }
            }

            public Enumerator GetEnumerator() => this;

            public (ReadOnlyMemory<char> Key, TValue? Value) Current
            {
                get
                {
                    if (_current is null || _currentKeyArray is null)
                        throw new InvalidOperationException();

                    return (new ReadOnlyMemory<char>(_currentKeyArray, 0, _currentKeyLength), _current.Value);
                }
            }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
                _nextNode = null;

                while (_nextEnumerator is not null)
                {
                    EnumeratorNode node = _nextEnumerator;
                    _nextEnumerator = _nextEnumerator.Previous;
                    node.Enumerator.Dispose();
                    s_enumeratorNodePool.Return(node);
                }

                _current = null;

                if (_currentKeyArray is not null)
                {
                    ArrayPool<char>.Shared.Return(_currentKeyArray, true);
                    _currentKeyArray = null;
                }

                _currentKeyLength = 0;
            }

            public bool MoveNext()
            {
                while (_nextNode is not null || _nextEnumerator is not null)
                {
                    if (_nextNode is not null)
                    {
                        TrieNode node = _nextNode;
                        _nextNode = null;

                        AppendEnumerator(node);

                        if (node.IsLeaf)
                        {
                            _current = node;
                            return true;
                        }
                    }

                    if (_nextEnumerator is not null)
                    {
                        ref Dictionary<char, TrieNode>.Enumerator enumerator = ref _nextEnumerator.Enumerator;
                        if (enumerator.MoveNext())
                        {
                            KeyValuePair<char, TrieNode> kvp = enumerator.Current;
                            AppendCurrentKey(kvp.Key);
                            _nextNode = kvp.Value;
                        }
                        else
                        {
                            if (_currentKeyLength > 0)
                                _currentKeyLength--;

                            enumerator.Dispose();

                            EnumeratorNode node = _nextEnumerator;
                            _nextEnumerator = _nextEnumerator.Previous;
                            s_enumeratorNodePool.Return(node);
                        }
                    }
                }

                // Cleanup
                Dispose();

                return false;
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            private void AppendEnumerator(TrieNode trieNode)
            {
                if (trieNode is null)
                    return;

                EnumeratorNode node = s_enumeratorNodePool.Get();
                node.Enumerator = trieNode.Children.GetEnumerator();
                node.Previous = _nextEnumerator;
                _nextEnumerator = node;
            }

            private void AppendCurrentKey(char c)
            {
                int length = _currentKeyLength + 1;

                if (_currentKeyArray is null)
                {
                    _currentKeyArray = ArrayPool<char>.Shared.Rent(Math.Max(length, MininumInitialKeyArrayLength));
                }
                else if (length > _currentKeyArray.Length)
                {
                    char[] replacement = ArrayPool<char>.Shared.Rent(length);
                    _currentKeyArray.CopyTo(replacement, 0);
                    ArrayPool<char>.Shared.Return(_currentKeyArray, true);
                    _currentKeyArray = replacement;
                }

                _currentKeyArray[_currentKeyLength++] = c;
            }
        }

        #endregion
    }

    /// <summary>
    /// Provides a read-only abstraction of a trie.
    /// </summary>
    public interface IReadOnlyTrie : IReadOnlyCollection<ReadOnlyMemory<char>>
    {
        /// <summary>
        /// Determines whether the trie contains the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <returns><see langword="true"/> if the trie contains an element with the specified <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
        bool Contains(ReadOnlySpan<char> key);

        /// <summary>
        /// Returns an enumerator that iterates through keys in the collection that start with a specified <paramref name="prefix"/>.
        /// </summary>
        /// <param name="prefix">The prefix of the keys to match.</param>
        /// <returns>An enumerator that can be used to iterate over the keys in the collection that start with a specified <paramref name="prefix"/>.</returns>
        Trie<byte>.KeyEnumerator StartsWith(ReadOnlySpan<char> prefix);
    }

    /// <summary>
    /// A read-only wrapper around a <see cref="Trie"/>.
    /// </summary>
    public class ReadOnlyTrie : IReadOnlyTrie
    {
        private readonly Trie _trie;

        public ReadOnlyTrie(Trie trie)
        {
            _trie = trie ?? throw new ArgumentNullException(nameof(trie));
        }

        public int Count => _trie.Count;

        public bool Contains(ReadOnlySpan<char> key)
        {
            return _trie.Contains(key);
        }

        public Trie<byte>.KeyEnumerator StartsWith(ReadOnlySpan<char> prefix)
        {
            return _trie.StartsWith(prefix);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        public Trie<byte>.KeyEnumerator GetEnumerator()
        {
            return _trie.GetEnumerator();
        }

        IEnumerator<ReadOnlyMemory<char>> IEnumerable<ReadOnlyMemory<char>>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    /// <summary>
    /// Provides a read-only abstraction of a trie.
    /// </summary>
    public interface IReadOnlyTrie<TValue> : IReadOnlyCollection<(ReadOnlyMemory<char> Key, TValue? Value)>
    {
        /// <summary>
        /// Gets the value associated with the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The value associated with the <paramref name="key"/>.</returns>
        TValue this[ReadOnlySpan<char> key] { get; }

        /// <summary>
        /// Determines whether the trie contains the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <returns><see langword="true"/> if the trie contains an element with the specified <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
        bool ContainsKey(ReadOnlySpan<char> key);

        /// <summary>
        /// Gets the value associated with the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <param name="value">The value if found. Otherwise, the <see langword="default"/> value.</param>
        /// <returns><see langword="true"/> if the trie contains an element with the specified <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
        bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value);

        /// <summary>
        /// Returns an enumerator that iterates through items in the collection that have keys that start with a specified <paramref name="prefix"/>.
        /// </summary>
        /// <param name="prefix">The prefix of the keys to match.</param>
        /// <returns>An enumerator that can be used to iterate over the items in the collection that have keys that start with a specified <paramref name="prefix"/>.</returns>
        Trie<TValue>.Enumerator StartsWith(ReadOnlySpan<char> prefix);
    }

    /// <summary>
    /// A read-only wrapper around a <see cref="Trie{TValue}"/>.
    /// </summary>
    public class ReadOnlyTrie<TValue> : IReadOnlyTrie<TValue>
    {
        private readonly Trie<TValue> _trie;

        public ReadOnlyTrie(Trie<TValue> trie)
        {
            _trie = trie ?? throw new ArgumentNullException(nameof(trie));
        }

        public TValue this[ReadOnlySpan<char> key] => _trie[key];

        public int Count => _trie.Count;

        public bool ContainsKey(ReadOnlySpan<char> key)
        {
            return _trie.ContainsKey(key);
        }

        public bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
        {
            return _trie.TryGetValue(key, out value);
        }

        public Trie<TValue>.Enumerator StartsWith(ReadOnlySpan<char> prefix)
        {
            return _trie.StartsWith(prefix);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        public Trie<TValue>.Enumerator GetEnumerator()
        {
            return _trie.GetEnumerator();
        }

        IEnumerator<(ReadOnlyMemory<char> Key, TValue? Value)> IEnumerable<(ReadOnlyMemory<char> Key, TValue? Value)>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
