using System.Globalization;
using Microsoft.Win32;
using UseControl.Core.Browsers;
using UseControl.Core.Configuration;
using UseControl.Windows.Registry;

namespace UseControl.Windows.Restrictions;

/// <summary>Chrome, Edge and Firefox policies in the account's own HKCU\Software\Policies.</summary>
public static class BrowserPolicyWriter
{
    internal const string ChromeKey = @"Software\Policies\Google\Chrome";
    internal const string EdgeKey = @"Software\Policies\Microsoft\Edge";
    internal const string FirefoxKey = @"Software\Policies\Mozilla\Firefox";

    public static void Remove(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var k in new[] { ChromeKey, EdgeKey, FirefoxKey }) RegistryHelpers.DeleteTree(root, k);
    }

    public static void Apply(RegistryKey root, BrowserSettings browsers)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(browsers);
        WriteChromium(root, ChromeKey, browsers.Chrome);
        WriteChromium(root, EdgeKey, browsers.Edge);
        WriteFirefox(root, browsers.Firefox);
    }

    private static void WriteChromium(RegistryKey root, string path, ChromiumSettings s)
    {
        using var key = RegistryHelpers.Ensure(root, path);
        WritePolicies(key, PolicyValues.FromConfig(s.Policies));
        if (!s.BlockExtensions) return;
        using (var block = RegistryHelpers.Ensure(key, "ExtensionInstallBlocklist")) block.SetValue("1", "*", RegistryValueKind.String);
        if (s.ExtensionsAllowed.Count == 0) return;
        using var allow = RegistryHelpers.Ensure(key, "ExtensionInstallAllowlist");
        for (var i = 0; i < s.ExtensionsAllowed.Count; i++)
            allow.SetValue((i + 1).ToString(CultureInfo.InvariantCulture), s.ExtensionsAllowed[i], RegistryValueKind.String);
    }

    private static void WriteFirefox(RegistryKey root, FirefoxSettings s)
    {
        using var key = RegistryHelpers.Ensure(root, FirefoxKey);
        WritePolicies(key, PolicyValues.FromConfig(s.Policies));
        if (s.BlockExtensions) key.SetValue("ExtensionSettings", new[] { PolicyValues.FirefoxExtensionSettings(s) }, RegistryValueKind.MultiString);
        if (s.DisablePrivateBrowsing) key.SetValue("DisablePrivateBrowsing", 1, RegistryValueKind.DWord);
        if (s.DisableDnsOverHttps)
        {
            using var doh = RegistryHelpers.Ensure(key, "DNSOverHTTPS");
            doh.SetValue("Enabled", 0, RegistryValueKind.DWord);
            doh.SetValue("Locked", 1, RegistryValueKind.DWord);
        }
    }

    private static void WritePolicies(RegistryKey key, IEnumerable<PolicyValue> values)
    {
        foreach (var v in values)
        {
            if (v.Kind == PolicyValueKind.DWord) key.SetValue(v.Name, (int)v.Data, RegistryValueKind.DWord);
            else key.SetValue(v.Name, (string)v.Data, RegistryValueKind.String);
        }
    }
}
