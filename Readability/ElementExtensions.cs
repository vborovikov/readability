namespace Readability;

using System.Text;
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

    public static string GetPath(this Tag? element)
    {
        if (element is null)
            return "/";

        var path = new StringBuilder(512);

        path.Append('/').Append(element.Name);
        for (var parent = element.Parent; parent is not null and not { Name: "body" or "head" or "html" }; parent = parent.Parent)
        {
            path.Insert(0, parent.Name).Insert(0, '/');
        }

        if (element.Attributes["id"] is { Length: > 0 } id)
        {
            path.Append('#').Append(id);
        }

        if (element.Attributes["name"] is { Length: > 0 } name)
        {
            path.Append('@').Append(name);
        }

        if (element.Attributes["class"] is { Length: > 0 } klass)
        {
            path.Append('[').Append(klass).Append(']');
        }

        return path.ToString();
    }
}
