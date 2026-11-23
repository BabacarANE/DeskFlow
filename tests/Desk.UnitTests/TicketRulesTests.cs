using Desk.Core.Domain;
using Desk.Core.Rules;

namespace Desk.UnitTests;

public class TicketStateMachineTests
{
    private static readonly HashSet<(TicketStatus, TicketStatus)> Allowed =
    [
        (TicketStatus.New, TicketStatus.Open),
        (TicketStatus.Open, TicketStatus.Pending),
        (TicketStatus.Pending, TicketStatus.Open),
        (TicketStatus.Open, TicketStatus.Resolved),
        (TicketStatus.Resolved, TicketStatus.Open),
        (TicketStatus.Resolved, TicketStatus.Closed),
    ];

    public static TheoryData<TicketStatus, TicketStatus, bool> AllPairs()
    {
        var data = new TheoryData<TicketStatus, TicketStatus, bool>();
        foreach (var from in Enum.GetValues<TicketStatus>())
        {
            foreach (var to in Enum.GetValues<TicketStatus>())
            {
                data.Add(from, to, Allowed.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void EveryPair_FollowsTheSpecificationTable(TicketStatus from, TicketStatus to, bool expected) =>
        Assert.Equal(expected, TicketStateMachine.CanTransition(from, to));

    [Fact]
    public void ThereAreExactlyTwentyFivePairsAndSixAllowed()
    {
        Assert.Equal(25, Enum.GetValues<TicketStatus>().Length * Enum.GetValues<TicketStatus>().Length);
        Assert.Equal(6, Allowed.Count);
    }

    [Fact]
    public void Closed_IsTerminal() => Assert.Empty(TicketStateMachine.NextStatuses(TicketStatus.Closed));

    [Theory]
    [InlineData(TicketStatus.New, true)]
    [InlineData(TicketStatus.Open, true)]
    [InlineData(TicketStatus.Pending, false)]
    [InlineData(TicketStatus.Resolved, false)]
    [InlineData(TicketStatus.Closed, false)]
    public void SlaRuns_OnlyInNewAndOpen(TicketStatus status, bool runs) =>
        Assert.Equal(runs, TicketStateMachine.SlaRuns(status));
}

public class SlaCalculatorTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Priority.Urgent, 30, 240)]
    [InlineData(Priority.High, 120, 480)]
    [InlineData(Priority.Normal, 480, 1440)]
    [InlineData(Priority.Low, 1440, 4320)]
    public void DefaultPolicies_GiveTheSpecificationDeadlines(Priority priority, int response, int resolution)
    {
        var policy = SlaPolicy.CreateDefaults(Guid.NewGuid()).Single(p => p.Priority == priority);

        var due = SlaCalculator.Compute(Created, policy.FirstResponseMinutes, policy.ResolutionMinutes);

        Assert.Equal(Created.AddMinutes(response), due.ResponseDueAt);
        Assert.Equal(Created.AddMinutes(resolution), due.ResolutionDueAt);
    }

    [Fact]
    public void PausedTime_IsAddedToBothDeadlines()
    {
        var due = SlaCalculator.Compute(Created, 30, 240, TimeSpan.FromMinutes(45));

        Assert.Equal(Created.AddMinutes(75), due.ResponseDueAt);
        Assert.Equal(Created.AddMinutes(285), due.ResolutionDueAt);
    }

    [Fact]
    public void PausedDuration_NeverNegative() =>
        Assert.Equal(TimeSpan.Zero, SlaCalculator.PausedDuration(Created, Created.AddMinutes(-5)));

    [Fact]
    public void Shift_MovesBothDeadlines()
    {
        var shifted = SlaCalculator.Shift(new SlaDeadlines(Created, Created.AddHours(1)), TimeSpan.FromMinutes(10));

        Assert.Equal(Created.AddMinutes(10), shifted.ResponseDueAt);
        Assert.Equal(Created.AddMinutes(70), shifted.ResolutionDueAt);
    }
}

public class TicketBehaviourTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private static Ticket NewTicket(Priority priority = Priority.Normal)
    {
        var policy = SlaPolicy.CreateDefaults(Guid.NewGuid()).Single(p => p.Priority == priority);
        var due = SlaCalculator.Compute(Created, policy.FirstResponseMinutes, policy.ResolutionMinutes);
        return new Ticket { Priority = priority, CreatedAt = Created, UpdatedAt = Created, ResponseDueAt = due.ResponseDueAt, ResolutionDueAt = due.ResolutionDueAt };
    }

