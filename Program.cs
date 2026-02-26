using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

public static class Native
{
  public static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

  public const int SERVICE_CONTROL_POWEREVENT = 0x0000000D;
  public const int PBT_POWERSETTINGCHANGE = 0x8013;
  public const int SERVICE_CONTROL_STOP = 0x00000001;
  public const int SERVICE_CONTROL_SHUTDOWN = 0x00000005;

  [DllImport("User32.dll", SetLastError = true)]
  public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

  [DllImport("User32.dll", SetLastError = true)]
  public static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

  public delegate int ServiceControlHandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

  [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, ServiceControlHandlerEx cb, IntPtr context);

  [StructLayout(LayoutKind.Sequential)]
  public struct POWERBROADCAST_SETTING
  {
    public Guid PowerSetting;
    public int DataLength;
    public byte Data;
  }

  [DllImport("kernel32.dll")]
  private static extern uint SetThreadExecutionState(uint flags);

  private const uint ES_CONTINUOUS = 0x80000000;
  private const uint ES_SYSTEM_REQUIRED = 0x1;
  private const uint ES_AWAYMODE_REQUIRED = 0x40;

  public static void Hold()
  {
    try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED); } catch { }
  }

  public static void Unhold()
  {
    try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
  }
}

public class LidService : ServiceBase
{
  // Your PCIe Root Port (double backslashes)
  private static readonly string Root = @"PCI\VEN_1022&DEV_14EE&SUBSYS_50EE17AA&REV_00\3&2411E6FE&0&12";

  private static readonly string LogFile = @"C:\Scripts\gpp6-cutover-service.log";

