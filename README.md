# PCIe Root Port Standby Controller

A Windows Service that automatically disables and enables a specific PCIe Root Port during system suspend/resume cycles. This service monitors laptop lid close/open events and uses `pnputil.exe` to toggle the device state, preventing issues with specific hardware during sleep transitions.

## Features

- Monitors laptop lid close/open events via Windows power management notifications
- Automatically disables a PCIe Root Port before system suspend
- Re-enables the device on system resume
- Race condition protection for rapid lid open/close events
- Prevents system sleep during device operations using execution state control
- Comprehensive logging for troubleshooting

## Requirements

- Windows operating system
- .NET Framework 4.8 Runtime
- Administrator privileges (for service installation and device control)

## Installation

### 1. Configure the Target Device

Before building, you need to identify your PCIe Root Port's device instance path:

**Option 1: Device Manager**
1. Open Device Manager
2. Find your PCIe Root Port device
3. Right-click → Properties → Details
4. Select "Device instance path" from the dropdown
5. Copy the value

**Option 2: Command Line**
```powershell
pnputil /enum-devices /class PCI
```

Edit `Program.cs` line 57 and update the `Root` variable with your device instance path:
```csharp
private static readonly string Root = @"PCI\VEN_XXXX&DEV_XXXX&...";
```

### 2. Build the Service

Using MSBuild:
```powershell
msbuild Gpp6Cutover.csproj /p:Configuration=Release
```

Or using dotnet CLI (requires .NET 4.8 targeting pack):
```powershell
dotnet build -c Release
```

### 3. Install the Service

Open an Administrator PowerShell prompt and run:

```powershell
sc create Gpp6Cutover binPath= "C:\full\path\to\bin\Release\net48\Gpp6Cutover.exe" start= auto
```

Replace `C:\full\path\to` with the actual path to your compiled executable.

### 4. Start the Service

```powershell
sc start Gpp6Cutover
```

## Usage

Once installed and started, the service runs automatically in the background. It will:

1. **On lid close**: Disable the configured PCIe Root Port before system enters sleep
2. **On lid open**: Re-enable the PCIe Root Port after system resumes

### Manual Testing

You can manually trigger the disable/enable actions without closing your laptop lid:

```powershell
# Manually trigger disable
sc control Gpp6Cutover 128

# Manually trigger enable
sc control Gpp6Cutover 129
```

### Service Management

```powershell
# Stop the service
sc stop Gpp6Cutover

# Start the service
sc start Gpp6Cutover

# Check service status
sc query Gpp6Cutover

# Uninstall the service
sc delete Gpp6Cutover
```

## Logging

The service logs all operations to:
```
C:\Scripts\gpp6-cutover-service.log
```

Log entries include:
- Service start/stop events
- Lid state changes (open/closed)
- Device enable/disable operations
- Errors and warnings

You can customize the log location by editing line 59 in `Program.cs`.

## Technical Details

### Architecture

This is a single-file Windows Service built on .NET Framework 4.8. It implements custom power event handling through:

1. **Custom Service Control Handler**: Uses `RegisterServiceCtrlHandlerEx` instead of the standard ServiceBase power events for more control
2. **Power Setting Notifications**: Subscribes to `GUID_LIDSWITCH_STATE_CHANGE` via `RegisterPowerSettingNotification`
3. **P/Invoke Power Event Reception**: Receives power events in the `HandlerEx` callback, which marshals `POWERBROADCAST_SETTING` structures
4. **Execution State Control**: Uses `SetThreadExecutionState` to prevent sleep during device operations
5. **State Machine**: Manages a thread-safe state machine with `Interlocked` operations to prevent race conditions

### State Machine

The service maintains a phase state to prevent race conditions:
- `0`: Idle
- `1`: Disabling in progress
- `2`: Disabled
- `3`: Enabling in progress

This ensures that rapid lid open/close events don't cause conflicting device operations.

### P/Invoke Considerations

- The `ServiceControlHandlerEx` delegate callback must remain static to avoid garbage collection issues
- Power setting registration requires the service handle (not the process handle)
- `POWERBROADCAST_SETTING` has a variable-length data field; `Marshal.ReadByte` is used at the Data offset

## Troubleshooting

### Service won't start
- Check that you're running with Administrator privileges
- Verify the device instance path in `Program.cs` line 57 is correct
- Check Windows Event Viewer for service errors

### Device not being disabled/enabled
- Check the log file at `C:\Scripts\gpp6-cutover-service.log`
- Verify the device instance path matches your hardware
- Test manually using `sc control Gpp6Cutover 128` and `129`

### Log file not created
- Ensure the service has permissions to create `C:\Scripts\` directory
- Modify the `LogFile` path in `Program.cs` line 59 if needed

## Development

### Code Style
- Language: C# (.NET Framework 4.8)
- Indentation: 4 spaces, no tabs
- Line endings: CRLF
- Naming: PascalCase for types/methods, camelCase for locals/parameters
- Keep P/Invoke signatures exact; maintain callbacks static to prevent GC

### Building from Source

Clone this repository and build using your preferred method:

```powershell
# Clone
git clone <repository-url>
cd disable-root-port-standby

# Build
dotnet build -c Release
```

## License

[Add your license information here]

## Contributing

[Add contribution guidelines here]

## Acknowledgments

This service was created to address specific hardware compatibility issues during Windows suspend/resume cycles.
