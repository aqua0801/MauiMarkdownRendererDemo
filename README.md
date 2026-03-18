# MauiMarkdownRenderer Demo

Demo application for the [![NuGet](https://img.shields.io/nuget/v/Aqua0801.MauiMarkdownRenderer.svg)](https://www.nuget.org/packages/Aqua0801.MauiMarkdownRenderer)
NuGet package.

## Screenshots
<img width="1920" height="1032" alt="image" src="https://github.com/user-attachments/assets/4432984c-2ec7-417c-b753-c40824b50122" />
<img width="1920" height="1032" alt="image" src="https://github.com/user-attachments/assets/05825eb9-c688-47f8-a775-b8d4ea2b4c81" />

## Features Demonstrated
- Markdown rendering (headings, lists, tables, blockquotes)
- LaTeX / math equation rendering via CSharpMath
- Syntax highlighting for 20+ languages
- Streaming incremental render via Append()
- Live font size, color, throttle controls
- Copy to clipboard with toast notification

## Render Engine Benchmarking
The demo includes a built-in benchmarking UI to measure renderer performance:
- Toggle between **full render** (`Text =`) and **incremental streaming** (`Append()`)
- Adjustable **chunk size** and **append throttle** to simulate different LLM streaming speeds
- `RenderFinished` event reports elapsed time per render pass
- Distinguishes between `FullRender` and `IncrementalRender` event types
- Useful for tuning `AppendThrottle` for your target device and content complexity

## NuGet Package
```xml
<PackageReference Include="Aqua0801.MauiMarkdownRenderer" Version="1.0.1" />
```

## Requirements
- .NET 10
- MAUI
- Windows / Android / iOS

## Building
Clone and open `MauiMarkdownRendererWithLaTeX.sln` in Visual Studio 2026.
