﻿////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.Debugger.Interop;
using AndroidPlusPlus.Common;

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

namespace AndroidPlusPlus.VsDebugEngine
{

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  public class CLangDebuggeeMemoryBytes : IDebugMemoryBytes2
  {

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private readonly CLangDebugger m_debugger;

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public CLangDebuggeeMemoryBytes (CLangDebugger debugger)
    {
      m_debugger = debugger;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public int GetSize (out ulong pqwSize)
    {
      // 
      // Gets the size, in bytes, of the memory represented by this interface.
      // 

      LoggingUtils.PrintFunction ();

      pqwSize = 0xffffffffL;

      return DebugEngineConstants.S_OK;
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public int ReadAt (IDebugMemoryContext2 pStartContext, uint dwCount, byte [] rgbMemory, out uint pdwRead, ref uint pdwUnreadable)
    {
      // 
      // Reads a sequence of bytes, starting at a given location.
      // 

      LoggingUtils.PrintFunction ();

      try
      {
        DebuggeeCodeContext codeContext = pStartContext as DebuggeeCodeContext;

        string command = string.Format ("-data-read-memory-bytes {0} {1}", codeContext.Address.ToString (), dwCount);

        MiResultRecord resultRecord = m_debugger.GdbClient.SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);

        MiResultValue byteStream = resultRecord ["memory"] [0] ["contents"] [0];

        string hexValue = byteStream.GetString ();

        if ((hexValue.Length / 2) != dwCount)
        {
          throw new InvalidOperationException ();
        }

        for (int i = 0; i < dwCount; ++i)
        {
          rgbMemory [i] = byte.Parse (hexValue.Substring (i * 2, 2), NumberStyles.HexNumber);
        }

        pdwRead = dwCount;

        pdwUnreadable = 0;

        return DebugEngineConstants.S_OK;
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);

        pdwRead = 0;

        pdwUnreadable = dwCount;

        return DebugEngineConstants.E_FAIL;
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public int WriteAt (IDebugMemoryContext2 pStartContext, uint dwCount, byte [] rgbMemory)
    {
      // 
      // Writes the specified number of bytes of memory, starting at the specified address.
      // 

      LoggingUtils.PrintFunction ();

      try
      {
        DebuggeeCodeContext codeContext = pStartContext as DebuggeeCodeContext;

        StringBuilder stringBuilder = new StringBuilder ((int)dwCount * 2);

        for (uint i = 0; i < dwCount; ++i)
        {
          stringBuilder.Append (rgbMemory [i].ToString ("x"));
        }

        string command = string.Format ("-data-write-memory {0} {1} {2}", codeContext.Address.ToString (), stringBuilder.ToString (), dwCount);

        MiResultRecord resultRecord = m_debugger.GdbClient.SendCommand (command);

        MiResultRecord.RequireOk (resultRecord, command);

        return DebugEngineConstants.S_OK;
      }
      catch (Exception e)
      {
        LoggingUtils.HandleException (e);

        return DebugEngineConstants.E_FAIL;
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
