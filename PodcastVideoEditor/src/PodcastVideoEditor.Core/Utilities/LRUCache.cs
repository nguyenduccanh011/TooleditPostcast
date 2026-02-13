#nullable enable
using System;
using System.Collections.Generic;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Thread-safe LRU (Least Recently Used) cache with automatic eviction.
/// Optimized for thumbnail caching with efficient memory management.
/// </summary>
public class LRUCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _dict;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly object _lock = new();

    public LRUCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be greater than 0", nameof(capacity));
        
        _capacity = capacity;
        _dict = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        _lruList = new LinkedList<CacheItem>();
    }

    /// <summary>
    /// Try to get value from cache. Returns true if found and moves item to front (most recently used).
    /// </summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_dict.TryGetValue(key, out var node))
            {
                // Move to front (most recently used)
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                
                value = node.Value.Value;
                return true;
            }
            
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Add or update value in cache. Automatically evicts least recently used item if at capacity.
    /// </summary>
    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            // Update existing item
            if (_dict.TryGetValue(key, out var existingNode))
            {
                _lruList.Remove(existingNode);
                existingNode.Value.Value = value;
                _lruList.AddFirst(existingNode);
                return;
            }

            // Evict least recently used if at capacity
            if (_dict.Count >= _capacity)
            {
                var last = _lruList.Last;
                if (last != null)
                {
                    _lruList.RemoveLast();
                    _dict.Remove(last.Value.Key);
                }
            }

            // Add new item at front (most recently used)
            var item = new CacheItem(key, value);
            var node = _lruList.AddFirst(item);
            _dict[key] = node;
        }
    }

    /// <summary>
    /// Remove specific item from cache.
    /// </summary>
    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            if (_dict.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _dict.Remove(key);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Clear all items from cache.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _dict.Clear();
            _lruList.Clear();
        }
    }

    /// <summary>
    /// Get current number of items in cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _dict.Count;
            }
        }
    }

    /// <summary>
    /// Check if key exists in cache without updating LRU order.
    /// </summary>
    public bool ContainsKey(TKey key)
    {
        lock (_lock)
        {
            return _dict.ContainsKey(key);
        }
    }

    private class CacheItem
    {
        public TKey Key { get; }
        public TValue Value { get; set; }

        public CacheItem(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }
    }
}
