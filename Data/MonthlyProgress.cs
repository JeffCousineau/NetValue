using System.Globalization;

namespace NetValue.Data;

public sealed record MonthlySnapshot(DateTime Month, decimal Assets, decimal Liabilities, decimal Cash,
    int EnteredAccounts, int TotalAccounts)
{
    public decimal NetWorth => Assets - Liabilities;
}

public static class MonthlyProgress
{
    public static List<MonthlySnapshot> For(Profile profile)
    {
        var result = new List<MonthlySnapshot>();
        foreach (var (key, balances) in profile.Months)
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var entered = profile.Accounts.Where(a => balances.ContainsKey(a.Id)).ToList();
            if (entered.Count == 0) continue;
            decimal Sum(params AccountKind[] kinds) => entered.Where(a => kinds.Contains(a.Kind)).Sum(a => balances[a.Id]);
            result.Add(new(date, Sum(AccountKind.Cash, AccountKind.Investment, AccountKind.Property),
                Sum(AccountKind.ShortTermDebt, AccountKind.LongTermDebt), Sum(AccountKind.Cash), entered.Count, profile.Accounts.Count));
        }
        return result.OrderBy(row => row.Month).ToList();
    }

    public static decimal? Change(MonthlySnapshot current, MonthlySnapshot? previous) =>
        previous is not null && previous.Month.AddMonths(1) == current.Month ? current.NetWorth - previous.NetWorth : null;

    public static decimal? Percent(decimal change, decimal previous) => previous == 0 ? null : change / Math.Abs(previous) * 100;

    public static List<MonthlySnapshot> ForHousehold(IEnumerable<Profile> profiles)
    {
        var people = profiles.ToList();
        var accountCount = people.Sum(profile => profile.Accounts.Count);
        return people.SelectMany(For).GroupBy(snapshot => snapshot.Month)
            .OrderBy(group => group.Key)
            .Select(group => new MonthlySnapshot(group.Key, group.Sum(row => row.Assets),
                group.Sum(row => row.Liabilities), group.Sum(row => row.Cash),
                group.Sum(row => row.EnteredAccounts), accountCount)).ToList();
    }
}
