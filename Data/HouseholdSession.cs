using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace NetValue.Data;

/// <summary>Uses the authenticated Blazor circuit identity, never client-supplied ownership IDs.</summary>
public sealed class HouseholdSession(AuthenticationStateProvider authentication, HouseholdRepository repository)
{
    private ClaimsPrincipal principal = new();
    public HouseholdAccess? Access { get; private set; }
    public Guid ObjectId => repository.Identity(principal).Object;
    public bool IsOwner => Access?.Role == HouseholdRole.Owner;
    public async Task InitializeAsync()
    {
        principal = (await authentication.GetAuthenticationStateAsync()).User;
        Access = repository.Open(principal);
    }
    public List<Profile> Load()
    {
        Access = repository.Open(principal, Access?.Id);
        return Access.Profiles;
    }
    public void Save(List<Profile> profiles) => Save(profiles, false);
    public void Restore(List<Profile> profiles) => Save(profiles, true);
    private void Save(List<Profile> profiles, bool restore)
    {
        if (Access is null) throw new UnauthorizedAccessException("No household access.");
        var revision = repository.Save(principal, Access.Id, Access.Revision, profiles, restore);
        Access = Access with { Revision = revision, Profiles = profiles };
    }
    public List<MemberInfo> Members() => repository.Members(principal, Access?.Id ?? throw new UnauthorizedAccessException());
    public void SetMember(Guid objectId, string name, HouseholdRole? role) => repository.SetMember(principal, Access?.Id ?? throw new UnauthorizedAccessException(), objectId, name, role);
}
