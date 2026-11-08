// LINE VIEW MODELS - one console line and one frame row as the lists show them.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Avalonia.Media;
using HdlcGatewayTest.Gateway;

namespace HdlcGatewayTest.App.ViewModels;

/// <summary>One console line with its colour.</summary>
public sealed class ConsoleLineViewModel
{
    /// <summary>
    /// Wrap a line.
    /// </summary>
    /// <param name="line">The line.</param>
    public ConsoleLineViewModel(ConsoleLine line)
    {
        Line = line;
        string prefix = line.Kind switch
        {
            ConsoleLineKind.Command => line.Source == FrameSource.Mcp ? "> (mcp) " : "> ",
            ConsoleLineKind.Log => "~ ",
            ConsoleLineKind.Event => "! ",
            ConsoleLineKind.Notice => "* ",
            _ => "  ",
        };
        Text = line.Time.ToString("HH:mm:ss.fff") + " " + prefix + line.Text;
        // Mid-tone colours that read on both the light and the dark theme (the first set was
        // chosen on a dark theme and was light grey on white for Ronny, 2026-11-08). A reply
        // uses the theme's text colour, resolved here: a null Foreground on a TextBlock does NOT
        // inherit, it paints nothing, and the STAT reply was invisible (bench, 2026-11-08).
        Brush = line.Kind switch
        {
            ConsoleLineKind.Command => Brushes.RoyalBlue,
            ConsoleLineKind.Log => Brushes.SlateGray,
            ConsoleLineKind.Event => Brushes.MediumOrchid,
            ConsoleLineKind.Notice => Brushes.Chocolate,
            ConsoleLineKind.RawText => Brushes.DarkGoldenrod,
            _ => ThemeText(),
        };
    }

    /// <summary>
    /// The plain text colour for the theme in use: near-black on the light theme, near-white
    /// on the dark one. Read at construction, on the UI thread.
    /// </summary>
    /// <returns>A brush.</returns>
    public static IBrush ThemeText()
    {
        Avalonia.Application? app = Avalonia.Application.Current;
        bool dark = app != null && app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        return dark ? Brushes.Gainsboro : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    }

    /// <summary>The line.</summary>
    public ConsoleLine Line { get; }

    /// <summary>Time, prefix and text.</summary>
    public string Text { get; }

    /// <summary>Colour by kind; never null (a null foreground paints nothing).</summary>
    public IBrush Brush { get; }
}

/// <summary>One frame row.</summary>
public sealed class FrameRecordViewModel
{
    /// <summary>
    /// Wrap a record.
    /// </summary>
    /// <param name="record">The record.</param>
    public FrameRecordViewModel(FrameRecord record)
    {
        Record = record;
        Text = record.Time.ToString("HH:mm:ss.fff") + " " + (record.Sent ? "TX " : "RX ")
               + record.Source.ToString().PadRight(7) + " " + record.Bytes.Length.ToString().PadLeft(4) + " B  "
               + Describe(record);
        // Readable on light and dark alike: control purple, bad FCS red, sent blue, received green.
        Brush = record.IsControl ? Brushes.MediumOrchid
              : !record.FcsGood ? Brushes.Crimson
              : record.Sent ? Brushes.RoyalBlue
              : Brushes.SeaGreen;
    }

    /// <summary>The record.</summary>
    public FrameRecord Record { get; }

    /// <summary>The row text.</summary>
    public string Text { get; }

    /// <summary>Colour: control purple, bad FCS red, sent blue, received green.</summary>
    public IBrush Brush { get; }

    /// <summary>The detail panel text: description then hex dump.</summary>
    public string Detail => Record.Describe() + "\r\n" + Record.HexDump();

    private static string Describe(FrameRecord r)
    {
        if (r.IsControl && r.ControlHeader.HasValue)
        {
            return "CTRL " + r.ControlHeader.Value.Type + " id " + r.ControlHeader.Value.Id;
        }
        if (r.Bytes.Length < 2)
        {
            return "short";
        }
        string s = "addr " + r.Bytes[0].ToString("X2") + "  " + Core.HdlcControl.Decode(r.Bytes[1]).Summary();
        if (!r.FcsGood)
        {
            s += "  FCS BAD";
        }
        int shown = Math.Min(8, r.Bytes.Length);
        System.Text.StringBuilder sb = new System.Text.StringBuilder(s);
        sb.Append("  ");
        for (int i = 0; i < shown; i++)
        {
            sb.Append(r.Bytes[i].ToString("X2")).Append(' ');
        }
        if (r.Bytes.Length > shown)
        {
            sb.Append("...");
        }
        return sb.ToString();
    }
}
