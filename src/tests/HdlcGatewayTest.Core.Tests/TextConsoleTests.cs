// TEXT CONSOLE TESTS - the three-step ESC ESC ESC entry against gateways that answer at each step.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class TextConsoleTests
{
    /// <summary>A gateway that answers with a prompt only after the Nth write.</summary>
    private static FakeTransport AnswersOnWrite(int n)
    {
        FakeTransport t = new FakeTransport();
        int writes = 0;
        t.OnWrite = _ =>
        {
            writes++;
            if (writes == n)
            {
                t.InjectText("\r\n*** Debug console ***\r\n# ");
            }
        };
        return t;
    }

    [TestCase(1, new byte[] { 0x1B, 0x1B, 0x1B })]
    [TestCase(2, new byte[] { 0x0D })]
    [TestCase(3, new byte[] { 0x7E, 0x1B, 0x1B, 0x1B })]
    public void Enter_TriesTheThreeProbesInOrder(int answersOn, byte[] lastProbe)
    {
        FakeTransport t = AnswersOnWrite(answersOn);
        TextConsole console = new TextConsole(t);

        Assert.That(console.Enter(out string transcript), Is.True);
        Assert.That(transcript, Does.EndWith("# "));
        Assert.That(t.Writes.Count, Is.EqualTo(answersOn));
        Assert.That(t.Writes[answersOn - 1], Is.EqualTo(lastProbe));
    }

    [Test]
    public void Enter_GivesUpAfterThreeProbes()
    {
        FakeTransport t = new FakeTransport();
        TextConsole console = new TextConsole(t);
        Assert.That(console.Enter(out _), Is.False);
        Assert.That(t.Writes.Count, Is.EqualTo(3));
    }

    [Test]
    public void Command_SendsCrAndWaitsForPrompt()
    {
        FakeTransport t = new FakeTransport();
        t.OnWrite = _ => t.InjectText("BAUD 9600\r\nBaud rate set to 9600\r\n# ");
        TextConsole console = new TextConsole(t);

        Assert.That(console.Command("BAUD 9600", out string transcript), Is.True);
        Assert.That(t.Writes[0], Is.EqualTo(System.Text.Encoding.ASCII.GetBytes("BAUD 9600\r")));
        Assert.That(transcript, Does.Contain("set to 9600"));
    }
}
