﻿////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.IO;
using System.Xaml;

using Microsoft.Build.Framework;
using Microsoft.Build.Framework.XamlTypes;
using Microsoft.Build.Utilities;

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

namespace AndroidPlusPlus.MsBuild.Common
{

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  // NOTE:
  // There are MSBuild classes which support command line building,
  // argument switch encapsulation and more.  Code within VCToolTask,
  // a hidden interface, uses some these classes to generate command line
  // switches given project settings. As the VCToolTask class is a hidden
  // class and the MSBuild documentation is very sparse, we are going
  // with a small custom solution.  For reference see the
  // Microsoft.Build.Tasks.Xaml, Microsoft.Build.Framework.XamlTypes namespaces.

  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

  public class XamlParser
  {

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private Rule m_parsedBuildRule;

    private Dictionary<Type, Action<CommandLineBuilder, BaseProperty, string>> m_typeFunctionMap;

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    // 
    // Allows storing of MSBuild property type for faster searching.
    // 

    private class PropertyWrapper
    {
      public PropertyWrapper (Type newType, BaseProperty newProperty)
      {
        PropertyType = newType;
        Property = newProperty;
      }

      public Type PropertyType { get; set; }

      public BaseProperty Property { get; set; }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public XamlParser (string path)
    {
      // 
      // Allow for the potential to load properties through XAML files with a 'ProjectSchemaDefinitions' root node.
      // 

      object xmlRootNode = XamlServices.Load (path);

      if (xmlRootNode.GetType () == typeof (ProjectSchemaDefinitions))
      {
        ProjectSchemaDefinitions projectSchemaDefs = (ProjectSchemaDefinitions)xmlRootNode;

        m_parsedBuildRule = (Rule)projectSchemaDefs.Nodes [0];
      }
      else
      {
        m_parsedBuildRule = (Rule)xmlRootNode;
      }

      ToolProperties = new Dictionary<string, PropertyWrapper> ();

      foreach (var prop in m_parsedBuildRule.Properties)
      {
        ToolProperties.Add (prop.Name, new PropertyWrapper (prop.GetType (), prop));
      }

      m_typeFunctionMap = new Dictionary<Type, Action<CommandLineBuilder, BaseProperty, string>>
      {
        { typeof(StringListProperty), GenerateArgumentStringList },
        { typeof(StringProperty), GenerateArgumentString },
        { typeof(IntProperty), GenerateArgumentInt },
        { typeof(BoolProperty), GenerateArgumentBool },
        { typeof(EnumProperty), GenerateArgumentEnum }
      };
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private Dictionary<string, PropertyWrapper> ToolProperties { get; set; }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public string Parse (ITaskItem taskItem)
    {
      // 
      // Build a command line in the order defined by the source XML file.
      // 

      CommandLineBuilder builder = new CommandLineBuilder ();

      foreach (string key in ToolProperties.Keys)
      {
        string value = taskItem.GetMetadata (key);

        AppendArgumentForProperty (builder, key, value);
      }

      return builder.ToString ().Replace ('\\', '/');
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    public string ParseProperty (ITaskItem taskItem, string metadataKey)
    {
      // 
      // Build command line for a single metadata property.
      // 

      CommandLineBuilder builder = new CommandLineBuilder ();

      string value = taskItem.GetMetadata (metadataKey);

      AppendArgumentForProperty (builder, metadataKey, value);

      return builder.ToString ().Replace ('\\', '/');
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void AppendArgumentForProperty (CommandLineBuilder builder, string name, string value)
    {
      PropertyWrapper property = null;

      if (ToolProperties.TryGetValue (name, out property))
      {
        if ((property != null) && (value.Length > 0) && property.Property.IncludeInCommandLine)
        {
          m_typeFunctionMap [property.PropertyType] (builder, property.Property, value);
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void GenerateArgumentEnum (CommandLineBuilder builder, BaseProperty property, string value)
    {
      var result = ((EnumProperty)property).AdmissibleValues.Find (x => (x.Name == value));

      if (result != null)
      {
        builder.AppendSwitchUnquotedIfNotNull (m_parsedBuildRule.SwitchPrefix, result.Switch);
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void AppendStringValue (CommandLineBuilder builder, BaseProperty property, string subtype, string value)
    {
      string switchName = property.SwitchPrefix;

      if (string.IsNullOrEmpty (property.SwitchPrefix))
      {
        switchName = m_parsedBuildRule.SwitchPrefix;
      }

      switchName += property.Switch + property.Separator;

      if (subtype == "file" || subtype == "folder")
      {
        if (!string.IsNullOrWhiteSpace (switchName))
        {
          builder.AppendSwitchIfNotNull (switchName, value);
        }
        else
        {
          builder.AppendTextUnquoted (" " + PathUtils.QuoteIfNeeded (value));
        }
      }
      else if (!string.IsNullOrEmpty (property.Switch))
      {
        builder.AppendSwitchUnquotedIfNotNull (switchName, value);
      }
      else if (!string.IsNullOrEmpty (value))
      {
        builder.AppendTextUnquoted (" " + value);
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void AppendStringListValue (CommandLineBuilder builder, BaseProperty property, string subtype, string [] value, string delimiter)
    {
      string switchName = m_parsedBuildRule.SwitchPrefix + property.Switch;

      switchName += property.Separator;

      if (subtype == "file" || subtype == "folder")
      {
        builder.AppendSwitchUnquotedIfNotNull (switchName, value, delimiter);
      }
      else if (!string.IsNullOrEmpty (property.Switch))
      {
        builder.AppendSwitchUnquotedIfNotNull (switchName, value, delimiter);
      }
      else if (value.Length > 0)
      {
        foreach (string entry in value)
        {
          builder.AppendTextUnquoted (entry + delimiter);
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void AppendIntValue (CommandLineBuilder builder, BaseProperty property, string value)
    {
      value = value.Trim ();

      string switchName = m_parsedBuildRule.SwitchPrefix + property.Switch;

      switchName += property.Separator;

      builder.AppendSwitchUnquotedIfNotNull (switchName, value);
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void GenerateArgumentStringList (CommandLineBuilder builder, BaseProperty property, string value)
    {
      string [] arguments = value.Split (new char [] { ';' }, StringSplitOptions.RemoveEmptyEntries);

      if (arguments.Length > 0)
      {
        StringListProperty casted = (StringListProperty)property;

        if (casted.CommandLineValueSeparator != null)
        {
          List<string> sanitised = new List<string> ();

          foreach (string argument in arguments)
          {
            if (argument.Length > 0)
            {
              sanitised.Add (argument.Trim (new char [] { ' ', '\"' }));
            }
          }

          if (sanitised.Count > 0)
          {
            AppendStringListValue (builder, property, casted.Subtype, sanitised.ToArray (), casted.CommandLineValueSeparator);
          }
        }
        else
        {
          foreach (string argument in arguments)
          {
            if (argument.Length > 0)
            {
              AppendStringValue (builder, property, casted.Subtype, argument.Trim (new char [] { ' ', '\"' }));
            }
          }
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void GenerateArgumentString (CommandLineBuilder builder, BaseProperty property, string value)
    {
      StringProperty casted = (StringProperty)property;

      AppendStringValue (builder, property, casted.Subtype, value.Trim (new char [] { ' ', '\"' }));
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void GenerateArgumentInt (CommandLineBuilder builder, BaseProperty property, string value)
    {
      AppendIntValue (builder, property, value);
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    private void GenerateArgumentBool (CommandLineBuilder builder, BaseProperty property, string value)
    {
      if (value == "true")
      {
        builder.AppendSwitchUnquotedIfNotNull (m_parsedBuildRule.SwitchPrefix, property.Switch);
      }
      else if (value == "false" && ((BoolProperty)property).ReverseSwitch != null)
      {
        builder.AppendSwitchUnquotedIfNotNull (m_parsedBuildRule.SwitchPrefix, ((BoolProperty)property).ReverseSwitch);
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
