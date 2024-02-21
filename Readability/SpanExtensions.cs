namespace Readability;

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FuzzyCompare.Text;

delegate bool SpanPredicate<T>(ReadOnlySpan<T> span);

static class SpanExtensions
{
    public static string ToTrimString(this ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
            return string.Empty;

        return ToTrimString(span.ToArray());
    }

    public static string ToTrimString(this string str)
    {
        if (str.Length == 0)
            return string.Empty;

        return ToTrimString(str.ToCharArray());
    }

    private static string ToTrimString(Span<char> span)
    {
        var len = span.Length;
        if (len == 0)
            return string.Empty;

        ref char src = ref MemoryMarshal.GetReference(span);
        ref char dst = ref MemoryMarshal.GetReference(span);
        var space = false;
        var pos = 0;
        while (len > 0)
        {
            if (char.IsWhiteSpace(src))
            {
                space = true;
            }
            else
            {
                if (space && pos > 0)
                {
                    dst = ' ';
                    dst = ref Unsafe.Add(ref dst, 1);
                    ++pos;
                }
                space = false;
                dst = src;
                dst = ref Unsafe.Add(ref dst, 1);
                ++pos;
            }

            src = ref Unsafe.Add(ref src, 1);
            --len;
        }

        return new string(span[..pos]);
    }

    public static bool HasAnyWord(this ReadOnlySpan<char> span, string[] words)
    {
        foreach (var token in span.EnumerateTokens())
        {
            if (token.Category != TokenCategory.Word)
                continue;

            foreach (var word in words)
            {
                if (token.Span.StartsWith(word, StringComparison.OrdinalIgnoreCase) ||
                    token.Span.EndsWith(word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static int CountAny(this ReadOnlySpan<char> span, SearchValues<char> chars)
    {
        var length = span.Length;
        if (length == 0)
            return 0;

        var count = 0;
        ref char ch = ref MemoryMarshal.GetReference(span);
        while (length > 0)
        {
            if (chars.Contains(ch))
                ++count;
            ch = ref Unsafe.Add(ref ch, 1);
            --length;
        }

        return count;
    }

    public static bool Contains(this IEnumerable<string> strings, ReadOnlySpan<char> span, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        foreach (var str in strings)
        {
            if (span.Equals(str, comparison))
                return true;
        }

        return false;
    }

    public static int IndexOf<T>(this T[] array, int windowLength, SpanPredicate<T> predicate) =>
        IndexOf(array.AsSpan(), windowLength, predicate);

    public static int IndexOf<T>(this Span<T> span, int windowLength, SpanPredicate<T> predicate) =>
        IndexOf((ReadOnlySpan<T>)span, windowLength, predicate);

    public static int IndexOf<T>(this ReadOnlySpan<T> span, int windowLength, SpanPredicate<T> predicate)
    {
        for (var end = windowLength; end <= span.Length; ++end)
        {
            var start = end - windowLength;
            if (predicate(span[start..end]))
                return start;
        }

        return -1;
    }

    public static int LastIndexOf<T>(this T[] array, int windowLength, SpanPredicate<T> predicate) =>
        LastIndexOf(array.AsSpan(), windowLength, predicate);

    public static int LastIndexOf<T>(this Span<T> span, int windowLength, SpanPredicate<T> predicate) =>
        LastIndexOf((ReadOnlySpan<T>)span, windowLength, predicate);

    public static int LastIndexOf<T>(this ReadOnlySpan<T> span, int windowLength, SpanPredicate<T> predicate)
    {
        for (var end = span.Length; end >= windowLength; --end)
        {
            var start = end - windowLength;
            if (predicate(span[start..end]))
                return start;
        }

        return -1;
    }

    public static int IndexOf<T>(this T[] array, Func<T, bool> predicate) => IndexOf(array.AsSpan(), predicate);

    public static int IndexOf<T>(this Span<T> span, Func<T, bool> predicate) => IndexOf((ReadOnlySpan<T>)span, predicate);

    public static int IndexOf<T>(this ReadOnlySpan<T> span, Func<T, bool> predicate)
    {
        for (var i = 0; i < span.Length; ++i)
        {
            if (predicate(span[i]))
                return i;
        }

        return -1;
    }

    public static ValueEnumerator EnumerateValues(this ReadOnlySpan<char> span) => new(span);

    public ref struct ValueEnumerator
    {
        private ReadOnlySpan<char> span;
        private ReadOnlySpan<char> current;

        public ValueEnumerator(ReadOnlySpan<char> span)
        {
            this.span = span;
        }

        public readonly ReadOnlySpan<char> Current => this.current;

        public readonly ValueEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var remaining = this.span;
            if (remaining.IsEmpty)
                return false;

            var start = remaining.IndexOfAnyExcept(' ');
            if (start >= 0)
            {
                remaining = remaining[start..];
                var end = remaining.IndexOf(' ');
                if (end > 0)
                {
                    this.current = remaining[..end];
                    this.span = remaining[(end + 1)..];
                    return true;
                }

                this.current = remaining;
                this.span = default;
                return true;
            }

            this.span = default;
            return false;
        }
    }
}
