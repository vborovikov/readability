namespace Readability;

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

// Lightweight Data URL parser
// Request for Comments: 2397

readonly struct DataUrl : ISpanParsable<DataUrl>
{
    private readonly ReadOnlyMemory<char> memory;
    private readonly Range mimeType;
    private readonly Range parameter;
    private readonly Range encoding;
    private readonly Range data;

    private DataUrl(ReadOnlyMemory<char> memory, ReadOnlySpan<Range> ranges)
    {
        this.memory = memory;
        this.mimeType = ranges[0];
        this.parameter = ranges[1]; // all parameters combined, usually it's just a single parameter
        this.encoding = ranges[2];
        this.data = ranges[3];
    }

    public Range MimeTypeRange => this.mimeType;
    public ReadOnlySpan<char> MimeType => GetSpan(this.mimeType);

    public Range ParameterRange => this.parameter;
    public ReadOnlySpan<char> Parameter => GetSpan(this.parameter);

    public Range EncodingRange => this.encoding;
    public ReadOnlySpan<char> Encoding => GetSpan(this.encoding);

    public Range DataRange => this.data;
    public ReadOnlySpan<char> Data => GetSpan(this.data);

    public static DataUrl Parse(ReadOnlySpan<char> s) =>
        Parse(s, null);

    public static DataUrl Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException();

    public static DataUrl Parse(string s) =>
        Parse(s, null);

    public static DataUrl Parse(string s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException();

    public static bool TryParse(ReadOnlySpan<char> s, [MaybeNullWhen(false)] out DataUrl result) =>
        TryParse(s, null, out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out DataUrl result) =>
        TryParsePrivate(s, ReadOnlyMemory<char>.Empty, out result);

    public static bool TryParse([NotNullWhen(true)] string? s, [MaybeNullWhen(false)] out DataUrl result) =>
        TryParse(s, null, out result);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out DataUrl result) =>
        TryParsePrivate(s.AsSpan(), s.AsMemory(), out result);

    private static bool TryParsePrivate(ReadOnlySpan<char> span, ReadOnlyMemory<char> memory, out DataUrl dataUrl)
    {
        Span<Range> ranges = stackalloc Range[4];
        ranges.Clear();

        foreach (var part in EnumerateParts(span))
        {
            switch (part.Type)
            {
                case DataUrlPartType.MimeType:
                    ranges[0] = part.Range;
                    break;
                case DataUrlPartType.Parameter:
                    ranges[1] = ranges[1].Equals(default) ? part.Range : ranges[1].Start..part.Range.End;
                    break;
                case DataUrlPartType.Encoding:
                    ranges[2] = part.Range;
                    break;
                case DataUrlPartType.Data:
                    ranges[3] = part.Range;
                    break;
            }
        }

        if (ranges[3].Equals(default))
        {
            dataUrl = default;
            return false;
        }

        dataUrl = new(memory, ranges);
        return true;
    }

    private static DataUrlPartEnumerator EnumerateParts(ReadOnlySpan<char> span) => new(span);

    private ReadOnlySpan<char> GetSpan(Range range) => this.memory.IsEmpty || range.Equals(default) ? [] : memory.Span[range];

    private enum DataUrlPartType
    {
        Unknown,
        Scheme,
        MimeType,
        Parameter,
        Encoding,
        Data,
    }

    [DebuggerDisplay("{Span}: {Type}")]
    private readonly ref struct DataUrlPart
    {
        public DataUrlPart(DataUrlPartType type, ReadOnlySpan<char> span, Range range)
        {
            this.Type = type;
            this.Span = span;
            this.Range = range;
        }

        public DataUrlPartType Type { get; }
        public ReadOnlySpan<char> Span { get; }
        public Range Range { get; }
    }

    private ref struct DataUrlPartEnumerator
    {
        private static readonly SearchValues<char> Separators = SearchValues.Create(";,");

        private ReadOnlySpan<char> span;
        private DataUrlPartType type;
        private DataUrlPart current;
        private int index;

        public DataUrlPartEnumerator(ReadOnlySpan<char> span)
        {
            this.span = span;
        }

        public readonly DataUrlPart Current => this.current;

        public readonly DataUrlPartEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var remaining = this.span;
            if (remaining.IsEmpty)
                return false;

            var start = remaining.IndexOfAnyExcept(' ');
            if (start >= 0)
            {
                ++this.type;
                this.index += start;

                remaining = remaining[start..];
                var end = this.type == DataUrlPartType.Scheme ? remaining.IndexOf(':') : remaining.IndexOfAny(Separators);
                if (end >= 0)
                {
                    var part = remaining[..end];

                    this.type = AdjustType(part, this.type);
                    if (this.type == DataUrlPartType.Unknown)
                    {
                        goto UnknownPart;
                    }
                    else if (this.type == DataUrlPartType.Data)
                    {
                        goto DataFound;
                    }

                    this.current = new(this.type, part, this.index..(this.index + end));
                    this.span = remaining[(end + 1)..];
                    this.index += end + 1;
                    return true;
                }

                this.type = AdjustType(remaining, this.type);
                if (this.type == DataUrlPartType.Unknown)
                    goto UnknownPart;

            DataFound:
                this.current = new(this.type, remaining, this.index..);
                this.span = default;
                return true;
            }

        UnknownPart:
            this.span = default;
            return false;
        }

        private static DataUrlPartType AdjustType(ReadOnlySpan<char> span, DataUrlPartType type)
        {
            var newType = type;

            do
            {
                if (newType == DataUrlPartType.Unknown)
                    break;

                type = newType;
                newType = type switch
                {
                    DataUrlPartType.Scheme when !span.Equals("data", StringComparison.Ordinal) => DataUrlPartType.Unknown,
                    DataUrlPartType.MimeType when span.Contains('=') => DataUrlPartType.Parameter,
                    DataUrlPartType.MimeType when span.Length > 0 && !span.Contains('/') => DataUrlPartType.Unknown,
                    DataUrlPartType.Parameter when span.Length > 0 && !span.Contains('=') => DataUrlPartType.Encoding,
                    DataUrlPartType.Encoding when span.Contains('=') => DataUrlPartType.Parameter,
                    DataUrlPartType.Encoding when !span.Equals("base64", StringComparison.Ordinal) => DataUrlPartType.Data,
                    _ => type
                };
            } while (newType != type);

            return newType;
        }
    }
}