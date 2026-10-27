namespace ServiceDesk.Tests;

public class DomainTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(161)]
    public void Title_limits_are_enforced(int length) => Assert.Throws<ServiceDesk.Domain.RuleException>(() => ServiceDesk.Domain.Rules.Text(new string('a', length), "Title", 160));
}
