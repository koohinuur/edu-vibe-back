using FluentAssertions;
using LMS.Application.Common.Security;
using Xunit;

namespace LMS.Tests;

/// <summary>
/// The bulk-import generator must always emit a password the platform policy
/// accepts (length + at least three of four character classes), otherwise a
/// freshly-provisioned student couldn't log in with the password we handed out.
/// </summary>
public sealed class PasswordGeneratorTests
{
    [Fact]
    public void Generated_passwords_always_satisfy_the_policy()
    {
        for (var i = 0; i < 500; i++)
        {
            var pwd = PasswordGenerator.Generate();
            PasswordPolicy.IsValid(pwd).Should().BeTrue($"generated '{pwd}' should pass the policy");
        }
    }

    [Fact]
    public void Respects_requested_length_and_floors_short_requests()
    {
        PasswordGenerator.Generate(20).Length.Should().Be(20);
        // Anything below the policy minimum is bumped up to a safe length.
        PasswordGenerator.Generate(3).Length.Should().BeGreaterThanOrEqualTo(PasswordPolicy.MinLength);
    }

    [Fact]
    public void Successive_calls_are_not_identical()
    {
        var a = PasswordGenerator.Generate();
        var b = PasswordGenerator.Generate();
        a.Should().NotBe(b);
    }
}
