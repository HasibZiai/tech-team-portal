using System.Security.Cryptography;

namespace PcServ.Core.Security;

/// <summary>
/// Generates easy-to-read temporary passwords (no 0/O, 1/l/I) that an admin can read out to a user.
/// Always contains at least one uppercase letter, lowercase letter and digit.
/// </summary>
public static class TempPasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    private const string All = Upper + Lower + Digits;

    public static string Generate(int length = 12)
    {
        if (length < 8) throw new ArgumentOutOfRangeException(nameof(length), "Temporary passwords must be at least 8 characters.");

        var chars = new char[length];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        for (var i = 3; i < length; i++) chars[i] = Pick(All);

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}
