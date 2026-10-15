namespace CardCrafter.App.ViewModels;

/// <summary>An item in a drop-down: a value plus the text shown for it.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public static class Options
{
    public static IReadOnlyList<Option<T>> Of<T>(params (T Value, string Label)[] items) =>
        items.Select(i => new Option<T>(i.Value, i.Label)).ToList();

    public static Option<T> Find<T>(IReadOnlyList<Option<T>> list, T value) =>
        list.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? list[0];
}