    private static Ticket Open()
    {
        var ticket = NewTicket();
        Assert.True(ticket.ChangeStatus(TicketStatus.Open, Created));
        return ticket;
    }

    [Fact]
    public void InvalidTransition_ChangesNothing()
    {
        var ticket = NewTicket();

        Assert.False(ticket.ChangeStatus(TicketStatus.Resolved, Created.AddHours(1)));

        Assert.Equal(TicketStatus.New, ticket.Status);
        Assert.Null(ticket.ResolvedAt);
    }

    [Fact]
    public void LeavingPending_ShiftsDeadlinesByTheTimeSpentPending()
    {
        var ticket = Open();
        var responseDue = ticket.ResponseDueAt!.Value;
        var resolutionDue = ticket.ResolutionDueAt!.Value;

        ticket.ChangeStatus(TicketStatus.Pending, Created.AddHours(1));
        Assert.Equal(responseDue, ticket.ResponseDueAt);
        ticket.ChangeStatus(TicketStatus.Open, Created.AddHours(4));

        Assert.Equal(responseDue.AddHours(3), ticket.ResponseDueAt);
        Assert.Equal(resolutionDue.AddHours(3), ticket.ResolutionDueAt);
        Assert.Null(ticket.PendingSince);
        Assert.Equal(TimeSpan.FromHours(3), ticket.SlaPausedTotal);
    }

    [Fact]
    public void TwoPendingPeriods_Accumulate()
    {
        var ticket = Open();
        var resolutionDue = ticket.ResolutionDueAt!.Value;

        ticket.ChangeStatus(TicketStatus.Pending, Created.AddHours(1));
        ticket.ChangeStatus(TicketStatus.Open, Created.AddHours(2));
        ticket.ChangeStatus(TicketStatus.Pending, Created.AddHours(3));
        ticket.ChangeStatus(TicketStatus.Open, Created.AddHours(5));

        Assert.Equal(resolutionDue.AddHours(3), ticket.ResolutionDueAt);
    }

    [Fact]
    public void PriorityChange_RecomputesFromCreation_NotFromNow()
    {
        var ticket = Open();

        ticket.ChangePriority(Priority.Urgent, 30, 240);

        Assert.Equal(Priority.Urgent, ticket.Priority);
        Assert.Equal(Created.AddMinutes(30), ticket.ResponseDueAt);
        Assert.Equal(Created.AddMinutes(240), ticket.ResolutionDueAt);
    }

    [Fact]
    public void PriorityChange_KeepsPendingTimeAlreadySpent()
    {
        var ticket = Open();
        ticket.ChangeStatus(TicketStatus.Pending, Created.AddHours(1));
        ticket.ChangeStatus(TicketStatus.Open, Created.AddHours(3));

        ticket.ChangePriority(Priority.High, 120, 480);

        Assert.Equal(Created.AddMinutes(120).AddHours(2), ticket.ResponseDueAt);
        Assert.Equal(Created.AddMinutes(480).AddHours(2), ticket.ResolutionDueAt);
    }

    [Fact]
    public void Resolved_SetsResolvedAt_AndReopeningClearsIt()
    {
        var ticket = Open();

        ticket.ChangeStatus(TicketStatus.Resolved, Created.AddHours(2));
        Assert.Equal(Created.AddHours(2), ticket.ResolvedAt);

        ticket.ChangeStatus(TicketStatus.Open, Created.AddHours(3));
        Assert.Null(ticket.ResolvedAt);
    }

