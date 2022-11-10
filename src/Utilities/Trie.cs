using Microsoft.Extensions.ObjectPool;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

#nullable enable

namespace SS.Utilities
{
    /// <summary>
    /// An implementation of the trie data structure for storing string keys as <see cref="char"/> symbols.
    /// </summary>
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

        public IEnumerator<ReadOnlyMemory<char>> GetEnumerator()
        {
            return _trie.Keys;
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
    public class Trie<TValue> : IEnumerable<(ReadOnlyMemory<char> Key, TValue? Value)>
    {
        private static readonly ObjectPool<TrieNode<TValue>> CaseSensitiveTrieNodePool = new NonTransientObjectPool<TrieNode<TValue>>(new TrieNodePooledObjectPolicy<TValue>(true));
        private static readonly ObjectPool<TrieNode<TValue>> CaseInsensitiveTrieNodePool = new NonTransientObjectPool<TrieNode<TValue>>(new TrieNodePooledObjectPolicy<TValue>(false));

        private readonly ObjectPool<TrieNode<TValue>> _trieNodePool;
        private readonly TrieNode<TValue> _root;

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
            _trieNodePool = caseSensitive ? CaseSensitiveTrieNodePool : CaseInsensitiveTrieNodePool;
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

            ThrowIfNotAsciiOrContainsControlCharacter(key);

            TrieNode<TValue> current = _root;

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (!current.Children.TryGetValue(c, out TrieNode<TValue>? node))
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

            ThrowIfNotAsciiOrContainsControlCharacter(key);

            return RemoveFromNode(_root, key, out value);


            bool RemoveFromNode(TrieNode<TValue> node, ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
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

                if (!node.Children.TryGetValue(key[0], out TrieNode<TValue>? child))
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

            ThrowIfNotAsciiOrContainsControlCharacter(key);

            TrieNode<TValue> current = _root;
            for (int i = 0; i < key.Length; i++)
            {
                if (current.Children.TryGetValue(key[i], out TrieNode<TValue>? node))
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

            ThrowIfNotAsciiOrContainsControlCharacter(key);

            TrieNode<TValue> current = _root;
            for (int i = 0; i < key.Length; i++)
            {
                if (current.Children.TryGetValue(key[i], out TrieNode<TValue>? node))
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

        // TODO: can this be made more efficient (no allocations) by using an enumerator struct?
        public IEnumerator<(ReadOnlyMemory<char> Key, TValue? Value)> GetEnumerator()
        {
            foreach (var key in EnumerateKeys(_root))
            {
                yield return key;
            }

            IEnumerable<(ReadOnlyMemory<char> Key, TValue? Value)> EnumerateKeys(TrieNode<TValue> node)
            {
                if (node.IsLeaf)
                {
                    // Figure out how many characters are in the key.
                    int charCount = 0;
                    TrieNode<TValue>? temp = node;
                    while ((temp = temp.Parent) != null)
                    {
                        charCount++;
                    }

                    char[] keyArray = ArrayPool<char>.Shared.Rent(charCount);
                    try
                    {
                        // Copy the characters to the rented array.
                        int index = charCount - 1;
                        temp = node;
                        do
                        {
                            keyArray[index--] = temp.Symbol;
                            temp = temp.Parent;
                        }
                        while (temp != null && temp.Parent != null);

                        yield return (new ReadOnlyMemory<char>(keyArray, 0, charCount), node.Value);
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(keyArray);
                    }
                }

                foreach (var childKVP in node.Children)
                {
                    foreach (var key in EnumerateKeys(childKVP.Value))
                    {
                        yield return key;
                    }
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        // TODO: can this be made more efficient (no allocations) by using an enumerator struct?
        public IEnumerator<ReadOnlyMemory<char>> Keys
        {
            get
            {
                foreach (ReadOnlyMemory<char> key in EnumerateKeys(_root))
                {
                    yield return key;
                }

                IEnumerable<ReadOnlyMemory<char>> EnumerateKeys(TrieNode<TValue> node)
                {
                    if (node.IsLeaf)
                    {
                        // Figure out how many characters are in the key.
                        int charCount = 0;
                        TrieNode<TValue>? temp = node;
                        while ((temp = temp.Parent) != null)
                        {
                            charCount++;
                        }

                        char[] keyArray = ArrayPool<char>.Shared.Rent(charCount);
                        try
                        {
                            // Copy the characters to the rented array.
                            int index = charCount - 1;
                            temp = node;
                            do
                            {
                                keyArray[index--] = temp.Symbol;
                                temp = temp.Parent;
                            }
                            while (temp != null && temp.Parent != null);

                            yield return new ReadOnlyMemory<char>(keyArray, 0, charCount);
                        }
                        finally
                        {
                            ArrayPool<char>.Shared.Return(keyArray);
                        }
                    }

                    foreach (var childKVP in node.Children)
                    {
                        foreach (var key in EnumerateKeys(childKVP.Value))
                        {
                            yield return key;
                        }
                    }
                }
            }
        }

        // TODO: can this be made more efficient (no allocations) by using an enumerator struct?
        public IEnumerable<TValue?> Values
        {
            get
            {
                foreach (var value in EnumerateValues(_root))
                {
                    yield return value;
                }

                IEnumerable<TValue?> EnumerateValues(TrieNode<TValue> node)
                {
                    if (node.IsLeaf)
                    {
                        yield return node.Value;
                    }

                    foreach (var childKVP in node.Children)
                    {
                        foreach (var value in EnumerateValues(childKVP.Value))
                        {
                            yield return value;
                        }
                    }
                }
            }
        }

        /*
        public ValueEnumerator<TValue> Values => new ValueEnumerator(this);

        public struct ValueEnumerator<TData>
        {
            private readonly Trie<TData> _trie;
            //private TrieNode<TData>

            internal ValueEnumerator(Trie<TData> trie)
            {
                _trie = trie;
            }

            public TData? Current { get; private set; }

            public bool MoveNext()
            {
            }
        }
        */

        /// <summary>
        /// Removes all elements from the trie.
        /// </summary>
        public void Clear()
        {
            RemoveAllChildren(_root);

            void RemoveAllChildren(TrieNode<TValue> node)
            {
                foreach (TrieNode<TValue> childNode in node.Children.Values)
                {
                    RemoveAllChildren(childNode);
                    _trieNodePool.Return(childNode);
                }

                node.Children.Clear();
            }

            Count = 0;
        }

        private static void ThrowIfNotAsciiOrContainsControlCharacter(ReadOnlySpan<char> value, [CallerArgumentExpression("value")] string? caller = null)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsAscii(c))
                {
                    throw new ArgumentException("Cannot contain non-ASCII characters.", caller);
                }

                if (char.IsControl(c))
                {
                    throw new ArgumentException("Cannot contain control characters.", caller);
                }
            }
        }

        #region Types

        private class TrieNode<TData>
        {
            public readonly Dictionary<char, TrieNode<TData>> Children;

            public TrieNode()
            {
                Children = new();
            }

            public TrieNode(IEqualityComparer<char> equalityComparer)
            {
                Children = new(equalityComparer);
            }

            public char Symbol;
            public TrieNode<TData>? Parent;
            public bool IsLeaf = false;
            public TData? Value = default;
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

        private class TrieNodePooledObjectPolicy<TData> : IPooledObjectPolicy<TrieNode<TData>>
        {
            private readonly bool _caseSensitive;

            public TrieNodePooledObjectPolicy(bool caseSensitive)
            {
                _caseSensitive = caseSensitive;
            }

            public TrieNode<TData> Create()
            {
                return _caseSensitive ? new() : new(CaseInsensitiveCharEqualityComparer.Instance);
            }

            public bool Return(TrieNode<TData> obj)
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

        #endregion
    }
}
