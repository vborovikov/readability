namespace Readability;

using System;
using System.Text.Json.Serialization;
using Brackets;

class ArticleMetadata
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(this.Title);

    public string? Title { get; set; }
    public string? Byline { get; set; }
    public string? Excerpt { get; set; }
    public string? SiteName { get; set; }
    public DateTimeOffset? Published { get; set; }
}

public record Article
{
    /** article title */
    public string? Title { get; init; }

    /** HTML string of processed article content */
    [JsonIgnore]
    public Element Content { get; init; }

    /** text content of the article, with all the HTML tags removed */
    //[JsonIgnore]
    //public string TextContent { get; init; }
    
    /** length of an article, in characters */
    public int Length { get; init; }
    
    /** article description, or short excerpt from the content */
    public string? Excerpt { get; init; }
    
    /** author metadata */
    public string? Byline { get; init; }
    public string? Author { get; init; }
    
    /** content direction */
    public string? Dir { get; init; }
    
    /** name of the site */
    public string? SiteName { get; init; }
    
    /** content language */
    public string? Language { get; init; }
    
    /** published time */
    public DateTimeOffset? PublishedTime { get; init; }

    public string? FeaturedImage { get; init; }
}

[Serializable]
public class ArticleNotFoundException : Exception
{
    public ArticleNotFoundException()
    {
    }

    public ArticleNotFoundException(string? message) : base(message)
    {
    }

    public ArticleNotFoundException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}