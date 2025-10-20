using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

public static class Native
{
  // --- Power/lid GUIDs ---
  public static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

  // --- Register for power setting notifications ---
  [DllImport("User32.dll", SetLastError = true)]
  public static extern IntPtr RegisterPowerSettingNotification(
      IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

  [DllImport("User32.dll", SetLastError = true)]
  public static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

  // --- Service control handler (Ex) ---
  public delegate int ServiceControlHandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

  [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, ServiceControlHandlerEx cb, IntPtr context);

  [DllImport("Advapi32.dll", SetLastError = true)]
  public static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);

  [StructLayout(LayoutKind.Sequential)]
  public struct SERVICE_STATUS
  {
    public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
  }

  // --- POWERBROADCAST structures ---
  public const int SERVICE_CONTROL_POWEREVENT = 0x0000000D;
  public const int PBT_POWERSETTINGCHANGE = 0x8013;

  [StructLayout(LayoutKind.Sequential)]
  public struct POWERBROADCAST_SETTING
  {
    public Guid PowerSetting;
    public int DataLength;
    public byte Data; // first byte of variable array
  }

  // --- Exec hold while we work ---
  [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
  const uint ES_CONTINUOUS=0x80000000, ES_SYSTEM_REQUIRED=0x1, ES_AWAYMODE_REQUIRED=0x40;
  public static void Hold()   { try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED); } catch {} }
  public static void Unhold() { try { SetThreadExecutionState(ES_CONTINUOUS); } catch {} }
}

public class LidService : ServiceBase
{
  // Your PCIe Root Port (double backslashes)
  private static readonly string Root = @"PCI\VEN_1022&DEV_14EE&SUBSYS_50EE17AA&REV_00\3&2411E6FE&0&12";

  private static readonly string LogFile = @"C:\Scripts\gpp6-cutover-service.log";
  private static void Log(string m){ try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFile)); System.IO.File.AppendAllText(LogFile, $"{DateTime.Now:O}  {m}\r\n"); } catch {} }

  private IntPtr _svcHandle = IntPtr.Zero;
  private IntPtr _lidReg = IntPtr.Zero;

  // race guard: 0=idle, 1=disabling, 2=disabled, 3=enabling
  private static int _phase = 0;

  public LidService()
  {
    ServiceName = "Gpp6Cutover";
    CanStop = true; CanShutdown = true;
    CanHandlePowerEvent = true; // not relied on, but fine
    AutoLog = false;
  }

protected override void OnStart(string[] args)
{
  Log("Service started.");

  _svcHandle = Native.RegisterServiceCtrlHandlerEx(this.ServiceName, HandlerEx, IntPtr.Zero);
  if (_svcHandle == IntPtr.Zero) Log("RegisterServiceCtrlHandlerEx failed: " + Marshal.GetLastWin32Error());

  var lidGuid = Native.GUID_LIDSWITCH_STATE_CHANGE; // <-- fix here
  _lidReg = Native.RegisterPowerSettingNotification(_svcHandle, ref lidGuid, 1 /*DEVICE_NOTIFY_SERVICE_HANDLE*/);
  if (_lidReg == IntPtr.Zero) Log("RegisterPowerSettingNotification failed: " + Marshal.GetLastWin32Error());
  else Log("Registered GUID_LIDSWITCH_STATE_CHANGE.");
}

  protected override void OnStop()
  {
    try { if (_lidReg != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_lidReg); } catch {}
    Log("Service stopped.");
  }

  // Service control callback with full power data
  private int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context)
  {
    if (control == Native.SERVICE_CONTROL_POWEREVENT && eventType == Native.PBT_POWERSETTINGCHANGE)
    {
      try
      {
        // Marshal the setting blob
        var setting = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(eventData);
        if (setting.PowerSetting == Native.GUID_LIDSWITCH_STATE_CHANGE)
        {
          // Data is a BYTE: 0=closed, 1=open
          byte state = Marshal.ReadByte(eventData, Marshal.OffsetOf<Native.POWERBROADCAST_SETTING>("Data").ToInt32());
          if (state == 0) { Log("LID: CLOSED → pre-sleep disable"); TryDisableOnce(); }
          else            { Log("LID: OPEN   → resume enable");     TryEnableOnce();  }
        }
      }
      catch (Exception ex) { Log("HandlerEx error: " + ex); }
    }
    return 0;
  }

  private static void Run(string exe, string args)
  {
    var p = new Process { StartInfo = new ProcessStartInfo(exe, args){ UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden } };
    p.Start(); p.WaitForExit(5000);
  }

  private static bool TryDisableOnce()
  {
    if (Interlocked.CompareExchange(ref _phase, 1, 0) != 0) return false;
    Log("Disable: acquired");
    Native.Hold();
    try { Run("pnputil.exe", $"/disable-device \"{Root}\""); Interlocked.Exchange(ref _phase, 2); Log("Disable: done"); return true; }
    catch (Exception ex){ Log("Disable ERROR: " + ex); Interlocked.Exchange(ref _phase, 0); return false; }
    finally { Native.Unhold(); }
  }

  private static bool TryEnableOnce()
  {
    if (Interlocked.CompareExchange(ref _phase, 3, 2) != 2) return false;
    Log("Enable: acquired");
    try { Thread.Sleep(800); Run("pnputil.exe", $"/enable-device \"{Root}\""); Interlocked.Exchange(ref _phase, 0); Log("Enable: done"); return true; }
    catch (Exception ex){ Log("Enable ERROR: " + ex); Interlocked.Exchange(ref _phase, 0); return false; }
  }

  public static void Main(){ ServiceBase.Run(new LidService()); }
}
