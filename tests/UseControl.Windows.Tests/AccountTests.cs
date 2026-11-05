using System.Security.Principal;
using UseControl.Core;
using UseControl.Windows.Accounts;

namespace UseControl.Windows.Tests;

public class AccountTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public void Lists_local_accounts_including_the_current_one()
    {
        var all = LocalAccounts.GetAll();
        Assert.Contains(all, a => a.Sid == Me);
        Assert.Contains(all, a => a.IsBuiltInAdministrator);
    }

    [Fact]
    public void Administrators_cannot_be_restricted()
    {
        var me = LocalAccounts.GetAll().Single(a => a.Sid == Me);
        if (!me.IsAdministrator) return;   // the check only makes sense when the tests run as an administrator
        Assert.Equal(ErrorCodes.AccountIsAdministrator, Assert.Throws<UcException>(() => LocalAccounts.GetRestrictable(me.Name)).Code);
        Assert.Contains(me.Name, LocalAccounts.GetAdministratorNames(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_accounts_are_reported()
    {
        Assert.Null(LocalAccounts.Find("no-such-user-42"));
        Assert.Equal(ErrorCodes.AccountNotFound, Assert.Throws<UcException>(() => LocalAccounts.GetRestrictable("no-such-user-42")).Code);
    }
}
