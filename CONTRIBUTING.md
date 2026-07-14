# Contributing

Thanks for your interest in **Mintokei Agent Runtime**.

## How this repository works

This repository is a **published, buildable mirror**. The source of truth is Mintokei's private
monorepo, and the code here is synced automatically on every merge upstream. That has one important
consequence:

- **Pull requests opened here are not merged directly.** Code lands in the monorepo and flows down
  on the next sync — a PR merged into this repo would be overwritten by the mirror.

## What you can do

- **Open an issue** — bug reports, questions, and feature requests are very welcome, and are the best
  way to get a change made. A minimal reproduction helps a lot.
- **Discuss** — design feedback and real use-case reports genuinely shape what gets built.
- **Suggest a patch** — have a fix? Attach a diff or describe it in an issue. We'll apply it upstream
  and credit you; we just can't merge the PR itself, only carry the change into the monorepo.

## Building locally

Requires the .NET 10 SDK.

```bash
dotnet build Mintokei.slnx
dotnet test  Mintokei.slnx
```

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
