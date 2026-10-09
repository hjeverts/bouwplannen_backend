using System.Runtime.CompilerServices;

namespace Bouwplannen.Api.Tests;

public sealed class AssertionException(string message) : Exception(message);

/// <summary>Small set of assertions; enough for these tests without a test framework package.</summary>
public static class Assert
{
    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{expr}: verwacht <{expected}>, maar was <{actual}>");
    }

    public static void True(bool condition, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertionException($"Verwacht waar: {expr}");
    }

    public static void False(bool condition, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition) throw new AssertionException($"Verwacht onwaar: {expr}");
    }
}
