using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace OmniRelay.TestSupport.Assertions;

public enum Case
{
    Sensitive,
    Insensitive
}

public static class AwesomeAssertionExtensions
{
    public static void ShouldBe<T>(this IEnumerable<T> actual, IEnumerable<T> expected, string? because = null)
    {
        actual.Should().Equal(expected, because);
    }

    public static void ShouldBe(this double actual, double expected, double precision, string? because = null)
    {
        actual.Should().BeApproximately(expected, precision, because);
    }

    public static void ShouldBe<T>(this T actual, T expected, string? because = null)
    {
        if (actual is IEnumerable actualEnumerable &&
            expected is IEnumerable expectedEnumerable &&
            actual is not string &&
            expected is not string)
        {
            actualEnumerable.Cast<object?>().Should().Equal(expectedEnumerable.Cast<object?>(), because);
            return;
        }

        actual.Should().Be(expected, because);
    }

    public static void ShouldNotBe<T>(this T actual, T expected, string? because = null)
    {
        actual.Should().NotBe(expected, because);
    }

    public static void ShouldBeSameAs<T>(this T actual, T expected, string? because = null)
        where T : class?
    {
        actual.Should().BeSameAs(expected, because);
    }

    public static void ShouldNotBeSameAs<T>(this T actual, T expected, string? because = null)
        where T : class?
    {
        actual.Should().NotBeSameAs(expected, because);
    }

    public static void ShouldBeTrue(this bool actual, string? because = null)
    {
        actual.Should().BeTrue(because);
    }

    public static void ShouldBeFalse(this bool actual, string? because = null)
    {
        actual.Should().BeFalse(because);
    }

    public static void ShouldBeNull(this object? actual, string? because = null)
    {
        actual.Should().BeNull(because);
    }

    public static void ShouldNotBeNull(this object? actual, string? because = null)
    {
        actual.Should().NotBeNull(because);
    }

    public static void ShouldBeNullOrWhiteSpace(this string? actual, string? because = null)
    {
        actual.Should().BeNullOrWhiteSpace(because);
    }

    public static void ShouldNotBeNullOrWhiteSpace(this string? actual, string? because = null)
    {
        actual.Should().NotBeNullOrWhiteSpace(because);
    }

    public static void ShouldBeNullOrEmpty(this string? actual, string? because = null)
    {
        actual.Should().BeNullOrEmpty(because);
    }

    public static void ShouldBeNullOrEmpty<T>(this IEnumerable<T>? actual, string? because = null)
    {
        actual.Should().BeNullOrEmpty(because);
    }

    public static void ShouldBeEmpty<T>(this IEnumerable<T>? actual, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().BeEmpty(because);
    }

    public static void ShouldBeEmpty(this string? actual, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().BeEmpty(because);
    }

    public static void ShouldNotBeEmpty<T>(this IEnumerable<T>? actual, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().NotBeEmpty(because);
    }

    public static void ShouldNotBeEmpty(this string? actual, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().NotBeNullOrEmpty(because);
    }

    public static void ShouldBeGreaterThan<T>(this T actual, T expected, string? because = null)
        where T : IComparable<T>
    {
        actual.Should().BeGreaterThan(expected, because);
    }

    public static void ShouldBeGreaterThanOrEqualTo<T>(this T actual, T expected, string? because = null)
        where T : IComparable<T>
    {
        actual.Should().BeGreaterThanOrEqualTo(expected, because);
    }

    public static void ShouldBeLessThanOrEqualTo<T>(this T actual, T expected, string? because = null)
        where T : IComparable<T>
    {
        actual.Should().BeLessThanOrEqualTo(expected, because);
    }

    public static void ShouldBeInRange<T>(this T actual, T start, T end, string? because = null)
        where T : IComparable<T>
    {
        actual.Should().BeInRange(start, end, because);
    }

    public static void ShouldStartWith(this string actual, string expected, string? because = null)
    {
        actual.Should().StartWith(expected, because);
    }

    public static void ShouldContain(this string? actual, string expected, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().Contain(expected, because);
    }

    public static void ShouldContain(this string? actual, string expected, Case caseSensitivity, string? because = null)
    {
        actual.Should().NotBeNull(because);
        var value = actual!;
        if (caseSensitivity == Case.Insensitive)
        {
            value.Should().ContainEquivalentOf(expected, because);
            return;
        }

        value.Should().Contain(expected, because);
    }

    public static void ShouldNotContain(this string? actual, string expected, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().NotContain(expected, because);
    }

    public static void ShouldContain<T>(this IEnumerable<T>? actual, T expected, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().Contain(expected, because);
    }

    public static void ShouldContain<T>(this IEnumerable<T>? actual, Func<T, bool> predicate, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().Contain(item => predicate(item), because);
    }

    public static void ShouldNotContain<T>(this IEnumerable<T>? actual, Func<T, bool> predicate, string? because = null)
    {
        actual.Should().NotBeNull(because);
        actual!.Should().NotContain(item => predicate(item), because);
    }

    public static void ShouldAllBe<T>(this IEnumerable<T> actual, Func<T, bool> predicate, string? because = null)
    {
        foreach (var item in actual)
        {
            predicate(item).ShouldBeTrue(because);
        }
    }

    public static T ShouldBeOfType<T>(this object? actual, string? because = null)
    {
        var assertion = actual.Should().BeOfType<T>(because);
        return (T)assertion.Subject!;
    }

    public static T ShouldBeAssignableTo<T>(this object? actual, string? because = null)
    {
        var assertion = actual.Should().BeAssignableTo<T>(because);
        return (T)assertion.Subject!;
    }

    public static T ShouldBeOneOf<T>(this T actual, params T[] expected)
    {
        expected.Should().Contain(actual);
        return actual;
    }

    public static T ShouldHaveSingleItem<T>(this IEnumerable<T>? actual, string? because = null)
    {
        actual.Should().NotBeNull(because);
        var constraint = actual!.Should().ContainSingle(because);
        return constraint.Which;
    }

    public static object? ShouldHaveSingleItem(this IEnumerable? actual, string? because = null)
    {
        actual.Should().NotBeNull(because);
        var items = actual!.Cast<object?>().ToList();
        items.Should().ContainSingle(because);
        return items.Single();
    }

    public static void ShouldContainKey<TKey, TValue>(this IDictionary<TKey, TValue> actual, TKey key, string? because = null)
    {
        actual.Should().ContainKey(key, because);
    }
}

public static class Should
{
    public static TException Throw<TException>(Action action, string? because = null)
        where TException : Exception
    {
        return action.Should().Throw<TException>(because).Which;
    }

    public static void NotThrow(Action action, string? because = null)
    {
        action.Should().NotThrow(because);
    }

    public static async Task<TException> ThrowAsync<TException>(Func<Task> action, string? because = null)
        where TException : Exception
    {
        var constraint = await AwesomeAssertions.FluentActions.Invoking(action).Should().ThrowAsync<TException>(because);
        return constraint.Which;
    }
}
