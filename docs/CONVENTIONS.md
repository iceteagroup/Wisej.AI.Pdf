# Wisej.AI.Pdf: coding conventions

All the code in this repository must follow these rules. They keep the codebase consistent and production quality.

## Language and platform

- C# (`LangVersion` latest), targeting **netstandard2.0** and **net8.0**.
- Code must compile for netstandard2.0. That means:
  - no `Span<T>`, `Memory<T>`, `stackalloc` into spans, or `System.Buffers`;
  - no `ArrayPool`;
  - no `Math.Clamp`;
  - no `MathF`;
  - no `BitOperations`.
- **No NuGet dependencies.** Only the BCL is allowed. `System.Drawing` is never used.
- `Nullable` is disabled and `ImplicitUsings` is disabled, so write explicit `using` directives.
- `unsafe` code is allowed only in measured hot loops, and must be commented.

## Formatting

- Indent with **tabs**.
- Use **Allman** braces (each brace on its own line).
- A single-statement `if`/`for` body may omit braces, but put it on its own line. Never put it on the same line.
- Separate logical steps in a method with a blank line.
- Use `this.` for instance properties and `_camelCase` for private fields.
- Use `PascalCase` for types, methods, properties and constants. Use `camelCase` for locals and parameters.
- Use `var` when the type is evident.
- Group members with `#region` in larger files, in this order: Nested types, fields, constructors, Properties, Methods, Implementation.
- Every file starts with the Ice Tea Group header, followed by a comment describing the file:

```
///////////////////////////////////////////////////////////////////////////////
//
// (C) 2026 ICE TEA GROUP LLC - ALL RIGHTS RESERVED
//
// 
//
// ALL INFORMATION CONTAINED HEREIN IS, AND REMAINS
// THE PROPERTY OF ICE TEA GROUP LLC AND ITS SUPPLIERS, IF ANY.
// THE INTELLECTUAL PROPERTY AND TECHNICAL CONCEPTS CONTAINED
// HEREIN ARE PROPRIETARY TO ICE TEA GROUP LLC AND ITS SUPPLIERS
// AND MAY BE COVERED BY U.S. AND FOREIGN PATENTS, PATENT IN PROCESS, AND
// ARE PROTECTED BY TRADE SECRET OR COPYRIGHT LAW.
//
// DISSEMINATION OF THIS INFORMATION OR REPRODUCTION OF THIS MATERIAL
// IS STRICTLY FORBIDDEN UNLESS PRIOR WRITTEN PERMISSION IS OBTAINED
// FROM ICE TEA GROUP LLC.
//
///////////////////////////////////////////////////////////////////////////////

// <one or two lines describing the file, with the spec section when relevant>
```

## Documentation

- **Every** type and member gets XML documentation: `<summary>`, `<param>`, `<returns>`, plus `<exception>` where it applies. That includes `private` fields, which get either a `//` comment or a `<summary>`.
- Explain *why* with `//` comments wherever the code handles a spec subtlety, a workaround for broken files, or a non-obvious algorithm step.
- Write comments in lowercase sentence style (`// skip the zlib header.`), matching the existing code.
- The build must produce **zero warnings**, including CS1591 (missing XML comment).

## Robustness

- Real-world PDFs are frequently broken. Decoders and parsers must:
  - **never throw** on malformed input, except where the API documents it;
  - never hang or loop forever;
  - never allocate unbounded memory.
- On malformed input, return what was decoded so far, or a documented failure value.
- Validate sizes before allocating. Guard recursion depth.
- Prefer `internal` visibility. Only the high-level extraction API is `public`.

## Performance

- Avoid LINQ in hot paths.
- Avoid per-pixel and per-glyph allocations.
- Reuse buffers.
- Process pages independently, so callers can parallelize.
