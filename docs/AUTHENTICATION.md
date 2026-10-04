# Entra authentication and household ownership

NetValue uses single-tenant Microsoft Entra OpenID Connect sign-in. Profiles, accounts, balances, exports, and restores are scoped to a household. No anonymous or development bypass grants data access.

## Local setup

The non-secret tenant ID, client ID, and initial owner's user Object ID are configured in `appsettings.json`. They can be overridden by environment variables or user secrets for another installation.

1. In Entra → App registrations → NetValue → Authentication, configure the **Web** redirect URI `http://localhost:62645/signin-oidc`. Add `http://localhost:62645/signout-callback-oidc` as another Web redirect URI for sign-out. Use authorization code flow; implicit-flow checkboxes are not needed.
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

The configured owner's first authorized visit initializes the SQLite household database and imports existing `App_Data/households.json`, or `App_Data/portfolio.json` if there is no household JSON file. Original files are left untouched. Another user cannot claim these records by being the first visitor. An existing database is never overwritten by bootstrap configuration. See [database configuration and Azure SQL migration](DATABASE.md).

## Give your wife access

Invite her account into the same Entra directory and have her redeem the invitation. In Entra → Users, copy her user Object ID. As the household owner, open Settings → Household members, enter her name and Object ID, and select Member or Owner. Sign-in itself does not grant household access. Owner privileges come from stored membership, not email addresses or browser-selected IDs.

An owner can revoke membership or update the role; the last owner cannot be removed or demoted. Each data operation checks current membership. Sessions last at most eight hours. Reload the page after any membership change; a previously opened browser can retain information already viewed but cannot save or export after access is revoked. Stale financial saves are rejected through household revisions.

## Backups and deployment

Excel exports and JSON backups include only the authenticated user's household financial data. Financial backups intentionally exclude sign-in identities and membership, so importing a backup cannot grant access or replace another household. A restored household keeps its existing owner and members. Owners alone can restore; recovery copies are household-scoped JSON backups in `App_Data/backups`.

`App_Data` is excluded from Git **and** publish output. Use the [operator database export/import commands](DATABASE.md) to preserve household and identity mappings during a move, or a Settings financial backup for an independently configured installation. SQLite files and recovery copies require local filesystem protection; Azure SQL should use managed identity and platform backups.

For Azure, set the client secret through secure environment configuration/Key Vault, use HTTPS redirect URIs, configure the public host in `AllowedHosts`, and do not set `ASPNETCORE_ENVIRONMENT=Development`. Enable MFA and restrict enterprise application assignments in Entra according to the tenant's policies. This application performs its own OIDC validation; App Service authentication is not required for the local flow. Set up HTTPS/forwarded headers appropriately before cloud deployment.

Reference: https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-oidc
