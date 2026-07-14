# Contributing

Thanks for your interest in **Mintokei Agent Runtime**.

## How this repository works

This repository is maintained as the standalone runtime library repo.

Mintokei's private product repo (`Mintokei/mintokei`) consumes it as a git submodule at
`external/mintokei-agent-runtime`. That means runtime changes should be made here first, then pulled
into the product repo by updating the submodule pointer.

## What you can do

- **Open an issue** — bug reports, questions, and feature requests are very welcome, and are the best
  way to get a change made. A minimal reproduction helps a lot.
- **Discuss** — design feedback and real use-case reports genuinely shape what gets built.
- **Open a pull request** — if you have access, target this repo directly for runtime-library
  changes.
- **Update the product repo after merge** — if the change is needed in `Mintokei/mintokei`, bump the
  `external/mintokei-agent-runtime` submodule there to the merged commit from this repo.

## Building locally

Requires the .NET 10 SDK.

```bash
dotnet build Mintokei.slnx
dotnet test  Mintokei.slnx
```

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
