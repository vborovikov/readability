namespace Readability;

using System;
using System.Diagnostics;

// Lightweight inline CSS declaration parser

[DebuggerDisplay("{Property}: {Value};")]
public readonly ref struct CssDeclaration
{
    public CssDeclaration(ReadOnlySpan<char> property, ReadOnlySpan<char> value)
    {
        this.Property = property;
        this.Value = value;
    }

    public ReadOnlySpan<char> Property { get; }
    public ReadOnlySpan<char> Value { get; }
}

static class Css
{
    public static DeclarationEnumerator EnumerateCssDeclarations(this ReadOnlySpan<char> span) => new(span);

    public ref struct DeclarationEnumerator
    {
        private ReadOnlySpan<char> span;
        private CssDeclaration current;

        public DeclarationEnumerator(ReadOnlySpan<char> span)
        {
            this.span = span;
        }

        public readonly CssDeclaration Current => this.current;

        public readonly DeclarationEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var remaining = this.span;
            if (remaining.IsEmpty)
                return false;

            var start = remaining.IndexOfAnyExcept(' ');
            if (start >= 0)
            {
                remaining = remaining[start..];
                var end = remaining.IndexOf(';');
                // check for escaped semicolon
                while (end > 0 && remaining[end - 1] == '\\')
                {
                    if (++end == remaining.Length)
                        goto InvalidDeclaration;

                    end = remaining[end..].IndexOf(';');
                    if (end >= 0)
                        end += 1;
                }

                var decl = end > 0 ? remaining[..end] : remaining;
                var col = decl.IndexOf(':');
                if (col <= 0)
                    goto InvalidDeclaration;

                var property = decl[..col].TrimEnd();
                var value = decl[(col + 1)..].Trim();
                if (property.IsEmpty || value.IsEmpty)
                    goto InvalidDeclaration;

                this.current = new(property, value);
                this.span = end > 0 ? remaining[(end + 1)..] : default;
                return true;
            }

        InvalidDeclaration:
            this.span = default;
            return false;
        }
    }
}
