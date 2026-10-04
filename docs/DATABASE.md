# Database storage and moving to Azure SQL

NetValue uses EF Core 10 with SQLite in Development and SQL Server/Azure SQL for deployment. Users, households, memberships, profiles, accounts, saved months, and balances have relational tables and foreign keys. Balances use C# decimal and SQL Server decimal(18,2); inputs with more than two decimal places are rejected rather than silently rounded. SQLite keeps decimal values as text, preserving exact amounts. Calculations remain in C#.

## Local data

Restart the application after updating it. In Development, the SQLite schema is applied automatically on first database access. At the configured owner's first sign-in, an empty database imports `App_Data/households.json` if present, otherwise `App_Data/portfolio.json`. Existing JSON files are left untouched. Existing database records take precedence on subsequent starts. A failed or invalid import does not partially save records. No other user is granted access by this migration.

The default database is `App_Data/netvalue.db`. This directory is excluded from Git and publish output. To keep data outside the project, set a directory in local user secrets before first run:

```powershell
dotnet user-secrets set "Database:DataDirectory" "C:\NetValueData"
```

If already using SQLite, stop the app and copy the **whole existing App_Data directory** to that directory first, including any database journal/WAL files and recovery backups. Do not change the directory while the app is running. JSON import and restore recovery copies use the configured directory too. Give the application identity filesystem access to this directory. SQLite is restricted to Development; a production process must explicitly select SqlServer.

## Backups and migration

Settings → Download full backup remains a **financial backup of the current household**, and owner-only Restore preserves existing membership. Excel exports are unchanged.

For an installation move that must preserve Entra IDs, household IDs, ownership, and membership, use the operator commands below. These run locally without starting the web server and are not exposed as web endpoints. Keep operator backups outside Git; they contain private financial and identity data. Export refuses to overwrite an existing file. Import validates the source and only accepts an empty destination database.

From the source installation (SQLite Development):

```powershell
dotnet run -- --export-database "C:\NetValueData\NetValue-operator.json"
```

After creating the destination SQL database and configuring its connection:

```powershell
dotnet run --no-launch-profile -- --migrate-database
dotnet run --no-launch-profile -- --import-database "C:\NetValueData\NetValue-operator.json"
```

The import creates all records in one transaction. It does not invite people, change tenant assignments, or create new memberships beyond those already in the backup. Keep the same Entra tenant/application configuration for the move. For a different tenant, configure new ownership and use the household financial backup instead.

For a consistent cutover, stop financial editing, export the source, import the empty destination, verify totals and sign-in, and switch traffic. Keep the source and operator backup until the destination is verified. Changing Database:Provider does not automatically move data between databases.

## Azure SQL configuration

Create Azure SQL and enable the App Service managed identity. Grant that identity database access. Configure these App Service settings outside the repository:

```text
Database__Provider=SqlServer
ConnectionStrings__NetValue=Server=tcp:<server>.database.windows.net,1433;Database=<database>;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;
Database__DataDirectory=<private writable directory for recovery copies>
```

Use Azure SQL automated backups for database recovery. Local JSON recovery copies are supplementary; use persistent protected storage for the configured data directory on App Service. Keep authentication secrets in secure App Service configuration/Key Vault as described in AUTHENTICATION.md. No connection password is needed with managed identity. See [Microsoft's SqlClient Entra authentication documentation](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/azure-active-directory-authentication).

Production does **not** automatically apply schema changes. Run `--migrate-database` once through a controlled deployment job using an identity with schema permissions. The web application identity needs CRUD access, not schema-change permission. Run operator import with a trusted identity that has CRUD access. A developer workstation can use `Authentication=Active Directory Default` with an authorized developer identity instead of Managed Identity. Store that connection string in local user secrets or environment configuration.

Provider migrations live in `Data/Migrations/Sqlite` and `Data/Migrations/SqlServer`. Both must be updated when the model changes. To scaffold or generate a reviewed SQL deployment script:

```powershell
dotnet tool update dotnet-ef --tool-path obj/ef-tools --version 10.0.12
dotnet build
./obj/ef-tools/dotnet-ef migrations add <Name> --context SqliteNetValueContext --output-dir Data/Migrations/Sqlite
./obj/ef-tools/dotnet-ef migrations add <Name> --context SqlServerNetValueContext --output-dir Data/Migrations/SqlServer
./obj/ef-tools/dotnet-ef migrations script --idempotent --context SqlServerNetValueContext --output obj/azure-sql-migrations.sql
```

Schema scripting does not require a live SQL connection. [EF Core documents separate migrations for different providers](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers).

## Concurrency and verification

Household revisions reject financial edits from stale tabs. A database-wide revision also prevents lost snapshot or membership updates across repository/application instances. Writes and imports are transactional and only changed rows are inserted, updated, or deleted. This initial implementation loads small household datasets into memory; it suits this personal tracker, but should use household-filtered database queries before scaling to many households. Multiple instances also need shared ASP.NET data-protection keys and appropriate Blazor session routing before deployment.

Run `dotnet run --project Tests/NetValue.Checks.csproj`. The checks exercise real SQLite migrations/storage, legacy JSON import, exact amounts, account order, missing versus zero, ownership boundaries, revocation, stale writes, recovery, and complete operator transfers. They also verify SQL Server migration generation and both model snapshots. A live Azure SQL integration test is still required before deployment.

The project targets .NET 10 with EF Core 10. The SDK version is pinned in global.json.

## Household invitations

The HouseholdInvitations migration adds an Invitations table for hashed, single-use links, household and creator IDs, and UTC expiry. Apply production migrations before deploying the invitation-enabled application; the existing controlled deployment workflow does this. SQLite and SQL Server migration histories are both updated. Existing financial data and memberships are unchanged. Operator backups preserve outstanding invitation hashes; financial backups exclude them. Legacy operator backups without an Invitations field remain supported.
