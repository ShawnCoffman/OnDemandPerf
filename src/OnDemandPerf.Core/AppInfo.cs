using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace OnDemandPerf.Core;

public static class AppInfo
{
    /// <summary>Single source of truth is Directory.Build.props; the build stamps it into the assembly.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";
        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}

/// <summary>
/// Short fingerprint of the numeric parameters that define a benchmark profile. Two results are only comparable when the
/// profile name AND this fingerprint match, so a changed duration cannot be silently pooled with older runs.
/// It cannot see logic changes (for example a different dataset generator); change the profile name for those.
/// </summary>
public static class ProfileHash
{
    public static string Compute(params object[] parameters)
    {
        string text = string.Join("|", parameters.Select(p => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
    }
}
