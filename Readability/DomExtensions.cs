﻿namespace Readability;

using System;
using System.Buffers;
using Brackets;

static class DomExtensions
{
    public static IEnumerable<ParentTag> EnumerateAncestors(this Element element, int maxDepth = 0)
    {
        var count = 0;
        for (var parent = element.Parent as ParentTag; parent is not null; parent = parent.Parent)
        {
            yield return parent;
            if (maxDepth > 0 && ++count == maxDepth)
                yield break;
        }
    }

    public static ParentTag? FindAncestor(this Element element, Predicate<ParentTag> predicate)
    {
        for (var parent = element.Parent as ParentTag; parent is not null; parent = parent.Parent)
        {
            if (predicate(parent))
                return parent;
        }

        return null;
    }

    /**
     * Check if this node has only whitespace and a single element with given tag
     * Returns false if the DIV node contains non-empty text nodes
     * or if it contains no element with given tag or more than 1 element.
     *
     * @param Element
     * @param string tag of child element
    **/
    // _hasSingleTagInsideElement
    public static bool HasSingleTagInside(this ParentTag parent, string tagName)
    {
        if (parent.Count(e => e is Tag) != 1 || ((Tag)parent.First(e => e is Tag)).Name != tagName)
            return false;

        return !parent.Any(e => e is Content { Data.IsEmpty: false } content && !content.Data.IsWhiteSpace());
    }

    /**
     * Finds the next node, starting from the given node, and ignoring
     * whitespace in between. If the given node is an element, the same node is
     * returned.
     */
    // _nextNode
    public static Element? NextElement(this Element? node)
    {
        var next = node;
        while (next is not null &&
            next is CharacterData chars &&
            chars.Data.IsWhiteSpace())
        {
            next = next.NextSiblingOrDefault();
        }
        return next;
    }

    /**
     * Traverse the DOM from node to node, starting at the node passed in.
     * Pass true for the second parameter to indicate this node itself
     * (and its kids) are going away, and we want the next node over.
     *
     * Calling this in a loop will traverse the DOM depth-first.
     */
    // _getNextNode
    public static Tag? NextTagOrDefault(this Tag element, bool ignoreSelfAndKids = false)
    {
        // First check for kids if those aren't being ignored
        if (!ignoreSelfAndKids && (element as ParentTag)?.FirstOrDefault(el => el is Tag) is Tag firstElementChild)
        {
            return firstElementChild;
        }

        // Then for siblings...
        if (element.NextSiblingOrDefault(el => el is Tag) is Tag nextElementSibling)
        {
            return nextElementSibling;
        }

        // And finally, move up the parent chain *and* find a sibling
        // (because this is depth-first traversal, we will have already
        // seen the parent nodes themselves).
        var parent = element.Parent;
        while (parent is not null)
        {
            if (parent.NextSiblingOrDefault() is Tag)
                break;
            parent = parent.Parent;
        }
        return parent?.NextSiblingOrDefault() as Tag ?? parent;
    }

    public static Tag? RemoveAndGetNextTag(this Tag element)
    {
        var next = element.NextTagOrDefault(ignoreSelfAndKids: true);
        element.Remove();
        return next;
    }

    public static bool TryRemove(this Element element)
    {
        if (element.Parent is ParentTag parent)
        {
            parent.Remove(element);
            return true;
        }

        return false;
    }

    public static void Remove(this Element element)
    {
        if (!element.TryRemove())
            throw new InvalidOperationException();
    }

    public static bool TryReplaceWith(this Element element, Element replacement)
    {
        if (element.Parent is ParentTag parent)
        {
            parent.Replace(element, replacement);
            return true;
        }

        return false;
    }

    public static void ReplaceWith(this Element element, Element replacement)
    {
        if (!element.TryReplaceWith(replacement))
            throw new InvalidOperationException();
    }

    public static bool TryRemoveAttribute(this Tag tag, string attrName)
    {
        if (tag.FindAttribute(attr => attr.Name == attrName) is Attr attr)
        {
            tag.RemoveAttribute(attr);
            return true;
        }
        return false;
    }

    public static int GetContentLength(this Element element, bool normalizeSpaces = true)
    {
        return element switch
        {
            Content content => content.Length,
            ParentTag parent => parent.FindAll<Content>(c => true).Sum(c => c.Length),
            _ => 0,
        };
    }

    public static int GetCharCount(this Element element, SearchValues<char> chars)
    {
        return element switch
        {
            Content content => content.Data.CountAny(chars),
            ParentTag parent => parent.FindAll<Content>(c => true).Sum(c => c.Data.CountAny(chars)),
            _ => 0,
        };
    }

    public static bool ValueHasImageWithSize(this Attr attribute)
    {
        if (!attribute.HasValue)
            return false;

        var value = attribute.Value;
        var pos = value.IndexOf(".jpg ", StringComparison.OrdinalIgnoreCase);
        if (pos < 0) pos = value.IndexOf(".jpeg ", StringComparison.OrdinalIgnoreCase);
        if (pos < 0) value.IndexOf(".png ", StringComparison.OrdinalIgnoreCase);
        if (pos < 0) value.IndexOf(".webp ", StringComparison.OrdinalIgnoreCase);
        if (pos <= 0) return false;

        value = value[pos..];
        pos = value.IndexOfAnyExcept(' ');
        if (pos < 0) return false;

        return char.IsDigit(value[pos]);
    }

    public static bool ValueHasImage(this Attr attribute)
    {
        if (!attribute.HasValue)
            return false;

        var value = attribute.Value;
        return
            value.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".webp", StringComparison.OrdinalIgnoreCase);
    }
}