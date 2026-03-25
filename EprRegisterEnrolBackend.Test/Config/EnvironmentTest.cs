using Microsoft.AspNetCore.Builder;

namespace EprRegisterEnrolBackend.Test.Config;

public class EnvironmentTest
{
    [Fact]
    public void IsNotDevModeByDefault()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        var isDev = EprRegisterEnrolBackend.Config.Environment.IsDevMode(builder);
        Assert.False(isDev);
    }
}