    [Fact]
    public void Closing_KeepsResolvedAt()
    {
        var ticket = Open();
        ticket.ChangeStatus(TicketStatus.Resolved, Created.AddHours(2));

        ticket.ChangeStatus(TicketStatus.Closed, Created.AddHours(5));

        Assert.Equal(Created.AddHours(2), ticket.ResolvedAt);
    }
}

public class TicketPolicyTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Theory]
    [InlineData(Role.SuperAdmin, false)]
    [InlineData(Role.TenantAdmin, true)]
    [InlineData(Role.Agent, true)]
    [InlineData(Role.Requester, true)]
    public void CanCreate(Role role, bool expected) => Assert.Equal(expected, TicketPolicy.CanCreate(role));

    [Theory]
    [InlineData(Role.SuperAdmin, false)]
    [InlineData(Role.TenantAdmin, true)]
    [InlineData(Role.Agent, true)]
    [InlineData(Role.Requester, false)]
    public void CanModifyAndPostInternal_AreForStaffOnly(Role role, bool expected)
    {
        Assert.Equal(expected, TicketPolicy.CanModify(role));
        Assert.Equal(expected, TicketPolicy.CanPostInternal(role));
    }

    [Fact]
    public void RequesterSeesOnlyHisTickets_StaffSeesAll_SuperAdminNone()
    {
        Assert.True(TicketPolicy.CanView(Role.Requester, Me, Me));
        Assert.False(TicketPolicy.CanView(Role.Requester, Me, Other));
        Assert.True(TicketPolicy.CanView(Role.Agent, Me, Other));
        Assert.True(TicketPolicy.CanView(Role.TenantAdmin, Me, Other));
        Assert.False(TicketPolicy.CanView(Role.SuperAdmin, Me, Other));
        Assert.False(TicketPolicy.CanView(Role.SuperAdmin, Me, Me));
    }

    [Theory]
    [InlineData(Role.Requester, true, false)]
    [InlineData(Role.Requester, false, true)]
    [InlineData(Role.Agent, true, true)]
    [InlineData(Role.TenantAdmin, true, true)]
    [InlineData(Role.SuperAdmin, false, false)]
    public void CanSeeComment_HidesInternalNotesFromRequesters(Role role, bool isInternal, bool expected) =>
        Assert.Equal(expected, TicketPolicy.CanSeeComment(role, isInternal));

    [Theory]
    [InlineData(Role.Agent, true, true)]
    [InlineData(Role.TenantAdmin, true, true)]
    [InlineData(Role.Requester, true, false)]
    [InlineData(Role.SuperAdmin, true, false)]
    [InlineData(Role.Agent, false, false)]
    public void Assignable_IsAnActiveAgentOrTenantAdmin(Role role, bool active, bool expected) =>
        Assert.Equal(expected, TicketPolicy.IsAssignable(role, active));

    [Fact]
    public void FirstResponse_NeedsAPublicStaffAnswerFromSomeoneOtherThanTheRequester()
    {
        Assert.True(TicketPolicy.CountsAsFirstResponse(Role.Agent, Me, Other, isInternal: false));
        Assert.False(TicketPolicy.CountsAsFirstResponse(Role.Agent, Me, Other, isInternal: true));
        Assert.False(TicketPolicy.CountsAsFirstResponse(Role.Requester, Me, Other, isInternal: false));
        Assert.False(TicketPolicy.CountsAsFirstResponse(Role.Agent, Me, Me, isInternal: false));
    }

    [Theory]
    [InlineData(Role.Requester, TicketStatus.Pending, true)]
    [InlineData(Role.Requester, TicketStatus.Open, false)]
    [InlineData(Role.Requester, TicketStatus.Resolved, false)]
    [InlineData(Role.Agent, TicketStatus.Pending, false)]
    public void OnlyARequesterReopensAPendingTicket(Role role, TicketStatus status, bool expected) =>
        Assert.Equal(expected, TicketPolicy.ReopensPending(role, status));
}
