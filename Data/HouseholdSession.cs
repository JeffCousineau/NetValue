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
    public Guid UserId => repository.UserId(principal);
    public List<HouseholdChoice> Households() => repository.Households(principal);
    public string CreateInvitation() => repository.CreateInvitation(principal, Access!.Id);
    public List<HouseholdInvitation> Invitations() => repository.Invitations(principal, Access!.Id);
    public void RevokeInvitation(Guid id) => repository.RevokeInvitation(principal, Access!.Id, id);
    public InvitationPreview PreviewInvitation(string token) => repository.PreviewInvitation(principal, token);
    public void AcceptInvitation(string token) => Access = repository.AcceptInvitation(principal, token);
    public void RemoveMember(Guid userId) => repository.RemoveMember(principal, Access!.Id, userId);
    public void SetMemberRole(Guid userId, HouseholdRole role) => repository.SetMemberRole(principal, Access!.Id, userId, role);
    public bool CanCreateHousehold { get; private set; }
    public void CreateHousehold(string name) => Access = repository.CreateHousehold(principal, name);
    public async Task InitializeAsync(Guid? householdId = null)
    {
        principal = (await authentication.GetAuthenticationStateAsync()).User;
        CanCreateHousehold = false;
        try { Access = repository.Open(principal, householdId); }
        catch (UnauthorizedAccessException)
        {
            try { CanCreateHousehold = repository.CanCreateHousehold(principal); }
            catch (UnauthorizedAccessException) { }
            throw;
        }
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
