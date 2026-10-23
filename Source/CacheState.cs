using System;
using System.Collections.Generic;

namespace UCHGamePerformance
{
    // Pure managed policies, also exercised by the offline harness.
    internal sealed class ArraySnapshot<T> where T : class
    {
        private T[] array = new T[0];
        internal T[] Read(List<T> live)
        {
            bool same = array.Length == live.Count;
            if (same)
                for (int i = 0; i < array.Length; i++)
                    if (!ReferenceEquals(array[i], live[i])) { same = false; break; }
            if (!same) array = live.ToArray();
            return array;
        }
    }

    internal sealed class MembershipMirror<T> where T : class
    {
        private readonly HashSet<T> set = new HashSet<T>(EqualityComparer<T>.Default);
        private int version, count;
        private bool initialized;
        internal bool Contains(List<T> list, int currentVersion, T value)
        {
            if (!initialized || version != currentVersion || count != list.Count)
            {
                set.Clear();
                for (int i = 0; i < list.Count; i++) set.Add(list[i]);
                version = currentVersion; count = list.Count; initialized = true;
            }
            return set.Contains(value);
        }
        internal void Added(List<T> list, int beforeVersion, int afterVersion, T value)
        {
            if (!initialized || version != beforeVersion || count != list.Count - 1) { initialized = false; return; }
            set.Add(value); version = afterVersion; count = list.Count;
        }
    }

    internal sealed class NestedScratch<T>
    {
        private readonly List<List<T>> levels = new List<List<T>>();
        internal int Depth { get; private set; }
        internal int Enter() { return Depth++; }
        internal List<T> Current()
        {
            if (Depth <= 0) throw new InvalidOperationException("Scratch used outside an event send");
            while (levels.Count < Depth) levels.Add(new List<T>());
            return levels[Depth - 1];
        }
        internal void Exit(int state)
        {
            if (Depth != state + 1) throw new InvalidOperationException("Event scratch nesting mismatch");
            if (state < levels.Count) levels[state].Clear();
            Depth = state;
        }
    }
}
