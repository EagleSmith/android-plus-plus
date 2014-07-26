﻿////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

namespace AndroidPlusPlus.Common
{

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  using ProcessGdbMiAsyncInputParamType = Tuple<string, GdbClient.OnResultRecordDelegate>;

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  public sealed class GdbClient : AsyncRedirectProcess.EventListener, IDisposable
  {

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public enum StepType
    {
      Statement,
      Line,
      Instruction
    };

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public delegate void OnResultRecordDelegate (MiResultRecord resultRecord);

    public delegate void OnAsyncRecordDelegate (MiAsyncRecord asyncRecord);

    public delegate void OnStreamRecordDelegate (MiStreamRecord streamRecord);

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public OnResultRecordDelegate OnResultRecord { get; set; }

    public OnAsyncRecordDelegate OnAsyncRecord { get; set; }

    public OnStreamRecordDelegate OnStreamRecord { get; set; }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private class AsyncCommandData
    {
      public AsyncCommandData ()
      {
        StreamRecords = new List<MiStreamRecord> ();
      }

      public string Command { get; set; }

      public List<MiStreamRecord> StreamRecords;

      public OnResultRecordDelegate ResultDelegate { get; set; }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private readonly GdbSetup m_gdbSetup;

    private GdbServer m_gdbServer = null;

    private AsyncRedirectProcess m_gdbClientInstance = null;

    private List<string> m_gdbSupportedClientMiFeatures = new List<string> ();

    private List<string> m_gdbSupportedTargetMiFeatures = new List<string> ();

    private Dictionary<uint, AsyncCommandData> m_asyncCommandData = new Dictionary<uint,AsyncCommandData> ();

    private Dictionary<string, ManualResetEvent> m_syncCommandLocks = new Dictionary<string, ManualResetEvent> ();

    private int m_lastOperationTimestamp = 0;

    private uint m_sessionCommandToken = 1; // Start at 1 so 0 can represent an invalid token.

    private Dictionary<uint, string> m_registerIdMapping = new Dictionary<uint, string> ();

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public GdbClient (GdbSetup gdbSetup)
    {
      m_gdbSetup = gdbSetup;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void Dispose ()
    {
      LoggingUtils.PrintFunction ();

      try
      {
        string command = "-gdb-exit";

        MiResultRecord resultRecord = SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);
      }
      finally
      {
        if (m_gdbClientInstance != null)
        {
          m_gdbClientInstance.Dispose ();

          m_gdbClientInstance = null;
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void Start ()
    {
      LoggingUtils.PrintFunction ();

      // 
      // Export an execution script ('gdb.setup') for standard start-up properties.
      // 

      string [] execCommands = m_gdbSetup.CreateGdbExecutionScript ();

      using (StreamWriter writer = new StreamWriter (Path.Combine (m_gdbSetup.CacheDirectory, "gdb.setup")))
      {
        foreach (string command in execCommands)
        {
          writer.WriteLine (command);
        }

        writer.Close ();
      }

      // 
      // Spawn a new GDB instance which executes gdb.setup and begins debugging file 'app_process'.
      // 

      m_lastOperationTimestamp = Environment.TickCount;

      string clientArguments = m_gdbSetup.GdbToolArguments + string.Format (@" -fullname -x {0}", PathUtils.SantiseWindowsPath (Path.Combine (m_gdbSetup.CacheDirectory, "gdb.setup")));

      m_gdbClientInstance = new AsyncRedirectProcess (m_gdbSetup.GdbToolPath, clientArguments);

      m_gdbClientInstance.Start (this);

      // 
      // Evaluate this client's GDB/MI support and capabilities. 
      // 

      MiResultRecord resultRecord = SendCommand ("-list-features");

      MiResultRecord.RequireOk (resultRecord, "-list-features");

      if (resultRecord.HasField ("features"))
      {
        foreach (MiResultValue feature in resultRecord ["features"] [0].Values)
        {
          m_gdbSupportedClientMiFeatures.Add (feature.GetString ());
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void Attach (GdbServer gdbServer)
    {
      LoggingUtils.PrintFunction ();

      if (gdbServer == null)
      {
        throw new ArgumentNullException ("gdbServer");
      }

      m_gdbServer = gdbServer;

      m_gdbSetup.ClearPortForwarding ();

      m_gdbSetup.SetupPortForwarding ();

      SetSetting ("auto-solib-add", "on", false);

      SetSetting ("stop-on-solib-events", "0", false);

      SetSetting ("breakpoint pending", "on", false); // an unrecognized breakpoint location should automatically result in a pending breakpoint being created.

      // 
      // Probe each 'application' library for special embedded 'gdb.setup' sections.
      // 

      string [] cachedAppBinaries = m_gdbSetup.CacheApplicationBinaries ();

#if FALSE
      string gnuObjdumpToolPath = m_gdbSetup.GdbToolPath.Replace ("-gdb", "-objdump");

      foreach (string binary in cachedAppBinaries)
      {
        string embeddedGdbSetupSection = GnuObjdump.GetElfSectionData (gnuObjdumpToolPath, binary, "gdb.setup");

        if (!string.IsNullOrWhiteSpace (embeddedGdbSetupSection))
        {
          string [] embeddedCommands = embeddedGdbSetupSection.Replace ("\r", "").Split (new char [] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

          foreach (string command in embeddedCommands)
          {
            // 
            // Sanitise path arguments, but only if they are rooted.
            // 

            bool setCommand = command.StartsWith ("set ");

            string [] arguments = command.Replace ("set ", "").Split (new char [] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);

            StringBuilder joinedArguments = new StringBuilder ();

            for (int i = 1; i < arguments.Length; ++i)
            {
              if (Path.IsPathRooted (arguments [i]))
              {
                arguments [i] = PathUtils.SantiseWindowsPath (arguments [i]);
              }

              joinedArguments.Append (";" + arguments [i]);
            }

            if (joinedArguments.Length > 0)
            {
              joinedArguments.Remove (0, 1); // clear leading ';'
            }

            if (setCommand)
            {
              SetSetting (arguments [0], joinedArguments.ToString (), true);
            }
            else
            {
              SendCommand (arguments [0] + " " + joinedArguments.ToString ());
            }
          }
        }
      }
#endif

      // 
      // Configure default shared library settings.
      // 

      SetSetting ("sysroot", PathUtils.SantiseWindowsPath (m_gdbSetup.CacheSysRoot), false);

      SetSetting ("solib-search-path", PathUtils.SantiseWindowsPath (m_gdbSetup.CacheSysRoot), true);

      SetSetting ("debug-file-directory", PathUtils.SantiseWindowsPath (m_gdbSetup.CacheSysRoot), true);

      // 
      // Pull required system and application binaries from the device.
      // TODO: Use 'ndk-depends' to figure out which system shared libraries are used and pull these.
      // 

      string [] cachedSystemBinaries = m_gdbSetup.CacheSystemBinaries ();

      List<string> sharedLibrarySearchPaths = new List<string> ();

      foreach (string systemBinary in cachedSystemBinaries)
      {
        string posixSystemBinaryDirectory = PathUtils.SantiseWindowsPath (Path.GetDirectoryName (systemBinary));

        if (!sharedLibrarySearchPaths.Contains (posixSystemBinaryDirectory))
        {
          sharedLibrarySearchPaths.Add (posixSystemBinaryDirectory);
        }
      }

      foreach (string appBinary in cachedAppBinaries)
      {
        string posixAppBinaryDirectory = PathUtils.SantiseWindowsPath (Path.GetDirectoryName (appBinary));

        if (!sharedLibrarySearchPaths.Contains (posixAppBinaryDirectory))
        {
          sharedLibrarySearchPaths.Add (posixAppBinaryDirectory);
        }
      }

      SetSetting ("solib-search-path", string.Join (";", sharedLibrarySearchPaths.ToArray ()), true);

      SetSetting ("debug-file-directory", string.Join (";", sharedLibrarySearchPaths.ToArray ()), true);

      // 
      // Specify the executable file to be debugged. 'ndk-gdb' uses app_process for this, so we will too.
      // TODO: Android-L supports 64-bit app_process binaries, so add very early support for this here.
      // 

      string cachedTargetBinary = Path.Combine (m_gdbSetup.CacheSysRoot, @"system\bin\app_process64");

      if (!File.Exists (cachedTargetBinary))
      {
        cachedTargetBinary = Path.Combine (m_gdbSetup.CacheSysRoot, @"system\bin\app_process32");
      }

      if (!File.Exists (cachedTargetBinary))
      {
        cachedTargetBinary = Path.Combine (m_gdbSetup.CacheSysRoot, @"system\bin\app_process");
      }

      try
      {
        string command = "-file-exec-and-symbols " + PathUtils.SantiseWindowsPath (cachedTargetBinary);

        MiResultRecord resultRecord = SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);

        throw;
      }

      // 
      // Connect to the remote target.
      // 

      try
      {
        string command = string.Format ("-target-select remote {0}:{1}", m_gdbSetup.Host, m_gdbSetup.Port);

        MiResultRecord resultRecord = SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);

        throw;
      }

      // 
      // Evaluate target's GDB/MI support and capabilities. 
      // 

      try
      {
        string command = "-list-target-features";

        MiResultRecord resultRecord = SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);

        if (resultRecord.HasField ("features"))
        {
          foreach (MiResultValue feature in resultRecord ["features"] [0].Values)
          {
            m_gdbSupportedTargetMiFeatures.Add (feature.GetString ());
          }
        }
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);

        throw;
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void Detach ()
    {
      LoggingUtils.PrintFunction ();

      try
      {
        string command = "-target-detach";

        MiResultRecord resultRecord = SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);

        throw;
      }
      finally
      {
        m_gdbServer = null;

        m_gdbSetup.ClearPortForwarding ();
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void Stop ()
    {
      LoggingUtils.PrintFunction ();

      string command = "-exec-interrupt";

      MiResultRecord resultRecord = SendCommand (command);

      MiResultRecord.RequireOk (resultRecord, command);
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void Continue ()
    {
      LoggingUtils.PrintFunction ();

      string command = "-exec-continue";

      MiResultRecord resultRecord = SendCommand (command);

      MiResultRecord.RequireOk (resultRecord, command);
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void Terminate ()
    {
      // 
      // If the program has already terminated, the 'kill' command will error with a description - treat this as success.
      // 

      LoggingUtils.PrintFunction ();

      try
      {
        string command = "kill";

        MiResultRecord resultRecord = SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);

        if (!e.Message.Contains ("The program is not being run"))
        {
          throw;
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void StepInto (uint threadId, StepType stepType, bool reverse)
    {
      LoggingUtils.PrintFunction ();

      switch (stepType)
      {
        case StepType.Statement:
        case StepType.Line:
        {
          // 
          // -exec-step
          // Resumes execution of the inferior program, stopping when the beginning of the next source line is reached, if the next source line is not a function call.
          // If it is, stop at the first instruction of the called function.
          // 

          string command = string.Format ("-exec-step --thread {0} {1}", threadId, ((reverse) ? "--reverse" : ""));

          MiResultRecord resultRecord = SendCommand (command);

          MiResultRecord.RequireOk (resultRecord, command);

          break;
        }

        case StepType.Instruction:
        {
          // 
          // -exec-step-instruction
          // Resumes the inferior which executes one machine instruction.
          // 

          string command = string.Format ("-exec-step-instruction --thread {0} {1}", threadId, ((reverse) ? "--reverse" : ""));

          MiResultRecord resultRecord = SendCommand (command);

          MiResultRecord.RequireOk (resultRecord, command);

          break;
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void StepOut (uint threadId, StepType stepType, bool reverse)
    {
      LoggingUtils.PrintFunction ();

      switch (stepType)
      {
        case StepType.Statement:
        case StepType.Line:
        case StepType.Instruction:
        {
          string command = string.Format ("-exec-finish --thread {0} {1}", threadId, ((reverse) ? "--reverse" : ""));

          MiResultRecord resultRecord = SendCommand (command);

          MiResultRecord.RequireOk (resultRecord, command);

          break;
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void StepOver (uint threadId, StepType stepType, bool reverse)
    {
      LoggingUtils.PrintFunction ();

      switch (stepType)
      {
        case StepType.Statement:
        case StepType.Line:
        {
          // 
          // -exec-next
          // Resumes execution of the inferior program, stopping when the beginning of the next source line is reached.
          // 

          string command = string.Format ("-exec-next --thread {0} {1}", threadId, ((reverse) ? "--reverse" : ""));

          MiResultRecord resultRecord = SendCommand (command);

          MiResultRecord.RequireOk (resultRecord, command);

          break;
        }

        case StepType.Instruction:
        {
          // 
          // -exec-next-instruction
          // Executes one machine instruction. If the instruction is a function call, continues until the function returns.
          // 

          string command = string.Format ("-exec-next-instruction --thread {0} {1}", threadId, ((reverse) ? "--reverse" : ""));

          MiResultRecord resultRecord = SendCommand (command);

          MiResultRecord.RequireOk (resultRecord, command);

          break;
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public Dictionary <uint, string> GetRegisterIdMapping ()
    {
      LoggingUtils.Print (string.Format ("[GdbClient] GetRegisterNameFromId"));

      if (m_registerIdMapping.Count == 0)
      {
        string command = "-data-list-register-names";

        MiResultRecord resultRecord = SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);

        if (resultRecord.HasField ("register-names"))
        {
          MiResultValue registerNames = resultRecord ["register-names"] [0];

          for (int i = 0; i < registerNames.Values.Count; ++i)
          {
            string register = registerNames [i].GetString ();

            m_registerIdMapping.Add ((uint)i, register);
          }
        }
      }

      return m_registerIdMapping;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public string GetSetting (string setting)
    {
      LoggingUtils.Print (string.Format ("[GdbClient] GetSetting: " + setting));

      string command = string.Format ("-gdb-show {0}", setting);

      MiResultRecord resultRecord = SendCommand (command);

      MiResultRecord.RequireOk (resultRecord, command);

      if (resultRecord.HasField ("value"))
      {
        return resultRecord ["value"] [0].GetString ();
      }

      return string.Empty;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void SetSetting (string setting, string value, bool appendToExisting)
    {
      LoggingUtils.Print (string.Format ("[GdbClient] SetSetting: " + setting + "=" + value));

      if (appendToExisting)
      {
        // 
        // Validate that the requested value isn't already set before joining.
        // 

        string existingSettingValue = GetSetting (setting);

        if (!string.IsNullOrWhiteSpace (existingSettingValue))
        {
          string [] existingValues = existingSettingValue.Split (new char [] { ';' });

          foreach (string existing in existingValues)
          {
            if (existing.Equals (value))
            {
              appendToExisting = false;

              break;
            }
          }

          if (appendToExisting)
          {
            // Prefix the new value so it takes precedence. This is usually what's intended.
            value = string.Join (";", new string [] { value, existingSettingValue });
          }
        }
      }

      string command = string.Format ("-gdb-set {0} {1}", setting, value);

      MiResultRecord resultRecord = SendCommand (command);

      MiResultRecord.RequireOk (resultRecord, command);
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public MiResultRecord SendCommand (string command, int timeout = 60000)
    {
      // 
      // Perform a synchronous command request; issue a standard async command and keep alive whilst still receiving output.
      // 

      LoggingUtils.Print (string.Format ("[GdbClient] SendCommand: {0}", command));

      MiResultRecord syncResultRecord = null;

      if (m_gdbClientInstance == null)
      {
        return syncResultRecord;
      }

      ManualResetEvent syncCommandLock = new ManualResetEvent (false);

      m_syncCommandLocks [command] = syncCommandLock;

      SendAsyncCommand (command, delegate (MiResultRecord record) 
      {
        syncResultRecord = record;

        syncCommandLock.Set ();
      });

      // 
      // Wait for asynchronous record response (or exit), reset timeout each time new activity occurs.
      // 

      int timeoutFromCurrentTick = (timeout + m_lastOperationTimestamp) - Environment.TickCount;

      bool responseSignaled = false;

      while ((!responseSignaled) && (timeoutFromCurrentTick > 0))
      {
        responseSignaled = syncCommandLock.WaitOne (0);

        if (!responseSignaled)
        {
          timeoutFromCurrentTick = (timeout + m_lastOperationTimestamp) - Environment.TickCount;

          Thread.Yield ();
        }
      }

      m_syncCommandLocks.Remove (command);

      if (!responseSignaled)
      {
        throw new TimeoutException ("Timed out waiting for synchronous response for command: " + command);
      }

      return syncResultRecord;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void SendAsyncCommand (string command, OnResultRecordDelegate asyncDelegate = null)
    {
      // 
      // Keep track of this command, and associated token-id, so results can be tracked asynchronously.
      // 

      LoggingUtils.PrintFunction ();

      if (m_gdbClientInstance == null)
      {
        return;
      }

      m_lastOperationTimestamp = Environment.TickCount;

      Thread asyncCommandThread = new Thread (ProcessGdbMiAsyncInput);

      asyncCommandThread.Start (new ProcessGdbMiAsyncInputParamType (command, asyncDelegate));

      LoggingUtils.Print (string.Format ("[GdbClient] SendAsyncCommand: {0}", command));
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void ProcessStdout (object sendingProcess, DataReceivedEventArgs args)
    {
      m_lastOperationTimestamp = Environment.TickCount;

      if (!string.IsNullOrEmpty (args.Data))
      {
        LoggingUtils.Print (string.Format ("[GdbClient] ProcessStdout: {0}", args.Data));

        try
        {
          // 
          // Distribute result records to registered delegate callbacks.
          // 

          Thread resultDelegateThread = new Thread (ProcessGdbMiOutput);

          resultDelegateThread.Start (args.Data);
        }
        catch (Exception e)
        {
          LoggingUtils.HandleException (e);
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void ProcessStderr (object sendingProcess, DataReceivedEventArgs args)
    {
      try
      {
        m_lastOperationTimestamp = Environment.TickCount;

        if (!string.IsNullOrWhiteSpace (args.Data))
        {
          LoggingUtils.Print (string.Format ("[GdbClient] ProcessStderr: {0}", args.Data));
        }
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public void ProcessExited (object sendingProcess, EventArgs args)
    {
      try
      {
        m_lastOperationTimestamp = Environment.TickCount;

        LoggingUtils.Print (string.Format ("[GdbClient] ProcessExited: {0}", args));

        m_gdbClientInstance = null;

        // 
        // If we're waiting on a synchronous command, signal a finish to process termination.
        // 

        foreach (KeyValuePair<string, ManualResetEvent> syncKeyPair in m_syncCommandLocks)
        {
          syncKeyPair.Value.Set ();
        }
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void ProcessGdbMiAsyncInput (object threadStartParameter)
    {
      // 
      // Thread body - takes a command and associated delegate, feeds command to pipe and registers delegate to trigger when GDB responds.
      // 

      try
      {
        if (threadStartParameter == null)
        {
          throw new ArgumentNullException ("threadStartParameter");
        }
        else if (!(threadStartParameter is ProcessGdbMiAsyncInputParamType))
        {
          throw new ArgumentException ("threadStartParameter expected ProcessGdbMiAsyncInputParamType type");
        }

        ProcessGdbMiAsyncInputParamType parameter = (ProcessGdbMiAsyncInputParamType) threadStartParameter;

        string command = (string) parameter.Item1;

        GdbClient.OnResultRecordDelegate resultDelegate = (GdbClient.OnResultRecordDelegate) parameter.Item2;

        AsyncCommandData commandData = new AsyncCommandData ();

        commandData.Command = command;

        commandData.ResultDelegate = resultDelegate;

        lock (m_asyncCommandData)
        {
          m_asyncCommandData.Add (m_sessionCommandToken, commandData);
        }

        // 
        // Prepend (and increment) GDB/MI token.
        // 

        command = m_sessionCommandToken + command;

        ++m_sessionCommandToken;

        m_gdbClientInstance.SendCommand (command);

        m_lastOperationTimestamp = Environment.TickCount;
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void ProcessGdbMiOutput (object threadStartParameter)
    {
      // 
      // Thread body - parses GDB/MI output. If a command has be completely processed, call its associated registered delegate.
      // 

      try
      {
        if (threadStartParameter == null)
        {
          throw new ArgumentNullException ("threadStartParameter");
        }
        else if (!(threadStartParameter is string))
        {
          throw new ArgumentException ("threadStartParameter expected string type");
        }

        string gdbOutput = (string) threadStartParameter;

        MiRecord record = MiInterpreter.ParseGdbOutputRecord (gdbOutput);

        if (record is MiPromptRecord)
        {
          //m_asyncCommandLock.Set ();
        }
        else if ((record is MiAsyncRecord) && (OnAsyncRecord != null))
        {
          MiAsyncRecord asyncRecord = record as MiAsyncRecord;

          OnAsyncRecord (asyncRecord);
        }
        else if ((record is MiResultRecord) && (OnResultRecord != null))
        {
          MiResultRecord resultRecord = record as MiResultRecord;

          OnResultRecord (resultRecord);
        }
        else if ((record is MiStreamRecord) && (OnStreamRecord != null))
        {
          MiStreamRecord streamRecord = record as MiStreamRecord;

          OnStreamRecord (streamRecord);

          // 
          // Non-GDB/MI commands (standard client interface commands) report their output using standard stream records.
          // We cache these outputs for any active CLI commands, identifiable as the commands don't start with '-'.
          // 

          lock (m_asyncCommandData)
          {
            foreach (KeyValuePair<uint, AsyncCommandData> asyncCommand in m_asyncCommandData)
            {
              if (!asyncCommand.Value.Command.StartsWith ("-"))
              {
                asyncCommand.Value.StreamRecords.Add (streamRecord);
              }
            }
          }
        }

        // 
        // Call the corresponding registered delegate for the token response.
        // 

        MiResultRecord callbackRecord = record as MiResultRecord;

        if ((callbackRecord != null) && (callbackRecord.Token != 0))
        {
          AsyncCommandData callbackCommandData = null;

          lock (m_asyncCommandData)
          {
            if (m_asyncCommandData.TryGetValue (callbackRecord.Token, out callbackCommandData))
            {
              callbackRecord.Records.AddRange (callbackCommandData.StreamRecords);

              m_asyncCommandData.Remove (callbackRecord.Token);
            }
          }

          // 
          // Spawn any registered callback handlers on a dedicated thread, as not to block GDB output.
          // 

          if ((callbackCommandData != null) && (callbackCommandData.ResultDelegate != null))
          {
            callbackCommandData.ResultDelegate (callbackRecord);
          }
        }
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  }

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

}

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
