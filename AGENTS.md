# Repository Guidelines

## Project Structure & Module Organization
- `Lagrange.Core/` hosts the core NTQQ protocol library; features live under `Common`, `Event`, `Message`, and `Utility`.
- `Lagrange.OneBot/` runs the OneBot bridge with auto-update and persistence; `Core/` contains adapters, `Database/` handles Realm models, and `Resources/` ships default configs.
- `Lagrange.Core.Test/` is a console-style harness; helpers sit in `Utility/`, scenarios in `Tests/`.
- `Vendors/` tracks bundled third-party definitions; update with care and document sources in commits.
- `Lagrange.Core.sln` binds the .NET 6/7/8/9 projects; run solution-wide commands from this root to keep restore and publish outputs aligned.

## Build, Test, and Development Commands
- `dotnet restore Lagrange.Core.sln` pulls solution dependencies.
- `dotnet build Lagrange.Core.sln -c Release` verifies all projects and prepares artifacts.
- `dotnet run --project Lagrange.OneBot` creates `appsettings.json` on first launch and starts the runner locally.
- `dotnet run --project Lagrange.Core.Test` executes the curated regression checks wired in `Program.cs`.
- `docker compose up` (after following `Docker.md`) starts the containerized OneBot stack for integration testing.

## Coding Style & Naming Conventions
- `.editorconfig` enforces LF endings, four-space indents, System-first `using` order, and block-scoped namespaces.
- Use `var` for inferred types except built-in primitives; keep expression-bodied members intentional.
- Prefix interfaces with `I`, keep public types and members PascalCase, and follow camelCase for locals and fields.
- Organize files by feature folders; namespace-to-folder mirroring is optional (`dotnet_style_namespace_match_folder = false`).

## Testing Guidelines
- Add new scenarios under `Lagrange.Core.Test/Tests` and expose entry points through `Program.cs`.
- Reuse serializers and cryptography helpers in `Lagrange.Core.Test/Utility` instead of duplicating protocol code.
- Name test methods for behaviour (`LoginByPasswordAsync`, `BinarySerializationSmoke`) and log expectations via `Console.WriteLine`.
- Run targeted checks with `dotnet run --project Lagrange.Core.Test -- <args>`; document required credentials inline but never commit secrets.

## Commit & Pull Request Guidelines
- Mirror existing history: scope tags such as `chore:`, `ci:`, `[Core]`, `[CI]` plus an imperative summary.
- Keep commits narrow in scope; isolate vendor table or config updates in separate commits.
- PRs should outline intent, list manual verification (commands run, test output), and link related issues.
- Attach screenshots or protocol dumps when behaviour changes; ensure `dotnet build` succeeds before requesting review.
