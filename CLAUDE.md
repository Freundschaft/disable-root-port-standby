# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Windows Service that automatically disables/enables a specific PCIe Root Port during system suspend/resume cycles. The service monitors lid close/open events via Windows power management notifications and uses `pnputil.exe` to toggle the device state.

## Build and Development Commands

```powershell
# Build (MSBuild)
msbuild Gpp6Cutover.csproj /p:Configuration=Release

# Build (dotnet SDK - requires .NET 4.8 targeting pack)
dotnet build -c Release

# Install service (requires admin)
sc create Gpp6Cutover binPath= "<full-path>\bin\Release\net48\Gpp6Cutover.exe" start= auto

# Start/Stop service
sc start Gpp6Cutover
sc stop Gpp6Cutover

# Manual test actions (requires admin)
sc control Gpp6Cutover 128  # Trigger disable
sc control Gpp6Cutover 129  # Trigger enable
```

## Architecture

**Single-file Windows Service (.NET Framework 4.8)**

The service implements custom power event handling by:
1. Registering a custom service control handler via `RegisterServiceCtrlHandlerEx` (not using the standard ServiceBase power events)
2. Subscribing to `GUID_LIDSWITCH_STATE_CHANGE` notifications via `RegisterPowerSettingNotification`
3. Receiving power events in the `HandlerEx` callback, which marshals `POWERBROADCAST_SETTING` structures to detect lid state
4. Using `SetThreadExecutionState` to prevent sleep during device operations
5. Managing a state machine (`_phase`: 0=idle, 1=disabling, 2=disabled, 3=enabling) with `Interlocked` operations to prevent race conditions during rapid lid open/close events

**Critical P/Invoke patterns:**
- The `ServiceControlHandlerEx` delegate callback must remain static to avoid GC issues
- Power setting registration requires the service handle (not the process handle)
- POWERBROADCAST_SETTING has a variable-length data field; use `Marshal.ReadByte` at the Data offset

## Configuration

**Device Instance Path:** Update `LidService.Root` (line 57 in Program.cs) with your PCIe Root Port's device instance path:
- Find via Device Manager: Properties → Details → Device instance path
- Or via: `pnputil /enum-devices /class PCI`

**Log Location:** `C:\Scripts\gpp6-cutover-service.log` (configurable at line 59)

## Testing

No automated tests. Verify via:
- Service logs at `C:\Scripts\gpp6-cutover-service.log`
- System suspend/resume: close/open laptop lid and check logs for device toggle
- Manual triggers: `sc control Gpp6Cutover 128` (disable) / `129` (enable)

## Code Style

- Language: C# (.NET Framework 4.8)
- 4 spaces, no tabs, CRLF line endings
- PascalCase for types/methods, camelCase for locals/parameters
- Keep P/Invoke signatures exact; maintain callback static to prevent GC
