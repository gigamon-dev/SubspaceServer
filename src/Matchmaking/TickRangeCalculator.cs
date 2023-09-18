using Microsoft.Extensions.ObjectPool;
using SS.Utilities;
using System.Diagnostics;

namespace SS.Matchmaking
{
    /// <summary>
    /// Provides calculations to track multiple time ranges such that it tells how much additional time is added for each added time range, 
    /// taking into consideration the existing ranges.
    /// </summary>
    /// <remarks>
    /// The main goal of this is to assist in tracking EMP bomb shutdown times, especially in the scenario where they may overlap.
    /// This way, when calculating damage for the EMP shutdown times, no time period is counted more than once.
    /// </remarks>
    public class TickRangeCalculator
    {
        private static readonly ObjectPool<LinkedListNode<TickRange>> s_tickRangeLinkedListNodePool = 
            new DefaultObjectPool<LinkedListNode<TickRange>>(new TickRangeLinkedListNodePooledObjectPolicy(), 128);

        /// <summary>
        /// Used ranges. Sorted by start time. Ranges never overlap, they are expanded and combined as ranges are added.
        /// </summary>
        private readonly LinkedList<TickRange> _usedList = new();

        /// <summary>
        /// Adds a range. If the range overlaps with any previously added ranges, they will be merged.
        /// </summary>
        /// <param name="start">The start of the range.</param>
        /// <param name="end">The end of the range.</param>
        /// <returns>
        /// The additional # of ticks that were added. 
        /// If the range overlapped with any previously added range(s), this will only be the amount that wasn't already included.
        /// The value returned would be 0 if the range was already completely covered.
        /// </returns>
        public int Add(ServerTick start, ServerTick end)
        {
            if (start >= end)
                return 0;

            int total = 0;
            LinkedListNode<TickRange> node = _usedList.First;
            LinkedListNode<TickRange> newNode;

            while (node is not null)
            {
                ref TickRange range = ref node.ValueRef;
                if (end < range.Start)
                {
                    // No overlap. Simply add before the current node.
                    newNode = s_tickRangeLinkedListNodePool.Get();
                    newNode.ValueRef = new TickRange(start, end);
                    _usedList.AddBefore(node, newNode);
                    return end - start;
                }

                if (start <= range.End)
                {
                    // Overlaps
                    if (start < range.Start)
                    {
                        // Starts earlier than the range currently does.
                        // Expand the start of the range.
                        total = range.Start - start;
                        range.Start = start;
                    }

                    // Expand the end of the range if needed, combining any other ranges that overlap.
                    return total + ExpandRangeIfNeeded(node, end);
                }
                
                // No overlap. The new range comes after the current one.
                // Move on to the next existing range.
                node = node.Next;
            }

            newNode = s_tickRangeLinkedListNodePool.Get();
            newNode.ValueRef = new TickRange(start, end);
            _usedList.AddLast(newNode);
            return end - start;
        }

        private int ExpandRangeIfNeeded(LinkedListNode<TickRange> node, ServerTick end)
        {
            if (node is null)
                return 0;

            int total = 0;
            ref TickRange range = ref node.ValueRef;
            while (end > range.End)
            {
                LinkedListNode<TickRange> next = node.Next;
                if (next is null)
                    break; // There are no more ranges.

                ref TickRange nextRange = ref next.ValueRef;
                if (end >= nextRange.Start)
                {
                    // Overlaps the next range.
                    // Combine the next range into the current range.
                    total += nextRange.Start - range.End;
                    range.End = nextRange.End;
                    _usedList.Remove(next);
                    s_tickRangeLinkedListNodePool.Return(next);
                }
                else
                {
                    // Does not overlap with the next range.
                    break;
                }
            }

            if (end <= range.End)
            {
                // End falls within the range.
                return 0;
            }
            else
            {
                // Expand the current range.
                total += end - range.End;
                range.End = end;
                return total;
            }
        }

        public void Clear()
        {
            LinkedListNode<TickRange> node;

            while ((node = _usedList.First) is not null)
            {
                _usedList.Remove(node);
                s_tickRangeLinkedListNodePool.Return(node);
            }
        }

        #region Helper types

        private record struct TickRange(ServerTick Start, ServerTick End);

        private class TickRangeLinkedListNodePooledObjectPolicy : IPooledObjectPolicy<LinkedListNode<TickRange>>
        {
            public LinkedListNode<TickRange> Create()
            {
                return new LinkedListNode<TickRange>(default);
            }

            public bool Return(LinkedListNode<TickRange> obj)
            {
                if (obj is null)
                    return false;

                Debug.Assert(obj.List is null);
                if (obj.List is not null)
                    return false;

                obj.ValueRef = default;
                return true;
            }
        }

        #endregion
    }
}
