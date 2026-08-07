using Remi.Domain;
using Xunit;

namespace Remi.Tests;

public sealed class ReportingDeadlinePolicyTests
{
    [Theory]
    [InlineData("2026-06", 2026, 7, 7)]
    [InlineData("2026-07", 2026, 8, 7)]
    public void Gcloud14_uses_the_fifth_working_day_of_the_following_month(string reportingMonth, int year, int month, int day)
    {
        var deadline = Frameworks.Get(FrameworkCode.GCloud14).ReportingDeadline!.Calculate(reportingMonth);

        Assert.Equal(new DateOnly(year, month, day), deadline);
    }

    [Fact]
    public void Historical_deadline_policies_remain_attached_to_their_frameworks()
    {
        var deadline = Frameworks.Get(FrameworkCode.GCloud13).ReportingDeadline!.Calculate("2026-06");

        Assert.Equal(new DateOnly(2026, 7, 7), deadline);
    }
}
