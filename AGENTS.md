# Repository Guidelines

## Project Structure & Module Organization
- Program.cs: Windows Service (PowerSvc) that disables/enables a specific PCIe Root Port on suspend/resume using pnputil.
- Gpp6Cutover.csproj: .NET Framework 4.8 console/service app. Output in in/Release/net48/.
- No test project yet. Logs write to C:\Scripts\gpp6-cutover-service.log.

## Build, Run, and Development Commands
- Build (MSBuild): msbuild Gpp6Cutover.csproj /p:Configuration=Release
- Build (dotnet SDK): dotnet build -c Release (requires .NET 4.8 targeting pack)
- Install service (admin): sc create Gpp6Cutover binPath= "<full-path>\bin\Release\net48\Gpp6Cutover.exe" start= auto
- Start/Stop: sc start Gpp6Cutover / sc stop Gpp6Cutover
- Manual test actions: sc control Gpp6Cutover 128 (disable) / 129 (enable)

## Coding Style & Naming Conventions
- Language: C# (net48). Indentation: 4 spaces, no tabs. CRLF line endings.
- Naming: PascalCase for types/methods, camelCase for locals/parameters, constants PascalCase.
- Interop: Keep P/Invoke signatures exact; callbacks must remain static to avoid GC.
- Keep logs concise; avoid noisy loops. Prefer small helpers (e.g., Run, Log).

## Testing Guidelines
- No automated tests yet. Verify via:
  - Service logs: C:\Scripts\gpp6-cutover-service.log
  - Suspend/resume the system and confirm device toggling.
  - Custom commands: sc control Gpp6Cutover 128/129.
- If adding tests, create Gpp6Cutover.Tests (xUnit/MSTest), name files like PowerSvcTests.cs, and extract testable helpers where needed.

## Commit & Pull Request Guidelines
- Commits: imperative mood, concise scope, e.g., ix: guard callback registration errors.
- PRs: include summary, rationale, and screenshots/log excerpts when relevant. Reference issues with Fixes #<id>.
- Keep changes minimal and focused; update docs if behavior changes.

## Security & Configuration Tips
- Admin rights required to install/control the service and to run pnputil.
- Update Program.cs:Root to your exact device instance path. Find it via Device Manager (Properties ? Details ? Device instance path) or pnputil /enum-devices /class PCI.
- Log path is configurable; ensure the process can create/write C:\Scripts or adjust location.