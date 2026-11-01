using System.Collections.ObjectModel;
using System.Collections.Specialized;
using TeaChest.ViewModels;

namespace TeaChest.Tests;

public sealed class CollectionDiffTests
{
    private sealed class Row
    {
        public Row(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }
        public string Value { get; }
    }

    private sealed record Result(
        ObservableCollection<Row> Target,
        List<Row> Added,
        List<Row> Removed,
        List<NotifyCollectionChangedAction> Actions);

    private static Result Run(IEnumerable<Row> current, IEnumerable<Row> next)
    {
        var target = new ObservableCollection<Row>(current);
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);

        var added = new List<Row>();
        var removed = new List<Row>();
        CollectionDiff.Apply(
            target,
            next.ToList(),
            row => row.Key,
            (currentRow, nextRow) => currentRow.Key == nextRow.Key && currentRow.Value == nextRow.Value,
            added.Add,
            removed.Add);

        return new Result(target, added, removed, actions);
    }

    private static Row[] Rows(params string[] keys)
        => keys.Select(key => new Row(key, "value-" + key)).ToArray();

    [Fact]
    public void IdenticalInputRaisesNoEvents()
    {
        Row[] rows = Rows("a", "b", "c");

        Result result = Run(rows, rows);

        Assert.Equal(rows, result.Target);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        Assert.Empty(result.Actions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AddsAtAnyPosition(int index)
    {
        Row[] rows = Rows("a", "b", "c");
        var extra = new Row("x", "value-x");
        List<Row> next = rows.ToList();
        next.Insert(index, extra);

        Result result = Run(rows, next);

        Assert.Equal(next, result.Target);
        Assert.Equal(new[] { extra }, result.Added);
        Assert.Empty(result.Removed);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
        Assert.Equal(1, result.Actions.Count(a => a == NotifyCollectionChangedAction.Add));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RemovesFromAnyPosition(int index)
    {
        Row[] rows = Rows("a", "b", "c");
        List<Row> next = rows.ToList();
        Row removed = next[index];
        next.RemoveAt(index);

        Result result = Run(rows, next);

        Assert.Equal(next, result.Target);
        Assert.Empty(result.Added);
        Assert.Equal(new[] { removed }, result.Removed);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
        Assert.Equal(1, result.Actions.Count(a => a == NotifyCollectionChangedAction.Remove));
    }

    [Fact]
    public void ReversalEndsInTheNewOrder()
    {
        Row[] rows = Rows("a", "b", "c", "d");
        Row[] next = rows.Reverse().ToArray();

        Result result = Run(rows, next);

        Assert.Equal(next, result.Target);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        Assert.Contains(NotifyCollectionChangedAction.Move, result.Actions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void ShuffleEndsInTheNewOrder()
    {
        Row[] rows = Rows("a", "b", "c", "d", "e", "f");
        Row[] next = { rows[4], rows[2], rows[0], rows[5], rows[1], rows[3] };

        Result result = Run(rows, next);

        Assert.Equal(next, result.Target);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void ChangedItemIsReplacedAtItsPosition()
    {
        Row[] rows = Rows("a", "b", "c");
        var changed = new Row("b", "new value");
        Row[] next = { rows[0], changed, rows[2] };

        Result result = Run(rows, next);

        Assert.Same(rows[0], result.Target[0]);
        Assert.Same(changed, result.Target[1]);
        Assert.Same(rows[2], result.Target[2]);
        Assert.DoesNotContain(rows[1], result.Target);
        Assert.Equal(new[] { changed }, result.Added);
        Assert.Equal(new[] { rows[1] }, result.Removed);
        Assert.Contains(NotifyCollectionChangedAction.Replace, result.Actions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void UnchangedItemsKeepTheirInstances()
    {
        Row[] rows = Rows("a", "b", "c");
        Row[] next = Rows("a", "b", "c");

        Result result = Run(rows, next);

        Assert.Same(rows[0], result.Target[0]);
        Assert.Same(rows[1], result.Target[1]);
        Assert.Same(rows[2], result.Target[2]);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void UnchangedItemsKeepTheirInstancesWhenTheOrderChanges()
    {
        Row[] rows = Rows("a", "b", "c");
        Row[] next = Rows("c", "a", "b");

        Result result = Run(rows, next);

        Assert.Same(rows[2], result.Target[0]);
        Assert.Same(rows[0], result.Target[1]);
        Assert.Same(rows[1], result.Target[2]);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void DuplicateKeysAreTheirOwnIdentities()
    {
        var first = new Row("dup", "1");
        var second = new Row("dup", "2");
        var third = new Row("dup", "3");

        Result result = Run(new[] { first, second }, new[] { new Row("dup", "1"), new Row("dup", "2"), third });

        Assert.Same(first, result.Target[0]);
        Assert.Same(second, result.Target[1]);
        Assert.Same(third, result.Target[2]);
        Assert.Equal(new[] { third }, result.Added);
        Assert.Empty(result.Removed);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void DuplicateKeysCanBeAddedFromNothing()
    {
        var rows = new[] { new Row("dup", "1"), new Row("dup", "2"), new Row("dup", "3") };

        Result result = Run(Array.Empty<Row>(), rows);

        Assert.Equal(rows, result.Target);
        Assert.Equal(rows, result.Added);
        Assert.Empty(result.Removed);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void DuplicateKeysCanBeRemoved()
    {
        var first = new Row("dup", "1");
        var second = new Row("dup", "2");
        var third = new Row("dup", "3");

        Result result = Run(
            new[] { first, second, third },
            new[] { new Row("dup", "1"), new Row("dup", "2") });

        Assert.Same(first, result.Target[0]);
        Assert.Same(second, result.Target[1]);
        Assert.Equal(new[] { third }, result.Removed);
        Assert.Empty(result.Added);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void CallbacksFireOnceForEachRealAddAndRemove()
    {
        Row[] rows = Rows("a", "b", "c");
        var x = new Row("x", "value-x");
        var y = new Row("y", "value-y");
        var changed = new Row("b", "new value");

        Result result = Run(rows, new[] { x, changed, y });

        Assert.Equal(
            new[] { x, changed, y }.OrderBy(row => row.Key),
            result.Added.OrderBy(row => row.Key));
        Assert.Equal(
            new[] { rows[0], rows[1], rows[2] }.OrderBy(row => row.Key),
            result.Removed.OrderBy(row => row.Key));
        Assert.Equal(3, result.Added.Count);
        Assert.Equal(3, result.Removed.Count);
    }

    [Fact]
    public void CallbacksFireOncePerAddedAndRemovedInstance()
    {
        Row[] rows = Rows("a", "b", "c");
        var x = new Row("x", "value-x");
        var y = new Row("y", "value-y");

        Result result = Run(rows, new[] { x, rows[1], y });

        Assert.Equal(new[] { x, y }, result.Added);
        Assert.Equal(
            new[] { rows[0], rows[2] }.OrderBy(row => row.Key),
            result.Removed.OrderBy(row => row.Key));
        Assert.Equal(2, result.Added.Count);
        Assert.Equal(2, result.Removed.Count);
    }

    [Fact]
    public void EmptyInputRemovesEverything()
    {
        Row[] rows = Rows("a", "b");

        Result result = Run(rows, Array.Empty<Row>());

        Assert.Empty(result.Target);
        Assert.Equal(
            rows.OrderBy(row => row.Key),
            result.Removed.OrderBy(row => row.Key));
        Assert.Empty(result.Added);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
    }

    [Fact]
    public void RandomisedRoundsEndEqualToNext()
    {
        var random = new Random(20261101);
        string[] pool = Enumerable.Range(0, 10).Select(i => "k" + i).ToArray();

        for (int round = 0; round < 200; round++)
        {
            List<string> targetKeys = Pick(random, pool);
            List<string> nextKeys = Pick(random, pool);

            var current = targetKeys.Select(key => new Row(key, "value-" + key)).ToList();
            var next = new List<Row>();
            int changedCount = 0;
            foreach (string key in nextKeys)
            {
                bool changed = targetKeys.Contains(key) && random.Next(4) == 0;
                if (changed) changedCount++;
                next.Add(new Row(key, changed ? "changed-" + key : "value-" + key));
            }

            Result result = Run(current, next);

            Assert.Equal(
                next.Select(row => (row.Key, row.Value)),
                result.Target.Select(row => (row.Key, row.Value)));
            Assert.Equal(nextKeys.Except(targetKeys).Count() + changedCount, result.Added.Count);
            Assert.Equal(targetKeys.Except(nextKeys).Count() + changedCount, result.Removed.Count);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, result.Actions);
        }
    }

    private static List<string> Pick(Random random, string[] pool)
        => pool.OrderBy(_ => random.Next()).Take(random.Next(0, pool.Length + 1)).ToList();
}
