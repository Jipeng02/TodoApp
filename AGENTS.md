# Repository Guidelines

## Project Structure & Module Organization
This is a minimal .NET 8 console app. The solution lives at `TodoApp.sln`. Source code is in `TodoApp/`, with the entry point at `TodoApp/Program.cs`. Build outputs go to `TodoApp/bin/` and intermediates to `TodoApp/obj/`. There are currently no dedicated tests or assets folders.

## Build, Test, and Development Commands
Run these from the repository root:

- `dotnet build TodoApp.sln` — builds the solution.
- `dotnet run --project TodoApp/TodoApp.csproj` — runs the console app.
- `dotnet test TodoApp.sln` — runs tests (currently none defined).

## Coding Style & Naming Conventions
- Indentation: 4 spaces.
- C# naming: `PascalCase` for public types/methods/properties (e.g., `TodoItem`), `camelCase` for locals/parameters.
- Keep classes small and focused; prefer one public type per file as the project grows.
- Nullable reference types are enabled (`<Nullable>enable</Nullable>`), so avoid `null` unless explicitly allowed.

## Testing Guidelines
No test framework is configured yet. If you add tests, keep them in a `TodoApp.Tests/` project and use conventional naming:
- Test class: `TodoItemTests`
- Test method: `Adds_item_to_list_when_valid`
Run with `dotnet test`.

## Commit & Pull Request Guidelines
Git history only contains an initial commit, so no established convention exists. Use concise, imperative messages such as:
- `Add todo model`
- `Wire up console menu`

PRs should include:
- A short summary and rationale.
- Steps to validate (commands and expected output).
- Screenshots only if you introduce UI beyond console output.

## Security & Configuration Tips
There are no secrets or environment-specific configs in this repo. If you add any, document them and avoid committing sensitive values.
