﻿////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debuggers;
using Microsoft.VisualStudio.ProjectSystem.Utilities;
using Microsoft.VisualStudio.ProjectSystem.Utilities.DebuggerProviders;
using Microsoft.VisualStudio.ProjectSystem.VS.Debuggers;

using AndroidPlusPlus.Common;
using AndroidPlusPlus.VsDebugCommon;

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

namespace AndroidPlusPlus.VsDebugLauncher
{

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  [ComVisible (true)]

  [Guid ("A7A96E37-9D90-489A-84CB-14BBBE5686D2")]

  [ExportDebugger("AndroidPlusPlusDebugger")]

  [AppliesTo(ProjectCapabilities.VisualC)]

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  public class DebugLauncher : DebugLaunchProviderBase 
  {
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    [Import]
    private Rules.RuleProperties DebuggerProperties { get; set; }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private static IDebugLauncher s_debugLauncher;

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    [ImportingConstructor] // NOTE: Took me two days to realise this line was missing. Launcher won't work otherwise.
    public DebugLauncher (ConfiguredProject configuredProject)
      : base (configuredProject)
    {

    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public static IDebugLauncher GetDebugLauncher (IServiceProvider serviceProvider)
    {
      if (s_debugLauncher == null)
      {
        s_debugLauncher = new DebugLauncherCommon (serviceProvider);
      }

      return s_debugLauncher;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public override async Task<bool> CanLaunchAsync (DebugLaunchOptions launchOptions)
    {
      LoggingUtils.PrintFunction ();

      IDebugLauncher debugLauncher = null;

      try
      {
        debugLauncher = GetDebugLauncher (ServiceProvider);

        return await System.Threading.Tasks.Task.Run(() =>
       {
         try
         {
            return debugLauncher.CanLaunch((int)launchOptions);
         }
         catch (Exception e)
         {
           HandleExceptionDialog(e, debugLauncher);
         }

         return false;
       });
      }
      catch (Exception e)
      {
        HandleExceptionDialog(e, debugLauncher);
      }

      return false;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public override async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync (DebugLaunchOptions launchOptions)
    {
      LoggingUtils.PrintFunction ();

      IDebugLauncher debugLauncher = null;

      DebugLaunchSettings debugLaunchSettings = new DebugLaunchSettings (launchOptions);

      try
      {
        debugLauncher = GetDebugLauncher (ServiceProvider);

        debugLauncher.PrepareLaunch ();

        Dictionary<string, string> projectProperties = await DebuggerProperties.ProjectPropertiesToDictionary ();

        projectProperties.Add ("ConfigurationGeneral.ProjectDir", Path.GetDirectoryName (DebuggerProperties.GetConfiguredProject ().UnconfiguredProject.FullPath));

        LaunchConfiguration launchConfig = debugLauncher.GetLaunchConfigurationFromProjectProperties (projectProperties);

        LaunchProps [] launchProps = debugLauncher.GetLaunchPropsFromProjectProperties (projectProperties);

        if (launchOptions.HasFlag (DebugLaunchOptions.NoDebug))
        {
          debugLaunchSettings = (DebugLaunchSettings) debugLauncher.StartWithoutDebugging ((int) launchOptions, launchConfig, launchProps, projectProperties);
        }
        else
        {
          debugLaunchSettings = (DebugLaunchSettings) debugLauncher.StartWithDebugging ((int) launchOptions, launchConfig, launchProps, projectProperties);
        }
      }
      catch (Exception e)
      {
        HandleExceptionDialog (e, debugLauncher);
      }

      return new IDebugLaunchSettings [] { debugLaunchSettings };
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void HandleExceptionDialog (Exception e, IDebugLauncher debugLauncher)
    {
      LoggingUtils.HandleException(e);

      string description = string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", e.GetType(), e.Message);

      description += "\nStack trace:\n" + e.StackTrace;

      if (debugLauncher != null)
      {
        LoggingUtils.RequireOk(debugLauncher.GetConnectionService().LaunchDialogUpdate(description, true));
      }

      VsShellUtilities.ShowMessageBox(ServiceProvider, description, "Android++ Debugger", OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
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
