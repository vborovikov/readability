namespace Readability;

using Brackets;

static class ElementExtensions
{
    public static bool IsProbablyHidden(this Tag tag)
    {
        if (!tag.HasAttributes)
            return false;

        if (tag.Attributes["style"] is { Length: > 0 } style)
        {
            foreach (var cssDeclaration in style.EnumerateCssDeclarations())
            {
                if (cssDeclaration.Property is "display" && cssDeclaration.Value is "none")
                    return true;
                if (cssDeclaration.Property is "visibility" && cssDeclaration.Value is "hidden")
                    return true;
            }
        }

        return 
            tag.Attributes.Has("hidden") || 
            tag.Attributes.Has("aria-hidden", "true") || 
            tag.Attributes.Has("class", "hidden") ||
            tag.Attributes.Has("type", "hidden");
    }
}
