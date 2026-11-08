// HDLC CONTROL - the 8-bit control field decoded, as the gateways' DECODE and INSPECT print it.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>The three HDLC frame classes, told apart by the low control bits.</summary>
public enum HdlcFrameClass
{
    /// <summary>Information: bit 0 = 0, carries N(S) and N(R).</summary>
    Information,

    /// <summary>Supervisory: bits 1..0 = 01, carries N(R).</summary>
    Supervisory,

    /// <summary>Unnumbered: bits 1..0 = 11, M1 and M2 name the type.</summary>
    Unnumbered,
}

/// <summary>
/// A decoded modulo-8 control byte (ISO 13239 5.4). Port of hdlc_decode_control in the HDLCGW
/// firmware, same table and same names, so the PC shows what the gateway's console shows.
/// RC422Dev's decoder has a bug that never decodes REJ and SREJ (it tests 0x10 and 0x11 instead
/// of 2 and 3); this one, like HDLCGW's, uses the right values.
/// </summary>
/// <param name="Class">I, S or U.</param>
/// <param name="SendSequence">N(S), I frames only.</param>
/// <param name="ReceiveSequence">N(R), I and S frames.</param>
/// <param name="M1">U frames: bits 3..2.</param>
/// <param name="M2">U frames: bits 7..5.</param>
/// <param name="PollFinal">Bit 4.</param>
/// <param name="Name">Short type name: RR, SABM, UA, ...; empty for an I frame.</param>
/// <param name="Description">One-line meaning of <paramref name="Name"/>.</param>
public readonly record struct HdlcControl(HdlcFrameClass Class, int SendSequence, int ReceiveSequence,
                                          int M1, int M2, bool PollFinal, string Name, string Description)
{
    private readonly record struct UFrame(int M1, int M2, string Name, string Description);

    // Same table as the firmware (hdlc.cpp U_FRAMES) and RC422Dev. The first match wins, so a
    // command/response pair with the same bits decodes as the command.
    private static readonly UFrame[] s_uFrames =
    {
        new UFrame(0, 4, "SNRM",  "Set Normal Response Mode"),
        new UFrame(3, 0, "SARM",  "Set Asynchronous Response Mode"),
        new UFrame(3, 1, "SABM",  "Set Asynchronous Balanced Mode"),
        new UFrame(0, 2, "DISC",  "Disconnect"),
        new UFrame(3, 6, "SNRME", "Set Normal Response Mode Extended"),
        new UFrame(3, 2, "SARME", "Set Asynchronous Response Mode Extended"),
        new UFrame(3, 3, "SABME", "Set Asynchronous Balanced Mode Extended"),
        new UFrame(1, 0, "SIM",   "Set Initialization Mode"),
        new UFrame(0, 1, "UP",    "Unnumbered Poll"),
        new UFrame(0, 0, "UI",    "Unnumbered Information"),
        new UFrame(3, 5, "XID",   "Exchange Identification"),
        new UFrame(3, 4, "RSET",  "Reset (combined station only)"),
        new UFrame(0, 7, "TEST",  "Test Command"),
        new UFrame(0, 6, "SM",    "Set Mode"),
        new UFrame(3, 7, "UIH",   "Unnumbered Information with Header check"),
        new UFrame(0, 3, "UA",    "Unnumbered Acknowledge"),
        new UFrame(1, 4, "FRMR",  "Frame Reject"),
        new UFrame(3, 0, "DM",    "Disconnected Mode"),
        new UFrame(0, 2, "RD",    "Request Disconnect"),
        new UFrame(1, 0, "RIM",   "Request Initialization Mode"),
    };

    private static readonly string[] s_sNames = { "RR", "RNR", "REJ", "SREJ" };
    private static readonly string[] s_sDescriptions =
    {
        "Receive Ready", "Receive Not Ready", "Reject", "Selective Reject",
    };

    /// <summary>One-letter class name: I, S or U.</summary>
    public string ClassLetter => Class switch
    {
        HdlcFrameClass.Information => "I",
        HdlcFrameClass.Supervisory => "S",
        _ => "U",
    };

    /// <summary>
    /// Decode a control byte.
    /// </summary>
    /// <param name="control">The control byte, normally the second byte of a frame.</param>
    /// <returns>The decoded fields.</returns>
    public static HdlcControl Decode(byte control)
    {
        bool pollFinal = (control & 0x10) != 0;

        if ((control & 0x01) == 0)
        {
            // I frame: 0 N(S)x3 P/F N(R)x3
            return new HdlcControl(HdlcFrameClass.Information, (control >> 1) & 0x07, (control >> 5) & 0x07,
                                   0, 0, pollFinal, string.Empty, string.Empty);
        }
        if ((control & 0x03) == 0x01)
        {
            // S frame: 1 0 SS P/F N(R)x3
            int s = (control >> 2) & 0x03;
            return new HdlcControl(HdlcFrameClass.Supervisory, 0, (control >> 5) & 0x07, 0, 0, pollFinal,
                                   s_sNames[s], s_sDescriptions[s]);
        }
        // U frame: 1 1 M1x2 P/F M2x3
        int m1 = (control >> 2) & 0x03;
        int m2 = (control >> 5) & 0x07;
        string name = "UNKNOWN";
        string description = "Unknown frame type";
        for (int i = 0; i < s_uFrames.Length; i++)
        {
            if (s_uFrames[i].M1 == m1 && s_uFrames[i].M2 == m2)
            {
                name = s_uFrames[i].Name;
                description = s_uFrames[i].Description;
                break;
            }
        }
        return new HdlcControl(HdlcFrameClass.Unnumbered, 0, 0, m1, m2, pollFinal, name, description);
    }

    /// <summary>
    /// One short line for a frame list: "I N(S)=3 N(R)=5 P", "RR N(R)=2", "SABM P".
    /// </summary>
    /// <returns>The summary.</returns>
    public string Summary()
    {
        string pf = PollFinal ? " P/F" : string.Empty;
        return Class switch
        {
            HdlcFrameClass.Information => "I N(S)=" + SendSequence + " N(R)=" + ReceiveSequence + pf,
            HdlcFrameClass.Supervisory => Name + " N(R)=" + ReceiveSequence + pf,
            _ => Name + pf,
        };
    }
}
