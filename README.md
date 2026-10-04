# NetValue

A Blazor Server app for monthly personal and household net worth, in CAD.

## Run

Install the .NET 10 SDK (10.0.401 or a newer patch in that SDK feature band), then run from this folder:

**Sign-in is required.** Complete [Microsoft sign-in setup](docs/AUTHENTICATION.md), including the supported account audience and development client secret in .NET user secrets, before running. Personal and work/school Microsoft accounts can sign in and create their own empty household. Existing household memberships and the initial owner's data are preserved.

```powershell
dotnet run
```

Open http://localhost:62645. Create a profile for each person, select a month, and enter positive closing balances. Add accounts as needed. Choose Household to combine all profiles. Each month is independent; choose a saved month under Copy balances from and click Copy to this month to fill missing entries while preserving balances already entered (including zero). The source selector lists only other months with balances for the selected profile and defaults to the previous month when available, otherwise the latest earlier saved month or most recent saved month.

The local URL must match the Entra Web redirect URI: http://localhost:62645/signin-oidc. A temporary SDK is available at C:\Users\jeffr\AppData\Local\Temp\netvalue-dotnet-10\dotnet.exe; invoke it instead of dotnet until a system SDK is installed.

Assets = cash + investments + property. Liabilities = short-term + long-term debts. Net worth = assets − liabilities. Available today = cash − short-term debts. Enter each person's share of property market value and mortgage separately; do not enter home equity as property value.

To overwrite the destination month when copying, check Replace all balances in this month before clicking Copy to this month. This replaces the entire destination snapshot: source balances (including zero) are copied, and accounts missing from the source are cleared. The source month is unchanged. Replacement is off by default and resets when changing profile or destination month.

Clear this month removes all saved balances for the selected profile and month after confirmation. Accounts and other months are kept. Fields display `0.00` afterwards; the cleared month is no longer counted in Progress until new values are entered or copied.

Hover over, focus, or tap a net worth, total assets, or total liabilities number to see the accounts and amounts used in its calculation. Household breakdowns include profile names, missing balances are marked, and net worth shows assets added and liabilities subtracted. Press Escape or use the close button to dismiss the breakdown.

Data saves automatically to a local SQLite database at `App_Data/netvalue.db`. The configured owner imports existing households.json or portfolio.json data once; original JSON files are left untouched. App_Data is excluded from Git and publishing. You can keep data outside the project using Database:DataDirectory. See [database setup and Azure SQL migration](docs/DATABASE.md) for configuration and operator backups that preserve ownership.

Select a profile and use Rename profile to change its name. Each account has Edit and Delete controls. Editing its name or category preserves all monthly balances; changing category applies to every month. Deleting an account requires confirmation and removes its balances from every month permanently.

When adding or editing an account, choose its bank or financial institution from the dropdown, or select Other institution to enter a custom name. The bank is optional for assets such as your home. Existing accounts keep their balances and can be assigned a bank through Edit.

Select a profile and choose Progress in the sidebar for its net worth chart and monthly history. The view includes assets, liabilities, net worth, and changes from the previous calendar month. Click a month to review its balances. Months without entries are omitted, partial months are marked, and percentage changes use the absolute previous net worth (no percentage when it was zero). Historical totals reflect current account categories and any account deletions.

Run calculation checks with `dotnet run --project Tests/NetValue.Checks.csproj`.

Choose Household in the sidebar and then Progress to see combined monthly history for all profiles, including chart point tooltips and the net worth calculation breakdown. Household totals use balances from the same calendar month; absent profile balances count as zero and are not carried forward. Partial months count entered accounts against all household accounts. Clicking a history month opens the household overview for that month.

Export all data to Excel downloads `NetValue.xlsx` with Monthly summary, Profiles, Accounts, and Balances worksheets for every profile and saved month in the authenticated household. Summary formulas recalculate from numeric balance rows. Only entered balances appear in Balances, preserving the distinction between missing values and zero. Export reads the latest successfully saved data; finish editing a balance before downloading. All amounts are CAD and formatted to two decimal places.

## Settings and migration

Settings contains household membership, Excel export, financial backups, and owner-only restore. To migrate, configure authentication and ownership in the new installation, then import a financial backup. Restore replaces only the current household financial records and preserves memberships. Original portfolio.json files remain supported. Files are validated and limited to 10 MB.

Restore replaces the current household profiles and history. Memberships and other households remain unchanged. A financial recovery copy is saved under App_Data/backups before the new data is saved atomically. Stale financial saves are rejected using household revisions.

Entra sign-in and stored household membership are required. Members can maintain profiles and balances and export household data; owners can additionally manage access and restore. Development uses SQLite; production uses SQL Server/Azure SQL with explicit schema migrations. Database writes are transactional and stale edits are rejected. [Database documentation](docs/DATABASE.md) covers moving data and preserving memberships. No profile deletion or spreadsheet import is included yet.
