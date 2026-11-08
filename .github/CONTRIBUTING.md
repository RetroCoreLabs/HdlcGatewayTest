# Contributing to HdlcGatewayTest

Thanks for looking. This file says how to get a build running, what the code is expected to look
like, and what will get a change sent back.

---

## Getting a build

| # | Requirement | How to check |
|---|---|---|
| 1 | The .NET SDK version pinned in `src/global.json` | `dotnet --info` |
| 2 | PowerShell 7 or newer | `$PSVersionTable.PSVersion` |

```powershell
# from the repository root
.\src\build.ps1
```

`build.ps1` builds, runs the unit tests, and publishes `hdlc-loopback.exe` as a single
self-contained file with no loose DLLs and no .NET install needed on the target machine. It
**stops before publishing if any test fails**.

> [!IMPORTANT]
> Pass **`-nodeReuse:false`** on every `dotnet build` and `dotnet test` you run by hand.
> `build.ps1` already does. Node reuse is on by default and leaves MSBuild worker processes
> behind that hold a lock on the output DLL; the next build then quietly keeps the old binary and
> **the tests report green against stale code.**

---

## What the code is expected to look like

 - **No LINQ, and avoid `foreach`.** Use index-based `for` loops. The receive path runs per byte
   at full USB speed; an enumerator allocation per byte is a real cost.
 - **Unit tests only**, in `src/tests`. Everything that does not need a serial port is in
   `HdlcGatewayTest.Core` so it can be tested there. Never add a standalone program that prints
   something and exits - it cannot fail a build.
 - **No FluentAssertions.** Use NUnit's own asserts.
 - **Keep the comments, and add more.** A comment that records why the firmware behaves a certain
   way, or why a thing is NOT done the obvious way, is the most valuable line in the file. Never
   delete one because a style guide prefers less; replace one only when it has become wrong.
 - **Plain words.** In code, comments, commit messages and documentation alike.
 - **Run `dotnet format`** if whitespace drifts.

### Naming

Package id = root namespace = folder name. The standard is `NAMING.md`, kept beside the
repositories. The organisation is **RetroCoreLabs**.

---

## Documentation changes

```powershell
python src\eng\check-docs.py
```

It must exit 0. It fails on a relative link that does not resolve, a machine-specific absolute
path, and a Mermaid diagram that breaks `MERMAID_COLOR_STANDARDS.md`.

> [!WARNING]
> **Never put an absolute path in anything committed here.** A drive letter or a home directory is
> correct on exactly one machine, and this repository is public.

---

## Submitting a change

 1. Branch off `main`. Do not commit to `main` directly.
 2. One logical change per commit. A commit message says **what changed and why**, in plain
    words - the subject line in the imperative, then a blank line, then the reasoning. If you
    measured something, put the number in the message.
 3. `.\src\build.ps1` passes, including the tests, before you open the pull request.
 4. Say in the pull request what you verified and how, and on which hardware. "Tests pass" on its
    own is not useful; "5 minutes at 983000 bit/s on an S2 Mini loopback, 29000 frames, 0 lost" is.
 5. If something is still broken or unfinished, say so plainly.

---

## Licence

By contributing you agree your work is licensed under the MIT licence in [LICENSE](../LICENSE).
