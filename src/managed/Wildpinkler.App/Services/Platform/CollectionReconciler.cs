using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Wildpinkler.App.Services;

public static class CollectionReconciler
{
    public static void Reconcile<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, TKey> keySelector,
        Action<T, T>? update = null)
        where TKey : notnull
    {
        var incomingKeys = new HashSet<TKey>();
        foreach (var item in source)
        {
            if (!incomingKeys.Add(keySelector(item)))
                throw new ArgumentException("The source collection contains duplicate keys.", nameof(source));
        }

        var existingKeys = new HashSet<TKey>();
        foreach (var item in target)
        {
            if (!existingKeys.Add(keySelector(item)))
                throw new ArgumentException("The target collection contains duplicate keys.", nameof(target));
        }

        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!incomingKeys.Contains(keySelector(target[index])))
                target.RemoveAt(index);
        }

        for (var index = 0; index < source.Count; index++)
        {
            var key = keySelector(source[index]);
            var existingIndex = -1;
            for (var search = index; search < target.Count; search++)
            {
                if (EqualityComparer<TKey>.Default.Equals(keySelector(target[search]), key))
                {
                    existingIndex = search;
                    break;
                }
            }

            if (existingIndex < 0)
                target.Insert(index, source[index]);
            else
            {
                update?.Invoke(target[existingIndex], source[index]);
                if (existingIndex != index)
                    target.Move(existingIndex, index);
            }
        }
    }
}
