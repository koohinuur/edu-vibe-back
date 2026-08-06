using System.Security.Cryptography;

namespace LMS.Application.Common.Security;

/// <summary>
/// Generates cryptographically-random passwords that always satisfy
/// <see cref="PasswordPolicy"/> (length + at least three character classes). Used
/// when the bulk import provisions a brand-new student account and must hand back
/// a first password for the welcome sheet.
/// </summary>
public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // no I/O — avoids look-alikes
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";   // no l
    private const string Digits = "23456789";                   // no 0/1
    private const string Symbols = "!@#$%*?-_";

    /// <summary>Returns a 14-char password with at least one of each character class.</summary>
    public static string Generate(int length = 14)
    {
        if (length < 8) length = 8;

        // Guarantee one from each class so the policy's "3 of 4 classes" always holds,
        // then fill the rest from the combined alphabet.
        var all = Upper + Lower + Digits + Symbols;
        var chars = new char[length];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (var i = 4; i < length; i++) chars[i] = Pick(all);

        Shuffle(chars);
        return new string(chars);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    private static void Shuffle(char[] chars)
    {
        // Fisher–Yates with a cryptographic RNG.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
    }
}
