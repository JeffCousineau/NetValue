using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetValue.Components;
using NetValue.Data;

internal static class HouseholdLoadChecks
{
    public static async Task Run()
    {
        var tenant = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:TenantId"] = tenant.ToString(),
            ["Households:BootstrapOwnerObjectId"] = owner.ToString()
        }).Build();
        var repository = new HouseholdRepository(new StoreEnvironment(Path.Combine(Directory.GetCurrentDirectory(), "obj", "household-retry-" + Guid.NewGuid().ToString("N"))), configuration);
        var authentication = new RecoveringAuthentication(new ClaimsPrincipal(new ClaimsIdentity([
            new("tid", tenant.ToString()), new("oid", owner.ToString()),
            new("netvalue:expires", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())
        ], "Test")));
        var session = new HouseholdSession(authentication, repository);
        var dashboard = new Dashboard();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(Dashboard).GetProperty("Store", flags)!.SetValue(dashboard, session);
        typeof(Dashboard).GetProperty("Logger", flags)!.SetValue(dashboard, NullLogger<Dashboard>.Instance);
        var load = typeof(Dashboard).GetMethod("LoadHouseholdAsync", flags)!;
        var failed = typeof(Dashboard).GetField("loadFailed", flags)!;
        var error = typeof(Dashboard).GetField("error", flags)!;
        await (Task)load.Invoke(dashboard, null)!;
        if (!(bool)failed.GetValue(dashboard)! || session.Access is not null) throw new Exception("Failed loading must keep household data inaccessible.");
        authentication.Fail = false;
        await (Task)load.Invoke(dashboard, null)!;
        if ((bool)failed.GetValue(dashboard)! || (string)error.GetValue(dashboard)! != "" || session.Access?.Role != HouseholdRole.Owner)
            throw new Exception("Retry must clear the failed page state and load authorized household access.");
        configuration["Authentication:SelfServiceEnabled"] = "true";
        var newcomer = new RecoveringAuthentication(new ClaimsPrincipal(new ClaimsIdentity([
            new("tid", "9188040d-6c67-4c5b-b112-36a304b66dad"), new("oid", Guid.NewGuid().ToString()),
            new("netvalue:expires", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())
        ], "Test"))) { Fail = false };
        var signupSession = new HouseholdSession(newcomer, repository);
        typeof(Dashboard).GetProperty("Store", flags)!.SetValue(dashboard, signupSession);
        await (Task)load.Invoke(dashboard, null)!;
        if (!(bool)failed.GetValue(dashboard)! || !signupSession.CanCreateHousehold || signupSession.Access is not null)
            throw new Exception("A new Microsoft user must be offered registration without loading another household.");
        typeof(Dashboard).GetField("householdName", flags)!.SetValue(dashboard, "New household");
        await (Task)typeof(Dashboard).GetMethod("CreateHouseholdAsync", flags)!.Invoke(dashboard, null)!;
        if ((bool)failed.GetValue(dashboard)! || signupSession.CanCreateHousehold || signupSession.Access?.Name != "New household"
            || signupSession.Access.Role != HouseholdRole.Owner || signupSession.Access.Profiles.Count != 0)
            throw new Exception("Successful onboarding must open the new user's empty household and clear the registration state.");
        Console.WriteLine("Household load failure and recovery checks passed.");
    }

    private sealed class RecoveringAuthentication(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public bool Fail { get; set; } = true;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Fail
            ? throw new InvalidOperationException("Temporary authentication-state failure.")
            : Task.FromResult(new AuthenticationState(principal));
    }
}
