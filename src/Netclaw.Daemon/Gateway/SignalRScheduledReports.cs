// -----------------------------------------------------------------------
// <copyright file="SignalRScheduledReports.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>Confirmation state for the live SignalR attachment that received the registration.</summary>
internal sealed class SignalRScheduledReports(SessionId sessionId)
{
    // These are the observation and text limits of the v1 client.
    private const int MaximumRegistrations = 128;
    private const int MaximumFinalLength = 128_000;
    private readonly Dictionary<Guid, Registration> _registrations = [];

    public static Guid? ReadMessageId(string? text)
    {
        const string prefix = "[netratel-investigation:";
        const int uuidLength = 36;
        var separator = prefix.Length + uuidLength;
        var end = separator + 1 + uuidLength;
        if (text is null || text.Length < end + 2 || !text.StartsWith(prefix, StringComparison.Ordinal)
            || text[separator] != ':' || text[end] != ']' || text[end + 1] != '\n'
            || !Guid.TryParseExact(text.AsSpan(prefix.Length, uuidLength), "D", out var conversationId)
            || conversationId == Guid.Empty
            || !Guid.TryParseExact(text.AsSpan(separator + 1, uuidLength), "D", out var messageId)
            || messageId == Guid.Empty)
            return null;

        return messageId;
    }

    public void Clear() => _registrations.Clear();

    public SessionOutputDto Project(SessionOutput output, SignalRConnectionId connectionId)
    {
        var frame = SessionOutputDtoMapper.ToDto(output);
        if (output.SessionId != sessionId)
            return frame;

        return output switch
        {
            ToolResultOutput { FailureCode: null, SignalRReminder: { } saved } => ConfirmRegistration(frame, saved, connectionId),
            TurnCompleted { Outcome: TurnOutcome.Completed, QuietEnd: false, SignalRReport: { } final }
                => ConfirmReport(frame, final, connectionId),
            _ => frame
        };
    }

    private SessionOutputDto ConfirmRegistration(SessionOutputDto frame, SignalRReminderSaved saved, SignalRConnectionId connectionId)
    {
        if (saved.Origin.ConnectionId != connectionId.Value || !saved.Origin.IsAttached())
            return frame;

        if (!_registrations.TryGetValue(saved.RegistrationId, out var registration))
        {
            if (_registrations.Count >= MaximumRegistrations)
                return frame;

            registration = new Registration(saved);
            _registrations.Add(saved.RegistrationId, registration);
        }

        if (registration.Saved != saved)
            return frame;

        return frame with
        {
            TurnNumber = saved.TurnNumber,
            ScheduledReminder = new ScheduledReminderDto(saved.Id, saved.Origin.MessageId, saved.RegistrationId, saved.Recurring)
        };
    }

    private SessionOutputDto ConfirmReport(SessionOutputDto frame, SignalRReminderFinal final, SignalRConnectionId connectionId)
    {
        if (final.Text.Length > MaximumFinalLength || string.IsNullOrWhiteSpace(final.Text)
            || !_registrations.TryGetValue(final.RegistrationId, out var registration)
            || registration.Saved.Origin.ConnectionId != connectionId.Value || !registration.Saved.Origin.IsAttached()
            || frame.TurnNumber is not { } turn || turn.Value <= registration.Saved.TurnNumber.Value
            || !ReminderFireKey.TryParse(frame.SourceReminderId, out var fire) || fire.Value != frame.SourceReminderId
            || fire.DefinitionId.Value != registration.Saved.Id || fire.FireTimeMs <= registration.LastFireTimeMs
            || (!registration.Saved.Recurring && registration.LastFireTimeMs != 0))
            return frame;

        registration.LastFireTimeMs = fire.FireTimeMs;
        return frame with { ScheduledReport = new ScheduledReportDto(registration.Saved.Origin.MessageId, final.RegistrationId, final.Text) };
    }

    private sealed class Registration(SignalRReminderSaved saved)
    {
        public SignalRReminderSaved Saved { get; } = saved;
        public long LastFireTimeMs { get; set; }
    }
}
