namespace Readability;

using System;
using System.Text.Json.Serialization;
using Brackets;

class ArticleMetadata
{
    public string? Title { get; set; }
    public string? Byline { get; set; }
    public string? Excerpt { get; set; }
    public string? SiteName { get; set; }
    public DateTimeOffset? Published { get; set; }
}

public record ArticleInfo
{
    /** article title */
    public string? Title { get; init; }
    
    /** article description, or short excerpt from the content */
    public string? Excerpt { get; init; }
    
    /** author metadata */
    public string? Byline { get; init; }
    
    /** content direction */
    public string? Dir { get; init; }
    
    /** name of the site */
    public string? SiteName { get; init; }
    
    /** content language */
    [JsonPropertyName("lang")]
    public string? Language { get; init; }

    /** published time */
    [JsonPropertyName("publishedTime")]
    public DateTimeOffset? Published { get; init; }
}

public record Article : ArticleInfo
{
    /** HTML string of processed article content */
    public required ParentTag Content { get; init; }

    /** length of an article, in characters */
    public required int Length { get; init; }
}

[Serializable]
public class ArticleNotFoundException : Exception
{
    public ArticleNotFoundException() { }

    public ArticleNotFoundException(string? message) : base(message) { }

    public ArticleNotFoundException(string? message, Exception? innerException) : base(message, innerException) { }
}