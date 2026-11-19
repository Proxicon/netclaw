// -----------------------------------------------------------------------
// <copyright file="SignalRScheduledReportsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Daemon.Gateway;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SignalRScheduledReportsTests
{
    private static readonly SessionId Session = new("signalr/receipt-test");
    private static readonly SignalRConnectionId Owner = SignalRConnectionId.Create("owner");
    private static readonly SignalRConnectionId Other = SignalRConnectionId.Create("other");
    private readonly SignalRScheduledReports _reports = new(Session);

    [Theory]
    [InlineData("", "\n", true)]
    [InlineData("Quoted: ", "\n", false)]
    [InlineData("", " ", false)]
    public void The_marker_must_be_the_first_complete_line(string before, string separator, bool valid)
    {
        var id = Guid.NewGuid();
        var text = $"{before}[netratel-investigation:{Guid.NewGuid():D}:{id:D}]{separator}Request";
        Assert.Equal(valid ? id : (Guid?)null, SignalRScheduledReports.ReadMessageId(text));
    }

    [Fact]
    public void Missing_message_text_has_no_app_message_identity()
        => Assert.Null(SignalRScheduledReports.ReadMessageId(null));

    [Fact]
    public void Duplicate_terminal_confirms_neither_the_same_fire_again_nor_a_sibling()
    {
        var origin = new SignalRMessageOrigin(Owner.Value, Guid.NewGuid(), () => true);
        var first = new SignalRReminderSaved(origin, "report-a", Guid.NewGuid(), false, new TurnNumber(1));
        var second = first with { Id = "report-b", RegistrationId = Guid.NewGuid() };
        var registered = _reports.Project(ToolResult(first), Owner);
        Assert.Equal(new ScheduledReminderDto(first.Id, origin.MessageId, first.RegistrationId, false), registered.ScheduledReminder);
        Assert.Equal(first.TurnNumber, registered.TurnNumber);
        Assert.NotNull(_reports.Project(ToolResult(second), Owner).ScheduledReminder);

        const string finalText = "\nFinal A  \n";
        var terminal = Terminal("report-a:1000", first.RegistrationId, finalText, 2);
        var delivered = _reports.Project(terminal, Owner);
        Assert.Equal("report-a:1000", delivered.SourceReminderId);
        Assert.Equal(new TurnNumber(2), delivered.TurnNumber);
        Assert.Equal("completed", delivered.TurnOutcome);
        Assert.Equal(new ScheduledReportDto(origin.MessageId, first.RegistrationId, finalText), delivered.ScheduledReport);
        Assert.Null(_reports.Project(terminal, Owner).ScheduledReport);
        Assert.Null(_reports.Project(terminal with { TurnNumber = new TurnNumber(3) }, Owner).ScheduledReport);
        Assert.Null(_reports.Project(terminal with { SourceReminderId = new ReminderId("report-b:1001") }, Owner).ScheduledReport);

        var sibling = _reports.Project(Terminal("report-b:1001", second.RegistrationId, "Final B", 3), Owner);
        Assert.Equal(new ScheduledReportDto(origin.MessageId, second.RegistrationId, "Final B"), sibling.ScheduledReport);
    }

    [Fact]
    public void Only_a_completed_fire_of_the_observed_generation_reaches_its_attached_owner()
    {
        var attached = true;
        var origin = new SignalRMessageOrigin(Owner.Value, Guid.NewGuid(), () => attached);
        var saved = new SignalRReminderSaved(origin, "report", Guid.NewGuid(), true, new TurnNumber(1));
        Assert.Null(_reports.Project(ToolResult(saved), Other).ScheduledReminder);
        Assert.Null(_reports.Project(ToolResult(saved) with { FailureCode = "access_denied" }, Owner).ScheduledReminder);
        Assert.NotNull(_reports.Project(ToolResult(saved), Owner).ScheduledReminder);

        var terminal = Terminal("report:1000", saved.RegistrationId, "First", 2);
        Assert.Null(_reports.Project(Terminal("report:1000", Guid.NewGuid(), "Replacement", 2), Owner).ScheduledReport);
        Assert.Null(_reports.Project(terminal, Other).ScheduledReport);
        Assert.Null(_reports.Project(terminal with { Outcome = TurnOutcome.Failed }, Owner).ScheduledReport);
        Assert.Null(_reports.Project(terminal with { QuietEnd = true }, Owner).ScheduledReport);
        Assert.Null(_reports.Project(terminal with { SignalRReport = null }, Owner).ScheduledReport);
        Assert.NotNull(_reports.Project(terminal, Owner).ScheduledReport);

        var next = Terminal("report:2000", saved.RegistrationId, "Second", 3);
        Assert.NotNull(_reports.Project(next, Owner).ScheduledReport);
        Assert.Null(_reports.Project(terminal, Owner).ScheduledReport);
        attached = false;
        Assert.Null(_reports.Project(Terminal("report:3000", saved.RegistrationId, "Third", 4), Owner).ScheduledReport);
    }

    private static ToolResultOutput ToolResult(SignalRReminderSaved saved) => new()
    {
        SessionId = Session,
        ToolName = new ToolName("set_reminder"),
        CallId = new ToolCallId("scheduled"),
        Result = "Registered.",
        SignalRReminder = saved
    };

    private static TurnCompleted Terminal(string key, Guid registrationId, string text, int turn) => new()
    {
        SessionId = Session,
        SourceReminderId = new ReminderId(key),
        TurnNumber = new TurnNumber(turn),
        OpenChildRunIds = [],
        SignalRReport = new SignalRReminderFinal(registrationId, text)
    };
}
