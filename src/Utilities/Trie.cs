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
    public class Trie : IEnumerable<ReadOnlyMemory<char>>
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

        /// <summary>
        /// Determines whether the trie contains the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <returns><see langword="true"/> if the trie contains an element with the specified <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
        public bool Contains(ReadOnlySpan<char> key)
        {
            return _trie.ContainsKey(key);
        }

        // TODO:
        //public TrieEnumerator StartsWith(ReadOnlySpan<char> keyStart)
        //{
        //}

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
    public class Trie<TValue> : IEnumerable<(ReadOnlyMemory<char> Key, TValue? Value)>
    {
        private static readonly ObjectPool<TrieNode> s_caseSensitiveTrieNodePool = new NonTransientObjectPool<TrieNode>(new TrieNodePooledObjectPolicy(true));
        private static readonly ObjectPool<TrieNode> s_caseInsensitiveTrieNodePool = new NonTransientObjectPool<TrieNode>(new TrieNodePooledObjectPolicy(false));
        private static readonly ObjectPool<LinkedNode> s_linkedNodePool = new NonTransientObjectPool<LinkedNode>(new LinkedNodePooledObjectPolicy());

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
                    // TODO: Change to just replace into the existing ndoe instead of removing and then re-adding.
                    Remove(key, out _);
                    TryAdd(key, value);
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
            if (key.IsEmpty)
                return false;

            ThrowIfContainsSurrogateChar(key);

            TrieNode current = _root;

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (!current.Children.TryGetValue(c, out TrieNode? node))
                {
                    node = _trieNodePool.Get();
                    node.Symbol = c;
                    node.Parent = current;
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
            if (key.IsEmpty)
            {
                value = default;
                return false;
            }

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
                        child.Parent = null;
                        child.Symbol = default;
                        _trieNodePool.Return(child);
                    }

                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Determines whether the trie contains the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <returns><see langword="true"/> if the trie contains an element with the specified <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
        public bool ContainsKey(ReadOnlySpan<char> key)
        {
            if (key.IsEmpty)
            {
                return false;
            }

            ThrowIfContainsSurrogateChar(key);

            TrieNode current = _root;
            for (int i = 0; i < key.Length; i++)
            {
                if (current.Children.TryGetValue(key[i], out TrieNode? node))
                {
                    current = node;
                }
                else
                {
                    return false;
                }
            }

            return current.IsLeaf;
        }

        /// <summary>
        /// Gets the value associated with the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <param name="value">The value if found. Otherwise, the <see langword="default"/> value.</param>
        /// <returns><see langword="true"/> if the trie contains an element with the specified <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
        public bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
        {
            if (key.IsEmpty)
            {
                value = default;
                return false;
            }

            ThrowIfContainsSurrogateChar(key);

            TrieNode current = _root;
            for (int i = 0; i < key.Length; i++)
            {
                if (current.Children.TryGetValue(key[i], out TrieNode? node))
                {
                    current = node;
                }
                else
                {
                    value = default;
                    return false;
                }
            }

            if (current.IsLeaf)
            {
                value = current.Value!;
                return true;
            }
            else
            {
                value = default;
                return false;
            }
        }

        // TODO:
        //public TrieEnumerator<TData> StartsWith(ReadOnlySpan<char> keyStart)
        //{
        //}

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
            public readonly Dictionary<char, TrieNode> Children;

            public TrieNode()
            {
                Children = new();
            }

            public TrieNode(IEqualityComparer<char> equalityComparer)
            {
                Children = new(equalityComparer);
            }

            public char Symbol;
            public TrieNode? Parent;
            public bool IsLeaf = false;
            public TValue? Value = default;
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
                obj.Symbol = default;
                obj.Parent = null;
                obj.IsLeaf = false;
                obj.Value = default;

                return true;
            }
        }

        private class LinkedNode
        {
            public TrieNode? TrieNode { get; set; }
            public LinkedNode? Next { get; set; }
        }

        private class LinkedNodePooledObjectPolicy : IPooledObjectPolicy<LinkedNode>
        {
            public Trie<TValue>.LinkedNode Create()
            {
                return new LinkedNode();
            }

            public bool Return(Trie<TValue>.LinkedNode obj)
            {
                if (obj is null)
                    return false;

                obj.TrieNode = null;
                obj.Next = null;
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
            private readonly Trie<TValue> _trie;
            private bool _started = false;

            // stack implemented as a linked list
            private LinkedNode? _first = null;
            private LinkedNode? _last = null;

            // current
            private LinkedNode? _current = null;
            private char[]? _currentKeyArray = null;
            private int _currentKeyLength = 0;

            internal Enumerator(Trie<TValue> trie)
            {
                _trie = trie ?? throw new ArgumentNullException(nameof(trie));
            }

            public Enumerator GetEnumerator() => this;

            public (ReadOnlyMemory<char> Key, TValue? Value) Current
            {
                get
                {
                    if (_current is null)
                        throw new InvalidOperationException();

                    // Figure out how many characters are in the key.
                    int charCount = 0;
                    TrieNode? temp = _current.TrieNode!;
                    while ((temp = temp.Parent) != null)
                    {
                        charCount++;
                    }

                    // Make sure we have an array large enough to hold it.
                    if (_currentKeyArray is null || _currentKeyArray.Length < charCount)
                    {
                        if (_currentKeyArray is not null)
                        {
                            ArrayPool<char>.Shared.Return(_currentKeyArray);
                        }

                        _currentKeyArray = ArrayPool<char>.Shared.Rent(charCount);
                    }

                    // Copy the characters to the rented array.
                    int index = charCount - 1;
                    temp = _current.TrieNode!;
                    do
                    {
                        _currentKeyArray[index--] = temp.Symbol;
                        temp = temp.Parent;
                    }
                    while (temp != null && temp.Parent != null);

                    _currentKeyLength = charCount;

                    return (new ReadOnlyMemory<char>(_currentKeyArray, 0, _currentKeyLength), _current.TrieNode!.Value);
                }
            }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (!_started)
                {
                    _started = true;
                    _last = _first = Trie<TValue>.s_linkedNodePool.Get();
                    _first.TrieNode = _trie._root;
                    _first.Next = null;
                }

                if (_first is null)
                {
                    return false;
                }

                while (_first is not null)
                {
                    LinkedNode current = _first;

                    // Add child nodes to the end.
                    foreach (TrieNode child in current.TrieNode!.Children.Values)
                    {
                        LinkedNode childLink = Trie<TValue>.s_linkedNodePool.Get();
                        childLink.TrieNode = child;
                        childLink.Next = null;
                        _last!.Next = childLink;
                        _last = childLink;
                    }

                    // Remove the first node (the one we're working on).
                    _first = _first.Next;
                    if (_first is null)
                    {
                        _last = null;
                    }

                    if (current.TrieNode.IsLeaf)
                    {
                        if (_current is not null)
                        {
                            Trie<TValue>.s_linkedNodePool.Return(_current);
                        }

                        _current = current;
                        return true;
                    }
                }

                return false;
            }

            public void Dispose()
            {
                while (_first is not null)
                {
                    LinkedNode? next = _first.Next;
                    Trie<TValue>.s_linkedNodePool.Return(_first);
                    _first = next;
                }

                if (_current is not null)
                {
                    Trie<TValue>.s_linkedNodePool.Return(_current);
                    _current = null;
                }

                if (_currentKeyArray is not null)
                {
                    ArrayPool<char>.Shared.Return(_currentKeyArray);
                    _currentKeyArray = null;
                }
            }

            void IEnumerator.Reset()
            {
                throw new NotSupportedException();
            }
        }

        #endregion
    }
}
