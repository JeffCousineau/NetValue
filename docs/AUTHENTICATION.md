# Entra authentication and household ownership

NetValue uses Microsoft OpenID Connect sign-in. With `Authentication:SelfServiceEnabled=true`, personal Microsoft accounts and work/school accounts from other Entra directories can sign in through the `common` endpoint. Microsoft issuer, signing-key issuer, signature, audience, and token lifetime validation remain enabled. Application users are identified by the combination of tenant ID and Object ID, never by email.

New users enter a household name on their first visit. Creation stores an empty household and makes the signed-in user its owner. Existing members continue to open their assigned household. Repeated submissions do not create another household; users whose access was revoked are not automatically registered again. Exports never register users. Profiles, accounts, balances, exports, and restores remain scoped to authorized household membership.

Set `Authentication:SelfServiceEnabled=false` to return to configured-directory-only sign-in and disable registration. This does not delete users or households.

## Local setup

The non-secret tenant ID, client ID, and initial owner's user Object ID are configured in `appsettings.json`. They can be overridden by environment variables or user secrets for another installation.

1. Configure the sign-in app registration for **Accounts in any organizational directory and personal Microsoft accounts**, with requested access token version **2**. The deployment repository manages this through Terraform's `foundation` stack; apply that stack as the owner before enabling self-service in production. For a separate development registration, configure the same audience. Configure the **Web** redirect URI `http://localhost:62645/signin-oidc` and `http://localhost:62645/signout-callback-oidc`. Use authorization code flow; implicit-flow checkboxes are not needed.
2. Under Certificates & secrets, create a development client secret. Copy its **Value**, not its Secret ID. Keep it outside source control.
3. In PowerShell, run `./scripts/Set-AuthenticationSecret.ps1` from the project folder. It prompts with hidden input and saves the value to .NET user secrets. Alternatively, set it locally without putting the value in the command history:

```powershell
$sdk = "$env:TEMP\netvalue-dotnet-10\dotnet.exe"
$credential = Read-Host 'Entra client secret value' -AsSecureString
$secretValue = [System.Net.NetworkCredential]::new('', $credential).Password
@{ 'Authentication:ClientSecret' = $secretValue } | ConvertTo-Json | & $sdk user-secrets set
$secretValue = $null
$credential = $null
& $sdk run
```

If .NET is installed globally, use `dotnet` instead of the temporary SDK path. The project uses the user secrets ID `NetValue-local-authentication`. User secrets are local development storage, not an encrypted vault.

4. Open `http://localhost:62645` and sign in. The development server redirects the former `127.0.0.1` URL to localhost so the callback matches the app registration.

The configured owner's first authorized visit initializes the SQLite household database and imports existing `App_Data/households.json`, or `App_Data/portfolio.json` if there is no household JSON file. Original files are left untouched. Another user cannot claim these records by being the first visitor. Registration is blocked while this legacy import is pending. An existing database is never overwritten by bootstrap configuration. See [database configuration and Azure SQL migration](DATABASE.md).

## Invite someone to your household

Open Settings → Household members → Create invitation link. Copy the link and send it privately to the intended recipient. Anyone holding the link can accept it once within seven days. No email delivery service is configured or billed. Invitations grant Member access; owners can change a member's role afterwards.

The recipient signs in with a personal or work/school Microsoft account, reviews the household name and permissions, and explicitly accepts. New recipients join directly without creating a household. Existing recipients keep their own household and can switch households using the sidebar selector. Exports and restores use the selected household. Guest users with legacy directory membership can still use `/account/login?directory=true`.

Owners can revoke pending invitations, remove members, or change roles. The last owner cannot be removed or demoted. A link becomes invalid if its creator no longer owns the household. Invitations use cryptographically random single-use secrets; only their hashes are stored in the database. Links are bearer credentials: keep them private, including HTTP logs and browser history. Invitation URLs are removed after acceptance, with no-referrer and no-store response headers.

Sessions last at most eight hours. Each financial operation checks current membership. Previously viewed data may remain in an open browser after revocation, but new reads, saves, and exports require access. Stale financial saves are rejected through household revisions. Outstanding invitation hashes are included in operator database backups; household financial backups contain neither invitations nor memberships.

## Backups and deployment

Excel exports and JSON backups include only the authenticated user's household financial data. Financial backups intentionally exclude sign-in identities and membership, so importing a backup cannot grant access or replace another household. A restored household keeps its existing owner and members. Owners alone can restore; recovery copies are household-scoped JSON backups in `App_Data/backups`.

`App_Data` is excluded from Git **and** publish output. Use the [operator database export/import commands](DATABASE.md) to preserve household and identity mappings during a move, or a Settings financial backup for an independently configured installation. SQLite files and recovery copies require local filesystem protection; Azure SQL should use managed identity and platform backups.

For Azure, set the client secret through secure environment configuration/Key Vault, use HTTPS redirect URIs, configure the public host in `AllowedHosts`, and do not set `ASPNETCORE_ENVIRONMENT=Development`. Enable MFA and restrict enterprise application assignments in Entra according to the tenant's policies. This application performs its own OIDC validation; App Service authentication is not required for the local flow. Set up HTTPS/forwarded headers appropriately before cloud deployment.

Reference: https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-oidc
