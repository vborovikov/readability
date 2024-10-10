# Readability

[![NuGet](https://img.shields.io/nuget/v/ReadabilityLib.svg)](https://www.nuget.org/packages/ReadabilityLib/)
[![Downloads](https://img.shields.io/nuget/dt/ReadabilityLib.svg)](https://www.nuget.org/packages/ReadabilityLib/#versions-body-tab)
[![Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://www.apache.org/licenses/LICENSE-2.0)

A C# port of standalone version of the readability library used for [Firefox Reader View](https://support.mozilla.org/kb/firefox-reader-view-clutter-free-web-pages).

## Installation

Readability is available on Nuget:

```bash
dotnet add package ReadabilityLib
```

You can then `using Readability;` it.

## Basic usage

To parse an article, you need to call `Parse()` method on a [Brackets](https://github.com/vborovikov/brackets) `Document` object. Here's an example:

```csharp
var document = await Document.Html.ParseAsync(documentContentStream);
var article = document.Parse();
```