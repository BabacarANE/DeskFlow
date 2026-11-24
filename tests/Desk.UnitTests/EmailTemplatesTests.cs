using Desk.Core.Domain;
using Desk.Infrastructure.Email;

namespace Desk.UnitTests;

public class EmailTemplatesTests
{
    private const string Hostile = "<img src=x onerror=alert(1)> & \"quote\"";

    public static TheoryData<string, EmailContent> Templates => new()
    {
        { "assigned", EmailTemplates.TicketAssigned(Hostile, 12, Hostile, "http://x.test/t?a=1&b=2") },
        { "reply to requester", EmailTemplates.ReplyToRequester(Hostile, 12, Hostile, "http://x.test/t?a=1&b=2") },
        { "reply to assignee", EmailTemplates.ReplyToAssignee(Hostile, 12, Hostile, "http://x.test/t?a=1&b=2") },
        { "escalated", EmailTemplates.TicketEscalated(Hostile, 12, Hostile, "response", Priority.Normal, Priority.High, "http://x.test/t?a=1&b=2") },
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void User_values_are_encoded_in_the_html_and_never_in_the_subject(string name, EmailContent content)
    {
        Assert.StartsWith("[#12] ", content.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("<", content.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("onerror", content.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", content.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", content.Html, StringComparison.Ordinal);
        Assert.Contains("a=1&amp;b=2", content.Html, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Theory]
    [InlineData(Priority.Normal, Priority.High, "passe de Normale à Haute")]
    [InlineData(Priority.Urgent, Priority.Urgent, "reste inchangée")]
    public void The_escalation_states_the_priority_change(Priority from, Priority to, string expected)
    {
        var content = EmailTemplates.TicketEscalated("Ada", 3, "Titre", "resolution", from, to, "http://x.test/t");

        Assert.Contains(expected, content.Text, StringComparison.Ordinal);
        Assert.Contains("résolution", content.Text, StringComparison.Ordinal);
    }
}