  private static void Log(string m)
  {
    try
    {
      System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFile));
      System.IO.File.AppendAllText(LogFile, $"{DateTime.Now:O}  {m}\r\n");
    }
    catch { }
  }

  // race guard: 0=idle, 1=disabling, 2=disabled, 3=enabling
  private static int _phase = 0;
  private IntPtr _svcHandle = IntPtr.Zero;
  private IntPtr _lidReg = IntPtr.Zero;
  private bool _ignoreFirstLidNotification = true;
  private readonly Native.ServiceControlHandlerEx _handlerEx;

  public LidService()
  {
    ServiceName = "Gpp6Cutover";
    CanStop = true;
    CanShutdown = true;
    CanHandlePowerEvent = true;
    CanHandleSessionChangeEvent = true;
    AutoLog = false;
    _handlerEx = HandlerEx;
  }

  protected override void OnStart(string[] args)
  {
    Log("Service started.");
    EnsureEnabledOnStartup();

    _svcHandle = Native.RegisterServiceCtrlHandlerEx(this.ServiceName, _handlerEx, IntPtr.Zero);
    if (_svcHandle == IntPtr.Zero)
    {
      int win32 = Marshal.GetLastWin32Error();
      Log("RegisterServiceCtrlHandlerEx failed: " + win32);
      throw new InvalidOperationException("Failed to register service control handler. Win32=" + win32);
    }

    var lidGuid = Native.GUID_LIDSWITCH_STATE_CHANGE;
    _lidReg = Native.RegisterPowerSettingNotification(_svcHandle, ref lidGuid, 1 /* DEVICE_NOTIFY_SERVICE_HANDLE */);
    if (_lidReg == IntPtr.Zero)
    {
      int win32 = Marshal.GetLastWin32Error();
      Log("RegisterPowerSettingNotification failed: " + win32);
      throw new InvalidOperationException("Failed to register lid notification. Win32=" + win32);
    }
    else
    {
      Log("Registered GUID_LIDSWITCH_STATE_CHANGE.");
    }
  }

  protected override void OnStop()
  {
    try
    {
      if (_lidReg != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_lidReg);
    }
    catch { }

    Log("Service stopped.");
  }

  private int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context)
  {
    if (control == Native.SERVICE_CONTROL_STOP || control == Native.SERVICE_CONTROL_SHUTDOWN)
    {
      Log(control == Native.SERVICE_CONTROL_STOP ? "SERVICE_CONTROL_STOP received" : "SERVICE_CONTROL_SHUTDOWN received");
      ThreadPool.QueueUserWorkItem(_ =>
      {
        try { Stop(); }
        catch (Exception ex) { Log("Stop dispatch error: " + ex); }
      });
      return 0;
    }

    if (control == Native.SERVICE_CONTROL_POWEREVENT && eventType == Native.PBT_POWERSETTINGCHANGE)
    {
      try
      {
        var setting = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(eventData);
        if (setting.PowerSetting == Native.GUID_LIDSWITCH_STATE_CHANGE)
        {
          int dataOffset = Marshal.OffsetOf<Native.POWERBROADCAST_SETTING>("Data").ToInt32();
          byte state = Marshal.ReadByte(eventData, dataOffset);

          if (_ignoreFirstLidNotification)
          {
            Log($"LID: Initial state notification (state={state}) - ignoring");
            _ignoreFirstLidNotification = false;
            return 0;
          }

          if (state == 0)
          {
            Log("LID: CLOSED -> pre-sleep disable");
            TryDisableOnce();
          }
          else
          {
            Log("LID: OPEN -> enable");
            TryEnableAfterResume();
          }
        }
      }
      catch (Exception ex)
      {
        Log("HandlerEx error: " + ex);
      }
    }

    return 0;
  }

  protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
  {
    try
    {
      switch (powerStatus)
      {
        case PowerBroadcastStatus.Suspend:
          Log("POWER: SUSPEND -> disable");
          TryDisableOnce();
          break;

        case PowerBroadcastStatus.ResumeAutomatic:
        case PowerBroadcastStatus.ResumeSuspend:
          Log($"POWER: {powerStatus} -> enable");
          TryEnableAfterResume();
          break;
      }
    }
    catch (Exception ex)
    {
      Log("OnPowerEvent error: " + ex);
    }

    return true;
  }

  protected override void OnSessionChange(SessionChangeDescription changeDescription)
  {
    try
    {
      if (changeDescription.Reason == SessionChangeReason.SessionUnlock)
      {
        Log("SESSION: UNLOCK -> enable");
        TryEnableAfterResume();
      }
    }
    catch (Exception ex)
    {
      Log("OnSessionChange error: " + ex);
    }

    base.OnSessionChange(changeDescription);
  }

  protected override void OnCustomCommand(int command)
  {
    try
    {
      if (command == 128)
      {
        Log("CUSTOM: 128 -> disable");
        TryDisableOnce();
        return;
      }

      if (command == 129)
      {
        Log("CUSTOM: 129 -> enable");
        TryEnableAfterResume();
        return;
      }

      Log($"CUSTOM: {command} ignored");
    }
    catch (Exception ex)
    {
      Log("OnCustomCommand error: " + ex);
    }

    base.OnCustomCommand(command);
  }

  private static void Run(string exe, string args)
  {
    using (var p = new Process())
    {
      p.StartInfo = new ProcessStartInfo(exe, args)
      {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
      };

      p.Start();
      bool exited = p.WaitForExit(5000);
      if (!exited)
      {
        try
        {
          p.Kill();
          p.WaitForExit(2000);
        }
        catch { }

        Log($"{exe} {args} -> timeout");
        throw new System.TimeoutException($"{exe} timed out after 5000ms");
      }

      Log($"{exe} {args} -> exit {p.ExitCode}");
      if (p.ExitCode != 0)
      {
        throw new InvalidOperationException($"{exe} exited with code {p.ExitCode}");
      }
    }
  }

  private static bool TryDisableOnce()
  {
    if (Interlocked.CompareExchange(ref _phase, 1, 0) != 0) return false;

    Log("Disable: acquired");
    Native.Hold();

    try
    {
      Run("pnputil.exe", $"/disable-device \"{Root}\"");
      Interlocked.Exchange(ref _phase, 2);
      Log("Disable: done");
      return true;
    }
    catch (Exception ex)
    {
      Log("Disable ERROR: " + ex);
      Interlocked.Exchange(ref _phase, 0);
      return false;
    }
    finally
    {
      Native.Unhold();
    }
  }

  private static bool TryEnableAfterResume()
  {
    int previous = AcquireEnablePhase();
    if (previous < 0) return false;

    Log($"Enable: acquired (prev={previous})");

    try
    {
      Thread.Sleep(800);
      Run("pnputil.exe", $"/enable-device \"{Root}\"");
      Interlocked.Exchange(ref _phase, 0);
      Log("Enable: done");
      return true;
    }
    catch (Exception ex)
    {
      Log("Enable ERROR: " + ex);
      Interlocked.Exchange(ref _phase, 0);
      return false;
    }
  }

  private static int AcquireEnablePhase()
  {
    var sw = Stopwatch.StartNew();
    while (true)
    {
      int current = Volatile.Read(ref _phase);
      if (current == 3) return -1;

      if (current == 1)
      {
        if (sw.ElapsedMilliseconds > 6000)
        {
          Log("Enable skipped: disable still in progress");
          return -1;
        }

        Thread.Sleep(50);
        continue;
      }

      if (Interlocked.CompareExchange(ref _phase, 3, current) == current) return current;
    }
  }

  private static void EnsureEnabledOnStartup()
  {
    ThreadPool.QueueUserWorkItem(_ =>
    {
      try
      {
        Log("Startup: enabling root port to clear prior disabled state");
        Run("pnputil.exe", $"/enable-device \"{Root}\"");
        Interlocked.Exchange(ref _phase, 0);
        Log("Startup: enable attempted");
      }
      catch (Exception ex)
      {
        Log("Startup enable error: " + ex);
      }
    });
  }

  public static void Main()
  {
    ServiceBase.Run(new LidService());
  }
}
